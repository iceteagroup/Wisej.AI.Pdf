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

// Test helper: formats PDF tokens (strings, numbers, rectangles) and converts
// between text and the Latin-1 "byte strings" used by the PDF builder.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wisej.AI.Pdf.Tests.Helpers
{
	/// <summary>
	/// Formatting helpers for the PDF syntax written by <see cref="PdfBuilder"/>.
	/// </summary>
	/// <remarks>
	/// The builder works with "byte strings": .NET strings where every character is a byte
	/// (0-255). They keep the PDF syntax readable in the tests and convert losslessly to bytes.
	/// </remarks>
	internal static class PdfText
	{
		// the characters of WinAnsiEncoding (code page 1252) in the range 0x80-0x9F; '\0' marks unused codes.
		private const string WinAnsiHigh =
			"€\0‚ƒ„…†‡ˆ‰Š‹Œ\0Ž\0" +
			"\0‘’“”•–—˜™š›œ\0žŸ";

		// reverse map of the range above.
		private static readonly Dictionary<char, byte> _winAnsi = BuildWinAnsi();

		/// <summary>
		/// Returns a PDF literal string for <paramref name="text"/>, encoded with WinAnsiEncoding.
		/// </summary>
		/// <param name="text">The text; characters outside WinAnsiEncoding are written as '?'.</param>
		/// <returns>The literal string, including the parentheses.</returns>
		public static string Literal(string text) => LiteralBytes(ToWinAnsi(text));

		/// <summary>
		/// Returns a PDF literal string for raw bytes, escaping delimiters and non-printable bytes.
		/// </summary>
		/// <param name="bytes">The bytes of the string.</param>
		/// <returns>The literal string, including the parentheses.</returns>
		public static string LiteralBytes(byte[] bytes)
		{
			var sb = new StringBuilder(bytes.Length + 2);
			sb.Append('(');
			foreach (var b in bytes)
			{
				switch (b)
				{
					case (byte)'(':
					case (byte)')':
					case (byte)'\\':
						sb.Append('\\').Append((char)b);
						break;

					default:
						// control bytes are written as octal escapes so the content stays on one line.
						if (b < 32 || b == 127)
							sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
						else
							sb.Append((char)b);
						break;
				}
			}

			return sb.Append(')').ToString();
		}

		/// <summary>
		/// Returns a PDF hexadecimal string for raw bytes.
		/// </summary>
		/// <param name="bytes">The bytes of the string.</param>
		/// <returns>The hexadecimal string, including the angle brackets.</returns>
		public static string Hex(byte[] bytes)
		{
			var sb = new StringBuilder(bytes.Length * 2 + 2);
			sb.Append('<');
			foreach (var b in bytes)
				sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));

			return sb.Append('>').ToString();
		}

		/// <summary>
		/// Returns a PDF text string (ISO 32000-2, 7.9.2.2): PDFDocEncoding for ASCII text, UTF-16BE otherwise.
		/// </summary>
		/// <param name="text">The text.</param>
		/// <returns>The string token.</returns>
		public static string TextString(string text)
		{
			foreach (var c in text)
			{
				if (c < 32 || c > 126)
				{
					var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
					var bytes = new byte[utf16.Length + 2];
					bytes[0] = 0xFE;
					bytes[1] = 0xFF;
					Buffer.BlockCopy(utf16, 0, bytes, 2, utf16.Length);
					return Hex(bytes);
				}
			}

			return LiteralBytes(Encoding.ASCII.GetBytes(text));
		}

		/// <summary>
		/// Encodes text with WinAnsiEncoding.
		/// </summary>
		/// <param name="text">The text.</param>
		/// <returns>One byte per character; unmapped characters become '?'.</returns>
		public static byte[] ToWinAnsi(string text)
		{
			var bytes = new byte[text.Length];
			for (int i = 0; i < text.Length; i++)
			{
				var c = text[i];
				if (c < 0x80 || (c >= 0xA0 && c <= 0xFF))
					bytes[i] = (byte)c;
				else
					bytes[i] = _winAnsi.TryGetValue(c, out var code) ? code : (byte)'?';
			}

			return bytes;
		}

		/// <summary>
		/// Converts a byte string to bytes.
		/// </summary>
		/// <param name="text">A string whose characters are all in the range 0-255.</param>
		/// <returns>The bytes.</returns>
		/// <exception cref="ArgumentException">A character is outside the byte range.</exception>
		public static byte[] ToBytes(string text)
		{
			var bytes = new byte[text.Length];
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] > 0xFF)
					throw new ArgumentException($"Character U+{(int)text[i]:X4} at {i} is not a byte.", nameof(text));

				bytes[i] = (byte)text[i];
			}

			return bytes;
		}

		/// <summary>
		/// Converts bytes to a byte string.
		/// </summary>
		/// <param name="bytes">The bytes.</param>
		/// <returns>A string with one character per byte.</returns>
		public static string FromBytes(byte[] bytes)
		{
			var chars = new char[bytes.Length];
			for (int i = 0; i < bytes.Length; i++)
				chars[i] = (char)bytes[i];

			return new string(chars);
		}

		/// <summary>
		/// Formats a number with the invariant culture and at most four decimals.
		/// </summary>
		/// <param name="value">The value.</param>
		/// <returns>The PDF number token.</returns>
		public static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

		/// <summary>
		/// Formats a sequence of numbers separated by spaces.
		/// </summary>
		/// <param name="values">The values.</param>
		/// <returns>The number tokens.</returns>
		public static string Numbers(params double[] values)
		{
			var parts = new string[values.Length];
			for (int i = 0; i < values.Length; i++)
				parts[i] = Number(values[i]);

			return string.Join(" ", parts);
		}

		/// <summary>
		/// Formats a PDF array of numbers, for example a rectangle.
		/// </summary>
		/// <param name="values">The values.</param>
		/// <returns>The array token, including the brackets.</returns>
		public static string Array(params double[] values) => "[" + Numbers(values) + "]";

		/// <summary>
		/// Returns an indirect reference token.
		/// </summary>
		/// <param name="number">The object number.</param>
		/// <returns>The reference, for example "12 0 R".</returns>
		public static string Reference(int number) => number.ToString(CultureInfo.InvariantCulture) + " 0 R";

		// maps the characters of the WinAnsi range 0x80-0xFF to their codes.
		private static Dictionary<char, byte> BuildWinAnsi()
		{
			var map = new Dictionary<char, byte>();
			for (int i = 0; i < WinAnsiHigh.Length; i++)
			{
				if (WinAnsiHigh[i] != '\0')
					map[WinAnsiHigh[i]] = (byte)(0x80 + i);
			}

			return map;
		}
	}
}
