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

// Pattern dictionary and halftone region decoding procedures (ITU T.88, 6.6, 6.7 and Annex C).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// The parameters of the halftone region decoding procedure (Table 19).
	/// </summary>
	internal sealed class Jbig2HalftoneParameters
	{
		#region Fields

		/// <summary>HMMR: true when the gray-scale planes are MMR coded.</summary>
		public bool Mmr;

		/// <summary>HTEMPLATE: the generic region template of the gray-scale planes.</summary>
		public int Template;

		/// <summary>HENABLESKIP: true when grid cells outside the region are skipped.</summary>
		public bool EnableSkip;

		/// <summary>HCOMBOP: the operator used to draw the patterns.</summary>
		public int CombinationOperator;

		/// <summary>HGW: the width of the gray-scale image.</summary>
		public long GridWidth;

		/// <summary>HGH: the height of the gray-scale image.</summary>
		public long GridHeight;

		/// <summary>HGX: the horizontal offset of the grid, in 1/256 pixel.</summary>
		public int GridX;

		/// <summary>HGY: the vertical offset of the grid, in 1/256 pixel.</summary>
		public int GridY;

		/// <summary>HRX: the horizontal grid vector, in 1/256 pixel.</summary>
		public int VectorX;

		/// <summary>HRY: the vertical grid vector, in 1/256 pixel.</summary>
		public int VectorY;

		#endregion
	}

	/// <summary>
	/// Implements the pattern dictionary (6.7.5) and halftone region (6.6.5) decoding procedures.
	/// </summary>
	internal static class Jbig2Halftone
	{
		#region Methods

		/// <summary>
		/// Decodes the patterns of a pattern dictionary.
		/// </summary>
		/// <param name="mmr">HDMMR.</param>
		/// <param name="template">HDTEMPLATE.</param>
		/// <param name="patternWidth">HDPW.</param>
		/// <param name="patternHeight">HDPH.</param>
		/// <param name="grayMax">GRAYMAX: the index of the last pattern.</param>
		/// <param name="data">The segment data buffer.</param>
		/// <param name="start">The offset of the coded collective bitmap.</param>
		/// <param name="end">The end of the segment data.</param>
		/// <param name="allocator">The bitmap allocator.</param>
		/// <returns>The GRAYMAX + 1 patterns.</returns>
		public static List<Jbig2Bitmap> DecodePatterns(
			bool mmr,
			int template,
			int patternWidth,
			int patternHeight,
			long grayMax,
			byte[] data,
			int start,
			int end,
			Jbig2Allocator allocator)
		{
			var patterns = new List<Jbig2Bitmap>();
			if (patternWidth <= 0 || patternHeight <= 0 || grayMax < 0 || grayMax > 1 << 20)
				return patterns;

			// 6.7.5: all the patterns are coded side by side in one collective bitmap.
			var collective = allocator.Create((grayMax + 1) * patternWidth, patternHeight);
			if (mmr)
			{
				Jbig2MmrDecoder.Decode(new Jbig2BitReader(data, start, end), collective);
			}
			else
			{
				// the first AT pixel points at the same pixel of the previous pattern.
				var at = new int[] { -patternWidth, 0, -3, -1, 2, -2, -2, -2 };
				var decoder = new Jbig2ArithmeticDecoder(data, start, end);
				var contexts = new byte[Jbig2GenericRegion.GenericContextSize];
				Jbig2GenericRegion.Decode(decoder, contexts, collective, template, false, at, null);
			}

			for (var i = 0; i <= grayMax; i++)
				patterns.Add(collective.Extract((int)(i * patternWidth), 0, patternWidth, patternHeight));

			return patterns;
		}

		/// <summary>
		/// Decodes a halftone region into <paramref name="region"/>.
		/// </summary>
		/// <param name="parameters">The decoding parameters.</param>
		/// <param name="patterns">The patterns of the referred-to pattern dictionary.</param>
		/// <param name="region">The region bitmap, already filled with HDEFPIXEL.</param>
		/// <param name="data">The segment data buffer.</param>
		/// <param name="start">The offset of the coded gray-scale image.</param>
		/// <param name="end">The end of the segment data.</param>
		/// <param name="allocator">The bitmap allocator.</param>
		public static void DecodeRegion(
			Jbig2HalftoneParameters parameters,
			List<Jbig2Bitmap> patterns,
			Jbig2Bitmap region,
			byte[] data,
			int start,
			int end,
			Jbig2Allocator allocator)
		{
			var p = parameters;
			if (patterns == null || patterns.Count == 0)
				return;

			var gridWidth = p.GridWidth;
			var gridHeight = p.GridHeight;
			if (gridWidth <= 0 || gridHeight <= 0)
				return;

			var patternWidth = patterns[0].Width;
			var patternHeight = patterns[0].Height;

			// 6.6.5.1: the skip bitmap marks the grid cells whose pattern lies outside the region.
			Jbig2Bitmap skip = null;
			if (p.EnableSkip)
			{
				skip = allocator.Create(gridWidth, gridHeight);
				for (var m = 0; m < gridHeight; m++)
				{
					for (var n = 0; n < gridWidth; n++)
					{
						var x = (p.GridX + m * (long)p.VectorY + n * (long)p.VectorX) >> 8;
						var y = (p.GridY + m * (long)p.VectorX - n * (long)p.VectorY) >> 8;
						if (x + patternWidth <= 0 || x >= region.Width || y + patternHeight <= 0 || y >= region.Height)
							skip.SetPixel(n, m);
					}
				}
			}

			// HBPP: the number of bits per gray-scale value.
			var bitsPerValue = 0;
			while ((1L << bitsPerValue) < patterns.Count)
				bitsPerValue++;

			var values = DecodeGrayScale(p, (int)gridWidth, (int)gridHeight, bitsPerValue, skip, data, start, end, allocator);

			// 6.6.5.2: draw the pattern of every grid cell.
			var maxIndex = patterns.Count - 1;
			for (var m = 0; m < gridHeight; m++)
			{
				for (var n = 0; n < gridWidth; n++)
				{
					var x = (p.GridX + m * (long)p.VectorY + n * (long)p.VectorX) >> 8;
					var y = (p.GridY + m * (long)p.VectorX - n * (long)p.VectorY) >> 8;
					var index = Math.Min(values[m * gridWidth + n], maxIndex);
					allocator.Spend(1 + (long)patternWidth * patternHeight / 8);
					region.Compose(patterns[index], x, y, p.CombinationOperator);
				}
			}
		}

		#endregion

		#region Implementation

		// Annex C: decodes the bit planes of the gray-scale image and combines them into values.
		private static int[] DecodeGrayScale(
			Jbig2HalftoneParameters p,
			int width,
			int height,
			int bitsPerValue,
			Jbig2Bitmap skip,
			byte[] data,
			int start,
			int end,
			Jbig2Allocator allocator)
		{
			allocator.Reserve((long)width * 32, height);
			var values = new int[(long)width * height];
			if (bitsPerValue == 0)
				return values;

			var planes = new Jbig2Bitmap[bitsPerValue];
			var reader = p.Mmr ? new Jbig2BitReader(data, start, end) : null;
			var decoder = p.Mmr ? null : new Jbig2ArithmeticDecoder(data, start, end);
			var contexts = p.Mmr ? null : new byte[Jbig2GenericRegion.GenericContextSize];

			// C.5: the first AT pixel depends on the template, the others are the defaults.
			var at = new int[] { p.Template <= 1 ? 3 : 2, -1, -3, -1, 2, -2, -2, -2 };

			// the planes are coded from the most significant one down.
			for (var j = bitsPerValue - 1; j >= 0; j--)
			{
				var plane = allocator.Create(width, height);
				if (p.Mmr)
				{
					// each MMR plane ends at a byte boundary, after an optional EOFB.
					Jbig2MmrDecoder.Decode(reader, plane);
					reader.AlignToByte();
				}
				else
				{
					Jbig2GenericRegion.Decode(decoder, contexts, plane, p.Template, false, at, skip);
				}

				planes[j] = plane;
			}

			// C.5 step 3: undo the Gray coding, from the most significant plane down.
			for (var j = bitsPerValue - 2; j >= 0; j--)
			{
				var lower = planes[j].Data;
				var upper = planes[j + 1].Data;
				for (var i = 0; i < lower.Length; i++)
					lower[i] ^= upper[i];
			}

			// combine the planes into the gray-scale values.
			for (var j = 0; j < bitsPerValue; j++)
			{
				var plane = planes[j];
				var planeData = plane.Data;
				var stride = plane.Stride;
				for (var y = 0; y < height; y++)
				{
					var rowOffset = y * stride;
					var valueOffset = y * width;
					for (var x = 0; x < width; x++)
					{
						if ((planeData[rowOffset + (x >> 3)] & (0x80 >> (x & 7))) != 0)
							values[valueOffset + x] |= 1 << j;
					}
				}
			}

			return values;
		}

		#endregion
	}
}
