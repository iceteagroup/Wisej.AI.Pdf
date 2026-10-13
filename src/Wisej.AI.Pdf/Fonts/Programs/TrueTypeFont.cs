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

// TrueType and OpenType (sfnt) font programs: head, hhea, hmtx, maxp, loca, glyf, cmap, post,
// name, OS/2 and CFF tables (OpenType spec 1.9; ISO 32000-2, 9.9 FontFile2 and FontFile3/OpenType).

using System;
using System.Collections.Generic;
using System.Text;

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// A TrueType font, or an OpenType font with CFF outlines.
	/// </summary>
	/// <remarks>
	/// PDF subsets frequently have broken or missing tables. Missing tables only disable the
	/// features that depend on them: without <c>loca</c> there are no outlines, without <c>cmap</c>
	/// no character maps, a short <c>hmtx</c> repeats the last advance width, and so on.
	/// </remarks>
	internal sealed class TrueTypeFont : FontProgram
	{
		#region Nested types

		/// <summary>
		/// The location of a table inside the font data.
		/// </summary>
		private struct TableRecord
		{
			/// <summary>
			/// The offset from the start of the data.
			/// </summary>
			public int Offset;

			/// <summary>
			/// The length in bytes, clamped to the available data.
			/// </summary>
			public int Length;
		}

		/// <summary>
		/// The points of a glyf outline before conversion to a path; composites accumulate into one instance.
		/// </summary>
		private sealed class Outline
		{
			/// <summary>
			/// The x coordinates.
			/// </summary>
			public float[] X = new float[64];

			/// <summary>
			/// The y coordinates.
			/// </summary>
			public float[] Y = new float[64];

			/// <summary>
			/// True for on-curve points.
			/// </summary>
			public bool[] OnCurve = new bool[64];

			/// <summary>
			/// The number of points.
			/// </summary>
			public int Count;

			/// <summary>
			/// The index of the last point of each contour.
			/// </summary>
			public List<int> ContourEnds = new List<int>();

			/// <summary>
			/// Appends a point.
			/// </summary>
			/// <param name="x">The x coordinate.</param>
			/// <param name="y">The y coordinate.</param>
			/// <param name="onCurve">True for an on-curve point.</param>
			public void Add(float x, float y, bool onCurve)
			{
				if (this.Count == this.X.Length)
				{
					var size = this.Count * 2;
					Array.Resize(ref this.X, size);
					Array.Resize(ref this.Y, size);
					Array.Resize(ref this.OnCurve, size);
				}

				this.X[this.Count] = x;
				this.Y[this.Count] = y;
				this.OnCurve[this.Count] = onCurve;
				this.Count++;
			}
		}

		#endregion

		#region Fields

		// composite glyph flags (OpenType spec, 'glyf' table).

		/// <summary>ARG_1_AND_2_ARE_WORDS: the component arguments are 16-bit instead of 8-bit.</summary>
		private const int ArgsAreWords = 0x0001;

		/// <summary>ARGS_ARE_XY_VALUES: the arguments are an x/y offset rather than point numbers to match.</summary>
		private const int ArgsAreXYValues = 0x0002;

		/// <summary>WE_HAVE_A_SCALE: a single F2Dot14 uniform scale follows.</summary>
		private const int WeHaveAScale = 0x0008;

		/// <summary>MORE_COMPONENTS: another component record follows this one.</summary>
		private const int MoreComponents = 0x0020;

		/// <summary>WE_HAVE_AN_X_AND_Y_SCALE: separate F2Dot14 x and y scales follow.</summary>
		private const int WeHaveAnXAndYScale = 0x0040;

		/// <summary>WE_HAVE_A_TWO_BY_TWO: a full 2x2 F2Dot14 transformation follows.</summary>
		private const int WeHaveATwoByTwo = 0x0080;

		/// <summary>SCALED_COMPONENT_OFFSET: the component offset is transformed by the component scale.</summary>
		private const int ScaledComponentOffset = 0x0800;

		// simple glyph flags (OpenType spec, 'glyf' table).

		/// <summary>ON_CURVE_POINT: the point is on the curve rather than a quadratic control point.</summary>
		private const int OnCurvePoint = 0x01;

		/// <summary>X_SHORT_VECTOR: the x delta is one unsigned byte, its sign given by <see cref="XIsSameOrPositive"/>.</summary>
		private const int XShortVector = 0x02;

		/// <summary>Y_SHORT_VECTOR: the y delta is one unsigned byte, its sign given by <see cref="YIsSameOrPositive"/>.</summary>
		private const int YShortVector = 0x04;

		/// <summary>REPEAT_FLAG: the next byte is the number of additional times this flag repeats.</summary>
		private const int RepeatFlag = 0x08;

		/// <summary>X_IS_SAME_OR_POSITIVE_X_SHORT_VECTOR: a positive short x delta, or an unchanged x when not short.</summary>
		private const int XIsSameOrPositive = 0x10;

		/// <summary>Y_IS_SAME_OR_POSITIVE_Y_SHORT_VECTOR: a positive short y delta, or an unchanged y when not short.</summary>
		private const int YIsSameOrPositive = 0x20;

		// nesting limit for composite glyphs, which also stops reference cycles.
		private const int MaxCompositeDepth = 16;

		// limit on the number of component glyphs loaded for one outline.
		private const int MaxComponents = 1024;

		// limit on the number of points of a single glyph.
		private const int MaxPoints = 0xFFFF;

		// the whole font file.
		private readonly byte[] _data;

		// the tables found in the directory, by tag.
		private readonly Dictionary<string, TableRecord> _tables = new Dictionary<string, TableRecord>(StringComparer.Ordinal);

		// the em size from 'head'.
		private int _unitsPerEm = 1000;

		// the 'hmtx' table.
		private TableRecord _hmtx;

		// numberOfHMetrics from 'hhea': the count of full (advance, lsb) pairs in 'hmtx'.
		private int _numberOfHMetrics;

		// glyph offsets from 'loca', numGlyphs + 1 entries relative to 'glyf'; null when outlines are unavailable.
		private int[] _loca;

		// the 'glyf' table.
		private TableRecord _glyf;

		// glyph names from 'post', or null.
		private string[] _postNames;

		// the cmap subtables, in table order.
		private List<TrueTypeCmap> _cmaps = new List<TrueTypeCmap>();

		// the (platform, encoding) ids of the cmap subtables, parallel to _cmaps.
		private (int PlatformId, int EncodingId)[] _cmapIds = Array.Empty<(int, int)>();

		// the CFF outlines of an OpenType CFF font, or null.
		private CffFont _cff;

		// scale from the hmtx units to the glyph space of the CFF outlines (1 when they agree).
		private double _hmtxScale = 1;

		// glyph id to Unicode text, built on first use.
		private string[] _unicode;

		// true once _unicode has been built (it may legitimately stay null).
		private bool _unicodeBuilt;

		// guards the lazy construction of _unicode.
		private readonly object _unicodeLock = new object();

		// the decoded glyf outlines.
		private GlyphCache<GlyphPath> _paths;

		// the cache factory, allocated once.
		private readonly Func<int, GlyphPath> _decode;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a font over its data; call <see cref="Parse"/> to read the tables.
		/// </summary>
		/// <param name="data">The font file.</param>
		private TrueTypeFont(byte[] data)
		{
			_data = data;
			_decode = DecodeGlyph;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns <see cref="FontProgramKind.OpenTypeCff"/> when the outlines are CFF, otherwise <see cref="FontProgramKind.TrueType"/>.
		/// </summary>
		public override FontProgramKind Kind => _cff != null ? FontProgramKind.OpenTypeCff : FontProgramKind.TrueType;

		/// <summary>
		/// Returns the em size from the <c>head</c> table.
		/// </summary>
		public int UnitsPerEm => _unitsPerEm;

		/// <summary>
		/// Returns the built-in encoding of the CFF table of an OpenType CFF font, otherwise null.
		/// </summary>
		public override string[] BuiltInEncoding => _cff?.BuiltInEncoding;

		/// <summary>
		/// Returns the (platform id, encoding id) pairs of the decoded <c>cmap</c> subtables.
		/// </summary>
		public override IReadOnlyList<(int PlatformId, int EncodingId)> CmapSubtables => _cmapIds;

		/// <summary>
		/// Returns true when the font has outlines (a usable <c>glyf</c>/<c>loca</c> pair or a CFF table).
		/// </summary>
		public bool HasOutlines => _cff != null || _loca != null;

		#endregion

		#region Methods

		/// <summary>
		/// Parses an sfnt font.
		/// </summary>
		/// <param name="data">The font file.</param>
		/// <param name="offset">The offset of the table directory: 0, or the first font of a collection.</param>
		/// <returns>The font, or null when the data is not a usable sfnt.</returns>
		public static TrueTypeFont TryParse(byte[] data, int offset)
		{
			try
			{
				var font = new TrueTypeFont(data);
				return font.Parse(offset) ? font : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>
		/// Returns the name of a glyph from the <c>post</c> table, or from the CFF charset of an OpenType CFF font.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The glyph name, or null.</returns>
		public override string GetGlyphName(int glyphId)
		{
			if (_postNames != null && glyphId >= 0 && glyphId < _postNames.Length && _postNames[glyphId] != null)
				return _postNames[glyphId];

			return _cff?.GetGlyphName(glyphId);
		}

		/// <summary>
		/// Returns the id of a named glyph.
		/// </summary>
		/// <param name="glyphName">The glyph name.</param>
		/// <returns>The glyph id, or -1.</returns>
		public override int GetGlyphId(string glyphName) => GetGlyphIdFromNames(glyphName);

		/// <summary>
		/// Returns the advance width from <c>hmtx</c>, in glyph space units.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The advance width, or 0.</returns>
		public override double GetAdvanceWidth(int glyphId)
		{
			if (glyphId < 0 || glyphId >= this.GlyphCount)
				return 0;

			if (_numberOfHMetrics > 0)
			{
				// glyphs past numberOfHMetrics share the last advance width.
				var index = Math.Min(glyphId, _numberOfHMetrics - 1);
				return BigEndian.U16(_data, _hmtx.Offset + index * 4) * _hmtxScale;
			}

			return _cff != null ? _cff.GetAdvanceWidth(glyphId) : 0;
		}

		/// <summary>
		/// Returns the outline of a glyph, from <c>glyf</c> or from the CFF table.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The outline, or <see cref="GlyphPath.Empty"/>.</returns>
		public override GlyphPath GetGlyphPath(int glyphId)
		{
			if (glyphId < 0 || glyphId >= this.GlyphCount)
				return GlyphPath.Empty;

			if (_cff != null)
				return _cff.GetGlyphPath(glyphId);

			if (_loca == null)
				return GlyphPath.Empty;

			return _paths.Get(glyphId, _decode);
		}

		/// <summary>
		/// Looks up a code in the subtable (<paramref name="platformId"/>, <paramref name="encodingId"/>).
		/// </summary>
		/// <param name="platformId">The platform id.</param>
		/// <param name="encodingId">The encoding id.</param>
		/// <param name="code">The character code.</param>
		/// <param name="glyphId">Receives the glyph id.</param>
		/// <returns>True when the subtable exists and maps the code to a glyph other than 0.</returns>
		public override bool TryGetCmapGlyph(int platformId, int encodingId, int code, out int glyphId)
		{
			foreach (var cmap in _cmaps)
			{
				if (cmap.PlatformId == platformId && cmap.EncodingId == encodingId)
				{
					glyphId = cmap.Lookup(code);
					if (glyphId > 0 && glyphId < this.GlyphCount)
						return true;
				}
			}

			glyphId = 0;
			return false;
		}

		/// <summary>
		/// Returns the Unicode text of a glyph from the best Unicode cmap subtable.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The text, or null.</returns>
		public override string GetUnicodeForGlyph(int glyphId)
		{
			if (!_unicodeBuilt)
			{
				lock (_unicodeLock)
				{
					if (!_unicodeBuilt)
					{
						_unicode = BuildUnicodeMap();
						_unicodeBuilt = true;
					}
				}
			}

			var map = _unicode;
			return map != null && glyphId >= 0 && glyphId < map.Length ? map[glyphId] : null;
		}

		/// <summary>
		/// Returns the raw bytes of a table.
		/// </summary>
		/// <param name="tag">The four-character table tag.</param>
		/// <returns>A copy of the table, or null when the font has no such table.</returns>
		public byte[] GetTable(string tag)
		{
			if (!_tables.TryGetValue(tag, out var record))
				return null;

			var table = new byte[record.Length];
			Buffer.BlockCopy(_data, record.Offset, table, 0, record.Length);
			return table;
		}

		#endregion

		#region Implementation

		// reads the table directory and the tables; returns false when nothing usable was found.
		private bool Parse(int offset)
		{
			if (offset < 0 || offset + 12 > _data.Length)
				return false;

			var numTables = BigEndian.U16(_data, offset + 4);
			for (int i = 0; i < numTables; i++)
			{
				var record = offset + 12 + i * 16;
				if (record + 16 > _data.Length)
					break;

				var tag = BigEndian.Tag(_data, record);
				var tableOffset = BigEndian.S32(_data, record + 8);
				var tableLength = BigEndian.S32(_data, record + 12);
				if (tableOffset <= 0 || tableOffset >= _data.Length)
					continue;

				// truncated files: keep the part of the table that is present.
				if (tableLength < 0 || tableLength > _data.Length - tableOffset)
					tableLength = _data.Length - tableOffset;

				if (!_tables.ContainsKey(tag))
					_tables.Add(tag, new TableRecord { Offset = tableOffset, Length = tableLength });
			}

			if (_tables.Count == 0)
				return false;

			RepairShiftedTables();

			ParseHead(out var indexToLocFormat);
			var numGlyphs = ParseMaxp();
			ParseCff();

			if (_cff == null)
				ParseLoca(indexToLocFormat, ref numGlyphs);
			else if (numGlyphs <= 0)
				numGlyphs = _cff.GlyphCount;

			if (numGlyphs <= 0)
				return false;

			this.GlyphCount = numGlyphs;
			ParseHmtx();
			ParseCmap();
			ParsePost();
			ParseName();
			ParseOS2();

			if (_cff != null)
			{
				this.FontMatrix = _cff.FontMatrix;
				this.FontName = this.FontName ?? _cff.FontName;

				// hmtx is in font units; rescale when the CFF glyph space differs from 1/unitsPerEm.
				var cffUnits = Math.Abs(this.FontMatrix[0]) > 1e-12 ? 1.0 / this.FontMatrix[0] : 1000;
				if (Math.Abs(cffUnits - _unitsPerEm) > 0.5)
					_hmtxScale = cffUnits / _unitsPerEm;
			}
			else
			{
				var scale = 1.0 / _unitsPerEm;
				this.FontMatrix = new[] { scale, 0, 0, scale, 0, 0 };
			}

			_paths = new GlyphCache<GlyphPath>(numGlyphs);
			return _cff != null || _loca != null || _cmaps.Count > 0 || _postNames != null || _numberOfHMetrics > 0;
		}

		// some broken files have every table offset off by a few bytes: when the 'head' magic number
		// is not where the directory says but is found nearby, shift all the tables by the same amount.
		private void RepairShiftedTables()
		{
			const uint magic = 0x5F0F3CF5;
			if (!_tables.TryGetValue("head", out var head) || BigEndian.U32(_data, head.Offset + 12) == magic)
				return;

			for (int delta = -4; delta <= 4; delta++)
			{
				if (delta == 0 || BigEndian.U32(_data, head.Offset + 12 + delta) != magic)
					continue;

				var tags = new List<string>(_tables.Keys);
				foreach (var tag in tags)
				{
					var record = _tables[tag];
					var offset = record.Offset + delta;
					if (offset < 0 || offset >= _data.Length)
						continue;

					_tables[tag] = new TableRecord { Offset = offset, Length = Math.Min(record.Length, _data.Length - offset) };
				}

				return;
			}
		}

		// 'head': units per em, the loca format, the bounding box and the italic style bit.
		private void ParseHead(out int indexToLocFormat)
		{
			indexToLocFormat = 0;
			if (!_tables.TryGetValue("head", out var head) || head.Length < 54)
				return;

			var p = head.Offset;
			var unitsPerEm = BigEndian.U16(_data, p + 18);

			// the spec allows 16 to 16384; anything else is corrupt.
			if (unitsPerEm >= 16 && unitsPerEm <= 16384)
				_unitsPerEm = unitsPerEm;

			this.FontBBox = new double[]
			{
				BigEndian.S16(_data, p + 36), BigEndian.S16(_data, p + 38),
				BigEndian.S16(_data, p + 40), BigEndian.S16(_data, p + 42)
			};

			if ((BigEndian.U16(_data, p + 44) & 0x02) != 0)
				this.IsItalic = true;

			indexToLocFormat = BigEndian.S16(_data, p + 50);
		}

		// 'maxp': the number of glyphs, or 0 when missing.
		private int ParseMaxp()
		{
			if (!_tables.TryGetValue("maxp", out var maxp) || maxp.Length < 6)
				return 0;

			return BigEndian.U16(_data, maxp.Offset + 4);
		}

		// 'CFF ': the outlines of an OpenType CFF font.
		private void ParseCff()
		{
			if (!_tables.TryGetValue("CFF ", out var table))
				return;

			var bytes = new byte[table.Length];
			Buffer.BlockCopy(_data, table.Offset, bytes, 0, table.Length);
			_cff = CffFont.TryParse(bytes);
		}

		// 'loca' and 'glyf': the glyph offsets, validated and repaired where possible.
		private void ParseLoca(int indexToLocFormat, ref int numGlyphs)
		{
			if (!_tables.TryGetValue("glyf", out _glyf) || !_tables.TryGetValue("loca", out var loca))
				return;

			var shortSize = (numGlyphs + 1) * 2;
			var longSize = (numGlyphs + 1) * 4;

			// some subsetters write the wrong indexToLocFormat: trust the table size when it only fits the other one.
			var isLong = indexToLocFormat != 0;
			if (numGlyphs > 0)
			{
				if (isLong && loca.Length < longSize && loca.Length >= shortSize)
					isLong = false;
				else if (!isLong && loca.Length >= longSize && loca.Length < longSize + 4 && loca.Length != shortSize && LooksLikeLongLoca(loca))
					isLong = true;
			}

			var entrySize = isLong ? 4 : 2;
			var available = loca.Length / entrySize;

			// without maxp, the loca size gives the glyph count; a loca shorter than numGlyphs truncates it.
			if (numGlyphs <= 0 || available < numGlyphs + 1)
				numGlyphs = Math.Max(0, available - 1);

			if (numGlyphs <= 0)
				return;

			var offsets = new int[numGlyphs + 1];
			for (int i = 0; i <= numGlyphs; i++)
			{
				var p = loca.Offset + i * entrySize;
				offsets[i] = isLong ? BigEndian.S32(_data, p) : BigEndian.U16(_data, p) * 2;
			}

			_loca = offsets;
		}

		// tests whether a loca table read with 32-bit entries gives non-decreasing offsets inside 'glyf'.
		private bool LooksLikeLongLoca(TableRecord loca)
		{
			var previous = 0;
			for (int p = loca.Offset; p + 4 <= loca.Offset + loca.Length; p += 4)
			{
				var value = BigEndian.S32(_data, p);
				if (value < previous || value > _glyf.Length)
					return false;

				previous = value;
			}

			return true;
		}

		// 'hhea' and 'hmtx': the advance widths.
		private void ParseHmtx()
		{
			if (!_tables.TryGetValue("hmtx", out _hmtx))
				return;

			var count = 0;
			if (_tables.TryGetValue("hhea", out var hhea) && hhea.Length >= 36)
				count = BigEndian.U16(_data, hhea.Offset + 34);

			if (count <= 0)
				count = this.GlyphCount;

			// an hmtx shorter than announced keeps the metrics that are present.
			_numberOfHMetrics = Math.Min(count, _hmtx.Length / 4);
		}

		// 'cmap': all the decodable subtables.
		private void ParseCmap()
		{
			if (!_tables.TryGetValue("cmap", out var cmap))
				return;

			_cmaps = TrueTypeCmap.ParseTable(_data, cmap.Offset, cmap.Length);
			var ids = new List<(int, int)>();
			foreach (var subtable in _cmaps)
			{
				var id = (subtable.PlatformId, subtable.EncodingId);
				if (!ids.Contains(id))
					ids.Add(id);
			}

			_cmapIds = ids.ToArray();
		}

		// 'post': glyph names from formats 1, 2 and 2.5, and the italic angle.
		private void ParsePost()
		{
			if (!_tables.TryGetValue("post", out var post) || post.Length < 32)
				return;

			var p = post.Offset;
			var end = p + post.Length;
			if (BigEndian.S32(_data, p + 4) != 0)
				this.IsItalic = true;

			var mac = FontTables.MacStandardGlyphNames;
			var version = BigEndian.S32(_data, p);
			switch (version)
			{
				case 0x00010000:
				{
					var names = new string[this.GlyphCount];
					for (int i = 0; i < names.Length && i < mac.Length; i++)
						names[i] = mac[i];

					_postNames = names;
					break;
				}

				case 0x00020000:
					_postNames = ParsePostFormat2(p, end);
					break;

				case 0x00025000:
				{
					// format 2.5: each glyph stores a signed offset from its id into the standard order.
					var count = Math.Min(this.GlyphCount, BigEndian.U16(_data, p + 32));
					var names = new string[this.GlyphCount];
					for (int i = 0; i < count && p + 34 + i < end; i++)
					{
						var index = i + BigEndian.S8(_data, p + 34 + i);
						if (index >= 0 && index < mac.Length)
							names[i] = mac[index];
					}

					_postNames = names;
					break;
				}
			}
		}

		// post format 2: glyph name indices into the standard order or into trailing Pascal strings.
		private string[] ParsePostFormat2(int p, int end)
		{
			var count = BigEndian.U16(_data, p + 32);
			var indexStart = p + 34;
			var stringsStart = indexStart + count * 2;
			if (stringsStart > end)
				return null;

			// collect the custom names.
			var custom = new List<string>();
			var s = stringsStart;
			while (s < end)
			{
				var length = _data[s++];
				if (s + length > end)
					break;

				custom.Add(Encoding.ASCII.GetString(_data, s, length));
				s += length;
			}

			var mac = FontTables.MacStandardGlyphNames;
			var names = new string[this.GlyphCount];
			for (int i = 0; i < count && i < names.Length; i++)
			{
				var index = BigEndian.U16(_data, indexStart + i * 2);
				if (index < 258)
					names[i] = mac[index];
				else if (index - 258 < custom.Count)
					names[i] = custom[index - 258];
			}

			return names;
		}

		// 'name': the PostScript name (name id 6), falling back on the full name (4).
		private void ParseName()
		{
			if (!_tables.TryGetValue("name", out var table) || table.Length < 6)
				return;

			var p = table.Offset;
			var end = p + table.Length;
			var count = BigEndian.U16(_data, p + 2);
			var storage = p + BigEndian.U16(_data, p + 4);

			string best = null;
			var bestScore = -1;
			for (int i = 0; i < count; i++)
			{
				var record = p + 6 + i * 12;
				if (record + 12 > end)
					break;

				var platformId = BigEndian.U16(_data, record);
				var nameId = BigEndian.U16(_data, record + 6);
				var length = BigEndian.U16(_data, record + 8);
				var offset = storage + BigEndian.U16(_data, record + 10);
				if ((nameId != 6 && nameId != 4) || length == 0 || offset + length > end)
					continue;

				var text = platformId == 0 || platformId == 3
					? Encoding.BigEndianUnicode.GetString(_data, offset, length & ~1)
					: Latin1(offset, length);

				text = text.Trim('\0', ' ');
				if (text.Length == 0)
					continue;

				var score = (nameId == 6 ? 10 : 0) + (platformId == 3 ? 2 : platformId == 1 ? 1 : 0);
				if (score > bestScore)
				{
					best = text;
					bestScore = score;
				}
			}

			if (best != null)
				this.FontName = bestScore >= 10 ? best : best.Replace(" ", "");
		}

		// decodes single-byte text, treating the bytes as Latin-1 (Macintosh names are ASCII in practice).
		private string Latin1(int offset, int length)
		{
			var chars = new char[length];
			for (int i = 0; i < length; i++)
				chars[i] = (char)_data[offset + i];

			return new string(chars);
		}

		// 'OS/2': the weight class and the italic selection bit.
		private void ParseOS2()
		{
			if (!_tables.TryGetValue("OS/2", out var os2) || os2.Length < 64)
				return;

			var weight = BigEndian.U16(_data, os2.Offset + 4);

			// some old fonts store 1-9 instead of 100-900.
			if (weight >= 1 && weight <= 9)
				weight *= 100;

			if (weight >= 100 && weight <= 1000)
				this.Weight = Math.Min(900, (weight + 50) / 100 * 100);

			if ((BigEndian.U16(_data, os2.Offset + 62) & 0x01) != 0)
				this.IsItalic = true;
		}

		// builds the glyph id to text map from the best Unicode subtable.
		private string[] BuildUnicodeMap()
		{
			var cmap = FindUnicodeCmap();
			if (cmap == null)
				return null;

			var map = new string[this.GlyphCount];
			cmap.ForEach(this.GlyphCount, (code, gid) =>
			{
				// control codes and surrogates are not text.
				if (code < 0x20 || (code >= 0x7F && code < 0xA0) || (code >= 0xD800 && code <= 0xDFFF) || code > 0x10FFFF)
					return;

				var existing = map[gid];
				if (existing != null)
				{
					// keep the first code, unless it is in the private use area and the new one is not.
					var first = char.ConvertToUtf32(existing, 0);
					if (!(IsPrivateUse(first) && !IsPrivateUse(code)))
						return;
				}

				map[gid] = char.ConvertFromUtf32(code);
			});

			return map;
		}

		// returns the preferred Unicode subtable: (3,10), (3,1), then (0,*) with the highest encoding id.
		private TrueTypeCmap FindUnicodeCmap()
		{
			TrueTypeCmap best = null;
			var bestScore = 0;
			foreach (var cmap in _cmaps)
			{
				if (cmap.IsEmpty)
					continue;

				var score = 0;
				if (cmap.PlatformId == 3 && cmap.EncodingId == 10)
					score = 100;
				else if (cmap.PlatformId == 3 && cmap.EncodingId == 1)
					score = 90;
				else if (cmap.PlatformId == 0 && cmap.EncodingId != 5)
					score = 50 + cmap.EncodingId;

				if (score > bestScore)
				{
					best = cmap;
					bestScore = score;
				}
			}

			return best;
		}

		// tests for the Unicode private use areas.
		private static bool IsPrivateUse(int code)
		{
			return (code >= 0xE000 && code <= 0xF8FF) || code >= 0xF0000;
		}

		// decodes a glyf outline into a path; never throws.
		private GlyphPath DecodeGlyph(int glyphId)
		{
			try
			{
				var outline = new Outline();
				var budget = MaxComponents;
				LoadOutline(glyphId, outline, 0, ref budget);
				return ToPath(outline);
			}
			catch (Exception)
			{
				return GlyphPath.Empty;
			}
		}

		// returns the byte range of a glyph inside the font data; false when the glyph is empty or invalid.
		private bool GetGlyphRange(int glyphId, out int start, out int end)
		{
			start = end = 0;
			if (_loca == null || glyphId < 0 || glyphId + 1 >= _loca.Length)
				return false;

			var offset = _loca[glyphId];
			var next = _loca[glyphId + 1];
			if (offset < 0 || offset >= _glyf.Length)
				return false;

			// an empty glyph has equal offsets; a decreasing next offset is corrupt, so let the glyph parse to the end of glyf.
			if (next == offset)
				return false;

			if (next < offset || next > _glyf.Length)
				next = _glyf.Length;

			start = _glyf.Offset + offset;
			end = _glyf.Offset + next;
			return end - start >= 10;
		}

		// appends the points of a glyph (simple or composite) to the outline; budget bounds the total number of
		// glyphs loaded, since nested composites can otherwise expand exponentially.
		private void LoadOutline(int glyphId, Outline outline, int depth, ref int budget)
		{
			if (depth > MaxCompositeDepth || --budget < 0 || !GetGlyphRange(glyphId, out var start, out var end))
				return;

			var contours = BigEndian.S16(_data, start);
			if (contours >= 0)
				LoadSimple(start, end, contours, outline);
			else
				LoadComposite(start, end, outline, depth, ref budget);
		}

		// parses a simple glyph: contour end points, instructions, flags and delta-encoded coordinates.
		private void LoadSimple(int start, int end, int contours, Outline outline)
		{
			if (contours == 0)
				return;

			var p = start + 10;
			var ends = new int[contours];
			var last = -1;
			for (int i = 0; i < contours; i++)
			{
				var e = BigEndian.U16(_data, p + i * 2);

				// end points must increase; clamp broken ones so the contours stay well formed.
				if (e < last)
					e = last;

				ends[i] = e;
				last = e;
			}

			p += contours * 2;
			var pointCount = last + 1;
			if (pointCount <= 0 || pointCount > MaxPoints)
				return;

			var instructionLength = BigEndian.U16(_data, p);
			p += 2 + instructionLength;
			if (p >= end)
				return;

			// flags, with run-length repeats.
			var flags = new byte[pointCount];
			for (int i = 0; i < pointCount; )
			{
				if (p >= end)
				{
					pointCount = i;
					break;
				}

				var flag = _data[p++];
				flags[i++] = flag;
				if ((flag & RepeatFlag) != 0 && p < end)
				{
					var repeat = _data[p++];
					for (int r = 0; r < repeat && i < pointCount; r++)
						flags[i++] = flag;
				}
			}

			// x coordinates, then y coordinates, each relative to the previous point.
			var xs = new int[pointCount];
			var ys = new int[pointCount];
			p = ReadCoordinates(p, end, flags, pointCount, xs, XShortVector, XIsSameOrPositive);
			ReadCoordinates(p, end, flags, pointCount, ys, YShortVector, YIsSameOrPositive);

			var baseIndex = outline.Count;
			for (int i = 0; i < pointCount; i++)
				outline.Add(xs[i], ys[i], (flags[i] & OnCurvePoint) != 0);

			foreach (var e in ends)
			{
				if (e < pointCount)
					outline.ContourEnds.Add(baseIndex + e);
				else
				{
					// a truncated glyph: close the last complete contour on the last point read.
					if (pointCount > 0 && (outline.ContourEnds.Count == 0 || outline.ContourEnds[outline.ContourEnds.Count - 1] < baseIndex + pointCount - 1))
						outline.ContourEnds.Add(baseIndex + pointCount - 1);
					break;
				}
			}
		}

		// reads one coordinate array of a simple glyph and returns the position after it.
		private int ReadCoordinates(int p, int end, byte[] flags, int count, int[] values, int shortFlag, int sameFlag)
		{
			var value = 0;
			for (int i = 0; i < count; i++)
			{
				var flag = flags[i];
				if ((flag & shortFlag) != 0)
				{
					var delta = BigEndian.U8(_data, p++);
					value += (flag & sameFlag) != 0 ? delta : -delta;
				}
				else if ((flag & sameFlag) == 0)
				{
					value += BigEndian.S16(_data, p);
					p += 2;
				}

				values[i] = value;
			}

			return p;
		}

		// parses a composite glyph: each component is loaded, transformed and positioned.
		private void LoadComposite(int start, int end, Outline outline, int depth, ref int budget)
		{
			var p = start + 10;
			int flags;
			do
			{
				if (p + 4 > end)
					return;

				flags = BigEndian.U16(_data, p);
				var glyphIndex = BigEndian.U16(_data, p + 2);
				p += 4;

				int arg1, arg2;
				var xyValues = (flags & ArgsAreXYValues) != 0;
				if ((flags & ArgsAreWords) != 0)
				{
					arg1 = xyValues ? BigEndian.S16(_data, p) : BigEndian.U16(_data, p);
					arg2 = xyValues ? BigEndian.S16(_data, p + 2) : BigEndian.U16(_data, p + 2);
					p += 4;
				}
				else
				{
					arg1 = xyValues ? BigEndian.S8(_data, p) : BigEndian.U8(_data, p);
					arg2 = xyValues ? BigEndian.S8(_data, p + 1) : BigEndian.U8(_data, p + 1);
					p += 2;
				}

				// the 2x2 transform, as F2Dot14 values.
				double a = 1, b = 0, c = 0, d = 1;
				if ((flags & WeHaveAScale) != 0)
				{
					a = d = F2Dot14(p);
					p += 2;
				}
				else if ((flags & WeHaveAnXAndYScale) != 0)
				{
					a = F2Dot14(p);
					d = F2Dot14(p + 2);
					p += 4;
				}
				else if ((flags & WeHaveATwoByTwo) != 0)
				{
					a = F2Dot14(p);
					b = F2Dot14(p + 2);
					c = F2Dot14(p + 4);
					d = F2Dot14(p + 6);
					p += 8;
				}

				// load the component on its own so it can be transformed and matched.
				var component = new Outline();
				LoadOutline(glyphIndex, component, depth + 1, ref budget);
				for (int i = 0; i < component.Count; i++)
				{
					var x = component.X[i];
					var y = component.Y[i];
					component.X[i] = (float)(a * x + c * y);
					component.Y[i] = (float)(b * x + d * y);
				}

				double dx, dy;
				if (xyValues)
				{
					dx = arg1;
					dy = arg2;

					// Apple-style scaled offsets; the default (Microsoft) leaves the offset unscaled.
					if ((flags & ScaledComponentOffset) != 0)
					{
						dx = a * arg1 + c * arg2;
						dy = b * arg1 + d * arg2;
					}
				}
				else
				{
					// point matching: align the component point arg2 on the point arg1 of the glyph so far.
					// this is approximate because hinting instructions, which may move the points, are not run.
					dx = dy = 0;
					if (arg1 < outline.Count && arg2 < component.Count)
					{
						dx = outline.X[arg1] - component.X[arg2];
						dy = outline.Y[arg1] - component.Y[arg2];
					}
				}

				var baseIndex = outline.Count;
				for (int i = 0; i < component.Count && outline.Count < MaxPoints; i++)
					outline.Add((float)(component.X[i] + dx), (float)(component.Y[i] + dy), component.OnCurve[i]);

				foreach (var e in component.ContourEnds)
				{
					if (baseIndex + e < outline.Count)
						outline.ContourEnds.Add(baseIndex + e);
				}
			}
			while ((flags & MoreComponents) != 0);
		}

		// reads a 2.14 fixed-point number.
		private double F2Dot14(int p)
		{
			return BigEndian.S16(_data, p) / 16384.0;
		}

		// converts the quadratic outline to a path of lines and cubic curves.
		private static GlyphPath ToPath(Outline outline)
		{
			if (outline.ContourEnds.Count == 0)
				return GlyphPath.Empty;

			var builder = new GlyphPathBuilder();
			var first = 0;
			foreach (var last in outline.ContourEnds)
			{
				if (last >= first)
					AddContour(builder, outline, first, last);

				first = last + 1;
			}

			return builder.ToPath();
		}

		// adds one closed quadratic contour, inserting the implied on-curve points between two off-curve points.
		private static void AddContour(GlyphPathBuilder builder, Outline o, int first, int last)
		{
			var count = last - first + 1;
			if (count < 2)
				return;

			// pick an on-curve start point: the first point, else the last, else the midpoint of the two.
			float startX, startY;
			int begin, steps;
			if (o.OnCurve[first])
			{
				startX = o.X[first];
				startY = o.Y[first];
				begin = first + 1;
				steps = count - 1;
			}
			else if (o.OnCurve[last])
			{
				startX = o.X[last];
				startY = o.Y[last];
				begin = first;
				steps = count - 1;
			}
			else
			{
				startX = (o.X[first] + o.X[last]) / 2;
				startY = (o.Y[first] + o.Y[last]) / 2;
				begin = first;
				steps = count;
			}

			builder.MoveTo(startX, startY);

			var hasControl = false;
			float cx = 0, cy = 0;
			for (int k = 0; k < steps; k++)
			{
				var i = begin + k;
				var x = o.X[i];
				var y = o.Y[i];
				if (o.OnCurve[i])
				{
					if (hasControl)
						builder.QuadTo(cx, cy, x, y);
					else
						builder.LineTo(x, y);

					hasControl = false;
				}
				else
				{
					if (hasControl)
						builder.QuadTo(cx, cy, (cx + x) / 2, (cy + y) / 2);

					cx = x;
					cy = y;
					hasControl = true;
				}
			}

			if (hasControl)
				builder.QuadTo(cx, cy, startX, startY);
			else if (builder.CurrentX != startX || builder.CurrentY != startY)
				builder.LineTo(startX, startY);

			builder.ClosePath();
		}

		#endregion
	}
}
