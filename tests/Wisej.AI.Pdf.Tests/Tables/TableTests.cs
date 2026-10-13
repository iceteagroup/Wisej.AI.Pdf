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

// Integration tests of the table detection: ruled grids with merged cells,
// borderless tables, booktabs tables, tables continued across pages and the
// table output formats.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Tables
{
	/// <summary>
	/// Tests of <see cref="PdfTable"/> detection and output.
	/// </summary>
	public class TableTests
	{
		#region Ruled tables

		/// <summary>A fully ruled grid becomes a table with the same rows and columns.</summary>
		[Fact]
		public void Ruled_SimpleGrid()
		{
			var builder = new PdfBuilder();
			var page = builder.AddPage();
			DrawGrid(builder, page, 72, 700, new[] { 120.0, 90, 90 }, 20, new[]
			{
				new[] { "Country", "Capital", "Currency" },
				new[] { "France", "Paris", "Euro" },
				new[] { "Japan", "Tokyo", "Yen" },
				new[] { "Brazil", "Brasilia", "Real" },
			});

			var table = Assert.Single(TestPdf.Extract(builder).Tables);

			Assert.True(table.IsRuled);
			Assert.Equal(4, table.RowCount);
			Assert.Equal(3, table.ColumnCount);
			Assert.False(table.HasSpans);
			Assert.Equal(new[] { "Japan", "Tokyo", "Yen" }, table.ToArray()[2]);
		}

		/// <summary>Cells without inner rules are merged: row and column spans (spanning headers centred, as usual).</summary>
		[Fact]
		public void Ruled_MergedCells()
		{
			var table = Assert.Single(TestPdf.Extract(CreateMergedTable(centered: true)).Tables);

			Assert.Equal(4, table.RowCount);
			Assert.Equal(4, table.ColumnCount);
			Assert.True(table.HasSpans);

			var product = table[1, 0];
			Assert.Equal("Product", product.Text);
			Assert.Equal(2, product.RowSpan);
			Assert.Same(product, table[0, 0]);

			var sales = table[0, 2];
			Assert.Equal("Sales", sales.Text);
			Assert.Equal(2, sales.ColumnSpan);
			Assert.Equal(1, sales.Column);

			Assert.Equal(new[] { "Product", "Sales", string.Empty, "Notes" }, table.ToArray()[0]);
			Assert.Equal(new[] { string.Empty, "Q1", "Q2", string.Empty }, table.ToArray()[1]);

			var html = table.ToHtml();
			Assert.Contains("rowspan=\"2\">", html);
			Assert.Contains("colspan=\"2\">", html);
		}

		/// <summary>A merged cell whose text doesn't reach the missing rule is still a column span, as row spans are.</summary>
		[Fact]
		public void Ruled_MergedCells_LeftAlignedSpanningText()
		{
			var table = Assert.Single(TestPdf.Extract(CreateMergedTable(centered: false)).Tables);

			var sales = table[0, 2];
			Assert.Equal("Sales", sales.Text);
			Assert.Equal(2, sales.ColumnSpan);
		}

		/// <summary>A table with spans is written as HTML inside Markdown in the automatic table mode.</summary>
		[Fact]
		public void Ruled_MergedCells_MarkdownUsesHtml()
		{
			var content = TestPdf.Extract(CreateMergedTable(centered: true));

			Assert.Contains("<table>", content.ToMarkdown());
			Assert.Contains("| Product | Sales |  | Notes |", content.ToMarkdown(new PdfMarkdownOptions { TableMode = PdfMarkdownTableMode.Pipe }));
		}

		#endregion

		#region Borderless and booktabs tables

		/// <summary>Columns of aligned text without rules form a borderless table.</summary>
		[Fact]
		public void Borderless_AlignedColumns()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var bold = builder.Font("Helvetica-Bold");
			var page = builder.AddPage().Paragraph(font, 11, 72, 740, 14, "The inventory at the end of the month:");

			var rows = new[]
			{
				new[] { "Item", "Quantity", "Price" },
				new[] { "Apples", "120", "0.50" },
				new[] { "Bananas", "85", "0.25" },
				new[] { "Cherries", "300", "4.00" },
				new[] { "Dates", "42", "6.75" },
			};

			for (int r = 0; r < rows.Length; r++)
			{
				var y = 700 - r * 18;
				var rowFont = r == 0 ? bold : font;
				page.Text(rowFont, 11, 72, y, rows[r][0]).Text(rowFont, 11, 220, y, rows[r][1]).Text(rowFont, 11, 340, y, rows[r][2]);
			}

			var table = Assert.Single(TestPdf.Extract(builder).Tables);

			Assert.False(table.IsRuled);
			Assert.Equal(5, table.RowCount);
			Assert.Equal(3, table.ColumnCount);
			Assert.Equal(rows.Select(r => r.ToList()), table.ToArray().Select(r => r.ToList()));
		}

		/// <summary>A booktabs table (horizontal rules only) with a two-level header.</summary>
		[Fact]
		public void Booktabs_TwoLevelHeader()
		{
			var table = Assert.Single(TestPdf.Extract(CreateBooktabsTable()).Tables);
			var array = table.ToArray();

			Assert.False(table.IsRuled);
			Assert.Equal(5, table.ColumnCount);
			Assert.Equal(5, table.RowCount);
			Assert.Equal(2, table.HeaderRowCount);
			Assert.Contains("Accuracy", array[0]);
			Assert.Contains("Speed", array[0]);
			Assert.Equal(new[] { "Model", "Train", "Test", "CPU", "GPU" }, array[1]);
			Assert.Equal(new[] { "Gamma", "95.0", "92.7", "210", "25" }, array[4]);
			Assert.All(table.Cells.Where(c => c.Row < 2), c => Assert.True(c.IsHeader));
		}

		/// <summary>A group header centred over a partial rule ("cmidrule") spans the columns under the rule.</summary>
		[Fact]
		public void Booktabs_GroupHeaderSpansColumnsUnderItsRule()
		{
			var table = Assert.Single(TestPdf.Extract(CreateBooktabsTable()).Tables);

			var accuracy = table.Cells.Single(c => c.Text == "Accuracy");
			Assert.Equal(0, accuracy.Row);
			Assert.Equal(1, accuracy.Column);
			Assert.Equal(2, accuracy.ColumnSpan);
		}

		#endregion

		#region Tables across pages

		/// <summary>A table continued on the next page with the same columns is reported once, without the repeated header.</summary>
		[Fact]
		public void AcrossPages_AreJoined()
		{
			var content = TestPdf.Extract(CreateContinuedTable(2));

			var table = Assert.Single(content.Tables);
			var array = table.ToArray();

			Assert.Equal(7, table.RowCount);
			Assert.Equal(new[] { "Code", "Description", "Amount" }, array[0]);
			Assert.Equal(new[] { "A1", "Item one", "10" }, array[1]);
			Assert.Equal(new[] { "B3", "Item six", "60" }, array[6]);
			Assert.DoesNotContain(content.Pages[1].Blocks, b => b is PdfTable);
		}

		/// <summary>The joined table reports all the pages it spans.</summary>
		[Fact]
		public void AcrossPages_PageNumbersListAllPages()
		{
			var table = Assert.Single(TestPdf.Extract(CreateContinuedTable(2)).Tables);

			Assert.Equal(new[] { 1, 2 }, table.PageNumbers);
		}

		/// <summary>A table that spans three pages is joined into one.</summary>
		[Fact]
		public void AcrossPages_ThreePagesAreJoined()
		{
			var content = TestPdf.Extract(CreateContinuedTable(3));

			var table = Assert.Single(content.Tables);
			Assert.Equal(10, table.RowCount);
		}

		/// <summary>Disabling the join keeps one table per page.</summary>
		[Fact]
		public void AcrossPages_JoinDisabled()
		{
			var content = TestPdf.Extract(CreateContinuedTable(2), new PdfExtractionOptions { JoinTablesAcrossPages = false });

			Assert.Equal(2, content.Tables.Count);
		}

		#endregion

		#region Output formats

		/// <summary>CSV quotes values with commas, quotes and line breaks (RFC 4180).</summary>
		[Fact]
		public void Output_Csv()
		{
			var table = Assert.Single(TestPdf.Extract(CreateSpecialCharactersTable()).Tables);

			Assert.Equal("Name,Note\r\n\"Smith, John\",\"Said \"\"hi\"\"\"\r\nA|B,Plain\r\n", table.ToCsv());
		}

		/// <summary>Markdown pipe tables escape pipes and add the header separator.</summary>
		[Fact]
		public void Output_Markdown()
		{
			var table = Assert.Single(TestPdf.Extract(CreateSpecialCharactersTable()).Tables);

			Assert.Equal("| Name | Note |\n| --- | --- |\n| Smith, John | Said \"hi\" |\n| A\\|B | Plain |\n", table.ToMarkdown());
		}

		/// <summary>HTML tables encode special characters and mark the header cells.</summary>
		[Fact]
		public void Output_Html()
		{
			var builder = new PdfBuilder();
			var page = builder.AddPage();
			DrawGrid(builder, page, 72, 700, new[] { 120.0, 120 }, 20, new[]
			{
				new[] { "Symbol", "Meaning" },
				new[] { "<", "less & smaller" },
			}, boldHeader: true);

			var table = Assert.Single(TestPdf.Extract(builder).Tables);
			var html = table.ToHtml();

			Assert.Equal(1, table.HeaderRowCount);
			Assert.Contains("<th><strong>Symbol</strong></th>", html);
			Assert.Contains("<td>&lt;</td>", html);
			Assert.Contains("less &amp; smaller", html);
		}

		#endregion

		#region Helpers

		/// <summary>
		/// A cell of a table drawn by <see cref="DrawTable"/>.
		/// </summary>
		/// <param name="Row">The first row.</param>
		/// <param name="Column">The first column.</param>
		/// <param name="Text">The text.</param>
		/// <param name="RowSpan">The rows spanned.</param>
		/// <param name="ColumnSpan">The columns spanned.</param>
		/// <param name="Centered">True to centre the text in the cell; false to align it to the left.</param>
		internal sealed record TableCell(int Row, int Column, string Text, int RowSpan, int ColumnSpan, bool Centered);

		/// <summary>
		/// Creates a cell for <see cref="DrawTable"/>.
		/// </summary>
		/// <param name="row">The first row.</param>
		/// <param name="column">The first column.</param>
		/// <param name="text">The text.</param>
		/// <param name="rowSpan">The rows spanned.</param>
		/// <param name="columnSpan">The columns spanned.</param>
		/// <param name="centered">True to centre the text in the cell.</param>
		/// <returns>The cell.</returns>
		internal static TableCell Cell(int row, int column, string text, int rowSpan = 1, int columnSpan = 1, bool centered = false)
		{
			return new TableCell(row, column, text, rowSpan, columnSpan, centered);
		}

		/// <summary>
		/// Draws a fully ruled grid with one text per cell.
		/// </summary>
		/// <param name="builder">The document.</param>
		/// <param name="page">The page.</param>
		/// <param name="left">The left edge of the table.</param>
		/// <param name="top">The top edge of the table.</param>
		/// <param name="widths">The column widths.</param>
		/// <param name="rowHeight">The row height.</param>
		/// <param name="rows">The text of the cells, by row.</param>
		/// <param name="boldHeader">True to draw the first row in bold.</param>
		internal static void DrawGrid(PdfBuilder builder, PdfPageBuilder page, double left, double top, double[] widths, double rowHeight, string[][] rows, bool boldHeader = false)
		{
			var cells = new List<TableCell>();
			for (int r = 0; r < rows.Length; r++)
			{
				for (int c = 0; c < rows[r].Length; c++)
					cells.Add(Cell(r, c, rows[r][c]));
			}

			DrawTable(builder, page, left, top, widths, rowHeight, cells, boldHeader);
		}

		/// <summary>
		/// Draws a ruled table: the border of every cell, so merged cells have no inner rules.
		/// </summary>
		/// <param name="builder">The document.</param>
		/// <param name="page">The page.</param>
		/// <param name="left">The left edge of the table.</param>
		/// <param name="top">The top edge of the table.</param>
		/// <param name="widths">The column widths.</param>
		/// <param name="rowHeight">The row height.</param>
		/// <param name="cells">The cells.</param>
		/// <param name="boldHeader">True to draw the first row in bold.</param>
		internal static void DrawTable(PdfBuilder builder, PdfPageBuilder page, double left, double top, double[] widths, double rowHeight, IEnumerable<TableCell> cells, bool boldHeader = false)
		{
			var font = builder.Font("Helvetica");
			var bold = builder.Font("Helvetica-Bold");
			foreach (var cell in cells)
			{
				var x0 = left + widths.Take(cell.Column).Sum();
				var x1 = x0 + widths.Skip(cell.Column).Take(cell.ColumnSpan).Sum();
				var y0 = top - cell.Row * rowHeight;
				var y1 = y0 - cell.RowSpan * rowHeight;

				page.Line(x0, y0, x1, y0).Line(x1, y0, x1, y1).Line(x1, y1, x0, y1).Line(x0, y1, x0, y0);
				// Helvetica averages about half an em per character: close enough to centre short labels.
				var x = cell.Centered ? (x0 + x1 - cell.Text.Length * 5) / 2 : x0 + 4;
				page.Text(boldHeader && cell.Row == 0 ? bold : font, 10, x, (y0 + y1) / 2 - 3.5, cell.Text);
			}
		}

		/// <summary>
		/// Creates a document with a ruled table that starts at the bottom of page 1 and continues on the next pages,
		/// repeating its header row on each page.
		/// </summary>
		/// <param name="pages">The number of pages.</param>
		/// <returns>The document builder.</returns>
		private static PdfBuilder CreateContinuedTable(int pages)
		{
			var names = new[] { "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var widths = new[] { 80.0, 200, 80 };
			var header = new[] { "Code", "Description", "Amount" };

			for (int p = 0; p < pages; p++)
			{
				var page = builder.AddPage();
				var rows = new List<string[]> { header };
				for (int i = 0; i < 3; i++)
				{
					var n = p * 3 + i;
					rows.Add(new[] { $"{(char)('A' + p)}{i + 1}", "Item " + names[n], ((n + 1) * 10).ToString(CultureInfo.InvariantCulture) });
				}

				// the first page has text above the table, the following pages start with the table.
				if (p == 0)
					page.Paragraph(font, 11, 72, 720, 14, TestPdf.Prose[0], TestPdf.Prose[1], TestPdf.Prose[2]);

				DrawGrid(builder, page, 72, p == 0 ? 160 : 740, widths, 20, rows.ToArray());
			}

			return builder;
		}

		/// <summary>
		/// Creates a booktabs table: top rule, centred group headers over partial rules, column headers,
		/// a middle rule, three data rows and a bottom rule.
		/// </summary>
		/// <returns>The document builder.</returns>
		private static PdfBuilder CreateBooktabsTable()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Times-Roman");
			var page = builder.AddPage().Paragraph(font, 11, 72, 740, 14, "The results of the experiments are summarized below.");

			page.Line(72, 712, 472, 712, 1);
			page.Text(font, 11, 222, 700, "Accuracy").Text(font, 11, 380, 700, "Speed");
			page.Line(180, 694, 300, 694, 0.5).Line(330, 694, 460, 694, 0.5);

			var columns = new[] { 80.0, 190, 250, 340, 410 };
			var header = new[] { "Model", "Train", "Test", "CPU", "GPU" };
			for (int c = 0; c < columns.Length; c++)
				page.Text(font, 11, columns[c], 682, header[c]);

			page.Line(72, 676, 472, 676, 0.5);

			var data = new[]
			{
				new[] { "Alpha", "91.2", "88.4", "120", "15" },
				new[] { "Beta", "93.5", "90.1", "140", "18" },
				new[] { "Gamma", "95.0", "92.7", "210", "25" },
			};

			for (int r = 0; r < data.Length; r++)
			{
				for (int c = 0; c < columns.Length; c++)
					page.Text(font, 11, columns[c], 662 - r * 16, data[r][c]);
			}

			page.Line(72, 622, 472, 622, 1);
			return builder;
		}

		/// <summary>
		/// Creates a ruled table with a two-row header: "Product" and "Notes" span two rows, "Sales" spans two columns.
		/// </summary>
		/// <param name="centered">True to centre the spanning header texts.</param>
		/// <returns>The document builder.</returns>
		private static PdfBuilder CreateMergedTable(bool centered)
		{
			var builder = new PdfBuilder();
			DrawTable(builder, builder.AddPage(), 72, 700, new[] { 110.0, 70, 70, 120 }, 20, new[]
			{
				Cell(0, 0, "Product", rowSpan: 2),
				Cell(0, 1, "Sales", columnSpan: 2, centered: centered),
				Cell(0, 3, "Notes", rowSpan: 2),
				Cell(1, 1, "Q1"), Cell(1, 2, "Q2"),
				Cell(2, 0, "Widgets"), Cell(2, 1, "100"), Cell(2, 2, "120"), Cell(2, 3, "Stable"),
				Cell(3, 0, "Gadgets"), Cell(3, 1, "80"), Cell(3, 2, "95"), Cell(3, 3, "Growing"),
			});

			return builder;
		}

		/// <summary>
		/// Creates a ruled table whose cells contain commas, quotes and pipes.
		/// </summary>
		/// <returns>The document builder.</returns>
		private static PdfBuilder CreateSpecialCharactersTable()
		{
			var builder = new PdfBuilder();
			DrawGrid(builder, builder.AddPage(), 72, 700, new[] { 150.0, 150 }, 20, new[]
			{
				new[] { "Name", "Note" },
				new[] { "Smith, John", "Said \"hi\"" },
				new[] { "A|B", "Plain" },
			});

			return builder;
		}

		#endregion
	}
}
