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

// Integration tests of the text extraction: words and spaces, reading order,
// de-hyphenation, scripts, styles, rotation, Type 3 fonts and visibility.

using System.Linq;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Text
{
	/// <summary>
	/// Tests of the text produced by <see cref="PdfDocument.Extract"/>.
	/// </summary>
	public class TextExtractionTests
	{
		#region Words and spaces

		/// <summary>Explicit spaces separate words; text shown in pieces on one line joins in order.</summary>
		[Fact]
		public void ExplicitSpacesAndPieces()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Content($"BT /{font.Name} 12 Tf 72 700 Td (Hello World) Tj ( again) Tj (!) Tj ET").Use(font);

			Assert.Equal("Hello World again!", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>Small TJ kerning stays inside a word; a large negative adjustment is a word space.</summary>
		[Fact]
		public void KernedTJ_SpacesOnlyAtLargeGaps()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Times-Roman");
			builder.AddPage().Use(font).Content($"BT /{font.Name} 12 Tf 72 700 Td [(W) 80 (A) 40 (V) 120 (E) -30 (S) -300 (are) -280 (kerned)] TJ ET");

			Assert.Equal("WAVES are kerned", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>Words positioned with Td instead of spaces are still separated.</summary>
		[Fact]
		public void PositionedWordsWithoutSpaceCharacters()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Use(font).Content($"BT /{font.Name} 12 Tf 72 700 Td (Positioned) Tj 70 0 Td (words) Tj 40 0 Td (here) Tj ET");

			Assert.Equal("Positioned words here", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>The line operators T*, ' and " move to the next line using the leading (TL).</summary>
		[Fact]
		public void NextLineOperators()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Use(font).Content(
				$"BT /{font.Name} 12 Tf 16 TL 72 700 Td (Line one) Tj T* (Line two) Tj (Line three) ' 1 0.5 (Line four) \" ET");

			var content = TestPdf.Extract(builder, new PdfExtractionOptions { ExtractImages = false });

			Assert.Equal("Line one Line two Line three Line four", TestPdf.Normalize(content.ToText()));
		}

		/// <summary>Horizontal scaling (Tz) stretches the text without splitting its words.</summary>
		[Fact]
		public void HorizontalScaling()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Use(font).Content($"BT /{font.Name} 12 Tf 160 Tz 72 700 Td (Stretched words here) Tj ET");

			Assert.Equal("Stretched words here", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>Character and word spacing (Tc, Tw) widen the text without breaking words.</summary>
		[Fact]
		public void CharacterAndWordSpacing()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Use(font).Content($"BT /{font.Name} 12 Tf 1.5 Tc 6 Tw 72 700 Td (Spaced out text) Tj ET");

			Assert.Equal("Spaced out text", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>WinAnsi characters above 127 decode to the right Unicode characters.</summary>
		[Fact]
		public void WinAnsiCharacters()
		{
			var builder = new PdfBuilder();
			builder.AddPage().Text(builder.Font("Helvetica"), 12, 72, 700, "Café €5 “quoted” naïve — done");

			Assert.Equal("Café €5 “quoted” naïve — done", TestPdf.Normalize(Extract(builder)));
		}

		#endregion

		#region Reading order

		/// <summary>A full-width title, then the left column, then the right column.</summary>
		[Fact]
		public void TwoColumns_WithFullWidthTitle()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var bold = builder.Font("Helvetica-Bold");
			var page = builder.AddPage();

			page.Text(bold, 18, 72, 720, "A Title Spanning Both Columns Of The Page");
			page.Paragraph(font, 10, 72, 680, 13,
				"The left column starts with a sentence",
				"that wraps over several lines of text,",
				"as the body of an article usually does",
				"when it is set in two narrow columns.",
				"It goes on with more words to make the",
				"column look like real prose to readers.",
				"The left column ends on this line here.");
			page.Paragraph(font, 10, 320, 680, 13,
				"The right column begins with another",
				"sentence that also wraps over several",
				"lines, continuing the discussion from",
				"the bottom of the left column above it",
				"and adding a few more remarks on the",
				"topic so the layout has some substance.",
				"The right column ends on this line.");

			var content = TestPdf.Extract(builder);
			var blocks = TestPdf.TextBlocks(content);

			Assert.Equal(3, blocks.Count);
			Assert.Equal("A Title Spanning Both Columns Of The Page", blocks[0].Text);
			Assert.StartsWith("The left column starts", blocks[1].Text);
			Assert.EndsWith("The left column ends on this line here.", blocks[1].Text);
			Assert.StartsWith("The right column begins", blocks[2].Text);
			Assert.EndsWith("The right column ends on this line.", blocks[2].Text);
		}

		/// <summary>Content streams that draw lines bottom to top still read top to bottom.</summary>
		[Fact]
		public void LinesDrawnOutOfOrder_ReadTopToBottom()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage()
				.Text(font, 12, 72, 660, "third line")
				.Text(font, 12, 72, 700, "first line")
				.Text(font, 12, 72, 680, "second line");

			Assert.Equal("first line second line third line", TestPdf.Normalize(Extract(builder)));
		}

		#endregion

		#region De-hyphenation

		/// <summary>A word broken with a hyphen at the end of a line is joined.</summary>
		[Fact]
		public void Hyphenation_IsRemoved()
		{
			var builder = new PdfBuilder();
			builder.AddPage().Paragraph(builder.Font("Helvetica"), 11, 72, 700, 14,
				"The library processes every docu-",
				"ment with great care and atten-",
				"tion to the smallest details.");

			Assert.Equal("The library processes every document with great care and attention to the smallest details.", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>A compound used elsewhere in the document with its hyphen is not joined into one word.</summary>
		[Fact]
		public void Hyphenation_KnownCompound_IsNotJoined()
		{
			var text = TestPdf.Normalize(Extract(CreateCompoundDocument()));

			Assert.Contains("really well-", text);
			Assert.DoesNotContain("wellknown", text);
		}

		/// <summary>A compound broken after its hyphen keeps the hyphen and no space: "well-known".</summary>
		[Fact]
		public void Hyphenation_KnownCompound_KeepsHyphenWithoutSpace()
		{
			var text = TestPdf.Normalize(Extract(CreateCompoundDocument()));

			Assert.Contains("really well-known method", text);
		}

		/// <summary>A hyphen followed by a capitalized word is kept, without a space: "Paris-London".</summary>
		[Fact]
		public void Hyphenation_BeforeCapital_KeepsHyphenWithoutSpace()
		{
			var builder = new PdfBuilder();
			builder.AddPage().Paragraph(builder.Font("Helvetica"), 11, 72, 700, 14,
				"The flight connects the cities of Paris-",
				"London twice every single day of the week.");

			Assert.Contains("Paris-London", TestPdf.Normalize(Extract(builder)));
		}

		#endregion

		#region Styles and scripts

		/// <summary>Bold and italic fonts are reported on the runs.</summary>
		[Fact]
		public void BoldAndItalic_AreDetected()
		{
			var builder = new PdfBuilder();
			var regular = builder.Font("Times-Roman");
			var bold = builder.Font("Times-Bold");
			var italic = builder.Font("Times-Italic");
			builder.AddPage().Use(regular, bold, italic).Content(
				$"BT /{regular.Name} 12 Tf 72 700 Td (Some ) Tj /{bold.Name} 12 Tf (strong) Tj /{regular.Name} 12 Tf ( and ) Tj /{italic.Name} 12 Tf (slanted) Tj /{regular.Name} 12 Tf ( words.) Tj ET");

			var runs = TestPdf.Runs(TestPdf.Extract(builder));

			Assert.True(runs.Single(r => r.Text.Contains("strong")).IsBold);
			Assert.True(runs.Single(r => r.Text.Contains("slanted")).IsItalic);
			Assert.False(runs.First(r => r.Text.Contains("Some")).IsBold);
		}

		/// <summary>Synthetic bold (fill and stroke, Tr 2) and synthetic italic (skewed matrix) are detected.</summary>
		[Fact]
		public void SyntheticBoldAndItalic_AreDetected()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Use(font).Content(
				$"BT /{font.Name} 12 Tf 72 700 Td (Plain text before ) Tj 2 Tr 0.5 w (fake bold) Tj 0 Tr ( after.) Tj ET\n" +
				$"BT /{font.Name} 12 Tf 1 0 0.3 1 72 650 Tm (fake italic line) Tj ET");

			var runs = TestPdf.Runs(TestPdf.Extract(builder));

			Assert.True(runs.Single(r => r.Text.Contains("fake bold")).IsBold);
			Assert.True(runs.Single(r => r.Text.Contains("fake italic")).IsItalic);
		}

		/// <summary>Raised, smaller text is a superscript.</summary>
		[Fact]
		public void Superscript_IsDetected()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Times-Roman");
			builder.AddPage().Use(font).Content(
				$"BT /{font.Name} 12 Tf 72 700 Td (The energy equals mc) Tj /{font.Name} 7 Tf 5 Ts (2) Tj /{font.Name} 12 Tf 0 Ts ( in this simple example.) Tj ET");

			var content = TestPdf.Extract(builder);
			var superscript = TestPdf.Runs(content).Single(r => r.IsSuperscript);

			Assert.Equal("2", superscript.Text.Trim());
			Assert.Contains("mc<sup>2</sup>", content.ToMarkdown());
		}

		/// <summary>Lowered, smaller text is a subscript.</summary>
		[Fact]
		public void Subscript_IsDetected()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Times-Roman");
			builder.AddPage().Use(font).Content(
				$"BT /{font.Name} 12 Tf 72 700 Td (Water is H) Tj /{font.Name} 7 Tf -3 Ts (2) Tj /{font.Name} 12 Tf 0 Ts (O at room temperature.) Tj ET");

			var subscript = TestPdf.Runs(TestPdf.Extract(builder)).Single(r => r.IsSubscript);

			Assert.Equal("2", subscript.Text.Trim());
		}

		/// <summary>Text colours are reported as 0xRRGGBB.</summary>
		[Fact]
		public void TextColor_IsReported()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Use(font).Content($"BT 0 0 1 rg /{font.Name} 12 Tf 72 700 Td (Blue text) Tj ET");

			Assert.Equal(0x0000FF, TestPdf.Runs(TestPdf.Extract(builder)).Single().Color);
		}

		#endregion

		#region Rotation

		/// <summary>On a page with /Rotate 90 the text reads normally and the page size is swapped.</summary>
		[Fact]
		public void RotatedPage_ReadsNormally()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var page = builder.AddPage(612, 792);
			page.Rotation = 90;

			// text that runs up the unrotated page (+y) runs left to right once the page is turned clockwise.
			page.Use(font).Content($"BT /{font.Name} 12 Tf 0 1 -1 0 100 100 Tm (Upright on the rotated page) Tj ET");
			page.Content($"BT /{font.Name} 12 Tf 0 1 -1 0 120 100 Tm (Second line below it) Tj ET");

			var content = TestPdf.Extract(builder);

			Assert.Equal(792, content.Pages[0].Width);
			Assert.Equal(612, content.Pages[0].Height);
			Assert.Equal("Upright on the rotated page Second line below it", TestPdf.Normalize(content.ToText()));
		}

		/// <summary>Text rotated by 90 degrees on an unrotated page is extracted as a whole line.</summary>
		[Fact]
		public void RotatedText_IsExtracted()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage()
				.Text(font, 12, 72, 700, "Horizontal body text on the page.")
				.Use(font)
				.Content($"BT /{font.Name} 12 Tf 0 1 -1 0 40 300 Tm (Vertical margin note) Tj ET");

			var text = TestPdf.Normalize(Extract(builder));

			Assert.Contains("Horizontal body text on the page.", text);
			Assert.Contains("Vertical margin note", text);
		}

		#endregion

		#region Fonts

		/// <summary>A Type 3 font maps its glyph names to Unicode through /Differences.</summary>
		[Fact]
		public void Type3Font_IsExtracted()
		{
			var builder = new PdfBuilder();
			var square = builder.AddStream(string.Empty, PdfText.ToBytes("1000 0 0 0 750 750 d1 0 0 750 750 re f"));
			var font = builder.Register("Font", builder.AddObject(
				"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] /FontMatrix [0.001 0 0 0.001 0 0] " +
				$"/CharProcs << /H {PdfText.Reference(square)} /i {PdfText.Reference(square)} /exclam {PdfText.Reference(square)} >> " +
				"/Encoding << /Type /Encoding /Differences [33 /exclam 72 /H 105 /i] >> /FirstChar 33 /LastChar 105 " +
				"/Widths [1000 " + string.Join(" ", Enumerable.Repeat("0", 38)) + " 1000 " + string.Join(" ", Enumerable.Repeat("0", 32)) + " 1000] >>"));

			builder.AddPage().Text(font, 12, 72, 700, "Hi!");

			Assert.Equal("Hi!", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>A ToUnicode CMap overrides the font encoding, including multi-character mappings.</summary>
		[Fact]
		public void ToUnicodeCMap_OverridesEncoding()
		{
			var builder = new PdfBuilder();
			var cmap = builder.AddStream(string.Empty, PdfText.ToBytes(
				"begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
				"2 beginbfchar <41> <0058> <42> <00660069> endbfchar 1 beginbfrange <61> <63> <03B1> endbfrange endcmap"));
			var font = builder.Register("Font", builder.AddObject(
				$"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode {PdfText.Reference(cmap)} >>"));

			builder.AddPage().Text(font, 12, 72, 700, "AB abc");

			Assert.Equal("Xfi αβγ", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>A composite (Type 0) font with Identity-H reads two-byte codes, mapped through ToUnicode.</summary>
		[Fact]
		public void Type0Font_IdentityH()
		{
			var builder = new PdfBuilder();
			var cmap = builder.AddStream(string.Empty, PdfText.ToBytes(
				"begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
				"3 beginbfchar <0001> <0048> <0002> <0069> <0003> <4E16> endbfchar endcmap"));
			var descendant = builder.AddObject(
				"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Helvetica /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
				"/FontDescriptor << /Type /FontDescriptor /FontName /Helvetica /Flags 32 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 >> " +
				"/DW 1000 /W [1 [722 222] 3 [1000]] /CIDToGIDMap /Identity >>");
			var font = builder.Register("Font", builder.AddObject(
				$"<< /Type /Font /Subtype /Type0 /BaseFont /Helvetica /Encoding /Identity-H /DescendantFonts [{PdfText.Reference(descendant)}] /ToUnicode {PdfText.Reference(cmap)} >>"));

			builder.AddPage().Use(font).Content($"BT /{font.Name} 12 Tf 72 700 Td <000100020003> Tj ET");

			Assert.Equal("Hi世", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>/ActualText replaces the glyphs of a marked-content sequence.</summary>
		[Fact]
		public void ActualText_ReplacesGlyphs()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Use(font).Content(
				$"BT /{font.Name} 12 Tf 72 700 Td (Before ) Tj /Span << /ActualText {PdfText.TextString("replaced")} >> BDC (xyz) Tj EMC ( after) Tj ET");

			Assert.Equal("Before replaced after", TestPdf.Normalize(Extract(builder)));
		}

		#endregion

		#region Visibility

		/// <summary>Invisible text (Tr 3), such as the OCR layer of a scan, is extracted.</summary>
		[Fact]
		public void InvisibleText_IsIncluded()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Use(font).Content($"BT 3 Tr /{font.Name} 12 Tf 72 700 Td (Hidden OCR layer text) Tj ET");

			Assert.Equal("Hidden OCR layer text", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>Text outside the page, or outside a clipping path, is excluded.</summary>
		[Fact]
		public void ClippedText_IsExcluded()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage()
				.Text(font, 12, 72, 700, "Visible text")
				.Text(font, 12, -500, 700, "Off the left edge")
				.Text(font, 12, 72, 900, "Above the top edge")
				.Use(font)
				.Content($"q 72 500 200 20 re W n BT /{font.Name} 12 Tf 72 300 Td (Outside the clip) Tj ET Q");

			Assert.Equal("Visible text", TestPdf.Normalize(Extract(builder)));
		}

		/// <summary>Text in a form XObject is extracted at the position given by the form matrix.</summary>
		[Fact]
		public void FormXObject_TextIsExtracted()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var form = builder.Form($"BT /{font.Name} 12 Tf 0 0 Td (Text from a form) Tj ET", new double[] { 0, 0, 300, 50 }, new double[] { 1, 0, 0, 1, 0, 10 }, font);

			builder.AddPage()
				.Text(font, 12, 72, 700, "Page text above.")
				.Draw(form, 1, 0, 0, 1, 72, 640);

			Assert.Equal("Page text above. Text from a form", TestPdf.Normalize(Extract(builder)));
		}

		#endregion

		/// <summary>
		/// Extracts the plain text of a generated document.
		/// </summary>
		/// <param name="builder">The document.</param>
		/// <returns>The text.</returns>
		private static string Extract(PdfBuilder builder) => TestPdf.Extract(builder, new PdfExtractionOptions { ExtractImages = false }).ToText();

		/// <summary>
		/// Creates a paragraph that uses "well-known" on one line and breaks it across two lines later.
		/// </summary>
		/// <returns>The document builder.</returns>
		private static PdfBuilder CreateCompoundDocument()
		{
			var builder = new PdfBuilder();
			builder.AddPage().Paragraph(builder.Font("Helvetica"), 11, 72, 700, 14,
				"A well-known method is described here and the",
				"approach is simple. It is a really well-",
				"known method among the experts of the field.");

			return builder;
		}
	}
}
