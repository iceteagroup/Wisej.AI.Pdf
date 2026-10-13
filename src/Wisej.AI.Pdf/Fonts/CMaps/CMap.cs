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

// CMaps: character code to CID mapping and ToUnicode mapping (ISO 32000-2, 9.7.5, 9.7.6 and 9.10.3).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// Represents a CMap: the encoding of a composite (Type 0) font, mapping character codes to CIDs,
	/// or a ToUnicode CMap, mapping character codes to Unicode text.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Instances are created by <see cref="Parse(byte[])"/> (embedded CMap and ToUnicode streams),
	/// <see cref="GetPredefined(string)"/> (the predefined Adobe CMaps, loaded from embedded resources) and
	/// <see cref="CreateIdentity"/>.
	/// </para>
	/// <para>
	/// Character codes are handled as unsigned big-endian numbers of 1 to 4 bytes stored in an
	/// <see cref="int"/>: 4-byte codes starting with a byte &gt;= 0x80 (for example UTF-8 sequences)
	/// are negative when read as signed values.
	/// </para>
	/// <para>
	/// A CMap is immutable once created and all its members are thread-safe.
	/// </para>
	/// </remarks>
	internal sealed partial class CMap
	{
		#region Nested types

		/// <summary>
		/// One codespace range collected while parsing (ISO 32000-2, 9.7.6.2).
		/// </summary>
		private struct CodespaceRange
		{
			/// <summary>
			/// The length of the codes in bytes, 1 to 4.
			/// </summary>
			public readonly int Length;

			/// <summary>
			/// The lowest code, big-endian; each byte is the lower bound of the same byte of a code.
			/// </summary>
			public readonly uint Low;

			/// <summary>
			/// The highest code, big-endian; each byte is the upper bound of the same byte of a code.
			/// </summary>
			public readonly uint High;

			/// <summary>
			/// Initializes a codespace range.
			/// </summary>
			/// <param name="length">The length of the codes in bytes.</param>
			/// <param name="low">The lowest code.</param>
			/// <param name="high">The highest code.</param>
			public CodespaceRange(int length, uint low, uint high)
			{
				this.Length = length;
				this.Low = low;
				this.High = high;
			}
		}

		#endregion

		#region Fields

		// the maximum length of a character code (ISO 32000-2, 9.7.6.2).
		internal const int MaxCodeLength = 4;

		// the shared Identity-H CMap.
		private static readonly CMap _identityH = BuildIdentity(0);

		// the shared Identity-V CMap.
		private static readonly CMap _identityV = BuildIdentity(1);

		// the codespace ranges as parallel arrays: the code length in bytes of each range.
		private int[] _codespaceLengths = Array.Empty<int>();

		// the low bound of each codespace range; each byte bounds the same byte of a code.
		private uint[] _codespaceLows = Array.Empty<uint>();

		// the high bound of each codespace range.
		private uint[] _codespaceHighs = Array.Empty<uint>();

		// code -> CID mappings (cidrange/cidchar), keyed by code and length.
		private readonly CMapRangeTable _cids = new CMapRangeTable();

		// code -> notdef CID mappings (notdefrange/notdefchar), keyed by code and length.
		private readonly CMapRangeTable _notdefs = new CMapRangeTable();

		// code -> Unicode mappings (bfrange/bfchar), keyed by code only; the value indexes _unicodeStrings.
		private readonly CMapRangeTable _unicode = new CMapRangeTable();

		// destination strings of the bf mappings.
		private string[] _unicodeStrings = Array.Empty<string>();

		// cache of the strings computed for codes inside a bfrange, to avoid allocating per glyph.
		private ConcurrentDictionary<int, string> _unicodeCache;

		// the CMap referenced by usecmap, whose mappings apply when this one has none.
		private CMap _parent;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes an empty CMap.
		/// </summary>
		private CMap()
		{
			this.Name = string.Empty;
			this.Registry = string.Empty;
			this.Ordering = string.Empty;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the CMap name (<c>/CMapName</c>), or an empty string when not specified.
		/// </summary>
		public string Name { get; private set; }

		/// <summary>
		/// Returns the writing mode: 0 for horizontal, 1 for vertical.
		/// </summary>
		public int WMode { get; private set; }

		/// <summary>
		/// Returns true for the Identity-H and Identity-V CMaps: 2-byte codes where the CID is the code.
		/// </summary>
		public bool IsIdentity { get; private set; }

		/// <summary>
		/// Returns the registry of the character collection (<c>/CIDSystemInfo /Registry</c>), or an empty string.
		/// </summary>
		public string Registry { get; private set; }

		/// <summary>
		/// Returns the ordering of the character collection (<c>/CIDSystemInfo /Ordering</c>), or an empty string.
		/// </summary>
		public string Ordering { get; private set; }

		/// <summary>
		/// Returns the supplement of the character collection (<c>/CIDSystemInfo /Supplement</c>).
		/// </summary>
		public int Supplement { get; private set; }

		/// <summary>
		/// Returns the name of the CMap referenced by <c>usecmap</c>, or null.
		/// </summary>
		public string UseCMap { get; private set; }

		/// <summary>
		/// Returns true when the CMap (or the one it uses) defines code to CID mappings.
		/// </summary>
		public bool HasCidMappings => _cids.Count > 0 || this.IsIdentity || (_parent != null && _parent.HasCidMappings);

		/// <summary>
		/// Returns true when the CMap defines code to Unicode mappings (a ToUnicode CMap).
		/// </summary>
		public bool HasUnicodeMappings => _unicode.Count > 0 || (_parent != null && _parent.HasUnicodeMappings);

		/// <summary>
		/// Returns the number of codespace ranges.
		/// </summary>
		public int CodespaceRangeCount => _codespaceLengths.Length;

		/// <summary>
		/// Returns the length in bytes of the shortest codespace range, or 1 when there are none.
		/// </summary>
		public int MinCodeLength { get; private set; } = 1;

		/// <summary>
		/// Returns the length in bytes of the longest codespace range, or 1 when there are none.
		/// </summary>
		public int MaxCodeLengthInUse { get; private set; } = 1;

		#endregion

		#region Methods

		/// <summary>
		/// Reads one character code from <paramref name="bytes"/>, using the codespace ranges.
		/// </summary>
		/// <param name="bytes">The string bytes.</param>
		/// <param name="offset">The offset of the first byte of the code.</param>
		/// <param name="length">Returns the number of bytes consumed, always at least 1 when <paramref name="offset"/> is within the array.</param>
		/// <returns>The character code.</returns>
		/// <remarks>
		/// Codes that don't match any codespace range follow ISO 32000-2, 9.7.6.3: the number of bytes
		/// consumed is the length of the shortest codespace range whose first byte matches, or 1.
		/// </remarks>
		public int ReadCode(byte[] bytes, int offset, out int length)
		{
			if (bytes == null || offset < 0 || offset >= bytes.Length)
			{
				length = bytes != null && offset < bytes.Length ? 1 : 0;
				return 0;
			}

			var available = bytes.Length - offset;

			// identity: always 2 bytes (a trailing odd byte is consumed alone).
			if (this.IsIdentity)
			{
				if (available >= 2)
				{
					length = 2;
					return (bytes[offset] << 8) | bytes[offset + 1];
				}

				length = 1;
				return bytes[offset];
			}

			var lengths = _codespaceLengths;
			var lows = _codespaceLows;
			var highs = _codespaceHighs;
			if (lengths.Length == 0)
			{
				length = 1;
				return bytes[offset];
			}

			// accumulate bytes until the code matches a codespace range of the same length.
			var code = 0;
			var max = Math.Min(this.MaxCodeLengthInUse, available);
			for (int n = 1; n <= max; n++)
			{
				code = (code << 8) | bytes[offset + n - 1];
				for (int r = 0; r < lengths.Length; r++)
				{
					if (lengths[r] == n && Matches((uint)code, lows[r], highs[r], n))
					{
						length = n;
						return code;
					}
				}
			}

			// no full match: consume the length of the shortest range whose first byte matches.
			var first = bytes[offset];
			var partial = int.MaxValue;
			for (int r = 0; r < lengths.Length; r++)
			{
				var shift = (lengths[r] - 1) * 8;
				var lo = (lows[r] >> shift) & 0xFF;
				var hi = (highs[r] >> shift) & 0xFF;
				if (first >= lo && first <= hi && lengths[r] < partial)
					partial = lengths[r];
			}

			if (partial == int.MaxValue)
				partial = 1;

			length = Math.Min(partial, available);
			code = 0;
			for (int i = 0; i < length; i++)
				code = (code << 8) | bytes[offset + i];

			return code;
		}

		/// <summary>
		/// Returns the CID for a character code, determining the code length from the codespace ranges.
		/// </summary>
		/// <param name="code">The character code returned by <see cref="ReadCode"/>.</param>
		/// <returns>The CID; the notdef CID when the code falls in a notdef range; otherwise 0.</returns>
		public int GetCid(int code)
		{
			if (this.IsIdentity)
				return code & 0xFFFF;

			return GetCid(code, GetCodeLength(code));
		}

		/// <summary>
		/// Returns the CID for a character code of a known byte length.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <param name="length">The code length in bytes, as returned by <see cref="ReadCode"/>.</param>
		/// <returns>The CID; the notdef CID when the code falls in a notdef range; otherwise 0.</returns>
		public int GetCid(int code, int length)
		{
			if (this.IsIdentity)
				return code & 0xFFFF;

			var key = CMapRangeTable.MakeKey(code, length);

			// the mappings of this CMap override those inherited through usecmap.
			var depth = 0;
			for (var map = this; map != null && depth < 16; map = map._parent, depth++)
			{
				if (map.IsIdentity && length == 2)
					return code & 0xFFFF;

				if (map._cids.TryGet(key, out var cid, out var offset))
					return cid + offset;
			}

			depth = 0;
			for (var map = this; map != null && depth < 16; map = map._parent, depth++)
			{
				if (map._notdefs.TryGet(key, out var cid, out _))
					return cid;
			}

			return 0;
		}

		/// <summary>
		/// Returns the Unicode text that a ToUnicode CMap associates to a character code.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <param name="text">Returns the text, or null.</param>
		/// <returns>True when the code is mapped.</returns>
		public bool TryGetUnicode(int code, out string text)
		{
			var depth = 0;
			for (var map = this; map != null && depth < 16; map = map._parent, depth++)
			{
				if (map._unicode.Count > 0 && map._unicode.TryGet(CMapRangeTable.MakeKey(code, 0), out var index, out var offset))
				{
					text = map.GetUnicodeString(code, index, offset);
					return text != null;
				}
			}

			text = null;
			return false;
		}

		/// <summary>
		/// Returns a string representation of the CMap for debugging.
		/// </summary>
		/// <returns>The name and the size of the tables.</returns>
		public override string ToString()
		{
			return $"CMap {this.Name} (WMode {this.WMode}, {_cids.Count} cid ranges, {_unicode.Count} unicode ranges)";
		}

		#endregion

		#region Factory

		/// <summary>
		/// Parses an embedded CMap or a ToUnicode CMap stream.
		/// </summary>
		/// <param name="data">The decoded stream bytes.</param>
		/// <returns>The CMap; an empty CMap when the data is null or unreadable. Never null.</returns>
		/// <remarks>
		/// A <c>usecmap</c> operator referencing a predefined CMap is resolved with <see cref="GetPredefined(string)"/>.
		/// </remarks>
		public static CMap Parse(byte[] data)
		{
			return Parse(data, 0);
		}

		/// <summary>
		/// Returns the predefined CMap with the given name.
		/// </summary>
		/// <param name="name">The CMap name, for example <c>Identity-H</c> or <c>90ms-RKSJ-H</c>.</param>
		/// <returns>The CMap, or null when the name is not a predefined CMap.</returns>
		/// <remarks>
		/// The predefined CMaps are loaded on demand from a compressed embedded resource and cached.
		/// </remarks>
		public static CMap GetPredefined(string name)
		{
			return GetPredefined(name, 0);
		}

		/// <summary>
		/// Creates an Identity CMap: 2-byte codes mapped to the CID with the same value.
		/// </summary>
		/// <param name="wMode">The writing mode: 0 for Identity-H, 1 for Identity-V.</param>
		/// <returns>The shared Identity CMap instance.</returns>
		public static CMap CreateIdentity(int wMode)
		{
			return wMode == 1 ? _identityV : _identityH;
		}

		// builds an identity CMap with the 2-byte codespace <0000> <FFFF>.
		private static CMap BuildIdentity(int wMode)
		{
			var map = new CMap
			{
				Name = wMode == 1 ? "Identity-V" : "Identity-H",
				WMode = wMode,
				IsIdentity = true,
				Registry = "Adobe",
				Ordering = "Identity"
			};

			map.SetCodespaces(new List<CodespaceRange> { new CodespaceRange(2, 0x0000, 0xFFFF) });
			map._cids.Build();
			map._notdefs.Build();
			map._unicode.Build();
			return map;
		}

		#endregion

		#region Implementation

		// checks every byte of the code against the bounds of the same byte in the range.
		private static bool Matches(uint code, uint low, uint high, int length)
		{
			for (int i = 0, shift = (length - 1) * 8; i < length; i++, shift -= 8)
			{
				var b = (code >> shift) & 0xFF;
				if (b < ((low >> shift) & 0xFF) || b > ((high >> shift) & 0xFF))
					return false;
			}

			return true;
		}

		// returns the byte length of the shortest codespace range that contains the code value.
		private int GetCodeLength(int code)
		{
			var lengths = _codespaceLengths;
			var best = 0;
			for (int r = 0; r < lengths.Length; r++)
			{
				var n = lengths[r];
				if (n < 4 && (uint)code >= (1u << (n * 8)))
					continue;

				if ((best == 0 || n < best) && Matches((uint)code, _codespaceLows[r], _codespaceHighs[r], n))
					best = n;
			}

			if (best != 0)
				return best;

			// not in any codespace: guess from the magnitude of the value.
			var value = (uint)code;
			return value <= 0xFF ? Math.Max(1, this.MinCodeLength) : value <= 0xFFFF ? 2 : value <= 0xFFFFFF ? 3 : 4;
		}

		// stores the codespace ranges in flat arrays and computes the code length bounds.
		private void SetCodespaces(List<CodespaceRange> ranges)
		{
			var count = ranges.Count;
			_codespaceLengths = new int[count];
			_codespaceLows = new uint[count];
			_codespaceHighs = new uint[count];

			var min = int.MaxValue;
			var max = 0;
			for (int i = 0; i < count; i++)
			{
				_codespaceLengths[i] = ranges[i].Length;
				_codespaceLows[i] = ranges[i].Low;
				_codespaceHighs[i] = ranges[i].High;
				min = Math.Min(min, ranges[i].Length);
				max = Math.Max(max, ranges[i].Length);
			}

			this.MinCodeLength = count == 0 ? 1 : min;
			this.MaxCodeLengthInUse = count == 0 ? 1 : max;
		}

		// returns the codespace ranges as a list, to merge them with a child CMap.
		private List<CodespaceRange> GetCodespaces()
		{
			var list = new List<CodespaceRange>(_codespaceLengths.Length);
			for (int i = 0; i < _codespaceLengths.Length; i++)
				list.Add(new CodespaceRange(_codespaceLengths[i], _codespaceLows[i], _codespaceHighs[i]));

			return list;
		}

		// computes the destination string of a bf mapping, incrementing the base string of a bfrange.
		private string GetUnicodeString(int code, int index, int offset)
		{
			var strings = _unicodeStrings;
			if (index < 0 || index >= strings.Length)
				return null;

			var text = strings[index];
			if (offset == 0 || string.IsNullOrEmpty(text))
				return text;

			var cache = _unicodeCache;
			if (cache == null)
			{
				cache = new ConcurrentDictionary<int, string>();
				cache = System.Threading.Interlocked.CompareExchange(ref _unicodeCache, cache, null) ?? cache;
			}

			if (cache.TryGetValue(code, out var cached))
				return cached;

			var result = IncrementString(text, offset);

			// bound the cache: a pathological bfrange can cover millions of codes.
			if (cache.Count < 65536)
				cache[code] = result;

			return result;
		}

		/// <summary>
		/// Increments the last character of a bfrange destination string.
		/// </summary>
		/// <param name="text">The destination string of the first code in the range.</param>
		/// <param name="offset">The offset of the code from the start of the range.</param>
		/// <returns>The destination string of the code.</returns>
		/// <remarks>
		/// The specification increments only the last byte of the destination, so ranges should not
		/// cross a 256-code boundary. Producers ignore that rule, and readers (Acrobat, pdf.js, PDFBox)
		/// carry into the previous byte, so the last character is incremented as a whole. When the
		/// destination is a single supplementary character (a surrogate pair), its code point is
		/// incremented so the surrogates roll over correctly.
		/// </remarks>
		internal static string IncrementString(string text, int offset)
		{
			if (text.Length == 1 || (text.Length == 2 && char.IsHighSurrogate(text[0]) && char.IsLowSurrogate(text[1])))
			{
				var codePoint = text.Length == 1 ? text[0] : char.ConvertToUtf32(text[0], text[1]);
				var value = (long)codePoint + offset;
				if (value < 0 || value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF))
					return text.Length == 1 && value >= 0 && value <= 0xFFFF ? ((char)value).ToString() : "�";

				return value <= 0xFFFF ? ((char)value).ToString() : char.ConvertFromUtf32((int)value);
			}

			var chars = text.ToCharArray();
			chars[chars.Length - 1] = (char)(chars[chars.Length - 1] + offset);
			return new string(chars);
		}

		#endregion
	}
}
