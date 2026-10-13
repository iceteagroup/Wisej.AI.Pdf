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

// Tests of the stream filters (ISO 32000-2, 7.4): Flate, LZW, ASCIIHex,
// ASCII85 and RunLength, with known vectors and damaged input.

using System;
using System.Linq;
using System.Text;
using Wisej.AI.Pdf.Filters;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Filters
{
	/// <summary>
	/// Tests of the decoders in <c>Wisej.AI.Pdf.Filters</c>.
	/// </summary>
	public class FilterTests
	{
		#region Flate

		/// <summary>Data compressed by the library's zlib encoder decodes to the original bytes.</summary>
		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(1000)]
		[InlineData(250000)]
		public void Flate_RoundTrip(int length)
		{
			var data = CreateData(length);

			var encoded = FlateFilter.EncodeZlib(data);

			Assert.Equal(data, FlateFilter.Decode(encoded));
		}

		/// <summary>The zlib trailer carries the Adler-32 checksum of the data.</summary>
		[Fact]
		public void Flate_EncodeZlib_WritesAdler32()
		{
			var data = Encoding.ASCII.GetBytes("Wikipedia");

			var encoded = FlateFilter.EncodeZlib(data);

			// the Adler-32 of "Wikipedia" is 0x11E60398.
			Assert.Equal(0x11E60398u, FlateFilter.Adler32(data));
			Assert.Equal(new byte[] { 0x11, 0xE6, 0x03, 0x98 }, encoded.Skip(encoded.Length - 4).ToArray());
		}

		/// <summary>A truncated stream returns the data decoded before the cut, without throwing.</summary>
		[Fact]
		public void Flate_Truncated_ReturnsPrefix()
		{
			var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i} of the document\n")));
			var encoded = FlateFilter.EncodeZlib(data);

			var decoded = FlateFilter.Decode(encoded.Take(encoded.Length / 2).ToArray());

			Assert.NotEmpty(decoded);
			Assert.True(decoded.Length < data.Length);
			Assert.Equal(data.Take(decoded.Length), decoded);
		}

		/// <summary>Raw deflate data without the zlib header is accepted.</summary>
		[Fact]
		public void Flate_RawDeflateWithoutHeader_IsDecoded()
		{
			var data = Encoding.ASCII.GetBytes("raw deflate data, as written by some producers");
			var zlib = FlateFilter.EncodeZlib(data);

			// strip the 2-byte header and the 4-byte checksum.
			var raw = zlib.Skip(2).Take(zlib.Length - 6).ToArray();

			Assert.Equal(data, FlateFilter.Decode(raw));
		}

		/// <summary>Garbage that is not deflate data decodes to an empty result instead of throwing.</summary>
		[Fact]
		public void Flate_Garbage_ReturnsEmpty()
		{
			var garbage = Encoding.ASCII.GetBytes("this is not compressed data at all");

			Assert.Empty(FlateFilter.Decode(garbage));
		}

		#endregion

		#region LZW

		/// <summary>The example of the specification: "-----A---B" with EarlyChange 1.</summary>
		[Fact]
		public void Lzw_SpecificationExample()
		{
			var encoded = new byte[] { 0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01 };

			Assert.Equal("-----A---B", Encoding.ASCII.GetString(LzwFilter.Decode(encoded, 1)));
		}

		/// <summary>The reference encoder reproduces the example of the specification.</summary>
		[Fact]
		public void Lzw_ReferenceEncoder_MatchesSpecification()
		{
			Assert.Equal(new byte[] { 0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01 }, Encoders.Lzw(Encoding.ASCII.GetBytes("-----A---B"), 1));
		}

		/// <summary>Data long enough to use 10, 11 and 12-bit codes and a table reset decodes with both EarlyChange values.</summary>
		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		public void Lzw_EarlyChange_RoundTripAcrossCodeWidths(int earlyChange)
		{
			var data = CreateData(60000);

			var encoded = Encoders.Lzw(data, earlyChange);

			Assert.Equal(data, LzwFilter.Decode(encoded, earlyChange));
		}

		/// <summary>Decoding with the wrong EarlyChange value corrupts the data once the code width grows.</summary>
		[Fact]
		public void Lzw_WrongEarlyChange_DiffersFromOriginal()
		{
			var data = CreateData(20000);

			var encoded = Encoders.Lzw(data, 0);

			Assert.NotEqual(data, LzwFilter.Decode(encoded, 1));
		}

		/// <summary>The /EarlyChange decode parameter selects the code width rule.</summary>
		[Fact]
		public void Lzw_DecodeParms_EarlyChangeIsHonored()
		{
			var data = CreateData(5000);
			var stream = TestPdf.ParseStream("/Filter /LZWDecode /DecodeParms << /EarlyChange 0 >>", Encoders.Lzw(data, 0));

			Assert.Equal(data, stream.GetBytes());
		}

		/// <summary>Truncated LZW data returns the bytes decoded so far.</summary>
		[Fact]
		public void Lzw_Truncated_ReturnsPrefix()
		{
			var data = CreateData(5000);
			var encoded = Encoders.Lzw(data);

			var decoded = LzwFilter.Decode(encoded.Take(encoded.Length / 2).ToArray(), 1);

			Assert.NotEmpty(decoded);
			Assert.Equal(data.Take(decoded.Length), decoded);
		}

		#endregion

		#region ASCIIHex, ASCII85 and RunLength

		/// <summary>ASCIIHexDecode ignores whitespace, stops at '&gt;' and pads an odd final digit.</summary>
		[Theory]
		[InlineData("48 65 6C6C\n6F>", "Hello")]
		[InlineData("4142434>", "ABC@")]
		[InlineData("41>42", "A")]
		[InlineData("", "")]
		public void AsciiHex_Decodes(string input, string expected)
		{
			Assert.Equal(expected, Encoding.Latin1.GetString(AsciiHexFilter.Decode(Encoding.ASCII.GetBytes(input))));
		}

		/// <summary>ASCII85Decode handles the "z" shorthand, partial final groups, whitespace and the delimiters.</summary>
		[Theory]
		[InlineData("9jqo^~>", "Man ")]
		[InlineData("<~9jqo^~>", "Man ")]
		[InlineData("9jqo~>", "Man")]
		[InlineData("9jq~>", "Ma")]
		[InlineData("9j~>", "M")]
		[InlineData("z~>", "\0\0\0\0")]
		[InlineData("9jqo^z9jqo^~>", "Man \0\0\0\0Man ")]
		[InlineData("9j qo\n^~>", "Man ")]
		public void Ascii85_Decodes(string input, string expected)
		{
			Assert.Equal(expected, Encoding.Latin1.GetString(Ascii85Filter.Decode(Encoding.ASCII.GetBytes(input))));
		}

		/// <summary>Arbitrary binary data survives an ASCII85 round trip through the reference encoder.</summary>
		[Theory]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		[InlineData(4)]
		[InlineData(999)]
		public void Ascii85_RoundTrip(int length)
		{
			var data = CreateData(length);

			var decoded = Ascii85Filter.Decode(Encoding.ASCII.GetBytes(Encoders.Ascii85(data)));

			Assert.Equal(data, decoded);
		}

		/// <summary>RunLengthDecode copies literal runs, repeats bytes and stops at the end-of-data marker.</summary>
		[Fact]
		public void RunLength_Decodes()
		{
			var encoded = new byte[] { 2, (byte)'a', (byte)'b', (byte)'c', 254, (byte)'x', 0, (byte)'!', 128, 5, 5, 5 };

			Assert.Equal("abcxxx!", Encoding.ASCII.GetString(RunLengthFilter.Decode(encoded)));
		}

		/// <summary>A literal run longer than the remaining data is truncated instead of failing.</summary>
		[Fact]
		public void RunLength_TruncatedRun_ReturnsAvailableBytes()
		{
			var encoded = new byte[] { 10, (byte)'a', (byte)'b' };

			Assert.Equal("ab", Encoding.ASCII.GetString(RunLengthFilter.Decode(encoded)));
		}

		#endregion

		/// <summary>
		/// Creates deterministic test data with repetitions, so that compressors have something to find.
		/// </summary>
		/// <param name="length">The number of bytes.</param>
		/// <returns>The data.</returns>
		internal static byte[] CreateData(int length)
		{
			var random = new Random(length);
			var data = new byte[length];
			for (int i = 0; i < length; i++)
				data[i] = i > 16 && random.Next(3) == 0 ? data[i - random.Next(1, 16)] : (byte)random.Next(256);

			return data;
		}
	}
}
