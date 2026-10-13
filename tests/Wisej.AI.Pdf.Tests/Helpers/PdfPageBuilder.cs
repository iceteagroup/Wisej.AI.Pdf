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

// Test helper: builds the content stream, resources and annotations of a page.

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wisej.AI.Pdf.Tests.Helpers
{
	/// <summary>
	/// A page of a <see cref="PdfBuilder"/> document.
	/// </summary>
	/// <remarks>
	/// The drawing methods append operators to the content stream and register the resources
	/// they use, so the tests only deal with coordinates and text. Coordinates are in PDF user
	/// space: points from the bottom-left corner of the page.
	/// </remarks>
	internal sealed class PdfPageBuilder
	{
		// the document that owns the page.
		private readonly PdfBuilder _owner;

		// the object number of the content stream.
		private readonly int _contentNumber;

		// the content stream operators.
		private readonly StringBuilder _content = new StringBuilder();

		// the resources used by the content, by name.
		private readonly Dictionary<string, PdfResource> _resources = new Dictionary<string, PdfResource>();

		// the object numbers of the page annotations.
		private readonly List<int> _annotations = new List<int>();

		// additional page dictionary entries in PDF syntax.
		private readonly List<string> _entries = new List<string>();

		/// <summary>
		/// Initializes a new page.
		/// </summary>
		/// <param name="owner">The document.</param>
		/// <param name="number">The object number of the page dictionary.</param>
		/// <param name="contentNumber">The object number of the content stream.</param>
		/// <param name="width">The width of the media box.</param>
		/// <param name="height">The height of the media box.</param>
		internal PdfPageBuilder(PdfBuilder owner, int number, int contentNumber, double width, double height)
		{
			_owner = owner;
			_contentNumber = contentNumber;
			this.Number = number;
			this.Width = width;
			this.Height = height;
		}

		#region Properties

		/// <summary>
		/// The object number of the page dictionary.
		/// </summary>
		public int Number { get; }

		/// <summary>
		/// The indirect reference to the page dictionary.
		/// </summary>
		public string Reference => PdfText.Reference(this.Number);

		/// <summary>
		/// The width of the media box, in points.
		/// </summary>
		public double Width { get; }

		/// <summary>
		/// The height of the media box, in points.
		/// </summary>
		public double Height { get; }

		/// <summary>
		/// The /Rotate value of the page. Default: 0.
		/// </summary>
		public int Rotation { get; set; }

		/// <summary>
		/// Compress the content stream with FlateDecode. Default: false, which keeps files readable when debugging.
		/// </summary>
		public bool CompressContent { get; set; }

		#endregion

		#region Content

		/// <summary>
		/// Appends raw operators to the content stream.
		/// </summary>
		/// <param name="operators">The operators in PDF syntax (a byte string).</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder Content(string operators)
		{
			_content.Append(operators).Append('\n');
			return this;
		}

		/// <summary>
		/// Registers resources used by raw content, without drawing anything.
		/// </summary>
		/// <param name="resources">The resources.</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder Use(params PdfResource[] resources)
		{
			foreach (var resource in resources)
				_resources[resource.Category + "/" + resource.Name] = resource;

			return this;
		}

		/// <summary>
		/// Draws a line of text in its own text object.
		/// </summary>
		/// <param name="font">The font.</param>
		/// <param name="size">The font size in points.</param>
		/// <param name="x">The x coordinate of the baseline origin.</param>
		/// <param name="y">The y coordinate of the baseline origin.</param>
		/// <param name="text">The text, encoded with WinAnsiEncoding.</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder Text(PdfResource font, double size, double x, double y, string text)
		{
			Use(font);
			return Content($"BT /{font.Name} {PdfText.Number(size)} Tf {PdfText.Numbers(x, y)} Td {PdfText.Literal(text)} Tj ET");
		}

		/// <summary>
		/// Draws lines of text with a fixed leading, starting at (<paramref name="x"/>, <paramref name="y"/>).
		/// </summary>
		/// <param name="font">The font.</param>
		/// <param name="size">The font size in points.</param>
		/// <param name="x">The x coordinate of the first baseline.</param>
		/// <param name="y">The y coordinate of the first baseline.</param>
		/// <param name="leading">The distance between baselines.</param>
		/// <param name="lines">The lines.</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder Paragraph(PdfResource font, double size, double x, double y, double leading, params string[] lines)
		{
			for (int i = 0; i < lines.Length; i++)
				Text(font, size, x, y - i * leading, lines[i]);

			return this;
		}

		/// <summary>
		/// Draws an image or a form XObject scaled to a rectangle.
		/// </summary>
		/// <param name="xobject">The XObject.</param>
		/// <param name="x">The left edge.</param>
		/// <param name="y">The bottom edge.</param>
		/// <param name="width">The width.</param>
		/// <param name="height">The height.</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder Draw(PdfResource xobject, double x, double y, double width, double height)
		{
			return Draw(xobject, width, 0, 0, height, x, y);
		}

		/// <summary>
		/// Draws an XObject with an arbitrary transformation matrix.
		/// </summary>
		/// <param name="xobject">The XObject.</param>
		/// <param name="a">Matrix element a.</param>
		/// <param name="b">Matrix element b.</param>
		/// <param name="c">Matrix element c.</param>
		/// <param name="d">Matrix element d.</param>
		/// <param name="e">Matrix element e (translation x).</param>
		/// <param name="f">Matrix element f (translation y).</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder Draw(PdfResource xobject, double a, double b, double c, double d, double e, double f)
		{
			Use(xobject);
			return Content($"q {PdfText.Numbers(a, b, c, d, e, f)} cm /{xobject.Name} Do Q");
		}

		/// <summary>
		/// Strokes a straight line.
		/// </summary>
		/// <param name="x1">The x coordinate of the start.</param>
		/// <param name="y1">The y coordinate of the start.</param>
		/// <param name="x2">The x coordinate of the end.</param>
		/// <param name="y2">The y coordinate of the end.</param>
		/// <param name="width">The line width.</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder Line(double x1, double y1, double x2, double y2, double width = 0.75)
		{
			return Content($"{PdfText.Number(width)} w {PdfText.Numbers(x1, y1)} m {PdfText.Numbers(x2, y2)} l S");
		}

		/// <summary>
		/// Fills a rectangle with an RGB colour.
		/// </summary>
		/// <param name="x">The left edge.</param>
		/// <param name="y">The bottom edge.</param>
		/// <param name="width">The width.</param>
		/// <param name="height">The height.</param>
		/// <param name="rgb">The colour as 0xRRGGBB.</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder FillRectangle(double x, double y, double width, double height, int rgb)
		{
			var r = ((rgb >> 16) & 0xFF) / 255.0;
			var g = ((rgb >> 8) & 0xFF) / 255.0;
			var b = (rgb & 0xFF) / 255.0;
			return Content($"q {PdfText.Numbers(r, g, b)} rg {PdfText.Numbers(x, y, width, height)} re f Q");
		}

		#endregion

		#region Annotations and entries

		/// <summary>
		/// Adds a link annotation to a URI.
		/// </summary>
		/// <param name="left">The left edge.</param>
		/// <param name="bottom">The bottom edge.</param>
		/// <param name="right">The right edge.</param>
		/// <param name="top">The top edge.</param>
		/// <param name="uri">The target URI.</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder Link(double left, double bottom, double right, double top, string uri)
		{
			var rect = PdfText.Array(left, bottom, right, top);
			_annotations.Add(_owner.AddObject($"<< /Type /Annot /Subtype /Link /Rect {rect} /Border [0 0 0] /A << /S /URI /URI {PdfText.TextString(uri)} >> >>"));
			return this;
		}

		/// <summary>
		/// Adds a link annotation to another page.
		/// </summary>
		/// <param name="left">The left edge.</param>
		/// <param name="bottom">The bottom edge.</param>
		/// <param name="right">The right edge.</param>
		/// <param name="top">The top edge.</param>
		/// <param name="target">The target page.</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder Link(double left, double bottom, double right, double top, PdfPageBuilder target)
		{
			var rect = PdfText.Array(left, bottom, right, top);
			_annotations.Add(_owner.AddObject($"<< /Type /Annot /Subtype /Link /Rect {rect} /Border [0 0 0] /Dest [{target.Reference} /Fit] >>"));
			return this;
		}

		/// <summary>
		/// Adds an entry to the page dictionary.
		/// </summary>
		/// <param name="entry">The key and value in PDF syntax, for example "/UserUnit 2".</param>
		/// <returns>This page.</returns>
		public PdfPageBuilder Entry(string entry)
		{
			_entries.Add(entry);
			return this;
		}

		#endregion

		#region Implementation

		/// <summary>
		/// Writes the page dictionary and the content stream to the document.
		/// </summary>
		/// <param name="parent">The object number of the page tree node.</param>
		internal void Materialize(int parent)
		{
			_owner.SetStream(_contentNumber, string.Empty, PdfText.ToBytes(_content.ToString()), this.CompressContent);

			var sb = new StringBuilder("<< /Type /Page");
			sb.Append(" /Parent ").Append(PdfText.Reference(parent));
			sb.Append(" /MediaBox ").Append(PdfText.Array(0, 0, this.Width, this.Height));
			sb.Append(" /Resources ").Append(PdfBuilder.FormatResources(_resources.Values));
			sb.Append(" /Contents ").Append(PdfText.Reference(_contentNumber));

			if (this.Rotation != 0)
				sb.Append(" /Rotate ").Append(this.Rotation.ToString(CultureInfo.InvariantCulture));

			if (_annotations.Count > 0)
			{
				sb.Append(" /Annots [");
				foreach (var annotation in _annotations)
					sb.Append(PdfText.Reference(annotation)).Append(' ');

				sb.Length--;
				sb.Append(']');
			}

			foreach (var entry in _entries)
				sb.Append(' ').Append(entry);

			_owner.SetObject(this.Number, sb.Append(" >>").ToString());
		}

		#endregion
	}
}
