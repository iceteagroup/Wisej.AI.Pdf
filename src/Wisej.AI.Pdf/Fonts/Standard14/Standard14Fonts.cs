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

// The 14 standard Type 1 fonts (ISO 32000-2, 9.6.2.2): metrics lookup, aliases and substitution.

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// Provides the metrics of the 14 standard fonts and resolves font names to them.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The metrics come from Adobe's Core14 AFM files, stored in a compressed embedded resource that is
	/// decoded on first use. All members are thread-safe.
	/// </para>
	/// <para>
	/// Besides the standard names, <see cref="Get"/> recognizes the metric-compatible fonts that
	/// producers commonly reference without embedding them: Arial for Helvetica, Times New Roman for
	/// Times and Courier New for Courier, in their PostScript (<c>Arial-BoldMT</c>,
	/// <c>TimesNewRomanPS-ItalicMT</c>) and Windows (<c>Arial,Bold</c>) naming styles.
	/// </para>
	/// </remarks>
	internal static class Standard14Fonts
	{
		#region Fields

		// the logical name of the embedded resource with the metrics.
		internal const string ResourceName = "Wisej.AI.Pdf.Resources.Standard14.core14.bin";

		// the fonts by canonical name, loaded on first use.
		private static readonly Lazy<Dictionary<string, Standard14Font>> _fonts = new Lazy<Dictionary<string, Standard14Font>>(Load);

		// the canonical family prefixes that are recognized, longest first so that
		// "TimesNewRomanPS" is matched before "Times". Names are lowercase without spaces.
		private static readonly string[][] _families =
		{
			new[] { "timesnewromanps", "Times" },
			new[] { "timesnewroman", "Times" },
			new[] { "itczapfdingbats", "ZapfDingbats" },
			new[] { "couriernewps", "Courier" },
			new[] { "zapfdingbats", "ZapfDingbats" },
			new[] { "couriernew", "Courier" },
			new[] { "helvetica", "Helvetica" },
			new[] { "dingbats", "ZapfDingbats" },
			new[] { "symbolmt", "Symbol" },
			new[] { "courier", "Courier" },
			new[] { "arialmt", "Helvetica" },
			new[] { "symbol", "Symbol" },
			new[] { "arial", "Helvetica" },
			new[] { "times", "Times" },
		};

		// the style words accepted after the family name, longest first. Anything else (Narrow, Black,
		// Light, Condensed, ...) means a font with different metrics, which is not a standard font.
		private static readonly string[] _styleWords =
		{
			"boldoblique", "bolditalic", "regular", "oblique", "italic", "normal", "roman", "plain",
			"bold", "book", "psmt", "std", "ps", "mt"
		};

		#endregion

		#region Properties

		/// <summary>
		/// Returns the canonical names of the 14 standard fonts.
		/// </summary>
		public static ICollection<string> Names => _fonts.Value.Keys;

		#endregion

		#region Methods

		/// <summary>
		/// Returns the standard font for a font name, recognizing aliases and subset prefixes.
		/// </summary>
		/// <param name="baseFontName">The <c>/BaseFont</c> name, for example <c>Helvetica-Bold</c>, <c>ABCDEF+Arial,Bold</c> or <c>TimesNewRomanPS-BoldMT</c>.</param>
		/// <returns>The font, or null when the name is not one of the 14 standard fonts or a metric-compatible alias.</returns>
		public static Standard14Font Get(string baseFontName)
		{
			var name = Normalize(baseFontName);
			if (name.Length == 0)
				return null;

			string family = null;
			string rest = null;
			foreach (var entry in _families)
			{
				if (name.StartsWith(entry[0], StringComparison.Ordinal))
				{
					family = entry[1];
					rest = name.Substring(entry[0].Length);
					break;
				}
			}

			if (family == null)
				return null;

			if (!ParseStyle(rest, out var bold, out var italic))
				return null;

			return Find(family, bold, italic);
		}

		/// <summary>
		/// Returns the best standard font to substitute for any font, using the name and the descriptor hints.
		/// </summary>
		/// <param name="baseFontName">The font name; may be null.</param>
		/// <param name="bold">True when the font is known to be bold (for example from the font weight or flags).</param>
		/// <param name="italic">True when the font is known to be italic.</param>
		/// <param name="serif">True when the font is known to be a serif font.</param>
		/// <param name="fixedPitch">True when the font is known to be monospaced.</param>
		/// <returns>The substitute font. Never null.</returns>
		public static Standard14Font GetSubstitute(string baseFontName, bool bold, bool italic, bool serif, bool fixedPitch)
		{
			var font = Get(baseFontName);
			if (font != null)
				return font;

			var name = Normalize(baseFontName);

			bold |= ContainsAny(name, "bold", "black", "heavy", "demi", "semibold", "extrabold", "ultrabold", "fat");
			italic |= ContainsAny(name, "italic", "oblique", "slanted", "kursiv") || name.EndsWith("-it", StringComparison.Ordinal) || name.EndsWith(",it", StringComparison.Ordinal);
			fixedPitch |= ContainsAny(name, "mono", "courier", "consol", "typewriter", "fixed", "menlo", "inconsolata", "lucidaconsole", "andale");

			// serif hints, unless the name says sans-serif (gothic is the CJK sans-serif).
			if (!ContainsAny(name, "sans", "gothic", "grotesk", "arial", "helvetica", "verdana", "tahoma", "calibri", "segoe"))
			{
				serif |= ContainsAny(name, "times", "serif", "georgia", "garamond", "minion", "cambria", "palatino", "bookman",
					"century", "baskerville", "caslon", "bodoni", "didot", "antiqua", "mincho", "song", "ming", "batang", "myungjo", "roman");
			}

			if (fixedPitch)
				return Find("Courier", bold, italic);

			return Find(serif ? "Times" : "Helvetica", bold, italic);
		}

		#endregion

		#region Implementation

		// removes the subset prefix ("ABCDEF+") and the spaces, and lowercases the name.
		private static string Normalize(string name)
		{
			if (string.IsNullOrEmpty(name))
				return string.Empty;

			var plus = name.IndexOf('+');
			if (plus > 0 && plus <= 8)
			{
				var isTag = true;
				for (int i = 0; i < plus && isTag; i++)
					isTag = char.IsLetterOrDigit(name[i]);

				if (isTag)
					name = name.Substring(plus + 1);
			}

			var chars = new char[name.Length];
			var length = 0;
			foreach (var c in name)
			{
				if (c != ' ')
					chars[length++] = char.ToLowerInvariant(c);
			}

			return new string(chars, 0, length);
		}

		// parses the style suffix after the family name: separators and the known style words only.
		private static bool ParseStyle(string rest, out bool bold, out bool italic)
		{
			bold = false;
			italic = false;

			var position = 0;
			while (position < rest.Length)
			{
				var c = rest[position];
				if (c == ',' || c == '-' || c == '_' || c == ';')
				{
					position++;
					continue;
				}

				string matched = null;
				foreach (var word in _styleWords)
				{
					if (string.CompareOrdinal(rest, position, word, 0, word.Length) == 0)
					{
						matched = word;
						break;
					}
				}

				if (matched == null)
					return false;

				if (matched.StartsWith("bold", StringComparison.Ordinal))
					bold = true;

				if (matched.EndsWith("italic", StringComparison.Ordinal) || matched.EndsWith("oblique", StringComparison.Ordinal))
					italic = true;

				position += matched.Length;
			}

			return true;
		}

		// returns the font of a family with the given style.
		private static Standard14Font Find(string family, bool bold, bool italic)
		{
			string name;
			switch (family)
			{
				case "Symbol":
				case "ZapfDingbats":
					name = family;
					break;

				case "Times":
					name = bold ? (italic ? "Times-BoldItalic" : "Times-Bold") : (italic ? "Times-Italic" : "Times-Roman");
					break;

				default:
					name = family + (bold ? (italic ? "-BoldOblique" : "-Bold") : (italic ? "-Oblique" : string.Empty));
					break;
			}

			var fonts = _fonts.Value;
			if (fonts.TryGetValue(name, out var font))
				return font;

			// only reachable when the resource is missing: return a font without widths rather than null.
			return new Standard14Font(null)
			{
				Name = name,
				FamilyName = family,
				IsBold = bold,
				IsItalic = italic,
				IsFixedPitch = family == "Courier",
				IsSerif = family == "Times",
				IsSymbolic = family == "Symbol" || family == "ZapfDingbats",
				Ascent = 718,
				Descent = -207,
				CapHeight = 718,
				XHeight = 523,
				FontBBox = new double[] { -166, -225, 1000, 931 }
			};
		}

		// true when the text contains any of the words.
		private static bool ContainsAny(string text, params string[] words)
		{
			foreach (var word in words)
			{
				if (text.IndexOf(word, StringComparison.Ordinal) >= 0)
					return true;
			}

			return false;
		}

		/// <summary>
		/// Loads the metrics of the 14 fonts from the embedded resource.
		/// </summary>
		/// <returns>The fonts by name; empty when the resource is missing.</returns>
		/// <remarks>
		/// <para>The resource is a <see cref="CMapArchive"/> with one entry, <c>core14</c>:</para>
		/// <code>
		/// fontCount
		/// { name, familyName, flags (byte: 1 bold, 2 italic, 4 fixed, 8 serif, 16 symbolic),
		///   italicAngle * 10, ascent, descent, capHeight, xHeight, bbox[4]    (zigzag varints)
		///   glyphCount, { code (zigzag, -1 when unencoded), width, glyphName } }
		/// </code>
		/// </remarks>
		private static Dictionary<string, Standard14Font> Load()
		{
			var fonts = new Dictionary<string, Standard14Font>(StringComparer.Ordinal);
			var data = CMapArchive.LoadResource(ResourceName).Read("core14");
			if (data == null)
				return fonts;

			var reader = new CMapDataReader(data, 0);
			var count = reader.ReadVarInt();
			for (int f = 0; f < count && !reader.IsEnd; f++)
			{
				var name = reader.ReadString();
				var familyName = reader.ReadString();
				var flags = reader.ReadByte();
				var italicAngle = reader.ReadSignedVarLong() / 10.0;
				var ascent = reader.ReadSignedVarLong();
				var descent = reader.ReadSignedVarLong();
				var capHeight = reader.ReadSignedVarLong();
				var xHeight = reader.ReadSignedVarLong();
				var bbox = new double[4];
				for (int i = 0; i < 4; i++)
					bbox[i] = reader.ReadSignedVarLong();

				var glyphCount = reader.ReadVarInt();
				var widths = new Dictionary<string, double>(Math.Min(glyphCount, 1024), StringComparer.Ordinal);
				string[] encoding = null;
				for (int g = 0; g < glyphCount && !reader.IsEnd; g++)
				{
					var code = (int)reader.ReadSignedVarLong();
					var width = reader.ReadVarInt();
					var glyph = reader.ReadString();
					widths[glyph] = width;

					// the built-in encoding matters only for the symbolic fonts: the others use StandardEncoding.
					if ((flags & 16) != 0 && code >= 0 && code < 256)
					{
						if (encoding == null)
							encoding = new string[256];

						encoding[code] = glyph;
					}
				}

				fonts[name] = new Standard14Font(widths)
				{
					Name = name,
					FamilyName = familyName,
					IsBold = (flags & 1) != 0,
					IsItalic = (flags & 2) != 0,
					IsFixedPitch = (flags & 4) != 0,
					IsSerif = (flags & 8) != 0,
					IsSymbolic = (flags & 16) != 0,
					ItalicAngle = italicAngle,
					Ascent = ascent,
					Descent = descent,
					CapHeight = capHeight,
					XHeight = xHeight,
					FontBBox = bbox,
					BuiltInEncoding = encoding
				};
			}

			return fonts;
		}

		#endregion
	}
}
