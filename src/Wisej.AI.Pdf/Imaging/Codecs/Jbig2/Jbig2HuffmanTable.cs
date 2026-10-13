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

// Huffman tables of JBIG2: standard tables B.1 to B.15 and custom table segments (ITU T.88, Annex B).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// A JBIG2 Huffman code table (B.1) with canonical prefix codes assigned as in B.3.
	/// </summary>
	internal sealed class Jbig2HuffmanTable
	{
		#region Nested types

		/// <summary>
		/// The kind of a table line.
		/// </summary>
		private enum LineKind : byte
		{
			/// <summary>A normal line: RANGELOW + offset.</summary>
			Normal,

			/// <summary>The lower range line: RANGELOW - offset (B.2, 32-bit offset).</summary>
			Lower,

			/// <summary>The upper range line: RANGELOW + offset (32-bit offset).</summary>
			Upper,

			/// <summary>The out-of-band line.</summary>
			OutOfBand,
		}

		#endregion

		#region Fields

		/// <summary>
		/// The value returned by <see cref="Decode"/> for the out-of-band symbol and for invalid codes.
		/// </summary>
		public const int Oob = int.MinValue;

		/// <summary>
		/// The longest prefix code supported; longer codes only occur in corrupt tables.
		/// </summary>
		private const int MaxPrefixLength = 32;

		// the standard tables, created on first use.
		private static readonly Jbig2HuffmanTable[] StandardTables = new Jbig2HuffmanTable[16];

		// lock for the creation of the standard tables.
		private static readonly object StandardTablesLock = new object();

		/// <summary>The lowest value of each line (for the lower range line, the value before the first offset).</summary>
		private readonly int[] _rangeLow;

		/// <summary>The number of offset bits of each line.</summary>
		private readonly int[] _rangeLength;

		/// <summary>The kind of each line.</summary>
		private readonly LineKind[] _kinds;

		/// <summary>The first code of each prefix length (B.3 FIRSTCODE).</summary>
		private readonly long[] _firstCode;

		/// <summary>The number of codes of each prefix length.</summary>
		private readonly int[] _lengthCount;

		/// <summary>The line indices sorted by prefix length, then by line order (canonical order).</summary>
		private readonly int[] _sortedLines;

		/// <summary>The index in <see cref="_sortedLines"/> of the first line of each prefix length.</summary>
		private readonly int[] _lengthStart;

		/// <summary>The longest prefix length in the table.</summary>
		private readonly int _maxLength;

		#endregion

		#region Constructors

		/// <summary>
		/// Creates a table from its lines and assigns the prefix codes (B.3).
		/// </summary>
		/// <param name="rangeLow">RANGELOW of each line.</param>
		/// <param name="prefixLength">PREFLEN of each line; lines with 0 have no code.</param>
		/// <param name="rangeLength">RANGELEN of each line.</param>
		/// <param name="kinds">The kind of each line.</param>
		private Jbig2HuffmanTable(int[] rangeLow, int[] prefixLength, int[] rangeLength, LineKind[] kinds)
		{
			_rangeLow = rangeLow;
			_rangeLength = rangeLength;
			_kinds = kinds;

			var count = rangeLow.Length;
			for (var i = 0; i < count; i++)
				_maxLength = Math.Max(_maxLength, prefixLength[i]);

			_lengthCount = new int[_maxLength + 1];
			for (var i = 0; i < count; i++)
			{
				if (prefixLength[i] > 0)
					_lengthCount[prefixLength[i]]++;
			}

			// canonical assignment of B.3: codes of the same length are consecutive in line order.
			_firstCode = new long[_maxLength + 1];
			_lengthStart = new int[_maxLength + 2];
			for (var length = 1; length <= _maxLength; length++)
			{
				_firstCode[length] = (_firstCode[length - 1] + _lengthCount[length - 1]) << 1;
				_lengthStart[length + 1] = _lengthStart[length] + _lengthCount[length];
			}

			_sortedLines = new int[_lengthStart[_maxLength + 1]];
			var fill = (int[])_lengthStart.Clone();
			for (var i = 0; i < count; i++)
			{
				if (prefixLength[i] > 0)
					_sortedLines[fill[prefixLength[i]]++] = i;
			}
		}

		#endregion

		#region Methods

		/// <summary>
		/// Returns the standard table B.<paramref name="number"/>.
		/// </summary>
		/// <param name="number">The table number, 1 to 15.</param>
		/// <returns>The table.</returns>
		public static Jbig2HuffmanTable GetStandardTable(int number)
		{
			lock (StandardTablesLock)
			{
				return StandardTables[number] ?? (StandardTables[number] = CreateStandardTable(number));
			}
		}

		/// <summary>
		/// Creates a table with the given code lengths for the values 0 to n - 1, as used by the
		/// symbol ID tables of text regions (7.4.3.1.7).
		/// </summary>
		/// <param name="codeLengths">The prefix length of each value.</param>
		/// <returns>The table.</returns>
		public static Jbig2HuffmanTable FromCodeLengths(int[] codeLengths)
		{
			var count = codeLengths.Length;
			var rangeLow = new int[count];
			var kinds = new LineKind[count];
			for (var i = 0; i < count; i++)
				rangeLow[i] = i;

			return new Jbig2HuffmanTable(rangeLow, codeLengths, new int[count], kinds);
		}

		/// <summary>
		/// Parses a custom table from the data of a tables segment (B.2).
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="start">The offset of the segment data.</param>
		/// <param name="end">The end of the segment data.</param>
		/// <returns>The table, or null when the data is invalid.</returns>
		public static Jbig2HuffmanTable Parse(byte[] data, int start, int end)
		{
			if (end - start < 9)
				return null;

			var flags = data[start];
			var hasOob = (flags & 1) != 0;
			var prefixBits = ((flags >> 1) & 7) + 1;
			var rangeBits = ((flags >> 4) & 7) + 1;
			var low = ReadInt32(data, start + 1);
			var high = ReadInt32(data, start + 5);
			if (high < low)
				return null;

			var reader = new Jbig2BitReader(data, start + 9, end);
			var rangeLows = new List<int>();
			var prefixLengths = new List<int>();
			var rangeLengths = new List<int>();
			var kinds = new List<LineKind>();

			// the normal lines cover [low, high) one range at a time.
			long current = low;
			while (current < high)
			{
				if (reader.IsExhausted || rangeLows.Count > 65536)
					return null;

				var prefixLength = (int)reader.ReadBits(prefixBits);
				var rangeLength = (int)reader.ReadBits(rangeBits);
				if (rangeLength > 31)
					return null;

				rangeLows.Add((int)current);
				prefixLengths.Add(prefixLength);
				rangeLengths.Add(rangeLength);
				kinds.Add(LineKind.Normal);
				current += 1L << rangeLength;
			}

			// the lower range line codes the values below low.
			rangeLows.Add(low - 1);
			prefixLengths.Add((int)reader.ReadBits(prefixBits));
			rangeLengths.Add(32);
			kinds.Add(LineKind.Lower);

			// the upper range line codes the values from high up.
			rangeLows.Add(high);
			prefixLengths.Add((int)reader.ReadBits(prefixBits));
			rangeLengths.Add(32);
			kinds.Add(LineKind.Upper);

			if (hasOob)
			{
				rangeLows.Add(0);
				prefixLengths.Add((int)reader.ReadBits(prefixBits));
				rangeLengths.Add(0);
				kinds.Add(LineKind.OutOfBand);
			}

			var lengths = prefixLengths.ToArray();
			for (var i = 0; i < lengths.Length; i++)
			{
				if (lengths[i] > MaxPrefixLength)
					return null;
			}

			return new Jbig2HuffmanTable(rangeLows.ToArray(), lengths, rangeLengths.ToArray(), kinds.ToArray());
		}

		/// <summary>
		/// Decodes one value.
		/// </summary>
		/// <param name="reader">The bit reader.</param>
		/// <returns>The value, or <see cref="Oob"/> for the out-of-band symbol or an invalid code.</returns>
		public int Decode(Jbig2BitReader reader)
		{
			long code = 0;
			for (var length = 1; length <= _maxLength; length++)
			{
				code = (code << 1) | (uint)reader.ReadBit();
				var index = code - _firstCode[length];
				if (index >= 0 && index < _lengthCount[length])
				{
					var line = _sortedLines[_lengthStart[length] + (int)index];
					switch (_kinds[line])
					{
						case LineKind.OutOfBand:
							return Oob;

						case LineKind.Lower:
							return Clamp((long)_rangeLow[line] - reader.ReadBits(32));

						case LineKind.Upper:
							return Clamp((long)_rangeLow[line] + reader.ReadBits(32));

						default:
							return Clamp((long)_rangeLow[line] + reader.ReadBits(_rangeLength[line]));
					}
				}
			}

			// no code matched: the data is corrupt.
			return Oob;
		}

		#endregion

		#region Implementation

		// clamps a decoded value into the 32-bit range, avoiding the OOB sentinel.
		private static int Clamp(long value)
		{
			if (value > int.MaxValue)
				return int.MaxValue;

			if (value <= int.MinValue)
				return int.MinValue + 1;

			return (int)value;
		}

		// reads a big-endian signed 32-bit integer.
		private static int ReadInt32(byte[] data, int offset)
		{
			return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
		}

		// creates a standard table from Annex B.5. Each line is { RANGELOW, PREFLEN, RANGELEN };
		// the lower and upper range lines and the OOB line are flagged separately.
		private static Jbig2HuffmanTable CreateStandardTable(int number)
		{
			int[,] lines;
			var lower = -1;
			var upper = -1;
			var oob = -1;

			switch (number)
			{
				case 1:
					lines = new[,] { { 0, 1, 4 }, { 16, 2, 8 }, { 272, 3, 16 }, { 65808, 3, 32 } };
					upper = 3;
					break;

				case 2:
					lines = new[,] { { 0, 1, 0 }, { 1, 2, 0 }, { 2, 3, 0 }, { 3, 4, 3 }, { 11, 5, 6 }, { 75, 6, 32 }, { 0, 6, 0 } };
					upper = 5;
					oob = 6;
					break;

				case 3:
					lines = new[,] { { -256, 8, 8 }, { 0, 1, 0 }, { 1, 2, 0 }, { 2, 3, 0 }, { 3, 4, 3 }, { 11, 5, 6 }, { -257, 8, 32 }, { 75, 7, 32 }, { 0, 6, 0 } };
					lower = 6;
					upper = 7;
					oob = 8;
					break;

				case 4:
					lines = new[,] { { 1, 1, 0 }, { 2, 2, 0 }, { 3, 3, 0 }, { 4, 4, 3 }, { 12, 5, 6 }, { 76, 5, 32 } };
					upper = 5;
					break;

				case 5:
					lines = new[,] { { -255, 7, 8 }, { 1, 1, 0 }, { 2, 2, 0 }, { 3, 3, 0 }, { 4, 4, 3 }, { 12, 5, 6 }, { -256, 7, 32 }, { 76, 6, 32 } };
					lower = 6;
					upper = 7;
					break;

				case 6:
					lines = new[,]
					{
						{ -2048, 5, 10 }, { -1024, 4, 9 }, { -512, 4, 8 }, { -256, 4, 7 }, { -128, 5, 6 }, { -64, 5, 5 }, { -32, 4, 5 },
						{ 0, 2, 7 }, { 128, 3, 7 }, { 256, 3, 8 }, { 512, 4, 9 }, { 1024, 4, 10 }, { -2049, 6, 32 }, { 2048, 6, 32 },
					};
					lower = 12;
					upper = 13;
					break;

				case 7:
					lines = new[,]
					{
						{ -1024, 4, 9 }, { -512, 3, 8 }, { -256, 4, 7 }, { -128, 5, 6 }, { -64, 5, 5 }, { -32, 4, 5 }, { 0, 4, 5 },
						{ 32, 5, 5 }, { 64, 5, 6 }, { 128, 4, 7 }, { 256, 3, 8 }, { 512, 3, 9 }, { 1024, 3, 10 }, { -1025, 5, 32 },
						{ 2048, 5, 32 },
					};
					lower = 13;
					upper = 14;
					break;

				case 8:
					lines = new[,]
					{
						{ -15, 8, 3 }, { -7, 9, 1 }, { -5, 8, 1 }, { -3, 9, 0 }, { -2, 7, 0 }, { -1, 4, 0 }, { 0, 2, 1 }, { 2, 5, 0 },
						{ 3, 6, 0 }, { 4, 3, 4 }, { 20, 6, 1 }, { 22, 4, 4 }, { 38, 4, 5 }, { 70, 5, 6 }, { 134, 5, 7 }, { 262, 6, 7 },
						{ 390, 7, 8 }, { 646, 6, 10 }, { -16, 9, 32 }, { 1670, 9, 32 }, { 0, 2, 0 },
					};
					lower = 18;
					upper = 19;
					oob = 20;
					break;

				case 9:
					lines = new[,]
					{
						{ -31, 8, 4 }, { -15, 9, 2 }, { -11, 8, 2 }, { -7, 9, 1 }, { -5, 7, 1 }, { -3, 4, 1 }, { -1, 3, 1 }, { 1, 3, 1 },
						{ 3, 5, 1 }, { 5, 6, 1 }, { 7, 3, 5 }, { 39, 6, 2 }, { 43, 4, 5 }, { 75, 4, 6 }, { 139, 5, 7 }, { 267, 5, 8 },
						{ 523, 6, 8 }, { 779, 7, 9 }, { 1291, 6, 11 }, { -32, 9, 32 }, { 3339, 9, 32 }, { 0, 2, 0 },
					};
					lower = 19;
					upper = 20;
					oob = 21;
					break;

				case 10:
					lines = new[,]
					{
						{ -21, 7, 4 }, { -5, 8, 0 }, { -4, 7, 0 }, { -3, 5, 0 }, { -2, 2, 2 }, { 2, 5, 0 }, { 3, 6, 0 }, { 4, 7, 0 },
						{ 5, 8, 0 }, { 6, 2, 6 }, { 70, 5, 5 }, { 102, 6, 5 }, { 134, 6, 6 }, { 198, 6, 7 }, { 326, 6, 8 }, { 582, 6, 9 },
						{ 1094, 6, 10 }, { 2118, 7, 11 }, { -22, 8, 32 }, { 4166, 8, 32 }, { 0, 2, 0 },
					};
					lower = 18;
					upper = 19;
					oob = 20;
					break;

				case 11:
					lines = new[,]
					{
						{ 1, 1, 0 }, { 2, 2, 1 }, { 4, 4, 0 }, { 5, 4, 1 }, { 7, 5, 1 }, { 9, 5, 2 }, { 13, 6, 2 }, { 17, 7, 2 },
						{ 21, 7, 3 }, { 29, 7, 4 }, { 45, 7, 5 }, { 77, 7, 6 }, { 141, 7, 32 },
					};
					upper = 12;
					break;

				case 12:
					lines = new[,]
					{
						{ 1, 1, 0 }, { 2, 2, 0 }, { 3, 3, 1 }, { 5, 5, 0 }, { 6, 5, 1 }, { 8, 6, 1 }, { 10, 7, 0 }, { 11, 7, 1 },
						{ 13, 7, 2 }, { 17, 7, 3 }, { 25, 7, 4 }, { 41, 8, 5 }, { 73, 8, 32 },
					};
					upper = 12;
					break;

				case 13:
					lines = new[,]
					{
						{ 1, 1, 0 }, { 2, 3, 0 }, { 3, 4, 0 }, { 4, 5, 0 }, { 5, 4, 1 }, { 7, 3, 3 }, { 15, 6, 1 }, { 17, 6, 2 },
						{ 21, 6, 3 }, { 29, 6, 4 }, { 45, 6, 5 }, { 77, 7, 6 }, { 141, 7, 32 },
					};
					upper = 12;
					break;

				case 14:
					lines = new[,] { { -2, 3, 0 }, { -1, 3, 0 }, { 0, 1, 0 }, { 1, 3, 0 }, { 2, 3, 0 } };
					break;

				case 15:
					lines = new[,]
					{
						{ -24, 7, 4 }, { -8, 6, 2 }, { -4, 5, 1 }, { -2, 4, 0 }, { -1, 3, 0 }, { 0, 1, 0 }, { 1, 3, 0 }, { 2, 4, 0 },
						{ 3, 5, 1 }, { 5, 6, 2 }, { 9, 7, 4 }, { -25, 7, 32 }, { 25, 7, 32 },
					};
					lower = 11;
					upper = 12;
					break;

				default:
					throw new ArgumentOutOfRangeException(nameof(number));
			}

			var count = lines.GetLength(0);
			var rangeLow = new int[count];
			var prefixLength = new int[count];
			var rangeLength = new int[count];
			var kinds = new LineKind[count];
			for (var i = 0; i < count; i++)
			{
				rangeLow[i] = lines[i, 0];
				prefixLength[i] = lines[i, 1];
				rangeLength[i] = lines[i, 2];
				kinds[i] = i == lower ? LineKind.Lower : i == upper ? LineKind.Upper : i == oob ? LineKind.OutOfBand : LineKind.Normal;
			}

			return new Jbig2HuffmanTable(rangeLow, prefixLength, rangeLength, kinds);
		}

		#endregion
	}
}
