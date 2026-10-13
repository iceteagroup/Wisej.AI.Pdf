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

// Bounds-checked big-endian reads used by the TrueType and CFF parsers.

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// Reads big-endian integers from a byte array, returning zero for any byte outside the array
	/// instead of throwing, so that truncated font tables degrade gracefully.
	/// </summary>
	internal static class BigEndian
	{
		/// <summary>
		/// Reads an unsigned byte.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="offset">The position.</param>
		/// <returns>The value, or 0 when out of range.</returns>
		public static int U8(byte[] data, int offset)
		{
			return (uint)offset < (uint)data.Length ? data[offset] : 0;
		}

		/// <summary>
		/// Reads a signed byte.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="offset">The position.</param>
		/// <returns>The value, or 0 when out of range.</returns>
		public static int S8(byte[] data, int offset)
		{
			return (sbyte)U8(data, offset);
		}

		/// <summary>
		/// Reads an unsigned 16-bit integer.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="offset">The position.</param>
		/// <returns>The value; missing bytes read as 0.</returns>
		public static int U16(byte[] data, int offset)
		{
			if (offset >= 0 && offset + 2 <= data.Length)
				return (data[offset] << 8) | data[offset + 1];

			return (U8(data, offset) << 8) | U8(data, offset + 1);
		}

		/// <summary>
		/// Reads a signed 16-bit integer.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="offset">The position.</param>
		/// <returns>The value; missing bytes read as 0.</returns>
		public static int S16(byte[] data, int offset)
		{
			return (short)U16(data, offset);
		}

		/// <summary>
		/// Reads an unsigned 24-bit integer.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="offset">The position.</param>
		/// <returns>The value; missing bytes read as 0.</returns>
		public static int U24(byte[] data, int offset)
		{
			return (U8(data, offset) << 16) | (U8(data, offset + 1) << 8) | U8(data, offset + 2);
		}

		/// <summary>
		/// Reads a 32-bit integer.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="offset">The position.</param>
		/// <returns>The value as a signed integer; missing bytes read as 0.</returns>
		public static int S32(byte[] data, int offset)
		{
			return (U8(data, offset) << 24) | (U8(data, offset + 1) << 16) | (U8(data, offset + 2) << 8) | U8(data, offset + 3);
		}

		/// <summary>
		/// Reads an unsigned 32-bit integer.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="offset">The position.</param>
		/// <returns>The value; missing bytes read as 0.</returns>
		public static uint U32(byte[] data, int offset)
		{
			return (uint)S32(data, offset);
		}

		/// <summary>
		/// Reads a big-endian unsigned integer of 1 to 4 bytes, as used by CFF offsets.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="offset">The position.</param>
		/// <param name="size">The number of bytes, 1 to 4.</param>
		/// <returns>The value; missing bytes read as 0.</returns>
		public static int UVar(byte[] data, int offset, int size)
		{
			var value = 0;
			for (int i = 0; i < size; i++)
				value = (value << 8) | U8(data, offset + i);

			return value;
		}

		/// <summary>
		/// Reads a four-character table tag.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="offset">The position.</param>
		/// <returns>The tag.</returns>
		public static string Tag(byte[] data, int offset)
		{
			var chars = new char[4];
			for (int i = 0; i < 4; i++)
				chars[i] = (char)U8(data, offset + i);

			return new string(chars);
		}
	}
}
