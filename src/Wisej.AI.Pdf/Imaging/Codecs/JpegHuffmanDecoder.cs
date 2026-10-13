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

// Huffman entropy decoder for sequential and progressive JPEG scans
// (ITU-T T.81, F.2.2 and G.1.2).

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Decodes Huffman-coded scans: baseline, extended sequential, progressive and lossless.
	/// </summary>
	internal sealed partial class JpegHuffmanDecoder : JpegEntropyDecoder
	{
		#region Fields

		// the bit buffer: the next bits are left-aligned in _bits, _count of them are valid.
		private ulong _bits;

		// number of valid bits in _bits.
		private int _count;

		// number of zero bits appended after the end of the segment (see IsExhausted).
		private int _fakeBits;

		// remaining blocks of the current end-of-band run (progressive AC scans).
		private int _eobRun;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new instance of the <see cref="JpegHuffmanDecoder"/> class.
		/// </summary>
		/// <param name="data">The JPEG stream.</param>
		/// <param name="end">The end of the usable data.</param>
		public JpegHuffmanDecoder(byte[] data, int end)
			: base(data, end)
		{
		}

		#endregion

		#region Implementation

		/// <summary>
		/// Gets whether the decoder consumed bits past the end of the entropy-coded segment.
		/// </summary>
		/// <remarks>
		/// Zero bits are appended once a marker or the end of the data is reached. A valid stream
		/// never needs them, so consuming any means the data is truncated or corrupt.
		/// </remarks>
		protected override bool IsExhausted
		{
			get { return _fakeBits > _count; }
		}

		/// <summary>
		/// Discards the buffered bits.
		/// </summary>
		protected override void ResetReader()
		{
			_bits = 0;
			_count = 0;
			_fakeBits = 0;
		}

		/// <summary>
		/// Resets the predictors and the bit buffer at the start of a restart interval.
		/// </summary>
		/// <param name="unit">The index of the first unit of the interval.</param>
		/// <param name="unitsPerLine">The number of units per line.</param>
		protected override void StartInterval(long unit, int unitsPerLine)
		{
			ResetReader();
			_eobRun = 0;

			foreach (var component in _scan.Components)
			{
				component.DcPredictor = 0;
				component.IntervalRow = (int)(unit / unitsPerLine) * (_scan.Components.Length == 1 ? 1 : component.V);
			}
		}

		/// <summary>
		/// Decodes one unit, dispatching lossless frames to the sample decoder.
		/// </summary>
		/// <param name="unit">The unit index.</param>
		/// <param name="unitsPerLine">The number of units per line.</param>
		/// <param name="single">True for a non-interleaved scan.</param>
		protected override void DecodeUnit(long unit, int unitsPerLine, bool single)
		{
			if (_frame.IsLossless)
				DecodeLosslessUnit(unit, unitsPerLine, single);
			else
				base.DecodeUnit(unit, unitsPerLine, single);
		}

		/// <summary>
		/// Decodes one block according to the scan type.
		/// </summary>
		/// <param name="component">The component the block belongs to.</param>
		/// <param name="offset">The offset of the block in the coefficient buffer.</param>
		protected override void DecodeBlock(JpegComponent component, int offset)
		{
			if (!_frame.IsProgressive)
			{
				DecodeBaseline(component, component.Coefficients, offset);
			}
			else if (_scan.Ss == 0)
			{
				if (_scan.Ah == 0)
					DecodeDcFirst(component, component.Coefficients, offset);
				else
					DecodeDcRefine(component.Coefficients, offset);
			}
			else
			{
				if (_scan.Ah == 0)
					DecodeAcFirst(component, component.Coefficients, offset);
				else
					DecodeAcRefine(component, component.Coefficients, offset);
			}
		}

		// sequential DCT block: DC difference followed by the run-length coded AC coefficients (F.2.2).
		private void DecodeBaseline(JpegComponent component, short[] block, int offset)
		{
			var size = DecodeSymbol(component.DcTable);
			var diff = size == 0 ? 0 : ReceiveExtend(size);
			component.DcPredictor += diff;
			block[offset] = (short)component.DcPredictor;

			var table = component.AcTable;
			var fastAc = table.FastAc;
			var zigZag = JpegTables.ZigZag;
			for (int k = 1; k < 64;)
			{
				if (_count < 16)
					Fill();

				// most coefficients are resolved together with their magnitude bits in one lookup.
				var fast = fastAc[(int)(_bits >> (64 - JpegHuffmanTable.FastBits))];
				if (fast != 0)
				{
					var length = fast & 15;
					_bits <<= length;
					_count -= length;
					k += (fast >> 4) & 15;
					block[offset + zigZag[k]] = (short)(fast >> 8);
					k++;
					continue;
				}

				var symbol = DecodeSymbol(table);
				var run = symbol >> 4;
				var magnitude = symbol & 15;
				if (magnitude == 0)
				{
					// EOB, or ZRL (sixteen zeros).
					if (run != 15)
						break;

					k += 16;
					continue;
				}

				k += run;
				var value = ReceiveExtend(magnitude);
				if (k > 63)
				{
					_corrupt = true;
					break;
				}

				block[offset + zigZag[k]] = (short)value;
				k++;
			}
		}

		// progressive DC first scan (G.1.2.1).
		private void DecodeDcFirst(JpegComponent component, short[] block, int offset)
		{
			var size = DecodeSymbol(component.DcTable);
			var diff = size == 0 ? 0 : ReceiveExtend(size);
			component.DcPredictor += diff;
			block[offset] = (short)(component.DcPredictor << _scan.Al);
		}

		// progressive DC refinement: one raw bit per block.
		private void DecodeDcRefine(short[] block, int offset)
		{
			if (ReadBit() != 0)
				block[offset] |= (short)(1 << _scan.Al);
		}

		// progressive AC first scan with end-of-band runs (G.1.2.2).
		private void DecodeAcFirst(JpegComponent component, short[] block, int offset)
		{
			if (_eobRun > 0)
			{
				_eobRun--;
				return;
			}

			var table = component.AcTable;
			var zigZag = JpegTables.ZigZag;
			var se = _scan.Se;
			var shift = _scan.Al;
			for (int k = _scan.Ss; k <= se; k++)
			{
				var symbol = DecodeSymbol(table);
				var run = symbol >> 4;
				var magnitude = symbol & 15;
				if (magnitude == 0)
				{
					if (run < 15)
					{
						// EOBn: this block and the next (2^n - 1 + extra) blocks end here.
						_eobRun = (1 << run) - 1;
						if (run > 0)
							_eobRun += ReadBits(run);

						break;
					}

					k += 15;
					continue;
				}

				k += run;
				var value = ReceiveExtend(magnitude);
				if (k > 63)
				{
					_corrupt = true;
					break;
				}

				block[offset + zigZag[k]] = (short)(value * (1 << shift));
			}
		}

		// progressive AC refinement scan (G.1.2.3): adds one bit to the coefficients that are already
		// nonzero, and places the new +-1 coefficients among the zero ones.
		private void DecodeAcRefine(JpegComponent component, short[] block, int offset)
		{
			var table = component.AcTable;
			var zigZag = JpegTables.ZigZag;
			var se = _scan.Se;
			var positive = 1 << _scan.Al;
			var negative = -1 << _scan.Al;
			var k = _scan.Ss;

			if (_eobRun == 0)
			{
				for (; k <= se; k++)
				{
					var symbol = DecodeSymbol(table);
					var run = symbol >> 4;
					var magnitude = symbol & 15;
					var value = 0;

					if (magnitude == 0)
					{
						if (run < 15)
						{
							_eobRun = 1 << run;
							if (run > 0)
								_eobRun += ReadBits(run);

							// the rest of this block is handled by the end-of-band code below.
							break;
						}

						// ZRL: skip sixteen zero coefficients.
					}
					else
					{
						// the size of a new coefficient is always 1; anything else is corrupt but harmless.
						value = ReadBit() != 0 ? positive : negative;
					}

					// advance over the already-nonzero coefficients (refining them) and over
					// `run` zero coefficients; the new value goes into the next zero one.
					for (; k <= se; k++)
					{
						var index = offset + zigZag[k];
						var coefficient = block[index];
						if (coefficient != 0)
						{
							if (ReadBit() != 0 && (coefficient & positive) == 0)
								block[index] = (short)(coefficient >= 0 ? coefficient + positive : coefficient + negative);
						}
						else
						{
							if (run == 0)
							{
								if (value != 0)
									block[index] = (short)value;

								break;
							}

							run--;
						}
					}
				}
			}

			if (_eobRun > 0)
			{
				// inside an end-of-band run only the existing coefficients receive correction bits.
				for (; k <= se; k++)
				{
					var index = offset + zigZag[k];
					var coefficient = block[index];
					if (coefficient != 0 && ReadBit() != 0 && (coefficient & positive) == 0)
						block[index] = (short)(coefficient >= 0 ? coefficient + positive : coefficient + negative);
				}

				_eobRun--;
			}
		}

		// refills the bit buffer up to at least 57 bits, handling stuffed bytes and markers (F.1.2.3).
		private void Fill()
		{
			var data = _data;
			while (_count <= 56)
			{
				int value;
				if (_marker >= 0 || _position >= _end)
				{
					// past the end of the segment: feed zeros and remember how many.
					value = 0;
					_fakeBits += 8;
				}
				else
				{
					value = data[_position];
					if (value == 0xFF)
					{
						var next = _position + 1 < _end ? data[_position + 1] : -1;
						if (next == 0)
						{
							_position += 2;
						}
						else if (next == 0xFF)
						{
							// fill byte before a marker.
							_position++;
							continue;
						}
						else if (next < 0)
						{
							_position = _end;
							continue;
						}
						else if (IsBogusMarker(next))
						{
							// not a marker that can end a scan: corrupt bytes, skip them.
							_position += 2;
							continue;
						}
						else if (!IsPlausibleMarker(_position))
						{
							// a marker code without a valid segment: corrupt data, skip it.
							_position += 2;
							continue;
						}
						else
						{
							_marker = next;
							continue;
						}
					}
					else
					{
						_position++;
					}
				}

				_bits |= (ulong)value << (56 - _count);
				_count += 8;
			}
		}

		// decodes one Huffman symbol.
		private int DecodeSymbol(JpegHuffmanTable table)
		{
			if (_count < 16)
				Fill();

			var entry = table.Fast[(int)(_bits >> (64 - JpegHuffmanTable.FastBits))];
			if (entry != 0)
			{
				var length = entry >> 8;
				_bits <<= length;
				_count -= length;
				return entry & 0xFF;
			}

			// codes longer than the fast lookup.
			var code16 = (int)(_bits >> 48);
			var maxCode = table.MaxCode;
			for (int length = JpegHuffmanTable.FastBits + 1; length <= 16; length++)
			{
				var code = code16 >> (16 - length);
				if (code <= maxCode[length])
				{
					_bits <<= length;
					_count -= length;
					return table.Values[(code + table.ValueOffset[length]) & 0xFF];
				}
			}

			// not a valid code: skip a bit so that decoding always progresses, and return 0 (EOB / zero difference).
			_corrupt = true;
			_bits <<= 1;
			_count--;
			return 0;
		}

		// reads `size` (1-16) bits and converts them to a signed value (F.2.2.1, EXTEND).
		private int ReceiveExtend(int size)
		{
			if (_count < size)
				Fill();

			var value = (int)(_bits >> (64 - size));
			_bits <<= size;
			_count -= size;
			return value < (1 << (size - 1)) ? value - (1 << size) + 1 : value;
		}

		// reads `size` (1-16) raw bits.
		private int ReadBits(int size)
		{
			if (_count < size)
				Fill();

			var value = (int)(_bits >> (64 - size));
			_bits <<= size;
			_count -= size;
			return value;
		}

		// reads one raw bit.
		private int ReadBit()
		{
			if (_count < 1)
				Fill();

			var value = (int)(_bits >> 63);
			_bits <<= 1;
			_count--;
			return value;
		}

		#endregion
	}
}
