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

// Baseline JPEG encoder (ITU T.81): YCbCr 4:2:0 or grayscale, standard
// Huffman tables, separable float DCT.

using System;
using System.IO;

namespace Wisej.AI.Pdf.Imaging
{
	/// <summary>
	/// Encodes <see cref="RasterImage"/> instances as baseline JPEG files.
	/// </summary>
	/// <remarks>
	/// Transparent pixels are composited on white, since JPEG has no alpha channel.
	/// </remarks>
	internal sealed class JpegEncoder
	{
		#region Standard tables (ITU T.81, Annex K)

		/// <summary>Example luminance quantization table (Table K.1), natural (row-major) order, quality 50.</summary>
		private static readonly int[] LuminanceQuantization =
		{
			16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
			14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
			18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
			49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
		};

		/// <summary>Example chrominance quantization table (Table K.2), natural (row-major) order, quality 50.</summary>
		private static readonly int[] ChrominanceQuantization =
		{
			17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99,
			24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
			99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
			99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
		};

		/// <summary>Maps a zig-zag scan position to its natural (row-major) index in an 8x8 block (Figure A.6).</summary>
		private static readonly int[] ZigZag =
		{
			0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
			12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
			35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
			58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
		};

		// typical Huffman tables (K.3): "Bits" holds the number of codes of each length 1..16, "Values" the symbols.
		/// <summary>Number of luminance DC codes of each length 1..16 (Table K.3).</summary>
		private static readonly byte[] DcLuminanceBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };

		/// <summary>Luminance DC symbols (difference categories) in code order.</summary>
		private static readonly byte[] DcLuminanceValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

		/// <summary>Number of chrominance DC codes of each length 1..16 (Table K.4).</summary>
		private static readonly byte[] DcChrominanceBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };

		/// <summary>Chrominance DC symbols (difference categories) in code order.</summary>
		private static readonly byte[] DcChrominanceValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

		/// <summary>Number of luminance AC codes of each length 1..16 (Table K.5).</summary>
		private static readonly byte[] AcLuminanceBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };

		/// <summary>Luminance AC symbols (run &lt;&lt; 4 | size) in code order.</summary>
		private static readonly byte[] AcLuminanceValues =
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
			0xf9, 0xfa,
		};

		/// <summary>Number of chrominance AC codes of each length 1..16 (Table K.6).</summary>
		private static readonly byte[] AcChrominanceBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };

		/// <summary>Chrominance AC symbols (run &lt;&lt; 4 | size) in code order.</summary>
		private static readonly byte[] AcChrominanceValues =
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
			0xf9, 0xfa,
		};

		#endregion

		#region Fields

		/// <summary>Luminance quantization table scaled for the requested quality, natural order.</summary>
		private readonly int[] _luminanceTable = new int[64];

		/// <summary>Chrominance quantization table scaled for the requested quality, natural order.</summary>
		private readonly int[] _chrominanceTable = new int[64];

		/// <summary>Reciprocal luminance quantizers with the AAN DCT scale factors folded in.</summary>
		private readonly float[] _luminanceDivisors = new float[64];

		/// <summary>Reciprocal chrominance quantizers with the AAN DCT scale factors folded in.</summary>
		private readonly float[] _chrominanceDivisors = new float[64];

		// Huffman tables indexed by symbol: the code bits and the code length (built by BuildHuffman).
		/// <summary>Luminance DC and AC Huffman codes and code lengths, indexed by symbol.</summary>
		private readonly int[] _dcLuminanceCodes, _dcLuminanceLengths, _acLuminanceCodes, _acLuminanceLengths;

		/// <summary>Chrominance DC and AC Huffman codes and code lengths, indexed by symbol.</summary>
		private readonly int[] _dcChrominanceCodes, _dcChrominanceLengths, _acChrominanceCodes, _acChrominanceLengths;

		/// <summary>The stream receiving the encoded file.</summary>
		private MemoryStream _output;

		/// <summary>Pending bits not yet flushed to <see cref="_output"/>, right-aligned.</summary>
		private int _bitBuffer;

		/// <summary>Number of valid bits in <see cref="_bitBuffer"/> (0..7 between calls).</summary>
		private int _bitCount;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new encoder.
		/// </summary>
		/// <param name="quality">The quality, 1 (smallest) to 100 (best).</param>
		public JpegEncoder(int quality = 90)
		{
			quality = Math.Max(1, Math.Min(100, quality));
			var scale = quality < 50 ? 5000 / quality : 200 - quality * 2;

			// AAN scale factors fold the DCT normalization into the quantization divisors.
			var aan = new[] { 1.0, 1.387039845, 1.306562965, 1.175875602, 1.0, 0.785694958, 0.541196100, 0.275899379 };
			for (int i = 0; i < 64; i++)
			{
				_luminanceTable[i] = Math.Max(1, Math.Min(255, (LuminanceQuantization[i] * scale + 50) / 100));
				_chrominanceTable[i] = Math.Max(1, Math.Min(255, (ChrominanceQuantization[i] * scale + 50) / 100));

				var row = i / 8;
				var column = i % 8;
				_luminanceDivisors[i] = (float)(1.0 / (_luminanceTable[i] * aan[row] * aan[column] * 8));
				_chrominanceDivisors[i] = (float)(1.0 / (_chrominanceTable[i] * aan[row] * aan[column] * 8));
			}

			BuildHuffman(DcLuminanceBits, DcLuminanceValues, out _dcLuminanceCodes, out _dcLuminanceLengths);
			BuildHuffman(AcLuminanceBits, AcLuminanceValues, out _acLuminanceCodes, out _acLuminanceLengths);
			BuildHuffman(DcChrominanceBits, DcChrominanceValues, out _dcChrominanceCodes, out _dcChrominanceLengths);
			BuildHuffman(AcChrominanceBits, AcChrominanceValues, out _acChrominanceCodes, out _acChrominanceLengths);
		}

		#endregion

		#region Methods

		/// <summary>
		/// Encodes the image.
		/// </summary>
		/// <param name="image">The image.</param>
		/// <returns>The JPEG file bytes.</returns>
		public byte[] Encode(RasterImage image)
		{
			var gray = image.IsGrayscale;
			var width = image.Width;
			var height = image.Height;

			// planes in YCbCr (or Y only), with transparent pixels composited on white.
			var y = new float[width * height];
			var cb = gray ? null : new float[width * height];
			var cr = gray ? null : new float[width * height];
			var pixels = image.Pixels;
			for (int i = 0, p = 0; i < y.Length; i++, p += 4)
			{
				var a = pixels[p + 3] / 255f;
				var r = pixels[p] * a + 255 * (1 - a);
				var g = pixels[p + 1] * a + 255 * (1 - a);
				var b = pixels[p + 2] * a + 255 * (1 - a);

				y[i] = 0.299f * r + 0.587f * g + 0.114f * b - 128;
				if (!gray)
				{
					cb[i] = -0.168736f * r - 0.331264f * g + 0.5f * b;
					cr[i] = 0.5f * r - 0.418688f * g - 0.081312f * b;
				}
			}

			_output = new MemoryStream(width * height / 4 + 1024);
			_bitBuffer = 0;
			_bitCount = 0;

			WriteHeaders(width, height, gray);

			var block = new float[64];
			var coefficients = new int[64];
			int previousY = 0, previousCb = 0, previousCr = 0;

			if (gray)
			{
				for (int by = 0; by < height; by += 8)
				{
					for (int bx = 0; bx < width; bx += 8)
					{
						LoadBlock(y, width, height, bx, by, 1, block);
						previousY = EncodeBlock(block, _luminanceDivisors, previousY, _dcLuminanceCodes, _dcLuminanceLengths, _acLuminanceCodes, _acLuminanceLengths, coefficients);
					}
				}
			}
			else
			{
				// 4:2:0: four luminance blocks and one subsampled block of each chroma plane per 16x16 macroblock.
				for (int my = 0; my < height; my += 16)
				{
					for (int mx = 0; mx < width; mx += 16)
					{
						for (int i = 0; i < 4; i++)
						{
							LoadBlock(y, width, height, mx + (i % 2) * 8, my + (i / 2) * 8, 1, block);
							previousY = EncodeBlock(block, _luminanceDivisors, previousY, _dcLuminanceCodes, _dcLuminanceLengths, _acLuminanceCodes, _acLuminanceLengths, coefficients);
						}

						LoadBlock(cb, width, height, mx, my, 2, block);
						previousCb = EncodeBlock(block, _chrominanceDivisors, previousCb, _dcChrominanceCodes, _dcChrominanceLengths, _acChrominanceCodes, _acChrominanceLengths, coefficients);

						LoadBlock(cr, width, height, mx, my, 2, block);
						previousCr = EncodeBlock(block, _chrominanceDivisors, previousCr, _dcChrominanceCodes, _dcChrominanceLengths, _acChrominanceCodes, _acChrominanceLengths, coefficients);
					}
				}
			}

			// pad the last byte with ones and write the end-of-image marker.
			WriteBits(0x7F, 7);
			_output.WriteByte(0xFF);
			_output.WriteByte(0xD9);

			return _output.ToArray();
		}

		#endregion

		#region Implementation

		// reads an 8x8 block, averaging "step" x "step" pixels (chroma subsampling) and replicating the edges.
		private static void LoadBlock(float[] plane, int width, int height, int x0, int y0, int step, float[] block)
		{
			for (int row = 0; row < 8; row++)
			{
				for (int column = 0; column < 8; column++)
				{
					float sum = 0;
					for (int dy = 0; dy < step; dy++)
					{
						var y = Math.Min(height - 1, y0 + row * step + dy);
						for (int dx = 0; dx < step; dx++)
						{
							var x = Math.Min(width - 1, x0 + column * step + dx);
							sum += plane[y * width + x];
						}
					}

					block[row * 8 + column] = sum / (step * step);
				}
			}
		}

		/// <summary>
		/// Transforms, quantizes and entropy-codes one 8x8 block.
		/// </summary>
		/// <param name="block">The level-shifted samples, natural order; overwritten with the DCT output.</param>
		/// <param name="divisors">The reciprocal quantizers for the component.</param>
		/// <param name="previousDc">The quantized DC value of the previous block of the same component.</param>
		/// <param name="dcCodes">The DC Huffman codes, indexed by symbol.</param>
		/// <param name="dcLengths">The DC Huffman code lengths, indexed by symbol.</param>
		/// <param name="acCodes">The AC Huffman codes, indexed by symbol.</param>
		/// <param name="acLengths">The AC Huffman code lengths, indexed by symbol.</param>
		/// <param name="coefficients">Scratch buffer of 64 entries for the quantized coefficients, zig-zag order.</param>
		/// <returns>The quantized DC value of this block, the predictor for the next block.</returns>
		private int EncodeBlock(float[] block, float[] divisors, int previousDc, int[] dcCodes, int[] dcLengths, int[] acCodes, int[] acLengths, int[] coefficients)
		{
			ForwardDct(block);

			for (int i = 0; i < 64; i++)
			{
				var value = block[ZigZag[i]] * divisors[ZigZag[i]];
				coefficients[i] = (int)Math.Round(value);
			}

			// DC: difference with the previous block.
			var diff = coefficients[0] - previousDc;
			EncodeValue(diff, dcCodes, dcLengths, 0);

			// AC: run-length of zeros + value category.
			var zeros = 0;
			for (int i = 1; i < 64; i++)
			{
				var value = coefficients[i];
				if (value == 0)
				{
					zeros++;
					continue;
				}

				while (zeros >= 16)
				{
					WriteBits(acCodes[0xF0], acLengths[0xF0]);
					zeros -= 16;
				}

				EncodeValue(value, acCodes, acLengths, zeros << 4);
				zeros = 0;
			}

			// end of block.
			if (zeros > 0)
				WriteBits(acCodes[0x00], acLengths[0x00]);

			return coefficients[0];
		}

		// writes the Huffman code of (run << 4 | size) followed by the value bits.
		private void EncodeValue(int value, int[] codes, int[] lengths, int run)
		{
			var magnitude = Math.Abs(value);
			var size = 0;
			while (magnitude > 0)
			{
				size++;
				magnitude >>= 1;
			}

			var symbol = run | size;
			WriteBits(codes[symbol], lengths[symbol]);

			if (size > 0)
			{
				var bits = value < 0 ? value - 1 : value;
				WriteBits(bits & ((1 << size) - 1), size);
			}
		}

		/// <summary>
		/// Appends the low <paramref name="count"/> bits of <paramref name="bits"/> to the entropy-coded data, MSB first,
		/// stuffing a zero byte after each 0xFF (T.81 F.1.2.3).
		/// </summary>
		/// <param name="bits">The bits to write, right-aligned.</param>
		/// <param name="count">The number of bits, 0..16.</param>
		private void WriteBits(int bits, int count)
		{
			_bitBuffer = (_bitBuffer << count) | (bits & ((1 << count) - 1));
			_bitCount += count;
			while (_bitCount >= 8)
			{
				var b = (byte)(_bitBuffer >> (_bitCount - 8));
				_output.WriteByte(b);

				// 0xFF in entropy-coded data is followed by a stuffed zero byte.
				if (b == 0xFF)
					_output.WriteByte(0);

				_bitCount -= 8;
			}

			_bitBuffer &= (1 << _bitCount) - 1;
		}

		// AAN forward DCT, in place, rows then columns.
		private static void ForwardDct(float[] d)
		{
			for (int pass = 0; pass < 2; pass++)
			{
				for (int i = 0; i < 8; i++)
				{
					var stride = pass == 0 ? 1 : 8;
					var o = pass == 0 ? i * 8 : i;

					var t0 = d[o] + d[o + 7 * stride];
					var t7 = d[o] - d[o + 7 * stride];
					var t1 = d[o + stride] + d[o + 6 * stride];
					var t6 = d[o + stride] - d[o + 6 * stride];
					var t2 = d[o + 2 * stride] + d[o + 5 * stride];
					var t5 = d[o + 2 * stride] - d[o + 5 * stride];
					var t3 = d[o + 3 * stride] + d[o + 4 * stride];
					var t4 = d[o + 3 * stride] - d[o + 4 * stride];

					// even part.
					var t10 = t0 + t3;
					var t13 = t0 - t3;
					var t11 = t1 + t2;
					var t12 = t1 - t2;

					d[o] = t10 + t11;
					d[o + 4 * stride] = t10 - t11;

					var z1 = (t12 + t13) * 0.707106781f;
					d[o + 2 * stride] = t13 + z1;
					d[o + 6 * stride] = t13 - z1;

					// odd part.
					t10 = t4 + t5;
					t11 = t5 + t6;
					t12 = t6 + t7;

					var z5 = (t10 - t12) * 0.382683433f;
					var z2 = 0.541196100f * t10 + z5;
					var z4 = 1.306562965f * t12 + z5;
					var z3 = t11 * 0.707106781f;

					var z11 = t7 + z3;
					var z13 = t7 - z3;

					d[o + 5 * stride] = z13 + z2;
					d[o + 3 * stride] = z13 - z2;
					d[o + stride] = z11 + z4;
					d[o + 7 * stride] = z11 - z4;
				}
			}
		}

		/// <summary>
		/// Writes SOI, the JFIF APP0 segment, DQT, SOF0, DHT and SOS.
		/// </summary>
		/// <param name="width">The image width in pixels.</param>
		/// <param name="height">The image height in pixels.</param>
		/// <param name="gray">True for a single-component file; false for YCbCr 4:2:0.</param>
		private void WriteHeaders(int width, int height, bool gray)
		{
			var o = _output;

			// SOI and JFIF APP0.
			o.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 }, 0, 20);

			// DQT.
			WriteQuantizationTable(0, _luminanceTable);
			if (!gray)
				WriteQuantizationTable(1, _chrominanceTable);

			// SOF0.
			var components = gray ? 1 : 3;
			WriteMarker(0xC0, 8 + components * 3);
			o.WriteByte(8);
			o.WriteByte((byte)(height >> 8));
			o.WriteByte((byte)height);
			o.WriteByte((byte)(width >> 8));
			o.WriteByte((byte)width);
			o.WriteByte((byte)components);
			if (gray)
			{
				o.Write(new byte[] { 1, 0x11, 0 }, 0, 3);
			}
			else
			{
				o.Write(new byte[] { 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 }, 0, 9);
			}

			// DHT.
			WriteHuffmanTable(0x00, DcLuminanceBits, DcLuminanceValues);
			WriteHuffmanTable(0x10, AcLuminanceBits, AcLuminanceValues);
			if (!gray)
			{
				WriteHuffmanTable(0x01, DcChrominanceBits, DcChrominanceValues);
				WriteHuffmanTable(0x11, AcChrominanceBits, AcChrominanceValues);
			}

			// SOS.
			WriteMarker(0xDA, 6 + components * 2);
			o.WriteByte((byte)components);
			if (gray)
				o.Write(new byte[] { 1, 0x00 }, 0, 2);
			else
				o.Write(new byte[] { 1, 0x00, 2, 0x11, 3, 0x11 }, 0, 6);
			o.Write(new byte[] { 0, 63, 0 }, 0, 3);
		}

		/// <summary>
		/// Writes a marker followed by its 16-bit big-endian segment length.
		/// </summary>
		/// <param name="marker">The marker code (the byte after 0xFF).</param>
		/// <param name="length">The segment length, including the two length bytes.</param>
		private void WriteMarker(byte marker, int length)
		{
			_output.WriteByte(0xFF);
			_output.WriteByte(marker);
			_output.WriteByte((byte)(length >> 8));
			_output.WriteByte((byte)length);
		}

		/// <summary>
		/// Writes a DQT segment holding one 8-bit table in zig-zag order.
		/// </summary>
		/// <param name="id">The table destination identifier.</param>
		/// <param name="table">The quantization table, natural order.</param>
		private void WriteQuantizationTable(byte id, int[] table)
		{
			WriteMarker(0xDB, 67);
			_output.WriteByte(id);
			for (int i = 0; i < 64; i++)
				_output.WriteByte((byte)table[ZigZag[i]]);
		}

		/// <summary>
		/// Writes a DHT segment holding one table.
		/// </summary>
		/// <param name="id">The table class (high nibble: 0 = DC, 1 = AC) and destination (low nibble).</param>
		/// <param name="bits">The number of codes of each length 1..16.</param>
		/// <param name="values">The symbols in code order.</param>
		private void WriteHuffmanTable(byte id, byte[] bits, byte[] values)
		{
			WriteMarker(0xC4, 3 + 16 + values.Length);
			_output.WriteByte(id);
			_output.Write(bits, 0, 16);
			_output.Write(values, 0, values.Length);
		}

		// canonical Huffman codes from the bit counts (Annex C).
		private static void BuildHuffman(byte[] bits, byte[] values, out int[] codes, out int[] lengths)
		{
			codes = new int[256];
			lengths = new int[256];
			var code = 0;
			var k = 0;
			for (int length = 1; length <= 16; length++)
			{
				for (int i = 0; i < bits[length - 1]; i++)
				{
					codes[values[k]] = code;
					lengths[values[k]] = length;
					k++;
					code++;
				}

				code <<= 1;
			}
		}

		#endregion
	}
}
