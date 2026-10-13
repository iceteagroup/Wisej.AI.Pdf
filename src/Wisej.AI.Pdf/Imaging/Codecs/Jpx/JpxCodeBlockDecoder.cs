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

// Tier-1 (EBCOT) bit-plane decoding of JPEG 2000 code-blocks (ITU-T T.800, Annex D).

using System;
using System.Runtime.CompilerServices;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Decodes the coding passes of one code-block into quantized coefficients.
	/// </summary>
	/// <remarks>
	/// The state of every coefficient lives in one <see cref="int"/> of a flag array padded by one
	/// sample on each side, so neighbours never need bounds checks. When a coefficient becomes
	/// significant it publishes its significance and sign into the flags of its eight neighbours;
	/// the zero-coding and sign-coding contexts are then a single table lookup.
	/// <para>
	/// Magnitudes are accumulated in half units: a coefficient that becomes significant in bit-plane
	/// <c>p</c> is set to 1.5 × 2^p, and each refinement moves it to the middle of the remaining
	/// interval, which gives the usual mid-point reconstruction for truncated code-blocks for free.
	/// </para>
	/// An instance holds scratch buffers and is not thread safe; use one per thread.
	/// </remarks>
	internal sealed class JpxCodeBlockDecoder
	{
		#region Constants

		// significance of the eight neighbours.

		/// <summary>The north-west neighbour is significant.</summary>
		private const int NW = 1 << 0;

		/// <summary>The north neighbour is significant.</summary>
		private const int N = 1 << 1;

		/// <summary>The north-east neighbour is significant.</summary>
		private const int NE = 1 << 2;

		/// <summary>The west neighbour is significant.</summary>
		private const int W = 1 << 3;

		/// <summary>The east neighbour is significant.</summary>
		private const int E = 1 << 4;

		/// <summary>The south-west neighbour is significant.</summary>
		private const int SW = 1 << 5;

		/// <summary>The south neighbour is significant.</summary>
		private const int S = 1 << 6;

		/// <summary>The south-east neighbour is significant.</summary>
		private const int SE = 1 << 7;

		// sign of the four direct neighbours (only meaningful when the neighbour is significant).

		/// <summary>The north neighbour is negative.</summary>
		private const int NNeg = 1 << 8;

		/// <summary>The west neighbour is negative.</summary>
		private const int WNeg = 1 << 9;

		/// <summary>The east neighbour is negative.</summary>
		private const int ENeg = 1 << 10;

		/// <summary>The south neighbour is negative.</summary>
		private const int SNeg = 1 << 11;

		// state of the coefficient itself.

		/// <summary>The coefficient is significant.</summary>
		private const int Sig = 1 << 12;

		/// <summary>The coefficient was coded in the significance propagation pass of the current bit-plane.</summary>
		private const int Visit = 1 << 13;

		/// <summary>The coefficient has been refined at least once (selects refinement context 16).</summary>
		private const int Refined = 1 << 14;

		/// <summary>The coefficient is negative.</summary>
		private const int Neg = 1 << 15;

		// the neighbour bits used by the zero-coding and sign-coding contexts.

		/// <summary>The eight neighbour significance bits, the index of the zero-coding table.</summary>
		private const int NeighborMask = 0xFF;

		/// <summary>The neighbour significance and sign bits, the index of the sign-coding table.</summary>
		private const int SignMask = 0xFFF;

		/// <summary>
		/// Clears the neighbours of the stripe below, because in vertically causal mode the last row
		/// of a stripe ignores them (T.800, D.7).
		/// </summary>
		private const int CausalMask = ~(SW | S | SE | SNeg);

		/// <summary>
		/// Selective arithmetic coding bypass.
		/// </summary>
		public const int StyleBypass = 0x01;

		/// <summary>
		/// Reset of the context probabilities after each coding pass.
		/// </summary>
		public const int StyleReset = 0x02;

		/// <summary>
		/// Termination of the arithmetic coder after each coding pass.
		/// </summary>
		public const int StyleTermAll = 0x04;

		/// <summary>
		/// Vertically causal context formation.
		/// </summary>
		public const int StyleCausal = 0x08;

		/// <summary>
		/// Segmentation symbols after each cleanup pass.
		/// </summary>
		public const int StyleSegmentation = 0x20;

		/// <summary>
		/// High-throughput block coding (ITU-T T.814), not supported.
		/// </summary>
		public const int StyleHighThroughput = 0x40;

		/// <summary>
		/// The largest number of bit-planes that fits the half-unit magnitude representation.
		/// </summary>
		public const int MaxBitPlanes = 29;

		#endregion

		#region Tables

		/// <summary>
		/// The zero-coding contexts (T.800, Table D.1) for the three orientations, indexed by
		/// <c>orientation * 256 + neighbour bits</c>.
		/// </summary>
		private static readonly byte[] ZeroContexts = BuildZeroContexts();

		/// <summary>
		/// The sign-coding context in the low 5 bits and the XOR bit in bit 7 (T.800, Table D.3),
		/// indexed by the low 12 flag bits.
		/// </summary>
		private static readonly byte[] SignContexts = BuildSignContexts();

		/// <summary>
		/// Builds the <see cref="ZeroContexts"/> table.
		/// </summary>
		/// <returns>768 contexts: LL/LH, HL and HH orientations, 256 neighbour patterns each.</returns>
		private static byte[] BuildZeroContexts()
		{
			var table = new byte[3 * 256];
			for (int bits = 0; bits < 256; bits++)
			{
				var h = ((bits & W) != 0 ? 1 : 0) + ((bits & E) != 0 ? 1 : 0);
				var v = ((bits & N) != 0 ? 1 : 0) + ((bits & S) != 0 ? 1 : 0);
				var d = ((bits & NW) != 0 ? 1 : 0) + ((bits & NE) != 0 ? 1 : 0) + ((bits & SW) != 0 ? 1 : 0) + ((bits & SE) != 0 ? 1 : 0);

				// LL and LH sub-bands; the HL sub-band swaps the roles of the horizontal and vertical neighbours.
				table[bits] = (byte)ZeroContext(h, v, d);
				table[256 + bits] = (byte)ZeroContext(v, h, d);

				// HH sub-band.
				int context;
				var hv = h + v;
				if (d >= 3)
					context = 8;
				else if (d == 2)
					context = hv >= 1 ? 7 : 6;
				else if (d == 1)
					context = hv >= 2 ? 5 : hv == 1 ? 4 : 3;
				else
					context = hv >= 2 ? 2 : hv == 1 ? 1 : 0;

				table[512 + bits] = (byte)context;
			}

			return table;
		}

		// zero-coding context of the LL and LH sub-bands (Table D.1).
		private static int ZeroContext(int h, int v, int d)
		{
			if (h == 2)
				return 8;

			if (h == 1)
				return v >= 1 ? 7 : d >= 1 ? 6 : 5;

			if (v == 2)
				return 4;

			if (v == 1)
				return 3;

			return d >= 2 ? 2 : d;
		}

		/// <summary>
		/// Builds the <see cref="SignContexts"/> table.
		/// </summary>
		/// <returns>4096 entries, one for each combination of neighbour significance and sign bits.</returns>
		private static byte[] BuildSignContexts()
		{
			var table = new byte[4096];
			for (int bits = 0; bits < 4096; bits++)
			{
				var h = Contribution(bits, W, WNeg) + Contribution(bits, E, ENeg);
				var v = Contribution(bits, N, NNeg) + Contribution(bits, S, SNeg);
				h = Math.Max(-1, Math.Min(1, h));
				v = Math.Max(-1, Math.Min(1, v));

				// Table D.3: the context depends on |H| and V, the XOR bit makes the table symmetric.
				var xor = 0;
				if (h < 0 || h == 0 && v < 0)
				{
					h = -h;
					v = -v;
					xor = 1;
				}

				int context;
				if (h == 0)
					context = v == 0 ? 9 : 10;
				else
					context = v == 1 ? 13 : v == 0 ? 12 : 11;

				table[bits] = (byte)(context | (xor << 7));
			}

			return table;
		}

		// the sign contribution of one neighbour: +1 when significant and positive, -1 when negative.
		private static int Contribution(int bits, int significant, int negative)
		{
			if ((bits & significant) == 0)
				return 0;

			return (bits & negative) != 0 ? -1 : 1;
		}

		#endregion

		#region Fields

		// the arithmetic decoder.
		private readonly JpxMqDecoder _mq = new JpxMqDecoder();

		// the coefficient flags, padded by one sample on each side.
		private int[] _flags = new int[66 * 66];

		// the decoded coefficients (magnitudes during decoding, signed values at the end).
		private int[] _data = new int[64 * 64];

		// the code-block width, in coefficients.
		private int _width;

		// the code-block height, in coefficients.
		private int _height;

		// the row stride of the padded flag array (width + 2).
		private int _stride;

		#endregion

		#region Properties

		/// <summary>
		/// The decoded coefficients in raster order, in half quantization steps with their sign applied.
		/// </summary>
		public int[] Coefficients
		{
			get { return _data; }
		}

		#endregion

		#region Methods

		/// <summary>
		/// Decodes a code-block.
		/// </summary>
		/// <param name="data">The concatenated codeword segments.</param>
		/// <param name="segmentLengths">The length of each segment.</param>
		/// <param name="segmentPasses">The number of coding passes in each segment.</param>
		/// <param name="segmentCount">The number of segments.</param>
		/// <param name="width">The code-block width.</param>
		/// <param name="height">The code-block height.</param>
		/// <param name="bandType">The sub-band type: 0 = LL, 1 = HL, 2 = LH, 3 = HH.</param>
		/// <param name="style">The code-block style flags.</param>
		/// <param name="planes">The number of magnitude bit-planes to decode, including the ROI shift.</param>
		/// <param name="roiShift">The ROI maxshift value, or 0.</param>
		/// <returns>The number of coding passes decoded.</returns>
		public int Decode(byte[] data, int[] segmentLengths, int[] segmentPasses, int segmentCount, int width, int height, int bandType, int style, int planes, int roiShift)
		{
			_width = width;
			_height = height;
			_stride = width + 2;

			var flagCount = (width + 2) * (height + 2);
			if (_flags.Length < flagCount)
				_flags = new int[flagCount];
			else
				Array.Clear(_flags, 0, flagCount);

			var count = width * height;
			if (_data.Length < count)
				_data = new int[count];
			else
				Array.Clear(_data, 0, count);

			if (planes <= 0 || segmentCount == 0)
				return 0;

			// a corrupt zero bit-plane count could exceed the precision of the accumulator.
			planes = Math.Min(planes, MaxBitPlanes);

			var orientation = bandType == 1 ? 256 : bandType == 3 ? 512 : 0;
			var causal = (style & StyleCausal) != 0;
			var bypass = (style & StyleBypass) != 0;

			_mq.ResetContexts();

			// the first pass is the cleanup pass of the most significant bit-plane.
			var plane = planes - 1;
			var passType = 2;
			var pass = 0;
			var position = 0;

			for (int segment = 0; segment < segmentCount && plane >= 0; segment++)
			{
				var start = position;
				var end = Math.Min(start + segmentLengths[segment], data.Length);
				position = end;

				// with the bypass style, significance and refinement passes after the fourth bit-plane are raw.
				var raw = bypass && pass >= 10 && passType != 2;
				if (raw)
					_mq.StartRaw(data, start, end);
				else
					_mq.Start(data, start, end);

				for (int i = segmentPasses[segment]; i > 0 && plane >= 0; i--)
				{
					switch (passType)
					{
						case 0:
							if (raw)
								SignificancePassRaw(plane, causal);
							else
								SignificancePass(plane, orientation, causal);
							break;

						case 1:
							if (raw)
								RefinementPassRaw(plane);
							else
								RefinementPass(plane, causal);
							break;

						default:
							CleanupPass(plane, orientation, causal);
							if ((style & StyleSegmentation) != 0)
							{
								// the segmentation symbol 1010 only serves error detection.
								for (int k = 0; k < 4; k++)
									_mq.Decode(JpxMqDecoder.UniformContext);
							}
							break;
					}

					if ((style & StyleReset) != 0)
						_mq.ResetContexts();

					pass++;
					if (++passType == 3)
					{
						passType = 0;
						plane--;
					}
				}
			}

			Finish(roiShift);
			return pass;
		}

		#endregion

		#region Implementation

		// marks the coefficient at flag index f as significant and publishes it to its neighbours.
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void SetSignificant(int f, int negative)
		{
			var flags = _flags;
			var stride = _stride;

			flags[f - stride - 1] |= SE;
			flags[f - stride] |= S | (negative << 11);
			flags[f - stride + 1] |= SW;
			flags[f - 1] |= E | (negative << 10);
			flags[f] |= Sig | (negative << 15);
			flags[f + 1] |= W | (negative << 9);
			flags[f + stride - 1] |= NE;
			flags[f + stride] |= N | (negative << 8);
			flags[f + stride + 1] |= NW;
		}

		// significance propagation pass (D.3.1).
		private void SignificancePass(int plane, int orientation, bool causal)
		{
			var flags = _flags;
			var data = _data;
			var mq = _mq;
			var width = _width;
			var height = _height;
			var stride = _stride;
			var one = 3 << plane;

			for (int y0 = 0; y0 < height; y0 += 4)
			{
				var rows = Math.Min(4, height - y0);
				var flagRow = (y0 + 1) * stride + 1;
				var dataRow = y0 * width;

				for (int x = 0; x < width; x++)
				{
					var f = flagRow + x;

					// skip stripe columns where no coefficient has a significant neighbour.
					if (rows == 4 && ((flags[f] | flags[f + stride] | flags[f + 2 * stride] | flags[f + 3 * stride]) & NeighborMask) == 0)
						continue;

					var d = dataRow + x;
					for (int r = 0; r < rows; r++, f += stride, d += width)
					{
						var state = flags[f];
						if ((state & Sig) != 0)
							continue;

						if (causal && r == 3)
							state &= CausalMask;

						// only coefficients with a significant neighbour are coded in this pass.
						if ((state & NeighborMask) == 0)
							continue;

						if (mq.Decode(ZeroContexts[orientation + (state & NeighborMask)]) != 0)
						{
							var sign = SignContexts[state & SignMask];
							var negative = mq.Decode(sign & 0x1F) ^ (sign >> 7);
							SetSignificant(f, negative);
							data[d] = one;
						}

						flags[f] |= Visit;
					}
				}
			}
		}

		// significance propagation pass of a raw (bypass) segment.
		private void SignificancePassRaw(int plane, bool causal)
		{
			var flags = _flags;
			var data = _data;
			var mq = _mq;
			var width = _width;
			var height = _height;
			var stride = _stride;
			var one = 3 << plane;

			for (int y0 = 0; y0 < height; y0 += 4)
			{
				var rows = Math.Min(4, height - y0);
				var flagRow = (y0 + 1) * stride + 1;
				var dataRow = y0 * width;

				for (int x = 0; x < width; x++)
				{
					var f = flagRow + x;

					// skip stripe columns where no coefficient has a significant neighbour.
					if (rows == 4 && ((flags[f] | flags[f + stride] | flags[f + 2 * stride] | flags[f + 3 * stride]) & NeighborMask) == 0)
						continue;

					var d = dataRow + x;
					for (int r = 0; r < rows; r++, f += stride, d += width)
					{
						var state = flags[f];
						if ((state & Sig) != 0)
							continue;

						if (causal && r == 3)
							state &= CausalMask;

						if ((state & NeighborMask) == 0)
							continue;

						if (mq.DecodeRaw() != 0)
						{
							SetSignificant(f, mq.DecodeRaw());
							data[d] = one;
						}

						flags[f] |= Visit;
					}
				}
			}
		}

		// magnitude refinement pass (D.3.3).
		private void RefinementPass(int plane, bool causal)
		{
			var flags = _flags;
			var data = _data;
			var mq = _mq;
			var width = _width;
			var height = _height;
			var stride = _stride;
			var half = 1 << plane;

			for (int y0 = 0; y0 < height; y0 += 4)
			{
				var rows = Math.Min(4, height - y0);
				var flagRow = (y0 + 1) * stride + 1;
				var dataRow = y0 * width;

				for (int x = 0; x < width; x++)
				{
					var f = flagRow + x;

					// skip stripe columns without significant coefficients.
					if (rows == 4 && ((flags[f] | flags[f + stride] | flags[f + 2 * stride] | flags[f + 3 * stride]) & Sig) == 0)
						continue;

					var d = dataRow + x;
					for (int r = 0; r < rows; r++, f += stride, d += width)
					{
						var state = flags[f];

						// refine coefficients that were significant before this bit-plane.
						if ((state & (Sig | Visit)) != Sig)
							continue;

						int context;
						if ((state & Refined) != 0)
						{
							context = 16;
						}
						else
						{
							var neighbors = causal && r == 3 ? state & CausalMask : state;
							context = (neighbors & NeighborMask) != 0 ? 15 : 14;
						}

						data[d] += mq.Decode(context) != 0 ? half : -half;
						flags[f] = state | Refined;
					}
				}
			}
		}

		// magnitude refinement pass of a raw (bypass) segment.
		private void RefinementPassRaw(int plane)
		{
			var flags = _flags;
			var data = _data;
			var mq = _mq;
			var width = _width;
			var height = _height;
			var stride = _stride;
			var half = 1 << plane;

			for (int y0 = 0; y0 < height; y0 += 4)
			{
				var rows = Math.Min(4, height - y0);
				var flagRow = (y0 + 1) * stride + 1;
				var dataRow = y0 * width;

				for (int x = 0; x < width; x++)
				{
					var f = flagRow + x;

					// skip stripe columns without significant coefficients.
					if (rows == 4 && ((flags[f] | flags[f + stride] | flags[f + 2 * stride] | flags[f + 3 * stride]) & Sig) == 0)
						continue;

					var d = dataRow + x;
					for (int r = 0; r < rows; r++, f += stride, d += width)
					{
						var state = flags[f];
						if ((state & (Sig | Visit)) != Sig)
							continue;

						data[d] += mq.DecodeRaw() != 0 ? half : -half;
						flags[f] = state | Refined;
					}
				}
			}
		}

		// cleanup pass with run-length coding (D.3.4).
		private void CleanupPass(int plane, int orientation, bool causal)
		{
			var flags = _flags;
			var data = _data;
			var mq = _mq;
			var width = _width;
			var height = _height;
			var stride = _stride;
			var one = 3 << plane;

			for (int y0 = 0; y0 < height; y0 += 4)
			{
				var rows = Math.Min(4, height - y0);
				var flagRow = (y0 + 1) * stride + 1;
				var dataRow = y0 * width;

				for (int x = 0; x < width; x++)
				{
					var f0 = flagRow + x;
					var d0 = dataRow + x;
					var r = 0;

					// run-length mode: a full stripe column with no significant or visited coefficients
					// and no significant neighbours.
					if (rows == 4)
					{
						var last = flags[f0 + 3 * stride];
						if (causal)
							last &= CausalMask;

						if (((flags[f0] | flags[f0 + stride] | flags[f0 + 2 * stride] | last) & (Sig | Visit | NeighborMask)) == 0)
						{
							if (mq.Decode(JpxMqDecoder.RunLengthContext) == 0)
								continue;

							r = mq.Decode(JpxMqDecoder.UniformContext) << 1;
							r |= mq.Decode(JpxMqDecoder.UniformContext);

							// all the neighbours are insignificant, so the sign context is the neutral one.
							var sign = SignContexts[0];
							var negative = mq.Decode(sign & 0x1F) ^ (sign >> 7);
							SetSignificant(f0 + r * stride, negative);
							data[d0 + r * width] = one;
							r++;
						}
					}

					var f = f0 + r * stride;
					var d = d0 + r * width;
					for (; r < rows; r++, f += stride, d += width)
					{
						var state = flags[f];
						if ((state & (Sig | Visit)) == 0)
						{
							if (causal && r == 3)
								state &= CausalMask;

							if (mq.Decode(ZeroContexts[orientation + (state & NeighborMask)]) != 0)
							{
								var sign = SignContexts[state & SignMask];
								var negative = mq.Decode(sign & 0x1F) ^ (sign >> 7);
								SetSignificant(f, negative);
								data[d] = one;
							}
						}

						flags[f] &= ~Visit;
					}
				}
			}
		}

		// applies the ROI down-shift and the signs to the decoded magnitudes.
		private void Finish(int roiShift)
		{
			var flags = _flags;
			var data = _data;
			var width = _width;
			var height = _height;
			var stride = _stride;

			// with maxshift, every coefficient at or above 2^shift belongs to the region of interest (Annex H).
			var threshold = roiShift > 0 && roiShift < 30 ? 1 << (roiShift + 1) : int.MaxValue;

			for (int y = 0; y < height; y++)
			{
				var f = (y + 1) * stride + 1;
				var d = y * width;
				for (int x = 0; x < width; x++, f++, d++)
				{
					var magnitude = data[d];
					if (magnitude == 0)
						continue;

					if (magnitude >= threshold)
						magnitude >>= roiShift;

					data[d] = (flags[f] & Neg) != 0 ? -magnitude : magnitude;
				}
			}
		}

		#endregion
	}
}
