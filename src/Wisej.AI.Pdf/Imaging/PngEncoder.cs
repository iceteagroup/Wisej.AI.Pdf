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

// PNG encoder (ISO/IEC 15948) with automatic colour type selection and
// adaptive row filtering.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Wisej.AI.Pdf.Filters;

namespace Wisej.AI.Pdf.Imaging
{
	/// <summary>
	/// Encodes <see cref="RasterImage"/> instances as PNG files.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The smallest colour type that represents the image losslessly is chosen: palette (up to 256
	/// colours, common in charts and diagrams), grayscale, grayscale with alpha, RGB or RGBA.
	/// </para>
	/// <para>
	/// Truecolour and grayscale rows use the filter that minimizes the sum of absolute differences,
	/// the heuristic recommended by the PNG specification; palette rows are not filtered, as recommended.
	/// Very large images are compressed with the fastest deflate level to bound the encoding time.
	/// </para>
	/// </remarks>
	internal static class PngEncoder
	{
		#region Fields

		// raw images larger than this are compressed with the fastest deflate level.
		private const int FastCompressionThreshold = 32 * 1024 * 1024;

		/// <summary>The CRC-32 lookup table (polynomial 0xEDB88320) used for chunk checksums.</summary>
		private static readonly uint[] _crcTable = BuildCrcTable();

		#endregion

		#region Methods

		/// <summary>
		/// Encodes the image.
		/// </summary>
		/// <param name="image">The image.</param>
		/// <returns>The PNG file bytes.</returns>
		public static byte[] Encode(RasterImage image)
		{
			var width = image.Width;
			var height = image.Height;
			var pixels = image.Pixels;

			Analyze(pixels, out var hasAlpha, out var isGray, out var colors);

			// choose the colour type.
			byte colorType;
			int bitDepth = 8;
			int bytesPerPixel;
			if (isGray)
			{
				colorType = hasAlpha ? (byte)4 : (byte)0;
				bytesPerPixel = hasAlpha ? 2 : 1;
			}
			else if (colors != null)
			{
				colorType = 3;
				bytesPerPixel = 1;
				bitDepth = colors.Count <= 2 ? 1 : colors.Count <= 4 ? 2 : colors.Count <= 16 ? 4 : 8;
			}
			else
			{
				colorType = hasAlpha ? (byte)6 : (byte)2;
				bytesPerPixel = hasAlpha ? 4 : 3;
			}

			var rowLength = colorType == 3 ? (width * bitDepth + 7) / 8 : width * bytesPerPixel;
			var filtered = new byte[height * (rowLength + 1)];

			if (colorType == 3)
				WritePaletteRows(pixels, width, height, rowLength, bitDepth, colors, filtered);
			else
				WriteFilteredRows(pixels, width, height, rowLength, bytesPerPixel, colorType, filtered);

			var compressed = Compress(filtered);

			using (var output = new MemoryStream(compressed.Length + 1024))
			{
				// signature.
				output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

				var header = new byte[13];
				WriteInt(header, 0, width);
				WriteInt(header, 4, height);
				header[8] = (byte)bitDepth;
				header[9] = colorType;
				WriteChunk(output, "IHDR", header);

				if (colorType == 3)
				{
					var palette = new byte[colors.Count * 3];
					var transparency = new byte[colors.Count];
					var hasTransparentEntries = false;
					for (int i = 0; i < colors.Count; i++)
					{
						var color = colors[i];
						palette[i * 3] = (byte)(color >> 24);
						palette[i * 3 + 1] = (byte)(color >> 16);
						palette[i * 3 + 2] = (byte)(color >> 8);
						transparency[i] = (byte)color;
						if ((color & 0xFF) != 255)
							hasTransparentEntries = true;
					}

					WriteChunk(output, "PLTE", palette);
					if (hasTransparentEntries)
						WriteChunk(output, "tRNS", transparency);
				}

				WriteChunk(output, "IDAT", compressed);
				WriteChunk(output, "IEND", new byte[0]);

				return output.ToArray();
			}
		}

		#endregion

		#region Analysis

		// one pass over the pixels: transparency, grayscale and the palette (null when more than 256 colours).
		private static void Analyze(byte[] pixels, out bool hasAlpha, out bool isGray, out List<int> colors)
		{
			hasAlpha = false;
			isGray = true;
			colors = new List<int>();
			var seen = new HashSet<int>();
			// the start value is outside the range of RGBA keys, which use all 32 bits (opaque white is -1).
			long previous = -1L << 40;
			var tracking = true;

			for (int i = 0; i < pixels.Length; i += 4)
			{
				var r = pixels[i];
				var g = pixels[i + 1];
				var b = pixels[i + 2];
				var a = pixels[i + 3];

				if (a != 255)
					hasAlpha = true;
				if (r != g || g != b)
					isGray = false;

				if (!tracking)
				{
					// nothing left to learn once the image is known to be colour, transparent and not palettable.
					if (hasAlpha && !isGray)
						break;

					continue;
				}

				// runs of the same colour are frequent: skip the hash lookup for them.
				var key = NormalizeTransparent((r << 24) | (g << 16) | (b << 8) | a);
				if (key == previous)
					continue;

				previous = key;
				if (seen.Add(key))
				{
					if (seen.Count > 256)
					{
						tracking = false;
						colors = null;
						continue;
					}

					colors.Add(key);
				}
			}

			if (colors != null)
			{
				// transparent entries first, so the tRNS chunk could be shorter.
				colors.Sort((x, y) => (x & 0xFF).CompareTo(y & 0xFF));
			}
		}

		// all fully transparent pixels share the same palette entry.
		private static int NormalizeTransparent(int rgba) => (rgba & 0xFF) == 0 ? 0 : rgba;

		#endregion

		#region Rows

		// palette rows: indexes packed at the bit depth, filter type 0.
		private static void WritePaletteRows(byte[] pixels, int width, int height, int rowLength, int bitDepth, List<int> colors, byte[] output)
		{
			var index = new Dictionary<int, int>(colors.Count);
			for (int i = 0; i < colors.Count; i++)
				index[colors[i]] = i;

			// the start value is outside the range of RGBA keys, which use all 32 bits (opaque white is -1).
			long previousKey = -1L << 40;
			var previousIndex = 0;
			for (int y = 0; y < height; y++)
			{
				var row = y * (rowLength + 1) + 1;
				for (int x = 0; x < width; x++)
				{
					var p = (y * width + x) * 4;
					var key = NormalizeTransparent((pixels[p] << 24) | (pixels[p + 1] << 16) | (pixels[p + 2] << 8) | pixels[p + 3]);
					if (key != previousKey)
					{
						previousKey = key;
						previousIndex = index[key];
					}

					if (bitDepth == 8)
					{
						output[row + x] = (byte)previousIndex;
					}
					else
					{
						var bit = x * bitDepth;
						output[row + bit / 8] |= (byte)(previousIndex << (8 - bitDepth - bit % 8));
					}
				}
			}
		}

		// truecolour and grayscale rows with the adaptive filter selection.
		private static void WriteFilteredRows(byte[] pixels, int width, int height, int rowLength, int bpp, byte colorType, byte[] output)
		{
			var current = new byte[rowLength];
			var previous = new byte[rowLength];

			for (int y = 0; y < height; y++)
			{
				// extract the row in the chosen format.
				var p = y * width * 4;
				switch (colorType)
				{
					case 0:
						for (int x = 0; x < width; x++, p += 4)
							current[x] = pixels[p];
						break;

					case 4:
						for (int x = 0; x < width; x++, p += 4)
						{
							current[x * 2] = pixels[p];
							current[x * 2 + 1] = pixels[p + 3];
						}
						break;

					case 2:
						for (int x = 0, o = 0; x < width; x++, p += 4, o += 3)
						{
							current[o] = pixels[p];
							current[o + 1] = pixels[p + 1];
							current[o + 2] = pixels[p + 2];
						}
						break;

					default:
						Buffer.BlockCopy(pixels, p, current, 0, rowLength);
						break;
				}

				var target = y * (rowLength + 1);
				var filter = ChooseFilter(current, previous, rowLength, bpp, y == 0);
				output[target] = (byte)filter;
				ApplyFilter(filter, current, previous, rowLength, bpp, output, target + 1);

				var swap = previous;
				previous = current;
				current = swap;
			}
		}

		// the filter with the smallest sum of absolute (signed) residuals.
		private static int ChooseFilter(byte[] row, byte[] up, int length, int bpp, bool firstRow)
		{
			long none = 0, sub = 0, upSum = 0, average = 0, paeth = 0;
			for (int i = 0; i < length; i++)
			{
				int x = row[i];
				int a = i >= bpp ? row[i - bpp] : 0;
				int b = up[i];
				int c = i >= bpp ? up[i - bpp] : 0;

				none += Cost(x);
				sub += Cost(x - a);
				upSum += Cost(x - b);
				average += Cost(x - ((a + b) >> 1));
				paeth += Cost(x - Predictor.Paeth(a, b, c));
			}

			// the first row has no previous row: Up and Paeth degenerate.
			if (firstRow)
				return sub < none ? 1 : 0;

			var best = 0;
			var bestSum = none;
			if (sub < bestSum)
			{
				best = 1;
				bestSum = sub;
			}

			if (upSum < bestSum)
			{
				best = 2;
				bestSum = upSum;
			}

			if (average < bestSum)
			{
				best = 3;
				bestSum = average;
			}

			if (paeth < bestSum)
				best = 4;

			return best;
		}

		// the cost of a residual: its absolute value as a signed byte.
		private static int Cost(int value)
		{
			var b = value & 0xFF;
			return b < 128 ? b : 256 - b;
		}

		/// <summary>
		/// Applies a PNG filter (PNG spec, 9.2) to one row of raw bytes.
		/// </summary>
		/// <param name="filter">The filter type: 0 None, 1 Sub, 2 Up, 3 Average, 4 Paeth.</param>
		/// <param name="row">The raw bytes of the current row.</param>
		/// <param name="up">The raw bytes of the previous row, all zero for the first row.</param>
		/// <param name="length">The number of bytes in the row, excluding the filter type byte.</param>
		/// <param name="bpp">The number of bytes per complete pixel, at least 1.</param>
		/// <param name="output">The buffer receiving the filtered bytes.</param>
		/// <param name="offset">The position in <paramref name="output"/> of the first filtered byte.</param>
		private static void ApplyFilter(int filter, byte[] row, byte[] up, int length, int bpp, byte[] output, int offset)
		{
			switch (filter)
			{
				case 0:
					Buffer.BlockCopy(row, 0, output, offset, length);
					break;

				case 1:
					for (int i = 0; i < length; i++)
						output[offset + i] = (byte)(row[i] - (i >= bpp ? row[i - bpp] : 0));
					break;

				case 2:
					for (int i = 0; i < length; i++)
						output[offset + i] = (byte)(row[i] - up[i]);
					break;

				case 3:
					for (int i = 0; i < length; i++)
						output[offset + i] = (byte)(row[i] - (((i >= bpp ? row[i - bpp] : 0) + up[i]) >> 1));
					break;

				default:
					for (int i = 0; i < length; i++)
					{
						var a = i >= bpp ? row[i - bpp] : 0;
						var c = i >= bpp ? up[i - bpp] : 0;
						output[offset + i] = (byte)(row[i] - Predictor.Paeth(a, up[i], c));
					}
					break;
			}
		}

		#endregion

		#region Chunks

		/// <summary>
		/// Compresses the filtered image data into a zlib stream for the IDAT chunk.
		/// </summary>
		/// <param name="data">The filtered scanlines.</param>
		/// <returns>The zlib-wrapped deflate data.</returns>
		private static byte[] Compress(byte[] data)
		{
			return FlateFilter.EncodeZlib(data, fastest: data.Length > FastCompressionThreshold);
		}

		/// <summary>
		/// Writes a chunk: big-endian length, type, data and the CRC-32 of type and data.
		/// </summary>
		/// <param name="output">The stream receiving the chunk.</param>
		/// <param name="type">The four-character ASCII chunk type.</param>
		/// <param name="data">The chunk data.</param>
		private static void WriteChunk(Stream output, string type, byte[] data)
		{
			var header = new byte[8];
			WriteInt(header, 0, data.Length);
			for (int i = 0; i < 4; i++)
				header[4 + i] = (byte)type[i];

			output.Write(header, 0, 8);
			output.Write(data, 0, data.Length);

			var crc = Crc32(header, 4, 4, 0xFFFFFFFF);
			crc = Crc32(data, 0, data.Length, crc) ^ 0xFFFFFFFF;

			var trailer = new byte[4];
			WriteInt(trailer, 0, (int)crc);
			output.Write(trailer, 0, 4);
		}

		/// <summary>
		/// Stores a 32-bit integer in big-endian (network) byte order.
		/// </summary>
		/// <param name="buffer">The target buffer.</param>
		/// <param name="offset">The position of the most significant byte.</param>
		/// <param name="value">The value to store.</param>
		private static void WriteInt(byte[] buffer, int offset, int value)
		{
			buffer[offset] = (byte)(value >> 24);
			buffer[offset + 1] = (byte)(value >> 16);
			buffer[offset + 2] = (byte)(value >> 8);
			buffer[offset + 3] = (byte)value;
		}

		/// <summary>
		/// Updates a running CRC-32 with a range of bytes. The caller applies the initial and final inversion.
		/// </summary>
		/// <param name="data">The bytes.</param>
		/// <param name="offset">The first byte to include.</param>
		/// <param name="length">The number of bytes to include.</param>
		/// <param name="crc">The running CRC.</param>
		/// <returns>The updated running CRC.</returns>
		private static uint Crc32(byte[] data, int offset, int length, uint crc)
		{
			for (int i = offset; i < offset + length; i++)
				crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

			return crc;
		}

		/// <summary>
		/// Builds the byte-wise CRC-32 lookup table.
		/// </summary>
		/// <returns>The 256-entry table.</returns>
		private static uint[] BuildCrcTable()
		{
			var table = new uint[256];
			for (uint n = 0; n < 256; n++)
			{
				var c = n;
				for (int k = 0; k < 8; k++)
					c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;

				table[n] = c;
			}

			return table;
		}

		#endregion
	}
}
