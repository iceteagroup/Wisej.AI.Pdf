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

// JPEG coding tables: Huffman (ITU-T T.81, C and K.3), quantization (B.2.4.1, K.1)
// and arithmetic conditioning (B.2.4.3).

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// A Huffman decoding table with a fast lookup for short codes.
	/// </summary>
	internal sealed class JpegHuffmanTable
	{
		#region Fields

		/// <summary>
		/// The number of bits resolved by the fast lookup tables.
		/// </summary>
		public const int FastBits = 9;

		/// <summary>
		/// Fast symbol lookup indexed by the next <see cref="FastBits"/> bits: <c>(length &lt;&lt; 8) | symbol</c>, or 0 when the code is longer.
		/// </summary>
		public readonly ushort[] Fast = new ushort[1 << FastBits];

		/// <summary>
		/// Fast AC lookup indexed by the next <see cref="FastBits"/> bits, resolving the code and its extra bits at once:
		/// <c>(value &lt;&lt; 8) | (run &lt;&lt; 4) | totalLength</c>, or 0 when the pair does not fit.
		/// </summary>
		public readonly short[] FastAc = new short[1 << FastBits];

		/// <summary>
		/// The largest code of each length (index 1-16), or -1 when the length has no codes.
		/// </summary>
		public readonly int[] MaxCode = new int[18];

		/// <summary>
		/// Added to a code of each length to obtain the index of its symbol in <see cref="Values"/>.
		/// </summary>
		public readonly int[] ValueOffset = new int[18];

		/// <summary>
		/// The symbols in code order.
		/// </summary>
		public readonly byte[] Values = new byte[256];

		#endregion

		#region Methods

		/// <summary>
		/// Builds a table from the code-length counts and the symbols of a DHT segment.
		/// </summary>
		/// <param name="counts">The array holding the number of codes of each length 1-16.</param>
		/// <param name="countsOffset">The offset of the 16 counts in <paramref name="counts"/>.</param>
		/// <param name="values">The array holding the symbols.</param>
		/// <param name="valuesOffset">The offset of the first symbol in <paramref name="values"/>.</param>
		/// <param name="valueCount">The number of symbols available.</param>
		/// <returns>The new table.</returns>
		public static JpegHuffmanTable Create(byte[] counts, int countsOffset, byte[] values, int valuesOffset, int valueCount)
		{
			var table = new JpegHuffmanTable();
			if (valueCount > 256)
				valueCount = 256;

			System.Array.Copy(values, valuesOffset, table.Values, 0, valueCount);

			// generate the canonical codes (T.81, C.2) and the fast lookup entries at the same time.
			int code = 0;
			int index = 0;
			for (int length = 1; length <= 16; length++)
			{
				int count = counts[countsOffset + length - 1];
				table.ValueOffset[length] = index - code;

				for (int i = 0; i < count && index < valueCount; i++)
				{
					// an over-subscribed table (more codes than the length allows) is corrupt; ignore the excess codes.
					if (code >= (1 << length))
						break;

					if (length <= FastBits)
						table.AddFast(code, length, table.Values[index]);

					code++;
					index++;
				}

				table.MaxCode[length] = count > 0 ? code - 1 : -1;
				code <<= 1;
			}

			table.MaxCode[17] = int.MaxValue;
			return table;
		}

		/// <summary>
		/// Returns one of the standard tables of T.81 Annex K.3, used when a stream omits its DHT segment.
		/// </summary>
		/// <param name="isAc">True for an AC table, false for a DC table.</param>
		/// <param name="isChrominance">True for the chrominance table, false for the luminance table.</param>
		/// <returns>The new table.</returns>
		public static JpegHuffmanTable CreateDefault(bool isAc, bool isChrominance)
		{
			if (isAc)
			{
				return isChrominance
					? Create(StandardAcChrominanceCounts, 0, StandardAcChrominanceValues, 0, StandardAcChrominanceValues.Length)
					: Create(StandardAcLuminanceCounts, 0, StandardAcLuminanceValues, 0, StandardAcLuminanceValues.Length);
			}

			var dcValues = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
			return isChrominance
				? Create(StandardDcChrominanceCounts, 0, dcValues, 0, dcValues.Length)
				: Create(StandardDcLuminanceCounts, 0, dcValues, 0, dcValues.Length);
		}

		#endregion

		#region Implementation

		// fills the fast lookup entries for one code of up to FastBits bits.
		private void AddFast(int code, int length, int symbol)
		{
			var shift = FastBits - length;
			var first = code << shift;
			var last = first + (1 << shift);
			for (int index = first; index < last; index++)
			{
				this.Fast[index] = (ushort)((length << 8) | symbol);

				// when the magnitude bits that follow an AC code also fit, resolve the coefficient in one step.
				var run = symbol >> 4;
				var size = symbol & 15;
				if (size == 0 || length + size > FastBits)
					continue;

				var bits = (index >> (FastBits - length - size)) & ((1 << size) - 1);
				var value = bits < (1 << (size - 1)) ? bits - (1 << size) + 1 : bits;
				if (value >= -128 && value <= 127)
					this.FastAc[index] = (short)((value * 256) | (run << 4) | (length + size));
			}
		}

		// T.81 Annex K.3, table K.3: luminance DC code lengths.
		private static readonly byte[] StandardDcLuminanceCounts = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };

		// T.81 Annex K.3, table K.4: chrominance DC code lengths.
		private static readonly byte[] StandardDcChrominanceCounts = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };

		// T.81 Annex K.3, table K.5: luminance AC code lengths.
		private static readonly byte[] StandardAcLuminanceCounts = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };

		// T.81 Annex K.3, table K.5: luminance AC symbols.
		private static readonly byte[] StandardAcLuminanceValues =
		{
			0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
			0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
			0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
			0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
			0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
			0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
			0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
			0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
			0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
			0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
			0xf9, 0xfa
		};

		// T.81 Annex K.3, table K.6: chrominance AC code lengths.
		private static readonly byte[] StandardAcChrominanceCounts = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };

		// T.81 Annex K.3, table K.6: chrominance AC symbols.
		private static readonly byte[] StandardAcChrominanceValues =
		{
			0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
			0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
			0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
			0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
			0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
			0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
			0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
			0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
			0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
			0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
			0xf9, 0xfa
		};

		#endregion
	}

	/// <summary>
	/// The coding tables defined so far in a JPEG stream.
	/// </summary>
	internal sealed class JpegTables
	{
		#region Fields

		/// <summary>
		/// Maps the zigzag position of a coefficient to its natural (row-major) index.
		/// Padded with 63 so that corrupt run lengths can never index outside a block.
		/// </summary>
		public static readonly byte[] ZigZag =
		{
			0, 1, 8, 16, 9, 2, 3, 10,
			17, 24, 32, 25, 18, 11, 4, 5,
			12, 19, 26, 33, 40, 48, 41, 34,
			27, 20, 13, 6, 7, 14, 21, 28,
			35, 42, 49, 56, 57, 50, 43, 36,
			29, 22, 15, 23, 30, 37, 44, 51,
			58, 59, 52, 45, 38, 31, 39, 46,
			53, 60, 61, 54, 47, 55, 62, 63,
			63, 63, 63, 63, 63, 63, 63, 63,
			63, 63, 63, 63, 63, 63, 63, 63
		};

		/// <summary>
		/// The DC Huffman tables 0-3; null when not defined.
		/// </summary>
		public readonly JpegHuffmanTable[] DcTables = new JpegHuffmanTable[4];

		/// <summary>
		/// The AC Huffman tables 0-3; null when not defined.
		/// </summary>
		public readonly JpegHuffmanTable[] AcTables = new JpegHuffmanTable[4];

		/// <summary>
		/// The quantization tables 0-3 in natural order; null when not defined.
		/// </summary>
		public readonly ushort[][] QuantTables = new ushort[4][];

		/// <summary>
		/// The arithmetic DC conditioning lower bounds (L), defaulting to 0.
		/// </summary>
		public readonly byte[] ArithmeticDcL = new byte[4];

		/// <summary>
		/// The arithmetic DC conditioning upper bounds (U), defaulting to 1.
		/// </summary>
		public readonly byte[] ArithmeticDcU = { 1, 1, 1, 1 };

		/// <summary>
		/// The arithmetic AC conditioning values (Kx), defaulting to 5.
		/// </summary>
		public readonly byte[] ArithmeticAcK = { 5, 5, 5, 5 };

		/// <summary>
		/// The restart interval in MCUs, 0 when restart markers are not used.
		/// </summary>
		public int RestartInterval;

		#endregion

		#region Methods

		/// <summary>
		/// Returns the standard quantization table of T.81 Annex K.1, used when a stream omits its DQT segment.
		/// </summary>
		/// <param name="isChrominance">True for the chrominance table, false for the luminance table.</param>
		/// <returns>A new table in natural order.</returns>
		public static ushort[] CreateDefaultQuantTable(bool isChrominance)
		{
			var source = isChrominance ? StandardChrominanceQuant : StandardLuminanceQuant;
			var table = new ushort[64];
			for (int i = 0; i < 64; i++)
				table[i] = source[i];

			return table;
		}

		#endregion

		#region Implementation

		// T.81 Annex K.1, table K.1, in natural order.
		private static readonly byte[] StandardLuminanceQuant =
		{
			16, 11, 10, 16, 24, 40, 51, 61,
			12, 12, 14, 19, 26, 58, 60, 55,
			14, 13, 16, 24, 40, 57, 69, 56,
			14, 17, 22, 29, 51, 87, 80, 62,
			18, 22, 37, 56, 68, 109, 103, 77,
			24, 35, 55, 64, 81, 104, 113, 92,
			49, 64, 78, 87, 103, 121, 120, 101,
			72, 92, 95, 98, 112, 100, 103, 99
		};

		// T.81 Annex K.1, table K.2, in natural order.
		private static readonly byte[] StandardChrominanceQuant =
		{
			17, 18, 24, 47, 99, 99, 99, 99,
			18, 21, 26, 66, 99, 99, 99, 99,
			24, 26, 56, 99, 99, 99, 99, 99,
			47, 66, 99, 99, 99, 99, 99, 99,
			99, 99, 99, 99, 99, 99, 99, 99,
			99, 99, 99, 99, 99, 99, 99, 99,
			99, 99, 99, 99, 99, 99, 99, 99,
			99, 99, 99, 99, 99, 99, 99, 99
		};

		#endregion
	}
}
