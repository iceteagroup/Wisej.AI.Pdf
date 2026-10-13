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

// Substitution of non-embedded fonts (ISO 32000-2, 9.6.2.2 and 9.10) with
// TrueType fonts installed on the system.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Wisej.AI.Pdf.Fonts;
using Wisej.AI.Pdf.Fonts.Programs;

namespace Wisej.AI.Pdf.Rendering
{
	/// <summary>
	/// The glyphs that draw a character code with a substituted font.
	/// </summary>
	internal sealed class SubstituteGlyphs
	{
		/// <summary>
		/// The result for codes that can't be drawn.
		/// </summary>
		public static readonly SubstituteGlyphs None = new SubstituteGlyphs(null, Array.Empty<int>());

		/// <summary>
		/// Initializes a new instance of <see cref="SubstituteGlyphs"/>.
		/// </summary>
		/// <param name="program">The font program of the glyphs.</param>
		/// <param name="glyphs">The glyph ids, drawn one after the other.</param>
		public SubstituteGlyphs(FontProgram program, int[] glyphs)
		{
			this.Program = program;
			this.Glyphs = glyphs;
		}

		/// <summary>
		/// The font program of the glyphs.
		/// </summary>
		public FontProgram Program { get; }

		/// <summary>
		/// The glyph ids: usually one, several for decomposed ligatures; empty when the code can't be drawn.
		/// </summary>
		public int[] Glyphs { get; }
	}

	/// <summary>
	/// A system font substituted for a PDF font that has no embedded program.
	/// </summary>
	internal sealed class SubstituteFont
	{
		// the glyphs of each character code.
		private readonly Dictionary<int, SubstituteGlyphs> _glyphs = new Dictionary<int, SubstituteGlyphs>();

		// the PDF font, for the Unicode text and the glyph names of the codes.
		private readonly PdfFont _font;

		/// <summary>
		/// Initializes a new instance of <see cref="SubstituteFont"/>.
		/// </summary>
		/// <param name="font">The PDF font.</param>
		/// <param name="program">The system font program.</param>
		/// <param name="isSymbol">True when the program is a symbol font addressed through the (3,0) cmap.</param>
		public SubstituteFont(PdfFont font, FontProgram program, bool isSymbol)
		{
			_font = font;
			this.Program = program;
			this.IsSymbol = isSymbol;
		}

		/// <summary>
		/// The system font program.
		/// </summary>
		public FontProgram Program { get; }

		/// <summary>
		/// True when the program is a symbol font.
		/// </summary>
		public bool IsSymbol { get; }

		/// <summary>
		/// Returns the glyphs that draw a character code.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <returns>The glyphs; <see cref="SubstituteGlyphs.None"/> when the code can't be drawn.</returns>
		public SubstituteGlyphs GetGlyphs(int code)
		{
			lock (_glyphs)
			{
				if (_glyphs.TryGetValue(code, out var cached))
					return cached;
			}

			SubstituteGlyphs glyphs;
			try
			{
				glyphs = Map(code);
			}
			catch (Exception)
			{
				glyphs = SubstituteGlyphs.None;
			}

			lock (_glyphs)
				_glyphs[code] = glyphs;

			return glyphs;
		}

		// maps a character code to glyphs: the symbol cmap, the Unicode text (ligatures and CJK fallback), then the glyph name.
		private SubstituteGlyphs Map(int code)
		{
			var program = this.Program;

			// symbol fonts map single-byte codes in the private use area.
			if (this.IsSymbol && code >= 0 && code < 256)
			{
				if (program.TryGetCmapGlyph(3, 0, 0xF000 + code, out var symbolGlyph) || program.TryGetCmapGlyph(3, 0, code, out symbolGlyph))
					return new SubstituteGlyphs(program, new[] { symbolGlyph });
			}

			var text = _font.GetUnicode(code);
			if (!string.IsNullOrEmpty(text))
			{
				// the text of ligatures is expanded: try the ligature glyph first.
				var ligature = GetLigature(text);
				if (ligature != 0 && TryUnicode(program, ligature, out var ligatureGlyph))
					return new SubstituteGlyphs(program, new[] { ligatureGlyph });

				var glyphs = MapText(program, text, out var complete);

				// CJK characters missing from a Latin substitute: try a CJK font of the system.
				if (!complete && HasCjk(text))
				{
					var cjk = SystemFonts.GetCjkProgram((_font as Type0Font)?.Ordering);
					if (cjk != null)
					{
						var cjkGlyphs = MapText(cjk, text, out _);
						if (cjkGlyphs != null)
							return new SubstituteGlyphs(cjk, cjkGlyphs);
					}
				}

				if (glyphs != null)
					return new SubstituteGlyphs(program, glyphs);
			}

			// fall back on the glyph name, looked up in the post table of the system font.
			if (_font is SimpleFont simple)
			{
				var name = simple.GetGlyphName(code);
				if (name != null)
				{
					var glyph = program.GetGlyphId(name);
					if (glyph > 0)
						return new SubstituteGlyphs(program, new[] { glyph });
				}
			}

			return SubstituteGlyphs.None;
		}

		// the glyphs of the characters of the text, or null when none is found or the text is too long.
		private static int[] MapText(FontProgram program, string text, out bool complete)
		{
			var glyphs = new List<int>(text.Length);
			complete = true;
			for (int i = 0; i < text.Length; i++)
			{
				var codePoint = (int)text[i];
				if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
				{
					codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
					i++;
				}

				// spaces have no outline.
				if (codePoint == ' ')
					continue;

				if (TryUnicode(program, codePoint, out var glyph))
					glyphs.Add(glyph);
				else
					complete = false;
			}

			return glyphs.Count > 0 && glyphs.Count <= 4 ? glyphs.ToArray() : null;
		}

		// looks up a code point in the Unicode cmaps of the program.
		private static bool TryUnicode(FontProgram program, int codePoint, out int glyph)
		{
			return program.TryGetCmapGlyph(3, 1, codePoint, out glyph)
				|| program.TryGetCmapGlyph(3, 10, codePoint, out glyph)
				|| program.TryGetCmapGlyph(0, 3, codePoint, out glyph)
				|| program.TryGetCmapGlyph(0, 4, codePoint, out glyph);
		}

		// true when the text has characters of the CJK blocks (radicals, kana, ideographs, Hangul, full-width forms).
		private static bool HasCjk(string text)
		{
			foreach (var c in text)
			{
				if (c >= 0x2E80 && c <= 0x9FFF || c >= 0xAC00 && c <= 0xD7AF || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFFEF || char.IsSurrogate(c))
					return true;
			}

			return false;
		}

		// the code point of the ligature glyph for the expanded text, or 0 when it is not a ligature.
		private static int GetLigature(string text)
		{
			switch (text)
			{
				case "ff": return 0xFB00;
				case "fi": return 0xFB01;
				case "fl": return 0xFB02;
				case "ffi": return 0xFB03;
				case "ffl": return 0xFB04;
				default: return 0;
			}
		}
	}

	/// <summary>
	/// Finds system fonts to substitute the PDF fonts that are not embedded.
	/// </summary>
	/// <remarks>
	/// The font folders of Windows, Linux and macOS are indexed once by file name. A font is matched
	/// by its family name first, then by its classification (sans serif, serif, monospaced, symbol),
	/// using the metric-compatible fonts of each platform: Arial, Times New Roman and Courier New
	/// on Windows, the Liberation fonts on Linux, Helvetica and Times on macOS. All the members are thread-safe.
	/// </remarks>
	internal static class SystemFonts
	{
		// bounds on the font folder scan.
		private const int MaxFiles = 20000;
		// the deepest subfolder level scanned.
		private const int MaxDepth = 5;

		// the substitutes of the fonts, computed once per font.
		private static readonly ConditionalWeakTable<PdfFont, Holder> _substitutes = new ConditionalWeakTable<PdfFont, Holder>();

		// loaded programs by file path; null entries are files that failed to load.
		private static readonly Dictionary<string, FontProgram> _programs = new Dictionary<string, FontProgram>(StringComparer.OrdinalIgnoreCase);

		// font files by normalized file name (lowercase letters and digits only).
		private static Dictionary<string, string> _files;
		// the lock guarding the lazy scan of the font folders.
		private static readonly object _sync = new object();

		// families of the metric-compatible substitutes, in order of preference.
		private static readonly string[] SansFamilies = { "arial", "liberationsans", "helvetica", "arimo", "nimbussans", "freesans", "dejavusans" };
		// the serif families.
		private static readonly string[] SerifFamilies = { "times", "timesnewroman", "liberationserif", "tinos", "nimbusroman", "freeserif", "dejavuserif" };
		// the monospaced families.
		private static readonly string[] MonoFamilies = { "cour", "couriernew", "liberationmono", "cousine", "nimbusmono", "freemono", "dejavusansmono" };
		// the symbol families.
		private static readonly string[] SymbolFamilies = { "symbol", "standardsymbolsps" };
		// the families with the dingbat and wingding glyphs.
		private static readonly string[] DingbatFamilies = { "seguisym", "dejavusans", "notosanssymbols2", "freeserif" };

		// CJK fonts by character collection, in order of preference (Windows, Linux, macOS).
		private static readonly string[] JapanFamilies = { "yugothr", "msgothic", "meiryo", "msmincho", "notosanscjkjp", "notosansjp", "notosanscjk", "ipaexg", "ipag", "takaogothic", "droidsansfallbackfull", "arialuni", "arialunicode" };
		// the simplified Chinese (GB1) families.
		private static readonly string[] ChineseSimplifiedFamilies = { "msyh", "simsun", "simhei", "deng", "notosanscjksc", "notosanssc", "notosanscjk", "wqyzenhei", "wqymicrohei", "pingfang", "hiraginosansgb", "droidsansfallbackfull", "arialuni", "arialunicode" };
		// the traditional Chinese (CNS1) families.
		private static readonly string[] ChineseTraditionalFamilies = { "msjh", "mingliu", "notosanscjktc", "notosanstc", "notosanscjk", "pingfang", "droidsansfallbackfull", "arialuni", "arialunicode" };
		// the Korean (Korea1) families.
		private static readonly string[] KoreanFamilies = { "malgun", "gulim", "batang", "notosanscjkkr", "notosanskr", "notosanscjk", "nanumgothic", "applesdgothicneo", "droidsansfallbackfull", "arialuni", "arialunicode" };

		// the CJK program of each character collection; null entries were not found.
		private static readonly Dictionary<string, FontProgram> _cjkPrograms = new Dictionary<string, FontProgram>(StringComparer.Ordinal);

		/// <summary>
		/// Holds the substitute of a font, including a null result.
		/// </summary>
		private sealed class Holder
		{
			/// <summary>
			/// The substitute font, or null when none was found.
			/// </summary>
			public SubstituteFont Font;
		}

		#region Methods

		/// <summary>
		/// Returns the system font substituted for <paramref name="font"/>.
		/// </summary>
		/// <param name="font">The PDF font.</param>
		/// <returns>The substitute, or null when no suitable system font is installed.</returns>
		public static SubstituteFont Get(PdfFont font)
		{
			if (font == null)
				return null;

			var holder = _substitutes.GetValue(font, f =>
			{
				try
				{
					return new Holder { Font = Find(f) };
				}
				catch (Exception)
				{
					return new Holder();
				}
			});

			return holder.Font;
		}

		/// <summary>
		/// Returns a system font with the CJK characters of a character collection.
		/// </summary>
		/// <param name="ordering">The CIDSystemInfo ordering (Japan1, GB1, CNS1, Korea1), or null when unknown.</param>
		/// <returns>The font program, or null when no CJK font is installed.</returns>
		public static FontProgram GetCjkProgram(string ordering)
		{
			string[] families;
			switch (ordering)
			{
				case "GB1":
					families = ChineseSimplifiedFamilies;
					break;

				case "CNS1":
					families = ChineseTraditionalFamilies;
					break;

				case "Korea1":
					families = KoreanFamilies;
					break;

				case "Japan1":
					families = JapanFamilies;
					break;

				default:
					// an unknown collection: the Japanese fonts cover kana and most ideographs.
					ordering = "Japan1";
					families = JapanFamilies;
					break;
			}

			lock (_cjkPrograms)
			{
				if (_cjkPrograms.TryGetValue(ordering, out var cached))
					return cached;
			}

			FontProgram program = null;
			try
			{
				foreach (var family in families)
				{
					program = Load(family, false, false);
					if (program != null)
						break;
				}
			}
			catch (Exception)
			{
				program = null;
			}

			lock (_cjkPrograms)
				_cjkPrograms[ordering] = program;

			return program;
		}
		#endregion

		#region Implementation

		// finds the substitute of a font: its own family when installed, then a metric-compatible family of its class.
		private static SubstituteFont Find(PdfFont font)
		{
			var name = font.BaseFont ?? string.Empty;
			var bold = font.IsBold;
			var italic = font.IsItalic;

			// the family itself, when installed.
			var family = Normalize(PdfFont.GetFamilyName(name));
			if (family.Length > 0)
			{
				var exact = Load(family, bold, italic);
				if (exact != null)
					return new SubstituteFont(font, exact, IsSymbolName(family));
			}

			string[] families;
			if (IsSymbolName(Normalize(name)))
				families = SymbolFamilies;
			else if (name.IndexOf("Dingbat", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Wingding", StringComparison.OrdinalIgnoreCase) >= 0)
				families = DingbatFamilies;
			else if (font.IsFixedPitch)
				families = MonoFamilies;
			else if (IsSerif(font, name))
				families = SerifFamilies;
			else
				families = SansFamilies;

			foreach (var candidate in families)
			{
				var program = Load(candidate, bold, italic);
				if (program != null)
					return new SubstituteFont(font, program, families == SymbolFamilies);
			}

			// the symbol font is missing: draw the Unicode text with a regular font.
			if (families == SymbolFamilies)
			{
				foreach (var candidate in SansFamilies)
				{
					var program = Load(candidate, bold, italic);
					if (program != null)
						return new SubstituteFont(font, program, false);
				}
			}

			return null;
		}

		// true when the normalized name is the Symbol font.
		private static bool IsSymbolName(string normalized) => normalized == "symbol" || normalized.StartsWith("symbolmt", StringComparison.Ordinal);

		// true when the font is serif, from its flags or from well-known words in its name.
		private static bool IsSerif(PdfFont font, string name)
		{
			if (font.IsSerif)
				return true;

			var lower = name.ToLowerInvariant();
			return lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("roman")
				|| lower.Contains("georgia") || lower.Contains("garamond") || lower.Contains("cambria") || lower.Contains("palatino")
				|| lower.Contains("bookman") || lower.Contains("century") || lower.Contains("minion") || lower.StartsWith("cm", StringComparison.Ordinal);
		}

		// loads the best style of a family: the exact style, then the regular one.
		private static FontProgram Load(string family, bool bold, bool italic)
		{
			var files = GetFiles();
			if (files.Count == 0)
				return null;

			foreach (var suffix in GetStyleSuffixes(bold, italic))
			{
				if (files.TryGetValue(family + suffix, out var path))
				{
					var program = LoadFile(path);
					if (program != null)
						return program;
				}
			}

			return null;
		}

		// file name suffixes of a style, in Windows ("arialbd") and Linux/macOS ("LiberationSans-Bold") conventions.
		private static IEnumerable<string> GetStyleSuffixes(bool bold, bool italic)
		{
			if (bold && italic)
			{
				yield return "bi";
				yield return "z";
				yield return "bolditalic";
				yield return "boldoblique";
				yield return "boldit";
			}

			if (bold)
			{
				yield return "bd";
				yield return "b";
				yield return "bold";
				yield return "boldmt";
			}

			if (italic)
			{
				yield return "i";
				yield return "italic";
				yield return "oblique";
				yield return "it";
			}

			yield return string.Empty;
			yield return "regular";
			yield return "mt";
			yield return "roman";
		}

		// loads and caches a font file; returns null when the file can't be loaded.
		private static FontProgram LoadFile(string path)
		{
			lock (_programs)
			{
				if (_programs.TryGetValue(path, out var cached))
					return cached;
			}

			FontProgram program = null;
			try
			{
				var info = new FileInfo(path);

				// font files larger than 64 MB are not worth loading for a substitution.
				if (info.Exists && info.Length < 64 * 1024 * 1024)
					program = FontProgram.TryLoad(File.ReadAllBytes(path), "FontFile2");
			}
			catch (Exception)
			{
				program = null;
			}

			lock (_programs)
				_programs[path] = program;

			return program;
		}

		// the index of the font files of the system, scanned once.
		private static Dictionary<string, string> GetFiles()
		{
			lock (_sync)
			{
				if (_files == null)
				{
					var files = new Dictionary<string, string>(StringComparer.Ordinal);
					try
					{
						foreach (var folder in GetFontFolders())
							Scan(folder, files, 0);
					}
					catch (Exception)
					{
						// keep what was found.
					}

					_files = files;
				}

				return _files;
			}
		}

		// the font folders of Windows, Linux and macOS, including the per-user ones.
		private static IEnumerable<string> GetFontFolders()
		{
			var folders = new List<string>();
			try
			{
				var windows = Environment.GetEnvironmentVariable("WINDIR");
				if (!string.IsNullOrEmpty(windows))
					folders.Add(Path.Combine(windows, "Fonts"));

				var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
				if (!string.IsNullOrEmpty(local))
					folders.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));

				var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				folders.Add("/usr/share/fonts");
				folders.Add("/usr/local/share/fonts");
				if (!string.IsNullOrEmpty(home))
				{
					folders.Add(Path.Combine(home, ".fonts"));
					folders.Add(Path.Combine(home, ".local", "share", "fonts"));
					folders.Add(Path.Combine(home, "Library", "Fonts"));
				}

				folders.Add("/System/Library/Fonts");
				folders.Add("/System/Library/Fonts/Supplemental");
				folders.Add("/Library/Fonts");
			}
			catch (Exception)
			{
				// environment access may be restricted.
			}

			return folders;
		}

		// adds the font files of a folder and its subfolders to the index, within the scan bounds.
		private static void Scan(string folder, Dictionary<string, string> files, int depth)
		{
			if (depth > MaxDepth || files.Count >= MaxFiles || !Directory.Exists(folder))
				return;

			try
			{
				foreach (var file in Directory.EnumerateFiles(folder))
				{
					var extension = Path.GetExtension(file).ToLowerInvariant();
					if (extension != ".ttf" && extension != ".otf" && extension != ".ttc")
						continue;

					var key = Normalize(Path.GetFileNameWithoutExtension(file));
					if (key.Length > 0 && !files.ContainsKey(key))
						files[key] = file;

					if (files.Count >= MaxFiles)
						return;
				}

				foreach (var directory in Directory.EnumerateDirectories(folder))
					Scan(directory, files, depth + 1);
			}
			catch (Exception)
			{
				// unreadable folders are skipped.
			}
		}

		// lowercase letters and digits only: "Liberation Sans-Bold" -> "liberationsansbold".
		private static string Normalize(string name)
		{
			if (string.IsNullOrEmpty(name))
				return string.Empty;

			var chars = new char[name.Length];
			var count = 0;
			foreach (var c in name)
			{
				if (c < 128 && char.IsLetterOrDigit(c))
					chars[count++] = char.ToLowerInvariant(c);
			}

			return new string(chars, 0, count);
		}

		#endregion
	}
}
