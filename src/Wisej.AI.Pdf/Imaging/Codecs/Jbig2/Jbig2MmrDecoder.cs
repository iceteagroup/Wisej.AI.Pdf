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

// MMR (ITU T.6 two-dimensional) decoder for JBIG2 generic regions (ITU T.88, 6.2.6).

using System;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Decodes MMR-coded (T.6) bitmaps into packed JBIG2 bitmaps, where 1 is black.
	/// </summary>
	/// <remarks>
	/// The decoder tracks the changing elements of the reference and coding lines (T.4, 4.2.1.3)
	/// and fills the black runs of each finished line into the packed row. Run-length codes are
	/// resolved through 13-bit lookup tables built once from the code lists of T.4 Tables 2 and 3.
	/// </remarks>
	internal static class Jbig2MmrDecoder
	{
		#region Fields

		// two-dimensional coding modes (T.4 Table 4).
		/// <summary>Pass mode (P): b2 lies left of a1.</summary>
		private const int ModePass = 0;

		/// <summary>Horizontal mode (H): two run lengths follow.</summary>
		private const int ModeHorizontal = 1;

		/// <summary>Vertical mode V(0): a1 is directly below b1.</summary>
		private const int ModeVertical0 = 2;

		/// <summary>Vertical mode VR(1): a1 is one pixel right of b1.</summary>
		private const int ModeVerticalR1 = 3;

		/// <summary>Vertical mode VR(2): a1 is two pixels right of b1.</summary>
		private const int ModeVerticalR2 = 4;

		/// <summary>Vertical mode VR(3): a1 is three pixels right of b1.</summary>
		private const int ModeVerticalR3 = 5;

		/// <summary>Vertical mode VL(1): a1 is one pixel left of b1.</summary>
		private const int ModeVerticalL1 = 6;

		/// <summary>Vertical mode VL(2): a1 is two pixels left of b1.</summary>
		private const int ModeVerticalL2 = 7;

		/// <summary>Vertical mode VL(3): a1 is three pixels left of b1.</summary>
		private const int ModeVerticalL3 = 8;

		/// <summary>End of facsimile block (EOFB, two EOL codes).</summary>
		private const int ModeEndOfBlock = 9;

		/// <summary>An unrecognised mode code.</summary>
		private const int ModeInvalid = 10;

		// the number of bits used to index the run-length lookup tables.
		private const int LookupBits = 13;

		// T.4 Table 2: white terminating codes for the runs 0 to 63.
		private static readonly string[] WhiteTerminating =
		{
			"00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111",
			"10011", "10100", "00111", "01000", "001000", "000011", "110100", "110101",
			"101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100",
			"0101000", "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010",
			"00011011", "00010010", "00010011", "00010100", "00010101", "00010110", "00010111", "00101000",
			"00101001", "00101010", "00101011", "00101100", "00101101", "00000100", "00000101", "00001010",
			"00001011", "01010010", "01010011", "01010100", "01010101", "00100100", "00100101", "01011000",
			"01011001", "01011010", "01011011", "01001010", "01001011", "00110010", "00110011", "00110100",
		};

		// T.4 Table 3: white make-up codes for the runs 64 to 1728.
		private static readonly string[] WhiteMakeUp =
		{
			"11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101",
			"01101000", "01100111", "011001100", "011001101", "011010010", "011010011", "011010100", "011010101",
			"011010110", "011010111", "011011000", "011011001", "011011010", "011011011", "010011000", "010011001",
			"010011010", "011000", "010011011",
		};

		// T.4 Table 2: black terminating codes for the runs 0 to 63.
		private static readonly string[] BlackTerminating =
		{
			"0000110111", "010", "11", "10", "011", "0011", "0010", "00011",
			"000101", "000100", "0000100", "0000101", "0000111", "00000100", "00000111", "000011000",
			"0000010111", "0000011000", "0000001000", "00001100111", "00001101000", "00001101100", "00000110111", "00000101000",
			"00000010111", "00000011000", "000011001010", "000011001011", "000011001100", "000011001101", "000001101000", "000001101001",
			"000001101010", "000001101011", "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111",
			"000001101100", "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111",
			"000001100100", "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000", "000000100111",
			"000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010", "000001100110", "000001100111",
		};

		// T.4 Table 3: black make-up codes for the runs 64 to 1728.
		private static readonly string[] BlackMakeUp =
		{
			"0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101", "0000001101100",
			"0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101", "0000001110010", "0000001110011", "0000001110100",
			"0000001110101", "0000001110110", "0000001110111", "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010",
			"0000001011011", "0000001100100", "0000001100101",
		};

		// T.4 Table 3 (extended): make-up codes for the runs 1792 to 2560, shared by both colours.
		private static readonly string[] ExtendedMakeUp =
		{
			"00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101",
			"000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
		};

		// lookup tables indexed by the next 13 bits: (run << 4) | code length, or 0 for an invalid code.
		/// <summary>Run-length lookup table for white runs.</summary>
		private static readonly int[] WhiteLookup = BuildLookup(WhiteTerminating, WhiteMakeUp);

		/// <summary>Run-length lookup table for black runs.</summary>
		private static readonly int[] BlackLookup = BuildLookup(BlackTerminating, BlackMakeUp);

		#endregion

		#region Methods

		/// <summary>
		/// Decodes an MMR-coded bitmap.
		/// </summary>
		/// <param name="reader">The bit reader positioned at the first code; it is left after the data,
		/// including an optional EOFB code.</param>
		/// <param name="bitmap">The cleared bitmap that receives the decoded pixels.</param>
		/// <remarks>Rows after an early end-of-block or invalid code are left white.</remarks>
		public static void Decode(Jbig2BitReader reader, Jbig2Bitmap bitmap)
		{
			var width = bitmap.Width;
			var height = bitmap.Height;
			if (width <= 0 || height <= 0)
				return;

			// changing elements of the reference and coding lines, terminated by sentinels at width.
			var reference = new int[width + 4];
			var coding = new int[width + 4];
			reference[0] = width;
			reference[1] = width;
			var referenceCount = 0;

			var data = bitmap.Data;
			var stride = bitmap.Stride;

			for (var y = 0; y < height; y++)
			{
				var codingCount = 0;
				var a0 = -1;
				var color = 0;
				var bi = 0;

				while (a0 < width)
				{
					var mode = ReadMode(reader);
					if (mode == ModeEndOfBlock || mode == ModeInvalid || reader.IsExhausted)
						return;

					// b1 is the first changing element on the reference line to the right of a0 and of
					// opposite colour to the colour of a0; even entries are white-to-black changes.
					if (bi > 0)
						bi--;

					if ((bi & 1) != color)
						bi++;

					while (bi >= 2 && reference[bi - 2] > a0)
						bi -= 2;

					while (reference[bi] <= a0 && bi < referenceCount)
						bi += 2;

					var b1 = bi < referenceCount ? reference[bi] : width;
					var b2 = bi + 1 < referenceCount ? reference[bi + 1] : width;

					switch (mode)
					{
						case ModePass:
							a0 = b2;
							break;

						case ModeHorizontal:
							{
								var start = a0 < 0 ? 0 : a0;
								var run1 = ReadRun(reader, color == 0 ? WhiteLookup : BlackLookup);
								var run2 = ReadRun(reader, color == 0 ? BlackLookup : WhiteLookup);
								if (run1 < 0 || run2 < 0)
									return;

								var a1 = Math.Min(width, start + run1);
								var a2 = Math.Min(width, a1 + run2);
								AddChange(coding, ref codingCount, a1);
								AddChange(coding, ref codingCount, a2);
								a0 = a2;
								break;
							}

						default:
							{
								var delta = mode <= ModeVerticalR3 ? mode - ModeVertical0 : ModeVerticalR3 - mode;
								var a1 = b1 + delta;

								// keep the coding line monotonic on corrupt data.
								if (a1 < a0)
									a1 = a0 < 0 ? 0 : a0;

								if (a1 > width)
									a1 = width;

								AddChange(coding, ref codingCount, a1);
								color ^= 1;
								a0 = a1;
								break;
							}
					}

					// a pass or a vertical mode at the end of the line ends it.
					if (a0 >= width)
						break;
				}

				// fill the black runs of the coding line.
				var rowOffset = y * stride;
				for (var i = 0; i < codingCount; i += 2)
				{
					var from = coding[i];
					var to = i + 1 < codingCount ? coding[i + 1] : width;
					FillBits(data, rowOffset, from, Math.Min(to, width));
				}

				// the coding line becomes the reference line.
				var swap = reference;
				reference = coding;
				coding = swap;
				referenceCount = codingCount;
				reference[referenceCount] = width;
				reference[referenceCount + 1] = width;
			}

			// consume an optional end-of-block code (T.6, 2.4).
			if (reader.PeekBits(24) == 0x001001)
				reader.Skip(24);
		}

		#endregion

		#region Implementation

		// adds a changing element; a change at the same position as the previous one cancels it.
		private static void AddChange(int[] changes, ref int count, int position)
		{
			if (count > 0 && changes[count - 1] == position)
			{
				count--;
				return;
			}

			if (count < changes.Length - 2)
				changes[count++] = position;
		}

		// sets the bits [from, to) of a packed row.
		private static void FillBits(byte[] data, int rowOffset, int from, int to)
		{
			if (from >= to)
				return;

			var firstByte = from >> 3;
			var lastByte = (to - 1) >> 3;
			var firstMask = 0xFF >> (from & 7);
			var lastMask = (0xFF << (7 - ((to - 1) & 7))) & 0xFF;

			if (firstByte == lastByte)
			{
				data[rowOffset + firstByte] |= (byte)(firstMask & lastMask);
				return;
			}

			data[rowOffset + firstByte] |= (byte)firstMask;
			for (var b = firstByte + 1; b < lastByte; b++)
				data[rowOffset + b] = 0xFF;

			data[rowOffset + lastByte] |= (byte)lastMask;
		}

		// reads a two-dimensional mode code (T.4, Table 4).
		private static int ReadMode(Jbig2BitReader reader)
		{
			var bits = reader.PeekBits(7);
			if ((bits & 0x40) != 0)
			{
				reader.Skip(1);
				return ModeVertical0;
			}

			if ((bits & 0x20) != 0)
			{
				reader.Skip(3);
				switch (bits >> 4)
				{
					case 3:
						return ModeVerticalR1;

					case 2:
						return ModeVerticalL1;

					default:
						return ModeHorizontal;
				}
			}

			if ((bits & 0x10) != 0)
			{
				// 001 is horizontal; the check above handled 01x.
				reader.Skip(3);
				return ModeHorizontal;
			}

			if ((bits & 0x08) != 0)
			{
				reader.Skip(4);
				return ModePass;
			}

			if ((bits & 0x04) != 0)
			{
				reader.Skip(6);
				return (bits & 0x02) != 0 ? ModeVerticalR2 : ModeVerticalL2;
			}

			if ((bits & 0x02) != 0)
			{
				reader.Skip(7);
				return (bits & 0x01) != 0 ? ModeVerticalR3 : ModeVerticalL3;
			}

			// 000000x: either the end-of-block (two EOLs) or an unsupported extension.
			if (reader.PeekBits(12) == 1)
			{
				reader.Skip(12);
				if (reader.PeekBits(12) == 1)
					reader.Skip(12);

				return ModeEndOfBlock;
			}

			return ModeInvalid;
		}

		// reads a run length made of make-up codes followed by a terminating code; -1 on invalid data.
		private static int ReadRun(Jbig2BitReader reader, int[] lookup)
		{
			var total = 0;
			for (var guard = 0; guard < 1024; guard++)
			{
				var entry = lookup[reader.PeekBits(LookupBits)];
				if (entry == 0 || reader.IsExhausted)
					return -1;

				reader.Skip(entry & 0xF);
				var run = entry >> 4;
				total += run;
				if (run < 64)
					return total;
			}

			return -1;
		}

		// builds a lookup table from the terminating and make-up code lists of one colour.
		private static int[] BuildLookup(string[] terminating, string[] makeUp)
		{
			var table = new int[1 << LookupBits];
			for (var i = 0; i < terminating.Length; i++)
				AddCode(table, terminating[i], i);

			for (var i = 0; i < makeUp.Length; i++)
				AddCode(table, makeUp[i], (i + 1) * 64);

			for (var i = 0; i < ExtendedMakeUp.Length; i++)
				AddCode(table, ExtendedMakeUp[i], 1792 + i * 64);

			return table;
		}

		// stores a code in all the table entries that start with it.
		private static void AddCode(int[] table, string code, int run)
		{
			var length = code.Length;
			var value = Convert.ToInt32(code, 2);
			var first = value << (LookupBits - length);
			var count = 1 << (LookupBits - length);
			for (var i = 0; i < count; i++)
				table[first + i] = (run << 4) | length;
		}

		#endregion
	}
}
