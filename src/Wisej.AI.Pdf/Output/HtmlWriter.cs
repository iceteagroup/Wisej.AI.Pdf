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

// Semantic HTML output.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// Writes <see cref="PdfContent"/> as a semantic HTML document.
	/// </summary>
	internal sealed class HtmlWriter
	{
		// true to embed the images as data URIs.
		private readonly bool _embedImages;
		// the output.
		private readonly StringBuilder _sb = new StringBuilder();

		/// <summary>
		/// Initializes a new instance of <see cref="HtmlWriter"/>.
		/// </summary>
		/// <param name="embedImages">True to embed the images as data URIs.</param>
		public HtmlWriter(bool embedImages)
		{
			_embedImages = embedImages;
		}

		#region Methods

		/// <summary>
		/// Writes the content as a complete HTML document.
		/// </summary>
		/// <param name="content">The content.</param>
		/// <returns>The HTML text.</returns>
		public string Write(PdfContent content)
		{
			var title = content.Info?.Title;
			_sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
			if (!string.IsNullOrEmpty(title))
				_sb.Append("<title>").Append(Encode(title)).Append("</title>\n");
			_sb.Append("</head>\n<body>\n");

			foreach (var page in content.Pages)
			{
				_sb.Append("<section data-page=\"").Append(page.Number.ToString(CultureInfo.InvariantCulture)).Append("\">\n");
				foreach (var block in page.Blocks)
					WriteBlock(block);

				_sb.Append("</section>\n");
			}

			_sb.Append("</body>\n</html>\n");
			return _sb.ToString();
		}

		/// <summary>
		/// Writes a table as an HTML element with row and column spans.
		/// </summary>
		/// <param name="table">The table.</param>
		/// <returns>The HTML text.</returns>
		public static string WriteTable(PdfTable table)
		{
			var sb = new StringBuilder();
			sb.Append("<table>\n");

			for (int r = 0; r < table.RowCount; r++)
			{
				if (r == 0 && table.HeaderRowCount > 0)
					sb.Append("<thead>\n");
				if (r == table.HeaderRowCount && table.HeaderRowCount > 0)
					sb.Append("<tbody>\n");

				sb.Append("<tr>");
				for (int c = 0; c < table.ColumnCount; c++)
				{
					var cell = table[r, c];

					// positions covered by a spanning cell are not written again.
					if (cell != null && (cell.Row != r || cell.Column != c))
						continue;

					var tag = cell != null && cell.IsHeader ? "th" : "td";
					sb.Append('<').Append(tag);
					if (cell != null && cell.RowSpan > 1)
						sb.Append(" rowspan=\"").Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
					if (cell != null && cell.ColumnSpan > 1)
						sb.Append(" colspan=\"").Append(cell.ColumnSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
					sb.Append('>');

					if (cell != null)
						sb.Append(FormatRuns(cell.Runs).Trim());

					sb.Append("</").Append(tag).Append('>');
				}

				sb.Append("</tr>\n");

				if (r == table.HeaderRowCount - 1)
					sb.Append("</thead>\n");
			}

			if (table.HeaderRowCount > 0 && table.HeaderRowCount < table.RowCount)
				sb.Append("</tbody>\n");

			sb.Append("</table>\n");
			return sb.ToString();
		}

		#endregion

		#region Blocks

		/// <summary>
		/// Writes a block of any type.
		/// </summary>
		/// <param name="block">The block.</param>
		private void WriteBlock(PdfBlock block)
		{
			switch (block)
			{
				case PdfTextBlock text:
					WriteText(text);
					break;

				case PdfList list:
					WriteList(list);
					break;

				case PdfTable table:
					if (table.Caption != null)
						_sb.Append("<p class=\"caption\">").Append(Encode(table.Caption)).Append("</p>\n");
					_sb.Append(WriteTable(table));
					break;

				case PdfFigure figure:
					WriteFigure(figure);
					break;
			}
		}

		/// <summary>
		/// Writes a text block with the element matching its type.
		/// </summary>
		/// <param name="block">The block.</param>
		private void WriteText(PdfTextBlock block)
		{
			// headings are bold already: their runs are written without <strong>.
			var html = FormatRuns(block.Runs, inHeading: block.Type == PdfBlockType.Heading).Trim();
			switch (block.Type)
			{
				case PdfBlockType.Heading:
					var level = Math.Max(1, Math.Min(6, block.Level)).ToString(CultureInfo.InvariantCulture);
					_sb.Append("<h").Append(level).Append('>').Append(html).Append("</h").Append(level).Append(">\n");
					break;

				case PdfBlockType.Code:
					_sb.Append("<pre><code>").Append(Encode(block.Text)).Append("</code></pre>\n");
					break;

				case PdfBlockType.Caption:
					_sb.Append("<p class=\"caption\">").Append(html).Append("</p>\n");
					break;

				case PdfBlockType.PageHeader:
					_sb.Append("<header>").Append(html).Append("</header>\n");
					break;

				case PdfBlockType.PageFooter:
					_sb.Append("<footer>").Append(html).Append("</footer>\n");
					break;

				default:
					_sb.Append("<p>").Append(html).Append("</p>\n");
					break;
			}
		}

		/// <summary>
		/// Writes a list, nesting ul/ol elements by depth.
		/// </summary>
		/// <param name="list">The list.</param>
		private void WriteList(PdfList list)
		{
			// nested lists are opened and closed as the depth changes.
			var tag = list.IsOrdered ? "ol" : "ul";
			var depth = -1;
			foreach (var item in list.Items)
			{
				while (depth < item.Depth)
				{
					_sb.Append('<').Append(tag).Append(">\n");
					depth++;
				}

				while (depth > item.Depth)
				{
					_sb.Append("</").Append(tag).Append(">\n");
					depth--;
				}

				_sb.Append("<li>").Append(FormatRuns(item.Runs).Trim()).Append("</li>\n");
			}

			while (depth-- >= 0)
				_sb.Append("</").Append(tag).Append(">\n");
		}

		/// <summary>
		/// Writes a figure element with its image and caption.
		/// </summary>
		/// <param name="figure">The figure.</param>
		private void WriteFigure(PdfFigure figure)
		{
			_sb.Append("<figure>\n");
			if (figure.Image != null)
			{
				var source = _embedImages ? figure.Image.ToDataUri() : figure.Image.FileName;
				_sb.Append("<img src=\"").Append(Encode(source)).Append("\" alt=\"").Append(Encode(figure.AltText ?? figure.Caption ?? string.Empty))
					.Append("\" width=\"").Append(figure.Image.Width.ToString(CultureInfo.InvariantCulture))
					.Append("\" height=\"").Append(figure.Image.Height.ToString(CultureInfo.InvariantCulture)).Append("\">\n");
			}

			if (figure.Caption != null)
				_sb.Append("<figcaption>").Append(Encode(figure.Caption)).Append("</figcaption>\n");

			_sb.Append("</figure>\n");
		}

		#endregion

		#region Inline formatting

		/// <summary>
		/// Formats runs as inline HTML.
		/// </summary>
		/// <param name="runs">The runs.</param>
		/// <param name="inHeading">True inside headings, which are bold already.</param>
		/// <returns>The HTML text.</returns>
		private static string FormatRuns(IReadOnlyList<PdfTextRun> runs, bool inHeading = false)
		{
			var sb = new StringBuilder();
			foreach (var run in runs)
			{
				var html = Encode(run.Text);
				if (run.IsMonospace)
					html = "<code>" + html + "</code>";
				if (run.IsSuperscript)
					html = "<sup>" + html + "</sup>";
				if (run.IsSubscript)
					html = "<sub>" + html + "</sub>";
				if (run.IsItalic)
					html = "<em>" + html + "</em>";
				if (run.IsBold && !inHeading)
					html = "<strong>" + html + "</strong>";
				if (run.LinkUri != null && Uri.IsWellFormedUriString(run.LinkUri, UriKind.Absolute))
					html = "<a href=\"" + Encode(run.LinkUri) + "\">" + html + "</a>";

				sb.Append(html);
			}

			return sb.ToString();
		}

		// HTML-encodes text; null becomes empty.
		private static string Encode(string text) => WebUtility.HtmlEncode(text ?? string.Empty);

		#endregion
	}
}
