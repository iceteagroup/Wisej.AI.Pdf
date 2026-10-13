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

// Integration tests of the layout analysis: headings, lists, running headers
// and footers, captions and code blocks.

using System.Linq;
using Wisej.AI.Pdf.Tests.Helpers;
using Wisej.AI.Pdf.Tests.Tables;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Layout
{
	/// <summary>
	/// Tests of the block structure produced by <see cref="PdfDocument.Extract"/>.
	/// </summary>
	public class LayoutTests
	{
		#region Headings

		/// <summary>Text clearly larger than the body is a heading; distinct sizes get distinct levels.</summary>
		[Fact]
		public void Headings_LargerFonts_GetLevels()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var bold = builder.Font("Helvetica-Bold");
			builder.AddPage()
				.Text(bold, 22, 72, 720, "Annual Review")
				.Paragraph(font, 11, 72, 690, 14, TestPdf.Prose[0], TestPdf.Prose[1], TestPdf.Prose[2])
				.Text(bold, 15, 72, 630, "Operations")
				.Paragraph(font, 11, 72, 605, 14, TestPdf.Prose[3], TestPdf.Prose[4], TestPdf.Prose[5]);

			var content = TestPdf.Extract(builder);
			var headings = content.Blocks.OfType<PdfTextBlock>().Where(b => b.Type == PdfBlockType.Heading).ToList();

			Assert.Equal(new[] { "Annual Review", "Operations" }, headings.Select(h => h.Text));
			Assert.Equal(1, headings[0].Level);
			Assert.Equal(2, headings[1].Level);

			var markdown = content.ToMarkdown();
			Assert.Contains("# Annual Review\n", markdown);
			Assert.Contains("## Operations\n", markdown);
		}

		/// <summary>A bold line at body size followed by a real paragraph is a heading.</summary>
		[Fact]
		public void Headings_BoldBodySizeBeforeParagraph()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var bold = builder.Font("Helvetica-Bold");
			builder.AddPage()
				.Paragraph(font, 11, 72, 720, 14, TestPdf.Prose[0], TestPdf.Prose[1])
				.Text(bold, 11, 72, 680, "Background")
				.Paragraph(font, 11, 72, 662, 14, TestPdf.Prose[2], TestPdf.Prose[3], TestPdf.Prose[4]);

			var blocks = TestPdf.TextBlocks(TestPdf.Extract(builder));
			var heading = blocks.Single(b => b.Type == PdfBlockType.Heading);

			Assert.Equal("Background", heading.Text);
			Assert.True(blocks.IndexOf(heading) == 1);
		}

		/// <summary>A bold sentence inside the flow, followed by more bold text, is emphasis and not a heading.</summary>
		[Fact]
		public void Headings_BoldFollowedByBold_IsNotHeading()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var bold = builder.Font("Helvetica-Bold");
			builder.AddPage()
				.Paragraph(font, 11, 72, 720, 14, TestPdf.Prose[0], TestPdf.Prose[1])
				.Text(bold, 11, 72, 680, "Important")
				.Paragraph(bold, 11, 72, 662, 14, TestPdf.Prose[2], TestPdf.Prose[3], TestPdf.Prose[4]);

			var blocks = TestPdf.TextBlocks(TestPdf.Extract(builder));

			Assert.DoesNotContain(blocks, b => b.Type == PdfBlockType.Heading);
		}

		/// <summary>Disabling heading detection leaves large text as paragraphs.</summary>
		[Fact]
		public void Headings_DetectionDisabled()
		{
			var builder = new PdfBuilder();
			builder.AddPage()
				.Text(builder.Font("Helvetica-Bold"), 22, 72, 720, "Annual Review")
				.Paragraph(builder.Font("Helvetica"), 11, 72, 690, 14, TestPdf.Prose[0], TestPdf.Prose[1]);

			var content = TestPdf.Extract(builder, new PdfExtractionOptions { DetectHeadings = false });

			Assert.DoesNotContain(content.Blocks, b => b.Type == PdfBlockType.Heading);
		}

		#endregion

		#region Lists

		/// <summary>Bullet lines form an unordered list whose items don't include the marker.</summary>
		[Fact]
		public void Lists_Bulleted()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage()
				.Paragraph(font, 11, 72, 720, 14, "The package contains the following parts:")
				.Paragraph(font, 11, 80, 700, 14,
					"• A sturdy wooden frame",
					"• Two adjustable shelves",
					"• The assembly instructions");

			var content = TestPdf.Extract(builder);
			var list = Assert.Single(content.Blocks.OfType<PdfList>());

			Assert.False(list.IsOrdered);
			Assert.Equal(new[] { "A sturdy wooden frame", "Two adjustable shelves", "The assembly instructions" }, list.Items.Select(i => i.Text.Trim()));
			Assert.All(list.Items, i => Assert.Equal("•", i.Marker));
			Assert.Contains("- A sturdy wooden frame\n- Two adjustable shelves\n- The assembly instructions", content.ToMarkdown());
		}

		/// <summary>Numbered lines form an ordered list.</summary>
		[Fact]
		public void Lists_Numbered()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage()
				.Paragraph(font, 11, 72, 720, 14, "Follow these steps to install the software:")
				.Paragraph(font, 11, 80, 700, 14,
					"1. Download the installer package",
					"2. Run the setup program",
					"3. Restart the computer");

			var content = TestPdf.Extract(builder);
			var list = Assert.Single(content.Blocks.OfType<PdfList>());

			Assert.True(list.IsOrdered);
			Assert.Equal(new[] { "1.", "2.", "3." }, list.Items.Select(i => i.Marker));
			Assert.Equal("Download the installer package", list.Items[0].Text.Trim());
			Assert.Contains("1. Download the installer package\n2. Run the setup program\n3. Restart the computer", content.ToMarkdown());
		}

		/// <summary>An item that wraps keeps its continuation line; indented sub-items are nested.</summary>
		[Fact]
		public void Lists_WrappedAndNestedItems()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage()
				.Paragraph(font, 11, 72, 720, 14, "The plan has two phases:")
				.Text(font, 11, 80, 700, "1. Preparation of the site, which includes clearing the land and")
				.Text(font, 11, 92, 686, "marking the boundaries of the building")
				.Text(font, 11, 100, 672, "a. Survey the plot")
				.Text(font, 11, 100, 658, "b. Remove the old fence")
				.Text(font, 11, 80, 644, "2. Construction of the foundations");

			var list = Assert.Single(TestPdf.Extract(builder).Blocks.OfType<PdfList>());

			Assert.Equal(4, list.Items.Count);
			Assert.EndsWith("marking the boundaries of the building", list.Items[0].Text.Trim());
			Assert.Equal(new[] { 0, 1, 1, 0 }, list.Items.Select(i => i.Depth));
		}

		#endregion

		#region Running headers and footers

		/// <summary>Text repeated at the top and bottom of every page is a running header and footer, left out of Markdown.</summary>
		[Fact]
		public void HeadersAndFooters_AreRecognizedAndOmitted()
		{
			var content = TestPdf.Extract(CreateReport(4));

			for (int p = 0; p < 4; p++)
			{
				var blocks = content.Pages[p].Blocks.OfType<PdfTextBlock>().ToList();
				Assert.Contains(blocks, b => b.Type == PdfBlockType.PageHeader && b.Text == "ACME Corporation Quarterly Report");
				Assert.Contains(blocks, b => b.Type == PdfBlockType.PageFooter && b.Text == $"Page {p + 1} of 4");
			}

			var markdown = content.ToMarkdown();
			Assert.DoesNotContain("ACME Corporation", markdown);
			Assert.DoesNotContain("Page 2 of 4", markdown);
			Assert.Contains("Body of page 3.", markdown);
		}

		/// <summary>The Markdown option includes the headers and footers again.</summary>
		[Fact]
		public void HeadersAndFooters_IncludedOnRequest()
		{
			var markdown = TestPdf.Extract(CreateReport(3)).ToMarkdown(new PdfMarkdownOptions { IncludePageHeadersAndFooters = true });

			Assert.Contains("ACME Corporation Quarterly Report", markdown);
			Assert.Contains("Page 3 of 3", markdown);
		}

		/// <summary>With detection disabled, the repeated text stays in the flow.</summary>
		[Fact]
		public void HeadersAndFooters_DetectionDisabled()
		{
			var content = TestPdf.Extract(CreateReport(3), new PdfExtractionOptions { DetectHeadersAndFooters = false });

			Assert.DoesNotContain(content.Blocks, b => b.Type == PdfBlockType.PageHeader || b.Type == PdfBlockType.PageFooter);
			Assert.Contains("ACME Corporation Quarterly Report", content.ToMarkdown());
		}

		#endregion

		#region Captions and code

		/// <summary>A "Figure n:" paragraph under a picture becomes the caption of the figure.</summary>
		[Fact]
		public void Captions_AttachToFigure()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var image = builder.Image(2, 2, "/DeviceRGB", 8, new byte[] { 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0 });
			builder.AddPage()
				.Paragraph(font, 11, 72, 720, 14, TestPdf.Prose[0], TestPdf.Prose[1])
				.Draw(image, 72, 500, 150, 150)
				.Text(font, 10, 72, 485, "Figure 1: A red square drawn as a picture.")
				.Paragraph(font, 11, 72, 440, 14, TestPdf.Prose[2], TestPdf.Prose[3]);

			var content = TestPdf.Extract(builder);
			var figure = Assert.Single(content.Blocks.OfType<PdfFigure>());

			Assert.Equal("Figure 1: A red square drawn as a picture.", figure.Caption);
			Assert.Equal(figure.Caption, figure.Image.Caption);
			Assert.DoesNotContain(content.Blocks, b => b.Type == PdfBlockType.Caption);
			Assert.Contains("*Figure 1: A red square drawn as a picture.*", content.ToMarkdown());
		}

		/// <summary>A "Table n." paragraph above a table becomes the caption of the table.</summary>
		[Fact]
		public void Captions_AttachToTable()
		{
			var builder = new PdfBuilder();
			var page = builder.AddPage().Text(builder.Font("Helvetica"), 10, 72, 715, "Table 2. Monthly sales by region.");
			TableTests.DrawGrid(builder, page, 72, 700, new[] { 100.0, 100, 100 }, 20, new[]
			{
				new[] { "Region", "January", "February" },
				new[] { "North", "120", "135" },
				new[] { "South", "98", "101" },
			});

			var table = Assert.Single(TestPdf.Extract(builder).Tables);

			Assert.Equal("Table 2. Monthly sales by region.", table.Caption);
		}

		/// <summary>Consecutive lines in a monospaced font form one code block that keeps its line breaks.</summary>
		[Fact]
		public void Code_CourierLinesFormCodeBlock()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var courier = builder.Font("Courier");
			builder.AddPage()
				.Paragraph(font, 11, 72, 720, 14, "The sum is computed as shown below.")
				.Text(courier, 10, 72, 690, "int a = 1;")
				.Text(courier, 10, 72, 678, "int b = 2;")
				.Text(courier, 10, 72, 666, "Console.WriteLine(a + b);")
				.Paragraph(font, 11, 72, 640, 14, "The program prints the result and exits.");

			var content = TestPdf.Extract(builder);
			var code = Assert.Single(TestPdf.TextBlocks(content), b => b.Type == PdfBlockType.Code);

			Assert.Equal("int a = 1;\nint b = 2;\nConsole.WriteLine(a + b);", code.Text.TrimEnd());
			Assert.True(code.Runs.All(r => r.IsMonospace));
			Assert.Contains("```\nint a = 1;\nint b = 2;\nConsole.WriteLine(a + b);\n```", content.ToMarkdown());
		}

		/// <summary>Indented code lines stay in the same block, in order, with their indentation.</summary>
		[Fact]
		public void Code_IndentedLinesStayInOneBlock()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var courier = builder.Font("Courier");
			builder.AddPage()
				.Paragraph(font, 11, 72, 720, 14, "The entry point of the program is shown below.")
				.Text(courier, 10, 72, 690, "static void Main()")
				.Text(courier, 10, 72, 678, "{")
				.Text(courier, 10, 96, 666, "Console.WriteLine(\"Hello\");")
				.Text(courier, 10, 72, 654, "}")
				.Paragraph(font, 11, 72, 625, 14, "The program prints a greeting and exits.");

			var content = TestPdf.Extract(builder);
			var code = Assert.Single(TestPdf.TextBlocks(content), b => b.Type == PdfBlockType.Code);

			Assert.Equal("static void Main()\n{\n    Console.WriteLine(\"Hello\");\n}", code.Text.TrimEnd());
		}

		/// <summary>A monospaced word inside a paragraph is inline code, not a code block.</summary>
		[Fact]
		public void Code_InlineMonospace()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var courier = builder.Font("Courier");
			builder.AddPage().Use(font, courier).Content(
				$"BT /{font.Name} 11 Tf 72 700 Td (Call the ) Tj /{courier.Name} 11 Tf (Dispose) Tj /{font.Name} 11 Tf ( method when the work is done.) Tj ET");

			var content = TestPdf.Extract(builder);

			Assert.DoesNotContain(content.Blocks, b => b.Type == PdfBlockType.Code);
			Assert.Contains("Call the `Dispose` method", content.ToMarkdown());
		}

		#endregion

		/// <summary>
		/// Creates a report with a running header, a page number footer and a paragraph on each page.
		/// </summary>
		/// <param name="pages">The number of pages.</param>
		/// <returns>The document builder.</returns>
		internal static PdfBuilder CreateReport(int pages)
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			for (int p = 1; p <= pages; p++)
			{
				builder.AddPage()
					.Text(font, 9, 72, 760, "ACME Corporation Quarterly Report")
					.Paragraph(font, 11, 72, 680, 14, $"Body of page {p}.", TestPdf.Prose[p % TestPdf.Prose.Length], TestPdf.Prose[(p + 1) % TestPdf.Prose.Length])
					.Text(font, 9, 280, 40, $"Page {p} of {pages}");
			}

			return builder;
		}
	}
}
