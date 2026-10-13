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

// Named encodings, PDFDocEncoding and glyph name to Unicode mapping.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// Provides the predefined simple font encodings.
	/// </summary>
	internal static partial class Encodings
	{
		/// <summary>
		/// Returns the glyph-name table of a predefined encoding.
		/// </summary>
		/// <param name="name">The encoding name, for example <c>WinAnsiEncoding</c>.</param>
		/// <returns>The table, or null when the name is not a predefined encoding.</returns>
		public static string[] Get(string name)
		{
			switch (name)
			{
				case "StandardEncoding": return Standard;
				case "WinAnsiEncoding": return WinAnsi;
				case "MacRomanEncoding": return MacRoman;
				case "MacExpertEncoding": return MacExpert;
				case "SymbolSetEncoding": return Symbol;
				case "ZapfDingbatsEncoding": return ZapfDingbats;
				default: return null;
			}
		}

		/// <summary>
		/// Provides decoding of PDFDocEncoding, the single-byte encoding used for text strings.
		/// </summary>
		public static class PdfDocEncoding
		{
			// byte to character; differs from Latin-1 at 0x18-0x1F, 0x80-0xA0 and 0xAD.
			private static readonly char[] _table = BuildTable();

			/// <summary>
			/// Builds the PDFDocEncoding table (ISO 32000-2, Annex D.2): Latin-1 with the exceptions at
			/// 0x18-0x1F (spacing diacritics), 0x80-0xA0 (typographic characters, ligatures, the euro sign)
			/// and the undefined codes 0x9F and 0xAD, which decode as U+FFFD.
			/// </summary>
			/// <returns>The 256-entry table.</returns>
			private static char[] BuildTable()
			{
				var table = new char[256];
				for (int i = 0; i < 256; i++)
					table[i] = (char)i;

				var low = "˘ˇˆ˙˝˛˚˜";
				for (int i = 0; i < low.Length; i++)
					table[0x18 + i] = low[i];

				var high =
					"•†‡…—–ƒ⁄‹›−‰„“”‘" +
					"’‚™ﬁﬂŁŒŠŸŽıłœšž�" +
					"€";
				for (int i = 0; i < high.Length; i++)
					table[0x80 + i] = high[i];

				table[0xAD] = '�';
				return table;
			}

			/// <summary>
			/// Decodes PDFDocEncoded bytes.
			/// </summary>
			/// <param name="bytes">The encoded bytes.</param>
			/// <returns>The decoded text.</returns>
			public static string Decode(byte[] bytes)
			{
				var chars = new char[bytes.Length];
				for (int i = 0; i < bytes.Length; i++)
					chars[i] = _table[bytes[i]];

				return new string(chars).TrimEnd('\0');
			}
		}
	}

	/// <summary>
	/// Maps glyph names to Unicode text using the Adobe Glyph List and its naming conventions.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Resolution follows the Adobe Glyph List Specification: the name is truncated at the first period,
	/// split into components at underscores (ligatures), and each component is looked up in the
	/// AGL, or decoded from the <c>uniXXXX</c> and <c>uXXXX[XX]</c> forms.
	/// </para>
	/// <para>
	/// Common non-standard names written by real-world tools are also recognized, for example
	/// <c>Gxx</c>/<c>gxx</c> hexadecimal glyph names produced by some subsetters.
	/// </para>
	/// </remarks>
	internal static class GlyphList
	{
		// the Adobe Glyph List: glyph name to Unicode text, loaded on first use.
		private static readonly Lazy<Dictionary<string, string>> _agl = new Lazy<Dictionary<string, string>>(() => Load("glyphlist.txt"));

		// the ITC Zapf Dingbats glyph list (a1, a2, ...), loaded on first use.
		private static readonly Lazy<Dictionary<string, string>> _dingbats = new Lazy<Dictionary<string, string>>(() => Load("zapfdingbats.txt"));

		// Unicode text to the shortest AGL glyph name, built on first use.
		private static readonly Lazy<Dictionary<string, string>> _reverse = new Lazy<Dictionary<string, string>>(BuildReverse);

		/// <summary>
		/// Returns the Unicode text for <paramref name="glyphName"/>.
		/// </summary>
		/// <param name="glyphName">The glyph name.</param>
		/// <param name="dingbats">True to look up the ZapfDingbats glyph list first.</param>
		/// <returns>The text, or null when the name cannot be mapped.</returns>
		public static string ToUnicode(string glyphName, bool dingbats = false)
		{
			if (string.IsNullOrEmpty(glyphName) || glyphName == ".notdef")
				return null;

			if (dingbats && _dingbats.Value.TryGetValue(glyphName, out var dingbat))
				return dingbat;

			if (_agl.Value.TryGetValue(glyphName, out var text))
				return text;

			// drop the suffix after the first period: "a.sc" -> "a".
			var name = glyphName;
			var period = name.IndexOf('.');
			if (period > 0)
				name = name.Substring(0, period);
			else if (period == 0)
				return null;

			// ligatures: "f_f_i" -> "ffi".
			if (name.IndexOf('_') > 0)
			{
				var sb = new StringBuilder();
				foreach (var part in name.Split('_'))
				{
					var partText = MapComponent(part, dingbats);
					if (partText == null)
						return null;

					sb.Append(partText);
				}

				return sb.ToString();
			}

			return MapComponent(name, dingbats);
		}

		/// <summary>
		/// Returns the AGL glyph name for a Unicode character, used to match fonts by name.
		/// </summary>
		/// <param name="text">The character.</param>
		/// <returns>The glyph name, or null.</returns>
		public static string FromUnicode(string text)
		{
			return text != null && _reverse.Value.TryGetValue(text, out var name) ? name : null;
		}

		/// <summary>
		/// Maps one component of a glyph name (no period suffix, no underscores): a list lookup,
		/// then the <c>uniXXXX[XXXX...]</c> and <c>uXXXX[XX]</c> forms.
		/// </summary>
		/// <param name="name">The name component.</param>
		/// <param name="dingbats">True to look up the ZapfDingbats glyph list first.</param>
		/// <returns>The text, or null when the component cannot be mapped.</returns>
		private static string MapComponent(string name, bool dingbats)
		{
			if (name.Length == 0)
				return null;

			if (dingbats && _dingbats.Value.TryGetValue(name, out var dingbat))
				return dingbat;

			if (_agl.Value.TryGetValue(name, out var text))
				return text;

			// "uniXXXX[XXXX...]": sequence of 4-digit UTF-16 code units (no surrogates).
			if (name.Length >= 7 && name.StartsWith("uni", StringComparison.Ordinal) && (name.Length - 3) % 4 == 0)
			{
				var sb = new StringBuilder();
				for (int i = 3; i < name.Length; i += 4)
				{
					if (!TryParseHex(name, i, 4, out var value) || (value >= 0xD800 && value <= 0xDFFF))
						return null;

					sb.Append((char)value);
				}

				return sb.ToString();
			}

			// "uXXXX" to "uXXXXXX": a single code point.
			if (name.Length >= 5 && name.Length <= 7 && name[0] == 'u')
			{
				if (TryParseHex(name, 1, name.Length - 1, out var value) && value <= 0x10FFFF && (value < 0xD800 || value > 0xDFFF))
					return char.ConvertFromUtf32(value);
			}

			return null;
		}

		/// <summary>
		/// Tries to parse a hexadecimal number from a part of <paramref name="text"/>.
		/// </summary>
		/// <param name="text">The source text.</param>
		/// <param name="start">Index of the first digit.</param>
		/// <param name="length">Number of digits.</param>
		/// <param name="value">Receives the parsed value.</param>
		/// <returns>True when all the characters are hexadecimal digits.</returns>
		public static bool TryParseHex(string text, int start, int length, out int value)
		{
			value = 0;
			if (length <= 0 || start + length > text.Length)
				return false;

			for (int i = start; i < start + length; i++)
			{
				var c = text[i];
				int digit;
				if (c >= '0' && c <= '9')
					digit = c - '0';
				else if (c >= 'A' && c <= 'F')
					digit = c - 'A' + 10;
				else if (c >= 'a' && c <= 'f')
					digit = c - 'a' + 10;
				else
					return false;

				value = value * 16 + digit;
			}

			return true;
		}

		/// <summary>
		/// Loads a glyph list embedded as a resource, in the AGL format <c>name;XXXX[ XXXX...]</c> with <c>#</c> comments.
		/// </summary>
		/// <param name="resourceName">The resource file name under <c>Wisej.AI.Pdf.Resources</c>.</param>
		/// <returns>The glyph name to text table; empty when the resource is missing.</returns>
		private static Dictionary<string, string> Load(string resourceName)
		{
			var table = new Dictionary<string, string>(StringComparer.Ordinal);
			using (var stream = typeof(GlyphList).Assembly.GetManifestResourceStream("Wisej.AI.Pdf.Resources." + resourceName))
			{
				if (stream == null)
					return table;

				using (var reader = new StreamReader(stream))
				{
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						// format: "name;XXXX[ XXXX...]"
						if (line.Length == 0 || line[0] == '#')
							continue;

						var semicolon = line.IndexOf(';');
						if (semicolon <= 0)
							continue;

						var sb = new StringBuilder();
						foreach (var code in line.Substring(semicolon + 1).Split(' '))
						{
							if (int.TryParse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
								sb.Append(char.ConvertFromUtf32(value));
						}

						table[line.Substring(0, semicolon)] = sb.ToString();
					}
				}
			}

			return table;
		}

		/// <summary>
		/// Builds the Unicode text to glyph name table from the AGL.
		/// </summary>
		/// <returns>The reverse table.</returns>
		private static Dictionary<string, string> BuildReverse()
		{
			var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var entry in _agl.Value)
			{
				// keep the shortest name for each character, which is the canonical one in practice.
				if (!reverse.TryGetValue(entry.Value, out var existing) || entry.Key.Length < existing.Length)
					reverse[entry.Value] = entry.Key;
			}

			return reverse;
		}
	}
}
