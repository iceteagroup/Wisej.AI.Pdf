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

// The predefined CMaps (ISO 32000-2, 9.7.5.2), loaded from the compressed Adobe cmap-resources data.

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Fonts
{
	/// <content>
	/// The predefined CMaps, loaded on demand from the embedded archive.
	/// </content>
	internal sealed partial class CMap
	{
		#region Fields

		// the logical name of the embedded archive with the predefined CMaps.
		internal const string PredefinedResourceName = "Wisej.AI.Pdf.Resources.CMaps.cmaps.bin";

		// the archive, opened on first use.
		private static readonly Lazy<CMapArchive> _predefinedArchive = new Lazy<CMapArchive>(() => CMapArchive.LoadResource(PredefinedResourceName));

		// the predefined CMaps loaded so far (null for a corrupt entry). Guarded by itself.
		private static readonly Dictionary<string, CMap> _predefined = new Dictionary<string, CMap>(StringComparer.Ordinal);

		#endregion

		#region Properties

		/// <summary>
		/// Returns the names of all the predefined CMaps available, excluding Identity-H and Identity-V.
		/// </summary>
		public static ICollection<string> PredefinedNames => _predefinedArchive.Value.Names;

		#endregion

		#region Implementation

		// returns a predefined CMap; depth guards usecmap chains.
		private static CMap GetPredefined(string name, int depth)
		{
			if (string.IsNullOrEmpty(name))
				return null;

			switch (name)
			{
				case "Identity-H":
					return _identityH;

				case "Identity-V":
					return _identityV;
			}

			// unknown names are not cached, so arbitrary names from documents can't grow the cache.
			if (depth > MaxUseCMapDepth || !_predefinedArchive.Value.Contains(name))
				return null;

			lock (_predefined)
			{
				if (_predefined.TryGetValue(name, out var cached))
					return cached;
			}

			// decode outside the lock; the parent is loaded (and cached) recursively.
			var data = _predefinedArchive.Value.Read(name);
			var map = data != null ? Deserialize(data, depth) : null;

			lock (_predefined)
			{
				// another thread may have won the race: keep its instance.
				if (_predefined.TryGetValue(name, out var cached))
					return cached;

				_predefined[name] = map;
				return map;
			}
		}

		/// <summary>
		/// Writes the CMap in the compact binary format of the predefined CMap archive.
		/// </summary>
		/// <returns>The uncompressed bytes.</returns>
		/// <remarks>
		/// <para>Layout (see <see cref="CMapDataWriter"/> for the primitive encodings):</para>
		/// <code>
		/// name, wmode (byte), usecmap name ("" for none), registry, ordering, supplement
		/// codespace count, { length (byte), low, high }
		/// cid table, notdef table
		/// </code>
		/// <para>
		/// A table is a range count followed by, for each range sorted by key:
		/// the gap from the end of the previous range, the span (end - start), and the zigzag
		/// difference between the value and the value predicted by continuing the previous range.
		/// Most entries in the Adobe CMaps continue the previous range, so the data is mostly small
		/// varints that Deflate compresses very well.
		/// </para>
		/// <para>
		/// The mappings inherited through usecmap are not included: they are loaded from the parent at runtime.
		/// </para>
		/// </remarks>
		internal byte[] Serialize()
		{
			var writer = new CMapDataWriter();
			writer.WriteString(this.Name);
			writer.WriteByte(this.WMode);
			writer.WriteString(this.UseCMap ?? (_parent != null ? _parent.Name : string.Empty));
			writer.WriteString(this.Registry);
			writer.WriteString(this.Ordering);
			writer.WriteVarInt(this.Supplement);

			// only the codespace ranges defined by this CMap: the parent ones are merged at load time.
			var inherited = _parent != null ? _parent.CodespaceRangeCount : 0;
			writer.WriteVarInt(_codespaceLengths.Length - inherited);
			for (int i = inherited; i < _codespaceLengths.Length; i++)
			{
				writer.WriteByte(_codespaceLengths[i]);
				writer.WriteVarInt(_codespaceLows[i]);
				writer.WriteVarInt(_codespaceHighs[i]);
			}

			WriteTable(writer, _cids);
			WriteTable(writer, _notdefs);
			return writer.ToArray();
		}

		// writes a range table with delta coding.
		private static void WriteTable(CMapDataWriter writer, CMapRangeTable table)
		{
			var ranges = table.Ranges;
			writer.WriteVarInt(ranges.Length);

			var previousEnd = -1L;
			var predicted = 0L;
			foreach (var range in ranges)
			{
				var value = (long)range.Value + range.Delta;
				writer.WriteVarInt(range.Start - previousEnd - 1);
				writer.WriteVarInt(range.End - range.Start);
				writer.WriteSignedVarInt(value - predicted);

				previousEnd = range.End;
				predicted = value + (range.End - range.Start) + 1;
			}
		}

		// reads a range table written by WriteTable.
		private static void ReadTable(ref CMapDataReader reader, CMapRangeTable table)
		{
			var count = reader.ReadVarInt();
			var ranges = new List<CMapRangeTable.Range>(Math.Min(count, 1 << 16));

			var previousEnd = -1L;
			var predicted = 0L;
			for (int i = 0; i < count && !reader.IsEnd; i++)
			{
				var start = previousEnd + 1 + reader.ReadVarLong();
				var end = start + reader.ReadVarLong();
				var value = predicted + reader.ReadSignedVarLong();

				ranges.Add(new CMapRangeTable.Range { Start = start, End = end, Value = (int)value });
				previousEnd = end;
				predicted = value + (end - start) + 1;
			}

			table.SetSorted(ranges.ToArray());
		}

		// decodes a CMap written by Serialize, resolving its usecmap parent.
		private static CMap Deserialize(byte[] data, int depth)
		{
			var reader = new CMapDataReader(data, 0);
			var map = new CMap();
			map.Name = reader.ReadString();
			map.WMode = reader.ReadByte() == 1 ? 1 : 0;
			map._wModeDefined = true;

			var useCMap = reader.ReadString();
			map.Registry = reader.ReadString();
			map.Ordering = reader.ReadString();
			map.Supplement = reader.ReadVarInt();

			var count = reader.ReadVarInt();
			var codespaces = new List<CodespaceRange>();
			for (int i = 0; i < count && !reader.IsEnd; i++)
			{
				var length = reader.ReadByte();
				var low = (uint)reader.ReadVarLong();
				var high = (uint)reader.ReadVarLong();
				if (length >= 1 && length <= MaxCodeLength)
					codespaces.Add(new CodespaceRange(length, low, high));
			}

			ReadTable(ref reader, map._cids);
			ReadTable(ref reader, map._notdefs);
			map._unicode.SetSorted(null);

			CMap parent = null;
			if (useCMap.Length > 0)
			{
				map.UseCMap = useCMap;
				parent = GetPredefined(useCMap, depth + 1);
			}

			map.Complete(codespaces, parent, null);
			return map;
		}

		#endregion
	}
}
