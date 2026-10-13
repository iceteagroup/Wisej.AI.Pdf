///////////////////////////////////////////////////////////////////////////////
//
// (C) 2026 ICE TEA GROUP LLC - ALL RIGHTS RESERVED
//
// 
//
// ALL INFORMATION CONTAINED HEREIN IS, AND REMAINS
// THE PROPERTY OF ICE TEA GROUP LLC AND ITS SUPPLIERS, IF ANY.
// THE INTELLECTUAL PROPERTY AND TECHNICAL CONCEPTS CONTAINED
// HEREIN ARE PROPRIETARY TO ICE TEA GROUP LLC AND ITS SUPPLIERS
// AND MAY BE COVERED BY U.S. AND FOREIGN PATENTS, PATENT IN PROCESS, AND
// ARE PROTECTED BY TRADE SECRET OR COPYRIGHT LAW.
//
// DISSEMINATION OF THIS INFORMATION OR REPRODUCTION OF THIS MATERIAL
// IS STRICTLY FORBIDDEN UNLESS PRIOR WRITTEN PERMISSION IS OBTAINED
// FROM ICE TEA GROUP LLC.
//
///////////////////////////////////////////////////////////////////////////////

// Interprets page content streams (ISO 32000-2, chapters 8 and 9) and produces
// the positioned glyphs, paths and images painted on the page.

using System;
using System.Collections.Generic;
using System.Threading;
using Wisej.AI.Pdf.Fonts;
using Wisej.AI.Pdf.Graphics;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Content
{
	/// <summary>
	/// Options that control what <see cref="ContentInterpreter"/> collects.
	/// </summary>
	internal sealed class InterpreterOptions
	{
		/// <summary>
		/// Collect the glyphs. Default: true.
		/// </summary>
		public bool Glyphs = true;

		/// <summary>
		/// Collect the paths. Default: true.
		/// </summary>
		public bool Paths = true;

		/// <summary>
		/// Collect the image placements. Default: true.
		/// </summary>
		public bool Images = true;

		/// <summary>
		/// Process the appearance streams of annotations (form fields, stamps, comments). Default: true.
		/// </summary>
		public bool Annotations = true;

		/// <summary>
		/// The maximum number of operators executed per page, to bound the time spent on pathological content.
		/// </summary>
		public int MaxOperators = 10_000_000;

		/// <summary>
		/// Token used to cancel the interpretation.
		/// </summary>
		public CancellationToken CancellationToken;
	}

	/// <summary>
	/// Executes the content of a page and collects the painted objects in display space.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The initial transformation matrix is the page display matrix, so all the coordinates
	/// produced by the interpreter are already in display space: top-left origin, rotated
	/// as displayed, in points.
	/// </para>
	/// <para>
	/// The interpreter is resilient: malformed operators are skipped, and an exception in a
	/// nested form or annotation doesn't stop the processing of the rest of the page.
	/// </para>
	/// </remarks>
	internal sealed class ContentInterpreter
	{
		#region Nested types

		/// <summary>
		/// An entry of the marked-content stack.
		/// </summary>
		private struct MarkedContent
		{
			/// <summary>The marked-content identifier, or -1.</summary>
			public int Mcid;

			/// <summary>True for /Artifact sequences.</summary>
			public bool IsArtifact;

			/// <summary>True when an optional content group hides the sequence.</summary>
			public bool IsHidden;

			/// <summary>The /ActualText replacement, or null.</summary>
			public string ActualText;

			/// <summary>The identifier of the /ActualText sequence.</summary>
			public int ActualTextId;
		}

		#endregion

		// maximum nesting of form XObjects.
		private const int MaxFormDepth = 24;

		// the document that owns the page.
		private readonly PdfDocument _document;
		// the page being interpreted.
		private readonly PdfPage _page;
		// what to collect, limits and cancellation.
		private readonly InterpreterOptions _options;
		// fonts and colour spaces shared by all the pages.
		private readonly ResourceCache _cache;
		// the objects collected so far.
		private readonly PageObjects _result;
		// the visibility of layers, or null when the document has no hidden layers.
		private readonly OptionalContent _optionalContent;

		// the current graphics state and the stack saved by q/Q.
		private GraphicsState _state;
		// the graphics states saved by q and restored by Q.
		private readonly Stack<GraphicsState> _stateStack = new Stack<GraphicsState>();

		// text object state (not part of the graphics state).
		private Matrix _textMatrix = Matrix.Identity;
		// the text line matrix: the start of the current line.
		private Matrix _textLineMatrix = Matrix.Identity;

		// current path in display space.
		private readonly PathBuilder _path = new PathBuilder();
		// set by W/W*: the path is used as clip when it's painted.
		private int _pendingClip;

		// marked content.
		private readonly List<MarkedContent> _markedContent = new List<MarkedContent>();
		// generates the identifiers of /ActualText sequences.
		private int _actualTextCounter;

		// forms being executed, to detect recursion.
		private readonly HashSet<PdfStream> _activeForms = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);

		// painting order of the collected objects.
		private int _objectIndex;
		// operators executed so far, checked against InterpreterOptions.MaxOperators.
		private int _operatorCount;
		// the /StructParents key of the content stream being executed, for the structure tree.
		private int _structParent = -1;

		// the matrix of the default coordinate space of the content stream being executed (page or form): patterns are relative to it.
		private Matrix _baseMatrix;
		// true while executing an annotation appearance.
		private bool _inAnnotation;

		/// <summary>
		/// Initializes a new instance of <see cref="ContentInterpreter"/>.
		/// </summary>
		/// <param name="page">The page to interpret.</param>
		/// <param name="options">The options; null for the defaults.</param>
		public ContentInterpreter(PdfPage page, InterpreterOptions options = null)
		{
			_page = page;
			_document = page.Document;
			_options = options ?? new InterpreterOptions();
			_cache = _document.ResourceCache;
			_optionalContent = OptionalContent.Get(_document);
			_result = new PageObjects { Page = page };
		}

		#region Methods

		/// <summary>
		/// Interprets the page content and the annotation appearances.
		/// </summary>
		/// <returns>The objects painted on the page.</returns>
		public PageObjects Run()
		{
			_state = new GraphicsState
			{
				Ctm = _page.DisplayMatrix,
				Clip = new PdfRect(0, 0, _page.Width, _page.Height),
			};

			_structParent = _page.Dictionary.GetInt(PdfNames.StructParents, -1);
			_baseMatrix = _page.DisplayMatrix;

			try
			{
				Execute(_page.GetContentBytes(), _page.Resources, 0);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception)
			{
				// keep what was collected before the error.
			}

			if (_options.Annotations || _options.Glyphs)
				ProcessAnnotations();

			return _result;
		}

		#endregion

		#region Execution

		/// <summary>
		/// Executes a content stream.
		/// </summary>
		/// <param name="content">The decoded content.</param>
		/// <param name="resources">The resources of the content stream.</param>
		/// <param name="depth">The nesting depth of forms and patterns.</param>
		private void Execute(byte[] content, PdfDictionary resources, int depth)
		{
			if (content == null || content.Length == 0)
				return;

			resources = resources ?? new PdfDictionary();
			var reader = new ContentReader(content, _document.File);
			var baseStackDepth = _stateStack.Count;
			var baseMarkedDepth = _markedContent.Count;

			try
			{
				while (reader.Read(out var op))
				{
					if (++_operatorCount > _options.MaxOperators)
						break;

					if ((_operatorCount & 0xFFF) == 0)
						_options.CancellationToken.ThrowIfCancellationRequested();

					try
					{
						Dispatch(op, reader, resources, depth);
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception)
					{
						// a malformed operator: skip it.
					}
				}
			}
			finally
			{
				// unbalanced q/Q and BMC/EMC inside a stream must not leak into the caller.
				while (_stateStack.Count > baseStackDepth)
					_state = _stateStack.Pop();

				if (_markedContent.Count > baseMarkedDepth)
					_markedContent.RemoveRange(baseMarkedDepth, _markedContent.Count - baseMarkedDepth);
			}
		}

		/// <summary>
		/// Executes one operator.
		/// </summary>
		/// <param name="op">The operator keyword.</param>
		/// <param name="r">The reader, which holds the operands.</param>
		/// <param name="resources">The resources of the content stream.</param>
		/// <param name="depth">The nesting depth of forms and patterns.</param>
		private void Dispatch(string op, ContentReader r, PdfDictionary resources, int depth)
		{
			switch (op)
			{
				#region General graphics state

				case "q":
					_stateStack.Push(_state);
					_state = _state.Clone();
					break;

				case "Q":
					if (_stateStack.Count > 0)
						_state = _stateStack.Pop();
					break;

				case "cm":
					if (r.HasNumbers(6))
					{
						var m = new Matrix(r.GetNumber(0), r.GetNumber(1), r.GetNumber(2), r.GetNumber(3), r.GetNumber(4), r.GetNumber(5));
						_state.Ctm = m.Multiply(_state.Ctm);
					}
					break;

				case "w":
					_state.LineWidth = r.GetNumber(0);
					break;

				case "gs":
					ApplyExtGState(resources.GetDictionary(PdfNames.ExtGState)?.GetDictionary(r.GetName(0)), resources);
					break;

				case "J":
				case "j":
				case "M":
				case "d":
				case "ri":
				case "i":
					// line cap, join, miter, dash, intent and flatness don't affect extraction.
					break;

				#endregion

				#region Path construction

				case "m":
					_path.MoveTo(_state.Ctm.Transform(r.GetNumber(0), r.GetNumber(1)));
					break;

				case "l":
					_path.LineTo(_state.Ctm.Transform(r.GetNumber(0), r.GetNumber(1)));
					break;

				case "c":
					_path.CurveTo(
						_state.Ctm.Transform(r.GetNumber(0), r.GetNumber(1)),
						_state.Ctm.Transform(r.GetNumber(2), r.GetNumber(3)),
						_state.Ctm.Transform(r.GetNumber(4), r.GetNumber(5)));
					break;

				case "v":
					_path.CurveTo(
						_path.CurrentPoint,
						_state.Ctm.Transform(r.GetNumber(0), r.GetNumber(1)),
						_state.Ctm.Transform(r.GetNumber(2), r.GetNumber(3)));
					break;

				case "y":
					{
						var end = _state.Ctm.Transform(r.GetNumber(2), r.GetNumber(3));
						_path.CurveTo(_state.Ctm.Transform(r.GetNumber(0), r.GetNumber(1)), end, end);
					}
					break;

				case "h":
					_path.ClosePath();
					break;

				case "re":
					{
						double x = r.GetNumber(0), y = r.GetNumber(1), w = r.GetNumber(2), h = r.GetNumber(3);
						var ctm = _state.Ctm;
						_path.Rectangle(ctm.Transform(x, y), ctm.Transform(x + w, y), ctm.Transform(x + w, y + h), ctm.Transform(x, y + h));
					}
					break;

				#endregion

				#region Path painting and clipping

				case "S":
					PaintPath(stroke: true, fill: false);
					break;

				case "s":
					_path.ClosePath();
					PaintPath(stroke: true, fill: false);
					break;

				case "f":
				case "F":
				case "f*":
					PaintPath(stroke: false, fill: true);
					break;

				case "B":
				case "B*":
					PaintPath(stroke: true, fill: true);
					break;

				case "b":
				case "b*":
					_path.ClosePath();
					PaintPath(stroke: true, fill: true);
					break;

				case "n":
					PaintPath(stroke: false, fill: false);
					break;

				case "W":
				case "W*":
					_pendingClip = 1;
					break;

				#endregion

				#region Colour

				case "CS":
					_state.StrokeSpace = _cache.GetColorSpace(r.GetValue(0), resources);
					_state.StrokeIsPattern = _state.StrokeSpace.Kind == ColorSpaceKind.Pattern;
					_state.StrokeColor = _state.StrokeSpace.ToRgb(_state.StrokeSpace.GetInitialColor());
					break;

				case "cs":
					_state.FillSpace = _cache.GetColorSpace(r.GetValue(0), resources);
					_state.FillIsPattern = _state.FillSpace.Kind == ColorSpaceKind.Pattern;
					_state.FillColor = _state.FillSpace.ToRgb(_state.FillSpace.GetInitialColor());
					break;

				case "SC":
				case "SCN":
					_state.StrokeColor = ReadColor(r, _state.StrokeSpace, out _state.StrokeIsPattern);
					break;

				case "sc":
				case "scn":
					_state.FillColor = ReadColor(r, _state.FillSpace, out _state.FillIsPattern);
					_state.FillPattern = _state.FillIsPattern ? resources.GetDictionary(PdfNames.Pattern)?.GetDictionary(r.GetName(r.OperandCount - 1)) : null;
					break;

				case "G":
					_state.StrokeSpace = ColorSpace.DeviceGray;
					_state.StrokeColor = ColorSpace.DeviceGray.ToRgb(new[] { r.GetNumber(0) });
					_state.StrokeIsPattern = false;
					break;

				case "g":
					_state.FillSpace = ColorSpace.DeviceGray;
					_state.FillColor = ColorSpace.DeviceGray.ToRgb(new[] { r.GetNumber(0) });
					_state.FillIsPattern = false;
					break;

				case "RG":
					_state.StrokeSpace = ColorSpace.DeviceRgb;
					_state.StrokeColor = ColorSpace.DeviceRgb.ToRgb(new[] { r.GetNumber(0), r.GetNumber(1), r.GetNumber(2) });
					_state.StrokeIsPattern = false;
					break;

				case "rg":
					_state.FillSpace = ColorSpace.DeviceRgb;
					_state.FillColor = ColorSpace.DeviceRgb.ToRgb(new[] { r.GetNumber(0), r.GetNumber(1), r.GetNumber(2) });
					_state.FillIsPattern = false;
					break;

				case "K":
					_state.StrokeSpace = ColorSpace.DeviceCmyk;
					_state.StrokeColor = ColorSpace.DeviceCmyk.ToRgb(new[] { r.GetNumber(0), r.GetNumber(1), r.GetNumber(2), r.GetNumber(3) });
					_state.StrokeIsPattern = false;
					break;

				case "k":
					_state.FillSpace = ColorSpace.DeviceCmyk;
					_state.FillColor = ColorSpace.DeviceCmyk.ToRgb(new[] { r.GetNumber(0), r.GetNumber(1), r.GetNumber(2), r.GetNumber(3) });
					_state.FillIsPattern = false;
					break;

				#endregion

				#region Text objects and state

				case "BT":
					_textMatrix = Matrix.Identity;
					_textLineMatrix = Matrix.Identity;
					break;

				case "ET":
					break;

				case "Tc":
					_state.CharSpacing = r.GetNumber(0);
					break;

				case "Tw":
					_state.WordSpacing = r.GetNumber(0);
					break;

				case "Tz":
					_state.HorizontalScaling = r.GetNumber(0) / 100;
					break;

				case "TL":
					_state.Leading = r.GetNumber(0);
					break;

				case "Tf":
					_state.Font = _cache.GetFont(resources.GetDictionary(PdfNames.Font)?.GetDictionary(r.GetName(0)));
					_state.FontSize = r.GetNumber(1);
					break;

				case "Tr":
					_state.RenderMode = (int)r.GetNumber(0);
					break;

				case "Ts":
					_state.Rise = r.GetNumber(0);
					break;

				case "Td":
					MoveTextLine(r.GetNumber(0), r.GetNumber(1));
					break;

				case "TD":
					_state.Leading = -r.GetNumber(1);
					MoveTextLine(r.GetNumber(0), r.GetNumber(1));
					break;

				case "Tm":
					if (r.HasNumbers(6))
					{
						_textMatrix = new Matrix(r.GetNumber(0), r.GetNumber(1), r.GetNumber(2), r.GetNumber(3), r.GetNumber(4), r.GetNumber(5));
						_textLineMatrix = _textMatrix;
					}
					break;

				case "T*":
					MoveTextLine(0, -_state.Leading);
					break;

				#endregion

				#region Text showing

				case "Tj":
					if (r.GetValue(0) is PdfString tj)
						ShowText(tj.Bytes);
					break;

				case "'":
					MoveTextLine(0, -_state.Leading);
					if (r.GetValue(0) is PdfString quote)
						ShowText(quote.Bytes);
					break;

				case "\"":
					_state.WordSpacing = r.GetNumber(0);
					_state.CharSpacing = r.GetNumber(1);
					MoveTextLine(0, -_state.Leading);
					if (r.GetValue(2) is PdfString doubleQuote)
						ShowText(doubleQuote.Bytes);
					break;

				case "TJ":
					if (r.GetValue(0) is PdfArray array)
						ShowTextArray(array);
					break;

				#endregion

				#region XObjects, images and shadings

				case "Do":
					DoXObject(r.GetName(0), resources, depth);
					break;

				case "BI":
					if (r.InlineImage != null && _options.Images && !IsHidden)
						AddImage(null, r.InlineImage, null, resources);
					break;

				case "sh":
					AddShading(resources.GetDictionary(PdfNames.Shading)?.GetDictionary(r.GetName(0)));
					break;

				#endregion

				#region Marked content

				case "BMC":
					BeginMarkedContent(r.GetName(0), null, resources);
					break;

				case "BDC":
					BeginMarkedContent(r.GetName(0), r.GetValue(1), resources);
					break;

				case "EMC":
					if (_markedContent.Count > 0)
						_markedContent.RemoveAt(_markedContent.Count - 1);
					break;

				#endregion

				default:
					// MP, DP, BX, EX, d0, d1 and unknown operators are ignored.
					break;
			}
		}

		#endregion

		#region Graphics state

		/// <summary>
		/// Applies the parameters of an ExtGState dictionary (gs operator) that matter for extraction.
		/// </summary>
		/// <param name="gs">The graphics state parameter dictionary.</param>
		/// <param name="resources">The resources of the content stream.</param>
		private void ApplyExtGState(PdfDictionary gs, PdfDictionary resources)
		{
			if (gs == null)
				return;

			foreach (var entry in gs)
			{
				switch (entry.Key.Value)
				{
					case "LW":
						_state.LineWidth = gs.GetNumber(entry.Key, _state.LineWidth);
						break;

					case "CA":
						_state.StrokeAlpha = gs.GetNumber(entry.Key, 1);
						break;

					case "ca":
						_state.FillAlpha = gs.GetNumber(entry.Key, 1);
						break;

					case "SMask":
						_state.SoftMask = gs.Resolve(entry.Key) as PdfDictionary;
						break;

					case "Font":
						{
							var font = gs.GetArray(entry.Key);
							if (font != null && font.Count >= 2)
							{
								_state.Font = _cache.GetFont(font.GetDictionary(0));
								_state.FontSize = font.GetNumber(1);
							}
						}
						break;
				}
			}
		}

		/// <summary>
		/// Reads the operands of SC/SCN/sc/scn and converts them to RGB.
		/// </summary>
		/// <param name="r">The reader, which holds the operands.</param>
		/// <param name="space">The current colour space.</param>
		/// <param name="isPattern">Receives true when the colour selects a pattern.</param>
		/// <returns>The colour as 0xRRGGBB.</returns>
		private int ReadColor(ContentReader r, ColorSpace space, out bool isPattern)
		{
			isPattern = false;

			// a trailing name selects a pattern.
			var count = r.OperandCount;
			if (count > 0 && r.GetName(count - 1) != null)
			{
				isPattern = true;
				count--;
				if (count == 0)
					return 0x808080;
			}

			var components = new double[Math.Max(count, space.ComponentCount)];
			for (int i = 0; i < count; i++)
				components[i] = r.GetNumber(i);

			// a number of operands that doesn't match the space: the producer used a device space.
			if (count == 1 && space.ComponentCount != 1 && space.Kind != ColorSpaceKind.Pattern)
				return ColorSpace.DeviceGray.ToRgb(components);
			if (count == 3 && space.ComponentCount == 1)
				return ColorSpace.DeviceRgb.ToRgb(components);

			return space.ToRgb(components);
		}

		#endregion

		#region Paths

		/// <summary>
		/// Paints the current path (S, f, B, n...), applies a pending clip and resets the path.
		/// </summary>
		/// <param name="stroke">True when the path is stroked.</param>
		/// <param name="fill">True when the path is filled.</param>
		private void PaintPath(bool stroke, bool fill)
		{
			_path.EndPath();

			if (_pendingClip != 0)
			{
				// the clipping path takes effect after painting; only its bounds are tracked.
				if (!_path.IsEmpty)
					_state.Clip = _state.Clip.Intersect(_path.Bounds);

				_pendingClip = 0;
			}

			if ((stroke || fill) && _options.Paths && !_path.IsEmpty && !IsHidden)
			{
				var lineWidth = Math.Max(_state.LineWidth, 0) * Math.Sqrt(Math.Abs(_state.Ctm.Determinant));
				var bounds = _path.Bounds;
				if (stroke)
					bounds = bounds.Inflate(lineWidth / 2);

				var shape = new PathShape
				{
					IsStroked = stroke && _state.StrokeAlpha > 0,
					IsFilled = fill && _state.FillAlpha > 0,
					LineWidth = lineWidth,
					StrokeColor = _state.StrokeColor,
					FillColor = _state.FillColor,
					IsPatternFill = fill && _state.FillIsPattern,
					Segments = _path.Segments,
					Rectangles = _path.Rectangles,
					HasCurves = _path.HasCurves,
					PointCount = _path.PointCount,
				};

				SetCommon(shape, bounds);
				if (!_state.Clip.IntersectsWith(bounds) && !(bounds.Width == 0 || bounds.Height == 0))
					shape.IsClipped = true;

				if (shape.IsStroked || shape.IsFilled)
					_result.Paths.Add(shape);
			}

			// pattern fills paint their own content: pictures drawn as patterns are images too.
			if (fill && _state.FillPattern != null && !_path.IsEmpty && !IsHidden)
				PaintPattern(_state.FillPattern, _path.Bounds);

			_path.Reset();
		}

		// executes the cell of a tiling pattern once over the filled area; shading patterns become shaded areas.
		private void PaintPattern(PdfDictionary pattern, PdfRect area)
		{
			// the pattern matrix is relative to the default space of the content stream that uses it.
			var matrix = Matrix.FromArray(pattern.GetArray(PdfNames.Matrix)?.ToNumbers()).Multiply(_baseMatrix);
			var clip = _state.Clip.Intersect(area);
			if (clip.IsEmpty)
				return;

			switch (pattern.GetInt("PatternType"))
			{
				case 2:
					if (_options.Paths)
					{
						var shading = new ShadingArea();
						SetCommon(shading, clip);
						_result.Shadings.Add(shading);
					}
					break;

				case 1:
					if (!(pattern is PdfStream cell) || !_activeForms.Add(cell))
						return;

					_stateStack.Push(_state);
					_state = _state.Clone();
					var savedBase = _baseMatrix;
					var savedText = _textMatrix;
					var savedLine = _textLineMatrix;

					try
					{
						_state.Ctm = matrix;
						_state.Clip = clip;
						_state.FillPattern = null;
						_state.FillIsPattern = false;
						_baseMatrix = matrix;

						// the cell is clipped to its bounding box.
						var bbox = pattern.GetArray(PdfNames.BBox)?.ToNumbers();
						if (bbox != null && bbox.Length == 4)
						{
							matrix.TransformBounds(bbox[0], bbox[1], bbox[2], bbox[3], out var left, out var bottom, out var right, out var top);
							_state.Clip = _state.Clip.Intersect(new PdfRect(left, bottom, right, top));
						}

						byte[] content;
						try
						{
							content = cell.GetBytes();
						}
						catch (Exception)
						{
							content = null;
						}

						Execute(content, cell.GetDictionary(PdfNames.Resources) ?? new PdfDictionary(), 1);
					}
					finally
					{
						_state = _stateStack.Pop();
						_baseMatrix = savedBase;
						_textMatrix = savedText;
						_textLineMatrix = savedLine;
						_activeForms.Remove(cell);
					}
					break;
			}
		}

		#endregion

		#region Text

		/// <summary>
		/// Moves to the start of the next line (Td, TD, T*, ').
		/// </summary>
		/// <param name="tx">The horizontal offset in unscaled text space.</param>
		/// <param name="ty">The vertical offset in unscaled text space.</param>
		private void MoveTextLine(double tx, double ty)
		{
			_textLineMatrix = Matrix.Translation(tx, ty).Multiply(_textLineMatrix);
			_textMatrix = _textLineMatrix;
		}

		/// <summary>
		/// Shows the strings of a TJ array, applying the positioning adjustments between them.
		/// </summary>
		/// <param name="array">The TJ operand.</param>
		private void ShowTextArray(PdfArray array)
		{
			var font = _state.Font;
			for (int i = 0; i < array.Count; i++)
			{
				var item = array.Resolve(i);
				if (item is PdfString s)
				{
					ShowText(s.Bytes);
				}
				else if (item is int || item is double)
				{
					// adjustments are in thousandths of text space units, subtracted from the advance.
					var adjustment = -PdfConvert.ToNumber(item) / 1000 * _state.FontSize;
					if (font != null && font.IsVertical)
						_textMatrix = Matrix.Translation(0, adjustment).Multiply(_textMatrix);
					else
						_textMatrix = Matrix.Translation(adjustment * _state.HorizontalScaling, 0).Multiply(_textMatrix);
				}
			}
		}

		/// <summary>
		/// Shows a string: decodes the character codes, creates the glyphs and advances the text matrix.
		/// </summary>
		/// <param name="bytes">The bytes of the string.</param>
		private void ShowText(byte[] bytes)
		{
			var font = _state.Font ?? _cache.GetFont(null);
			var fontSize = _state.FontSize;
			var hScale = _state.HorizontalScaling;
			var fontMatrix = font.FontMatrix;
			var vertical = font.IsVertical;

			// text space -> display space, excluding the font size and rise which are applied per glyph.
			var textToDisplay = _textMatrix.Multiply(_state.Ctm);

			// style information shared by all the glyphs of the string.
			var collect = _options.Glyphs && !IsHidden;
			var renderMode = _state.RenderMode;
			var color = renderMode == 1 || renderMode == 5 ? _state.StrokeColor : _state.FillColor;
			var invisible = _state.FillAlpha <= 0 && renderMode != 1;
			var fakeBold = (renderMode == 2 || renderMode == 6) && _state.LineWidth > 0;
			var fakeItalic = IsSkewed(textToDisplay);
			var marked = GetCurrentMarkedContent();

			for (int offset = 0; offset < bytes.Length;)
			{
				var code = font.ReadCode(bytes, offset, out var length);
				offset += Math.Max(1, length);

				var width = font.GetWidth(code) * fontMatrix.A;
				var spacing = _state.CharSpacing + (font.IsWordSpace(code, length) ? _state.WordSpacing : 0);

				if (collect)
				{
					// the rendering matrix maps glyph text space (scaled by the font size) to display space.
					var trm = new Matrix(fontSize * hScale, 0, 0, fontSize, 0, _state.Rise).Multiply(textToDisplay);
					if (vertical)
					{
						font.GetVerticalMetrics(code, out _, out var vx, out var vy);
						trm = Matrix.Translation(-vx * fontMatrix.A, -vy * fontMatrix.A).Multiply(trm);
					}

					var glyph = CreateGlyph(font, code, trm, width, vertical);
					glyph.Color = color;
					glyph.RenderMode = invisible ? 3 : renderMode;
					glyph.IsBold = font.IsBold || fakeBold;
					glyph.IsItalic = font.IsItalic || fakeItalic;
					glyph.Rise = _state.Rise * Math.Sqrt(textToDisplay.C * textToDisplay.C + textToDisplay.D * textToDisplay.D);
					glyph.ActualText = marked.ActualText;
					glyph.ActualTextId = marked.ActualTextId;
					glyph.SpaceWidth = font.GetSpaceWidth() * fontMatrix.A * fontSize * hScale * Math.Sqrt(textToDisplay.A * textToDisplay.A + textToDisplay.B * textToDisplay.B);

					SetCommon(glyph, glyph.Bounds);

					// a glyph whose center is outside the clip is not visible.
					var cx = glyph.Bounds.CenterX;
					var cy = glyph.Bounds.CenterY;
					if (!_state.Clip.Inflate(1).Contains(cx, cy))
						glyph.IsClipped = true;

					_result.Glyphs.Add(glyph);
				}

				// advance the text matrix.
				if (vertical)
				{
					font.GetVerticalMetrics(code, out var w1, out _, out _);
					var ty = w1 * fontMatrix.A * fontSize + spacing;
					_textMatrix = Matrix.Translation(0, ty).Multiply(_textMatrix);
				}
				else
				{
					var tx = (width * fontSize + spacing) * hScale;
					_textMatrix = Matrix.Translation(tx, 0).Multiply(_textMatrix);
				}

				textToDisplay = _textMatrix.Multiply(_state.Ctm);
			}
		}

		/// <summary>
		/// Creates a glyph positioned with the text rendering matrix.
		/// </summary>
		/// <param name="font">The font.</param>
		/// <param name="code">The character code.</param>
		/// <param name="trm">The text rendering matrix (glyph space scaled by the font size to display space).</param>
		/// <param name="width">The advance of the glyph in text space units.</param>
		/// <param name="vertical">True for vertical writing.</param>
		/// <returns>The glyph.</returns>
		private TextGlyph CreateGlyph(PdfFont font, int code, Matrix trm, double width, bool vertical)
		{
			var text = font.GetUnicode(code);
			var glyph = new TextGlyph
			{
				Font = font,
				Code = code,
				Text = text ?? string.Empty,
				IsUnmapped = text == null,
			};

			// baseline origin and the end of the advance.
			var origin = trm.Transform(0, 0);
			var end = vertical ? trm.Transform(0, -1) : trm.Transform(width, 0);
			glyph.X = origin.X;
			glyph.Y = origin.Y;
			glyph.EndX = end.X;
			glyph.EndY = end.Y;

			// the em height in display space is the font size as perceived on the page.
			glyph.FontSize = Math.Sqrt(trm.C * trm.C + trm.D * trm.D);
			glyph.Angle = NormalizeAngle(Math.Atan2(trm.B, trm.A) * 180 / Math.PI);

			// glyph box: the advance horizontally, ascent to descent vertically.
			double left, bottom, right, top;
			if (vertical)
				trm.TransformBounds(0, -1, 1, 0, out left, out bottom, out right, out top);
			else
				trm.TransformBounds(0, font.Descent, Math.Max(width, 0.01), font.Ascent, out left, out bottom, out right, out top);

			glyph.Bounds = new PdfRect(left, bottom, right, top);
			return glyph;
		}

		// true when the x and y axes of the matrix are not perpendicular: text drawn with a synthetic slant.
		private static bool IsSkewed(Matrix m)
		{
			var dot = m.A * m.C + m.B * m.D;
			var lengths = Math.Sqrt((m.A * m.A + m.B * m.B) * (m.C * m.C + m.D * m.D));
			return lengths > 0 && Math.Abs(dot / lengths) > 0.1;
		}

		/// <summary>
		/// Normalizes an angle to [0, 360) degrees, snapping values close to 0.
		/// </summary>
		/// <param name="angle">The angle in degrees.</param>
		/// <returns>The normalized angle.</returns>
		private static double NormalizeAngle(double angle)
		{
			// display space is y-down: atan2 already gives clockwise angles; normalize to [0, 360).
			angle %= 360;
			if (angle < 0)
				angle += 360;

			// snap tiny deviations to the axis.
			if (angle < 0.5 || angle > 359.5)
				return 0;

			return angle;
		}

		#endregion

		#region XObjects and images

		/// <summary>
		/// Paints an XObject (Do operator): images are collected, forms are executed.
		/// </summary>
		/// <param name="name">The name of the XObject in the resources.</param>
		/// <param name="resources">The resources of the content stream.</param>
		/// <param name="depth">The nesting depth of forms.</param>
		private void DoXObject(PdfName name, PdfDictionary resources, int depth)
		{
			if (name == null)
				return;

			var xobjects = resources.GetDictionary(PdfNames.XObject);
			if (!(xobjects?.Resolve(name) is PdfStream stream))
				return;

			// optional content attached to the XObject itself.
			if (_optionalContent != null && !_optionalContent.IsVisible(stream.Resolve(PdfNames.OC)))
				return;

			switch (stream.GetNameValue(PdfNames.Subtype))
			{
				case "Image":
					if (_options.Images && !IsHidden)
						AddImage(stream, null, name.Value, resources);
					break;

				case "Form":
					DoForm(stream, resources, depth, null);
					break;

				default:
					// PostScript XObjects are ignored.
					break;
			}
		}

		/// <summary>
		/// Executes a form XObject or an annotation appearance.
		/// </summary>
		/// <param name="form">The form stream.</param>
		/// <param name="resources">The resources of the caller, inherited when the form has none.</param>
		/// <param name="depth">The nesting depth of forms.</param>
		/// <param name="extraMatrix">An additional matrix applied after the form matrix (annotation placement), or null.</param>
		private void DoForm(PdfStream form, PdfDictionary resources, int depth, Matrix? extraMatrix)
		{
			if (depth >= MaxFormDepth || !_activeForms.Add(form))
				return;

			_stateStack.Push(_state);
			_state = _state.Clone();
			var savedStructParent = _structParent;
			var savedBase = _baseMatrix;

			try
			{
				var matrix = Matrix.FromArray(form.GetArray(PdfNames.Matrix)?.ToNumbers());
				if (extraMatrix.HasValue)
					matrix = matrix.Multiply(extraMatrix.Value);

				_state.Ctm = matrix.Multiply(_state.Ctm);
				_baseMatrix = _state.Ctm;

				// the form bounding box clips its content.
				var bbox = form.GetArray(PdfNames.BBox)?.ToNumbers();
				if (bbox != null && bbox.Length == 4)
				{
					_state.Ctm.TransformBounds(bbox[0], bbox[1], bbox[2], bbox[3], out var left, out var bottom, out var right, out var top);
					_state.Clip = _state.Clip.Intersect(new PdfRect(left, bottom, right, top));
				}

				// forms with their own structure parent key are separate content streams for the structure tree.
				var structParent = form.GetInt(PdfNames.StructParents, -1);
				if (structParent >= 0)
					_structParent = structParent;

				// forms without resources inherit the resources of the caller.
				var formResources = form.GetDictionary(PdfNames.Resources) ?? resources;

				byte[] content;
				try
				{
					content = form.GetBytes();
				}
				catch (Exception)
				{
					content = null;
				}

				// the text matrices are saved because the form content may contain its own text objects.
				var savedText = _textMatrix;
				var savedLine = _textLineMatrix;
				Execute(content, formResources, depth + 1);
				_textMatrix = savedText;
				_textLineMatrix = savedLine;
			}
			finally
			{
				_state = _stateStack.Pop();
				_structParent = savedStructParent;
				_baseMatrix = savedBase;
				_activeForms.Remove(form);
			}
		}

		/// <summary>
		/// Records the placement of an image XObject or inline image.
		/// </summary>
		/// <param name="stream">The image XObject, or null.</param>
		/// <param name="inline">The inline image, or null.</param>
		/// <param name="name">The resource name of the XObject, or null.</param>
		/// <param name="resources">The resources in effect.</param>
		private void AddImage(PdfStream stream, InlineImage inline, string name, PdfDictionary resources)
		{
			var transform = _state.Ctm;
			transform.TransformBounds(0, 0, 1, 1, out var left, out var bottom, out var right, out var top);
			var bounds = new PdfRect(left, bottom, right, top);

			var image = new ImagePlacement
			{
				Stream = stream,
				Inline = inline,
				Name = name,
				Resources = resources,
				Transform = transform,
				FillColor = _state.FillColor,
				Alpha = _state.FillAlpha,
				SoftMask = _state.SoftMask,
			};

			SetCommon(image, bounds);
			image.VisibleBounds = bounds.Intersect(_state.Clip);
			image.IsClipped = image.VisibleBounds.IsEmpty;

			_result.Images.Add(image);
		}

		/// <summary>
		/// Records the area painted by a shading (sh operator).
		/// </summary>
		/// <param name="shading">The shading dictionary.</param>
		private void AddShading(PdfDictionary shading)
		{
			if (shading == null || !_options.Paths || IsHidden)
				return;

			// a shading fills the current clip, or its own bounding box when smaller.
			var bounds = _state.Clip;
			var bbox = shading.GetArray(PdfNames.BBox)?.ToNumbers();
			if (bbox != null && bbox.Length == 4)
			{
				_state.Ctm.TransformBounds(bbox[0], bbox[1], bbox[2], bbox[3], out var left, out var bottom, out var right, out var top);
				bounds = bounds.Intersect(new PdfRect(left, bottom, right, top));
			}

			if (bounds.IsEmpty)
				return;

			var area = new ShadingArea();
			SetCommon(area, bounds);
			_result.Shadings.Add(area);
		}

		#endregion

		#region Marked content

		/// <summary>
		/// Begins a marked-content sequence (BMC/BDC): MCID, /ActualText, artifacts and optional content.
		/// </summary>
		/// <param name="tag">The tag.</param>
		/// <param name="properties">The property list or its name in the resources, or null.</param>
		/// <param name="resources">The resources of the content stream.</param>
		private void BeginMarkedContent(PdfName tag, object properties, PdfDictionary resources)
		{
			var entry = new MarkedContent { Mcid = -1 };

			// properties can be inline or a name in the /Properties resources.
			var dictionary = properties as PdfDictionary;
			if (dictionary == null && properties is PdfName propertiesName)
				dictionary = resources.GetDictionary(PdfNames.Properties)?.GetDictionary(propertiesName);

			if (tag?.Value == "Artifact")
				entry.IsArtifact = true;

			if (dictionary != null)
			{
				entry.Mcid = dictionary.GetInt(PdfNames.MCID, -1);

				var actualText = dictionary.GetText(PdfNames.ActualText);
				if (actualText != null)
				{
					entry.ActualText = actualText;
					entry.ActualTextId = ++_actualTextCounter;
				}

				// "/OC /name BDC": the properties are an optional content group or membership dictionary.
				if (tag?.Value == "OC" && _optionalContent != null && !_optionalContent.IsVisible(dictionary))
					entry.IsHidden = true;
			}

			_markedContent.Add(entry);
		}

		// the innermost values of the marked-content stack.
		private MarkedContent GetCurrentMarkedContent()
		{
			var current = new MarkedContent { Mcid = -1 };
			for (int i = 0; i < _markedContent.Count; i++)
			{
				var entry = _markedContent[i];
				if (entry.Mcid >= 0)
					current.Mcid = entry.Mcid;
				if (entry.IsArtifact)
					current.IsArtifact = true;
				if (entry.IsHidden)
					current.IsHidden = true;

				// the outermost ActualText wins: it replaces everything inside it.
				if (entry.ActualText != null && current.ActualText == null)
				{
					current.ActualText = entry.ActualText;
					current.ActualTextId = entry.ActualTextId;
				}
			}

			return current;
		}

		// true when an optional content group hides the current content.
		private bool IsHidden
		{
			get
			{
				for (int i = 0; i < _markedContent.Count; i++)
				{
					if (_markedContent[i].IsHidden)
						return true;
				}

				return false;
			}
		}

		// sets the properties common to all page objects.
		private void SetCommon(PageObject obj, PdfRect bounds)
		{
			var marked = GetCurrentMarkedContent();
			obj.Index = _objectIndex++;
			obj.Bounds = bounds;
			obj.Mcid = marked.Mcid;
			obj.IsArtifact = marked.IsArtifact;
			obj.StructParent = _structParent;
			obj.IsAnnotation = _inAnnotation;
		}

		#endregion

		#region Annotations

		/// <summary>
		/// Collects the links and executes the visible appearances of the annotations of the page.
		/// </summary>
		private void ProcessAnnotations()
		{
			var annotations = _page.Dictionary.GetArray(PdfNames.Annots);
			if (annotations == null)
				return;

			var destinations = new PdfDestinations(_document);
			for (int i = 0; i < annotations.Count; i++)
			{
				var annotation = annotations.GetDictionary(i);
				if (annotation == null)
					continue;

				try
				{
					ProcessAnnotation(annotation, destinations);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception)
				{
					// a broken annotation doesn't affect the others.
				}
			}
		}

		/// <summary>
		/// Processes one annotation: links are recorded, other annotations execute their normal appearance.
		/// </summary>
		/// <param name="annotation">The annotation dictionary.</param>
		/// <param name="destinations">Resolves link targets.</param>
		private void ProcessAnnotation(PdfDictionary annotation, PdfDestinations destinations)
		{
			var subtype = annotation.GetNameValue(PdfNames.Subtype);
			var rect = annotation.GetArray(PdfNames.Rect)?.ToNumbers();
			if (rect == null || rect.Length < 4)
				return;

			_page.DisplayMatrix.TransformBounds(rect[0], rect[1], rect[2], rect[3], out var left, out var bottom, out var right, out var top);
			var bounds = new PdfRect(left, bottom, right, top);

			if (subtype == "Link")
			{
				var target = destinations.Resolve(annotation.Resolve(PdfNames.Dest), annotation.GetDictionary(PdfNames.A));
				if (target.Uri != null || target.PageNumber > 0)
					_result.Links.Add(new PageLink { Bounds = bounds, Uri = target.Uri, PageNumber = target.PageNumber });

				return;
			}

			if (!_options.Annotations || subtype == "Popup")
				return;

			// hidden (2) and no-view (32) annotations are not displayed.
			var flags = annotation.GetInt(PdfNames.F);
			if ((flags & 2) != 0 || (flags & 32) != 0)
				return;

			if (_optionalContent != null && !_optionalContent.IsVisible(annotation.Resolve(PdfNames.OC)))
				return;

			var appearance = GetNormalAppearance(annotation);
			if (appearance == null)
			{
				// form fields without an appearance (NeedAppearances) still have a value worth extracting.
				if (subtype == "Widget" && _options.Glyphs)
					AddFieldValue(annotation, bounds);

				return;
			}

			// Algorithm 8.1: map the transformed appearance box onto the annotation rectangle.
			var matrix = Matrix.FromArray(appearance.GetArray(PdfNames.Matrix)?.ToNumbers());
			var bbox = appearance.GetArray(PdfNames.BBox)?.ToNumbers();
			if (bbox == null || bbox.Length < 4)
				return;

			matrix.TransformBounds(bbox[0], bbox[1], bbox[2], bbox[3], out var bx0, out var by0, out var bx1, out var by1);
			var bw = bx1 - bx0;
			var bh = by1 - by0;
			if (bw <= 0 || bh <= 0)
				return;

			var x0 = Math.Min(rect[0], rect[2]);
			var y0 = Math.Min(rect[1], rect[3]);
			var x1 = Math.Max(rect[0], rect[2]);
			var y1 = Math.Max(rect[1], rect[3]);
			var fit = new Matrix((x1 - x0) / bw, 0, 0, (y1 - y0) / bh, x0 - bx0 * (x1 - x0) / bw, y0 - by0 * (y1 - y0) / bh);

			_state = new GraphicsState
			{
				Ctm = _page.DisplayMatrix,
				Clip = new PdfRect(0, 0, _page.Width, _page.Height),
			};

			_inAnnotation = true;
			_structParent = annotation.GetInt(PdfNames.StructParent, -1);
			try
			{
				DoForm(appearance, _page.Resources, 0, fit);
			}
			finally
			{
				_inAnnotation = false;
			}
		}

		/// <summary>
		/// Adds the value of a form field without appearance as text, laid out in the widget rectangle.
		/// </summary>
		/// <param name="widget">The widget annotation, which is also the field dictionary or one of its kids.</param>
		/// <param name="bounds">The widget rectangle in display space.</param>
		private void AddFieldValue(PdfDictionary widget, PdfRect bounds)
		{
			// /FT and /V are inheritable through the /Parent chain.
			string type = null;
			object value = null;
			var field = widget;
			for (int depth = 0; field != null && depth < 16; depth++)
			{
				type = type ?? field.GetNameValue("FT");
				value = value ?? field.Resolve("V");
				field = field.GetDictionary(PdfNames.Parent);
			}

			string text;
			switch (type)
			{
				case "Tx":
				case "Ch":
					text = value is PdfString s ? s.ToText() : (value as PdfArray)?.GetString(0)?.ToText();
					break;

				case "Btn":
					// check boxes and radio buttons: the selected state is any name other than Off.
					var state = widget.GetNameValue("AS") ?? (value as PdfName)?.Value;
					text = state != null && state != "Off" ? "\u2612" : null;
					break;

				default:
					text = null;
					break;
			}

			if (string.IsNullOrWhiteSpace(text) || bounds.IsEmpty)
				return;

			// the font size from the default appearance string ("/Helv 10 Tf"), or a size that fits the box.
			var size = 0.0;
			var da = widget.GetText("DA");
			if (da != null)
			{
				var parts = da.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				for (int i = 1; i < parts.Length; i++)
				{
					if (parts[i] == "Tf")
						double.TryParse(parts[i - 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out size);
				}
			}

			var multiline = (widget.GetInt("Ff") & (1 << 12)) != 0;
			if (size <= 0)
				size = Math.Min(12, Math.Max(6, bounds.Height * 0.7));

			// lay the characters out with an average advance; layout only needs plausible positions.
			var font = _cache.GetFont(null);
			var advance = size * 0.5;
			var x = bounds.Left + 2;
			var baseline = multiline ? bounds.Top + size : bounds.Bottom - Math.Max(1, (bounds.Height - size) / 2) - size * 0.2;
			foreach (var c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
			{
				if (c == '\n')
				{
					x = bounds.Left + 2;
					baseline += size * 1.2;
					continue;
				}

				var glyph = new TextGlyph
				{
					Text = c.ToString(),
					Font = font,
					X = x,
					Y = baseline,
					EndX = x + advance,
					EndY = baseline,
					FontSize = size,
					SpaceWidth = size * 0.25,
					Color = 0,
				};

				SetCommon(glyph, new PdfRect(x, baseline - size * 0.75, x + advance, baseline + size * 0.25));
				glyph.IsAnnotation = true;
				_result.Glyphs.Add(glyph);
				x += advance;
			}
		}

		// the normal appearance; for state-dependent appearances (check boxes), the one selected by /AS.
		private static PdfStream GetNormalAppearance(PdfDictionary annotation)
		{
			var ap = annotation.GetDictionary(PdfNames.AP);
			var normal = ap?.Resolve(PdfNames.N);
			if (normal is PdfStream stream)
				return stream;

			if (normal is PdfDictionary states)
			{
				var state = annotation.GetName("AS");
				if (state != null)
					return states.GetStream(state);
			}

			return null;
		}

		#endregion
	}
}
