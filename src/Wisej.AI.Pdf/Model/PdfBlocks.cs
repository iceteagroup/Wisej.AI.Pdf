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

// Public content model: blocks of text, lists, tables and figures.

using System;
using System.Collections.Generic;
using System.Text;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// The type of a <see cref="PdfBlock"/>.
	/// </summary>
	public enum PdfBlockType
	{
		/// <summary>A paragraph of text (<see cref="PdfTextBlock"/>).</summary>
		Paragraph,

		/// <summary>A heading (<see cref="PdfTextBlock"/> with a <see cref="PdfTextBlock.Level"/>).</summary>
		Heading,

		/// <summary>A list (<see cref="PdfList"/>).</summary>
		List,

		/// <summary>A table (<see cref="PdfTable"/>).</summary>
		Table,

		/// <summary>A figure: an image or a vector drawing (<see cref="PdfFigure"/>).</summary>
		Figure,

		/// <summary>A caption that is not attached to a figure or table (<see cref="PdfTextBlock"/>).</summary>
		Caption,

		/// <summary>Preformatted text such as source code (<see cref="PdfTextBlock"/>).</summary>
		Code,

		/// <summary>A running header repeated across pages (<see cref="PdfTextBlock"/>).</summary>
		PageHeader,

		/// <summary>A running footer or page number (<see cref="PdfTextBlock"/>).</summary>
		PageFooter,
	}

	/// <summary>
	/// A block of content: the unit of the reading order.
	/// </summary>
	public abstract class PdfBlock
	{
		/// <summary>
		/// Initializes the common properties of a block.
		/// </summary>
		/// <param name="type">The type of the block.</param>
		/// <param name="pageNumber">The 1-based page number.</param>
		/// <param name="bounds">The bounds on the page.</param>
		internal PdfBlock(PdfBlockType type, int pageNumber, PdfRect bounds)
		{
			this.Type = type;
			this.PageNumber = pageNumber;
			this.Bounds = bounds;
		}

		/// <summary>
		/// The type of the block.
		/// </summary>
		public PdfBlockType Type { get; }

		/// <summary>
		/// The 1-based number of the page where the block starts.
		/// </summary>
		public int PageNumber { get; }

		/// <summary>
		/// The bounds of the block on the page, in points from the top-left corner.
		/// </summary>
		public PdfRect Bounds { get; }

		/// <summary>
		/// The plain text of the block.
		/// </summary>
		public abstract string Text { get; }

		/// <inheritdoc/>
		public override string ToString() => $"{this.Type} (p. {this.PageNumber}): {this.Text}";
	}

	/// <summary>
	/// A span of text with uniform formatting.
	/// </summary>
	public sealed class PdfTextRun
	{
		/// <summary>
		/// Initializes an empty run; the layout engine sets the properties.
		/// </summary>
		internal PdfTextRun()
		{
		}

		/// <summary>The text.</summary>
		public string Text { get; internal set; }

		/// <summary>True when the text is bold.</summary>
		public bool IsBold { get; internal set; }

		/// <summary>True when the text is italic.</summary>
		public bool IsItalic { get; internal set; }

		/// <summary>True when the text uses a monospaced font.</summary>
		public bool IsMonospace { get; internal set; }

		/// <summary>True when the text is a superscript.</summary>
		public bool IsSuperscript { get; internal set; }

		/// <summary>True when the text is a subscript.</summary>
		public bool IsSubscript { get; internal set; }

		/// <summary>The target of a hyperlink on the text, or null.</summary>
		public string LinkUri { get; internal set; }

		/// <summary>The target page of an internal link on the text, or 0.</summary>
		public int LinkPage { get; internal set; }

		/// <summary>The PostScript name of the font.</summary>
		public string FontName { get; internal set; }

		/// <summary>The font size in points.</summary>
		public double FontSize { get; internal set; }

		/// <summary>The text colour as 0xRRGGBB.</summary>
		public int Color { get; internal set; }

		/// <inheritdoc/>
		public override string ToString() => this.Text;
	}

	/// <summary>
	/// A block of formatted text: paragraph, heading, caption, code or running header/footer.
	/// </summary>
	public sealed class PdfTextBlock : PdfBlock
	{
		/// <summary>The concatenated text of the runs, built on first access.</summary>
		private string _text;

		/// <summary>
		/// Initializes a text block.
		/// </summary>
		/// <param name="type">The type of the block: paragraph, heading, caption, code, page header or page footer.</param>
		/// <param name="pageNumber">The 1-based page number.</param>
		/// <param name="bounds">The bounds on the page, in points from the top-left corner.</param>
		/// <param name="runs">The formatted runs of text.</param>
		/// <param name="level">The heading level, 1 to 6; 0 for blocks that are not headings.</param>
		internal PdfTextBlock(PdfBlockType type, int pageNumber, PdfRect bounds, IReadOnlyList<PdfTextRun> runs, int level)
			: base(type, pageNumber, bounds)
		{
			this.Runs = runs;
			this.Level = level;
		}

		/// <summary>
		/// The formatted runs of text.
		/// </summary>
		public IReadOnlyList<PdfTextRun> Runs { get; }

		/// <summary>
		/// The heading level, from 1 (top) to 6; 0 for blocks that are not headings.
		/// </summary>
		public int Level { get; }

		/// <summary>
		/// The font size of the text.
		/// </summary>
		public double FontSize => this.Runs.Count > 0 ? this.Runs[0].FontSize : 0;

		/// <inheritdoc/>
		public override string Text => _text ?? (_text = PdfContentHelpers.Concat(this.Runs));
	}

	/// <summary>
	/// An item of a <see cref="PdfList"/>.
	/// </summary>
	public sealed class PdfListItem
	{
		/// <summary>The concatenated text of the runs, built on first access.</summary>
		private string _text;

		/// <summary>
		/// Initializes a list item.
		/// </summary>
		/// <param name="marker">The marker as printed, for example "•" or "1.".</param>
		/// <param name="depth">The nesting depth: 0 for top-level items.</param>
		/// <param name="runs">The formatted runs of the item text, without the marker.</param>
		internal PdfListItem(string marker, int depth, IReadOnlyList<PdfTextRun> runs)
		{
			this.Marker = marker;
			this.Depth = depth;
			this.Runs = runs;
		}

		/// <summary>
		/// The marker of the item as printed: "•", "1.", "(a)", etc.
		/// </summary>
		public string Marker { get; }

		/// <summary>
		/// The nesting depth: 0 for top-level items.
		/// </summary>
		public int Depth { get; }

		/// <summary>
		/// The formatted runs of the item text, without the marker.
		/// </summary>
		public IReadOnlyList<PdfTextRun> Runs { get; }

		/// <summary>
		/// The text of the item, without the marker.
		/// </summary>
		public string Text => _text ?? (_text = PdfContentHelpers.Concat(this.Runs));

		/// <inheritdoc/>
		public override string ToString() => $"{this.Marker} {this.Text}";
	}

	/// <summary>
	/// A bulleted or numbered list.
	/// </summary>
	public sealed class PdfList : PdfBlock
	{
		/// <summary>
		/// Initializes a list.
		/// </summary>
		/// <param name="pageNumber">The 1-based page number.</param>
		/// <param name="bounds">The bounds on the page, in points from the top-left corner.</param>
		/// <param name="isOrdered">True when the items are numbered or lettered.</param>
		/// <param name="items">The items of the list.</param>
		internal PdfList(int pageNumber, PdfRect bounds, bool isOrdered, IReadOnlyList<PdfListItem> items)
			: base(PdfBlockType.List, pageNumber, bounds)
		{
			this.IsOrdered = isOrdered;
			this.Items = items;
		}

		/// <summary>
		/// True when the items are numbered or lettered.
		/// </summary>
		public bool IsOrdered { get; }

		/// <summary>
		/// The items of the list.
		/// </summary>
		public IReadOnlyList<PdfListItem> Items { get; }

		/// <inheritdoc/>
		public override string Text
		{
			get
			{
				var sb = new StringBuilder();
				foreach (var item in this.Items)
					sb.Append(new string(' ', item.Depth * 2)).Append(item.Marker).Append(' ').AppendLine(item.Text);

				return sb.ToString().TrimEnd();
			}
		}
	}

	/// <summary>
	/// A cell of a <see cref="PdfTable"/>.
	/// </summary>
	public sealed class PdfTableCell
	{
		/// <summary>The trimmed text of the runs, built on first access.</summary>
		private string _text;

		/// <summary>
		/// Initializes a table cell.
		/// </summary>
		/// <param name="row">The zero-based row index.</param>
		/// <param name="column">The zero-based column index.</param>
		/// <param name="rowSpan">The number of rows spanned by the cell.</param>
		/// <param name="columnSpan">The number of columns spanned by the cell.</param>
		/// <param name="isHeader">True when the cell belongs to a header row.</param>
		/// <param name="bounds">The bounds of the cell on the page.</param>
		/// <param name="runs">The formatted runs of text.</param>
		internal PdfTableCell(int row, int column, int rowSpan, int columnSpan, bool isHeader, PdfRect bounds, IReadOnlyList<PdfTextRun> runs)
		{
			this.Row = row;
			this.Column = column;
			this.RowSpan = rowSpan;
			this.ColumnSpan = columnSpan;
			this.IsHeader = isHeader;
			this.Bounds = bounds;
			this.Runs = runs;
		}

		/// <summary>The zero-based row index.</summary>
		public int Row { get; }

		/// <summary>The zero-based column index.</summary>
		public int Column { get; }

		/// <summary>The number of rows spanned by the cell.</summary>
		public int RowSpan { get; }

		/// <summary>The number of columns spanned by the cell.</summary>
		public int ColumnSpan { get; }

		/// <summary>True when the cell belongs to a header row.</summary>
		public bool IsHeader { get; }

		/// <summary>The bounds of the cell on the page.</summary>
		public PdfRect Bounds { get; }

		/// <summary>The formatted runs of text.</summary>
		public IReadOnlyList<PdfTextRun> Runs { get; }

		/// <summary>The text of the cell.</summary>
		public string Text => _text ?? (_text = PdfContentHelpers.Concat(this.Runs).Trim());

		/// <inheritdoc/>
		public override string ToString() => this.Text;
	}

	/// <summary>
	/// A table.
	/// </summary>
	/// <remarks>
	/// Cells that span several rows or columns appear once in <see cref="Cells"/>; the indexer
	/// <see cref="this[int, int]"/> returns the spanning cell for every position it covers.
	/// </remarks>
	public sealed class PdfTable : PdfBlock
	{
		/// <summary>The cell covering each [row, column] position, or null for empty positions.</summary>
		private readonly PdfTableCell[,] _grid;

		/// <summary>
		/// Initializes a table and builds the position grid from the cells and their spans.
		/// </summary>
		/// <param name="pageNumber">The 1-based number of the page where the table starts.</param>
		/// <param name="bounds">The bounds on the first page, in points from the top-left corner.</param>
		/// <param name="rowCount">The number of rows.</param>
		/// <param name="columnCount">The number of columns.</param>
		/// <param name="headerRowCount">The number of header rows at the top of the table.</param>
		/// <param name="isRuled">True when the table is drawn with ruling lines.</param>
		/// <param name="cells">The cells, in row order; spans that exceed the table are clipped.</param>
		/// <param name="caption">The caption, or null.</param>
		/// <param name="pageNumbers">The pages the table spans.</param>
		internal PdfTable(int pageNumber, PdfRect bounds, int rowCount, int columnCount, int headerRowCount, bool isRuled, IReadOnlyList<PdfTableCell> cells, string caption, IReadOnlyList<int> pageNumbers)
			: base(PdfBlockType.Table, pageNumber, bounds)
		{
			this.RowCount = rowCount;
			this.ColumnCount = columnCount;
			this.HeaderRowCount = headerRowCount;
			this.IsRuled = isRuled;
			this.Cells = cells;
			this.Caption = caption;
			this.PageNumbers = pageNumbers;

			_grid = new PdfTableCell[rowCount, columnCount];
			foreach (var cell in cells)
			{
				for (int r = cell.Row; r < cell.Row + cell.RowSpan && r < rowCount; r++)
				{
					for (int c = cell.Column; c < cell.Column + cell.ColumnSpan && c < columnCount; c++)
						_grid[r, c] = cell;
				}
			}
		}

		/// <summary>The number of rows.</summary>
		public int RowCount { get; }

		/// <summary>The number of columns.</summary>
		public int ColumnCount { get; }

		/// <summary>The number of header rows at the top of the table.</summary>
		public int HeaderRowCount { get; }

		/// <summary>True when the table is drawn with ruling lines; false for tables recognized from text alignment.</summary>
		public bool IsRuled { get; }

		/// <summary>The cells of the table, in row order.</summary>
		public IReadOnlyList<PdfTableCell> Cells { get; }

		/// <summary>The caption of the table, or null.</summary>
		public string Caption { get; }

		/// <summary>The pages the table spans (more than one when it continues on following pages).</summary>
		public IReadOnlyList<int> PageNumbers { get; }

		/// <summary>
		/// True when some cell spans more than one row or column.
		/// </summary>
		public bool HasSpans
		{
			get
			{
				foreach (var cell in this.Cells)
				{
					if (cell.RowSpan > 1 || cell.ColumnSpan > 1)
						return true;
				}

				return false;
			}
		}

		/// <summary>
		/// Returns the cell at the given position, or null when the position is empty.
		/// </summary>
		/// <param name="row">The zero-based row index.</param>
		/// <param name="column">The zero-based column index.</param>
		/// <returns>The cell covering the position.</returns>
		public PdfTableCell this[int row, int column] => row >= 0 && row < this.RowCount && column >= 0 && column < this.ColumnCount ? _grid[row, column] : null;

		/// <summary>
		/// Returns the text of the table as rows of strings; spanning cells repeat their text only in the first position.
		/// </summary>
		/// <returns>An array of rows, each with <see cref="ColumnCount"/> strings.</returns>
		public string[][] ToArray()
		{
			var rows = new string[this.RowCount][];
			for (int r = 0; r < this.RowCount; r++)
			{
				rows[r] = new string[this.ColumnCount];
				for (int c = 0; c < this.ColumnCount; c++)
				{
					var cell = _grid[r, c];
					rows[r][c] = cell != null && cell.Row == r && cell.Column == c ? cell.Text : string.Empty;
				}
			}

			return rows;
		}

		/// <summary>
		/// Returns the table as CSV (RFC 4180).
		/// </summary>
		/// <returns>The CSV text.</returns>
		public string ToCsv()
		{
			var sb = new StringBuilder();
			foreach (var row in ToArray())
			{
				for (int c = 0; c < row.Length; c++)
				{
					if (c > 0)
						sb.Append(',');

					var value = row[c];
					if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
						sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
					else
						sb.Append(value);
				}

				sb.Append("\r\n");
			}

			return sb.ToString();
		}

		/// <summary>
		/// Returns the table in GitHub-flavored Markdown.
		/// </summary>
		/// <returns>The Markdown text.</returns>
		public string ToMarkdown() => MarkdownWriter.WriteTable(this, new PdfMarkdownOptions());

		/// <summary>
		/// Returns the table as an HTML <c>&lt;table&gt;</c> element, with row and column spans.
		/// </summary>
		/// <returns>The HTML text.</returns>
		public string ToHtml() => HtmlWriter.WriteTable(this);

		/// <inheritdoc/>
		public override string Text
		{
			get
			{
				var sb = new StringBuilder();
				foreach (var row in ToArray())
					sb.AppendLine(string.Join("\t", row));

				return sb.ToString().TrimEnd();
			}
		}
	}

	/// <summary>
	/// A figure: a picture or a vector drawing, with its caption.
	/// </summary>
	public sealed class PdfFigure : PdfBlock
	{
		/// <summary>
		/// Initializes a figure.
		/// </summary>
		/// <param name="pageNumber">The 1-based page number.</param>
		/// <param name="bounds">The bounds on the page, in points from the top-left corner.</param>
		/// <param name="image">The image of the figure, or null when images are not extracted.</param>
		/// <param name="caption">The caption, or null.</param>
		/// <param name="altText">The alternate text from the structure tree, or null.</param>
		/// <param name="labels">The text drawn inside the figure, or null.</param>
		/// <param name="isVector">True when the figure is a vector drawing.</param>
		internal PdfFigure(int pageNumber, PdfRect bounds, PdfImage image, string caption, string altText, string labels, bool isVector)
			: base(PdfBlockType.Figure, pageNumber, bounds)
		{
			this.Image = image;
			this.Caption = caption;
			this.AltText = altText;
			this.Labels = labels;
			this.IsVector = isVector;
		}

		/// <summary>
		/// The image of the figure, or null when images are not extracted.
		/// </summary>
		public PdfImage Image { get; }

		/// <summary>
		/// The caption of the figure, or null.
		/// </summary>
		public string Caption { get; }

		/// <summary>
		/// The alternate text declared by the document for accessibility, or null.
		/// </summary>
		public string AltText { get; }

		/// <summary>
		/// The text drawn inside the figure (chart labels, legends, diagram text), or null.
		/// </summary>
		public string Labels { get; }

		/// <summary>
		/// True when the figure is drawn with vector graphics (charts, diagrams) rather than being a picture.
		/// </summary>
		public bool IsVector { get; }

		/// <inheritdoc/>
		public override string Text => this.Caption ?? this.AltText ?? this.Labels ?? string.Empty;
	}

	/// <summary>
	/// Helpers shared by the content model classes.
	/// </summary>
	internal static class PdfContentHelpers
	{
		/// <summary>
		/// Concatenates the text of runs.
		/// </summary>
		/// <param name="runs">The runs.</param>
		/// <returns>The text.</returns>
		public static string Concat(IReadOnlyList<PdfTextRun> runs)
		{
			if (runs.Count == 1)
				return runs[0].Text;

			var sb = new StringBuilder();
			foreach (var run in runs)
				sb.Append(run.Text);

			return sb.ToString();
		}
	}
}
