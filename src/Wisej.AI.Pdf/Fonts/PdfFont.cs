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

// Base class of PDF font resources (ISO 32000-2, 9.5-9.10): decoding of
// character codes, Unicode mapping, glyph metrics and font style.

using System;
using System.Collections.Generic;
using System.Threading;
using Wisej.AI.Pdf.Fonts.Programs;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// Represents a font resource of a PDF document.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A font decodes the bytes of a text string into character codes, and maps each code to
	/// Unicode text and to a glyph width. Unicode mapping uses, in order of preference, the
	/// <c>/ToUnicode</c> map, the font encoding and glyph names, the embedded font program and
	/// finally predefined character collections.
	/// </para>
	/// <para>
	/// Instances are cached per document and are thread-safe.
	/// </para>
	/// </remarks>
	internal abstract class PdfFont
	{
		#region Flags

		/// <summary>FontDescriptor flag: all glyphs have the same width.</summary>
		protected const int FlagFixedPitch = 1 << 0;

		/// <summary>FontDescriptor flag: glyphs have serifs.</summary>
		protected const int FlagSerif = 1 << 1;

		/// <summary>FontDescriptor flag: the font uses characters outside the standard Latin set.</summary>
		protected const int FlagSymbolic = 1 << 2;

		/// <summary>FontDescriptor flag: glyphs resemble cursive handwriting.</summary>
		protected const int FlagScript = 1 << 3;

		/// <summary>FontDescriptor flag: the font uses the standard Latin character set.</summary>
		protected const int FlagNonsymbolic = 1 << 5;

		/// <summary>FontDescriptor flag: glyphs have dominant slanted vertical strokes.</summary>
		protected const int FlagItalic = 1 << 6;

		/// <summary>FontDescriptor flag: the font contains no lowercase letters.</summary>
		protected const int FlagAllCap = 1 << 16;

		/// <summary>FontDescriptor flag: lowercase letters are small capitals.</summary>
		protected const int FlagSmallCap = 1 << 17;

		/// <summary>FontDescriptor flag: bold glyphs are painted with extra pixels at small sizes.</summary>
		protected const int FlagForceBold = 1 << 18;

		#endregion

		#region Fields

		// the embedded font program, loaded on first access to Program; null when missing or unreadable.
		private FontProgram _program;

		// the load state of _program: 0 = not loaded, 1 = loading, 2 = loaded; guards the single load attempt.
		private int _programLoaded;

		// cache of code -> Unicode text; codes that cannot be mapped are cached as null.
		private readonly Dictionary<int, string> _unicodeCache = new Dictionary<int, string>();

		// guards _unicodeCache.
		private readonly object _sync = new object();

		// "/ToUnicode /Identity-H": the character codes are UCS-2 values.
		private bool _toUnicodeIsUcs2;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes the common properties from the font dictionary.
		/// </summary>
		/// <param name="dictionary">The font dictionary.</param>
		/// <param name="descriptor">The font descriptor (for Type0 fonts, the descriptor of the descendant font); can be null.</param>
		protected PdfFont(PdfDictionary dictionary, PdfDictionary descriptor)
		{
			this.Dictionary = dictionary;
			this.Descriptor = descriptor;
			this.Subtype = dictionary.GetNameValue(PdfNames.Subtype) ?? "Type1";

			var baseFont = dictionary.GetNameValue(PdfNames.BaseFont) ?? descriptor?.GetNameValue(PdfNames.FontName) ?? string.Empty;
			this.BaseFont = StripSubsetPrefix(baseFont);

			this.Flags = descriptor?.GetInt(PdfNames.Flags) ?? 0;
			this.ItalicAngle = descriptor?.GetNumber(PdfNames.ItalicAngle) ?? 0;
			this.FontWeight = descriptor?.GetNumber(PdfNames.FontWeight) ?? 0;

			LoadToUnicode();
		}

		#endregion

		#region Properties

		/// <summary>
		/// The font dictionary.
		/// </summary>
		public PdfDictionary Dictionary { get; }

		/// <summary>
		/// The font descriptor, or null for fonts without one (standard 14 fonts, Type 3 fonts).
		/// </summary>
		public PdfDictionary Descriptor { get; }

		/// <summary>
		/// The font subtype: Type1, MMType1, TrueType, Type3 or Type0.
		/// </summary>
		public string Subtype { get; }

		/// <summary>
		/// The PostScript name of the font without the subset prefix, for example "Helvetica-Bold".
		/// </summary>
		public string BaseFont { get; }

		/// <summary>
		/// The font family name derived from <see cref="BaseFont"/>, for example "Helvetica".
		/// </summary>
		public string FamilyName => GetFamilyName(this.BaseFont);

		/// <summary>
		/// The FontDescriptor flags.
		/// </summary>
		public int Flags { get; }

		/// <summary>
		/// The italic angle in degrees, counter-clockwise from vertical.
		/// </summary>
		public double ItalicAngle { get; }

		/// <summary>
		/// The weight declared in the font descriptor (100-900), or 0.
		/// </summary>
		public double FontWeight { get; }

		/// <summary>
		/// The ToUnicode map, or null.
		/// </summary>
		public CMap ToUnicode { get; private set; }

		/// <summary>
		/// True when the font writes vertically (WMode 1).
		/// </summary>
		public virtual bool IsVertical => false;

		/// <summary>
		/// True for Type 3 fonts, whose glyphs are content streams.
		/// </summary>
		public virtual bool IsType3 => false;

		/// <summary>
		/// The matrix that maps glyph space to text space: [0.001 0 0 0.001 0 0] except for Type 3 fonts.
		/// </summary>
		public virtual Matrix FontMatrix => new Matrix(0.001, 0, 0, 0.001, 0, 0);

		/// <summary>
		/// True when the font is bold.
		/// </summary>
		public bool IsBold
		{
			get
			{
				if (this.FontWeight >= 600 || (this.Flags & FlagForceBold) != 0)
					return true;

				var program = this.Program;
				if (program != null && program.Weight >= 600)
					return true;

				return NameIndicatesBold(this.BaseFont);
			}
		}

		/// <summary>
		/// True when the font is italic or oblique.
		/// </summary>
		public bool IsItalic
		{
			get
			{
				if ((this.Flags & FlagItalic) != 0 || Math.Abs(this.ItalicAngle) > 1)
					return true;

				var program = this.Program;
				if (program != null && program.IsItalic)
					return true;

				return NameIndicatesItalic(this.BaseFont);
			}
		}

		/// <summary>
		/// True when the font is monospaced.
		/// </summary>
		public bool IsFixedPitch
		{
			get
			{
				if ((this.Flags & FlagFixedPitch) != 0)
					return true;

				var name = this.BaseFont;
				return name.IndexOf("Courier", StringComparison.OrdinalIgnoreCase) >= 0
					|| name.IndexOf("Mono", StringComparison.OrdinalIgnoreCase) >= 0
					|| name.IndexOf("Consolas", StringComparison.OrdinalIgnoreCase) >= 0
					|| name.IndexOf("Typewriter", StringComparison.OrdinalIgnoreCase) >= 0
					|| name.StartsWith("CMTT", StringComparison.OrdinalIgnoreCase)
					|| name.StartsWith("SFTT", StringComparison.OrdinalIgnoreCase)
					|| name.StartsWith("Inconsolata", StringComparison.OrdinalIgnoreCase);
			}
		}

		/// <summary>
		/// True when the font is declared symbolic.
		/// </summary>
		public bool IsSymbolic => (this.Flags & FlagSymbolic) != 0 && (this.Flags & FlagNonsymbolic) == 0;

		/// <summary>
		/// True when the font is a serif font.
		/// </summary>
		public bool IsSerif => (this.Flags & FlagSerif) != 0;

		/// <summary>
		/// True when lowercase letters are rendered as small capitals.
		/// </summary>
		public bool IsSmallCaps => (this.Flags & FlagSmallCap) != 0;

		/// <summary>
		/// The embedded font program, or null when the font is not embedded or can't be parsed.
		/// </summary>
		public FontProgram Program
		{
			get
			{
				if (Interlocked.CompareExchange(ref _programLoaded, 1, 0) == 0)
				{
					try
					{
						_program = LoadProgram();
					}
					catch (Exception)
					{
						_program = null;
					}

					Volatile.Write(ref _programLoaded, 2);
				}
				else
				{
					// another thread is loading the program: wait for it.
					SpinWait.SpinUntil(() => Volatile.Read(ref _programLoaded) == 2);
				}

				return _program;
			}
		}

		/// <summary>
		/// The ascent of the font in text space units (fraction of the font size), always positive.
		/// </summary>
		public double Ascent { get; protected set; } = 0.75;

		/// <summary>
		/// The descent of the font in text space units (fraction of the font size), always negative or zero.
		/// </summary>
		public double Descent { get; protected set; } = -0.25;

		#endregion

		#region Methods

		/// <summary>
		/// Reads one character code from a text string.
		/// </summary>
		/// <param name="bytes">The bytes of the string.</param>
		/// <param name="offset">The offset of the code.</param>
		/// <param name="length">Receives the number of bytes consumed (at least 1).</param>
		/// <returns>The character code.</returns>
		public virtual int ReadCode(byte[] bytes, int offset, out int length)
		{
			length = 1;
			return bytes[offset];
		}

		/// <summary>
		/// Returns the Unicode text of a character code.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <returns>The text, or null when the code can't be mapped.</returns>
		public string GetUnicode(int code)
		{
			lock (_sync)
			{
				if (_unicodeCache.TryGetValue(code, out var cached))
					return cached;
			}

			var text = MapToUnicode(code);
			if (text != null)
				text = Normalize(text);

			lock (_sync)
				_unicodeCache[code] = text;

			return text;
		}

		/// <summary>
		/// Returns the horizontal advance of a character code in glyph space units (thousandths of the font size for non-Type 3 fonts).
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <returns>The width.</returns>
		public abstract double GetWidth(int code);

		/// <summary>
		/// Returns the vertical metrics for vertical writing: the vertical advance and the position vector, in glyph space units.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <param name="w1">Receives the vertical displacement (negative: downward).</param>
		/// <param name="vx">Receives the horizontal component of the position vector.</param>
		/// <param name="vy">Receives the vertical component of the position vector.</param>
		public virtual void GetVerticalMetrics(int code, out double w1, out double vx, out double vy)
		{
			w1 = -1000;
			vx = GetWidth(code) / 2;
			vy = 880;
		}

		/// <summary>
		/// Returns true when <paramref name="code"/> is a single-byte code 32, to which word spacing applies.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <param name="length">The number of bytes of the code.</param>
		/// <returns>True when word spacing applies.</returns>
		public bool IsWordSpace(int code, int length) => code == 32 && length == 1;

		/// <summary>
		/// Returns the glyph identifier of a character code in the embedded font program, for rendering.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <returns>The glyph id, or -1 when unknown.</returns>
		public virtual int GetGlyphId(int code) => -1;

		/// <summary>
		/// The width of a space character in glyph space units, used to infer word boundaries.
		/// </summary>
		/// <returns>The width of the space glyph, or an estimate when the font has none.</returns>
		public virtual double GetSpaceWidth() => 250;

		/// <inheritdoc/>
		public override string ToString() => $"{this.Subtype} {this.BaseFont}";

		#endregion

		#region Implementation

		/// <summary>
		/// Maps a character code to Unicode, without caching.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <returns>The text, or null.</returns>
		protected virtual string MapToUnicode(int code)
		{
			return TryToUnicode(code);
		}

		/// <summary>
		/// Looks up <paramref name="code"/> in the ToUnicode map, rejecting useless values.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <returns>The text, or null when not mapped or mapped to a meaningless value.</returns>
		protected string TryToUnicode(int code)
		{
			if (_toUnicodeIsUcs2 && code >= 0x20 && code <= 0xFFFF && (code < 0xD800 || code > 0xDFFF))
				return ((char)code).ToString();

			if (this.ToUnicode == null || !this.ToUnicode.TryGetUnicode(code, out var text) || string.IsNullOrEmpty(text))
				return null;

			// some producers map every glyph to NUL or to a control character: treat it as missing.
			if (text.Length == 1 && (text[0] < 0x20 && text[0] != '\t' && text[0] != '\n' && text[0] != '\r'))
				return null;

			return text;
		}

		/// <summary>
		/// Loads the embedded font program described by the font descriptor.
		/// </summary>
		/// <returns>The program, or null.</returns>
		protected virtual FontProgram LoadProgram()
		{
			var descriptor = this.Descriptor;
			if (descriptor == null)
				return null;

			var stream = descriptor.GetStream(PdfNames.FontFile2);
			var hint = "FontFile2";
			if (stream == null)
			{
				stream = descriptor.GetStream(PdfNames.FontFile3);
				hint = stream?.GetNameValue(PdfNames.Subtype) ?? "FontFile3";
			}

			if (stream == null)
			{
				stream = descriptor.GetStream(PdfNames.FontFile);
				hint = "FontFile";
			}

			return stream != null ? FontProgram.TryLoad(stream.GetBytes(), hint) : null;
		}

		/// <summary>
		/// Sets the ascent and descent from the font descriptor, the font bounding box or the given fallbacks.
		/// </summary>
		/// <param name="fallbackAscent">The ascent in glyph units when the descriptor has none.</param>
		/// <param name="fallbackDescent">The descent in glyph units when the descriptor has none.</param>
		protected void InitializeVerticalMetrics(double fallbackAscent, double fallbackDescent)
		{
			var ascent = this.Descriptor?.GetNumber(PdfNames.Ascent) ?? 0;
			var descent = this.Descriptor?.GetNumber(PdfNames.Descent) ?? 0;

			// the bounding box is a better source when the declared values are missing or absurd.
			var bbox = this.Descriptor?.GetArray(PdfNames.FontBBox)?.ToNumbers();
			if ((ascent <= 0 || ascent > 2500) && bbox != null && bbox.Length == 4 && bbox[3] > 0 && bbox[3] < 2500)
				ascent = bbox[3];

			if ((descent >= 0 || descent < -2500) && bbox != null && bbox.Length == 4 && bbox[1] < 0 && bbox[1] > -2500)
				descent = bbox[1];

			if (ascent <= 0 || ascent > 2500)
				ascent = fallbackAscent;

			if (descent >= 0 || descent < -2500)
				descent = fallbackDescent;

			// fonts with an extreme ascent+descent produce oversized glyph boxes; keep them within reason.
			var total = ascent - descent;
			if (total > 1600)
			{
				ascent = ascent * 1200 / total;
				descent = descent * 1200 / total;
			}

			this.Ascent = ascent / 1000;
			this.Descent = descent / 1000;
		}

		/// <summary>
		/// Loads the /ToUnicode CMap stream (ISO 32000-2, 9.10.3); <see cref="ToUnicode"/> stays null when it is missing or unreadable.
		/// </summary>
		private void LoadToUnicode()
		{
			var stream = this.Dictionary.GetStream(PdfNames.ToUnicode);
			if (stream != null)
			{
				try
				{
					this.ToUnicode = CMap.Parse(stream.GetBytes());
				}
				catch (Exception)
				{
					this.ToUnicode = null;
				}
			}
			else
			{
				// "/ToUnicode /Identity-H" is invalid but used by some producers to mean UCS-2 codes.
				var name = this.Dictionary.GetNameValue(PdfNames.ToUnicode);
				if (name == "Identity-H" || name == "Identity-V")
					_toUnicodeIsUcs2 = true;
			}
		}

		/// <summary>
		/// Normalizes mapped text: replaces presentation forms that hurt search and AI processing.
		/// </summary>
		/// <param name="text">The mapped text.</param>
		/// <returns>The normalized text.</returns>
		private static string Normalize(string text)
		{
			// unpaired surrogates (broken ToUnicode maps) are not valid text: they would break normalization and JSON.
			for (int i = 0; i < text.Length; i++)
			{
				var isPaired = char.IsHighSurrogate(text[i]) ? i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
					: char.IsLowSurrogate(text[i]) && i > 0 && char.IsHighSurrogate(text[i - 1]);
				if (char.IsSurrogate(text[i]) && !isPaired)
				{
					text = text.Substring(0, i) + "�" + text.Substring(i + 1);
				}
			}

			// Arabic presentation forms (positional glyph variants) become the base letters, which is what search and AI models expect.
			foreach (var c in text)
			{
				if ((c >= 'ﭐ' && c <= '﷿') || (c >= 'ﹰ' && c <= '﻾'))
					return text.Normalize(System.Text.NormalizationForm.FormKC);
			}

			if (text.Length == 1)
			{
				switch (text[0])
				{
					// typographic ligatures are expanded so words are searchable.
					case 'ﬀ': return "ff";
					case 'ﬁ': return "fi";
					case 'ﬂ': return "fl";
					case 'ﬃ': return "ffi";
					case 'ﬄ': return "ffl";
					case 'ﬅ': return "st";
					case 'ﬆ': return "st";

					// no-break and other fixed-width spaces become regular spaces.
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case ' ':
					case '　':
						return " ";

					// soft hyphen is rendered as a hyphen when it's actually drawn.
					case '­':
						return "-";
				}
			}

			return text;
		}

		/// <summary>
		/// Removes the 6-letter subset prefix from a font name: "ABCDEF+Helvetica" -> "Helvetica".
		/// </summary>
		/// <param name="name">The font name.</param>
		/// <returns>The name without prefix.</returns>
		public static string StripSubsetPrefix(string name)
		{
			if (name != null && name.Length > 7 && name[6] == '+')
			{
				for (int i = 0; i < 6; i++)
				{
					if (name[i] < 'A' || name[i] > 'Z')
						return name;
				}

				return name.Substring(7);
			}

			return name ?? string.Empty;
		}

		/// <summary>
		/// Returns the family part of a PostScript font name: "Arial-BoldMT" -> "Arial", "TimesNewRoman,Bold" -> "TimesNewRoman".
		/// </summary>
		/// <param name="name">The PostScript font name.</param>
		/// <returns>The family name.</returns>
		public static string GetFamilyName(string name)
		{
			if (string.IsNullOrEmpty(name))
				return string.Empty;

			var end = name.IndexOfAny(new[] { '-', ',' });
			var family = end > 0 ? name.Substring(0, end) : name;

			// "ArialMT" -> "Arial", "CourierNewPSMT" -> "CourierNew".
			if (family.EndsWith("PSMT", StringComparison.Ordinal))
				family = family.Substring(0, family.Length - 4);
			else if (family.EndsWith("MT", StringComparison.Ordinal) && family.Length > 2)
				family = family.Substring(0, family.Length - 2);

			return family;
		}

		/// <summary>
		/// Tests whether a PostScript font name denotes a bold weight, for fonts whose descriptor and program do not say.
		/// </summary>
		/// <param name="name">The font name without the subset prefix.</param>
		/// <returns>True when the name contains a bold style word or follows a known bold naming scheme (TeX CMBX, ",B" suffix).</returns>
		private static bool NameIndicatesBold(string name)
		{
			return ContainsStyle(name, "Bold") || (ContainsStyle(name, "Black") && name.IndexOf("Blackletter", StringComparison.OrdinalIgnoreCase) < 0) || ContainsStyle(name, "Heavy")
				|| ContainsStyle(name, "Semibold") || ContainsStyle(name, "SemiBold") || ContainsStyle(name, "Demi")
				|| ContainsStyle(name, "Extrabold") || (ContainsStyle(name, "Ultra") && name.IndexOf("UltraLight", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("UltraThin", StringComparison.OrdinalIgnoreCase) < 0)
				|| name.EndsWith("-B", StringComparison.Ordinal) || name.EndsWith(",B", StringComparison.Ordinal)
				|| name.StartsWith("CMBX", StringComparison.Ordinal) || name.StartsWith("CMB", StringComparison.Ordinal)
				|| name.StartsWith("SFBX", StringComparison.Ordinal);
		}

		/// <summary>
		/// Tests whether a PostScript font name denotes an italic or oblique style.
		/// </summary>
		/// <param name="name">The font name without the subset prefix.</param>
		/// <returns>True when the name contains an italic style word or follows a known italic naming scheme (TeX CMTI, "-It" suffix).</returns>
		private static bool NameIndicatesItalic(string name)
		{
			return ContainsStyle(name, "Italic") || ContainsStyle(name, "Oblique") || ContainsStyle(name, "Slanted")
				|| name.EndsWith("-It", StringComparison.Ordinal) || name.EndsWith("-I", StringComparison.Ordinal)
				|| name.EndsWith("-BI", StringComparison.Ordinal) || name.EndsWith("Ital", StringComparison.Ordinal)
				|| name.StartsWith("CMTI", StringComparison.Ordinal) || name.StartsWith("CMMI", StringComparison.Ordinal)
				|| name.StartsWith("CMSL", StringComparison.Ordinal) || name.StartsWith("SFTI", StringComparison.Ordinal);
		}

		// the style word must not be part of the family name: "Boldface" counts, "Ultravox" doesn't matter much.
		private static bool ContainsStyle(string name, string style)
		{
			var index = name.IndexOf(style, StringComparison.OrdinalIgnoreCase);
			if (index <= 0)
				return false;

			// the style comes after a separator or a lowercase letter (camel case).
			var previous = name[index - 1];
			return previous == '-' || previous == ',' || previous == ' ' || previous == '_' || char.IsLower(previous) || char.IsDigit(previous);
		}

		#endregion
	}
}
