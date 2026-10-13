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

// Tests of CMap parsing (ISO 32000-2, 9.10.3): ToUnicode bfchar and bfrange
// (including the array form), codespace ranges and predefined CMaps.

using Wisej.AI.Pdf.Fonts;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Fonts
{
	/// <summary>
	/// Tests of <see cref="CMap"/>.
	/// </summary>
	public class CMapTests
	{
		// a ToUnicode CMap with one-byte codes, as written by most producers.
		private const string OneByteToUnicode = @"/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
/CMapName /Adobe-Identity-UCS def
/CMapType 2 def
1 begincodespacerange
<00> <FF>
endcodespacerange
3 beginbfchar
<01> <0041>
<02> <00660069>
<03> <D835DC9C>
endbfchar
3 beginbfrange
<10> <12> <0061>
<20> <22> [<03B1> <0042> <00660066>]
<30> <31> <00FE>
endbfrange
endcmap
CMapName currentdict /CMap defineresource pop
end
end";

		/// <summary>bfchar maps single codes, including ligatures and surrogate pairs.</summary>
		[Theory]
		[InlineData(0x01, "A")]
		[InlineData(0x02, "fi")]
		[InlineData(0x03, "\U0001D49C")]
		public void BfChar_MapsCodes(int code, string expected)
		{
			var cmap = CMap.Parse(PdfText.ToBytes(OneByteToUnicode));

			Assert.True(cmap.TryGetUnicode(code, out var text));
			Assert.Equal(expected, text);
		}

		/// <summary>bfrange with a destination string increments the last byte for each code.</summary>
		[Theory]
		[InlineData(0x10, "a")]
		[InlineData(0x11, "b")]
		[InlineData(0x12, "c")]
		[InlineData(0x30, "þ")]
		[InlineData(0x31, "ÿ")]
		public void BfRange_IncrementsDestination(int code, string expected)
		{
			var cmap = CMap.Parse(PdfText.ToBytes(OneByteToUnicode));

			Assert.True(cmap.TryGetUnicode(code, out var text));
			Assert.Equal(expected, text);
		}

		/// <summary>bfrange with an array of destinations maps each code to its own string.</summary>
		[Theory]
		[InlineData(0x20, "α")]
		[InlineData(0x21, "B")]
		[InlineData(0x22, "ff")]
		public void BfRange_ArrayForm(int code, string expected)
		{
			var cmap = CMap.Parse(PdfText.ToBytes(OneByteToUnicode));

			Assert.True(cmap.TryGetUnicode(code, out var text));
			Assert.Equal(expected, text);
		}

		/// <summary>Codes without a mapping report false.</summary>
		[Fact]
		public void UnmappedCode_ReturnsFalse()
		{
			var cmap = CMap.Parse(PdfText.ToBytes(OneByteToUnicode));

			Assert.False(cmap.TryGetUnicode(0x7F, out _));
			Assert.True(cmap.HasUnicodeMappings);
		}

		/// <summary>Two-byte codespace ranges read two-byte codes; one-byte ranges read one byte.</summary>
		[Fact]
		public void Codespace_MixedLengths_ReadCodes()
		{
			var cmap = CMap.Parse(PdfText.ToBytes(@"begincmap
2 begincodespacerange
<00> <7F>
<8000> <FFFF>
endcodespacerange
1 beginbfchar
<8141> <3042>
endbfchar
endcmap"));

			var bytes = new byte[] { 0x41, 0x81, 0x41 };

			Assert.Equal(0x41, cmap.ReadCode(bytes, 0, out var first));
			Assert.Equal(1, first);
			Assert.Equal(0x8141, cmap.ReadCode(bytes, 1, out var second));
			Assert.Equal(2, second);

			Assert.True(cmap.TryGetUnicode(0x8141, out var text));
			Assert.Equal("あ", text);
		}

		/// <summary>cidrange and cidchar map codes to CIDs.</summary>
		[Fact]
		public void CidRangeAndCidChar_MapToCids()
		{
			var cmap = CMap.Parse(PdfText.ToBytes(@"begincmap
1 begincodespacerange
<0000> <FFFF>
endcodespacerange
1 begincidrange
<0020> <007E> 1
endcidrange
1 begincidchar
<3000> 633
endcidchar
endcmap"));

			Assert.Equal(1, cmap.GetCid(0x20, 2));
			Assert.Equal(34, cmap.GetCid(0x41, 2));
			Assert.Equal(633, cmap.GetCid(0x3000, 2));
		}

		/// <summary>The predefined Identity-H CMap maps two-byte codes to the same CID.</summary>
		[Fact]
		public void IdentityH_IsIdentity()
		{
			var cmap = CMap.GetPredefined("Identity-H");

			Assert.NotNull(cmap);
			Assert.True(cmap.IsIdentity);
			Assert.Equal(0, cmap.WMode);
			Assert.Equal(0x1234, cmap.ReadCode(new byte[] { 0x12, 0x34 }, 0, out var length));
			Assert.Equal(2, length);
			Assert.Equal(0x1234, cmap.GetCid(0x1234));
		}

		/// <summary>Garbage input produces an empty CMap rather than an exception.</summary>
		[Fact]
		public void Garbage_DoesNotThrow()
		{
			var cmap = CMap.Parse(PdfText.ToBytes("begincmap 3 beginbfrange <01> [ <02 endbfrange <<>> ] ] endcmap"));

			Assert.NotNull(cmap);
			Assert.False(cmap.TryGetUnicode(0x05, out _));
		}
	}
}
