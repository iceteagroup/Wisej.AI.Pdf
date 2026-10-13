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

// ASCIIHexDecode, ASCII85Decode and RunLengthDecode filters.

using System;
using System.IO;
using Wisej.AI.Pdf.IO;

namespace Wisej.AI.Pdf.Filters
{
	/// <summary>
	/// Decodes data encoded with the ASCIIHexDecode filter.
	/// </summary>
	internal static class AsciiHexFilter
	{
		/// <summary>
		/// Decodes hexadecimal digits up to the <c>&gt;</c> end-of-data marker, ignoring whitespace.
		/// </summary>
		/// <param name="data">The encoded bytes.</param>
		/// <returns>The decoded bytes.</returns>
		public static byte[] Decode(byte[] data)
		{
			var output = new MemoryStream(data.Length / 2);
			var high = -1;
			foreach (var c in data)
			{
				if (c == '>')
					break;

				if (!PdfLexer.IsHex(c))
					continue;

				if (high < 0)
				{
					high = PdfLexer.HexValue(c);
				}
				else
				{
					output.WriteByte((byte)(high * 16 + PdfLexer.HexValue(c)));
					high = -1;
				}
			}

			// an odd number of digits is padded with a final 0.
			if (high >= 0)
				output.WriteByte((byte)(high * 16));

			return output.ToArray();
		}
	}

	/// <summary>
	/// Decodes data encoded with the ASCII85Decode (base-85) filter.
	/// </summary>
	internal static class Ascii85Filter
	{
		/// <summary>
		/// Decodes base-85 data up to the <c>~&gt;</c> end-of-data marker.
		/// </summary>
		/// <param name="data">The encoded bytes.</param>
		/// <returns>The decoded bytes.</returns>
		public static byte[] Decode(byte[] data)
		{
			var output = new MemoryStream(data.Length * 4 / 5);
			var start = 0;

			// skip an optional "<~" prefix.
			while (start < data.Length && PdfLexer.IsWhitespace(data[start]))
				start++;

			if (start + 1 < data.Length && data[start] == '<' && data[start + 1] == '~')
				start += 2;

			uint tuple = 0;
			var count = 0;
			for (int i = start; i < data.Length; i++)
			{
				var c = data[i];
				if (c == '~')
					break;

				// 'z' is shorthand for four zero bytes.
				if (c == 'z' && count == 0)
				{
					output.Write(new byte[4], 0, 4);
					continue;
				}

				if (c < '!' || c > 'u')
					continue;

				tuple = tuple * 85 + (uint)(c - '!');
				if (++count == 5)
				{
					output.WriteByte((byte)(tuple >> 24));
					output.WriteByte((byte)(tuple >> 16));
					output.WriteByte((byte)(tuple >> 8));
					output.WriteByte((byte)tuple);
					tuple = 0;
					count = 0;
				}
			}

			// a final partial group of n characters encodes n - 1 bytes: pad with 'u' and keep the leading bytes.
			if (count > 1)
			{
				for (int i = count; i < 5; i++)
					tuple = tuple * 85 + 84;

				for (int i = 0; i < count - 1; i++)
					output.WriteByte((byte)(tuple >> (24 - i * 8)));
			}

			return output.ToArray();
		}
	}

	/// <summary>
	/// Decodes data encoded with the RunLengthDecode filter (PackBits).
	/// </summary>
	internal static class RunLengthFilter
	{
		/// <summary>
		/// Decodes run-length encoded data up to the end-of-data byte (128).
		/// </summary>
		/// <param name="data">The encoded bytes.</param>
		/// <returns>The decoded bytes.</returns>
		public static byte[] Decode(byte[] data)
		{
			var output = new MemoryStream(data.Length * 2);
			var i = 0;
			while (i < data.Length)
			{
				var length = data[i++];
				if (length == 128)
					break;

				if (length < 128)
				{
					// copy the next length + 1 bytes literally.
					var count = Math.Min(length + 1, data.Length - i);
					output.Write(data, i, count);
					i += count;
				}
				else if (i < data.Length)
				{
					// repeat the next byte 257 - length times.
					var value = data[i++];
					for (int n = 257 - length; n > 0; n--)
						output.WriteByte(value);
				}
			}

			return output.ToArray();
		}
	}
}
