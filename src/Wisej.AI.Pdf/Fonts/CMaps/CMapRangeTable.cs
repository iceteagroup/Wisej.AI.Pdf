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

// Sorted table of disjoint code ranges used by the CMap mappings (ISO 32000-2, 9.7.5).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// Maps character code keys to values through a sorted list of disjoint ranges.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A key combines the code value and, when relevant, its byte length (see <see cref="MakeKey"/>),
	/// so that <c>&lt;20&gt;</c> and <c>&lt;0020&gt;</c> remain distinct codes.
	/// </para>
	/// <para>
	/// Each range maps <c>[Start, End]</c> to <c>Value</c> plus the offset of the key from the start of the
	/// range, adjusted by <c>Delta</c>. Ranges added later override earlier ones, which is the behaviour
	/// of a CMap that redefines a mapping or overrides one inherited through <c>usecmap</c>.
	/// </para>
	/// <para>
	/// The table is built once and is read-only afterwards, so lookups are thread-safe.
	/// </para>
	/// </remarks>
	internal sealed class CMapRangeTable
	{
		#region Nested types

		/// <summary>
		/// One range of consecutive keys.
		/// </summary>
		public struct Range
		{
			/// <summary>
			/// The first key of the range.
			/// </summary>
			public long Start;

			/// <summary>
			/// The last key of the range (inclusive).
			/// </summary>
			public long End;

			/// <summary>
			/// The value associated with the range: a CID, or the index of a destination string.
			/// </summary>
			public int Value;

			/// <summary>
			/// The offset added to <c>key - Start</c>; it is non-zero only for ranges that were trimmed
			/// on the left by an overriding definition.
			/// </summary>
			public int Delta;
		}

		#endregion

		#region Fields

		// the ranges in insertion order, until Build() sorts and resolves them.
		private List<Range> _pending = new List<Range>();

		// the final disjoint ranges, sorted by start.
		private Range[] _ranges = Array.Empty<Range>();

		#endregion

		#region Properties

		/// <summary>
		/// Returns the number of ranges in the built table.
		/// </summary>
		public int Count => _ranges.Length;

		/// <summary>
		/// Returns the built ranges, sorted by start key. The array must not be modified.
		/// </summary>
		public Range[] Ranges => _ranges;

		#endregion

		#region Methods

		/// <summary>
		/// Combines a code and its byte length into a lookup key.
		/// </summary>
		/// <param name="code">The code value, treated as an unsigned 32-bit number.</param>
		/// <param name="length">The code length in bytes, or 0 when the length is not significant.</param>
		/// <returns>The key.</returns>
		public static long MakeKey(int code, int length)
		{
			return ((long)length << 32) | (uint)code;
		}

		/// <summary>
		/// Adds a range. Ranges added later take precedence over earlier ones.
		/// </summary>
		/// <param name="start">The first key.</param>
		/// <param name="end">The last key (inclusive).</param>
		/// <param name="value">The value mapped to <paramref name="start"/>.</param>
		public void Add(long start, long end, int value)
		{
			if (end < start || _pending == null)
				return;

			_pending.Add(new Range { Start = start, End = end, Value = value });
		}

		/// <summary>
		/// Replaces the content of the table with ranges that are already sorted and disjoint.
		/// </summary>
		/// <param name="ranges">The sorted, disjoint ranges.</param>
		public void SetSorted(Range[] ranges)
		{
			_pending = null;
			_ranges = ranges ?? Array.Empty<Range>();
		}

		/// <summary>
		/// Sorts the ranges and resolves overlaps, giving precedence to later definitions.
		/// </summary>
		public void Build()
		{
			if (_pending == null)
				return;

			var pending = _pending;
			_pending = null;

			if (pending.Count == 0)
				return;

			// fast path: sort a copy (stable on insertion order) and check that no ranges overlap,
			// which is the case for virtually every CMap.
			var sorted = new Range[pending.Count];
			pending.CopyTo(sorted);
			var order = new int[sorted.Length];
			for (int i = 0; i < order.Length; i++)
				order[i] = i;

			Array.Sort(order, (a, b) =>
			{
				var c = sorted[a].Start.CompareTo(sorted[b].Start);
				return c != 0 ? c : a.CompareTo(b);
			});

			var result = new Range[sorted.Length];
			var overlaps = false;
			for (int i = 0; i < order.Length; i++)
			{
				result[i] = sorted[order[i]];
				if (i > 0 && result[i].Start <= result[i - 1].End)
					overlaps = true;
			}

			if (!overlaps)
			{
				_ranges = result;
				return;
			}

			_ranges = Resolve(pending);
		}

		/// <summary>
		/// Looks up <paramref name="key"/>.
		/// </summary>
		/// <param name="key">The key built by <see cref="MakeKey"/>.</param>
		/// <param name="value">Returns the value of the matching range.</param>
		/// <param name="offset">Returns the offset of the key within the range, including the range delta.</param>
		/// <returns>True when a range contains the key.</returns>
		public bool TryGet(long key, out int value, out int offset)
		{
			var ranges = _ranges;
			int lo = 0, hi = ranges.Length - 1;
			while (lo <= hi)
			{
				var mid = (lo + hi) >> 1;
				if (ranges[mid].Start > key)
				{
					hi = mid - 1;
				}
				else if (ranges[mid].End < key)
				{
					lo = mid + 1;
				}
				else
				{
					value = ranges[mid].Value;
					offset = (int)(key - ranges[mid].Start) + ranges[mid].Delta;
					return true;
				}
			}

			value = 0;
			offset = 0;
			return false;
		}

		#endregion

		#region Implementation

		// resolves overlapping ranges with a sweep over the range boundaries: for every elementary
		// interval the covering range defined last wins. a max-heap on the definition order keeps
		// this O(n log n), which matters for broken ToUnicode maps with thousands of duplicates.
		private static Range[] Resolve(List<Range> pending)
		{
			var count = pending.Count;

			// definition indices sorted by start.
			var byStart = new int[count];
			for (int i = 0; i < count; i++)
				byStart[i] = i;

			Array.Sort(byStart, (a, b) => pending[a].Start.CompareTo(pending[b].Start));

			// all the distinct boundaries: every start and every end + 1.
			var bounds = new long[count * 2];
			for (int i = 0; i < count; i++)
			{
				bounds[i * 2] = pending[i].Start;
				bounds[i * 2 + 1] = pending[i].End + 1;
			}

			Array.Sort(bounds);

			var result = new List<Range>(count);
			var heap = new int[count];
			var heapSize = 0;
			var next = 0;
			var lastWinner = -1;

			for (int b = 0; b < bounds.Length; b++)
			{
				var point = bounds[b];
				if (b > 0 && point == bounds[b - 1])
					continue;

				// activate the ranges starting here.
				while (next < count && pending[byStart[next]].Start <= point)
					HeapPush(heap, ref heapSize, byStart[next++]);

				// drop the ranges that ended before this point.
				while (heapSize > 0 && pending[heap[0]].End < point)
					HeapPop(heap, ref heapSize);

				if (heapSize == 0)
				{
					lastWinner = -1;
					continue;
				}

				// the elementary interval runs to the next distinct boundary.
				var end = point;
				for (int k = b + 1; k < bounds.Length; k++)
				{
					if (bounds[k] != point)
					{
						end = bounds[k] - 1;
						break;
					}
				}

				var winner = heap[0];
				var source = pending[winner];
				if (winner == lastWinner && result.Count > 0 && result[result.Count - 1].End + 1 == point)
				{
					var merged = result[result.Count - 1];
					merged.End = end;
					result[result.Count - 1] = merged;
				}
				else
				{
					result.Add(new Range
					{
						Start = point,
						End = end,
						Value = source.Value,
						Delta = source.Delta + (int)(point - source.Start)
					});
				}

				lastWinner = winner;
			}

			return result.ToArray();
		}

		// pushes a definition index on the max-heap (higher index = defined later = higher priority).
		private static void HeapPush(int[] heap, ref int size, int item)
		{
			var i = size++;
			while (i > 0)
			{
				var parent = (i - 1) >> 1;
				if (heap[parent] >= item)
					break;

				heap[i] = heap[parent];
				i = parent;
			}

			heap[i] = item;
		}

		// removes the top of the max-heap.
		private static void HeapPop(int[] heap, ref int size)
		{
			var item = heap[--size];
			var i = 0;
			while (true)
			{
				var child = i * 2 + 1;
				if (child >= size)
					break;

				if (child + 1 < size && heap[child + 1] > heap[child])
					child++;

				if (heap[child] <= item)
					break;

				heap[i] = heap[child];
				i = child;
			}

			if (size > 0)
				heap[i] = item;
		}

		#endregion
	}
}
