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

// TrueType/OpenType 'cmap' subtables, formats 0, 2, 4, 6, 10, 12 and 13 (OpenType spec, cmap).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// One decoded subtable of a TrueType <c>cmap</c> table, mapping character codes to glyph ids.
	/// </summary>
	/// <remarks>
	/// Every format is normalized to a sorted list of code ranges. A range maps its codes either by
	/// adding a delta, to a constant glyph, or through an explicit glyph array.
	/// </remarks>
	internal sealed class TrueTypeCmap
	{
		#region Nested types

		/// <summary>
		/// How a range maps its codes to glyph ids.
		/// </summary>
		private enum RangeKind : byte
		{
			/// <summary>
			/// glyph = (code + delta) modulo 65536, as in formats 2 and 4.
			/// </summary>
			Delta16,

			/// <summary>
			/// glyph = startGlyph + (code - start), as in format 12.
			/// </summary>
			Delta,

			/// <summary>
			/// glyph = startGlyph for every code, as in format 13.
			/// </summary>
			Constant,

			/// <summary>
			/// glyph = glyphs[code - start].
			/// </summary>
			Array
		}

		/// <summary>
		/// A contiguous range of character codes.
		/// </summary>
		private struct Range
		{
			/// <summary>
			/// The first code.
			/// </summary>
			public int Start;

			/// <summary>
			/// The last code, inclusive.
			/// </summary>
			public int End;

			/// <summary>
			/// The delta or the start glyph, depending on <see cref="Kind"/>.
			/// </summary>
			public int Value;

			/// <summary>
			/// The mapping method.
			/// </summary>
			public RangeKind Kind;

			/// <summary>
			/// The glyph ids of an <see cref="RangeKind.Array"/> range.
			/// </summary>
			public ushort[] Glyphs;
		}

		#endregion

		#region Fields

		// the largest number of codes materialized into explicit glyph arrays, to bound memory on corrupt tables.
		private const int MaxArrayCodes = 1 << 18;

		// the largest number of groups read from a format 12 or 13 subtable.
		private const int MaxGroups = 1 << 17;

		// the ranges sorted by start code.
		private Range[] _ranges;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes an empty subtable.
		/// </summary>
		/// <param name="platformId">The platform id.</param>
		/// <param name="encodingId">The platform-specific encoding id.</param>
		/// <param name="format">The subtable format.</param>
		private TrueTypeCmap(int platformId, int encodingId, int format)
		{
			this.PlatformId = platformId;
			this.EncodingId = encodingId;
			this.Format = format;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the platform id (0 Unicode, 1 Macintosh, 3 Windows).
		/// </summary>
		public int PlatformId { get; }

		/// <summary>
		/// Returns the platform-specific encoding id.
		/// </summary>
		public int EncodingId { get; }

		/// <summary>
		/// Returns the subtable format.
		/// </summary>
		public int Format { get; }

		/// <summary>
		/// Returns true when the subtable maps no code.
		/// </summary>
		public bool IsEmpty => _ranges.Length == 0;

		#endregion

		#region Methods

		/// <summary>
		/// Parses every subtable of a <c>cmap</c> table.
		/// </summary>
		/// <param name="data">The font data.</param>
		/// <param name="offset">The offset of the cmap table.</param>
		/// <param name="length">The length of the cmap table.</param>
		/// <returns>The subtables that could be decoded, in table order.</returns>
		public static List<TrueTypeCmap> ParseTable(byte[] data, int offset, int length)
		{
			var result = new List<TrueTypeCmap>();
			var end = Math.Min(data.Length, offset + length);
			var count = BigEndian.U16(data, offset + 2);
			for (int i = 0; i < count; i++)
			{
				var record = offset + 4 + i * 8;
				if (record + 8 > end)
					break;

				var platformId = BigEndian.U16(data, record);
				var encodingId = BigEndian.U16(data, record + 2);
				var subOffset = BigEndian.S32(data, record + 4);
				if (subOffset <= 0 || offset + subOffset >= end)
					continue;

				try
				{
					var cmap = Parse(data, offset + subOffset, end, platformId, encodingId);
					if (cmap != null)
						result.Add(cmap);
				}
				catch (Exception)
				{
					// a corrupt subtable is skipped; the others may still be usable.
				}
			}

			return result;
		}

		/// <summary>
		/// Returns the glyph id mapped to <paramref name="code"/>.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <returns>The glyph id, or 0 when the code is not mapped.</returns>
		public int Lookup(int code)
		{
			var ranges = _ranges;
			int lo = 0, hi = ranges.Length - 1;
			while (lo <= hi)
			{
				var mid = (lo + hi) >> 1;
				if (ranges[mid].Start > code)
					hi = mid - 1;
				else if (ranges[mid].End < code)
					lo = mid + 1;
				else
					return Map(ref ranges[mid], code);
			}

			return 0;
		}

		/// <summary>
		/// Calls <paramref name="action"/> for every mapped code with a glyph id below <paramref name="glyphCount"/>.
		/// </summary>
		/// <param name="glyphCount">The number of glyphs in the font, used to stop early on huge ranges.</param>
		/// <param name="action">Receives the code and the glyph id.</param>
		public void ForEach(int glyphCount, Action<int, int> action)
		{
			foreach (var range in _ranges)
			{
				var r = range;
				if (r.Kind == RangeKind.Constant)
				{
					// a many-to-one range: the first code is the representative.
					if (r.Value > 0 && r.Value < glyphCount)
						action(r.Start, r.Value);

					continue;
				}

				for (long code = r.Start; code <= r.End; code++)
				{
					var gid = Map(ref r, (int)code);
					if (gid > 0 && gid < glyphCount)
						action((int)code, gid);
					else if (r.Kind == RangeKind.Delta && gid >= glyphCount)
						break;
				}
			}
		}

		/// <summary>
		/// Returns a short description of the subtable, for debugging.
		/// </summary>
		/// <returns>The description.</returns>
		public override string ToString() => $"cmap ({this.PlatformId},{this.EncodingId}) format {this.Format}, {_ranges.Length} ranges";

		#endregion

		#region Implementation

		// maps a code inside a range.
		private static int Map(ref Range range, int code)
		{
			switch (range.Kind)
			{
				case RangeKind.Delta16:
					return (code + range.Value) & 0xFFFF;

				case RangeKind.Delta:
					return range.Value + (code - range.Start);

				case RangeKind.Constant:
					return range.Value;

				default:
					var index = code - range.Start;
					return index < range.Glyphs.Length ? range.Glyphs[index] : 0;
			}
		}

		// parses one subtable starting at offset; end is the end of the cmap table.
		private static TrueTypeCmap Parse(byte[] data, int offset, int end, int platformId, int encodingId)
		{
			var format = BigEndian.U16(data, offset);
			var cmap = new TrueTypeCmap(platformId, encodingId, format);
			var ranges = new List<Range>();

			switch (format)
			{
				case 0:
					ParseFormat0(data, offset, end, ranges);
					break;

				case 2:
					ParseFormat2(data, offset, end, ranges);
					break;

				case 4:
					ParseFormat4(data, offset, end, ranges);
					break;

				case 6:
					ParseTrimmed(data, offset + 10, end, BigEndian.U16(data, offset + 6), BigEndian.U16(data, offset + 8), ranges);
					break;

				case 10:
					ParseTrimmed(data, offset + 20, end, BigEndian.S32(data, offset + 12), BigEndian.S32(data, offset + 16), ranges);
					break;

				case 12:
				case 13:
					ParseGroups(data, offset, end, format == 13, ranges);
					break;

				default:
					// format 14 (variation sequences) and unknown formats are not character maps.
					return null;
			}

			ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
			cmap._ranges = ranges.ToArray();
			return cmap;
		}

		// format 0: byte encoding table, 256 one-byte glyph ids.
		private static void ParseFormat0(byte[] data, int offset, int end, List<Range> ranges)
		{
			var count = Math.Max(0, Math.Min(256, end - (offset + 6)));
			var glyphs = new ushort[count];
			for (int i = 0; i < count; i++)
				glyphs[i] = (ushort)data[offset + 6 + i];

			if (count > 0)
				ranges.Add(new Range { Start = 0, End = count - 1, Kind = RangeKind.Array, Glyphs = glyphs });
		}

		// format 2: high-byte mapping through sub-headers, for mixed one and two byte CJK encodings.
		private static void ParseFormat2(byte[] data, int offset, int end, List<Range> ranges)
		{
			var keysOffset = offset + 6;
			var headersOffset = keysOffset + 512;
			for (int high = 0; high < 256; high++)
			{
				var key = BigEndian.U16(data, keysOffset + high * 2) / 8;
				var header = headersOffset + key * 8;
				if (header + 8 > end)
					continue;

				var firstCode = BigEndian.U16(data, header);
				var entryCount = BigEndian.U16(data, header + 2);
				var idDelta = BigEndian.S16(data, header + 4);
				var idRangeOffset = BigEndian.U16(data, header + 6);

				// the glyph array is addressed relative to the position of the idRangeOffset field.
				var glyphArray = header + 6 + idRangeOffset;

				if (key == 0)
				{
					// sub-header 0 maps the single-byte codes: only the byte 'high' itself.
					if (high < firstCode || high >= firstCode + entryCount)
						continue;

					var gid = BigEndian.U16(data, glyphArray + (high - firstCode) * 2);
					if (gid != 0)
						gid = (gid + idDelta) & 0xFFFF;

					if (gid != 0)
						ranges.Add(new Range { Start = high, End = high, Kind = RangeKind.Constant, Value = gid });
				}
				else
				{
					entryCount = Math.Min(entryCount, 256 - Math.Min(firstCode, 256));
					if (entryCount <= 0)
						continue;

					var glyphs = new ushort[entryCount];
					for (int i = 0; i < entryCount; i++)
					{
						var gid = BigEndian.U16(data, glyphArray + i * 2);
						if (gid != 0)
							gid = (gid + idDelta) & 0xFFFF;

						glyphs[i] = (ushort)gid;
					}

					var start = (high << 8) | firstCode;
					ranges.Add(new Range { Start = start, End = start + entryCount - 1, Kind = RangeKind.Array, Glyphs = glyphs });
				}
			}
		}

		// format 4: segment mapping to delta values, the standard Windows BMP table.
		private static void ParseFormat4(byte[] data, int offset, int end, List<Range> ranges)
		{
			// some subsetters write a wrong length, so the segment count bounds the arrays rather than the length field.
			var segCount = BigEndian.U16(data, offset + 6) / 2;
			var endCodes = offset + 14;
			var startCodes = endCodes + segCount * 2 + 2;
			var deltas = startCodes + segCount * 2;
			var rangeOffsets = deltas + segCount * 2;
			if (rangeOffsets > end)
				segCount = Math.Max(0, (end - offset - 16) / 8);

			var materialized = 0;
			for (int i = 0; i < segCount; i++)
			{
				var endCode = BigEndian.U16(data, endCodes + i * 2);
				var startCode = BigEndian.U16(data, startCodes + i * 2);
				var delta = BigEndian.S16(data, deltas + i * 2);
				var rangeOffsetPos = rangeOffsets + i * 2;
				var rangeOffset = BigEndian.U16(data, rangeOffsetPos);
				if (startCode > endCode)
					continue;

				// the terminating 0xFFFF segment maps to .notdef and is skipped.
				if (startCode == 0xFFFF)
					continue;

				if (rangeOffset == 0)
				{
					ranges.Add(new Range { Start = startCode, End = endCode, Kind = RangeKind.Delta16, Value = delta });
					continue;
				}

				var count = endCode - startCode + 1;
				if (materialized + count > MaxArrayCodes)
					break;

				materialized += count;
				var glyphs = new ushort[count];
				for (int c = 0; c < count; c++)
				{
					var gid = BigEndian.U16(data, rangeOffsetPos + rangeOffset + c * 2);
					if (gid != 0)
						gid = (gid + delta) & 0xFFFF;

					glyphs[c] = (ushort)gid;
				}

				ranges.Add(new Range { Start = startCode, End = endCode, Kind = RangeKind.Array, Glyphs = glyphs });
			}
		}

		// formats 6 and 10: a trimmed table, a dense glyph array for a range of codes.
		private static void ParseTrimmed(byte[] data, int glyphsOffset, int end, int firstCode, int count, List<Range> ranges)
		{
			count = Math.Min(count, Math.Max(0, (end - glyphsOffset) / 2));
			if (count <= 0 || firstCode < 0 || count > MaxArrayCodes)
				return;

			var glyphs = new ushort[count];
			for (int i = 0; i < count; i++)
				glyphs[i] = (ushort)BigEndian.U16(data, glyphsOffset + i * 2);

			ranges.Add(new Range { Start = firstCode, End = firstCode + count - 1, Kind = RangeKind.Array, Glyphs = glyphs });
		}

		// formats 12 and 13: segmented coverage and many-to-one range mappings, with 32-bit codes.
		private static void ParseGroups(byte[] data, int offset, int end, bool constant, List<Range> ranges)
		{
			var count = Math.Min(BigEndian.S32(data, offset + 12), MaxGroups);
			count = Math.Min(count, Math.Max(0, (end - offset - 16) / 12));
			for (int i = 0; i < count; i++)
			{
				var group = offset + 16 + i * 12;
				var start = BigEndian.S32(data, group);
				var last = BigEndian.S32(data, group + 4);
				var glyph = BigEndian.S32(data, group + 8);
				if (start < 0 || last < start || last > 0x10FFFF || glyph < 0)
					continue;

				ranges.Add(new Range { Start = start, End = last, Kind = constant ? RangeKind.Constant : RangeKind.Delta, Value = glyph });
			}
		}

		#endregion
	}
}
