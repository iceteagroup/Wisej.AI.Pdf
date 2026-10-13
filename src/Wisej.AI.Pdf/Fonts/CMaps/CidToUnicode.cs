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

// CID to Unicode mapping of the Adobe character collections (ISO 32000-2, 9.10.2, method c).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// Maps the CIDs of the Adobe CJK character collections to Unicode text.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the fallback of ISO 32000-2, 9.10.2 for composite fonts without a ToUnicode CMap whose
	/// CIDSystemInfo names one of the Adobe-Japan1, Adobe-GB1, Adobe-CNS1, Adobe-Korea1, Adobe-KR
	/// or Adobe-Manga1 collections.
	/// </para>
	/// <para>
	/// The tables come from Adobe's <c>mapping-resources-pdf</c> (the <c>Adobe-*-UCS2</c> ToUnicode maps),
	/// completed with the inverted <c>Uni*-UTF32</c> CMaps of <c>cmap-resources</c> for the CIDs they don't
	/// cover. They are decoded on first use for each collection and cached. All members are thread-safe.
	/// </para>
	/// </remarks>
	internal static class CidToUnicode
	{
		#region Fields

		// the logical name of the embedded archive with the tables.
		internal const string ResourceName = "Wisej.AI.Pdf.Resources.CMaps.cid2unicode.bin";

		// the archive, opened on first use.
		private static readonly Lazy<CMapArchive> _archive = new Lazy<CMapArchive>(() => CMapArchive.LoadResource(ResourceName));

		// the decoded tables by ordering (null for a corrupt entry). Guarded by itself.
		private static readonly Dictionary<string, Table> _tables = new Dictionary<string, Table>(StringComparer.Ordinal);

		#endregion

		#region Nested types

		/// <summary>
		/// The decoded table of one character collection.
		/// </summary>
		private sealed class Table
		{
			/// <summary>
			/// The code point of each CID, 0 when unmapped, -1 when the text is in <see cref="Sequences"/>.
			/// </summary>
			public int[] CodePoints;

			/// <summary>
			/// The CIDs mapped to more than one code point.
			/// </summary>
			public Dictionary<int, string> Sequences;

			/// <summary>
			/// The strings created so far, so repeated lookups don't allocate. Races only create duplicates.
			/// </summary>
			public string[] Strings;
		}

		#endregion

		#region Methods

		/// <summary>
		/// Returns the Unicode text of a CID in an Adobe character collection.
		/// </summary>
		/// <param name="registry">The registry, which must be <c>Adobe</c>.</param>
		/// <param name="ordering">The ordering: <c>Japan1</c>, <c>GB1</c>, <c>CNS1</c>, <c>Korea1</c>, <c>KR</c> or <c>Manga1</c>.</param>
		/// <param name="cid">The CID.</param>
		/// <returns>The text, or null when the collection or the CID is unknown.</returns>
		public static string Map(string registry, string ordering, int cid)
		{
			if (cid <= 0 || registry != "Adobe")
				return null;

			var table = GetTable(ordering);
			if (table == null || cid >= table.CodePoints.Length)
				return null;

			var text = table.Strings[cid];
			if (text != null)
				return text;

			var codePoint = table.CodePoints[cid];
			if (codePoint == 0)
				return null;

			if (codePoint < 0)
				table.Sequences.TryGetValue(cid, out text);
			else
				text = codePoint <= 0xFFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint);

			table.Strings[cid] = text;
			return text;
		}

		/// <summary>
		/// Returns true when <see cref="Map"/> has a table for the character collection.
		/// </summary>
		/// <param name="registry">The registry.</param>
		/// <param name="ordering">The ordering.</param>
		/// <returns>True when the collection is supported.</returns>
		public static bool IsSupported(string registry, string ordering)
		{
			return registry == "Adobe" && GetTable(ordering) != null;
		}

		/// <summary>
		/// Returns the name of the predefined Unicode CMap of a character collection, used to map
		/// Unicode text to the CIDs of the collection (for example for non-embedded TrueType CID fonts).
		/// </summary>
		/// <param name="ordering">The ordering, for example <c>Japan1</c>.</param>
		/// <param name="vertical">True for the vertical writing mode.</param>
		/// <returns>The CMap name, for example <c>UniJIS-UCS2-H</c>, or null for unknown orderings.</returns>
		public static string GetUnicodeCMapName(string ordering, bool vertical = false)
		{
			string name;
			switch (ordering)
			{
				case "Japan1":
					name = "UniJIS-UCS2-";
					break;

				case "GB1":
					name = "UniGB-UCS2-";
					break;

				case "CNS1":
					name = "UniCNS-UCS2-";
					break;

				case "Korea1":
					name = "UniKS-UCS2-";
					break;

				case "Manga1":
					name = "UniManga-UTF16-";
					break;

				// Adobe-KR only has a horizontal UTF-16 CMap.
				case "KR": return "UniAKR-UTF16-H";

				default: return null;
			}

			return name + (vertical ? "V" : "H");
		}

		#endregion

		#region Implementation

		// returns the decoded table of an ordering, loading it on first use.
		private static Table GetTable(string ordering)
		{
			// unknown orderings are not cached, so arbitrary names from documents can't grow the cache.
			if (string.IsNullOrEmpty(ordering) || !_archive.Value.Contains(ordering))
				return null;

			lock (_tables)
			{
				if (_tables.TryGetValue(ordering, out var table))
					return table;
			}

			var data = _archive.Value.Read(ordering);
			var decoded = data != null ? Decode(data) : null;

			lock (_tables)
			{
				if (_tables.TryGetValue(ordering, out var table))
					return table;

				_tables[ordering] = decoded;
				return decoded;
			}
		}

		/// <summary>
		/// Decodes a table.
		/// </summary>
		/// <param name="data">The decompressed entry.</param>
		/// <returns>The table.</returns>
		/// <remarks>
		/// <para>Layout (varints; see <see cref="CMapDataWriter"/>):</para>
		/// <code>
		/// cidCount, runCount
		/// { gap, header, ... }      runs sorted by CID
		/// </code>
		/// <para>
		/// <c>gap</c> is the number of unmapped CIDs before the run. <c>header</c> is <c>length &lt;&lt; 1 | multi</c>.
		/// A single-code-point run maps consecutive CIDs to consecutive code points and is followed by the
		/// zigzag difference between its first code point and the one after the end of the previous run.
		/// A multi run has length 1 and is followed by the number of code points and the code points.
		/// </para>
		/// </remarks>
		private static Table Decode(byte[] data)
		{
			var reader = new CMapDataReader(data, 0);
			var cidCount = Math.Min(reader.ReadVarInt(), 1 << 20);
			var runCount = reader.ReadVarInt();

			var table = new Table
			{
				CodePoints = new int[cidCount],
				Sequences = new Dictionary<int, string>(),
				Strings = new string[cidCount]
			};

			var cid = 0;
			var predicted = 0L;
			for (int r = 0; r < runCount && !reader.IsEnd; r++)
			{
				cid += reader.ReadVarInt();
				var header = reader.ReadVarInt();
				var length = header >> 1;

				if ((header & 1) != 0)
				{
					var count = Math.Min(reader.ReadVarInt(), 64);
					var chars = new System.Text.StringBuilder(count * 2);
					for (int i = 0; i < count; i++)
						AppendCodePoint(chars, reader.ReadVarInt());

					if (cid < cidCount)
					{
						table.CodePoints[cid] = -1;
						table.Sequences[cid] = chars.ToString();
					}

					cid++;
					continue;
				}

				var codePoint = predicted + reader.ReadSignedVarLong();
				for (int i = 0; i < length; i++, cid++, codePoint++)
				{
					if (cid < cidCount && codePoint > 0 && codePoint <= 0x10FFFF)
						table.CodePoints[cid] = (int)codePoint;
				}

				predicted = codePoint;
			}

			return table;
		}

		// appends a code point as UTF-16, replacing invalid values.
		private static void AppendCodePoint(System.Text.StringBuilder builder, int codePoint)
		{
			if (codePoint < 0 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
				builder.Append('�');
			else if (codePoint <= 0xFFFF)
				builder.Append((char)codePoint);
			else
				builder.Append(char.ConvertFromUtf32(codePoint));
		}

		#endregion
	}
}
