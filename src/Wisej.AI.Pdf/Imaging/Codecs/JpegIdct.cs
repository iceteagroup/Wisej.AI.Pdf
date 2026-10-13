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

// Accurate integer inverse DCT with dequantization (ITU-T T.81, A.3.3), using the
// Loeffler-Ligtenberg-Moschytz factorization with 13-bit fixed-point constants.

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Dequantizes and inverse-transforms 8x8 blocks of DCT coefficients into 8-bit samples.
	/// </summary>
	/// <remarks>
	/// The transform is the separable 1-D LLM algorithm (12 multiplications per 8 points) in 32-bit
	/// fixed point, the same accuracy class as the "islow" method of the reference decoders: the
	/// results are within one level of an exact floating-point IDCT. Columns are processed first,
	/// keeping <c>pass1Bits</c> extra fraction bits for the row pass; 12-bit data keeps one bit
	/// less so that the intermediate values still fit in 32 bits.
	/// </remarks>
	internal static class JpegIdct
	{
		#region Fields

		// the fraction bits of the fixed-point constants.
		private const int ConstBits = 13;

		// the LLM rotation constants, round(c * 2^13), named after c with the decimal point removed.
		/// <summary>0.298631336 in 13-bit fixed point.</summary>
		private const int Fix0298631336 = 2446;
		/// <summary>0.390180644 in 13-bit fixed point.</summary>
		private const int Fix0390180644 = 3196;
		/// <summary>0.541196100 in 13-bit fixed point.</summary>
		private const int Fix0541196100 = 4433;
		/// <summary>0.765366865 in 13-bit fixed point.</summary>
		private const int Fix0765366865 = 6270;
		/// <summary>0.899976223 in 13-bit fixed point.</summary>
		private const int Fix0899976223 = 7373;
		/// <summary>1.175875602 in 13-bit fixed point.</summary>
		private const int Fix1175875602 = 9633;
		/// <summary>1.501321110 in 13-bit fixed point.</summary>
		private const int Fix1501321110 = 12299;
		/// <summary>1.847759065 in 13-bit fixed point.</summary>
		private const int Fix1847759065 = 15137;
		/// <summary>1.961570560 in 13-bit fixed point.</summary>
		private const int Fix1961570560 = 16069;
		/// <summary>2.053119869 in 13-bit fixed point.</summary>
		private const int Fix2053119869 = 16819;
		/// <summary>2.562915447 in 13-bit fixed point.</summary>
		private const int Fix2562915447 = 20995;
		/// <summary>3.072711026 in 13-bit fixed point.</summary>
		private const int Fix3072711026 = 25172;

		// maps a 12-bit sample to 8 bits with rounding.
		private static readonly byte[] Scale12To8 = BuildScaleTable();

		#endregion

		#region Methods

		/// <summary>
		/// Dequantizes and inverse-transforms one block.
		/// </summary>
		/// <param name="coefficients">The quantized coefficients, natural order.</param>
		/// <param name="offset">The offset of the block in <paramref name="coefficients"/>.</param>
		/// <param name="quant">The quantization table, natural order.</param>
		/// <param name="workspace">A scratch buffer of 64 integers.</param>
		/// <param name="output">The 8-bit sample plane.</param>
		/// <param name="outputOffset">The offset of the top-left sample of the block in <paramref name="output"/>.</param>
		/// <param name="stride">The number of bytes per row of <paramref name="output"/>.</param>
		/// <param name="precision">The sample precision: 8 or 12 bits.</param>
		public static void Transform(short[] coefficients, int offset, ushort[] quant, int[] workspace, byte[] output, int outputOffset, int stride, int precision)
		{
			var pass1Bits = precision > 8 ? 1 : 2;

			// pass 1: columns, from the coefficients into the workspace.
			for (int column = 0; column < 8; column++)
			{
				var i = offset + column;

				// a column with only a DC term (frequent) produces a constant column.
				if (coefficients[i + 8] == 0 && coefficients[i + 16] == 0 && coefficients[i + 24] == 0 && coefficients[i + 32] == 0
					&& coefficients[i + 40] == 0 && coefficients[i + 48] == 0 && coefficients[i + 56] == 0)
				{
					var dc = (coefficients[i] * quant[column]) << pass1Bits;
					for (int row = 0; row < 64; row += 8)
						workspace[column + row] = dc;

					continue;
				}

				// even part.
				var z2 = coefficients[i + 16] * quant[column + 16];
				var z3 = coefficients[i + 48] * quant[column + 48];
				var z1 = (z2 + z3) * Fix0541196100;
				var tmp2 = z1 - z3 * Fix1847759065;
				var tmp3 = z1 + z2 * Fix0765366865;

				z2 = coefficients[i] * quant[column];
				z3 = coefficients[i + 32] * quant[column + 32];
				var tmp0 = (z2 + z3) << ConstBits;
				var tmp1 = (z2 - z3) << ConstBits;

				var tmp10 = tmp0 + tmp3;
				var tmp13 = tmp0 - tmp3;
				var tmp11 = tmp1 + tmp2;
				var tmp12 = tmp1 - tmp2;

				// odd part.
				tmp0 = coefficients[i + 56] * quant[column + 56];
				tmp1 = coefficients[i + 40] * quant[column + 40];
				tmp2 = coefficients[i + 24] * quant[column + 24];
				tmp3 = coefficients[i + 8] * quant[column + 8];

				OddPart(ref tmp0, ref tmp1, ref tmp2, ref tmp3);

				var shift = ConstBits - pass1Bits;
				var round = 1 << (shift - 1);
				workspace[column] = (tmp10 + tmp3 + round) >> shift;
				workspace[column + 56] = (tmp10 - tmp3 + round) >> shift;
				workspace[column + 8] = (tmp11 + tmp2 + round) >> shift;
				workspace[column + 48] = (tmp11 - tmp2 + round) >> shift;
				workspace[column + 16] = (tmp12 + tmp1 + round) >> shift;
				workspace[column + 40] = (tmp12 - tmp1 + round) >> shift;
				workspace[column + 24] = (tmp13 + tmp0 + round) >> shift;
				workspace[column + 32] = (tmp13 - tmp0 + round) >> shift;
			}

			// pass 2: rows, from the workspace into the plane, removing the level shift (A.3.1).
			var twelveBit = precision > 8;
			var center = twelveBit ? 2048 : 128;
			var finalShift = ConstBits + pass1Bits + 3;
			var finalRound = 1 << (finalShift - 1);
			var dcShift = pass1Bits + 3;
			var dcRound = 1 << (dcShift - 1);

			for (int row = 0; row < 64; row += 8)
			{
				var target = outputOffset + (row >> 3) * stride;

				if (workspace[row + 1] == 0 && workspace[row + 2] == 0 && workspace[row + 3] == 0 && workspace[row + 4] == 0
					&& workspace[row + 5] == 0 && workspace[row + 6] == 0 && workspace[row + 7] == 0)
				{
					var value = Clamp(((workspace[row] + dcRound) >> dcShift) + center, twelveBit);
					for (int x = 0; x < 8; x++)
						output[target + x] = value;

					continue;
				}

				// even part.
				var z2 = workspace[row + 2];
				var z3 = workspace[row + 6];
				var z1 = (z2 + z3) * Fix0541196100;
				var tmp2 = z1 - z3 * Fix1847759065;
				var tmp3 = z1 + z2 * Fix0765366865;

				var tmp0 = (workspace[row] + workspace[row + 4]) << ConstBits;
				var tmp1 = (workspace[row] - workspace[row + 4]) << ConstBits;

				var tmp10 = tmp0 + tmp3;
				var tmp13 = tmp0 - tmp3;
				var tmp11 = tmp1 + tmp2;
				var tmp12 = tmp1 - tmp2;

				// odd part.
				tmp0 = workspace[row + 7];
				tmp1 = workspace[row + 5];
				tmp2 = workspace[row + 3];
				tmp3 = workspace[row + 1];

				OddPart(ref tmp0, ref tmp1, ref tmp2, ref tmp3);

				output[target] = Clamp(((tmp10 + tmp3 + finalRound) >> finalShift) + center, twelveBit);
				output[target + 7] = Clamp(((tmp10 - tmp3 + finalRound) >> finalShift) + center, twelveBit);
				output[target + 1] = Clamp(((tmp11 + tmp2 + finalRound) >> finalShift) + center, twelveBit);
				output[target + 6] = Clamp(((tmp11 - tmp2 + finalRound) >> finalShift) + center, twelveBit);
				output[target + 2] = Clamp(((tmp12 + tmp1 + finalRound) >> finalShift) + center, twelveBit);
				output[target + 5] = Clamp(((tmp12 - tmp1 + finalRound) >> finalShift) + center, twelveBit);
				output[target + 3] = Clamp(((tmp13 + tmp0 + finalRound) >> finalShift) + center, twelveBit);
				output[target + 4] = Clamp(((tmp13 - tmp0 + finalRound) >> finalShift) + center, twelveBit);
			}
		}

		/// <summary>
		/// Converts a sample of the frame precision to 8 bits.
		/// </summary>
		/// <param name="value">The sample.</param>
		/// <param name="twelveBit">True when <paramref name="value"/> has 12-bit precision.</param>
		/// <returns>The 8-bit sample.</returns>
		public static byte Clamp(int value, bool twelveBit)
		{
			if (twelveBit)
			{
				if ((uint)value > 4095)
					value = value < 0 ? 0 : 4095;

				return Scale12To8[value];
			}

			if ((uint)value > 255)
				return value < 0 ? (byte)0 : (byte)255;

			return (byte)value;
		}

		#endregion

		#region Implementation

		// the odd part of the 1-D transform: rotates the inputs 7, 5, 3, 1 into the outputs that
		// are added to / subtracted from the even part.
		private static void OddPart(ref int tmp0, ref int tmp1, ref int tmp2, ref int tmp3)
		{
			var z1 = tmp0 + tmp3;
			var z2 = tmp1 + tmp2;
			var z3 = tmp0 + tmp2;
			var z4 = tmp1 + tmp3;
			var z5 = (z3 + z4) * Fix1175875602;

			tmp0 *= Fix0298631336;
			tmp1 *= Fix2053119869;
			tmp2 *= Fix3072711026;
			tmp3 *= Fix1501321110;
			z1 *= -Fix0899976223;
			z2 *= -Fix2562915447;
			z3 = z3 * -Fix1961570560 + z5;
			z4 = z4 * -Fix0390180644 + z5;

			tmp0 += z1 + z3;
			tmp1 += z2 + z4;
			tmp2 += z2 + z3;
			tmp3 += z1 + z4;
		}

		// builds the 12-bit to 8-bit table: round(v * 255 / 4095).
		private static byte[] BuildScaleTable()
		{
			var table = new byte[4096];
			for (int i = 0; i < 4096; i++)
				table[i] = (byte)((i * 255 + 2047) / 4095);

			return table;
		}

		#endregion
	}
}
