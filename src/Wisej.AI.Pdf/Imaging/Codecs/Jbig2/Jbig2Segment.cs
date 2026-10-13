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

// JBIG2 segment header parsing (ITU T.88, 7.2).

using System;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// A segment of a JBIG2 stream: its header fields and the location of its data.
	/// </summary>
	internal sealed class Jbig2Segment
	{
		#region Fields

		/// <summary>Segment type: symbol dictionary.</summary>
		public const int SymbolDictionary = 0;

		/// <summary>Segment type: intermediate text region.</summary>
		public const int IntermediateTextRegion = 4;

		/// <summary>Segment type: immediate text region.</summary>
		public const int ImmediateTextRegion = 6;

		/// <summary>Segment type: immediate lossless text region.</summary>
		public const int ImmediateLosslessTextRegion = 7;

		/// <summary>Segment type: pattern dictionary.</summary>
		public const int PatternDictionary = 16;

		/// <summary>Segment type: intermediate halftone region.</summary>
		public const int IntermediateHalftoneRegion = 20;

		/// <summary>Segment type: immediate halftone region.</summary>
		public const int ImmediateHalftoneRegion = 22;

		/// <summary>Segment type: immediate lossless halftone region.</summary>
		public const int ImmediateLosslessHalftoneRegion = 23;

		/// <summary>Segment type: intermediate generic region.</summary>
		public const int IntermediateGenericRegion = 36;

		/// <summary>Segment type: immediate generic region.</summary>
		public const int ImmediateGenericRegion = 38;

		/// <summary>Segment type: immediate lossless generic region.</summary>
		public const int ImmediateLosslessGenericRegion = 39;

		/// <summary>Segment type: intermediate generic refinement region.</summary>
		public const int IntermediateRefinementRegion = 40;

		/// <summary>Segment type: immediate generic refinement region.</summary>
		public const int ImmediateRefinementRegion = 42;

		/// <summary>Segment type: immediate lossless generic refinement region.</summary>
		public const int ImmediateLosslessRefinementRegion = 43;

		/// <summary>Segment type: page information.</summary>
		public const int PageInformation = 48;

		/// <summary>Segment type: end of page.</summary>
		public const int EndOfPage = 49;

		/// <summary>Segment type: end of stripe.</summary>
		public const int EndOfStripe = 50;

		/// <summary>Segment type: end of file.</summary>
		public const int EndOfFile = 51;

		/// <summary>Segment type: tables.</summary>
		public const int Tables = 53;

		// the largest number of referred-to segments accepted in a header.
		private const int MaxReferredSegments = 1 << 16;

		// the size of the region segment information field (7.4.1).
		private const int RegionInfoSize = 17;

		/// <summary>The offset after the segment, including the row count trailer of an unknown-length segment.</summary>
		private int _next;

		#endregion

		#region Properties

		/// <summary>
		/// Returns the segment number.
		/// </summary>
		public uint Number { get; private set; }

		/// <summary>
		/// Returns the segment type, 0 to 63.
		/// </summary>
		public int Type { get; private set; }

		/// <summary>
		/// Returns the numbers of the referred-to segments.
		/// </summary>
		public uint[] ReferredTo { get; private set; }

		/// <summary>
		/// Returns the offset of the segment data.
		/// </summary>
		public int DataStart { get; private set; }

		/// <summary>
		/// Returns the offset after the segment data, clipped to the available bytes.
		/// </summary>
		public int DataEnd { get; private set; }

		/// <summary>
		/// Returns the row count found at the end of an immediate generic region whose data length was
		/// unknown (7.2.7), or -1.
		/// </summary>
		public long RowCount { get; private set; } = -1;

		#endregion

		#region Methods

		/// <summary>
		/// Reads the segment header at <paramref name="position"/> and locates the segment data.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="position">The offset of the header; on return, the offset after the segment data.</param>
		/// <param name="end">The end of the buffer range.</param>
		/// <returns>The segment, or null when the header is truncated or invalid.</returns>
		public static Jbig2Segment Read(byte[] data, ref int position, int end)
		{
			var pos = position;
			if (end - pos < 11)
				return null;

			var segment = new Jbig2Segment();
			segment.Number = ReadUInt32(data, pos);
			var flags = data[pos + 4];
			segment.Type = flags & 0x3F;
			var largePageAssociation = (flags & 0x40) != 0;
			pos += 5;

			// 7.2.4: the referred-to segment count, in the short or the long form.
			var countByte = data[pos];
			long referredCount = countByte >> 5;
			if (referredCount == 7)
			{
				referredCount = ReadUInt32(data, pos) & 0x1FFFFFFF;
				if (referredCount > MaxReferredSegments)
					return null;

				// the retention flags take one bit per referred-to segment plus one.
				pos += 4 + (int)((referredCount + 8) >> 3);
			}
			else if (referredCount > 4)
			{
				return null;
			}
			else
			{
				pos++;
			}

			// 7.2.5: the referred-to segment numbers are as wide as needed for this segment's number.
			var numberSize = segment.Number <= 256 ? 1 : segment.Number <= 65536 ? 2 : 4;
			if (pos + referredCount * numberSize + (largePageAssociation ? 4 : 1) + 4 > end)
				return null;

			var referred = new uint[referredCount];
			for (var i = 0; i < referredCount; i++)
			{
				if (numberSize == 1)
					referred[i] = data[pos];
				else if (numberSize == 2)
					referred[i] = (uint)((data[pos] << 8) | data[pos + 1]);
				else
					referred[i] = ReadUInt32(data, pos);

				pos += numberSize;
			}

			segment.ReferredTo = referred;

			// 7.2.6: the page association, ignored because a PDF stream holds a single page.
			pos += largePageAssociation ? 4 : 1;

			// 7.2.7: the data length.
			var length = ReadUInt32(data, pos);
			pos += 4;

			segment.DataStart = pos;
			if (length == 0xFFFFFFFF)
			{
				if (!segment.FindGenericRegionEnd(data, end))
					return null;
			}
			else
			{
				segment.DataEnd = (int)Math.Min((long)pos + length, end);
				segment._next = segment.DataEnd;
			}

			position = segment._next;
			return segment;
		}

		/// <summary>
		/// Reads a big-endian unsigned 32-bit integer.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="offset">The offset.</param>
		/// <returns>The value.</returns>
		public static uint ReadUInt32(byte[] data, int offset)
		{
			return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
		}

		#endregion

		#region Implementation

		// 7.2.7: an immediate generic region of unknown length ends with 0xFF 0xAC (arithmetic) or
		// 0x00 0x00 (MMR), followed by the 32-bit number of rows actually coded.
		private bool FindGenericRegionEnd(byte[] data, int end)
		{
			if (this.Type != ImmediateGenericRegion && this.Type != ImmediateLosslessGenericRegion)
				return false;

			var flagsOffset = this.DataStart + RegionInfoSize;
			if (flagsOffset >= end)
				return false;

			var mmr = (data[flagsOffset] & 1) != 0;
			var height = ReadUInt32(data, this.DataStart + 4);
			var marker1 = mmr ? 0x00 : 0xFF;
			var marker2 = mmr ? 0x00 : 0xAC;

			for (var i = flagsOffset + 1; i + 6 <= end; i++)
			{
				if (data[i] != marker1 || data[i + 1] != marker2)
					continue;

				var rows = ReadUInt32(data, i + 2);
				if (rows > height)
					continue;

				this.DataEnd = i;
				this.RowCount = rows;

				// the marker and the row count are part of the segment data.
				_next = i + 6;
				return true;
			}

			return false;
		}

		#endregion
	}
}
