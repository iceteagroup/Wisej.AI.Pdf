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

// Arithmetic (QM-coder) entropy decoder for sequential and progressive JPEG scans
// (ITU-T T.81, Annex D, F.2.4 and G.1.3).

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Decodes arithmetic-coded DCT scans (SOF9 and SOF10).
	/// </summary>
	/// <remarks>
	/// The decoder follows the procedures and the register layout of T.81 Annex D:
	/// the upper 16 bits of <c>C</c> are compared against the interval size <c>A</c>, and new bytes
	/// enter below them. Each statistics bin is one byte: the MPS sense in bit 7 and the index into
	/// the probability estimation table D.2 in bits 0-6.
	/// </remarks>
	internal sealed class JpegArithmeticDecoder : JpegEntropyDecoder
	{
		#region Fields

		// the statistics bins of the DC tables: 49 used of 64 (table F.4).
		private readonly byte[][] _dcStats = { new byte[64], new byte[64], new byte[64], new byte[64] };

		// the statistics bins of the AC tables: 245 used of 256 (table F.5).
		private readonly byte[][] _acStats = { new byte[256], new byte[256], new byte[256], new byte[256] };

		// the fixed-probability bin (Qe = 0x5A1D, never adapted) used for signs and refinement bits.
		private readonly byte[] _fixedBin = { FixedState };

		// the code register C.
		private uint _c;

		// the interval register A.
		private int _a;

		// the bit counter CT: the number of bits left in the low byte of C.
		private int _ct;

		// number of zero bytes fed after the end of the segment.
		private int _fakeBytes;

		// the index of the fixed 0.5 probability state appended to table D.2.
		private const int FixedState = 113;

		// T.81 table D.2: Qe for each state.
		private static readonly ushort[] Qe =
		{
			0x5a1d, 0x2586, 0x1114, 0x080b, 0x03d8, 0x01da, 0x00e5, 0x006f, 0x0036, 0x001a, 0x000d, 0x0006, 0x0003, 0x0001,
			0x5a7f, 0x3f25, 0x2cf2, 0x207c, 0x17b9, 0x1182, 0x0cef, 0x09a1, 0x072f, 0x055c, 0x0406, 0x0303, 0x0240, 0x01b1,
			0x0144, 0x00f5, 0x00b7, 0x008a, 0x0068, 0x004e, 0x003b, 0x002c, 0x5ae1, 0x484c, 0x3a0d, 0x2ef1, 0x261f, 0x1f33,
			0x19a8, 0x1518, 0x1177, 0x0e74, 0x0bfb, 0x09f8, 0x0861, 0x0706, 0x05cd, 0x04de, 0x040f, 0x0363, 0x02d4, 0x025c,
			0x01f8, 0x01a4, 0x0160, 0x0125, 0x00f6, 0x00cb, 0x00ab, 0x008f, 0x5b12, 0x4d04, 0x412c, 0x37d8, 0x2fe8, 0x293c,
			0x2379, 0x1edf, 0x1aa9, 0x174e, 0x1424, 0x119c, 0x0f6b, 0x0d51, 0x0bb6, 0x0a40, 0x5832, 0x4d1c, 0x438e, 0x3bdd,
			0x34ee, 0x2eae, 0x299a, 0x2516, 0x5570, 0x4ca9, 0x44d9, 0x3e22, 0x3824, 0x32b4, 0x2e17, 0x56a8, 0x4f46, 0x47e5,
			0x41cf, 0x3c3d, 0x375e, 0x5231, 0x4c0f, 0x4639, 0x415e, 0x5627, 0x50e7, 0x4b85, 0x5597, 0x504f, 0x5a10, 0x5522,
			0x59eb, 0x5a1d
		};

		// T.81 table D.2: the next state after an LPS.
		private static readonly byte[] NextLps =
		{
			1, 14, 16, 18, 20, 23, 25, 28, 30, 33, 35, 9, 10, 12, 15, 36, 38, 39, 40, 42, 43, 45, 46, 48, 49, 51, 52, 54,
			56, 57, 59, 60, 62, 63, 32, 33, 37, 64, 65, 67, 68, 69, 70, 72, 73, 74, 75, 77, 78, 79, 48, 50, 50, 51, 52, 53,
			54, 55, 56, 57, 58, 59, 61, 61, 65, 80, 81, 82, 83, 84, 86, 87, 87, 72, 72, 74, 74, 75, 77, 77, 80, 88, 89, 90,
			91, 92, 93, 86, 88, 95, 96, 97, 99, 99, 93, 95, 101, 102, 103, 104, 99, 105, 106, 107, 103, 105, 108, 109, 110,
			111, 110, 112, 112, 113
		};

		// T.81 table D.2: the next state after an MPS.
		private static readonly byte[] NextMps =
		{
			1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 13, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
			31, 32, 33, 34, 35, 9, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58,
			59, 60, 61, 62, 63, 32, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 48, 81, 82, 83, 84, 85, 86,
			87, 71, 89, 90, 91, 92, 93, 94, 86, 96, 97, 98, 99, 100, 93, 102, 103, 104, 99, 106, 107, 103, 109, 107, 111,
			109, 111, 113
		};

		// T.81 table D.2: the states where an LPS swaps the meaning of MPS (0 or 1, pre-shifted into bit 7).
		private static readonly byte[] SwitchMps = BuildSwitchTable();

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new instance of the <see cref="JpegArithmeticDecoder"/> class.
		/// </summary>
		/// <param name="data">The JPEG stream.</param>
		/// <param name="end">The end of the usable data.</param>
		public JpegArithmeticDecoder(byte[] data, int end)
			: base(data, end)
		{
		}

		#endregion

		#region Implementation

		/// <summary>
		/// Gets whether the decoder consumed many bytes past the end of the segment.
		/// </summary>
		/// <remarks>
		/// Unlike Huffman data, arithmetic-coded data legitimately runs into the marker: encoders drop
		/// trailing zero bytes and the decoder supplies them. Only a long run of supplied bytes indicates truncation.
		/// </remarks>
		protected override bool IsExhausted
		{
			get { return _fakeBytes > 16; }
		}

		/// <summary>
		/// Discards the decoder state so that the reader can be repositioned at a marker.
		/// </summary>
		protected override void ResetReader()
		{
			_c = 0;
			_a = 0;
			_ct = 0;
			_fakeBytes = 0;
		}

		/// <summary>
		/// Resets the statistics and the predictors, and initializes the decoder (D.2.7) at the start of a restart interval.
		/// </summary>
		/// <param name="unit">The index of the first unit of the interval.</param>
		/// <param name="unitsPerLine">The number of units per line.</param>
		protected override void StartInterval(long unit, int unitsPerLine)
		{
			var progressive = _frame.IsProgressive;
			foreach (var component in _scan.Components)
			{
				// statistics are reset only for the tables that the scan codes with (F.1.4.4 and G.1.3).
				if (!progressive || (_scan.Ss == 0 && _scan.Ah == 0))
				{
					System.Array.Clear(_dcStats[component.DcTableIndex], 0, 64);
					component.DcPredictor = 0;
					component.DcContext = 0;
				}

				if (!progressive || _scan.Ss != 0)
					System.Array.Clear(_acStats[component.AcTableIndex], 0, 256);
			}

			_fakeBytes = 0;
			_c = 0;
			ByteIn();
			_c <<= 8;
			ByteIn();
			_c <<= 8;
			_ct = 0;
			_a = 0x10000;
		}

		/// <summary>
		/// Decodes one block according to the scan type.
		/// </summary>
		/// <param name="component">The component the block belongs to.</param>
		/// <param name="offset">The offset of the block in the coefficient buffer.</param>
		protected override void DecodeBlock(JpegComponent component, int offset)
		{
			// after a decoding error the rest of the interval is left alone (it is resynchronized at the next RSTn).
			if (_corrupt || _frame.IsLossless)
				return;

			var block = component.Coefficients;
			if (!_frame.IsProgressive)
			{
				DecodeDc(component, block, offset, 0);
				DecodeAc(component, block, offset, 1, 63, 0);
			}
			else if (_scan.Ss == 0)
			{
				if (_scan.Ah == 0)
				{
					DecodeDc(component, block, offset, _scan.Al);
				}
				else if (Decode(_fixedBin, 0) != 0)
				{
					// DC refinement: the next bit of the two's complement value.
					block[offset] |= (short)(1 << _scan.Al);
				}
			}
			else
			{
				if (_scan.Ah == 0)
					DecodeAc(component, block, offset, _scan.Ss, _scan.Se, _scan.Al);
				else
					DecodeAcRefine(component, block, offset);
			}
		}

		// decodes a DC difference (F.2.4.1, figures F.19 and F.21-F.24).
		private void DecodeDc(JpegComponent component, short[] block, int offset, int shift)
		{
			var stats = _dcStats[component.DcTableIndex];
			var bin = component.DcContext;

			if (Decode(stats, bin) == 0)
			{
				component.DcContext = 0;
			}
			else
			{
				var sign = Decode(stats, bin + 1);
				bin += 2 + sign;

				// magnitude category: the first decision uses the SP/SN bin, the next ones X1, X2...
				var m = Decode(stats, bin);
				if (m != 0)
				{
					bin = 20;
					while (Decode(stats, bin) != 0)
					{
						m <<= 1;
						if (m == 0x8000)
						{
							_corrupt = true;
							return;
						}

						bin++;
					}
				}

				// conditioning category for the next difference (F.1.4.4.1.2).
				var tableIndex = component.DcTableIndex;
				if (m < (1 << _tables.ArithmeticDcL[tableIndex]) >> 1)
					component.DcContext = 0;
				else if (m > (1 << _tables.ArithmeticDcU[tableIndex]) >> 1)
					component.DcContext = 12 + sign * 4;
				else
					component.DcContext = 4 + sign * 4;

				// magnitude bits, using the M bins 14 places after the last X bin.
				var value = m;
				bin += 14;
				while ((m >>= 1) != 0)
				{
					if (Decode(stats, bin) != 0)
						value |= m;
				}

				value++;
				if (sign != 0)
					value = -value;

				component.DcPredictor = (component.DcPredictor + value) & 0xFFFF;
			}

			// the predictor is kept modulo 2^16 and interpreted as a signed value.
			block[offset] = (short)((short)component.DcPredictor << shift);
		}

		// decodes the AC coefficients ss..se of a sequential block or of a progressive first scan (F.2.4.2, G.1.3.2).
		private void DecodeAc(JpegComponent component, short[] block, int offset, int ss, int se, int shift)
		{
			var stats = _acStats[component.AcTableIndex];
			var kx = _tables.ArithmeticAcK[component.AcTableIndex];
			var zigZag = JpegTables.ZigZag;

			for (int k = ss; k <= se; k++)
			{
				var bin = 3 * (k - 1);

				// end of block?
				if (Decode(stats, bin) != 0)
					break;

				// skip zero coefficients.
				while (Decode(stats, bin + 1) == 0)
				{
					bin += 3;
					k++;
					if (k > se)
					{
						_corrupt = true;
						return;
					}
				}

				var sign = Decode(_fixedBin, 0);
				bin += 2;

				var m = Decode(stats, bin);
				if (m != 0)
				{
					if (Decode(stats, bin) != 0)
					{
						m <<= 1;
						bin = k <= kx ? 189 : 217;
						while (Decode(stats, bin) != 0)
						{
							m <<= 1;
							if (m == 0x8000)
							{
								_corrupt = true;
								return;
							}

							bin++;
						}
					}
				}

				var value = m;
				bin += 14;
				while ((m >>= 1) != 0)
				{
					if (Decode(stats, bin) != 0)
						value |= m;
				}

				value++;
				if (sign != 0)
					value = -value;

				block[offset + zigZag[k]] = (short)(value * (1 << shift));
			}
		}

		// decodes a progressive AC refinement scan (G.1.3.3).
		private void DecodeAcRefine(JpegComponent component, short[] block, int offset)
		{
			var stats = _acStats[component.AcTableIndex];
			var zigZag = JpegTables.ZigZag;
			var ss = _scan.Ss;
			var se = _scan.Se;
			var positive = 1 << _scan.Al;
			var negative = -1 << _scan.Al;

			// the end of block of the previous stages: beyond it an EOB decision is coded.
			var previousEnd = se;
			while (previousEnd > 0 && block[offset + zigZag[previousEnd]] == 0)
				previousEnd--;

			for (int k = ss; k <= se; k++)
			{
				var bin = 3 * (k - 1);
				if (k > previousEnd && Decode(stats, bin) != 0)
					break;

				while (true)
				{
					var index = offset + zigZag[k];
					var coefficient = block[index];
					if (coefficient != 0)
					{
						// correction bit of a coefficient that is already nonzero.
						if (Decode(stats, bin + 2) != 0)
							block[index] = (short)(coefficient < 0 ? coefficient + negative : coefficient + positive);

						break;
					}

					if (Decode(stats, bin + 1) != 0)
					{
						// a newly nonzero coefficient: only its sign is coded.
						block[index] = (short)(Decode(_fixedBin, 0) != 0 ? negative : positive);
						break;
					}

					bin += 3;
					k++;
					if (k > se)
					{
						_corrupt = true;
						return;
					}
				}
			}
		}

		// decodes one binary decision with the statistics bin stats[bin] (D.2.4 and D.2.5).
		private int Decode(byte[] stats, int bin)
		{
			int state = stats[bin];
			var index = state & 0x7F;
			var mps = state >> 7;
			int qe = Qe[index];

			_a -= qe;
			int decision;
			if ((int)(_c >> 16) < _a)
			{
				// MPS sub-interval.
				if (_a >= 0x8000)
					return mps;

				// conditional exchange: when the MPS interval became smaller than Qe, the LPS is decoded instead.
				if (_a < qe)
				{
					decision = 1 - mps;
					stats[bin] = (byte)((state & 0x80) ^ SwitchMps[index] | NextLps[index]);
				}
				else
				{
					decision = mps;
					stats[bin] = (byte)((state & 0x80) | NextMps[index]);
				}
			}
			else
			{
				// LPS sub-interval.
				_c -= (uint)_a << 16;
				if (_a < qe)
				{
					decision = mps;
					stats[bin] = (byte)((state & 0x80) | NextMps[index]);
				}
				else
				{
					decision = 1 - mps;
					stats[bin] = (byte)((state & 0x80) ^ SwitchMps[index] | NextLps[index]);
				}

				_a = qe;
			}

			// renormalization (D.2.6).
			do
			{
				if (_ct == 0)
				{
					ByteIn();
					_ct = 8;
				}

				_a <<= 1;
				_c <<= 1;
				_ct--;
			}
			while (_a < 0x8000);

			return decision;
		}

		// reads the next byte into C, removing stuffed zeros (D.2.8); at a marker zeros are supplied.
		private void ByteIn()
		{
			var value = 0;
			while (true)
			{
				if (_marker >= 0 || _position >= _end)
				{
					_fakeBytes++;
					break;
				}

				value = _data[_position];
				if (value != 0xFF)
				{
					_position++;
					break;
				}

				// skip fill bytes.
				var next = _position + 1;
				while (next < _end && _data[next] == 0xFF)
					next++;

				_position = next - 1;
				if (next >= _end)
				{
					_position = _end;
					value = 0;
					continue;
				}

				var code = _data[next];
				if (code == 0)
				{
					_position = next + 1;
					break;
				}

				// corrupt bytes that look like a marker are skipped; a real marker ends the data.
				if (IsBogusMarker(code) || !IsPlausibleMarker(_position))
				{
					_position += 2;
					value = 0;
					continue;
				}

				_marker = code;
				value = 0;
			}

			_c += (uint)value << 8;
		}

		// T.81 table D.2: Switch_MPS is 1 for the states 0, 14, 36, 64, 80, 88, 95, 105, 110 and 112.
		private static byte[] BuildSwitchTable()
		{
			var table = new byte[FixedState + 1];
			foreach (var state in new[] { 0, 14, 36, 64, 80, 88, 95, 105, 110, 112 })
				table[state] = 0x80;

			return table;
		}

		#endregion
	}
}
