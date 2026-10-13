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

// Compact Font Format font programs, name-keyed and CID-keyed (Adobe Technical Note #5176;
// ISO 32000-2, 9.9 FontFile3 with Type1C and CIDFontType0C).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// A CFF font: Top DICT, charset, encoding, Private DICTs and Type 2 charstrings.
	/// </summary>
	/// <remarks>
	/// Only the first font of a FontSet is used, as PDF requires. For CID-keyed fonts the charset maps
	/// glyph ids to CIDs and every glyph uses the Private DICT of its FDArray entry, selected by FDSelect.
	/// </remarks>
	internal sealed class CffFont : FontProgram
	{
		#region Nested types

		/// <summary>
		/// The charstring context of a Private DICT.
		/// </summary>
		private sealed class PrivateData
		{
			/// <summary>
			/// The local subroutines.
			/// </summary>
			public CffIndex Subrs = CffIndex.Empty;

			/// <summary>
			/// The width of glyphs that do not specify one.
			/// </summary>
			public double DefaultWidthX;

			/// <summary>
			/// The base of the widths specified in the charstrings.
			/// </summary>
			public double NominalWidthX;

			/// <summary>
			/// The transform from this font DICT's glyph space to the reported glyph space, or null for the identity.
			/// </summary>
			public double[] Transform;
		}

		/// <summary>
		/// A decoded glyph: outline and advance width.
		/// </summary>
		private sealed class GlyphRecord
		{
			/// <summary>
			/// The outline.
			/// </summary>
			public GlyphPath Path;

			/// <summary>
			/// The advance width in glyph space.
			/// </summary>
			public double Width;
		}

		#endregion

		#region Fields

		// Top DICT operators (Technical Note #5176, table 9); two-byte operators 12 x are encoded as 1200 + x.

		/// <summary>Weight: the SID of the weight name, for example "Bold".</summary>
		private const int OpWeight = 4;

		/// <summary>FontBBox: the font bounding box [xMin yMin xMax yMax] in glyph space.</summary>
		private const int OpFontBBox = 5;

		/// <summary>charset: the offset of the charset, or 0-2 for a predefined one.</summary>
		private const int OpCharset = 15;

		/// <summary>Encoding: the offset of the encoding, or 0/1 for Standard/Expert.</summary>
		private const int OpEncoding = 16;

		/// <summary>CharStrings: the offset of the CharStrings INDEX.</summary>
		private const int OpCharStrings = 17;

		/// <summary>Private: the size and offset of the Private DICT.</summary>
		private const int OpPrivate = 18;

		/// <summary>ItalicAngle (12 2): the italic angle in degrees counter-clockwise from vertical.</summary>
		private const int OpItalicAngle = 1202;

		/// <summary>FontMatrix (12 7): the glyph space to text space matrix.</summary>
		private const int OpFontMatrix = 1207;

		/// <summary>ROS (12 30): Registry, Ordering and Supplement; its presence marks a CID-keyed font.</summary>
		private const int OpRos = 1230;

		/// <summary>FDArray (12 36): the offset of the Font DICT INDEX of a CID-keyed font.</summary>
		private const int OpFdArray = 1236;

		/// <summary>FDSelect (12 37): the offset of the glyph to Font DICT mapping of a CID-keyed font.</summary>
		private const int OpFdSelect = 1237;

		/// <summary>FontName (12 38): the SID of the name of a Font DICT in the FDArray.</summary>
		private const int OpFontName = 1238;

		// Private DICT operators (Technical Note #5176, table 23).

		/// <summary>Subrs: the offset of the local subroutines, relative to the start of the Private DICT.</summary>
		private const int OpSubrs = 19;

		/// <summary>defaultWidthX: the advance width of glyphs whose charstring has no width.</summary>
		private const int OpDefaultWidthX = 20;

		/// <summary>nominalWidthX: the base added to the width stored in a charstring.</summary>
		private const int OpNominalWidthX = 21;

		// the limit of nested seac compositions (a seac base that is itself a seac).
		private const int MaxSeacDepth = 4;

		// the CFF data.
		private readonly byte[] _data;

		// the String INDEX, for string ids at or above 391.
		private CffIndex _strings = CffIndex.Empty;

		// the global subroutines.
		private CffIndex _globalSubrs = CffIndex.Empty;

		// the charstrings, one per glyph.
		private CffIndex _charStrings = CffIndex.Empty;

		// the string id (name-keyed) or CID (CID-keyed) of each glyph.
		private int[] _charset;

		// the built-in encoding (code to glyph name), or null for the Standard encoding.
		private string[] _encoding;

		// the Private DICT of a name-keyed font, or the FDArray Private DICTs of a CID-keyed font.
		private PrivateData[] _privates;

		// the FDArray index of each glyph of a CID-keyed font, or null.
		private byte[] _fdSelect;

		// CID to glyph id for CID-keyed fonts; null otherwise.
		private int[] _cidToGid;

		// the decoded glyphs.
		private GlyphCache<GlyphRecord> _glyphs;

		// the cache factory, allocated once.
		private readonly Func<int, GlyphRecord> _decode;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a font over its data; call <see cref="Parse"/> to read it.
		/// </summary>
		/// <param name="data">The CFF data.</param>
		private CffFont(byte[] data)
		{
			_data = data;
			_decode = gid => Decode(gid, 0);
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns <see cref="FontProgramKind.CffCid"/> for CID-keyed fonts, otherwise <see cref="FontProgramKind.Cff"/>.
		/// </summary>
		public override FontProgramKind Kind => this.IsCidKeyed ? FontProgramKind.CffCid : FontProgramKind.Cff;

		/// <summary>
		/// Returns the custom or Expert built-in encoding, or null when the font uses the Standard encoding.
		/// </summary>
		public override string[] BuiltInEncoding => _encoding;

		/// <summary>
		/// Returns the Registry-Ordering-Supplement of a CID-keyed font, for example <c>Adobe-Japan1-6</c>; otherwise null.
		/// </summary>
		public string CidSystemInfo { get; private set; }

		#endregion

		#region Methods

		/// <summary>
		/// Parses a bare CFF font.
		/// </summary>
		/// <param name="data">The CFF data.</param>
		/// <returns>The font, or null when the data is not a usable CFF font.</returns>
		public static CffFont TryParse(byte[] data)
		{
			try
			{
				if (data == null || data.Length < 4)
					return null;

				var font = new CffFont(data);
				return font.Parse() ? font : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>
		/// Returns the name of a glyph from the charset; null for CID-keyed fonts.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The glyph name, or null.</returns>
		public override string GetGlyphName(int glyphId)
		{
			if (this.IsCidKeyed || glyphId < 0 || glyphId >= _charset.Length)
				return null;

			return GetString(_charset[glyphId]);
		}

		/// <summary>
		/// Returns the id of a named glyph.
		/// </summary>
		/// <param name="glyphName">The glyph name.</param>
		/// <returns>The glyph id, or -1.</returns>
		public override int GetGlyphId(string glyphName) => this.IsCidKeyed ? -1 : GetGlyphIdFromNames(glyphName);

		/// <summary>
		/// Returns the CID of a glyph of a CID-keyed font.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The CID, or -1 when the font is not CID-keyed or the id is out of range.</returns>
		public int GetCid(int glyphId)
		{
			return this.IsCidKeyed && glyphId >= 0 && glyphId < _charset.Length ? _charset[glyphId] : -1;
		}

		/// <summary>
		/// Maps a CID to a glyph id through the charset of a CID-keyed font; identity for name-keyed fonts.
		/// </summary>
		/// <param name="cid">The CID.</param>
		/// <returns>The glyph id, or 0 when the CID is not in the font.</returns>
		public override int CidToGlyphId(int cid)
		{
			if (_cidToGid == null)
				return cid;

			return cid >= 0 && cid < _cidToGid.Length ? _cidToGid[cid] : 0;
		}

		/// <summary>
		/// Returns the advance width of a glyph, from its charstring.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The width in glyph space, or 0.</returns>
		public override double GetAdvanceWidth(int glyphId)
		{
			if (glyphId < 0 || glyphId >= this.GlyphCount)
				return 0;

			return _glyphs.Get(glyphId, _decode).Width;
		}

		/// <summary>
		/// Returns the outline of a glyph.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The outline, or <see cref="GlyphPath.Empty"/>.</returns>
		public override GlyphPath GetGlyphPath(int glyphId)
		{
			if (glyphId < 0 || glyphId >= this.GlyphCount)
				return GlyphPath.Empty;

			return _glyphs.Get(glyphId, _decode).Path;
		}

		#endregion

		#region Implementation

		// reads the header, the INDEXes and the Top DICT; returns false when the font is unusable.
		private bool Parse()
		{
			var headerSize = _data[2];
			var names = CffIndex.Read(_data, headerSize);
			var topDicts = CffIndex.Read(_data, names.End);
			if (topDicts.Count == 0)
				return false;

			_strings = CffIndex.Read(_data, topDicts.End);
			_globalSubrs = CffIndex.Read(_data, _strings.End);

			this.FontName = names.GetString(_data, 0);
			topDicts.TryGetRange(0, out var topStart, out var topEnd);
			var top = CffDict.Read(_data, topStart, topEnd);

			_charStrings = CffIndex.Read(_data, top.GetInt(OpCharStrings, -1));
			var glyphCount = _charStrings.Count;
			if (glyphCount == 0)
				return false;

			this.GlyphCount = glyphCount;
			this.IsCidKeyed = top.Has(OpRos);
			this.Weight = ParseWeight(GetString(top.GetInt(OpWeight, -1)));
			this.IsItalic = top.GetNumber(OpItalicAngle, 0) != 0;

			var bbox = top.Get(OpFontBBox);
			if (bbox != null && bbox.Length == 4)
				this.FontBBox = bbox;

			var topMatrix = top.Get(OpFontMatrix);
			if (!IsValidMatrix(topMatrix))
				topMatrix = null;

			if (topMatrix != null)
				this.FontMatrix = topMatrix;

			_charset = ReadCharset(top.GetInt(OpCharset, 0), glyphCount);

			if (this.IsCidKeyed)
			{
				var ros = top.Get(OpRos);
				if (ros.Length >= 3)
					this.CidSystemInfo = GetString((int)ros[0]) + "-" + GetString((int)ros[1]) + "-" + ros[2];

				ParseCidFont(top, topMatrix);
				BuildCidMap();
			}
			else
			{
				_privates = new[] { ReadPrivate(top.Get(OpPrivate)) };
				_encoding = ReadEncoding(top.GetInt(OpEncoding, 0));
			}

			_glyphs = new GlyphCache<GlyphRecord>(glyphCount);
			return true;
		}

		// reads the FDArray and FDSelect of a CID-keyed font and derives the reported FontMatrix.
		private void ParseCidFont(CffDict top, double[] topMatrix)
		{
			var fdArray = CffIndex.Read(_data, top.GetInt(OpFdArray, -1));
			var count = Math.Max(1, Math.Min(fdArray.Count, 256));
			var privates = new PrivateData[count];
			var matrices = new double[count][];
			for (int i = 0; i < count; i++)
			{
				CffDict fd = null;
				if (fdArray.TryGetRange(i, out var start, out var end))
					fd = CffDict.Read(_data, start, end);

				privates[i] = ReadPrivate(fd?.Get(OpPrivate));

				// the FD matrix concatenates with an explicit Top DICT matrix; with no Top DICT matrix it replaces the default.
				var matrix = fd?.Get(OpFontMatrix);
				if (!IsValidMatrix(matrix))
					matrix = null;

				if (matrix != null && topMatrix != null)
					matrix = Multiply(matrix, topMatrix);

				matrices[i] = matrix ?? this.FontMatrix;
				if (i == 0 && this.FontName == null)
					this.FontName = GetString(fd?.GetInt(OpFontName, -1) ?? -1);
			}

			// report the first FD's matrix; glyphs of the other FDs are transformed into its space.
			var reported = matrices[0];
			this.FontMatrix = reported;
			for (int i = 1; i < count; i++)
			{
				if (!SameMatrix(matrices[i], reported))
					privates[i].Transform = Multiply(matrices[i], Invert(reported));
			}

			_privates = privates;
			_fdSelect = ReadFdSelect(top.GetInt(OpFdSelect, -1), this.GlyphCount, count);
		}

		// reads a Private DICT from its [size, offset] operands.
		private PrivateData ReadPrivate(double[] operands)
		{
			var result = new PrivateData();
			if (operands == null || operands.Length < 2)
				return result;

			var size = (int)operands[0];
			var offset = (int)operands[1];
			if (size < 0 || offset < 0 || offset >= _data.Length)
				return result;

			var dict = CffDict.Read(_data, offset, offset + size);
			result.DefaultWidthX = dict.GetNumber(OpDefaultWidthX, 0);
			result.NominalWidthX = dict.GetNumber(OpNominalWidthX, 0);

			// the Subrs offset is relative to the start of the Private DICT.
			var subrs = dict.GetInt(OpSubrs, 0);
			if (subrs > 0)
				result.Subrs = CffIndex.Read(_data, offset + subrs);

			return result;
		}

		// reads the charset: the string id (or CID) of each glyph; glyph 0 is always .notdef.
		private int[] ReadCharset(int offset, int glyphCount)
		{
			var charset = new int[glyphCount];
			if (!this.IsCidKeyed && offset <= 2)
			{
				// predefined charsets: ISOAdobe, Expert and ExpertSubset.
				short[] table = offset == 1 ? FontTables.CffExpertCharset : offset == 2 ? FontTables.CffExpertSubsetCharset : null;
				for (int gid = 0; gid < glyphCount; gid++)
				{
					if (table != null)
						charset[gid] = gid < table.Length ? table[gid] : 0;
					else
						charset[gid] = gid <= FontTables.CffIsoAdobeLastSid ? gid : 0;
				}

				return charset;
			}

			if (offset <= 0 || offset >= _data.Length)
			{
				// a CID-keyed font without a charset: assume CID = glyph id.
				for (int gid = 0; gid < glyphCount; gid++)
					charset[gid] = this.IsCidKeyed ? gid : 0;

				return charset;
			}

			var format = _data[offset];
			var p = offset + 1;
			var g = 1;
			switch (format)
			{
				case 0:
					for (; g < glyphCount && p + 2 <= _data.Length; g++, p += 2)
						charset[g] = BigEndian.U16(_data, p);
					break;

				case 1:
				case 2:
					while (g < glyphCount && p < _data.Length)
					{
						var first = BigEndian.U16(_data, p);
						var left = format == 1 ? BigEndian.U8(_data, p + 2) : BigEndian.U16(_data, p + 2);
						p += format == 1 ? 3 : 4;
						for (int i = 0; i <= left && g < glyphCount; i++)
							charset[g++] = first + i;
					}
					break;
			}

			return charset;
		}

		// reads the encoding: code to glyph name; null for the Standard encoding.
		private string[] ReadEncoding(int offset)
		{
			if (offset == 0)
				return null;

			if (offset == 1)
				return (string[])FontTables.CffExpertEncoding.Clone();

			if (offset < 0 || offset >= _data.Length)
				return null;

			var encoding = new string[256];
			var format = _data[offset];
			var p = offset + 1;
			switch (format & 0x7F)
			{
				case 0:
				{
					// one code per glyph, starting at glyph 1.
					var count = BigEndian.U8(_data, p++);
					for (int i = 0; i < count; i++)
					{
						var code = BigEndian.U8(_data, p++);
						var gid = i + 1;
						if (gid < this.GlyphCount)
							encoding[code] = GetGlyphName(gid);
					}
					break;
				}

				case 1:
				{
					// ranges of consecutive codes assigned to consecutive glyphs.
					var ranges = BigEndian.U8(_data, p++);
					var gid = 1;
					for (int r = 0; r < ranges; r++)
					{
						var first = BigEndian.U8(_data, p);
						var left = BigEndian.U8(_data, p + 1);
						p += 2;
						for (int i = 0; i <= left && gid < this.GlyphCount; i++, gid++)
						{
							var code = first + i;
							if (code < 256)
								encoding[code] = GetGlyphName(gid);
						}
					}
					break;
				}

				default:
					return null;
			}

			// supplements: extra codes mapped to glyphs by name.
			if ((format & 0x80) != 0)
			{
				var count = BigEndian.U8(_data, p++);
				for (int i = 0; i < count; i++)
				{
					var code = BigEndian.U8(_data, p);
					var sid = BigEndian.U16(_data, p + 1);
					p += 3;
					encoding[code] = GetString(sid);
				}
			}

			return encoding;
		}

		// reads FDSelect formats 0 and 3: the font DICT index of every glyph.
		private byte[] ReadFdSelect(int offset, int glyphCount, int fdCount)
		{
			var select = new byte[glyphCount];
			if (offset <= 0 || offset >= _data.Length)
				return select;

			var format = _data[offset];
			if (format == 0)
			{
				for (int gid = 0; gid < glyphCount; gid++)
					select[gid] = (byte)BigEndian.U8(_data, offset + 1 + gid);
			}
			else if (format == 3)
			{
				var ranges = BigEndian.U16(_data, offset + 1);
				var p = offset + 3;
				for (int r = 0; r < ranges; r++)
				{
					var first = BigEndian.U16(_data, p);
					var fd = BigEndian.U8(_data, p + 2);

					// the next range's first glyph, or the sentinel, ends this range.
					var next = BigEndian.U16(_data, p + 3);
					p += 3;
					for (int gid = first; gid < next && gid < glyphCount; gid++)
						select[gid] = (byte)fd;
				}
			}

			// out-of-range FD indices fall back to the first FD.
			for (int i = 0; i < select.Length; i++)
			{
				if (select[i] >= fdCount)
					select[i] = 0;
			}

			return select;
		}

		// builds the CID to glyph id map from the charset of a CID-keyed font.
		private void BuildCidMap()
		{
			var max = 0;
			foreach (var cid in _charset)
				max = Math.Max(max, cid);

			// CIDs are limited to 16 bits by the charset format.
			var map = new int[Math.Min(max, 0xFFFF) + 1];
			for (int gid = _charset.Length - 1; gid > 0; gid--)
			{
				var cid = _charset[gid];
				if (cid > 0 && cid < map.Length)
					map[cid] = gid;
			}

			_cidToGid = map;
		}

		// returns a string by string id; null for invalid ids.
		private string GetString(int sid)
		{
			if (sid < 0)
				return null;

			if (sid < FontTables.CffStandardStringCount)
				return FontTables.CffStandardStrings[sid];

			return _strings.GetString(_data, sid - FontTables.CffStandardStringCount);
		}

		// runs the charstring of a glyph; never throws.
		private GlyphRecord Decode(int glyphId, int depth)
		{
			var record = new GlyphRecord { Path = GlyphPath.Empty };
			try
			{
				var fd = _fdSelect != null ? _fdSelect[glyphId] : 0;
				var priv = _privates[Math.Min(fd, _privates.Length - 1)];
				record.Width = priv.DefaultWidthX;
				if (!_charStrings.TryGetRange(glyphId, out var start, out var end))
					return record;

				var builder = new GlyphPathBuilder();
				var interpreter = new Type2CharString(_data, priv.Subrs, _globalSubrs, priv.DefaultWidthX, priv.NominalWidthX, builder);
				interpreter.Execute(start, end);
				record.Width = interpreter.Width;

				if (interpreter.IsSeac)
					AddSeac(builder, interpreter, depth);

				var path = builder.ToPath();
				var t = priv.Transform;
				if (t != null)
				{
					path = path.Transform(t[0], t[1], t[2], t[3], t[4], t[5]);
					record.Width *= t[0];
				}

				record.Path = path;
			}
			catch (Exception)
			{
				// keep the default width and an empty outline.
			}

			return record;
		}

		// composes a seac glyph: the base and accent glyphs are found by their StandardEncoding codes.
		private void AddSeac(GlyphPathBuilder builder, Type2CharString interpreter, int depth)
		{
			if (this.IsCidKeyed || depth >= MaxSeacDepth)
				return;

			var standard = Encodings.Standard;
			var baseCode = interpreter.BaseCode;
			var accentCode = interpreter.AccentCode;
			if (baseCode < 0 || baseCode >= standard.Length || accentCode < 0 || accentCode >= standard.Length)
				return;

			var baseGid = GetGlyphId(standard[baseCode]);
			var accentGid = GetGlyphId(standard[accentCode]);
			if (baseGid > 0)
				builder.Append(Decode(baseGid, depth + 1).Path, 0, 0);

			if (accentGid > 0)
				builder.Append(Decode(accentGid, depth + 1).Path, (float)interpreter.AccentX, (float)interpreter.AccentY);
		}

		// returns the product m1 x m2 of two affine matrices (apply m1, then m2).
		private static double[] Multiply(double[] m1, double[] m2)
		{
			return new[]
			{
				m1[0] * m2[0] + m1[1] * m2[2],
				m1[0] * m2[1] + m1[1] * m2[3],
				m1[2] * m2[0] + m1[3] * m2[2],
				m1[2] * m2[1] + m1[3] * m2[3],
				m1[4] * m2[0] + m1[5] * m2[2] + m2[4],
				m1[4] * m2[1] + m1[5] * m2[3] + m2[5]
			};
		}

		// returns the inverse of an invertible affine matrix.
		private static double[] Invert(double[] m)
		{
			var det = m[0] * m[3] - m[1] * m[2];
			return new[]
			{
				m[3] / det, -m[1] / det,
				-m[2] / det, m[0] / det,
				(m[2] * m[5] - m[3] * m[4]) / det,
				(m[1] * m[4] - m[0] * m[5]) / det
			};
		}

		// compares two matrices with a relative tolerance.
		private static bool SameMatrix(double[] a, double[] b)
		{
			for (int i = 0; i < 6; i++)
			{
				if (Math.Abs(a[i] - b[i]) > 1e-9 * Math.Max(1, Math.Abs(b[i])))
					return false;
			}

			return true;
		}

		#endregion
	}
}
