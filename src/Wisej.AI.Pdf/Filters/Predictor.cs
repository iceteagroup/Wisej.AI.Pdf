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

// PNG and TIFF predictors for the FlateDecode and LZWDecode filters (ISO 32000-2, 7.4.4.4).

using System;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Filters
{
	/// <summary>
	/// Reverses the prediction applied to the data before compression.
	/// </summary>
	internal static class Predictor
	{
		/// <summary>
		/// Applies the predictor described by <paramref name="parameters"/> to <paramref name="data"/>.
		/// </summary>
		/// <param name="data">The decompressed bytes.</param>
		/// <param name="parameters">The decode parameters with <c>/Predictor</c>, <c>/Colors</c>, <c>/BitsPerComponent</c> and <c>/Columns</c>.</param>
		/// <returns>The original bytes.</returns>
		public static byte[] Apply(byte[] data, PdfDictionary parameters)
		{
			if (parameters == null)
				return data;

			var predictor = parameters.GetInt("Predictor", 1);
			if (predictor <= 1)
				return data;

			var colors = Math.Max(1, parameters.GetInt("Colors", 1));
			var bitsPerComponent = Math.Max(1, parameters.GetInt(PdfNames.BitsPerComponent, 8));
			var columns = Math.Max(1, parameters.GetInt("Columns", 1));

			var bitsPerPixel = colors * bitsPerComponent;
			var bytesPerPixel = Math.Max(1, (bitsPerPixel + 7) / 8);
			// absurd parameters (corrupt files) would overflow or allocate huge rows: leave the data as is.
			if ((long)columns * bitsPerPixel > int.MaxValue / 2)
				return data;

			var rowLength = (columns * bitsPerPixel + 7) / 8;

			if (predictor == 2)
				return ApplyTiff(data, rowLength, colors, bitsPerComponent);

			return ApplyPng(data, rowLength, bytesPerPixel);
		}

		/// <summary>
		/// Reverses the PNG predictors (predictor values 10 to 15).
		/// </summary>
		/// <param name="data">The predicted rows, each one preceded by its filter-type byte.</param>
		/// <param name="rowLength">The length of a row in bytes, excluding the filter-type byte.</param>
		/// <param name="bytesPerPixel">The distance in bytes to the corresponding byte of the previous pixel (at least 1).</param>
		/// <returns>The original rows; a truncated final row is kept as far as available.</returns>
		/// <remarks>
		/// Every row starts with its own filter-type byte, so the value of <c>/Predictor</c> is only a hint.
		/// </remarks>
		private static byte[] ApplyPng(byte[] data, int rowLength, int bytesPerPixel)
		{
			var stride = rowLength + 1;
			var rows = (data.Length + stride - 1) / stride;
			var output = new byte[rows * rowLength];
			var previous = new byte[rowLength];

			var outputLength = 0;
			for (int row = 0; row < rows; row++)
			{
				var source = row * stride;
				if (source >= data.Length)
					break;

				var filter = data[source++];
				var available = Math.Min(rowLength, data.Length - source);
				var target = row * rowLength;

				for (int i = 0; i < available; i++)
				{
					var raw = data[source + i];
					var left = i >= bytesPerPixel ? output[target + i - bytesPerPixel] : 0;
					var up = previous[i];
					var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;

					int value;
					switch (filter)
					{
						case 1:
							// sub.
							value = raw + left;
							break;

						case 2:
							// up.
							value = raw + up;
							break;

						case 3:
							// average.
							value = raw + ((left + up) >> 1);
							break;

						case 4:
							value = raw + Paeth(left, up, upLeft);
							break;

						default:
							// none, or an invalid filter type.
							value = raw;
							break;
					}

					output[target + i] = (byte)value;
				}

				Buffer.BlockCopy(output, target, previous, 0, rowLength);
				outputLength = target + available;
			}

			if (outputLength < output.Length)
				Array.Resize(ref output, outputLength);

			return output;
		}

		/// <summary>
		/// The Paeth predictor function from the PNG specification.
		/// </summary>
		/// <param name="a">The byte to the left.</param>
		/// <param name="b">The byte above.</param>
		/// <param name="c">The byte above and to the left.</param>
		/// <returns>The predicted value.</returns>
		public static int Paeth(int a, int b, int c)
		{
			var p = a + b - c;
			var pa = Math.Abs(p - a);
			var pb = Math.Abs(p - b);
			var pc = Math.Abs(p - c);

			if (pa <= pb && pa <= pc)
				return a;

			return pb <= pc ? b : c;
		}

		/// <summary>
		/// Reverses TIFF predictor 2, where each component is the difference from the same component of the previous pixel.
		/// </summary>
		/// <param name="data">The predicted rows.</param>
		/// <param name="rowLength">The length of a row in bytes.</param>
		/// <param name="colors">The number of components per pixel.</param>
		/// <param name="bitsPerComponent">The number of bits per component: 1, 2, 4, 8 or 16; other values leave the data unchanged.</param>
		/// <returns>A copy of the data with the prediction reversed; a trailing partial row is copied unchanged.</returns>
		private static byte[] ApplyTiff(byte[] data, int rowLength, int colors, int bitsPerComponent)
		{
			var output = (byte[])data.Clone();
			var rows = data.Length / rowLength;

			for (int row = 0; row < rows; row++)
			{
				var start = row * rowLength;
				switch (bitsPerComponent)
				{
					case 8:
						for (int i = colors; i < rowLength; i++)
							output[start + i] = (byte)(output[start + i] + output[start + i - colors]);
						break;

					case 16:
						for (int i = colors * 2; i + 1 < rowLength; i += 2)
						{
							var value = ((output[start + i] << 8) | output[start + i + 1])
								+ ((output[start + i - colors * 2] << 8) | output[start + i - colors * 2 + 1]);
							output[start + i] = (byte)(value >> 8);
							output[start + i + 1] = (byte)value;
						}
						break;

					default:
						ApplyTiffBits(output, start, rowLength, colors, bitsPerComponent);
						break;
				}
			}

			return output;
		}

		/// <summary>
		/// Reverses TIFF predictor 2 in place on a row of 1, 2 or 4-bit components.
		/// </summary>
		/// <param name="data">The data that contains the row.</param>
		/// <param name="start">The offset of the row in <paramref name="data"/>.</param>
		/// <param name="rowLength">The length of the row in bytes.</param>
		/// <param name="colors">The number of components per pixel.</param>
		/// <param name="bits">The number of bits per component; other values than 1, 2 and 4 are ignored.</param>
		private static void ApplyTiffBits(byte[] data, int start, int rowLength, int colors, int bits)
		{
			if (bits != 1 && bits != 2 && bits != 4)
				return;

			var mask = (1 << bits) - 1;
			var samples = rowLength * 8 / bits;
			var previous = new int[colors];

			for (int s = 0; s < samples; s++)
			{
				var bitOffset = s * bits;
				var index = start + bitOffset / 8;
				var shift = 8 - bits - bitOffset % 8;

				var component = s % colors;
				var value = (((data[index] >> shift) & mask) + previous[component]) & mask;
				previous[component] = value;

				data[index] = (byte)((data[index] & ~(mask << shift)) | (value << shift));
			}
		}
	}
}
