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

// Internal model of a detected table.

using System.Collections.Generic;

namespace Wisej.AI.Pdf.Layout
{
	/// <summary>
	/// A cell of a detected table.
	/// </summary>
	internal sealed class TableCellModel
	{
		/// <summary>The zero-based row index.</summary>
		public int Row;

		/// <summary>The zero-based column index.</summary>
		public int Column;

		/// <summary>The number of rows spanned (at least 1).</summary>
		public int RowSpan = 1;

		/// <summary>The number of columns spanned (at least 1).</summary>
		public int ColumnSpan = 1;

		/// <summary>The bounds of the cell in the reading frame.</summary>
		public PdfRect Bounds;

		/// <summary>The lines of text in the cell, top to bottom.</summary>
		public List<TextLine> Lines = new List<TextLine>();

		/// <summary>True when the cell is a header cell.</summary>
		public bool IsHeader;

		/// <summary>
		/// Returns the text of the cell.
		/// </summary>
		/// <returns>The text with lines joined by spaces.</returns>
		public string GetText() => TextAssembler.JoinLines(this.Lines).Trim();

		/// <inheritdoc/>
		public override string ToString() => $"[{this.Row},{this.Column}] {GetText()}";
	}

	/// <summary>
	/// A table detected on a page.
	/// </summary>
	internal sealed class TableModel
	{
		/// <summary>The number of rows.</summary>
		public int RowCount;

		/// <summary>The number of columns.</summary>
		public int ColumnCount;

		/// <summary>The cells; spanned positions are covered by a single cell.</summary>
		public List<TableCellModel> Cells = new List<TableCellModel>();

		/// <summary>The number of header rows at the top of the table.</summary>
		public int HeaderRowCount;

		/// <summary>True when the table was detected from ruling lines; false for borderless tables.</summary>
		public bool IsRuled;

		/// <summary>The bounds of the table in the reading frame.</summary>
		public PdfRect Bounds;

		/// <summary>The pages where the table continues, after the page where it starts.</summary>
		public List<int> ContinuedOn = new List<int>();

		/// <summary>The horizontal positions of the column boundaries (ColumnCount + 1 values).</summary>
		public double[] ColumnEdges;

		/// <summary>The vertical positions of the row boundaries (RowCount + 1 values).</summary>
		public double[] RowEdges;

		/// <summary>
		/// Returns the cell that covers the position (<paramref name="row"/>, <paramref name="column"/>), or null.
		/// </summary>
		/// <param name="row">The row index.</param>
		/// <param name="column">The column index.</param>
		/// <returns>The cell, or null.</returns>
		public TableCellModel GetCell(int row, int column)
		{
			foreach (var cell in this.Cells)
			{
				if (row >= cell.Row && row < cell.Row + cell.RowSpan && column >= cell.Column && column < cell.Column + cell.ColumnSpan)
					return cell;
			}

			return null;
		}
	}
}
