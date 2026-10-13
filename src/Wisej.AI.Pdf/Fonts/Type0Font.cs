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

// Composite (Type 0) fonts with CIDFontType0 or CIDFontType2 descendants
// (ISO 32000-2, 9.7).

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.Fonts.Programs;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// A composite font: multi-byte character codes are mapped by a CMap to CIDs,
	/// which select the glyphs of a CIDFont.
	/// </summary>
	internal sealed class Type0Font : PdfFont
	{
		#region Fields

		// the /Encoding CMap: character codes to CIDs.
		private readonly CMap _encoding;

		// the descendant CIDFont dictionary (the first entry of /DescendantFonts).
		private readonly PdfDictionary _cidFont;

		// the CIDSystemInfo registry of the CIDFont, "Adobe" when missing.
		private readonly string _registry;

		// the CIDSystemInfo ordering, possibly inferred from the CMap name when it is "Identity".
		private readonly string _ordering;

		// true for CIDFontType2 (TrueType outlines), false for CIDFontType0 (CFF outlines).
		private readonly bool _isCidFontType2;

		// unicode-based predefined CMaps (Uni*-UCS2/UTF16/UTF32): the code itself is the text.
		private readonly int _unicodeCodeUnits;

		// horizontal widths by CID.
		private readonly Dictionary<int, double> _widths = new Dictionary<int, double>();

		// /DW: the width of CIDs missing from /W, in glyph space (1/1000 em).
		private readonly double _defaultWidth;

		// vertical metrics by CID: (w1y, vx, vy).
		private readonly Dictionary<int, (double W1, double Vx, double Vy)> _verticalMetrics = new Dictionary<int, (double, double, double)>();

		// /DW2[0]: the default vertical position vy of the origin, 880 when missing.
		private readonly double _defaultVy;

		// /DW2[1]: the default vertical displacement w1y, -1000 when missing.
		private readonly double _defaultW1;

		// CID -> GID for CIDFontType2 fonts; null for Identity.
		private readonly int[] _cidToGid;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new instance of <see cref="Type0Font"/>.
		/// </summary>
		/// <param name="dictionary">The Type 0 font dictionary.</param>
		public Type0Font(PdfDictionary dictionary) : base(dictionary, GetDescendant(dictionary)?.GetDictionary(PdfNames.FontDescriptor))
		{
			_cidFont = GetDescendant(dictionary) ?? new PdfDictionary();
			_isCidFontType2 = _cidFont.GetNameValue(PdfNames.Subtype) == "CIDFontType2";

			var systemInfo = _cidFont.GetDictionary(PdfNames.CIDSystemInfo);
			_registry = systemInfo?.GetText(PdfNames.Registry) ?? "Adobe";
			_ordering = systemInfo?.GetText(PdfNames.Ordering) ?? "Identity";

			_encoding = LoadEncoding(dictionary, out var encodingName);
			_unicodeCodeUnits = GetUnicodeCodeUnits(encodingName);

			// some files specify the ordering only in the CMap name, e.g. "UniGB-UCS2-H" with Adobe-Identity.
			if (_ordering == "Identity" && encodingName != null)
				_ordering = InferOrdering(encodingName) ?? _ordering;

			_defaultWidth = _cidFont.GetNumber(PdfNames.DW, 1000);
			LoadWidths(_cidFont.GetArray(PdfNames.W));

			var dw2 = _cidFont.GetArray(PdfNames.DW2)?.ToNumbers();
			_defaultVy = dw2 != null && dw2.Length > 0 ? dw2[0] : 880;
			_defaultW1 = dw2 != null && dw2.Length > 1 ? dw2[1] : -1000;
			LoadVerticalMetrics(_cidFont.GetArray(PdfNames.W2));

			_cidToGid = LoadCidToGidMap();

			InitializeVerticalMetrics(880, -120);
		}

		#endregion

		#region Properties

		/// <inheritdoc/>
		public override bool IsVertical => _encoding.WMode == 1;

		/// <summary>
		/// Returns the character collection ordering, for example "Japan1" or "Identity".
		/// </summary>
		public string Ordering => _ordering;

		#endregion

		#region Methods

		/// <inheritdoc/>
		public override int ReadCode(byte[] bytes, int offset, out int length)
		{
			var code = _encoding.ReadCode(bytes, offset, out length);
			if (length < 1)
				length = 1;

			return code;
		}

		/// <summary>
		/// Returns the CID of a character code.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <returns>The CID.</returns>
		public int GetCid(int code) => _encoding.IsIdentity ? code : _encoding.GetCid(code);

		/// <inheritdoc/>
		public override double GetWidth(int code)
		{
			var cid = GetCid(code);
			return _widths.TryGetValue(cid, out var width) ? width : _defaultWidth;
		}

		/// <inheritdoc/>
		public override void GetVerticalMetrics(int code, out double w1, out double vx, out double vy)
		{
			var cid = GetCid(code);
			if (_verticalMetrics.TryGetValue(cid, out var metrics))
			{
				w1 = metrics.W1;
				vx = metrics.Vx;
				vy = metrics.Vy;
				return;
			}

			w1 = _defaultW1;
			vx = GetWidth(code) / 2;
			vy = _defaultVy;
		}

		/// <inheritdoc/>
		public override int GetGlyphId(int code)
		{
			var cid = GetCid(code);
			if (_isCidFontType2)
			{
				if (_cidToGid != null)
					return cid < _cidToGid.Length ? _cidToGid[cid] : 0;

				return cid;
			}

			// CIDFontType0: CID-keyed CFF maps CIDs through its charset.
			var program = this.Program;
			return program != null ? program.CidToGlyphId(cid) : cid;
		}

		/// <inheritdoc/>
		public override double GetSpaceWidth()
		{
			// CJK fonts rarely encode a space: half the default width is a good estimate of the word gap.
			var spaceWidth = 0.0;
			foreach (var code in new[] { 32, 3, 1 })
			{
				var text = GetUnicode(code);
				if (text == " ")
				{
					spaceWidth = GetWidth(code);
					break;
				}
			}

			return spaceWidth > 0 ? spaceWidth : Math.Min(_defaultWidth, 1000) * 0.3;
		}

		#endregion

		#region Implementation

		/// <inheritdoc/>
		protected override string MapToUnicode(int code)
		{
			// 1. ToUnicode.
			var text = TryToUnicode(code);
			if (text != null)
				return text;

			// 2. Unicode-based CMaps: the code is the character.
			if (_unicodeCodeUnits > 0)
				return DecodeUnicodeCode(code);

			var cid = GetCid(code);

			// 3. predefined character collections.
			if (_registry == "Adobe" && _ordering != "Identity")
			{
				text = CidToUnicode.Map(_registry, _ordering, cid);
				if (text != null)
					return text;
			}

			// 4. the embedded font program: unicode cmap of TrueType, glyph names of non-CID CFF.
			var program = this.Program;
			if (program != null)
			{
				var glyph = GetGlyphId(code);
				if (glyph > 0)
				{
					text = program.GetUnicodeForGlyph(glyph);
					if (!string.IsNullOrEmpty(text) && !(text.Length == 1 && text[0] >= '' && text[0] <= ''))
						return text;

					var name = program.IsCidKeyed ? null : program.GetGlyphName(glyph);
					if (name != null)
					{
						text = GlyphList.ToUnicode(name);
						if (text != null)
							return text;
					}
				}
			}

			return null;
		}

		/// <summary>
		/// Decodes a character code of a Unicode-based predefined CMap (Uni*-UCS2/UTF16/UTF32) as text.
		/// </summary>
		/// <param name="code">The character code: a UTF-32 scalar, a UTF-16 code unit, or a surrogate pair packed high to low.</param>
		/// <returns>The text, or null for control characters, lone surrogates and invalid code points.</returns>
		private string DecodeUnicodeCode(int code)
		{
			if (_unicodeCodeUnits == 4)
				return code >= 0 && code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF) ? char.ConvertFromUtf32(code) : null;

			// UCS-2/UTF-16: a 4-byte code is a surrogate pair.
			if (code > 0xFFFF)
			{
				var high = (char)(code >> 16);
				var low = (char)(code & 0xFFFF);
				return char.IsSurrogatePair(high, low) ? new string(new[] { high, low }) : null;
			}

			return code >= 0x20 && (code < 0xD800 || code > 0xDFFF) ? ((char)code).ToString() : null;
		}

		/// <summary>
		/// Returns the descendant CIDFont dictionary of a Type 0 font.
		/// </summary>
		/// <param name="dictionary">The Type 0 font dictionary.</param>
		/// <returns>The first /DescendantFonts entry, or null when missing.</returns>
		private static PdfDictionary GetDescendant(PdfDictionary dictionary)
		{
			var descendants = dictionary.Resolve(PdfNames.DescendantFonts);
			if (descendants is PdfArray array)
				return array.GetDictionary(0);

			// a direct dictionary instead of an array is invalid but occurs.
			return descendants as PdfDictionary;
		}

		/// <summary>
		/// Loads the /Encoding CMap: a predefined CMap by name, or an embedded CMap stream (ISO 32000-2, 9.7.5).
		/// </summary>
		/// <param name="dictionary">The Type 0 font dictionary.</param>
		/// <param name="name">Receives the CMap name, or null when not known.</param>
		/// <returns>The CMap; an Identity CMap when the encoding is missing, unknown or broken.</returns>
		private static CMap LoadEncoding(PdfDictionary dictionary, out string name)
		{
			name = null;
			var encoding = dictionary.Resolve(PdfNames.Encoding);

			if (encoding is PdfName encodingName)
			{
				name = encodingName.Value;
				return CMap.GetPredefined(name) ?? CMap.CreateIdentity(name.EndsWith("-V", StringComparison.Ordinal) ? 1 : 0);
			}

			if (encoding is PdfStream stream)
			{
				try
				{
					// /UseCMap can name a predefined CMap or reference another embedded one.
					var useCMap = stream.Resolve(PdfNames.UseCMap);
					var parent = useCMap is PdfName useName ? CMap.GetPredefined(useName.Value)
						: useCMap is PdfStream useStream ? CMap.Parse(useStream.GetBytes()) : null;
					var cmap = parent != null ? CMap.Parse(stream.GetBytes(), parent) : CMap.Parse(stream.GetBytes());
					if (cmap != null)
					{
						name = stream.GetNameValue("CMapName") ?? cmap.Name;
						return cmap;
					}
				}
				catch (Exception)
				{
					// fall through to identity.
				}
			}

			return CMap.CreateIdentity(0);
		}

		// returns 2 for UCS-2/UTF-16 CMaps, 4 for UTF-32 CMaps, 0 otherwise.
		private static int GetUnicodeCodeUnits(string name)
		{
			if (name == null || !name.StartsWith("Uni", StringComparison.Ordinal))
				return 0;

			if (name.IndexOf("UTF32", StringComparison.Ordinal) > 0)
				return 4;

			if (name.IndexOf("UCS2", StringComparison.Ordinal) > 0 || name.IndexOf("UTF16", StringComparison.Ordinal) > 0)
				return 2;

			return 0;
		}

		/// <summary>
		/// Infers the Adobe character collection ordering from the name of a predefined CMap.
		/// </summary>
		/// <param name="cmapName">The CMap name, for example "UniJIS-UCS2-H".</param>
		/// <returns>"Japan1", "GB1", "CNS1" or "Korea1"; null when the name gives no hint.</returns>
		private static string InferOrdering(string cmapName)
		{
			if (cmapName.IndexOf("JIS", StringComparison.Ordinal) >= 0 || cmapName.StartsWith("90", StringComparison.Ordinal) || cmapName.StartsWith("83pv", StringComparison.Ordinal))
				return "Japan1";

			if (cmapName.IndexOf("GB", StringComparison.Ordinal) >= 0)
				return "GB1";

			if (cmapName.IndexOf("CNS", StringComparison.Ordinal) >= 0 || cmapName.IndexOf("B5", StringComparison.Ordinal) >= 0 || cmapName.IndexOf("ETen", StringComparison.Ordinal) >= 0 || cmapName.IndexOf("HKscs", StringComparison.Ordinal) >= 0)
				return "CNS1";

			if (cmapName.IndexOf("KSC", StringComparison.Ordinal) >= 0 || cmapName.IndexOf("UHC", StringComparison.Ordinal) >= 0 || cmapName.IndexOf("Johab", StringComparison.Ordinal) >= 0)
				return "Korea1";

			return null;
		}

		// /W: [c [w1 w2 ...]] or [cfirst clast w].
		private void LoadWidths(PdfArray w)
		{
			if (w == null)
				return;

			for (int i = 0; i < w.Count;)
			{
				var first = w.Resolve(i);
				if (!(first is int start))
				{
					i++;
					continue;
				}

				var second = w.Resolve(i + 1);
				if (second is PdfArray list)
				{
					for (int j = 0; j < list.Count; j++)
						_widths[start + j] = list.GetNumber(j);

					i += 2;
				}
				else if (second is int end && i + 2 < w.Count)
				{
					var width = w.GetNumber(i + 2);

					// guard against huge ranges in broken files.
					end = Math.Min(end, start + 65535);
					for (int cid = start; cid <= end; cid++)
						_widths[cid] = width;

					i += 3;
				}
				else
				{
					i++;
				}
			}
		}

		// /W2: [c [w1y vx vy ...]] or [cfirst clast w1y vx vy].
		private void LoadVerticalMetrics(PdfArray w2)
		{
			if (w2 == null)
				return;

			for (int i = 0; i < w2.Count;)
			{
				if (!(w2.Resolve(i) is int start))
				{
					i++;
					continue;
				}

				var second = w2.Resolve(i + 1);
				if (second is PdfArray list)
				{
					for (int j = 0; j + 2 < list.Count; j += 3)
						_verticalMetrics[start + j / 3] = (list.GetNumber(j), list.GetNumber(j + 1), list.GetNumber(j + 2));

					i += 2;
				}
				else if (second is int end && i + 4 < w2.Count)
				{
					var metrics = (w2.GetNumber(i + 2), w2.GetNumber(i + 3), w2.GetNumber(i + 4));
					end = Math.Min(end, start + 65535);
					for (int cid = start; cid <= end; cid++)
						_verticalMetrics[cid] = metrics;

					i += 5;
				}
				else
				{
					i++;
				}
			}
		}

		/// <summary>
		/// Loads the /CIDToGIDMap stream of a CIDFontType2 font: one big-endian 16-bit glyph id per CID.
		/// </summary>
		/// <returns>The CID to glyph id map; null for CIDFontType0, for /Identity or when the entry is missing.</returns>
		private int[] LoadCidToGidMap()
		{
			if (!_isCidFontType2)
				return null;

			var stream = _cidFont.GetStream(PdfNames.CIDToGIDMap);
			if (stream == null)
				return null;

			var data = stream.GetBytes();
			var map = new int[data.Length / 2];
			for (int i = 0; i < map.Length; i++)
				map[i] = (data[i * 2] << 8) | data[i * 2 + 1];

			return map;
		}

		#endregion
	}
}
