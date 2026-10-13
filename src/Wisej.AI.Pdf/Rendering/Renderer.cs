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

// Full-fidelity content stream interpreter that paints on a Canvas: paths,
// clipping, text, images, shadings, patterns, forms and annotation
// appearances (ISO 32000-2, chapters 8 and 9, 12.5).

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.Content;
using Wisej.AI.Pdf.Fonts;
using Wisej.AI.Pdf.Fonts.Programs;
using Wisej.AI.Pdf.Graphics;
using Wisej.AI.Pdf.Imaging;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Rendering
{
	/// <summary>
	/// Executes content streams and paints them on a <see cref="Canvas"/>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The structure mirrors <see cref="ContentInterpreter"/>: the transformation matrix starts at
	/// the device matrix (page display matrix scaled to pixels), forms and annotation appearances are
	/// executed recursively, and optional content hides what it marks as invisible.
	/// </para>
	/// <para>
	/// The renderer never throws for malformed content: an operator that fails is skipped, a nested
	/// form that fails doesn't stop the page, and the total number of operators is bounded.
	/// </para>
	/// <para>
	/// Transparency: constant alpha (<c>CA</c>, <c>ca</c>), the separable blend modes, and soft masks
	/// (<c>SMask</c> in the graphics state, luminosity or alpha, with backdrop and transfer function),
	/// which are rendered to a coverage mask over the clipping window. Transparency groups are painted
	/// directly on the page, as non-isolated, non-knockout groups; the non-separable blend modes
	/// (Hue, Saturation, Color, Luminosity) are painted as Normal.
	/// </para>
	/// </remarks>
	internal sealed class Renderer
	{
		// the deepest nesting of forms and Type 3 glyphs.
		private const int MaxDepth = 24;
		// the deepest nesting of pattern cells and soft mask groups, each rendered by a nested renderer.
		private const int MaxPatternDepth = 4;

		// curve flattening tolerance in pixels.
		private const double Tolerance = 0.2;

		// the largest pattern cell rendered, in pixels.
		private const int MaxTilePixels = 4_000_000;

		// the shared context of the page being rendered.
		private readonly RenderContext _context;
		// scan converts the flattened paths into coverage spans.
		private readonly Rasterizer _rasterizer;
		// composites the coverage spans on the canvas with the paint, alpha, masks and blend mode.
		private readonly CompositeSink _sink;
		// collects the coverage of clipping paths into clip masks.
		private readonly MaskSink _maskSink = new MaskSink();
		// converts stroked polylines into fillable outlines.
		private readonly Stroker _stroker = new Stroker();
		// the flattened polylines of the path being painted.
		private readonly Polylines _lines = new Polylines();

		// the current path under construction.
		private readonly RenderPath _path = new RenderPath();
		// a scratch path for glyph outlines and painted areas.
		private readonly RenderPath _scratch = new RenderPath();
		// the glyph outlines collected by the clipping text render modes, applied at ET.
		private readonly RenderPath _textClip = new RenderPath();
		// true when _textClip holds outlines to intersect with the clipping region.
		private bool _hasTextClip;

		// the current graphics state.
		private RenderState _state;
		// the graphics states saved by q and restored by Q.
		private readonly Stack<RenderState> _stack = new Stack<RenderState>();

		// the text matrix: the position of the next glyph.
		private Matrix _textMatrix = Matrix.Identity;
		// the text line matrix: the start of the current line.
		private Matrix _textLineMatrix = Matrix.Identity;

		// clipping requested by W / W*: 0 none, 1 non-zero, 2 even-odd.
		private int _pendingClip;

		// marked-content stack: true for sequences hidden by optional content.
		private readonly List<bool> _markedContent = new List<bool>();
		// the number of open marked-content sequences hidden by optional content.
		private int _hiddenCount;

		// forms being executed, to detect recursion.
		private readonly HashSet<PdfStream> _activeForms = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
		// the nesting depth of forms and Type 3 glyphs, bounded by MaxDepth.
		private int _depth;
		// the nesting level of pattern cells and soft mask groups: 0 for the page.
		private readonly int _patternDepth;

		// pattern space of the current content stream: the matrix at its start (8.7.3.1).
		private Matrix _baseMatrix;

		// the alpha of the enclosing transparency groups, applied to everything painted inside them.
		private double _groupAlpha = 1;
		// the soft mask of the enclosing transparency groups, applied to everything painted inside them.
		private ClipMask _groupMask;

		// true inside uncoloured Type 3 glyphs (d1) and uncoloured patterns: colour operators are ignored.
		private bool _ignoreColor;

		/// <summary>
		/// Initializes a new instance of <see cref="Renderer"/>.
		/// </summary>
		/// <param name="context">The shared context.</param>
		/// <param name="canvas">The target canvas.</param>
		/// <param name="patternDepth">The nesting level of pattern cells: 0 for the page.</param>
		public Renderer(RenderContext context, Canvas canvas, int patternDepth = 0)
		{
			_context = context;
			_patternDepth = patternDepth;
			_rasterizer = new Rasterizer(canvas.Width, canvas.Height);
			_sink = new CompositeSink(canvas);
		}

		#region Methods

		/// <summary>
		/// Renders the page content and the annotation appearances.
		/// </summary>
		/// <param name="deviceMatrix">Maps the default user space of the page to canvas pixels.</param>
		/// <param name="clip">The initial clipping region.</param>
		public void RenderPage(Matrix deviceMatrix, ClipRegion clip)
		{
			var page = _context.Page;
			_state = new RenderState { Ctm = deviceMatrix, Clip = clip };
			_baseMatrix = deviceMatrix;

			try
			{
				Execute(page.GetContentBytes(), page.Resources);
			}
			catch (Exception)
			{
				// keep what was painted before the error.
			}

			RenderAnnotations(deviceMatrix, clip);
		}

		/// <summary>
		/// Renders a content stream with a given initial state, used for pattern cells.
		/// </summary>
		/// <param name="content">The decoded content stream.</param>
		/// <param name="resources">The resources of the stream.</param>
		/// <param name="ctm">The initial transformation matrix.</param>
		/// <param name="clip">The initial clipping region.</param>
		/// <param name="forcedColor">The colour of uncoloured patterns, or -1 for coloured content.</param>
		public void RenderContent(byte[] content, PdfDictionary resources, Matrix ctm, ClipRegion clip, int forcedColor)
		{
			_state = new RenderState { Ctm = ctm, Clip = clip };
			_baseMatrix = ctm;

			if (forcedColor >= 0)
			{
				_state.FillColor = forcedColor;
				_state.StrokeColor = forcedColor;
				_ignoreColor = true;
			}

			try
			{
				Execute(content, resources);
			}
			catch (Exception)
			{
				// keep what was painted before the error.
			}
		}

		/// <summary>
		/// Renders a form XObject with a given initial state, used for soft mask groups.
		/// </summary>
		/// <param name="form">The form XObject.</param>
		/// <param name="resources">The resources used when the form has none.</param>
		/// <param name="ctm">The transformation matrix where the form is painted.</param>
		/// <param name="clip">The initial clipping region.</param>
		public void RenderForm(PdfStream form, PdfDictionary resources, Matrix ctm, ClipRegion clip)
		{
			_state = new RenderState { Ctm = ctm, Clip = clip };
			_baseMatrix = ctm;

			try
			{
				DoForm(form, resources, null);
			}
			catch (Exception)
			{
				// keep what was painted before the error.
			}
		}

		#endregion

		#region Execution

		// reads and executes the operators of a content stream; unbalanced q/Q and BMC/EMC are discarded at the end.
		private void Execute(byte[] content, PdfDictionary resources)
		{
			if (content == null || content.Length == 0)
				return;

			resources = resources ?? new PdfDictionary();
			var reader = new ContentReader(content, _context.Document.File);
			var baseStackDepth = _stack.Count;
			var baseMarkedDepth = _markedContent.Count;
			var baseHidden = _hiddenCount;

			try
			{
				while (reader.Read(out var op))
				{
					if (--_context.OperatorBudget < 0)
						break;

					try
					{
						Dispatch(op, reader, resources);
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
				while (_stack.Count > baseStackDepth)
					_state = _stack.Pop();

				if (_markedContent.Count > baseMarkedDepth)
					_markedContent.RemoveRange(baseMarkedDepth, _markedContent.Count - baseMarkedDepth);

				_hiddenCount = baseHidden;
			}
		}

		// executes one operator with the operands held by the reader.
		private void Dispatch(string op, ContentReader r, PdfDictionary resources)
		{
			switch (op)
			{
				#region General graphics state

				case "q":
					if (_stack.Count < 4096)
					{
						_stack.Push(_state);
						_state = _state.Clone();
					}
					break;

				case "Q":
					if (_stack.Count > 0)
						_state = _stack.Pop();
					break;

				case "cm":
					if (r.HasNumbers(6))
					{
						var m = new Matrix(r.GetNumber(0), r.GetNumber(1), r.GetNumber(2), r.GetNumber(3), r.GetNumber(4), r.GetNumber(5));
						_state.Ctm = m.Multiply(_state.Ctm);
					}
					break;

				case "w":
					_state.LineWidth = Math.Abs(r.GetNumber(0));
					break;

				case "J":
					_state.LineCap = Clamp((int)r.GetNumber(0), 0, 2);
					break;

				case "j":
					_state.LineJoin = Clamp((int)r.GetNumber(0), 0, 2);
					break;

				case "M":
					_state.MiterLimit = r.GetNumber(0);
					break;

				case "d":
					_state.Dash = (r.GetValue(0) as PdfArray)?.ToNumbers();
					_state.DashPhase = r.GetNumber(1);
					break;

				case "gs":
					ApplyExtGState(resources.GetDictionary(PdfNames.ExtGState)?.GetDictionary(r.GetName(0)));
					break;

				case "ri":
				case "i":
					// rendering intent and flatness don't affect the output.
					break;

				#endregion

				#region Path construction

				case "m":
					{
						var p = _state.Ctm.Transform(r.GetNumber(0), r.GetNumber(1));
						_path.MoveTo(p.X, p.Y);
					}
					break;

				case "l":
					{
						var p = _state.Ctm.Transform(r.GetNumber(0), r.GetNumber(1));
						_path.LineTo(p.X, p.Y);
					}
					break;

				case "c":
					{
						var ctm = _state.Ctm;
						var p1 = ctm.Transform(r.GetNumber(0), r.GetNumber(1));
						var p2 = ctm.Transform(r.GetNumber(2), r.GetNumber(3));
						var p3 = ctm.Transform(r.GetNumber(4), r.GetNumber(5));
						_path.CurveTo(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
					}
					break;

				case "v":
					{
						var ctm = _state.Ctm;
						var p2 = ctm.Transform(r.GetNumber(0), r.GetNumber(1));
						var p3 = ctm.Transform(r.GetNumber(2), r.GetNumber(3));
						_path.CurveTo(_path.CurrentX, _path.CurrentY, p2.X, p2.Y, p3.X, p3.Y);
					}
					break;

				case "y":
					{
						var ctm = _state.Ctm;
						var p1 = ctm.Transform(r.GetNumber(0), r.GetNumber(1));
						var p3 = ctm.Transform(r.GetNumber(2), r.GetNumber(3));
						_path.CurveTo(p1.X, p1.Y, p3.X, p3.Y, p3.X, p3.Y);
					}
					break;

				case "h":
					_path.Close();
					break;

				case "re":
					{
						double x = r.GetNumber(0), y = r.GetNumber(1), w = r.GetNumber(2), h = r.GetNumber(3);
						var ctm = _state.Ctm;
						var p0 = ctm.Transform(x, y);
						var p1 = ctm.Transform(x + w, y);
						var p2 = ctm.Transform(x + w, y + h);
						var p3 = ctm.Transform(x, y + h);
						_path.Rectangle(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
					}
					break;

				#endregion

				#region Path painting and clipping

				case "S":
					PaintPath(false, true, FillRule.NonZero);
					break;

				case "s":
					_path.Close();
					PaintPath(false, true, FillRule.NonZero);
					break;

				case "f":
				case "F":
					PaintPath(true, false, FillRule.NonZero);
					break;

				case "f*":
					PaintPath(true, false, FillRule.EvenOdd);
					break;

				case "B":
					PaintPath(true, true, FillRule.NonZero);
					break;

				case "B*":
					PaintPath(true, true, FillRule.EvenOdd);
					break;

				case "b":
					_path.Close();
					PaintPath(true, true, FillRule.NonZero);
					break;

				case "b*":
					_path.Close();
					PaintPath(true, true, FillRule.EvenOdd);
					break;

				case "n":
					PaintPath(false, false, FillRule.NonZero);
					break;

				case "W":
					_pendingClip = 1;
					break;

				case "W*":
					_pendingClip = 2;
					break;

				#endregion

				#region Colour

				case "CS":
					if (!_ignoreColor)
					{
						_state.StrokeSpace = _context.Cache.GetColorSpace(r.GetValue(0), resources);
						_state.StrokeColor = _state.StrokeSpace.ToRgb(_state.StrokeSpace.GetInitialColor());
						_state.StrokePattern = null;
					}
					break;

				case "cs":
					if (!_ignoreColor)
					{
						_state.FillSpace = _context.Cache.GetColorSpace(r.GetValue(0), resources);
						_state.FillColor = _state.FillSpace.ToRgb(_state.FillSpace.GetInitialColor());
						_state.FillPattern = null;
					}
					break;

				case "SC":
				case "SCN":
					if (!_ignoreColor)
						_state.StrokeColor = ReadColor(r, _state.StrokeSpace, resources, out _state.StrokePattern);
					break;

				case "sc":
				case "scn":
					if (!_ignoreColor)
						_state.FillColor = ReadColor(r, _state.FillSpace, resources, out _state.FillPattern);
					break;

				case "G":
					if (!_ignoreColor)
						SetDeviceColor(false, ColorSpace.DeviceGray, r, 1);
					break;

				case "g":
					if (!_ignoreColor)
						SetDeviceColor(true, ColorSpace.DeviceGray, r, 1);
					break;

				case "RG":
					if (!_ignoreColor)
						SetDeviceColor(false, ColorSpace.DeviceRgb, r, 3);
					break;

				case "rg":
					if (!_ignoreColor)
						SetDeviceColor(true, ColorSpace.DeviceRgb, r, 3);
					break;

				case "K":
					if (!_ignoreColor)
						SetDeviceColor(false, ColorSpace.DeviceCmyk, r, 4);
					break;

				case "k":
					if (!_ignoreColor)
						SetDeviceColor(true, ColorSpace.DeviceCmyk, r, 4);
					break;

				#endregion

				#region Text objects and state

				case "BT":
					_textMatrix = Matrix.Identity;
					_textLineMatrix = Matrix.Identity;
					_textClip.Reset();
					_hasTextClip = false;
					break;

				case "ET":
					if (_hasTextClip)
					{
						ApplyClip(_textClip, FillRule.NonZero);
						_textClip.Reset();
						_hasTextClip = false;
					}
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
					_state.Font = _context.Cache.GetFont(resources.GetDictionary(PdfNames.Font)?.GetDictionary(r.GetName(0)));
					_state.FontSize = r.GetNumber(1);
					break;

				case "Tr":
					_state.RenderMode = Clamp((int)r.GetNumber(0), 0, 7);
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
						ShowText(tj.Bytes, resources);
					break;

				case "'":
					MoveTextLine(0, -_state.Leading);
					if (r.GetValue(0) is PdfString quote)
						ShowText(quote.Bytes, resources);
					break;

				case "\"":
					_state.WordSpacing = r.GetNumber(0);
					_state.CharSpacing = r.GetNumber(1);
					MoveTextLine(0, -_state.Leading);
					if (r.GetValue(2) is PdfString doubleQuote)
						ShowText(doubleQuote.Bytes, resources);
					break;

				case "TJ":
					if (r.GetValue(0) is PdfArray array)
						ShowTextArray(array, resources);
					break;

				#endregion

				#region Type 3 glyphs

				case "d0":
					break;

				case "d1":
					// the glyph is a shape painted with the current colour: its colour operators are ignored.
					_ignoreColor = true;
					break;

				#endregion

				#region XObjects, images and shadings

				case "Do":
					DoXObject(r.GetName(0), resources);
					break;

				case "BI":
					if (r.InlineImage != null && _hiddenCount == 0)
						DrawImage(r.InlineImage.Dictionary, r.InlineImage.Data, null, resources);
					break;

				case "sh":
					if (_hiddenCount == 0)
						PaintShading(resources.GetDictionary(PdfNames.Shading)?.Resolve(r.GetName(0)) as PdfDictionary, resources);
					break;

				#endregion

				#region Marked content

				case "BMC":
					BeginMarkedContent(null, null, resources);
					break;

				case "BDC":
					BeginMarkedContent(r.GetName(0), r.GetValue(1), resources);
					break;

				case "EMC":
					if (_markedContent.Count > 0)
					{
						if (_markedContent[_markedContent.Count - 1])
							_hiddenCount--;

						_markedContent.RemoveAt(_markedContent.Count - 1);
					}
					break;

				#endregion

				default:
					// MP, DP, BX, EX and unknown operators are ignored.
					break;
			}
		}

		#endregion

		#region Graphics state

		// applies the entries of an ExtGState dictionary (gs) to the current graphics state.
		private void ApplyExtGState(PdfDictionary gs)
		{
			if (gs == null)
				return;

			foreach (var entry in gs)
			{
				switch (entry.Key.Value)
				{
					case "LW":
						_state.LineWidth = Math.Abs(gs.GetNumber(entry.Key, _state.LineWidth));
						break;

					case "LC":
						_state.LineCap = Clamp(gs.GetInt(entry.Key), 0, 2);
						break;

					case "LJ":
						_state.LineJoin = Clamp(gs.GetInt(entry.Key), 0, 2);
						break;

					case "ML":
						_state.MiterLimit = gs.GetNumber(entry.Key, 10);
						break;

					case "D":
						{
							var dash = gs.GetArray(entry.Key);
							if (dash != null && dash.Count >= 2)
							{
								_state.Dash = dash.GetArray(0)?.ToNumbers();
								_state.DashPhase = dash.GetNumber(1);
							}
						}
						break;

					case "CA":
						_state.StrokeAlpha = Clamp01(gs.GetNumber(entry.Key, 1));
						break;

					case "ca":
						_state.FillAlpha = Clamp01(gs.GetNumber(entry.Key, 1));
						break;

					case "Font":
						{
							var font = gs.GetArray(entry.Key);
							if (font != null && font.Count >= 2)
							{
								_state.Font = _context.Cache.GetFont(font.GetDictionary(0));
								_state.FontSize = font.GetNumber(1);
							}
						}
						break;

					case "BM":
						_state.BlendMode = ParseBlendMode(gs.Resolve(entry.Key));
						break;

					case "SMask":
						{
							var softMask = gs.Resolve(entry.Key);
							_state.SoftMask = softMask is PdfDictionary maskDictionary ? BuildSoftMask(maskDictionary) : null;
						}
						break;

					default:
						// TR, overprint, halftones and the other entries don't affect the rendering.
						break;
				}
			}
		}

		// the blend mode of a /BM entry: a name, or an array of names of which the first is used.
		private static BlendMode ParseBlendMode(object value)
		{
			if (value is PdfArray array)
			{
				for (int i = 0; i < array.Count; i++)
				{
					if (array.GetName(i) is PdfName name)
						return ParseBlendMode(name);
				}

				return BlendMode.Normal;
			}

			switch ((value as PdfName)?.Value)
			{
				case "Multiply": return BlendMode.Multiply;
				case "Screen": return BlendMode.Screen;
				case "Overlay": return BlendMode.Overlay;
				case "Darken": return BlendMode.Darken;
				case "Lighten": return BlendMode.Lighten;
				case "ColorDodge": return BlendMode.ColorDodge;
				case "ColorBurn": return BlendMode.ColorBurn;
				case "HardLight": return BlendMode.HardLight;
				case "SoftLight": return BlendMode.SoftLight;
				case "Difference": return BlendMode.Difference;
				case "Exclusion": return BlendMode.Exclusion;
				default: return BlendMode.Normal;
			}
		}

		// sets the fill or stroke colour in a device colour space from the operands (g, rg, k and their stroke variants).
		private void SetDeviceColor(bool fill, ColorSpace space, ContentReader r, int count)
		{
			var components = new double[count];
			for (int i = 0; i < count; i++)
				components[i] = r.GetNumber(i);

			var rgb = space.ToRgb(components);
			if (fill)
			{
				_state.FillSpace = space;
				_state.FillColor = rgb;
				_state.FillPattern = null;
			}
			else
			{
				_state.StrokeSpace = space;
				_state.StrokeColor = rgb;
				_state.StrokePattern = null;
			}
		}

		// reads the sc/scn operands as an RGB colour, and returns the pattern selected by a trailing name, if any.
		private int ReadColor(ContentReader r, ColorSpace space, PdfDictionary resources, out PdfDictionary pattern)
		{
			pattern = null;

			// a trailing name selects a pattern; components before it colour uncoloured patterns.
			var count = r.OperandCount;
			var name = count > 0 ? r.GetName(count - 1) : null;
			if (name != null)
			{
				pattern = resources.GetDictionary(PdfNames.Pattern)?.Resolve(name) as PdfDictionary;
				count--;
				if (count == 0)
					return 0;
			}

			var components = new double[Math.Max(count, space.ComponentCount)];
			for (int i = 0; i < count; i++)
				components[i] = r.GetNumber(i);

			// a number of operands that doesn't match the space: the producer used a device space.
			if (count == 1 && space.ComponentCount != 1 && space.Kind != ColorSpaceKind.Pattern)
				return ColorSpace.DeviceGray.ToRgb(components);
			if (count == 3 && space.ComponentCount == 1)
				return ColorSpace.DeviceRgb.ToRgb(components);
			if (count == 4 && space.ComponentCount != 4 && space.Kind != ColorSpaceKind.Pattern)
				return ColorSpace.DeviceCmyk.ToRgb(components);

			return space.ToRgb(components);
		}

		#endregion

		#region Painting

		// paints the current path with the fill and/or stroke paint, then applies the pending clip and ends the path.
		private void PaintPath(bool fill, bool stroke, FillRule rule)
		{
			if (!_path.IsEmpty && _hiddenCount == 0)
			{
				if (fill)
				{
					var paint = GetPaint(true);
					if (paint != null)
						FillPath(_path, rule, paint, _state.FillAlpha);
				}

				if (stroke)
				{
					var paint = GetPaint(false);
					if (paint != null)
						StrokePath(_path, paint, _state.StrokeAlpha);
				}
			}

			// the clipping path takes effect after painting.
			if (_pendingClip != 0)
			{
				ApplyClip(_path, _pendingClip == 2 ? FillRule.EvenOdd : FillRule.NonZero);
				_pendingClip = 0;
			}

			_path.Reset();
		}

		// fills a device space path with a paint, inside the clipping region.
		private void FillPath(RenderPath path, FillRule rule, Paint paint, double alpha)
		{
			var clip = _state.Clip;
			if (clip.IsEmpty || alpha <= 0 || path.IsEmpty)
				return;

			path.Flatten(_lines, Tolerance);
			_rasterizer.Reset(clip.Left, clip.Top, clip.Right, clip.Bottom);
			_lines.Fill(_rasterizer);
			Composite(rule, paint, alpha);
		}

		// strokes a device space path with the line style of the current state.
		private void StrokePath(RenderPath path, Paint paint, double alpha)
		{
			var clip = _state.Clip;
			if (clip.IsEmpty || alpha <= 0 || path.IsEmpty)
				return;

			path.Flatten(_lines, Tolerance);
			_rasterizer.Reset(clip.Left, clip.Top, clip.Right, clip.Bottom);
			_stroker.Stroke(_lines, _state.Ctm, _state.GetStrokeStyle(), _rasterizer);
			Composite(FillRule.NonZero, paint, alpha);
		}

		// composites the rasterized coverage on the canvas with the paint, the alpha, the clip, the masks and the blend mode.
		private void Composite(FillRule rule, Paint paint, double alpha)
		{
			if (_rasterizer.IsEmpty)
				return;

			_sink.Paint = paint;
			_sink.Clip = _state.Clip;
			_sink.Alpha = (int)(Clamp01(alpha * _groupAlpha) * 256 + 0.5);
			_sink.SoftMask = _state.SoftMask;
			_sink.GroupMask = _groupMask;
			_sink.BlendMode = _state.BlendMode;
			_rasterizer.Sweep(rule, _sink);
		}

		// intersects the clipping region with a device space path.
		private void ApplyClip(RenderPath path, FillRule rule)
		{
			var clip = _state.Clip;
			if (clip.IsEmpty)
				return;

			// rectangles, by far the most common clipping paths, don't need a mask.
			if (path.IsRectangle(out var x0, out var y0, out var x1, out var y1))
			{
				_state.Clip = clip.Intersect(x0, y0, x1, y1);
				return;
			}

			path.Flatten(_lines, Tolerance);
			_rasterizer.Reset(clip.Left, clip.Top, clip.Right, clip.Bottom);
			_lines.Fill(_rasterizer);

			if (_rasterizer.IsEmpty)
			{
				_state.Clip = new ClipRegion(0, 0, 0, 0, null);
				return;
			}

			var left = Math.Max(clip.Left, (int)Math.Floor(_rasterizer.MinX));
			var top = Math.Max(clip.Top, (int)Math.Floor(_rasterizer.MinY));
			var right = Math.Min(clip.Right, (int)Math.Ceiling(_rasterizer.MaxX) + 1);
			var bottom = Math.Min(clip.Bottom, (int)Math.Ceiling(_rasterizer.MaxY) + 1);

			// the path covers the whole visible window: the left edges are clamped there, so the mask is needed anyway.
			var mask = new ClipMask(left, top, right, bottom);
			_maskSink.Target = mask;
			_maskSink.Previous = clip.Mask;
			_rasterizer.Sweep(rule, _maskSink);
			_maskSink.Target = null;
			_maskSink.Previous = null;

			_state.Clip = clip.WithMask(mask);
		}

		// the paint of fills or strokes: the solid colour or the pattern.
		private Paint GetPaint(bool fill)
		{
			var pattern = fill ? _state.FillPattern : _state.StrokePattern;
			var color = fill ? _state.FillColor : _state.StrokeColor;
			if (pattern == null)
				return new SolidPaint(color);

			try
			{
				return GetPatternPaint(pattern, color);
			}
			catch (Exception)
			{
				return null;
			}
		}

		#endregion

		#region Patterns and shadings

		// creates the paint of a tiling or shading pattern; color is the colour of uncoloured tiling patterns.
		private Paint GetPatternPaint(PdfDictionary pattern, int color)
		{
			var matrix = Matrix.FromArray(pattern.GetArray(PdfNames.Matrix)?.ToNumbers()).Multiply(_baseMatrix);
			switch (pattern.GetInt("PatternType"))
			{
				case 1:
					return pattern is PdfStream stream ? GetTilePaint(stream, matrix, color) : null;

				case 2:
					{
						// the shading paints the area with its own colours; ExtGState entries of the pattern are ignored.
						var shading = pattern.Resolve("Shading") as PdfDictionary;
						return Shadings.Create(shading, matrix, _state.Clip, _context.Cache, pattern.GetDictionary(PdfNames.Resources));
					}

				default:
					return null;
			}
		}

		// renders the cell of a tiling pattern and returns a paint that repeats it.
		private Paint GetTilePaint(PdfStream pattern, Matrix patternToDevice, int color)
		{
			var uncolored = pattern.GetInt("PaintType", 1) == 2;
			var key = (pattern as PdfDictionary, patternToDevice, uncolored ? color : -1);
			if (_context.Tiles.TryGetValue(key, out var cached))
				return cached;

			Paint paint = null;
			if (_patternDepth < MaxPatternDepth && Math.Abs(patternToDevice.Determinant) > 1e-12)
				paint = RenderTile(pattern, patternToDevice, uncolored ? color : -1);

			_context.Tiles[key] = paint;
			return paint;
		}

		// renders one pattern cell to a bitmap and returns a paint that repeats it in device space, or null.
		private Paint RenderTile(PdfStream pattern, Matrix patternToDevice, int forcedColor)
		{
			var bbox = pattern.GetArray(PdfNames.BBox)?.ToNumbers();
			if (bbox == null || bbox.Length < 4)
				return null;

			var bx0 = Math.Min(bbox[0], bbox[2]);
			var by0 = Math.Min(bbox[1], bbox[3]);
			var bx1 = Math.Max(bbox[0], bbox[2]);
			var by1 = Math.Max(bbox[1], bbox[3]);
			var xStep = Math.Abs(pattern.GetNumber("XStep", bx1 - bx0));
			var yStep = Math.Abs(pattern.GetNumber("YStep", by1 - by0));
			if (!(xStep > 0) || !(yStep > 0) || double.IsInfinity(xStep) || double.IsInfinity(yStep))
				return null;

			// size of one cell on the device.
			var cellWidth = xStep * patternToDevice.ScaleX;
			var cellHeight = yStep * patternToDevice.ScaleY;

			// cells smaller than a few pixels are rendered larger and averaged: their detail can't be seen.
			var average = cellWidth < 3 || cellHeight < 3;
			var tw = average ? 16 : (int)Math.Round(cellWidth);
			var th = average ? 16 : (int)Math.Round(cellHeight);
			if ((long)tw * th > MaxTilePixels)
			{
				var scale = Math.Sqrt((double)MaxTilePixels / ((long)tw * th));
				tw = Math.Max(1, (int)(tw * scale));
				th = Math.Max(1, (int)(th * scale));
			}

			tw = Math.Max(1, tw);
			th = Math.Max(1, th);

			// pattern space -> tile pixels, with the y axis flipped like the device.
			var sx = tw / xStep;
			var sy = th / yStep;
			var patternToTile = new Matrix(sx, 0, 0, -sy, -bx0 * sx, (by0 + yStep) * sy);

			var canvas = new Canvas(tw, th);
			var renderer = new Renderer(_context, canvas, _patternDepth + 1);

			// the cell content is clipped to the bounding box.
			patternToTile.TransformBounds(bx0, by0, bx1, by1, out var cx0, out var cy0, out var cx1, out var cy1);
			var clip = new ClipRegion(Math.Max(0, cx0), Math.Max(0, cy0), Math.Min(tw, cx1), Math.Min(th, cy1), null);

			byte[] content;
			try
			{
				content = pattern.GetBytes();
			}
			catch (Exception)
			{
				return null;
			}

			renderer.RenderContent(content, pattern.GetDictionary(PdfNames.Resources), patternToTile, clip, forcedColor);

			var bitmap = new Bitmap(tw, th, canvas.Pixels);
			if (average)
				bitmap = Average(bitmap);

			// device -> pattern -> tile pixels.
			var deviceToTile = patternToDevice.Invert().Multiply(patternToTile);
			if (average)
				deviceToTile = Matrix.Identity;

			return new TilePaint(bitmap, deviceToTile);
		}

		// the average colour of a bitmap, as a 1 x 1 bitmap.
		private static Bitmap Average(Bitmap bitmap)
		{
			long r = 0, g = 0, b = 0, a = 0;
			var pixels = bitmap.Pixels;
			for (int i = 0; i < pixels.Length; i += 4)
			{
				r += pixels[i];
				g += pixels[i + 1];
				b += pixels[i + 2];
				a += pixels[i + 3];
			}

			var count = Math.Max(1, pixels.Length / 4);
			return new Bitmap(1, 1, new[] { (byte)(r / count), (byte)(g / count), (byte)(b / count), (byte)(a / count) });
		}

		// the "sh" operator: paints a shading over the clipping region, or its bounding box.
		private void PaintShading(PdfDictionary shading, PdfDictionary resources)
		{
			var clip = _state.Clip;
			if (shading == null || clip.IsEmpty)
				return;

			Paint paint;
			try
			{
				paint = Shadings.Create(shading, _state.Ctm, clip, _context.Cache, resources);
			}
			catch (Exception)
			{
				paint = null;
			}

			if (paint == null)
				return;

			_scratch.Reset();
			var bbox = shading.GetArray(PdfNames.BBox)?.ToNumbers();
			if (bbox != null && bbox.Length >= 4)
			{
				var ctm = _state.Ctm;
				var p0 = ctm.Transform(bbox[0], bbox[1]);
				var p1 = ctm.Transform(bbox[2], bbox[1]);
				var p2 = ctm.Transform(bbox[2], bbox[3]);
				var p3 = ctm.Transform(bbox[0], bbox[3]);
				_scratch.Rectangle(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
			}
			else
			{
				_scratch.Rectangle(clip.Left, clip.Top, clip.Right, clip.Top, clip.Right, clip.Bottom, clip.Left, clip.Bottom);
			}

			FillPath(_scratch, FillRule.NonZero, paint, _state.FillAlpha);
		}

		#endregion

		#region Soft masks

		// renders the group of a soft mask dictionary (11.6.5.2) to a mask over the current clipping window.
		private ClipMask BuildSoftMask(PdfDictionary dictionary)
		{
			var group = dictionary.GetStream("G");
			var clip = _state.Clip;
			if (group == null || clip.IsEmpty || _patternDepth >= MaxPatternDepth)
				return null;

			var key = (dictionary, _state.Ctm, clip.Left, clip.Top, clip.Right, clip.Bottom);
			if (_context.SoftMasks.TryGetValue(key, out var cached))
				return cached;

			ClipMask mask = null;
			try
			{
				mask = RenderSoftMask(dictionary, group, clip);
			}
			catch (Exception)
			{
				// a broken mask is ignored: the content is painted unmasked.
			}

			_context.SoftMasks[key] = mask;
			return mask;
		}

		// renders a soft mask group on its own canvas and converts it to a luminosity or alpha mask over the clipping window.
		private ClipMask RenderSoftMask(PdfDictionary dictionary, PdfStream group, ClipRegion clip)
		{
			var luminosity = dictionary.GetNameValue("S") != "Alpha";
			var width = clip.Right - clip.Left;
			var height = clip.Bottom - clip.Top;
			if ((long)width * height > MaxTilePixels * 4L)
				return null;

			// the backdrop of luminosity masks, in the colour space of the group (black by default).
			var backdrop = 0;
			var bc = dictionary.GetArray("BC")?.ToNumbers();
			if (luminosity && bc != null && bc.Length > 0)
			{
				var space = _context.Cache.GetColorSpace(group.GetDictionary("Group")?.Resolve(PdfNames.ColorSpace, "CS") ?? PdfName.Get("DeviceGray"), group.GetDictionary(PdfNames.Resources));
				backdrop = space.ToRgb(bc);
			}

			// the group is painted where it is used, offset to the window.
			var canvas = new Canvas(width, height);
			if (luminosity)
				canvas.Clear(backdrop);

			var offset = Matrix.Translation(-clip.Left, -clip.Top);
			var renderer = new Renderer(_context, canvas, _patternDepth + 1);
			var window = new ClipRegion(clip.X0 - clip.Left, clip.Y0 - clip.Top, clip.X1 - clip.Left, clip.Y1 - clip.Top, null);
			renderer.RenderForm(group, group.GetDictionary(PdfNames.Resources) ?? new PdfDictionary(), _state.Ctm.Multiply(offset), window);

			// the transfer function maps the mask values.
			var transfer = BuildTransfer(dictionary.Resolve("TR"));

			var mask = new ClipMask(clip.Left, clip.Top, clip.Right, clip.Bottom);
			var pixels = canvas.Pixels;
			var data = mask.Data;
			for (int i = 0, p = 0; i < data.Length; i++, p += 4)
			{
				// luminosity of the opaque composited group (Rec. 601 weights), or the alpha of the group.
				var value = luminosity ? (pixels[p] * 77 + pixels[p + 1] * 150 + pixels[p + 2] * 29) >> 8 : pixels[p + 3];
				data[i] = transfer != null ? transfer[value] : (byte)value;
			}

			return mask;
		}

		// the product of two masks, over the intersection of their bounds; either can be null.
		private static ClipMask CombineMasks(ClipMask first, ClipMask second)
		{
			if (first == null)
				return second;
			if (second == null)
				return first;

			var combined = new ClipMask(Math.Max(first.X0, second.X0), Math.Max(first.Y0, second.Y0), Math.Min(first.X1, second.X1), Math.Min(first.Y1, second.Y1));
			var data = combined.Data;
			for (int y = combined.Y0, i = 0; y < combined.Y1; y++)
			{
				for (int x = combined.X0; x < combined.X1; x++, i++)
				{
					var a = first.Get(x, y);
					var b = second.Get(x, y);
					data[i] = (byte)((a * b + 127) / 255);
				}
			}

			return combined;
		}

		// the transfer function of a soft mask as a lookup table, or null for the identity.
		private static byte[] BuildTransfer(object definition)
		{
			if (definition == null || definition is PdfName)
				return null;

			var function = PdfFunction.Create(definition);
			if (function == null)
				return null;

			var table = new byte[256];
			var input = new double[1];
			var output = new double[Math.Max(1, function.OutputCount)];
			for (int i = 0; i < 256; i++)
			{
				input[0] = i / 255.0;
				try
				{
					function.Evaluate(input, output);
				}
				catch (Exception)
				{
					output[0] = input[0];
				}

				var value = output[0];
				table[i] = (byte)(!(value > 0) ? 0 : value >= 1 ? 255 : (int)(value * 255 + 0.5));
			}

			return table;
		}

		#endregion

		#region Text

		// moves to the start of the next line, offset from the start of the current line (Td, TD, T*).
		private void MoveTextLine(double tx, double ty)
		{
			_textLineMatrix = Matrix.Translation(tx, ty).Multiply(_textLineMatrix);
			_textMatrix = _textLineMatrix;
		}

		// shows the strings of a TJ array, moving the text position by the number adjustments in between.
		private void ShowTextArray(PdfArray array, PdfDictionary resources)
		{
			var font = _state.Font;
			for (int i = 0; i < array.Count; i++)
			{
				var item = array.Resolve(i);
				if (item is PdfString s)
				{
					ShowText(s.Bytes, resources);
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

		// shows a string: paints each glyph in the current text render mode and advances the text position.
		private void ShowText(byte[] bytes, PdfDictionary resources)
		{
			var font = _state.Font ?? _context.Cache.GetFont(null);
			var fontSize = _state.FontSize;
			var hScale = _state.HorizontalScaling;
			var fontMatrix = font.FontMatrix;
			var vertical = font.IsVertical;
			var mode = _state.RenderMode;

			// nothing to paint: only advance the text position.
			var visible = _hiddenCount == 0 && mode != 3 && !_state.Clip.IsEmpty;

			for (int offset = 0; offset < bytes.Length;)
			{
				var code = font.ReadCode(bytes, offset, out var length);
				offset += Math.Max(1, length);

				var width = font.GetWidth(code) * fontMatrix.A;
				var spacing = _state.CharSpacing + (font.IsWordSpace(code, length) ? _state.WordSpacing : 0);

				if (visible || mode >= 4 && _hiddenCount == 0)
				{
					// the rendering matrix maps glyph text space (scaled by the font size) to device space.
					var trm = new Matrix(fontSize * hScale, 0, 0, fontSize, 0, _state.Rise).Multiply(_textMatrix).Multiply(_state.Ctm);
					if (vertical)
					{
						font.GetVerticalMetrics(code, out _, out var vx, out var vy);
						trm = Matrix.Translation(-vx * fontMatrix.A, -vy * fontMatrix.A).Multiply(trm);
					}

					try
					{
						if (font is Type3Font type3)
							DrawType3Glyph(type3, code, trm, resources);
						else
							DrawGlyph(font, code, trm, width);
					}
					catch (Exception)
					{
						// a broken glyph doesn't stop the string.
					}
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
			}
		}

		// draws a glyph of an embedded or substituted font program.
		private void DrawGlyph(PdfFont font, int code, Matrix trm, double width)
		{
			var program = font.Program;
			var glyph = program != null ? font.GetGlyphId(code) : -1;

			// glyph 0 is .notdef: missing glyphs of an embedded subset are not drawn, as viewers do.
			if (program != null && glyph >= 0)
			{
				if (glyph > 0)
					DrawOutline(program, glyph, Matrix.Identity, trm);

				return;
			}

			// not embedded, or a broken program: substitute a system font.
			var substitute = SystemFonts.Get(font);
			if (substitute == null)
				return;

			var substituted = substitute.GetGlyphs(code);
			var glyphs = substituted.Glyphs;
			if (glyphs.Length == 0)
				return;

			var substituteProgram = substituted.Program;
			var unitScale = GetProgramMatrix(substituteProgram).A;

			// the natural advance of the substitute glyphs, in text space.
			double natural = 0;
			for (int i = 0; i < glyphs.Length; i++)
				natural += substituteProgram.GetAdvanceWidth(glyphs[i]) * unitScale;

			// squeeze or stretch the substitute to the width of the original glyph, within reason.
			var scale = 1.0;
			if (width > 0 && natural > 0)
				scale = Math.Max(0.5, Math.Min(2, width / natural));

			var x = 0.0;
			for (int i = 0; i < glyphs.Length; i++)
			{
				var position = new Matrix(scale, 0, 0, 1, x, 0);
				DrawOutline(substituteProgram, glyphs[i], position, trm);
				x += substituteProgram.GetAdvanceWidth(glyphs[i]) * unitScale * scale;
			}
		}

		// paints the outline of a glyph according to the text rendering mode.
		private void DrawOutline(FontProgram program, int glyph, Matrix position, Matrix trm)
		{
			var outline = program.GetGlyphPath(glyph);
			if (outline == null || outline.IsEmpty)
				return;

			// glyph space -> text space -> (position in the string) -> device space.
			var m = GetProgramMatrix(program).Multiply(position).Multiply(trm);
			var mode = _state.RenderMode;
			var fill = mode == 0 || mode == 2 || mode == 4 || mode == 6;
			var stroke = mode == 1 || mode == 2 || mode == 5 || mode == 6;
			var clip = mode >= 4;

			if ((fill || stroke) && _hiddenCount == 0)
			{
				// skip glyphs outside the visible window, before building the path.
				outline.GetBounds(out var gx0, out var gy0, out var gx1, out var gy1);
				m.TransformBounds(gx0, gy0, gx1, gy1, out var left, out var top, out var right, out var bottom);
				var c = _state.Clip;
				var margin = stroke ? _state.LineWidth * Math.Max(_state.Ctm.ScaleX, _state.Ctm.ScaleY) + 2 : 1;
				if (right + margin >= c.Left && left - margin <= c.Right && bottom + margin >= c.Top && top - margin <= c.Bottom)
				{
					_scratch.Reset();
					outline.Visit(_scratch, m.A, m.B, m.C, m.D, m.E, m.F);

					if (fill)
					{
						var paint = GetPaint(true);
						if (paint != null)
							FillPath(_scratch, FillRule.NonZero, paint, _state.FillAlpha);
					}

					if (stroke)
					{
						var paint = GetPaint(false);
						if (paint != null)
							StrokePath(_scratch, paint, _state.StrokeAlpha);
					}
				}
			}

			if (clip)
			{
				outline.Visit(_textClip, m.A, m.B, m.C, m.D, m.E, m.F);
				_hasTextClip = true;
			}
		}

		// the matrix of a font program: glyph space -> text space.
		private static Matrix GetProgramMatrix(FontProgram program)
		{
			var values = program.FontMatrix;
			if (values == null || values.Length < 6 || Math.Abs(values[0] * values[3] - values[1] * values[2]) < 1e-12)
				return new Matrix(0.001, 0, 0, 0.001, 0, 0);

			return new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]);
		}

		// executes the glyph procedure of a Type 3 font.
		private void DrawType3Glyph(Type3Font font, int code, Matrix trm, PdfDictionary resources)
		{
			if (_hiddenCount > 0 || _state.RenderMode == 3 || _state.RenderMode == 7 || _depth >= MaxDepth)
				return;

			var procedure = font.GetCharProc(code);
			if (procedure == null || !_activeForms.Add(procedure))
				return;

			_stack.Push(_state);
			_state = _state.Clone();
			var savedText = _textMatrix;
			var savedLine = _textLineMatrix;
			var savedBase = _baseMatrix;
			var savedIgnore = _ignoreColor;
			var savedPath = _pendingClip;
			_depth++;

			try
			{
				// glyph space -> text space (font matrix) -> device space.
				_state.Ctm = font.FontMatrix.Multiply(trm);
				_state.RenderMode = 0;
				_baseMatrix = _state.Ctm;
				_path.Reset();
				_pendingClip = 0;

				byte[] content;
				try
				{
					content = procedure.GetBytes();
				}
				catch (Exception)
				{
					content = null;
				}

				Execute(content, font.Resources ?? resources);
			}
			finally
			{
				_depth--;
				_state = _stack.Pop();
				_textMatrix = savedText;
				_textLineMatrix = savedLine;
				_baseMatrix = savedBase;
				_ignoreColor = savedIgnore;
				_pendingClip = savedPath;
				_path.Reset();
				_activeForms.Remove(procedure);
			}
		}

		#endregion

		#region XObjects and images

		// paints the XObject with the given name: a form or an image, unless hidden by optional content.
		private void DoXObject(PdfName name, PdfDictionary resources)
		{
			if (name == null)
				return;

			var xobjects = resources.GetDictionary(PdfNames.XObject);
			if (!(xobjects?.Resolve(name) is PdfStream stream))
				return;

			// optional content attached to the XObject itself.
			var optionalContent = _context.OptionalContent;
			if (optionalContent != null && !optionalContent.IsVisible(stream.Resolve(PdfNames.OC)))
				return;

			switch (stream.GetNameValue(PdfNames.Subtype))
			{
				case "Image":
					if (_hiddenCount == 0)
						DrawImage(stream, null, stream, resources);
					break;

				case "Form":
					DoForm(stream, resources, null);
					break;

				default:
					// PostScript XObjects are ignored.
					break;
			}
		}

		// executes a form XObject in its own graphics state; extraMatrix, when present, maps the form into an annotation rectangle.
		private void DoForm(PdfStream form, PdfDictionary resources, Matrix? extraMatrix)
		{
			// a transparency group is composited as a whole with the alpha and soft mask current at "Do" (11.6.6):
			// a fully transparent group is invisible.
			var isGroup = form.GetDictionary("Group")?.GetNameValue(PdfNames.S) == "Transparency";
			if (isGroup && _state.FillAlpha * _groupAlpha <= 0)
				return;

			if (_depth >= MaxDepth || !_activeForms.Add(form))
				return;

			_stack.Push(_state);
			_state = _state.Clone();
			var savedBase = _baseMatrix;
			var savedText = _textMatrix;
			var savedLine = _textLineMatrix;
			var savedGroupAlpha = _groupAlpha;
			var savedGroupMask = _groupMask;
			_depth++;

			try
			{
				if (isGroup)
				{
					// the content starts with the initial alpha and soft mask; the group ones apply on top of them.
					// (the alpha multiplies the alphas of the content: exact unless the content overlaps itself.)
					_groupAlpha *= _state.FillAlpha;
					_groupMask = CombineMasks(_groupMask, _state.SoftMask);
					_state.FillAlpha = 1;
					_state.StrokeAlpha = 1;
					_state.SoftMask = null;
				}

				var matrix = Matrix.FromArray(form.GetArray(PdfNames.Matrix)?.ToNumbers());
				if (extraMatrix.HasValue)
					matrix = matrix.Multiply(extraMatrix.Value);

				_state.Ctm = matrix.Multiply(_state.Ctm);
				_baseMatrix = _state.Ctm;

				// the form bounding box clips its content.
				var bbox = form.GetArray(PdfNames.BBox)?.ToNumbers();
				if (bbox != null && bbox.Length >= 4)
				{
					var ctm = _state.Ctm;
					var p0 = ctm.Transform(bbox[0], bbox[1]);
					var p1 = ctm.Transform(bbox[2], bbox[1]);
					var p2 = ctm.Transform(bbox[2], bbox[3]);
					var p3 = ctm.Transform(bbox[0], bbox[3]);
					_scratch.Reset();
					_scratch.Rectangle(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
					ApplyClip(_scratch, FillRule.NonZero);
				}

				if (_state.Clip.IsEmpty)
					return;

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

				// the current path is not part of the graphics state: keep it apart from the form.
				Execute(content, formResources);
			}
			finally
			{
				_depth--;
				_state = _stack.Pop();
				_baseMatrix = savedBase;
				_textMatrix = savedText;
				_textLineMatrix = savedLine;
				_groupAlpha = savedGroupAlpha;
				_groupMask = savedGroupMask;
				_activeForms.Remove(form);
			}
		}

		// draws an image XObject (stream not null) or an inline image.
		private void DrawImage(PdfDictionary dictionary, byte[] inlineData, PdfStream stream, PdfDictionary resources)
		{
			var clip = _state.Clip;
			var ctm = _state.Ctm;
			if (clip.IsEmpty || _state.FillAlpha <= 0 || Math.Abs(ctm.Determinant) < 1e-12)
				return;

			// the image occupies the unit square of user space.
			ctm.TransformBounds(0, 0, 1, 1, out var left, out var top, out var right, out var bottom);
			if (right < clip.Left || left > clip.Right || bottom < clip.Top || top > clip.Bottom)
				return;

			var isMask = dictionary.GetBool(PdfNames.ImageMask) || dictionary.GetBool("IM");
			var fillColor = _state.FillColor;
			var cached = GetImage(dictionary, inlineData, stream, resources, isMask ? fillColor : 0);
			var raster = cached?.Raster;
			if (raster == null)
				return;

			// strongly downscaled images are first reduced with area averaging, then sampled bilinearly.
			var deviceWidth = ctm.ScaleX;
			var deviceHeight = ctm.ScaleY;
			var targetWidth = raster.Width > 2 * deviceWidth + 2 ? Math.Max(1, (int)Math.Ceiling(deviceWidth)) : raster.Width;
			var targetHeight = raster.Height > 2 * deviceHeight + 2 ? Math.Max(1, (int)Math.Ceiling(deviceHeight)) : raster.Height;

			var bitmap = cached.Bitmap;
			if (bitmap == null || bitmap.Width != targetWidth || bitmap.Height != targetHeight)
			{
				bitmap = Bitmap.FromRaster(raster, targetWidth, targetHeight);
				cached.Bitmap = bitmap;
			}

			// image pixel space -> unit square (row 0 at the top) -> device.
			var pixelToDevice = new Matrix(1.0 / bitmap.Width, 0, 0, -1.0 / bitmap.Height, 0, 1).Multiply(ctm);
			var paint = new ImagePaint(bitmap, pixelToDevice.Invert());

			var p0 = ctm.Transform(0, 0);
			var p1 = ctm.Transform(1, 0);
			var p2 = ctm.Transform(1, 1);
			var p3 = ctm.Transform(0, 1);
			_scratch.Reset();
			_scratch.Rectangle(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
			FillPath(_scratch, FillRule.NonZero, paint, _state.FillAlpha);
		}

		// returns the decoded image, cached per stream and fill colour; inline images are decoded every time.
		private CachedImage GetImage(PdfDictionary dictionary, byte[] inlineData, PdfStream stream, PdfDictionary resources, int fillColor)
		{
			// inline images are not shared: decode them directly.
			if (stream == null)
				return new CachedImage { Raster = Decode(dictionary, inlineData, resources, fillColor) };

			var key = (stream, fillColor);
			if (!_context.Images.TryGetValue(key, out var cached))
			{
				byte[] data;
				try
				{
					data = stream.GetRawBytes();
				}
				catch (Exception)
				{
					data = null;
				}

				cached = new CachedImage { Raster = data != null ? Decode(stream, data, resources, fillColor) : null };
				_context.Images[key] = cached;
			}

			return cached;
		}

		// decodes an image to a raster, or null when the image is broken.
		private RasterImage Decode(PdfDictionary dictionary, byte[] data, PdfDictionary resources, int fillColor)
		{
			try
			{
				return ImageDecoder.Decode(dictionary, data, resources, _context.Cache, fillColor, _context.ImageOptions)?.Raster;
			}
			catch (Exception)
			{
				return null;
			}
		}

		#endregion

		#region Marked content

		// begins a marked-content sequence (BMC, BDC), hidden when it marks optional content that is not visible.
		private void BeginMarkedContent(PdfName tag, object properties, PdfDictionary resources)
		{
			var hidden = false;

			// "/OC /name BDC": the properties are an optional content group or membership dictionary.
			var optionalContent = _context.OptionalContent;
			if (tag?.Value == "OC" && optionalContent != null)
			{
				var dictionary = properties as PdfDictionary;
				if (dictionary == null && properties is PdfName propertiesName)
					dictionary = resources.GetDictionary(PdfNames.Properties)?.GetDictionary(propertiesName);

				if (dictionary != null && !optionalContent.IsVisible(dictionary))
					hidden = true;
			}

			_markedContent.Add(hidden);
			if (hidden)
				_hiddenCount++;
		}

		#endregion

		#region Annotations

		// paints the normal appearances of the page annotations; a broken annotation is skipped.
		private void RenderAnnotations(Matrix deviceMatrix, ClipRegion clip)
		{
			var page = _context.Page;
			var annotations = page.Dictionary.GetArray(PdfNames.Annots);
			if (annotations == null)
				return;

			for (int i = 0; i < annotations.Count; i++)
			{
				var annotation = annotations.GetDictionary(i);
				if (annotation == null)
					continue;

				try
				{
					RenderAnnotation(annotation, deviceMatrix, clip);
				}
				catch (Exception)
				{
					// a broken annotation doesn't affect the others.
				}
			}
		}

		// paints the normal appearance of an annotation fitted to its rectangle, unless it is hidden.
		private void RenderAnnotation(PdfDictionary annotation, Matrix deviceMatrix, ClipRegion clip)
		{
			if (annotation.GetNameValue(PdfNames.Subtype) == "Popup")
				return;

			// hidden (2) and no-view (32) annotations are not displayed.
			var flags = annotation.GetInt(PdfNames.F);
			if ((flags & 2) != 0 || (flags & 32) != 0)
				return;

			var optionalContent = _context.OptionalContent;
			if (optionalContent != null && !optionalContent.IsVisible(annotation.Resolve(PdfNames.OC)))
				return;

			var appearance = GetNormalAppearance(annotation);
			var rect = annotation.GetArray(PdfNames.Rect)?.ToNumbers();
			if (appearance == null || rect == null || rect.Length < 4)
				return;

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

			_state = new RenderState { Ctm = deviceMatrix, Clip = clip };
			_baseMatrix = deviceMatrix;
			_markedContent.Clear();
			_hiddenCount = 0;
			_path.Reset();
			_pendingClip = 0;

			DoForm(appearance, _context.Page.Resources, fit);
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

		#region Helpers

		// clamps an integer to [min, max].
		private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

		// clamps a value to [0, 1]; NaN becomes 0.
		private static double Clamp01(double value) => !(value > 0) ? 0 : value > 1 ? 1 : value;

		#endregion
	}
}
