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

// Tests of the Markdown output: escaping, emphasis, links, page markers and
// the image modes.

using System.Linq;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Output
{
	/// <summary>
	/// Tests of <see cref="PdfContent.ToMarkdown"/>.
	/// </summary>
	public class MarkdownTests
	{
		#region Escaping

		/// <summary>Characters with a meaning in Markdown are escaped inside text.</summary>
		[Fact]
		public void Escaping_InlineCharacters()
		{
			var markdown = Paragraph("Use *stars*, _underscores_, [brackets], `ticks`, a \\ backslash and <tags>.");

			Assert.Contains(@"Use \*stars\*, \_underscores\_, \[brackets\], \`ticks\`, a \\ backslash and &lt;tags&gt;.", markdown);
		}

		/// <summary>A paragraph starting with "#" is not a heading.</summary>
		[Fact]
		public void Escaping_HashAtLineStart()
		{
			Assert.StartsWith(@"\#hashtags are popular on social networks these days.", Paragraph("#hashtags are popular on social networks these days."));
		}

		/// <summary>A paragraph starting with a number and a period is not an ordered list.</summary>
		[Fact]
		public void Escaping_NumberPeriodAtLineStart()
		{
			Assert.StartsWith(@"1984\. That was the year the company was founded by two engineers.", Paragraph("1984. That was the year the company was founded by two engineers."));
		}

		/// <summary>Plus and equals signs at the start of a paragraph are escaped; a dash followed by a space too.</summary>
		[Theory]
		[InlineData("+ positive values are shown above the line in the chart.", @"\+ positive")]
		[InlineData("= equal signs underline headings in some Markdown dialects.", @"\= equal")]
		public void Escaping_BlockMarkersAtLineStart(string text, string expected)
		{
			Assert.StartsWith(expected, Paragraph(text));
		}

		#endregion

		#region Emphasis

		/// <summary>Consecutive runs with the same style are wrapped once, outside the spaces.</summary>
		[Fact]
		public void Emphasis_IsGroupedAndTrimmed()
		{
			var builder = new PdfBuilder();
			var regular = builder.Font("Helvetica");
			var bold = builder.Font("Helvetica-Bold");
			var italic = builder.Font("Helvetica-Oblique");
			var boldItalic = builder.Font("Helvetica-BoldOblique");
			builder.AddPage().Use(regular, bold, italic, boldItalic).Content(
				$"BT /{regular.Name} 12 Tf 72 700 Td (Text with ) Tj /{bold.Name} 12 Tf (several bold words ) Tj (in a row) Tj " +
				$"/{regular.Name} 12 Tf ( then ) Tj /{italic.Name} 12 Tf (italic) Tj /{regular.Name} 12 Tf ( and ) Tj /{boldItalic.Name} 12 Tf (both) Tj /{regular.Name} 12 Tf ( styles.) Tj ET");

			var markdown = TestPdf.Extract(builder).ToMarkdown();

			Assert.Contains("Text with **several bold words in a row** then *italic* and ***both*** styles.", markdown);
		}

		/// <summary>Formatting can be turned off.</summary>
		[Fact]
		public void Emphasis_FormattingDisabled()
		{
			var builder = new PdfBuilder();
			var bold = builder.Font("Helvetica-Bold");
			builder.AddPage().Text(bold, 11, 72, 700, "Bold text that is part of a longer paragraph of normal length.");

			var markdown = TestPdf.Extract(builder).ToMarkdown(new PdfMarkdownOptions { IncludeFormatting = false });

			Assert.DoesNotContain("**", markdown);
		}

		#endregion

		#region Links

		/// <summary>A link annotation over a word becomes a Markdown link.</summary>
		[Fact]
		public void Links_UriAnnotation()
		{
			var builder = new PdfBuilder();
			builder.AddPage()
				.Text(builder.Font("Helvetica"), 12, 72, 700, "Visit our website for details.")
				.Link(117, 695, 161, 712, "https://example.com/docs");

			var content = TestPdf.Extract(builder);

			Assert.Contains("Visit our [website](https://example.com/docs) for details.", content.ToMarkdown());
			Assert.Equal("https://example.com/docs", TestPdf.Runs(content).Single(r => r.LinkUri != null).LinkUri);
		}

		/// <summary>Links can be left out of the Markdown.</summary>
		[Fact]
		public void Links_Disabled()
		{
			var builder = new PdfBuilder();
			builder.AddPage()
				.Text(builder.Font("Helvetica"), 12, 72, 700, "Visit our website for details.")
				.Link(117, 695, 161, 712, "https://example.com/docs");

			var markdown = TestPdf.Extract(builder).ToMarkdown(new PdfMarkdownOptions { IncludeLinks = false });

			Assert.Contains("Visit our website for details.", markdown);
		}

		/// <summary>Script URIs are never written as links.</summary>
		[Fact]
		public void Links_UnsafeSchemeIsNotLinked()
		{
			var builder = new PdfBuilder();
			builder.AddPage()
				.Text(builder.Font("Helvetica"), 12, 72, 700, "Visit our website for details.")
				.Link(117, 695, 161, 712, "javascript:alert(1)");

			var markdown = TestPdf.Extract(builder).ToMarkdown();

			Assert.DoesNotContain("javascript", markdown);
			Assert.Contains("Visit our website for details.", markdown);
		}

		/// <summary>A link to another page is reported as the target page number.</summary>
		[Fact]
		public void Links_InternalPage()
		{
			var builder = new PdfBuilder();
			var first = builder.AddPage();
			var second = builder.AddPage().Text(builder.Font("Helvetica"), 12, 72, 700, "The appendix starts here.");
			first.Text(builder.Font("Helvetica"), 12, 72, 700, "Visit our website for details.").Link(117, 695, 161, 712, second);

			var run = TestPdf.Runs(TestPdf.Extract(builder)).Single(r => r.LinkPage != 0);

			Assert.Equal(2, run.LinkPage);
			Assert.Equal("website", run.Text.Trim());
		}

		#endregion

		#region Pages and images

		/// <summary>Page markers are written as HTML comments before each page.</summary>
		[Fact]
		public void PageMarkers()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Text(font, 12, 72, 700, "First page text.");
			builder.AddPage().Text(font, 12, 72, 700, "Second page text.");

			var markdown = TestPdf.Extract(builder).ToMarkdown(new PdfMarkdownOptions { IncludePageMarkers = true });

			Assert.Equal("<!-- page 1 -->\n\nFirst page text.\n\n<!-- page 2 -->\n\nSecond page text.\n", markdown);
		}

		/// <summary>The image modes: reference (with folder or custom URL), embed, placeholder and omit.</summary>
		[Fact]
		public void ImageModes()
		{
			var content = TestPdf.Extract(CreateFigureDocument());

			Assert.Contains("![Figure 1: The logo.](images/image-1.png)\n\n*Figure 1: The logo.*", content.ToMarkdown());
			Assert.Contains("](assets/image-1.png)", content.ToMarkdown(new PdfMarkdownOptions { ImageFolder = "assets/" }));
			Assert.Contains("](https://cdn.example.com/image-1)", content.ToMarkdown(new PdfMarkdownOptions { ImageUrl = i => "https://cdn.example.com/" + i.Id }));
			Assert.Contains("](data:image/png;base64,", content.ToMarkdown(new PdfMarkdownOptions { ImageMode = PdfMarkdownImageMode.Embed }));
			Assert.Contains("[Figure: Figure 1: The logo.]", content.ToMarkdown(new PdfMarkdownOptions { ImageMode = PdfMarkdownImageMode.Placeholder }));

			var omitted = content.ToMarkdown(new PdfMarkdownOptions { ImageMode = PdfMarkdownImageMode.Omit });
			Assert.DoesNotContain("image-1", omitted);
			Assert.DoesNotContain("Figure 1", omitted);
		}

		/// <summary>Markdown of a single page contains only that page.</summary>
		[Fact]
		public void SinglePage()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Text(font, 12, 72, 700, "First page text.");
			builder.AddPage().Text(font, 12, 72, 700, "Second page text.");

			Assert.Equal("Second page text.\n", TestPdf.Extract(builder).Pages[1].ToMarkdown());
		}

		#endregion

		/// <summary>
		/// Extracts a document with a single paragraph and returns its Markdown.
		/// </summary>
		/// <param name="text">The text of the paragraph.</param>
		/// <returns>The Markdown.</returns>
		private static string Paragraph(string text)
		{
			var builder = new PdfBuilder();
			builder.AddPage().Text(builder.Font("Helvetica"), 11, 72, 700, text);
			return TestPdf.Extract(builder).ToMarkdown();
		}

		/// <summary>
		/// Creates a page with a paragraph, a picture and its caption.
		/// </summary>
		/// <returns>The document builder.</returns>
		internal static PdfBuilder CreateFigureDocument()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var image = builder.Image(2, 2, "/DeviceRGB", 8, new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 });
			builder.AddPage()
				.Paragraph(font, 11, 72, 720, 14, TestPdf.Prose[0], TestPdf.Prose[1])
				.Draw(image, 72, 500, 150, 150)
				.Text(font, 10, 72, 485, "Figure 1: The logo.");

			return builder;
		}
	}
}
