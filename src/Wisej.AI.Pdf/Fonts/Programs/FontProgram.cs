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

// Common interface of the embedded font programs (ISO 32000-2, 9.9) and format detection.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// The format of a parsed font program.
	/// </summary>
	internal enum FontProgramKind
	{
		/// <summary>
		/// A TrueType font with <c>glyf</c> outlines.
		/// </summary>
		TrueType,

		/// <summary>
		/// An OpenType font with CFF outlines (an sfnt container with a <c>CFF </c> table).
		/// </summary>
		OpenTypeCff,

		/// <summary>
		/// A bare, name-keyed CFF font (<c>FontFile3</c> with subtype <c>Type1C</c>).
		/// </summary>
		Cff,

		/// <summary>
		/// A bare, CID-keyed CFF font (<c>FontFile3</c> with subtype <c>CIDFontType0C</c>).
		/// </summary>
		CffCid,

		/// <summary>
		/// A Type 1 font in PFA or PFB format (<c>FontFile</c>).
		/// </summary>
		Type1
	}

	/// <summary>
	/// A font program embedded in a PDF file: glyph names, character maps, advance widths and outlines.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Font programs are used to recover Unicode text when a PDF font has no usable <c>/ToUnicode</c>
	/// map (through glyph names, the TrueType <c>cmap</c> and the built-in encoding), and to render glyph
	/// outlines.
	/// </para>
	/// <para>
	/// All the tables are parsed by <see cref="TryLoad"/>. Outlines are decoded on demand and cached; every
	/// member is safe to call from multiple threads and never throws on malformed data.
	/// </para>
	/// </remarks>
	internal abstract class FontProgram
	{
		#region Fields

		// the default FontMatrix of Type 1 and CFF fonts: 1000 units per em.
		private static readonly double[] DefaultMatrix = { 0.001, 0, 0, 0.001, 0, 0 };

		// glyph name to glyph id, built on first use by GetGlyphIdFromNames.
		private Dictionary<string, int> _nameToGlyph;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes the common properties with their defaults.
		/// </summary>
		protected FontProgram()
		{
			this.FontMatrix = (double[])DefaultMatrix.Clone();
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the format of the font program.
		/// </summary>
		public abstract FontProgramKind Kind { get; }

		/// <summary>
		/// Returns the PostScript name of the font, or null when not available.
		/// </summary>
		public string FontName { get; protected set; }

		/// <summary>
		/// Returns the matrix [a b c d e f] that maps glyph space to text space:
		/// 1/unitsPerEm for TrueType, the font's <c>FontMatrix</c> for CFF and Type 1 (default 0.001).
		/// </summary>
		public double[] FontMatrix { get; protected set; }

		/// <summary>
		/// Returns the font bounding box in glyph space [xMin yMin xMax yMax], or null when not known.
		/// </summary>
		public double[] FontBBox { get; protected set; }

		/// <summary>
		/// Returns the number of glyphs.
		/// </summary>
		public int GlyphCount { get; protected set; }

		/// <summary>
		/// Returns true for CID-keyed fonts, whose charset maps glyph ids to CIDs.
		/// </summary>
		public bool IsCidKeyed { get; protected set; }

		/// <summary>
		/// Returns the weight class, 100 to 900, or 0 when not known.
		/// </summary>
		public int Weight { get; protected set; }

		/// <summary>
		/// Returns true when the font is known to be italic or oblique.
		/// </summary>
		public bool IsItalic { get; protected set; }

		/// <summary>
		/// Returns the built-in encoding of a simple font, character code to glyph name (256 entries, null
		/// for unused codes); null when the font has none or uses the Standard encoding.
		/// </summary>
		public virtual string[] BuiltInEncoding => null;

		/// <summary>
		/// Returns the (platform id, encoding id) pairs of the <c>cmap</c> subtables, empty when the font has none.
		/// </summary>
		public virtual IReadOnlyList<(int PlatformId, int EncodingId)> CmapSubtables => Array.Empty<(int, int)>();

		#endregion

		#region Methods

		/// <summary>
		/// Returns the name of a glyph.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The glyph name, or null when unknown.</returns>
		public abstract string GetGlyphName(int glyphId);

		/// <summary>
		/// Returns the glyph id of a named glyph.
		/// </summary>
		/// <param name="glyphName">The glyph name.</param>
		/// <returns>The glyph id, or -1 when not found.</returns>
		public abstract int GetGlyphId(string glyphName);

		/// <summary>
		/// Returns the advance width of a glyph.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The width in glyph space units; 0 when unknown.</returns>
		public abstract double GetAdvanceWidth(int glyphId);

		/// <summary>
		/// Returns the outline of a glyph, in glyph space. The result is cached.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The outline; <see cref="GlyphPath.Empty"/> when the glyph has no outline or does not exist.</returns>
		public abstract GlyphPath GetGlyphPath(int glyphId);

		/// <summary>
		/// Looks up a code in one specific <c>cmap</c> subtable.
		/// </summary>
		/// <param name="platformId">The platform id of the subtable.</param>
		/// <param name="encodingId">The encoding id of the subtable.</param>
		/// <param name="code">The character code.</param>
		/// <param name="glyphId">Receives the glyph id.</param>
		/// <returns>True when the subtable exists and maps the code to a glyph other than 0.</returns>
		public virtual bool TryGetCmapGlyph(int platformId, int encodingId, int code, out int glyphId)
		{
			glyphId = 0;
			return false;
		}

		/// <summary>
		/// Returns the Unicode text of a glyph, from the reverse of the best Unicode <c>cmap</c> subtable:
		/// (3,10), then (3,1), then (0,*).
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The text, or null when the glyph is not mapped.</returns>
		public virtual string GetUnicodeForGlyph(int glyphId) => null;

		/// <summary>
		/// Maps a CID to a glyph id. CID-keyed CFF fonts use their charset; other fonts use the identity.
		/// </summary>
		/// <param name="cid">The CID.</param>
		/// <returns>The glyph id, or 0 (.notdef) when the CID is not in the font.</returns>
		public virtual int CidToGlyphId(int cid) => cid;

		/// <summary>
		/// Detects the format of a font program and parses it.
		/// </summary>
		/// <param name="data">The decoded font file stream.</param>
		/// <param name="hint">
		/// The PDF key or subtype the data came from: <c>FontFile</c>, <c>FontFile2</c>, <c>FontFile3</c>,
		/// <c>Type1C</c>, <c>CIDFontType0C</c> or <c>OpenType</c>. It is used only when the content is ambiguous.
		/// </param>
		/// <returns>The font program, or null when the data is not a supported font. Never throws.</returns>
		public static FontProgram TryLoad(byte[] data, string hint)
		{
			if (data == null || data.Length < 4)
				return null;

			try
			{
				var font = LoadCore(data, hint ?? string.Empty);
				return font != null && font.GlyphCount > 0 ? font : null;
			}
			catch (Exception)
			{
				// the parsers are defensive, but a font program must never break the caller.
				return null;
			}
		}

		/// <summary>
		/// Returns a short description of the font, for debugging.
		/// </summary>
		/// <returns>The description.</returns>
		public override string ToString() => $"{this.Kind} {this.FontName} ({this.GlyphCount} glyphs)";

		#endregion

		#region Implementation

		// sniffs the content first; the hint only picks the parser when the signature is not recognized.
		private static FontProgram LoadCore(byte[] data, string hint)
		{
			var tag = BigEndian.U32(data, 0);
			switch (tag)
			{
				case 0x00010000:
				case 0x74727565: // 'true'
				case 0x4F54544F: // 'OTTO'
					return TrueTypeFont.TryParse(data, 0);

				case 0x74746366: // 'ttcf': a collection, use the first font.
					return TrueTypeFont.TryParse(data, BigEndian.S32(data, 12));
			}

			// PFB segment header.
			if (data[0] == 0x80 && data[1] == 0x01)
				return Type1Font.TryParse(data);

			// CFF header: major version 1, a header size of at least 4, offsets of 1-4 bytes.
			if (data[0] == 1 && data[2] >= 4 && data[3] >= 1 && data[3] <= 4)
			{
				var cff = CffFont.TryParse(data);
				if (cff != null)
					return cff;
			}

			if (data[0] == '%' && data[1] == '!')
				return Type1Font.TryParse(data);

			// unrecognized signature: fall back on the PDF key.
			if (Contains(hint, "FontFile2") || Contains(hint, "TrueType") || Contains(hint, "OpenType"))
				return TrueTypeFont.TryParse(data, 0) ?? (FontProgram)Type1Font.TryParse(data);

			if (Contains(hint, "FontFile3") || Contains(hint, "Type1C") || Contains(hint, "CIDFontType0C"))
				return CffFont.TryParse(data) ?? (FontProgram)Type1Font.TryParse(data);

			return Type1Font.TryParse(data) ?? (FontProgram)CffFont.TryParse(data);
		}

		// case-insensitive substring test.
		private static bool Contains(string text, string value)
		{
			return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		/// <summary>
		/// Maps a weight name, such as the CFF and Type 1 <c>/Weight</c> string, to a weight class.
		/// </summary>
		/// <param name="weight">The weight name, for example <c>Bold</c> or <c>SemiBold</c>.</param>
		/// <returns>The weight class 100-900, or 0 when the name is not recognized.</returns>
		protected static int ParseWeight(string weight)
		{
			if (string.IsNullOrEmpty(weight))
				return 0;

			var w = weight.Replace(" ", "").Replace("-", "").ToLowerInvariant();

			// test the compound names before their suffixes ("semibold" before "bold").
			if (w.Contains("thin") || w.Contains("hairline"))
				return 100;

			if (w.Contains("extralight") || w.Contains("ultralight"))
				return 200;

			if (w.Contains("light"))
				return 300;

			if (w.Contains("semibold") || w.Contains("demibold") || w == "demi")
				return 600;

			if (w.Contains("extrabold") || w.Contains("ultrabold") || w.Contains("heavy"))
				return 800;

			if (w.Contains("black") || w == "ultra" || w.Contains("extrablack"))
				return 900;

			if (w.Contains("bold"))
				return 700;

			if (w.Contains("medium"))
				return 500;

			if (w.Contains("regular") || w.Contains("normal") || w.Contains("book") || w.Contains("roman") || w == "plain")
				return 400;

			return 0;
		}

		/// <summary>
		/// Looks up a glyph id by name through <see cref="GetGlyphName"/>, building the reverse
		/// dictionary on first use. When a name occurs twice, the lowest glyph id wins.
		/// </summary>
		/// <param name="glyphName">The glyph name.</param>
		/// <returns>The glyph id, or -1 when not found.</returns>
		protected int GetGlyphIdFromNames(string glyphName)
		{
			if (glyphName == null)
				return -1;

			var map = LazyInitializer.EnsureInitialized(ref _nameToGlyph, () =>
			{
				var names = new Dictionary<string, int>(StringComparer.Ordinal);
				for (int gid = 0; gid < this.GlyphCount; gid++)
				{
					var name = GetGlyphName(gid);
					if (name != null && !names.ContainsKey(name))
						names.Add(name, gid);
				}

				return names;
			});

			return map.TryGetValue(glyphName, out var id) ? id : -1;
		}

		/// <summary>
		/// Returns true when <paramref name="matrix"/> is a usable, invertible affine matrix.
		/// </summary>
		/// <param name="matrix">The matrix to test.</param>
		/// <returns>True when the matrix has six finite values and a non-zero determinant.</returns>
		protected static bool IsValidMatrix(double[] matrix)
		{
			if (matrix == null || matrix.Length != 6)
				return false;

			foreach (var v in matrix)
			{
				if (double.IsNaN(v) || double.IsInfinity(v))
					return false;
			}

			return Math.Abs(matrix[0] * matrix[3] - matrix[1] * matrix[2]) > 1e-12;
		}

		#endregion
	}

	/// <summary>
	/// A lock-free, write-once cache indexed by glyph id.
	/// </summary>
	/// <typeparam name="T">The cached value type.</typeparam>
	internal sealed class GlyphCache<T> where T : class
	{
		// the cached values; null entries are not decoded yet.
		private readonly T[] _items;

		/// <summary>
		/// Initializes a cache for <paramref name="count"/> glyphs.
		/// </summary>
		/// <param name="count">The number of glyphs.</param>
		public GlyphCache(int count)
		{
			_items = new T[Math.Max(0, count)];
		}

		/// <summary>
		/// Returns the cached value of a glyph, creating it with <paramref name="factory"/> on first use.
		/// </summary>
		/// <param name="glyphId">The glyph id, which must be in range.</param>
		/// <param name="factory">Creates the value; it may run more than once under contention.</param>
		/// <returns>The value.</returns>
		public T Get(int glyphId, Func<int, T> factory)
		{
			var item = Volatile.Read(ref _items[glyphId]);
			if (item != null)
				return item;

			// two threads may decode the same glyph; the first stored result wins.
			item = factory(glyphId);
			return Interlocked.CompareExchange(ref _items[glyphId], item, null) ?? item;
		}
	}
}
