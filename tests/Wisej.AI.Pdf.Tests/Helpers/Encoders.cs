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

// Test helper: reference encoders for the filters whose decoders are tested
// (LZW with both EarlyChange values, PNG and TIFF predictors, ASCII85).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Wisej.AI.Pdf.Tests.Helpers
{
	/// <summary>
	/// Straightforward encoders that produce test vectors for the library's decoders.
	/// </summary>
	internal static class Encoders
	{
		/// <summary>
		/// Encodes data with the LZW compression of the LZWDecode filter (ISO 32000-2, 7.4.4.2).
		/// </summary>
		/// <param name="data">The data.</param>
		/// <param name="earlyChange">1 to switch to longer codes one code early (the default of PDF), 0 otherwise.</param>
		/// <returns>The encoded data, starting with a clear-table code and ending with an end-of-data code.</returns>
		public static byte[] Lzw(byte[] data, int earlyChange = 1)
		{
			const int ClearTable = 256;
			const int EndOfData = 257;

			var writer = new BitWriter();
			var table = new Dictionary<(int Prefix, byte Suffix), int>();
			var next = 258;

			// the decoder adds its entries one code later than the encoder: the width rule accounts for it.
			int Width() => Math.Min(12, (next - 1 + earlyChange) >= 2048 ? 12 : (next - 1 + earlyChange) >= 1024 ? 11 : (next - 1 + earlyChange) >= 512 ? 10 : 9);

			writer.Write(ClearTable, 9);
			var current = -1;
			foreach (var b in data)
			{
				if (current < 0)
				{
					current = b;
					continue;
				}

				if (table.TryGetValue((current, b), out var code))
				{
					current = code;
					continue;
				}

				writer.Write(current, Width());
				table[(current, b)] = next++;
				current = b;

				// restart before the table overflows the 12-bit codes.
				if (next >= 4093)
				{
					writer.Write(ClearTable, Width());
					table.Clear();
					next = 258;
				}
			}

			if (current >= 0)
			{
				writer.Write(current, Width());
				next++;
			}

			writer.Write(EndOfData, Width());
			return writer.ToArray();
		}

		/// <summary>
		/// Applies a PNG filter to every row of raw image data (ISO/IEC 15948, 9).
		/// </summary>
		/// <param name="raw">The raw rows.</param>
		/// <param name="rowLength">The bytes per row.</param>
		/// <param name="bytesPerPixel">The bytes per complete pixel, at least 1.</param>
		/// <param name="filter">The filter type: 0 None, 1 Sub, 2 Up, 3 Average, 4 Paeth.</param>
		/// <returns>The filtered rows, each prefixed with the filter type.</returns>
		public static byte[] PngFilter(byte[] raw, int rowLength, int bytesPerPixel, int filter)
		{
			var rows = raw.Length / rowLength;
			var output = new byte[rows * (rowLength + 1)];
			for (int y = 0; y < rows; y++)
			{
				output[y * (rowLength + 1)] = (byte)filter;
				for (int i = 0; i < rowLength; i++)
				{
					var value = raw[y * rowLength + i];
					var left = i >= bytesPerPixel ? raw[y * rowLength + i - bytesPerPixel] : 0;
					var up = y > 0 ? raw[(y - 1) * rowLength + i] : 0;
					var upLeft = y > 0 && i >= bytesPerPixel ? raw[(y - 1) * rowLength + i - bytesPerPixel] : 0;

					int predicted;
					switch (filter)
					{
						case 1: predicted = left; break;
						case 2: predicted = up; break;
						case 3: predicted = (left + up) / 2; break;
						case 4: predicted = Paeth(left, up, upLeft); break;
						default: predicted = 0; break;
					}

					output[y * (rowLength + 1) + 1 + i] = (byte)(value - predicted);
				}
			}

			return output;
		}

		/// <summary>
		/// Applies TIFF predictor 2 (horizontal differencing) to 8 or 16-bit samples.
		/// </summary>
		/// <param name="raw">The raw rows.</param>
		/// <param name="columns">The pixels per row.</param>
		/// <param name="colors">The components per pixel.</param>
		/// <param name="bitsPerComponent">8 or 16.</param>
		/// <returns>The differenced rows.</returns>
		public static byte[] TiffPredictor(byte[] raw, int columns, int colors, int bitsPerComponent)
		{
			var bytesPerSample = bitsPerComponent / 8;
			var rowLength = columns * colors * bytesPerSample;
			var output = (byte[])raw.Clone();
			for (int row = 0; row * rowLength < raw.Length; row++)
			{
				// walk backwards so each difference uses the original previous sample.
				for (int x = columns - 1; x >= 1; x--)
				{
					for (int c = 0; c < colors; c++)
					{
						var index = row * rowLength + (x * colors + c) * bytesPerSample;
						var previous = index - colors * bytesPerSample;
						if (bytesPerSample == 1)
						{
							output[index] = (byte)(raw[index] - raw[previous]);
						}
						else
						{
							var value = ((raw[index] << 8) | raw[index + 1]) - ((raw[previous] << 8) | raw[previous + 1]);
							output[index] = (byte)(value >> 8);
							output[index + 1] = (byte)value;
						}
					}
				}
			}

			return output;
		}

		/// <summary>
		/// Encodes data with ASCII base-85 (ISO 32000-2, 7.4.3), without the 'z' shorthand.
		/// </summary>
		/// <param name="data">The data.</param>
		/// <returns>The encoded text, terminated by "~&gt;".</returns>
		public static string Ascii85(byte[] data)
		{
			var sb = new StringBuilder();
			for (int i = 0; i < data.Length; i += 4)
			{
				var count = Math.Min(4, data.Length - i);
				uint tuple = 0;
				for (int j = 0; j < 4; j++)
					tuple = (tuple << 8) | (j < count ? data[i + j] : (byte)0);

				var chars = new char[5];
				for (int j = 4; j >= 0; j--)
				{
					chars[j] = (char)('!' + tuple % 85);
					tuple /= 85;
				}

				// a final group of n bytes is written with n + 1 characters.
				sb.Append(chars, 0, count + 1);
			}

			return sb.Append("~>").ToString();
		}

		// the PNG Paeth predictor: returns the left, above or upper-left value closest to a + b - c.
		private static int Paeth(int a, int b, int c)
		{
			var p = a + b - c;
			var pa = Math.Abs(p - a);
			var pb = Math.Abs(p - b);
			var pc = Math.Abs(p - c);
			return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
		}

		/// <summary>
		/// Writes codes most significant bit first.
		/// </summary>
		private sealed class BitWriter
		{
			// the completed bytes.
			private readonly MemoryStream _output = new MemoryStream();

			// the bits of the byte being filled.
			private int _buffer;

			// the number of bits in the buffer.
			private int _count;

			/// <summary>
			/// Writes a code.
			/// </summary>
			/// <param name="value">The code.</param>
			/// <param name="bits">The width of the code.</param>
			public void Write(int value, int bits)
			{
				for (int i = bits - 1; i >= 0; i--)
				{
					_buffer = (_buffer << 1) | ((value >> i) & 1);
					if (++_count == 8)
					{
						_output.WriteByte((byte)_buffer);
						_buffer = 0;
						_count = 0;
					}
				}
			}

			/// <summary>
			/// Flushes the pending bits, padded with zeros, and returns the data.
			/// </summary>
			/// <returns>The encoded bytes.</returns>
			public byte[] ToArray()
			{
				if (_count > 0)
					_output.WriteByte((byte)(_buffer << (8 - _count)));

				_count = 0;
				return _output.ToArray();
			}
		}
	}
}
