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

// Tests of the JSON, HTML and plain text outputs.

using System.Linq;
using System.Text.Json;
using Wisej.AI.Pdf.Tests.Helpers;
using Wisej.AI.Pdf.Tests.Tables;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Output
{
	/// <summary>
	/// Tests of <see cref="PdfContent.ToJson"/>, <see cref="PdfContent.ToHtml"/> and <see cref="PdfContent.ToText"/>.
	/// </summary>
	public class JsonHtmlTextTests
	{
		#region JSON

		/// <summary>The JSON output is valid, indented or not, and describes pages, blocks, runs, tables and images.</summary>
		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void Json_IsValidAndComplete(bool indented)
		{
			var content = TestPdf.Extract(CreateRichDocument());

			using (var json = JsonDocument.Parse(content.ToJson(indented)))
			{
				var root = json.RootElement;
				Assert.Equal("Rich \"quoted\" title", root.GetProperty("info").GetProperty("title").GetString());
				Assert.Equal(1, root.GetProperty("pageCount").GetInt32());

				var page = root.GetProperty("pages")[0];
				Assert.Equal(612, page.GetProperty("width").GetDouble());

				var blocks = page.GetProperty("blocks").EnumerateArray().ToList();
				var types = blocks.Select(b => b.GetProperty("type").GetString()).ToList();
				Assert.Contains("heading", types);
				Assert.Contains("paragraph", types);
				Assert.Contains("table", types);
				Assert.Contains("figure", types);

				var heading = blocks.First(b => b.GetProperty("type").GetString() == "heading");
				Assert.Equal(1, heading.GetProperty("level").GetInt32());

				var paragraph = blocks.Last(b => b.GetProperty("type").GetString() == "paragraph");
				Assert.Equal("Quotes \"and\" a back\\slash survive.", paragraph.GetProperty("text").GetString());

				var table = blocks.First(b => b.GetProperty("type").GetString() == "table");
				Assert.Equal(2, table.GetProperty("columns").GetInt32());
				Assert.Equal("Key", table.GetProperty("cells")[0].GetProperty("text").GetString());

				var figure = blocks.First(b => b.GetProperty("type").GetString() == "figure");
				Assert.Equal("image-1", figure.GetProperty("image").GetString());

				var image = root.GetProperty("images")[0];
				Assert.Equal("image-1.png", image.GetProperty("fileName").GetString());
				Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
			}
		}

		/// <summary>Bold runs are flagged in the JSON runs.</summary>
		[Fact]
		public void Json_RunsCarryFormatting()
		{
			var builder = new PdfBuilder();
			var regular = builder.Font("Helvetica");
			var bold = builder.Font("Helvetica-Bold");
			builder.AddPage().Use(regular, bold).Content($"BT /{regular.Name} 12 Tf 72 700 Td (Normal ) Tj /{bold.Name} 12 Tf (strong) Tj /{regular.Name} 12 Tf ( end.) Tj ET");

			using (var json = JsonDocument.Parse(TestPdf.Extract(builder).ToJson()))
			{
				var runs = json.RootElement.GetProperty("pages")[0].GetProperty("blocks")[0].GetProperty("runs").EnumerateArray().ToList();
				var strong = runs.Single(r => r.GetProperty("text").GetString().Contains("strong"));

				Assert.True(strong.GetProperty("bold").GetBoolean());
				Assert.False(runs[0].TryGetProperty("bold", out _));
			}
		}

		/// <summary>Spanning table cells carry their spans in JSON.</summary>
		[Fact]
		public void Json_TableSpans()
		{
			var builder = new PdfBuilder();
			TableTests.DrawTable(builder, builder.AddPage(), 72, 700, new[] { 100.0, 100 }, 20, new[]
			{
				TableTests.Cell(0, 0, "Left", rowSpan: 2),
				TableTests.Cell(0, 1, "Top"),
				TableTests.Cell(1, 1, "Bottom"),
			});

			using (var json = JsonDocument.Parse(TestPdf.Extract(builder).ToJson()))
			{
				var table = json.RootElement.GetProperty("pages")[0].GetProperty("blocks").EnumerateArray().Single(b => b.GetProperty("type").GetString() == "table");
				var left = table.GetProperty("cells").EnumerateArray().Single(c => c.GetProperty("text").GetString() == "Left");

				Assert.Equal(2, left.GetProperty("rowSpan").GetInt32());
			}
		}

		/// <summary>Text with an unpaired surrogate (a broken ToUnicode mapping) still produces valid JSON.</summary>
		[Fact]
		public void Json_UnpairedSurrogate_IsValid()
		{
			var builder = new PdfBuilder();
			var cmap = builder.AddStream(string.Empty, PdfText.ToBytes(
				"begincmap 1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <D800> endbfchar endcmap"));
			var font = builder.Register("Font", builder.AddObject(
				$"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode {PdfText.Reference(cmap)} >>"));
			builder.AddPage().Text(font, 12, 72, 700, "A broken mapping");

			var json = TestPdf.Extract(builder).ToJson();

			using (JsonDocument.Parse(json))
			{
			}
		}

		#endregion

		#region HTML

		/// <summary>The HTML document has a title, headings, paragraphs, tables and embedded images, with special characters encoded.</summary>
		[Fact]
		public void Html_Document()
		{
			var html = TestPdf.Extract(CreateRichDocument()).ToHtml();

			Assert.StartsWith("<!DOCTYPE html>", html);
			Assert.Contains("<title>Rich &quot;quoted&quot; title</title>", html);
			Assert.Contains("<h1>Overview &amp; Scope</h1>", html);
			Assert.Contains("<table>", html);
			Assert.Contains("<img src=\"data:image/png;base64,", html);
			Assert.Contains("<section data-page=\"1\">", html);
		}

		/// <summary>Without embedding, images are referenced by file name.</summary>
		[Fact]
		public void Html_ReferencedImages()
		{
			var html = TestPdf.Extract(CreateRichDocument()).ToHtml(embedImages: false);

			Assert.Contains("<img src=\"image-1.png\"", html);
			Assert.DoesNotContain("base64", html);
		}

		/// <summary>Lists are written as ul and ol elements.</summary>
		[Fact]
		public void Html_Lists()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage()
				.Paragraph(font, 11, 72, 720, 14, "Follow these steps to install the software:")
				.Paragraph(font, 11, 80, 700, 14, "1. Download the installer package", "2. Run the setup program", "3. Restart the computer");

			var html = TestPdf.Extract(builder).ToHtml();

			Assert.Contains("<ol>", html);
			Assert.Contains("<li>Run the setup program</li>", html);
		}

		#endregion

		#region Plain text

		/// <summary>Plain text separates blocks with blank lines, tables with tabs and pages with form feeds on request.</summary>
		[Fact]
		public void Text_BlocksTablesAndPageBreaks()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Text(font, 12, 72, 700, "First page.");
			var page = builder.AddPage();
			TableTests.DrawGrid(builder, page, 72, 700, new[] { 100.0, 100 }, 20, new[] { new[] { "a", "b" }, new[] { "c", "d" } });

			var content = TestPdf.Extract(builder);

			Assert.Contains("a\tb\nc\td", content.ToText().Replace("\r\n", "\n"));
			Assert.Contains("\f", content.ToText(includePageBreaks: true));
			Assert.DoesNotContain("\f", content.ToText());
		}

		#endregion

		/// <summary>
		/// Creates a page with a heading, a paragraph with special characters, a table and a figure.
		/// </summary>
		/// <returns>The document builder.</returns>
		private static PdfBuilder CreateRichDocument()
		{
			var builder = new PdfBuilder();
			builder.SetInfo(("Title", "Rich \"quoted\" title"));

			var font = builder.Font("Helvetica");
			var page = builder.AddPage()
				.Text(font, 20, 72, 730, "Overview & Scope")
				.Paragraph(font, 11, 72, 700, 14, TestPdf.Prose[0], TestPdf.Prose[1])
				.Text(font, 11, 72, 660, "Quotes \"and\" a back\\slash survive.");

			TableTests.DrawGrid(builder, page, 72, 620, new[] { 100.0, 100 }, 20, new[] { new[] { "Key", "Value" }, new[] { "alpha", "1" } });

			var image = builder.Image(2, 2, "/DeviceRGB", 8, new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 });
			page.Draw(image, 72, 300, 120, 120);
			return builder;
		}
	}
}
