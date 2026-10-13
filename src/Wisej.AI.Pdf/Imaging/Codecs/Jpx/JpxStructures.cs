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

// Tile-component, resolution, sub-band, precinct and code-block structures (ITU-T T.800, B.5 to B.7).

using System;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// A code-block and the coded data collected for it from the packets.
	/// </summary>
	internal sealed class JpxCodeBlock
	{
		/// <summary>
		/// The left edge in sub-band coordinates.
		/// </summary>
		public int X0;

		/// <summary>
		/// The top edge in sub-band coordinates.
		/// </summary>
		public int Y0;

		/// <summary>
		/// The right edge (exclusive) in sub-band coordinates.
		/// </summary>
		public int X1;

		/// <summary>
		/// The bottom edge (exclusive) in sub-band coordinates.
		/// </summary>
		public int Y1;

		/// <summary>
		/// True once the code-block has been included in a packet.
		/// </summary>
		public bool Included;

		/// <summary>
		/// The number of missing most significant bit-planes.
		/// </summary>
		public int ZeroBitPlanes;

		/// <summary>
		/// The Lblock state variable used to decode the codeword segment lengths (B.10.7.1).
		/// </summary>
		public int LBlock = 3;

		/// <summary>
		/// The number of coding passes announced by the packet headers so far.
		/// </summary>
		public int Passes;

		/// <summary>
		/// The concatenated codeword segments.
		/// </summary>
		public byte[] Data;

		/// <summary>
		/// The number of valid bytes in <see cref="Data"/>.
		/// </summary>
		public int DataLength;

		/// <summary>
		/// The length of each codeword segment.
		/// </summary>
		public int[] SegmentLengths;

		/// <summary>
		/// The number of coding passes in each codeword segment.
		/// </summary>
		public int[] SegmentPasses;

		/// <summary>
		/// The number of codeword segments.
		/// </summary>
		public int SegmentCount;

		/// <summary>
		/// Appends a chunk of coded data contributed by one packet.
		/// </summary>
		/// <param name="source">The buffer containing the chunk.</param>
		/// <param name="offset">The position of the chunk in <paramref name="source"/>.</param>
		/// <param name="length">The length of the chunk.</param>
		/// <param name="passes">The number of coding passes in the chunk.</param>
		/// <param name="newSegment">True when the chunk starts a new codeword segment, false when it continues the last one.</param>
		public void Append(byte[] source, int offset, int length, int passes, bool newSegment)
		{
			if (this.Data == null)
			{
				this.Data = new byte[Math.Max(length, 64)];
				this.SegmentLengths = new int[4];
				this.SegmentPasses = new int[4];
			}
			else if (this.DataLength + length > this.Data.Length)
			{
				var data = new byte[Math.Max(this.DataLength + length, this.Data.Length * 2)];
				Buffer.BlockCopy(this.Data, 0, data, 0, this.DataLength);
				this.Data = data;
			}

			Buffer.BlockCopy(source, offset, this.Data, this.DataLength, length);
			this.DataLength += length;

			if (newSegment || this.SegmentCount == 0)
			{
				if (this.SegmentCount == this.SegmentLengths.Length)
				{
					Array.Resize(ref this.SegmentLengths, this.SegmentCount * 2);
					Array.Resize(ref this.SegmentPasses, this.SegmentCount * 2);
				}

				this.SegmentLengths[this.SegmentCount] = length;
				this.SegmentPasses[this.SegmentCount] = passes;
				this.SegmentCount++;
			}
			else
			{
				this.SegmentLengths[this.SegmentCount - 1] += length;
				this.SegmentPasses[this.SegmentCount - 1] += passes;
			}
		}
	}

	/// <summary>
	/// The code-blocks of one sub-band inside one precinct, with their tag trees.
	/// </summary>
	internal sealed class JpxPrecinctBand
	{
		/// <summary>
		/// The code-blocks in raster order.
		/// </summary>
		public JpxCodeBlock[] Blocks;

		/// <summary>
		/// The inclusion tag tree.
		/// </summary>
		public JpxTagTree Inclusion;

		/// <summary>
		/// The zero bit-plane tag tree.
		/// </summary>
		public JpxTagTree ZeroBitPlanes;
	}

	/// <summary>
	/// A precinct: the unit that groups code-blocks into packets.
	/// </summary>
	internal sealed class JpxPrecinct
	{
		/// <summary>
		/// The code-blocks of each sub-band of the resolution level.
		/// </summary>
		public JpxPrecinctBand[] Bands;

		/// <summary>
		/// The next layer expected for this precinct; packets are always read in layer order.
		/// </summary>
		public int NextLayer;
	}

	/// <summary>
	/// A sub-band of a resolution level.
	/// </summary>
	internal sealed class JpxBand
	{
		/// <summary>
		/// The sub-band type: 0 = LL, 1 = HL, 2 = LH, 3 = HH.
		/// </summary>
		public int Type;

		/// <summary>
		/// The left edge in sub-band coordinates.
		/// </summary>
		public int X0;

		/// <summary>
		/// The top edge in sub-band coordinates.
		/// </summary>
		public int Y0;

		/// <summary>
		/// The right edge (exclusive).
		/// </summary>
		public int X1;

		/// <summary>
		/// The bottom edge (exclusive).
		/// </summary>
		public int Y1;

		/// <summary>
		/// The horizontal position of the sub-band in the coefficient buffer.
		/// </summary>
		public int OffsetX;

		/// <summary>
		/// The vertical position of the sub-band in the coefficient buffer.
		/// </summary>
		public int OffsetY;

		/// <summary>
		/// The number of magnitude bit-planes (Mb, E.1).
		/// </summary>
		public int MagnitudeBits;

		/// <summary>
		/// The dequantization factor applied to the half-unit coefficients.
		/// </summary>
		public float Scale;
	}

	/// <summary>
	/// A resolution level of a tile-component.
	/// </summary>
	internal sealed class JpxResolution
	{
		/// <summary>
		/// The left edge in resolution coordinates.
		/// </summary>
		public int X0;

		/// <summary>
		/// The top edge in resolution coordinates.
		/// </summary>
		public int Y0;

		/// <summary>
		/// The right edge (exclusive).
		/// </summary>
		public int X1;

		/// <summary>
		/// The bottom edge (exclusive).
		/// </summary>
		public int Y1;

		/// <summary>
		/// The base-2 logarithm of the precinct width.
		/// </summary>
		public int PrecinctWidthExp;

		/// <summary>
		/// The base-2 logarithm of the precinct height.
		/// </summary>
		public int PrecinctHeightExp;

		/// <summary>
		/// The number of precincts across.
		/// </summary>
		public int PrecinctsWide;

		/// <summary>
		/// The number of precincts down.
		/// </summary>
		public int PrecinctsHigh;

		/// <summary>
		/// The sub-bands: LL for the lowest resolution, otherwise HL, LH and HH.
		/// </summary>
		public JpxBand[] Bands;

		/// <summary>
		/// The precincts in raster order.
		/// </summary>
		public JpxPrecinct[] Precincts;

		/// <summary>
		/// The width of the resolution level.
		/// </summary>
		public int Width
		{
			get { return this.X1 - this.X0; }
		}

		/// <summary>
		/// The height of the resolution level.
		/// </summary>
		public int Height
		{
			get { return this.Y1 - this.Y0; }
		}
	}

	/// <summary>
	/// A component of a tile, with its coding parameters and decomposition.
	/// </summary>
	internal sealed class JpxTileComponent
	{
		/// <summary>
		/// The left edge in component coordinates.
		/// </summary>
		public int X0;

		/// <summary>
		/// The top edge in component coordinates.
		/// </summary>
		public int Y0;

		/// <summary>
		/// The right edge (exclusive).
		/// </summary>
		public int X1;

		/// <summary>
		/// The bottom edge (exclusive).
		/// </summary>
		public int Y1;

		/// <summary>
		/// The coding style.
		/// </summary>
		public JpxCodingStyle Style;

		/// <summary>
		/// The ROI maxshift value, or 0.
		/// </summary>
		public int RoiShift;

		/// <summary>
		/// The resolution levels, from the lowest (index 0) to the highest.
		/// </summary>
		public JpxResolution[] Resolutions;

		/// <summary>
		/// The highest resolution level that is decoded (the others are discarded by the reduce option).
		/// </summary>
		public int DecodedLevel;

		/// <summary>
		/// The reconstructed samples of the decoded resolution level.
		/// </summary>
		public float[] Samples;
	}

	/// <summary>
	/// A tag tree (B.10.2) used for code-block inclusion and zero bit-plane information.
	/// </summary>
	internal sealed class JpxTagTree
	{
		// the value of each node (int.MaxValue while unknown), leaves first.
		private readonly int[] _values;

		// the lower bound already established for each node.
		private readonly int[] _lows;

		// the parent of each node, -1 for the root.
		private readonly int[] _parents;

		// scratch stack for the path from a leaf to the root.
		private readonly int[] _path = new int[32];

		/// <summary>
		/// Initializes a new instance of <see cref="JpxTagTree"/> with <paramref name="width"/> × <paramref name="height"/> leaves.
		/// </summary>
		/// <param name="width">The number of leaves across.</param>
		/// <param name="height">The number of leaves down.</param>
		public JpxTagTree(int width, int height)
		{
			// count the nodes of all the levels.
			var count = 0;
			var w = width;
			var h = height;
			while (true)
			{
				count += w * h;
				if (w <= 1 && h <= 1)
					break;

				w = (w + 1) >> 1;
				h = (h + 1) >> 1;
			}

			_values = new int[count];
			_lows = new int[count];
			_parents = new int[count];
			for (int i = 0; i < count; i++)
				_values[i] = int.MaxValue;

			// link every node to its parent in the next level.
			var levelStart = 0;
			w = width;
			h = height;
			while (w > 1 || h > 1)
			{
				var parentStart = levelStart + w * h;
				var parentWidth = (w + 1) >> 1;
				for (int y = 0; y < h; y++)
				{
					for (int x = 0; x < w; x++)
						_parents[levelStart + y * w + x] = parentStart + (y >> 1) * parentWidth + (x >> 1);
				}

				levelStart = parentStart;
				w = parentWidth;
				h = (h + 1) >> 1;
			}

			_parents[count - 1] = -1;
		}

		/// <summary>
		/// Decodes enough bits to tell whether the value of <paramref name="leaf"/> is below <paramref name="threshold"/>.
		/// </summary>
		/// <param name="reader">The packet header bit reader.</param>
		/// <param name="leaf">The leaf index in raster order.</param>
		/// <param name="threshold">The threshold.</param>
		/// <returns>True when the leaf value is less than <paramref name="threshold"/>.</returns>
		public bool Decode(JpxBitReader reader, int leaf, int threshold)
		{
			var depth = 0;
			for (int node = leaf; node >= 0 && depth < _path.Length; node = _parents[node])
				_path[depth++] = node;

			var low = 0;
			for (int i = depth - 1; i >= 0; i--)
			{
				var node = _path[i];
				if (low > _lows[node])
					_lows[node] = low;
				else
					low = _lows[node];

				while (low < threshold && low < _values[node])
				{
					if (reader.ReadBit() != 0)
						_values[node] = low;
					else
						low++;
				}

				_lows[node] = low;
			}

			return _values[leaf] < threshold;
		}

		/// <summary>
		/// Returns the decoded value of <paramref name="leaf"/>.
		/// </summary>
		/// <param name="leaf">The leaf index in raster order.</param>
		/// <returns>The value, or <see cref="int.MaxValue"/> when not decoded yet.</returns>
		public int GetValue(int leaf)
		{
			return _values[leaf];
		}
	}

	/// <summary>
	/// Reads the bits of packet headers, skipping the bit stuffed after each 0xFF byte (B.10.1).
	/// </summary>
	internal sealed class JpxBitReader
	{
		// the source buffer.
		private byte[] _data;

		// the position of the next byte to load from _data.
		private int _position;

		// the end of the readable data (exclusive).
		private int _end;

		// the current byte.
		private int _byte;

		// the number of unread bits in _byte.
		private int _bits;

		/// <summary>
		/// The position of the next unread byte.
		/// </summary>
		public int Position
		{
			get { return _position; }
		}

		/// <summary>
		/// True when a read went past the end of the data.
		/// </summary>
		public bool Overrun;

		/// <summary>
		/// Starts reading at <paramref name="position"/>.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="position">The start position.</param>
		/// <param name="end">The end of the readable data.</param>
		public void Reset(byte[] data, int position, int end)
		{
			_data = data;
			_position = position;
			_end = end;
			_byte = 0;
			_bits = 0;
			this.Overrun = false;
		}

		/// <summary>
		/// Moves to <paramref name="position"/> at a byte boundary.
		/// </summary>
		/// <param name="position">The new position.</param>
		public void Seek(int position)
		{
			_position = position;
			_byte = 0;
			_bits = 0;
		}

		/// <summary>
		/// Reads one bit.
		/// </summary>
		/// <returns>The bit; 0 past the end of the data.</returns>
		public int ReadBit()
		{
			if (_bits == 0)
			{
				// after a 0xFF byte only 7 bits are used: the most significant one is a stuffed 0.
				_bits = _byte == 0xFF ? 7 : 8;
				if (_position < _end)
				{
					_byte = _data[_position++];
				}
				else
				{
					_byte = 0;
					this.Overrun = true;
				}
			}

			_bits--;
			return (_byte >> _bits) & 1;
		}

		/// <summary>
		/// Reads <paramref name="count"/> bits, most significant first.
		/// </summary>
		/// <param name="count">The number of bits, at most 31.</param>
		/// <returns>The value.</returns>
		public int ReadBits(int count)
		{
			var value = 0;
			for (int i = 0; i < count; i++)
				value = (value << 1) | ReadBit();

			return value;
		}

		/// <summary>
		/// Skips to the next byte boundary at the end of a packet header.
		/// </summary>
		public void Align()
		{
			// a header never ends with 0xFF: the stuffed byte that follows belongs to it.
			if (_byte == 0xFF)
			{
				if (_position < _end)
					_position++;
			}

			_byte = 0;
			_bits = 0;
		}
	}
}
