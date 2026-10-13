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

// Generic region and generic refinement region decoding procedures (ITU T.88, 6.2 and 6.3).

using System;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Implements the arithmetic generic region decoding procedure (6.2.5) and the generic
	/// refinement region decoding procedure (6.3.5) on packed bitmaps.
	/// </summary>
	/// <remarks>
	/// The context of a pixel is built from sliding windows over the packed rows instead of reading
	/// every template pixel individually: for each output byte, 24 bits of the two rows above are
	/// loaded once and every pixel extracts its part of the template with a shift and a mask. Only the
	/// adaptive template (AT) pixels that are not at their nominal positions are read one by one.
	/// The bit layout of the contexts follows Figure 3 to Figure 6 of the standard, so that the
	/// pseudo-pixel contexts of typical prediction (6.2.5.7) have their standard values.
	/// </remarks>
	internal static class Jbig2GenericRegion
	{
		#region Fields

		/// <summary>
		/// The number of arithmetic contexts needed by any generic region template.
		/// </summary>
		public const int GenericContextSize = 1 << 16;

		/// <summary>
		/// The number of arithmetic contexts needed by any refinement template.
		/// </summary>
		public const int RefinementContextSize = 1 << 13;

		// the context of the SLTP pseudo-pixel for each template (6.2.5.7, Figures 8 to 11).
		private static readonly int[] TypicalContexts = { 0x9B25, 0x0795, 0x00E5, 0x0195 };

		// the number of adaptive template pixels of each template.
		private static readonly int[] AtCounts = { 4, 1, 1, 1 };

		// nominal AT positions (x, y) of each template (6.2.5.4, Figures 7 and 8).
		private static readonly int[][] NominalAt =
		{
			new int[] { 3, -1, -3, -1, 2, -2, -2, -2 },
			new int[] { 3, -1 },
			new int[] { 2, -1 },
			new int[] { 2, -1 },
		};

		// context bit occupied by each AT pixel.
		private static readonly int[][] AtBits =
		{
			new[] { 4, 10, 11, 15 },
			new[] { 3 },
			new[] { 2 },
			new[] { 4 },
		};

		// per template: shift and mask of the window of row y - 2, its position in the context,
		// shift and mask of the window of row y - 1, its position, and the mask of the current row.
		private static readonly int[][] Layout =
		{
			new[] { 13, 0x1F, 11, 12, 0x7F, 4, 0xF },
			new[] { 13, 0x0F, 9, 12, 0x3F, 3, 0x7 },
			new[] { 14, 0x07, 7, 13, 0x1F, 2, 0x3 },
			new[] { 0, 0x00, 0, 13, 0x3F, 4, 0xF },
		};

		#endregion

		#region Methods

		/// <summary>
		/// Returns the default AT pixels of a template, as stored in the generic region segment header.
		/// </summary>
		/// <param name="template">The template number, 0 to 3.</param>
		/// <returns>A new array with the x and y offsets of each AT pixel.</returns>
		public static int[] GetNominalAt(int template)
		{
			return (int[])NominalAt[template & 3].Clone();
		}

		/// <summary>
		/// Returns the number of AT pixels used by a template.
		/// </summary>
		/// <param name="template">The template number, 0 to 3.</param>
		/// <returns>4 for template 0, 1 otherwise.</returns>
		public static int GetAtCount(int template)
		{
			return AtCounts[template & 3];
		}

		/// <summary>
		/// Decodes an arithmetically coded generic region into <paramref name="bitmap"/> (6.2.5.7).
		/// </summary>
		/// <param name="decoder">The arithmetic decoder.</param>
		/// <param name="contexts">The <see cref="GenericContextSize"/> GB contexts.</param>
		/// <param name="bitmap">The cleared bitmap that receives the region.</param>
		/// <param name="template">GBTEMPLATE, 0 to 3.</param>
		/// <param name="typicalPrediction">TPGDON.</param>
		/// <param name="at">The AT pixels as (x, y) pairs; <see cref="GetAtCount"/> pairs are used.</param>
		/// <param name="skip">The SKIP bitmap of USESKIP, or null.</param>
		public static void Decode(
			Jbig2ArithmeticDecoder decoder,
			byte[] contexts,
			Jbig2Bitmap bitmap,
			int template,
			bool typicalPrediction,
			int[] at,
			Jbig2Bitmap skip)
		{
			template &= 3;
			var width = bitmap.Width;
			var height = bitmap.Height;
			var stride = bitmap.Stride;
			var data = bitmap.Data;
			if (width <= 0 || height <= 0)
				return;

			var layout = Layout[template];
			var shift2 = layout[0];
			var mask2 = layout[1];
			var pos2 = layout[2];
			var shift1 = layout[3];
			var mask1 = layout[4];
			var pos1 = layout[5];
			var mask0 = layout[6];

			// collect the AT pixels that are not at their nominal position: their bit in the window
			// context is cleared and replaced by the pixel read from the actual position.
			var atCount = AtCounts[template];
			var nominal = NominalAt[template];
			var atX = new int[atCount];
			var atY = new int[atCount];
			var atBit = new int[atCount];
			var movedCount = 0;
			var clearMask = ~0;
			for (var i = 0; i < atCount; i++)
			{
				int ax = nominal[2 * i];
				int ay = nominal[2 * i + 1];
				if (at != null && at.Length >= 2 * i + 2)
				{
					ax = at[2 * i];
					ay = at[2 * i + 1];
				}

				if (ax == nominal[2 * i] && ay == nominal[2 * i + 1])
					continue;

				atX[movedCount] = ax;
				atY[movedCount] = ay;
				atBit[movedCount] = AtBits[template][i];
				clearMask &= ~(1 << AtBits[template][i]);
				movedCount++;
			}

			var sltpContext = TypicalContexts[template];
			var ltp = 0;
			byte[] skipData = skip?.Data;
			var skipStride = skip?.Stride ?? 0;
			var simple = movedCount == 0 && skip == null;

			for (var y = 0; y < height; y++)
			{
				// far past the end of the data the decoder only produces filler: the rest stays white.
				if (decoder.IsExhausted)
					return;

				var rowOffset = y * stride;

				if (typicalPrediction)
				{
					ltp ^= decoder.DecodeBit(contexts, sltpContext);
					if (ltp != 0)
					{
						// typical row: a copy of the previous one (white above the first row).
						if (y > 0)
							Buffer.BlockCopy(data, rowOffset - stride, data, rowOffset, stride);

						continue;
					}
				}

				var row1 = y >= 1 ? rowOffset - stride : -1;
				var row2 = y >= 2 ? rowOffset - 2 * stride : -1;
				var skipRow = skipData != null && y < skip.Height ? y * skipStride : -1;
				var current = 0;

				for (var bx = 0; bx < stride; bx++)
				{
					// 24-bit windows over the rows above, holding the pixels bx*8-8 .. bx*8+15.
					var w1 = 0;
					if (row1 >= 0)
					{
						w1 = data[row1 + bx] << 8;
						if (bx > 0)
							w1 |= data[row1 + bx - 1] << 16;

						if (bx + 1 < stride)
							w1 |= data[row1 + bx + 1];
					}

					var w2 = 0;
					if (row2 >= 0)
					{
						w2 = data[row2 + bx] << 8;
						if (bx > 0)
							w2 |= data[row2 + bx - 1] << 16;

						if (bx + 1 < stride)
							w2 |= data[row2 + bx + 1];
					}

					var x0 = bx << 3;
					var count = Math.Min(8, width - x0);
					var outIndex = rowOffset + bx;
					var outByte = 0;

					if (simple)
					{
						// the common case: nominal AT pixels and no skip mask.
						for (var k = 0; k < count; k++)
						{
							var cx = (((w2 >> (shift2 - k)) & mask2) << pos2)
								| (((w1 >> (shift1 - k)) & mask1) << pos1)
								| (current & mask0);

							var pixel = decoder.DecodeBit(contexts, cx);
							current = (current << 1) | pixel;
							outByte |= pixel << (7 - k);
						}

						data[outIndex] = (byte)outByte;
						continue;
					}

					for (var k = 0; k < count; k++)
					{
						int pixel;
						if (skipRow >= 0 && x0 + k < skip.Width && (skipData[skipRow + bx] & (0x80 >> k)) != 0)
						{
							// pixels in the skip mask are not coded and are 0 (6.2.5.7, 3 c i).
							pixel = 0;
						}
						else
						{
							var cx = (((w2 >> (shift2 - k)) & mask2) << pos2)
								| (((w1 >> (shift1 - k)) & mask1) << pos1)
								| (current & mask0);

							if (movedCount != 0)
							{
								// the current byte is flushed first, because a moved AT pixel can
								// refer to a pixel decoded earlier in the same byte.
								data[outIndex] = (byte)outByte;
								cx &= clearMask;
								for (var i = 0; i < movedCount; i++)
								{
									var px = x0 + k + atX[i];
									var py = y + atY[i];
									if ((uint)px < (uint)width && (uint)py < (uint)height
										&& ((data[py * stride + (px >> 3)] >> (7 - (px & 7))) & 1) != 0)
									{
										cx |= 1 << atBit[i];
									}
								}
							}

							pixel = decoder.DecodeBit(contexts, cx);
						}

						current = (current << 1) | pixel;
						outByte |= pixel << (7 - k);
					}

					data[outIndex] = (byte)outByte;
				}
			}
		}

		/// <summary>
		/// Decodes a generic refinement region into <paramref name="bitmap"/> (6.3.5.6).
		/// </summary>
		/// <param name="decoder">The arithmetic decoder.</param>
		/// <param name="contexts">The <see cref="RefinementContextSize"/> GR contexts.</param>
		/// <param name="bitmap">The cleared bitmap that receives the region.</param>
		/// <param name="template">GRTEMPLATE, 0 or 1.</param>
		/// <param name="reference">The reference bitmap GRREFERENCE.</param>
		/// <param name="referenceDx">GRREFERENCEDX.</param>
		/// <param name="referenceDy">GRREFERENCEDY.</param>
		/// <param name="typicalPrediction">TPGRON.</param>
		/// <param name="at">The two AT pixels of template 0 as (x, y) pairs, or null.</param>
		public static void DecodeRefinement(
			Jbig2ArithmeticDecoder decoder,
			byte[] contexts,
			Jbig2Bitmap bitmap,
			int template,
			Jbig2Bitmap reference,
			int referenceDx,
			int referenceDy,
			bool typicalPrediction,
			int[] at)
		{
			var width = bitmap.Width;
			var height = bitmap.Height;
			var stride = bitmap.Stride;
			var data = bitmap.Data;
			if (width <= 0 || height <= 0)
				return;

			template &= 1;

			// the AT pixels of template 0: the first is in the coded bitmap, the second in the
			// reference bitmap; both are nominally at (-1, -1).
			int at1X = -1, at1Y = -1, at2X = -1, at2Y = -1;
			if (template == 0 && at != null && at.Length >= 4)
			{
				at1X = at[0];
				at1Y = at[1];
				at2X = at[2];
				at2Y = at[3];
			}

			var nominalAt1 = at1X == -1 && at1Y == -1;
			var nominalAt2 = at2X == -1 && at2Y == -1;

			// the pseudo-pixel context of TPGRON is the configuration where only the reference
			// pixel at the current position is 1 (6.3.5.6, Figures 14 and 15).
			var sltpContext = template == 0 ? 0x0080 : 0x0040;
			var ltp = 0;

			for (var y = 0; y < height; y++)
			{
				// far past the end of the data the decoder only produces filler: the rest stays white.
				if (decoder.IsExhausted)
					return;

				if (typicalPrediction)
					ltp ^= decoder.DecodeBit(contexts, sltpContext);

				var rowOffset = y * stride;
				var ry = y - referenceDy;

				// three-pixel windows: the row above in the coded bitmap at [x-1, x+1] and the
				// reference rows ry-1, ry and ry+1 at [rx-1, rx+1], where rx = x - referenceDx.
				var rx = -referenceDx;
				var coded = (GetPixel(data, stride, width, height, 0, y - 1) << 1)
					| GetPixel(data, stride, width, height, 1, y - 1);
				var ref0 = (reference.GetPixel(rx - 1, ry - 1) << 2) | (reference.GetPixel(rx, ry - 1) << 1) | reference.GetPixel(rx + 1, ry - 1);
				var ref1 = (reference.GetPixel(rx - 1, ry) << 2) | (reference.GetPixel(rx, ry) << 1) | reference.GetPixel(rx + 1, ry);
				var ref2 = (reference.GetPixel(rx - 1, ry + 1) << 2) | (reference.GetPixel(rx, ry + 1) << 1) | reference.GetPixel(rx + 1, ry + 1);
				var current = 0;

				for (var x = 0; x < width; x++, rx++)
				{
					int pixel;
					if (ltp != 0 && (ref0 & ref1 & ref2) == 7)
					{
						// typical prediction: the 3x3 reference neighbourhood is uniformly black.
						pixel = 1;
					}
					else if (ltp != 0 && (ref0 | ref1 | ref2) == 0)
					{
						// typical prediction: the 3x3 reference neighbourhood is uniformly white.
						pixel = 0;
					}
					else
					{
						int cx;
						if (template == 0)
						{
							var a1 = nominalAt1 ? (coded >> 2) & 1 : GetPixel(data, stride, width, height, x + at1X, y + at1Y);
							var a2 = nominalAt2 ? (ref0 >> 2) & 1 : reference.GetPixel(rx + at2X, ry + at2Y);
							cx = current
								| ((coded & 3) << 1)
								| (a1 << 3)
								| ((ref0 & 3) << 4)
								| (ref1 << 6)
								| (ref2 << 9)
								| (a2 << 12);
						}
						else
						{
							cx = current
								| (coded << 1)
								| (((ref0 >> 1) & 1) << 4)
								| (ref1 << 5)
								| ((ref2 & 3) << 8);
						}

						pixel = decoder.DecodeBit(contexts, cx);
					}

					if (pixel != 0)
						data[rowOffset + (x >> 3)] |= (byte)(0x80 >> (x & 7));

					// slide the windows one pixel to the right.
					current = pixel;
					coded = ((coded << 1) & 7) | GetPixel(data, stride, width, height, x + 2, y - 1);
					ref0 = ((ref0 << 1) & 7) | reference.GetPixel(rx + 2, ry - 1);
					ref1 = ((ref1 << 1) & 7) | reference.GetPixel(rx + 2, ry);
					ref2 = ((ref2 << 1) & 7) | reference.GetPixel(rx + 2, ry + 1);
				}
			}
		}

		#endregion

		#region Implementation

		// reads a pixel of a packed bitmap, returning 0 outside its bounds.
		private static int GetPixel(byte[] data, int stride, int width, int height, int x, int y)
		{
			if ((uint)x >= (uint)width || (uint)y >= (uint)height)
				return 0;

			return (data[y * stride + (x >> 3)] >> (7 - (x & 7))) & 1;
		}

		#endregion
	}
}
