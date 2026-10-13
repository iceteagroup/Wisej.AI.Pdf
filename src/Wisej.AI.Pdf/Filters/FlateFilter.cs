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

// FlateDecode filter (zlib/deflate, RFC 1950/1951).

using System;
using System.IO;
using System.IO.Compression;

namespace Wisej.AI.Pdf.Filters
{
	/// <summary>
	/// Decodes and encodes data compressed with the zlib/deflate method.
	/// </summary>
	/// <remarks>
	/// Damaged streams are common: decoding stops at the first error and returns all
	/// the data inflated up to that point instead of failing.
	/// </remarks>
	internal static class FlateFilter
	{
		/// <summary>
		/// Inflates <paramref name="data"/>.
		/// </summary>
		/// <param name="data">The compressed bytes, with or without the zlib header.</param>
		/// <returns>The decompressed bytes.</returns>
		public static byte[] Decode(byte[] data)
		{
			if (data.Length == 0)
				return data;

			// skip the 2-byte zlib header when present (CM = 8 and a valid FCHECK).
			var offset = 0;
			if (data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0)
			{
				offset = 2;

				// a preset dictionary (FDICT) is followed by its 4-byte id; PDF doesn't define dictionaries, skip it.
				if ((data[1] & 0x20) != 0)
					offset += 4;
			}

			var result = Inflate(data, offset, out var complete);

			// not a valid zlib header after all: retry as raw deflate data from the start.
			if (!complete && result.Length == 0 && offset > 0)
				result = Inflate(data, 0, out _);

			return result;
		}

		/// <summary>
		/// Inflates raw deflate data (RFC 1951), stopping at the first error or after
		/// <see cref="FilterPipeline.MaxDecodedLength"/> bytes.
		/// </summary>
		/// <param name="data">The compressed bytes.</param>
		/// <param name="offset">The offset of the deflate data in <paramref name="data"/>.</param>
		/// <param name="complete">Receives true when the data was inflated without errors.</param>
		/// <returns>The bytes inflated so far.</returns>
		private static byte[] Inflate(byte[] data, int offset, out bool complete)
		{
			complete = false;

			using (var output = new MemoryStream(Math.Max(256, Math.Min(data.Length * 4, 64 * 1024 * 1024))))
			{
				try
				{
					using (var input = new MemoryStream(data, offset, data.Length - offset, false))
					using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
					{
						var buffer = new byte[16384];
						int read;
						while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
						{
							output.Write(buffer, 0, read);
							if (output.Length > FilterPipeline.MaxDecodedLength)
								break;
						}
					}

					complete = true;
				}
				catch (InvalidDataException)
				{
					// keep the data decoded so far.
				}
				catch (IOException)
				{
					// keep the data decoded so far.
				}

				return output.ToArray();
			}
		}

		/// <summary>
		/// Compresses <paramref name="data"/> as a zlib stream (header, deflate data and Adler-32 checksum).
		/// </summary>
		/// <param name="data">The bytes to compress.</param>
		/// <param name="fastest">True to favor speed over size.</param>
		/// <returns>The zlib stream.</returns>
		public static byte[] EncodeZlib(byte[] data, bool fastest = false)
		{
			using (var output = new MemoryStream(data.Length / 2 + 64))
			{
				// CMF = deflate with a 32K window; FLG = default compression with a valid check.
				output.WriteByte(0x78);
				output.WriteByte(0x9C);

				using (var deflate = new DeflateStream(output, fastest ? CompressionLevel.Fastest : CompressionLevel.Optimal, true))
					deflate.Write(data, 0, data.Length);

				var adler = Adler32(data);
				output.WriteByte((byte)(adler >> 24));
				output.WriteByte((byte)(adler >> 16));
				output.WriteByte((byte)(adler >> 8));
				output.WriteByte((byte)adler);

				return output.ToArray();
			}
		}

		/// <summary>
		/// Computes the Adler-32 checksum of <paramref name="data"/>.
		/// </summary>
		/// <param name="data">The input bytes.</param>
		/// <returns>The checksum.</returns>
		public static uint Adler32(byte[] data)
		{
			const uint Modulo = 65521;

			uint a = 1, b = 0;
			var index = 0;
			var remaining = data.Length;
			while (remaining > 0)
			{
				// 5552 is the largest block that can't overflow 32 bits before the modulo.
				var block = Math.Min(remaining, 5552);
				remaining -= block;
				while (block-- > 0)
				{
					a += data[index++];
					b += a;
				}

				a %= Modulo;
				b %= Modulo;
			}

			return (b << 16) | a;
		}
	}
}
