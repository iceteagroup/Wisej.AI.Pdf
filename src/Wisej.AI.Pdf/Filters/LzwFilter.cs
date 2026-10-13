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

// LZWDecode filter (ISO 32000-2, 7.4.4).

using System.IO;

namespace Wisej.AI.Pdf.Filters
{
	/// <summary>
	/// Decodes data compressed with the LZW method used by TIFF and PDF.
	/// </summary>
	internal static class LzwFilter
	{
		// reserved codes and table size (ISO 32000-2, 7.4.4.2).

		/// <summary>The code that resets the table and the code length to 9 bits.</summary>
		private const int ClearTable = 256;

		/// <summary>The code that marks the end of the data.</summary>
		private const int EndOfData = 257;

		/// <summary>The maximum number of table entries (12-bit codes).</summary>
		private const int MaxCodes = 4096;

		/// <summary>
		/// Decodes LZW-compressed <paramref name="data"/>.
		/// </summary>
		/// <param name="data">The compressed bytes.</param>
		/// <param name="earlyChange">The <c>/EarlyChange</c> parameter: 1 (default) increases the code length one code early.</param>
		/// <returns>The decompressed bytes.</returns>
		public static byte[] Decode(byte[] data, int earlyChange)
		{
			// each table entry is stored as (prefix code, last byte, length) to avoid allocating strings.
			var prefix = new int[MaxCodes];
			var suffix = new byte[MaxCodes];
			var lengths = new int[MaxCodes];
			for (int i = 0; i < 256; i++)
			{
				prefix[i] = -1;
				suffix[i] = (byte)i;
				lengths[i] = 1;
			}

			var output = new MemoryStream(data.Length * 3);
			var scratch = new byte[MaxCodes];

			var next = 258;
			var codeLength = 9;
			var previous = -1;

			int bitBuffer = 0, bitCount = 0, position = 0;
			while (true)
			{
				// read the next code, most significant bit first.
				while (bitCount < codeLength)
				{
					if (position >= data.Length)
						return output.ToArray();

					bitBuffer = ((bitBuffer << 8) | data[position++]) & 0xFFFFFF;
					bitCount += 8;
				}

				var code = (bitBuffer >> (bitCount - codeLength)) & ((1 << codeLength) - 1);
				bitCount -= codeLength;

				if (code == EndOfData)
					break;

				if (code == ClearTable)
				{
					next = 258;
					codeLength = 9;
					previous = -1;
					continue;
				}

				int first;
				if (code < next)
				{
					first = WriteEntry(code, prefix, suffix, lengths, scratch, output);
				}
				else if (code == next && previous >= 0)
				{
					// the KwKwK case: the entry is the previous one followed by its own first byte.
					first = WriteEntry(previous, prefix, suffix, lengths, scratch, output);
					output.WriteByte((byte)first);
				}
				else
				{
					// corrupt data: keep what was decoded.
					break;
				}

				// bound the output against decompression bombs.
				if (output.Length > FilterPipeline.MaxDecodedLength)
					break;

				if (previous >= 0 && next < MaxCodes)
				{
					prefix[next] = previous;
					suffix[next] = (byte)first;
					lengths[next] = lengths[previous] + 1;
					next++;
				}

				previous = code;

				// the code length grows when the table is about to need one more bit.
				var limit = next + earlyChange;
				codeLength = limit >= 2048 ? 12 : limit >= 1024 ? 11 : limit >= 512 ? 10 : 9;
			}

			return output.ToArray();
		}

		/// <summary>
		/// Writes the bytes of a table entry, rebuilding them from the prefix chain.
		/// </summary>
		/// <param name="code">The table entry.</param>
		/// <param name="prefix">The prefix code of each entry, -1 for single bytes.</param>
		/// <param name="suffix">The last byte of each entry.</param>
		/// <param name="lengths">The length in bytes of each entry.</param>
		/// <param name="scratch">A buffer of at least <see cref="MaxCodes"/> bytes used to reverse the chain.</param>
		/// <param name="output">Receives the bytes.</param>
		/// <returns>The first byte of the entry.</returns>
		private static int WriteEntry(int code, int[] prefix, byte[] suffix, int[] lengths, byte[] scratch, MemoryStream output)
		{
			var length = lengths[code];
			for (int i = length - 1, c = code; i >= 0; i--)
			{
				scratch[i] = suffix[c];
				c = prefix[c];
			}

			output.Write(scratch, 0, length);
			return scratch[0];
		}
	}
}
