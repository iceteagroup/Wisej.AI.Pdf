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

// Markdown output (GitHub-flavored), designed for readability and for
// large language models: clean paragraphs, real headings, lists and tables.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// How images are written in Markdown.
	/// </summary>
	public enum PdfMarkdownImageMode
	{
		/// <summary>Reference the image by file name: <c>![caption](images/image-1.png)</c>. The caller saves the files.</summary>
		Reference,

		/// <summary>Embed the image as a data URI.</summary>
		Embed,

		/// <summary>Write only a textual placeholder with the caption and alternate text.</summary>
		Placeholder,

		/// <summary>Omit figures.</summary>
		Omit,
	}

	/// <summary>
	/// How tables are written in Markdown.
	/// </summary>
	public enum PdfMarkdownTableMode
	{
		/// <summary>GitHub-flavored pipe tables, or HTML when cells span rows or columns.</summary>
		Auto,

		/// <summary>Always GitHub-flavored pipe tables (spanning cells are written in their first position).</summary>
		Pipe,

		/// <summary>Always HTML tables.</summary>
		Html,
	}

	/// <summary>
	/// Options of the Markdown output.
	/// </summary>
	public sealed class PdfMarkdownOptions
	{
		/// <summary>
		/// How images are written. Default: <see cref="PdfMarkdownImageMode.Reference"/>.
		/// </summary>
		public PdfMarkdownImageMode ImageMode { get; set; } = PdfMarkdownImageMode.Reference;

		/// <summary>
		/// The folder prefix used for image references, for example "images/". Default: "images/".
		/// </summary>
		public string ImageFolder { get; set; } = "images/";

		/// <summary>
		/// Optional function that returns the URL of an image; overrides <see cref="ImageFolder"/>.
		/// </summary>
		public Func<PdfImage, string> ImageUrl { get; set; }

		/// <summary>
		/// How tables are written. Default: <see cref="PdfMarkdownTableMode.Auto"/>.
		/// </summary>
		public PdfMarkdownTableMode TableMode { get; set; } = PdfMarkdownTableMode.Auto;

		/// <summary>
		/// Write bold, italic, code, superscript and subscript formatting. Default: true.
		/// </summary>
		public bool IncludeFormatting { get; set; } = true;

		/// <summary>
		/// Write hyperlinks. Default: true.
		/// </summary>
		public bool IncludeLinks { get; set; } = true;

		/// <summary>
		/// Write running headers, footers and page numbers. Default: false.
		/// </summary>
		public bool IncludePageHeadersAndFooters { get; set; }

		/// <summary>
		/// Write a page marker comment (<c>&lt;!-- page 3 --&gt;</c>) at the start of each page; useful to cite sources in RAG pipelines. Default: false.
		/// </summary>
		public bool IncludePageMarkers { get; set; }

		/// <summary>
		/// Write the text found inside figures (chart labels, legends) below the figure. Default: false.
		/// </summary>
		public bool IncludeFigureLabels { get; set; }
	}

	/// <summary>
	/// Writes <see cref="PdfContent"/> as Markdown.
	/// </summary>
	internal sealed class MarkdownWriter
	{
		// the Markdown options.
		private readonly PdfMarkdownOptions _options;
		// the output.
		private readonly StringBuilder _sb = new StringBuilder();

		/// <summary>
		/// Initializes a new instance of <see cref="MarkdownWriter"/>.
		/// </summary>
		/// <param name="options">The options.</param>
		public MarkdownWriter(PdfMarkdownOptions options)
		{
			_options = options;
		}

		#region Methods

		/// <summary>
		/// Writes the content.
		/// </summary>
		/// <param name="content">The content to write.</param>
		/// <returns>The Markdown text.</returns>
		public string Write(PdfContent content)
		{
			foreach (var page in content.Pages)
			{
				if (_options.IncludePageMarkers)
					AppendBlock($"<!-- page {page.Number.ToString(CultureInfo.InvariantCulture)} -->");

				foreach (var block in page.Blocks)
					WriteBlock(block);
			}

			return _sb.ToString().TrimEnd() + "\n";
		}

		/// <summary>
		/// Writes a single page.
		/// </summary>
		/// <param name="page">The page to write.</param>
		/// <returns>The Markdown text.</returns>
		public string Write(PdfContentPage page)
		{
			foreach (var block in page.Blocks)
				WriteBlock(block);

			return _sb.ToString().TrimEnd() + "\n";
		}

		/// <summary>
		/// Writes a table in Markdown.
		/// </summary>
		/// <param name="table">The table.</param>
		/// <param name="options">The options.</param>
		/// <returns>The Markdown text of the table.</returns>
		public static string WriteTable(PdfTable table, PdfMarkdownOptions options)
		{
			var writer = new MarkdownWriter(options);
			writer.WriteTable(table);
			return writer._sb.ToString().TrimEnd() + "\n";
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
					WriteTable(table);
					break;

				case PdfFigure figure:
					WriteFigure(figure);
					break;
			}
		}

		/// <summary>
		/// Writes a text block: heading, paragraph, caption, code or header/footer.
		/// </summary>
		/// <param name="block">The block.</param>
		private void WriteText(PdfTextBlock block)
		{
			switch (block.Type)
			{
				case PdfBlockType.Heading:
					AppendBlock(new string('#', Math.Max(1, Math.Min(6, block.Level))) + " " + FormatRuns(block.Runs, inHeading: true).Trim());
					break;

				case PdfBlockType.Code:
					AppendBlock("```\n" + block.Text.TrimEnd() + "\n```");
					break;

				case PdfBlockType.Caption:
					AppendBlock("*" + EscapeInline(block.Text.Trim()) + "*");
					break;

				case PdfBlockType.PageHeader:
				case PdfBlockType.PageFooter:
					if (_options.IncludePageHeadersAndFooters)
						AppendBlock(FormatRuns(block.Runs).Trim());
					break;

				default:
					var text = FormatRuns(block.Runs).Trim();
					if (text.Length > 0)
						AppendBlock(EscapeLineStart(text));
					break;
			}
		}

		/// <summary>
		/// Writes a list with nested items indented.
		/// </summary>
		/// <param name="list">The list.</param>
		private void WriteList(PdfList list)
		{
			// Markdown renumbers ordered lists: markers like "(a)", "iv." or "(54)" are kept verbatim in a bullet list.
			var sequential = list.IsOrdered && HasSequentialNumbers(list);
			var sb = new StringBuilder();
			foreach (var item in list.Items)
			{
				var indent = new string(' ', item.Depth * (sequential ? 3 : 2));
				string marker;
				if (sequential)
					marker = item.Marker.TrimEnd('.', ')') + ".";
				else if (list.IsOrdered)
					marker = "- " + EscapeInline(item.Marker);
				else
					marker = "-";

				sb.Append(indent).Append(marker).Append(' ').Append(FormatRuns(item.Runs).Trim()).Append('\n');
			}

			AppendBlock(sb.ToString().TrimEnd());
		}

		// true when the top-level markers are consecutive arabic numbers ("3." "4." "5.").
		private static bool HasSequentialNumbers(PdfList list)
		{
			var expected = -1;
			foreach (var item in list.Items)
			{
				if (item.Depth != 0)
					continue;

				var text = item.Marker.TrimEnd('.', ')');
				if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || item.Marker.StartsWith("(", StringComparison.Ordinal))
					return false;

				if (expected >= 0 && number != expected)
					return false;

				expected = number + 1;
			}

			return expected >= 0;
		}

		/// <summary>
		/// Writes a table as a pipe table, or as HTML when cells span rows or columns.
		/// </summary>
		/// <param name="table">The table.</param>
		private void WriteTable(PdfTable table)
		{
			var useHtml = _options.TableMode == PdfMarkdownTableMode.Html
				|| (_options.TableMode == PdfMarkdownTableMode.Auto && table.HasSpans);

			if (table.Caption != null)
				AppendBlock("*" + EscapeInline(table.Caption.Trim()) + "*");

			if (useHtml)
			{
				AppendBlock(HtmlWriter.WriteTable(table).TrimEnd());
				return;
			}

			var rows = table.ToArray();
			if (rows.Length == 0)
				return;

			var sb = new StringBuilder();
			for (int r = 0; r < rows.Length; r++)
			{
				sb.Append('|');
				for (int c = 0; c < rows[r].Length; c++)
				{
					var cell = table[r, c];
					var text = cell != null && cell.Row == r && cell.Column == c ? FormatRuns(cell.Runs).Trim() : string.Empty;
					sb.Append(' ').Append(EscapeTableCell(text)).Append(" |");
				}

				sb.Append('\n');

				// the first row is the header row of a pipe table.
				if (r == 0)
				{
					sb.Append('|');
					for (int c = 0; c < rows[r].Length; c++)
						sb.Append(" --- |");

					sb.Append('\n');
				}
			}

			AppendBlock(sb.ToString().TrimEnd());
		}

		/// <summary>
		/// Writes a figure according to <see cref="PdfMarkdownOptions.ImageMode"/>.
		/// </summary>
		/// <param name="figure">The figure.</param>
		private void WriteFigure(PdfFigure figure)
		{
			var label = (figure.Caption ?? figure.AltText ?? string.Empty).Trim();

			switch (_options.ImageMode)
			{
				case PdfMarkdownImageMode.Omit:
					return;

				case PdfMarkdownImageMode.Placeholder:
					AppendBlock("[Figure" + (label.Length > 0 ? ": " + EscapeInline(label) : string.Empty) + "]");
					break;

				default:
					if (figure.Image == null)
					{
						if (label.Length > 0)
							AppendBlock("[Figure: " + EscapeInline(label) + "]");

						// without an image, the text of the figure is the only content left: never drop it.
						if (!_options.IncludeFigureLabels && !string.IsNullOrWhiteSpace(figure.Labels))
							AppendBlock(EscapeInline(figure.Labels.Trim()).Replace("\n", "  \n"));
						break;
					}

					var url = _options.ImageMode == PdfMarkdownImageMode.Embed
						? figure.Image.ToDataUri()
						: _options.ImageUrl?.Invoke(figure.Image) ?? (_options.ImageFolder ?? string.Empty) + figure.Image.FileName;

					var alt = figure.AltText ?? figure.Caption ?? figure.Image.Id;
					AppendBlock("![" + EscapeAlt(alt) + "](" + url.Replace(" ", "%20") + ")");

					if (figure.Caption != null)
						AppendBlock("*" + EscapeInline(figure.Caption.Trim()) + "*");
					break;
			}

			if (_options.IncludeFigureLabels && !string.IsNullOrWhiteSpace(figure.Labels))
				AppendBlock("> " + EscapeInline(figure.Labels.Trim()).Replace("\n", "\n> "));
		}

		#endregion

		#region Inline formatting

		/// <summary>
		/// Formats runs with Markdown emphasis, code, scripts and links.
		/// </summary>
		/// <param name="runs">The runs.</param>
		/// <param name="inHeading">True inside headings, which are bold already.</param>
		/// <returns>The Markdown text.</returns>
		private string FormatRuns(IReadOnlyList<PdfTextRun> runs, bool inHeading = false)
		{
			var sb = new StringBuilder();
			var formatting = _options.IncludeFormatting;

			// consecutive runs with the same emphasis are wrapped once, so markers never collide ("*a**b*").
			for (int i = 0; i < runs.Count;)
			{
				var run = runs[i];
				var bold = formatting && run.IsBold && !inHeading && !run.IsMonospace && !run.IsSuperscript && !run.IsSubscript;
				var italic = formatting && run.IsItalic && !run.IsMonospace && !run.IsSuperscript && !run.IsSubscript;

				var j = i;
				var inner = new StringBuilder();
				while (j < runs.Count)
				{
					var next = runs[j];
					var nextBold = formatting && next.IsBold && !inHeading && !next.IsMonospace && !next.IsSuperscript && !next.IsSubscript;
					var nextItalic = formatting && next.IsItalic && !next.IsMonospace && !next.IsSuperscript && !next.IsSubscript;
					if (j > i && (nextBold != bold || nextItalic != italic))
						break;

					inner.Append(FormatInner(next, inHeading));
					j++;
				}

				var text = inner.ToString();
				var core = text.Trim();
				if (core.Length == 0 || (!bold && !italic))
				{
					sb.Append(text);
				}
				else
				{
					// emphasis markers must wrap the text without surrounding spaces.
					var leading = text.Length - text.TrimStart().Length;
					var trailing = text.Length - text.TrimEnd().Length;
					var marker = bold && italic ? "***" : bold ? "**" : "*";
					sb.Append(text, 0, leading).Append(marker).Append(core).Append(marker).Append(text, text.Length - trailing, trailing);
				}

				i = j;
			}

			return sb.ToString();
		}

		// formats a run without emphasis: escaping, code, scripts and links.
		private string FormatInner(PdfTextRun run, bool inHeading)
		{
			var text = run.Text;
			var core = text.Trim();
			if (core.Length == 0)
				return text;

			var leading = text.Length - text.TrimStart().Length;
			var trailing = text.Length - text.TrimEnd().Length;

			string formatted;
			if (_options.IncludeFormatting && run.IsMonospace && !inHeading)
			{
				formatted = "`" + core.Replace("`", "\\`") + "`";
			}
			else
			{
				formatted = EscapeInline(core);
				if (_options.IncludeFormatting)
				{
					if (run.IsSuperscript)
						formatted = "<sup>" + formatted + "</sup>";
					else if (run.IsSubscript)
						formatted = "<sub>" + formatted + "</sub>";
				}
			}

			if (_options.IncludeLinks && run.LinkUri != null && IsSafeUri(run.LinkUri))
				formatted = "[" + formatted + "](" + run.LinkUri.Replace(" ", "%20").Replace(")", "%29") + ")";

			return text.Substring(0, leading) + formatted + text.Substring(text.Length - trailing);
		}

		// only web and mail links are written; other schemes (javascript:, file:) are dropped.
		private static bool IsSafeUri(
string uri)
		{
			return uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				|| uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
				|| uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
				|| uri.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Escapes the characters that Markdown would interpret inside running text.
		/// </summary>
		/// <param name="text">The text.</param>
		/// <returns>The escaped text.</returns>
		public static string EscapeInline(string text)
		{
			var sb = new StringBuilder(text.Length + 8);
			for (int i = 0; i < text.Length; i++)
			{
				var c = text[i];
				switch (c)
				{
					case '\\':
					case '`':
					case '*':
					case '_':
					case '[':
					case ']':
						sb.Append('\\').Append(c);
						break;

					case '<':
						sb.Append("&lt;");
						break;

					case '>':
						sb.Append("&gt;");
						break;

					default:
						sb.Append(c);
						break;
				}
			}

			return sb.ToString();
		}

		// characters that start block elements when at the beginning of a line.
		private static string EscapeLineStart(string text)
		{
			if (text.Length == 0)
				return text;

			var c = text[0];
			if (c == '#' || c == '>' || c == '+' || c == '=' || (c == '-' && (text.Length == 1 || text[1] == ' ')))
				return "\\" + text;

			// "1. text" would become an ordered list.
			var i = 0;
			while (i < text.Length && char.IsDigit(text[i]))
				i++;
			if (i > 0 && i < text.Length - 1 && (text[i] == '.' || text[i] == ')') && text[i + 1] == ' ')
				return text.Substring(0, i) + "\\" + text.Substring(i);

			return text;
		}

		// escapes the pipe and turns line breaks into <br> inside pipe table cells.
		private static string EscapeTableCell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", "<br>");

		// makes text safe inside the brackets of an image alternate text.
		private static string EscapeAlt(string text) => text.Replace("[", "(").Replace("]", ")").Replace("\n", " ").Trim();

		// appends a block separated from the previous one by a blank line.
		private void AppendBlock(string text)
		{
			if (string.IsNullOrEmpty(text))
				return;

			if (_sb.Length > 0)
				_sb.Append("\n\n");

			_sb.Append(text);
		}

		#endregion
	}
}
