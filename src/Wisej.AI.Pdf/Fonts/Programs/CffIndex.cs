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

// CFF INDEX structure and DICT data decoding (Adobe Technical Note #5176, sections 5 and 4).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// A CFF INDEX: an array of variable-sized objects stored contiguously.
	/// </summary>
	internal sealed class CffIndex
	{
		#region Fields

		/// <summary>
		/// The shared empty INDEX.
		/// </summary>
		public static readonly CffIndex Empty = new CffIndex(new int[] { 0 }, 0);

		// absolute start offsets of the objects, plus the end of the last one.
		private readonly int[] _offsets;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes an INDEX from its absolute object offsets.
		/// </summary>
		/// <param name="offsets">The absolute start of each object, followed by the end of the last.</param>
		/// <param name="end">The offset of the first byte after the INDEX.</param>
		private CffIndex(int[] offsets, int end)
		{
			_offsets = offsets;
			this.End = end;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the number of objects.
		/// </summary>
		public int Count => _offsets.Length - 1;

		/// <summary>
		/// Returns the offset of the first byte after the INDEX.
		/// </summary>
		public int End { get; }

		#endregion

		#region Methods

		/// <summary>
		/// Reads an INDEX at <paramref name="offset"/>.
		/// </summary>
		/// <param name="data">The CFF data.</param>
		/// <param name="offset">The position of the INDEX.</param>
		/// <returns>The INDEX; <see cref="Empty"/> when the data is missing or invalid.</returns>
		public static CffIndex Read(byte[] data, int offset)
		{
			if (offset < 0 || offset + 2 > data.Length)
				return Empty;

			var count = BigEndian.U16(data, offset);
			if (count == 0)
				return new CffIndex(new int[] { offset + 2 }, offset + 2);

			var offSize = BigEndian.U8(data, offset + 2);
			if (offSize < 1 || offSize > 4)
				return Empty;

			var table = offset + 3;
			if (table + (long)(count + 1) * offSize > data.Length)
				return Empty;

			// object offsets are 1-based, relative to the byte before the object data.
			var dataStart = table + (count + 1) * offSize - 1;
			var offsets = new int[count + 1];
			var previous = dataStart + 1;
			for (int i = 0; i <= count; i++)
			{
				var value = (long)dataStart + BigEndian.UVar(data, table + i * offSize, offSize);

				// clamp to the data and keep the offsets non-decreasing, so broken entries become empty objects.
				if (value > data.Length)
					value = data.Length;

				if (value < previous)
					value = previous;

				offsets[i] = (int)value;
				previous = (int)value;
			}

			return new CffIndex(offsets, offsets[count]);
		}

		/// <summary>
		/// Returns the byte range of an object.
		/// </summary>
		/// <param name="index">The object index.</param>
		/// <param name="start">Receives the absolute start offset.</param>
		/// <param name="end">Receives the absolute end offset (exclusive).</param>
		/// <returns>False when <paramref name="index"/> is out of range.</returns>
		public bool TryGetRange(int index, out int start, out int end)
		{
			if (index < 0 || index >= this.Count)
			{
				start = end = 0;
				return false;
			}

			start = _offsets[index];
			end = _offsets[index + 1];
			return true;
		}

		/// <summary>
		/// Returns an object decoded as Latin-1 text.
		/// </summary>
		/// <param name="data">The CFF data.</param>
		/// <param name="index">The object index.</param>
		/// <returns>The text, or null when the index is out of range.</returns>
		public string GetString(byte[] data, int index)
		{
			if (!TryGetRange(index, out var start, out var end))
				return null;

			var builder = new StringBuilder(end - start);
			for (int i = start; i < end; i++)
				builder.Append((char)data[i]);

			return builder.ToString();
		}

		#endregion
	}

	/// <summary>
	/// A decoded CFF DICT: operator to operands.
	/// </summary>
	internal sealed class CffDict
	{
		#region Fields

		// operands by operator; escaped (two-byte) operators are stored as 1200 + the second byte.
		private readonly Dictionary<int, double[]> _entries = new Dictionary<int, double[]>();

		#endregion

		#region Methods

		/// <summary>
		/// Decodes the DICT data in [<paramref name="start"/>, <paramref name="end"/>).
		/// </summary>
		/// <param name="data">The CFF data.</param>
		/// <param name="start">The first byte.</param>
		/// <param name="end">The end of the DICT (exclusive).</param>
		/// <returns>The DICT; entries decoded before an error are kept.</returns>
		public static CffDict Read(byte[] data, int start, int end)
		{
			var dict = new CffDict();
			end = Math.Min(end, data.Length);
			var operands = new List<double>();
			var p = Math.Max(0, start);
			while (p < end)
			{
				int b0 = data[p++];
				if (b0 <= 21)
				{
					var op = b0;
					if (b0 == 12)
						op = 1200 + BigEndian.U8(data, p++);

					dict._entries[op] = operands.ToArray();
					operands.Clear();
				}
				else if (b0 == 28)
				{
					operands.Add(BigEndian.S16(data, p));
					p += 2;
				}
				else if (b0 == 29)
				{
					operands.Add(BigEndian.S32(data, p));
					p += 4;
				}
				else if (b0 == 30)
				{
					operands.Add(ReadReal(data, ref p, end));
				}
				else if (b0 >= 32 && b0 <= 246)
				{
					operands.Add(b0 - 139);
				}
				else if (b0 >= 247 && b0 <= 250)
				{
					operands.Add((b0 - 247) * 256 + BigEndian.U8(data, p++) + 108);
				}
				else if (b0 >= 251 && b0 <= 254)
				{
					operands.Add(-(b0 - 251) * 256 - BigEndian.U8(data, p++) - 108);
				}

				// bytes 22-27, 31 and 255 are reserved: skip them.
				if (operands.Count > 48)
					operands.RemoveAt(0);
			}

			return dict;
		}

		/// <summary>
		/// Returns true when the DICT contains <paramref name="op"/>.
		/// </summary>
		/// <param name="op">The operator (1200 + n for escaped operators).</param>
		/// <returns>True when present.</returns>
		public bool Has(int op) => _entries.ContainsKey(op);

		/// <summary>
		/// Returns the operands of an operator.
		/// </summary>
		/// <param name="op">The operator.</param>
		/// <returns>The operands, or null when absent.</returns>
		public double[] Get(int op) => _entries.TryGetValue(op, out var value) ? value : null;

		/// <summary>
		/// Returns the first operand of an operator.
		/// </summary>
		/// <param name="op">The operator.</param>
		/// <param name="defaultValue">The value returned when the operator is absent.</param>
		/// <returns>The value.</returns>
		public double GetNumber(int op, double defaultValue)
		{
			var values = Get(op);
			return values != null && values.Length > 0 ? values[0] : defaultValue;
		}

		/// <summary>
		/// Returns the first operand of an operator as an integer.
		/// </summary>
		/// <param name="op">The operator.</param>
		/// <param name="defaultValue">The value returned when the operator is absent.</param>
		/// <returns>The value.</returns>
		public int GetInt(int op, int defaultValue)
		{
			var value = GetNumber(op, defaultValue);
			return value >= int.MinValue && value <= int.MaxValue ? (int)value : defaultValue;
		}

		#endregion

		#region Implementation

		// decodes a real number: a sequence of nibbles terminated by 0xF.
		private static double ReadReal(byte[] data, ref int p, int end)
		{
			var text = new StringBuilder();
			var done = false;
			while (!done && p < end)
			{
				var b = data[p++];
				for (int shift = 4; shift >= 0; shift -= 4)
				{
					var nibble = (b >> shift) & 0xF;
					switch (nibble)
					{
						case 0xA:
							text.Append('.');
							break;

						case 0xB:
							text.Append('E');
							break;

						case 0xC:
							text.Append("E-");
							break;

						case 0xD:
							// reserved nibble: ignored.
							break;

						case 0xE:
							text.Append('-');
							break;

						case 0xF:
							done = true;
							break;

						default:
							text.Append((char)('0' + nibble));
							break;
					}

					if (done)
						break;
				}
			}

			return double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
		}

		#endregion
	}
}
