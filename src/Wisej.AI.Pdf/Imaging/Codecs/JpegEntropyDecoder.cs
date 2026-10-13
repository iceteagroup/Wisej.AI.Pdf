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

// Base class of the JPEG entropy decoders: MCU iteration, restart intervals and resynchronization
// (ITU-T T.81, A.2, B.2.3, E.2.4 and F.2.2.6).

using System;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Decodes the entropy-coded segments of one scan into the frame buffers.
	/// </summary>
	/// <remarks>
	/// The base class walks the MCUs (or the blocks of a non-interleaved scan) and handles the restart
	/// markers: damaged intervals are abandoned at the next RSTn marker, and the marker number is used
	/// to skip intervals whose data is missing altogether, so a corrupt region doesn't shift the rest of the image.
	/// </remarks>
	internal abstract class JpegEntropyDecoder
	{
		#region Fields

		/// <summary>
		/// The JPEG stream.
		/// </summary>
		protected readonly byte[] _data;

		/// <summary>
		/// The end of the usable data in <see cref="_data"/>.
		/// </summary>
		protected readonly int _end;

		/// <summary>
		/// The position of the next entropy-coded byte; when <see cref="_marker"/> is set, the position of that marker.
		/// </summary>
		protected int _position;

		/// <summary>
		/// The code of the marker that stopped the entropy-coded data, or -1.
		/// </summary>
		protected int _marker = -1;

		/// <summary>
		/// The frame being decoded.
		/// </summary>
		protected JpegFrame _frame;

		/// <summary>
		/// The coding tables.
		/// </summary>
		protected JpegTables _tables;

		/// <summary>
		/// The current scan.
		/// </summary>
		protected JpegScan _scan;

		/// <summary>
		/// Set by the decoders when the data is invalid and the rest of the restart interval can't be trusted.
		/// </summary>
		protected bool _corrupt;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new instance of the <see cref="JpegEntropyDecoder"/> class.
		/// </summary>
		/// <param name="data">The JPEG stream.</param>
		/// <param name="end">The end of the usable data.</param>
		protected JpegEntropyDecoder(byte[] data, int end)
		{
			_data = data;
			_end = end;
		}

		#endregion

		#region Methods

		/// <summary>
		/// Decodes the entropy-coded data of <paramref name="scan"/> starting at <paramref name="position"/>.
		/// </summary>
		/// <param name="frame">The frame, already allocated.</param>
		/// <param name="tables">The coding tables.</param>
		/// <param name="scan">The scan header.</param>
		/// <param name="position">The position of the first entropy-coded byte.</param>
		/// <returns>The position from which the marker parser resumes.</returns>
		public int Decode(JpegFrame frame, JpegTables tables, JpegScan scan, int position)
		{
			_frame = frame;
			_tables = tables;
			_scan = scan;
			_position = position;
			_marker = -1;

			var single = scan.Components.Length == 1;
			var first = scan.Components[0];
			var unitsPerLine = single ? first.BlocksPerLine : frame.McusPerLine;
			var total = single
				? (long)first.BlocksPerLine * first.BlocksPerColumn
				: (long)frame.McusPerLine * frame.McusPerColumn;

			var interval = tables.RestartInterval;
			var expected = 0;
			var unit = 0L;
			var complete = true;

			while (unit < total)
			{
				_corrupt = false;
				StartInterval(unit, unitsPerLine);

				var end = interval > 0 ? Math.Min(total, unit + interval) : total;
				var damaged = false;
				while (unit < end)
				{
					DecodeUnit(unit, unitsPerLine, single);
					unit++;

					// stop when the data ran out; with restart markers also stop on invalid codes,
					// since the decoder can resynchronize at the next marker.
					if (unit < end && (this.IsExhausted || (_corrupt && interval > 0)))
					{
						damaged = true;
						break;
					}
				}

				if (unit >= total && !damaged)
					break;

				if (interval == 0)
				{
					complete = false;
					break;
				}

				// look for the RSTn marker that starts the next interval.
				var marker = SeekMarker();
				var skip = 0;
				while (marker >= 0xD0 && marker <= 0xD7)
				{
					// the marker numbers resynchronize damaged data (as in libjpeg): the next two numbers
					// mean that whole intervals are missing, the previous two are stale markers to pass over,
					// and anything else is taken as the expected marker.
					skip = (marker - 0xD0 - expected) & 7;
					if (skip < 6)
						break;

					_position += 2;
					_marker = -1;
					marker = SeekMarker();
				}

				if (marker < 0xD0 || marker > 0xD7)
				{
					complete = false;
					break;
				}

				_position += 2;
				_marker = -1;

				if (skip > 2)
					skip = 0;

				// a damaged interval is abandoned: its remaining blocks stay neutral.
				if (damaged)
					unit = end;

				if (damaged || skip != 0)
					frame.IsComplete = false;

				unit += (long)skip * interval;
				expected = (expected + skip + 1) & 7;
			}

			if (!complete)
			{
				frame.IsComplete = false;

				// a truncated sequential scan: remember how many image rows were fully decoded, so that
				// the rest can be filled from the last good row instead of being left neutral.
				if (!frame.IsProgressive)
				{
					var unitRows = unit / unitsPerLine;
					var blockSize = frame.IsLossless ? 1 : 8;
					var rows = single
						? unitRows * blockSize * frame.MaxV / first.V
						: unitRows * blockSize * frame.MaxV;

					frame.ValidRows = (int)Math.Min(frame.ValidRows, rows);
				}
			}
			else
			{
				foreach (var component in scan.Components)
					component.IsCovered = true;
			}

			return _marker >= 0 ? _position : SeekMarkerPosition();
		}

		#endregion

		#region Implementation

		/// <summary>
		/// Gets whether the decoder has consumed more data than the segment holds.
		/// </summary>
		protected abstract bool IsExhausted { get; }

		/// <summary>
		/// Resets the decoder state at the start of the scan and of each restart interval.
		/// </summary>
		/// <param name="unit">The index of the first unit (MCU or block) of the interval.</param>
		/// <param name="unitsPerLine">The number of units per line.</param>
		protected abstract void StartInterval(long unit, int unitsPerLine);

		/// <summary>
		/// Discards the buffered bits so the reader can be repositioned at a marker.
		/// </summary>
		protected abstract void ResetReader();

		/// <summary>
		/// Decodes one block into the coefficient buffer of <paramref name="component"/>.
		/// </summary>
		/// <param name="component">The component the block belongs to.</param>
		/// <param name="offset">The offset of the block in <see cref="JpegComponent.Coefficients"/>.</param>
		protected abstract void DecodeBlock(JpegComponent component, int offset);

		/// <summary>
		/// Decodes one unit: an MCU of an interleaved scan, or one block of a non-interleaved scan.
		/// </summary>
		/// <param name="unit">The unit index.</param>
		/// <param name="unitsPerLine">The number of units per line.</param>
		/// <param name="single">True for a non-interleaved scan.</param>
		protected virtual void DecodeUnit(long unit, int unitsPerLine, bool single)
		{
			var row = (int)(unit / unitsPerLine);
			var column = (int)(unit % unitsPerLine);

			if (single)
			{
				var component = _scan.Components[0];
				DecodeBlock(component, (row * component.BlocksPerLineAlloc + column) * 64);
				return;
			}

			foreach (var component in _scan.Components)
			{
				for (int y = 0; y < component.V; y++)
				{
					var blockRow = row * component.V + y;
					var blockColumn = column * component.H;
					var offset = (blockRow * component.BlocksPerLineAlloc + blockColumn) * 64;
					for (int x = 0; x < component.H; x++)
					{
						DecodeBlock(component, offset);
						offset += 64;
					}
				}
			}
		}

		/// <summary>
		/// Positions the reader at the next marker and returns its code.
		/// </summary>
		/// <returns>The marker code, or -1 at the end of the data.</returns>
		protected int SeekMarker()
		{
			ResetReader();
			if (_marker >= 0)
				return _marker;

			_position = SeekMarkerPosition();
			if (_position + 1 >= _end)
				return -1;

			_marker = _data[_position + 1];
			return _marker;
		}

		/// <summary>
		/// Returns the position of the next marker from the current position, skipping stuffed and fill bytes.
		/// </summary>
		/// <returns>The position of the 0xFF byte of the marker, or the end of the data.</returns>
		protected int SeekMarkerPosition()
		{
			var position = _position;
			while (position + 1 < _end)
			{
				if (_data[position] == 0xFF)
				{
					var code = _data[position + 1];
					if (code != 0xFF && !IsBogusMarker(code) && IsPlausibleMarker(position))
						return position;
				}

				position++;
			}

			return _end;
		}

		/// <summary>
		/// Returns true when a marker code can't appear inside entropy-coded data and is most likely
		/// a corrupt byte pair; such pairs are skipped instead of ending the scan.
		/// </summary>
		/// <param name="code">The second byte of the marker.</param>
		/// <returns>True for TEM and the reserved codes 0x02-0xBF.</returns>
		protected static bool IsBogusMarker(int code)
		{
			return code < 0xC0;
		}

		/// <summary>
		/// Returns true when the marker at <paramref name="position"/> looks like a real marker segment
		/// that ends the entropy-coded data, rather than corrupt bytes that happen to form a marker.
		/// </summary>
		/// <param name="position">The position of the 0xFF byte of the marker.</param>
		/// <returns>True for RSTn, EOI and the segments whose length leads to another marker.</returns>
		protected bool IsPlausibleMarker(int position)
		{
			var code = _data[position + 1];
			if (code == 0xD9 || (code >= 0xD0 && code <= 0xD7))
				return true;

			if (code == 0xD8 || IsBogusMarker(code))
				return false;

			// a marker at the very end of truncated data: nothing to lose.
			if (position + 4 >= _end)
				return true;

			var length = (_data[position + 2] << 8) | _data[position + 3];
			if (length < 2)
				return false;

			// a scan header is followed by entropy-coded data: check its size against the component count.
			if (code == 0xDA)
			{
				var count = _data[position + 4];
				return count >= 1 && count <= 4 && length == 6 + 2 * count;
			}

			var next = position + 2 + length;
			if (next >= _end - 1)
				return next <= _end;

			// the next marker should follow, possibly after a few garbage bytes.
			var limit = Math.Min(_end - 1, next + 16);
			for (int i = next; i < limit; i++)
			{
				if (_data[i] == 0xFF && _data[i + 1] != 0 && _data[i + 1] != 0xFF)
					return true;
			}

			return false;
		}

		#endregion
	}
}
