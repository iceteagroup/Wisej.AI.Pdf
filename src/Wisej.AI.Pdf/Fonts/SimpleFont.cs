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

// Simple fonts: Type1, MMType1, TrueType and Type3 (ISO 32000-2, 9.6).

using System;
using Wisej.AI.Pdf.Fonts.Programs;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// A font with single-byte character codes and an encoding that maps codes to glyph names.
	/// </summary>
	internal class SimpleFont : PdfFont
	{
		#region Fields

		// glyph name for each code after applying the base encoding and the differences; null entries are unmapped.
		private readonly string[] _glyphNames = new string[256];

		// widths from /Widths, indexed by code; NaN when not specified.
		private readonly double[] _widths = new double[256];

		// the standard 14 font metrics, for non-embedded standard fonts.
		private readonly Standard14Font _standard14;

		// /MissingWidth from the font descriptor, in glyph space (1/1000 em for non-Type 3 fonts); 0 when missing.
		private readonly double _missingWidth;

		// true when the font dictionary has an /Encoding entry (affects the TrueType glyph lookup of ISO 32000-2, 9.6.5.4).
		private readonly bool _hasEncodingEntry;

		// true for ZapfDingbats and its variants, whose glyph names (a1, a2, ...) use the ITC Zapf Dingbats glyph list.
		private readonly bool _isDingbats;

		// the cached result of GetSpaceWidth; -1 until computed.
		private double _spaceWidth = -1;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new instance of <see cref="SimpleFont"/>.
		/// </summary>
		/// <param name="dictionary">The font dictionary.</param>
		public SimpleFont(PdfDictionary dictionary) : base(dictionary, dictionary.GetDictionary(PdfNames.FontDescriptor))
		{
			_standard14 = Standard14Fonts.Get(this.BaseFont);
			_missingWidth = this.Descriptor?.GetNumber(PdfNames.MissingWidth) ?? 0;
			_isDingbats = this.BaseFont.IndexOf("Dingbats", StringComparison.OrdinalIgnoreCase) >= 0;
			_hasEncodingEntry = dictionary.Has(PdfNames.Encoding);

			LoadWidths();
			LoadEncoding();

			if (_standard14 != null && this.Descriptor == null)
				InitializeFromStandard14();
			else
				InitializeVerticalMetrics(750, -250);
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns true when the font is one of the standard 14 fonts and is not embedded.
		/// </summary>
		public bool IsStandard14 => _standard14 != null && this.Program == null;

		#endregion

		#region Methods

		/// <summary>
		/// Returns the glyph name mapped to <paramref name="code"/> by the encoding.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <returns>The glyph name, or null.</returns>
		public string GetGlyphName(int code) => code >= 0 && code < 256 ? _glyphNames[code] : null;

		/// <inheritdoc/>
		public override double GetWidth(int code)
		{
			if (code < 0 || code > 255)
				return _missingWidth;

			var width = _widths[code];
			if (!double.IsNaN(width))
				return width;

			// non-embedded standard fonts: the widths come from the AFM metrics.
			var name = _glyphNames[code];
			if (_standard14 != null && name != null)
			{
				var standardWidth = _standard14.GetWidth(name);
				if (standardWidth >= 0)
					return standardWidth;
			}

			// the embedded program knows the advance of each glyph.
			var program = this.Program;
			if (program != null)
			{
				var glyph = GetGlyphId(code);
				if (glyph > 0)
				{
					var matrix = program.FontMatrix;
					var scale = matrix != null && matrix.Length > 0 ? matrix[0] * 1000 : 1;
					var advance = program.GetAdvanceWidth(glyph) * scale;
					if (advance > 0)
						return advance;
				}
			}

			return _missingWidth > 0 ? _missingWidth : GetDefaultWidth();
		}

		/// <inheritdoc/>
		public override int GetGlyphId(int code)
		{
			var program = this.Program;
			if (program == null || code < 0 || code > 255)
				return -1;

			if (program.Kind == FontProgramKind.TrueType || program.Kind == FontProgramKind.OpenTypeCff && this.Subtype == "TrueType")
				return GetTrueTypeGlyphId(program, code);

			// Type1 and CFF programs are addressed by glyph name.
			var name = _glyphNames[code];
			if (name != null)
			{
				var glyph = program.GetGlyphId(name);
				if (glyph >= 0)
					return glyph;
			}

			// fall back on the built-in encoding of the program.
			var builtIn = program.BuiltInEncoding;
			if (builtIn != null && code < builtIn.Length && builtIn[code] != null)
				return program.GetGlyphId(builtIn[code]);

			return -1;
		}

		/// <inheritdoc/>
		public override double GetSpaceWidth()
		{
			if (_spaceWidth >= 0)
				return _spaceWidth;

			// find the code of the space glyph.
			double width = 0;
			for (int code = 0; code < 256; code++)
			{
				if (_glyphNames[code] == "space")
				{
					width = GetWidth(code);
					break;
				}
			}

			// no space glyph (common in subsets): estimate from the average glyph width.
			if (width <= 0)
				width = GetAverageWidth() * 0.45;

			// a quarter of an em, expressed in glyph space (which differs for Type 3 fonts).
			if (width <= 0)
				width = 0.25 / Math.Max(1e-9, Math.Abs(this.FontMatrix.A));

			_spaceWidth = width;
			return width;
		}

		#endregion

		#region Implementation

		/// <inheritdoc/>
		protected override string MapToUnicode(int code)
		{
			// 1. the ToUnicode map is authoritative when present.
			var text = TryToUnicode(code);
			if (text != null)
				return text;

			if (code < 0 || code > 255)
				return null;

			// 2. the glyph name from the encoding.
			var name = _glyphNames[code];
			if (name != null)
			{
				text = GlyphList.ToUnicode(name, _isDingbats);
				if (text != null)
					return text;

				text = MapNonStandardGlyphName(name);
				if (text != null)
					return text;
			}

			// 3. the embedded font program.
			var program = this.Program;
			if (program != null)
			{
				text = MapFromProgram(program, code);
				if (text != null)
					return text;
			}

			// 4. last resort: printable ASCII codes are very likely to be ASCII.
			if (code >= 32 && code < 127)
				return ((char)code).ToString();

			return null;
		}

		// maps codes through the embedded program: glyph names of Type1/CFF, cmap and post of TrueType.
		private string MapFromProgram(FontProgram program, int code)
		{
			var glyph = GetGlyphId(code);
			if (glyph <= 0)
				return null;

			var text = program.GetUnicodeForGlyph(glyph);
			if (!string.IsNullOrEmpty(text) && !IsPrivateUse(text))
				return text;

			var glyphName = program.GetGlyphName(glyph);
			if (glyphName != null)
			{
				text = GlyphList.ToUnicode(glyphName, _isDingbats) ?? MapNonStandardGlyphName(glyphName);
				if (text != null)
					return text;
			}

			return null;
		}

		/// <summary>
		/// Maps the glyph id of a TrueType font following the rules of ISO 32000-2, 9.6.5.4.
		/// </summary>
		/// <param name="program">The TrueType program.</param>
		/// <param name="code">The character code.</param>
		/// <returns>The glyph id, or -1.</returns>
		private int GetTrueTypeGlyphId(FontProgram program, int code)
		{
			int glyph;

			// nonsymbolic fonts with a (3,1) cmap: map the glyph name to Unicode, then through the cmap.
			if (!this.IsSymbolic || _hasEncodingEntry)
			{
				var name = _glyphNames[code];
				if (name != null)
				{
					var unicode = GlyphList.ToUnicode(name);
					if (unicode != null && unicode.Length == 1 && program.TryGetCmapGlyph(3, 1, unicode[0], out glyph) && glyph > 0)
						return glyph;

					// a (1,0) cmap is indexed by the Mac Roman code of the glyph name.
					var macCode = Array.IndexOf(Encodings.MacRoman, name);
					if (macCode >= 0 && program.TryGetCmapGlyph(1, 0, macCode, out glyph) && glyph > 0)
						return glyph;

					// or the name is in the post table.
					glyph = program.GetGlyphId(name);
					if (glyph > 0)
						return glyph;
				}
			}

			// symbolic fonts: the (3,0) cmap maps codes in the F000-F0FF range.
			if (program.TryGetCmapGlyph(3, 0, code, out glyph) && glyph > 0)
				return glyph;

			if (program.TryGetCmapGlyph(3, 0, 0xF000 + code, out glyph) && glyph > 0)
				return glyph;

			if (program.TryGetCmapGlyph(1, 0, code, out glyph) && glyph > 0)
				return glyph;

			if (program.TryGetCmapGlyph(3, 1, code, out glyph) && glyph > 0)
				return glyph;

			// no cmap at all: codes are glyph ids (common in subsets made by old drivers).
			if (program.CmapSubtables.Count == 0 && code < program.GlyphCount)
				return code;

			return -1;
		}

		/// <summary>
		/// Recognizes non-standard glyph names produced by common tools.
		/// </summary>
		/// <param name="name">The glyph name.</param>
		/// <returns>The text, or null.</returns>
		private static string MapNonStandardGlyphName(string name)
		{
			// "Gxx" / "gxx" with 2 hexadecimal digits: the character code in hexadecimal (Windows drivers).
			if (name.Length == 3 && (name[0] == 'G' || name[0] == 'g') && GlyphList.TryParseHex(name, 1, 2, out var hex) && hex >= 32)
				return ((char)hex).ToString();

			// "cdd", "Cdd", "add" with a decimal number: the character code in decimal.
			if (name.Length >= 2 && name.Length <= 4 && (name[0] == 'c' || name[0] == 'C' || name[0] == 'a'))
			{
				var value = 0;
				for (int i = 1; i < name.Length; i++)
				{
					if (name[i] < '0' || name[i] > '9')
						return null;

					value = value * 10 + (name[i] - '0');
				}

				if (value >= 32 && value < 256)
					return ((char)value).ToString();
			}

			return null;
		}

		/// <summary>
		/// Tests whether a text is a single character of the Unicode private use area (U+E000 to U+F8FF).
		/// </summary>
		/// <param name="text">The text to test.</param>
		/// <returns>True for a single private use character.</returns>
		private static bool IsPrivateUse(string text) => text.Length == 1 && text[0] >= '' && text[0] <= '';

		/// <summary>
		/// Loads /Widths starting at /FirstChar; codes without an entry keep NaN.
		/// </summary>
		private void LoadWidths()
		{
			for (int i = 0; i < 256; i++)
				_widths[i] = double.NaN;

			var widths = this.Dictionary.GetArray(PdfNames.Widths);
			if (widths == null)
				return;

			var first = this.Dictionary.GetInt(PdfNames.FirstChar, 0);
			for (int i = 0; i < widths.Count; i++)
			{
				var code = first + i;
				if (code < 0 || code > 255)
					continue;

				var value = widths.Resolve(i);
				if (value is int || value is double)
					_widths[code] = PdfConvert.ToNumber(value);
			}
		}

		/// <summary>
		/// Builds the code to glyph name table from the base encoding and the /Differences array (ISO 32000-2, 9.6.5).
		/// </summary>
		private void LoadEncoding()
		{
			var encoding = this.Dictionary.Resolve(PdfNames.Encoding);
			string[] baseTable = null;
			PdfArray differences = null;

			if (encoding is PdfName name)
			{
				baseTable = Encodings.Get(name.Value);
			}
			else if (encoding is PdfDictionary dictionary)
			{
				var baseName = dictionary.GetNameValue(PdfNames.BaseEncoding);
				if (baseName != null)
					baseTable = Encodings.Get(baseName);

				differences = dictionary.GetArray(PdfNames.Differences);
			}

			if (baseTable == null)
				baseTable = GetDefaultEncoding();

			if (baseTable != null)
			{
				for (int i = 0; i < 256 && i < baseTable.Length; i++)
					_glyphNames[i] = string.IsNullOrEmpty(baseTable[i]) ? null : baseTable[i];
			}

			if (differences != null)
			{
				var code = 0;
				for (int i = 0; i < differences.Count; i++)
				{
					var item = differences.Resolve(i);
					if (item is int number)
					{
						code = number;
					}
					else if (item is PdfName glyphName)
					{
						if (code >= 0 && code < 256)
						{
							_glyphNames[code] = glyphName.Value;
						}

						code++;
					}
				}
			}
		}

		// the encoding used when the font dictionary doesn't specify a base encoding.
		private string[] GetDefaultEncoding()
		{
			// standard 14 symbolic fonts have their own built-in encodings.
			if (_standard14 != null && _standard14.BuiltInEncoding != null)
				return _standard14.BuiltInEncoding;

			// embedded Type1/CFF programs carry a built-in encoding.
			if (this.Subtype != "TrueType")
			{
				var program = this.Program;
				var builtIn = program?.BuiltInEncoding;
				if (builtIn != null)
					return builtIn;
			}

			// symbolic TrueType fonts are addressed directly through the cmap: no names.
			if (this.Subtype == "TrueType" && this.IsSymbolic)
				return null;

			return Encodings.Standard;
		}

		/// <summary>
		/// Takes the ascent and descent of a non-embedded standard 14 font from its AFM metrics, as fractions of the em.
		/// </summary>
		private void InitializeFromStandard14()
		{
			this.Ascent = Math.Max(0.5, _standard14.Ascent / 1000);
			this.Descent = Math.Min(-0.1, _standard14.Descent / 1000);
		}

		// default width for codes without metrics: the average of the declared widths.
		private double GetDefaultWidth()
		{
			var average = GetAverageWidth();
			return average > 0 ? average : 500;
		}

		/// <summary>
		/// Returns the average of the positive widths in /Widths.
		/// </summary>
		/// <returns>The average width in glyph space, or 0 when no width is declared.</returns>
		private double GetAverageWidth()
		{
			double sum = 0;
			var count = 0;
			for (int i = 0; i < 256; i++)
			{
				var width = _widths[i];
				if (!double.IsNaN(width) && width > 0)
				{
					sum += width;
					count++;
				}
			}

			return count > 0 ? sum / count : 0;
		}

		#endregion
	}
}
