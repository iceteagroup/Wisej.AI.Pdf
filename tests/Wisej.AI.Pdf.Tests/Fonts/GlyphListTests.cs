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

// Tests of glyph name to Unicode mapping (Adobe Glyph List specification) and
// of the PDFDocEncoding text strings.

using Wisej.AI.Pdf.Fonts;
using Wisej.AI.Pdf.Objects;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Fonts
{
	/// <summary>
	/// Tests of <see cref="GlyphList"/> and <see cref="Encodings.PdfDocEncoding"/>.
	/// </summary>
	public class GlyphListTests
	{
		/// <summary>Names of the Adobe Glyph List map to their characters.</summary>
		[Theory]
		[InlineData("A", "A")]
		[InlineData("space", " ")]
		[InlineData("Adieresis", "Ä")]
		[InlineData("Euro", "€")]
		[InlineData("fi", "ﬁ")]
		[InlineData("quotedblleft", "“")]
		public void AglNames(string name, string expected)
		{
			Assert.Equal(expected, GlyphList.ToUnicode(name));
		}

		/// <summary>"uniXXXX" names, including sequences of several code units.</summary>
		[Theory]
		[InlineData("uni0041", "A")]
		[InlineData("uni00410042", "AB")]
		[InlineData("uni20AC", "€")]
		[InlineData("uni00e9", "é")]
		public void UniNames(string name, string expected)
		{
			Assert.Equal(expected, GlyphList.ToUnicode(name));
		}

		/// <summary>"uXXXX" to "uXXXXXX" names, including characters outside the BMP.</summary>
		[Theory]
		[InlineData("u0041", "A")]
		[InlineData("u1D49C", "\U0001D49C")]
		[InlineData("u10FFFF", "\U0010FFFF")]
		public void UNames(string name, string expected)
		{
			Assert.Equal(expected, GlyphList.ToUnicode(name));
		}

		/// <summary>Ligature names join the characters of their components.</summary>
		[Theory]
		[InlineData("f_f_i", "ffi")]
		[InlineData("f_l", "fl")]
		[InlineData("T_h", "Th")]
		[InlineData("uni0066_uni0069", "fi")]
		public void Ligatures(string name, string expected)
		{
			Assert.Equal(expected, GlyphList.ToUnicode(name));
		}

		/// <summary>Suffixes after a period are variants of the base glyph: "a.sc" is "a".</summary>
		[Theory]
		[InlineData("a.sc", "a")]
		[InlineData("one.oldstyle", "1")]
		[InlineData("f_i.alt", "fi")]
		[InlineData("uni0041.swash", "A")]
		public void Suffixes(string name, string expected)
		{
			Assert.Equal(expected, GlyphList.ToUnicode(name));
		}

		/// <summary>Names that cannot be mapped return null.</summary>
		[Theory]
		[InlineData(".notdef")]
		[InlineData("")]
		[InlineData(".sc")]
		[InlineData("uniD800")]
		[InlineData("u110000")]
		[InlineData("glyph123")]
		[InlineData("uni004")]
		public void Unmappable_ReturnsNull(string name)
		{
			Assert.Null(GlyphList.ToUnicode(name));
		}

		/// <summary>The ZapfDingbats list is used when requested.</summary>
		[Fact]
		public void Dingbats()
		{
			Assert.Equal("✁", GlyphList.ToUnicode("a1", dingbats: true));
		}

		/// <summary>PDFDocEncoding decodes the bytes that differ from Latin-1.</summary>
		[Fact]
		public void PdfDocEncoding_DecodesSpecialBytes()
		{
			var text = new PdfString(new byte[] { 0x41, 0x80, 0x84, 0x8D, 0x93, 0xA0, 0xE9 }).ToText();

			Assert.Equal("A•—“ﬁ€é", text);
		}

		/// <summary>Text strings with UTF-8 and UTF-16LE byte order marks are decoded too.</summary>
		[Fact]
		public void TextStrings_ByteOrderMarks()
		{
			Assert.Equal("Café", new PdfString(new byte[] { 0xEF, 0xBB, 0xBF, 0x43, 0x61, 0x66, 0xC3, 0xA9 }).ToText());
			Assert.Equal("Hi", new PdfString(new byte[] { 0xFF, 0xFE, 0x48, 0x00, 0x69, 0x00 }).ToText());
		}
	}
}
