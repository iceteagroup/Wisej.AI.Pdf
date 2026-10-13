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

// MSB-first bit reader for the Huffman-coded and MMR-coded parts of JBIG2 streams (ITU T.88, 6.2.6 and 7.4).

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Reads bits, most significant first, from a range of a byte array.
	/// </summary>
	/// <remarks>
	/// Reading past the end returns 0 bits and increments <see cref="Overrun"/>, so that decoding
	/// loops can stop on truncated data without exceptions.
	/// </remarks>
	internal sealed class Jbig2BitReader
	{
		#region Fields

		/// <summary>
		/// The number of bits read past the end after which <see cref="IsExhausted"/> reports the data as consumed.
		/// </summary>
		private const int MaxOverrun = 2048;

		/// <summary>The data buffer.</summary>
		private readonly byte[] _data;

		/// <summary>The current bit position, counted from the beginning of the buffer.</summary>
		private long _bitPosition;

		#endregion

		#region Constructors

		/// <summary>
		/// Creates a reader over <paramref name="data"/> from <paramref name="start"/> to <paramref name="end"/>.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="start">The offset of the first byte.</param>
		/// <param name="end">The offset after the last byte.</param>
		public Jbig2BitReader(byte[] data, int start, int end)
		{
			_data = data;
			_bitPosition = (long)start << 3;
			this.End = end;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the data buffer.
		/// </summary>
		public byte[] Data
		{
			get { return _data; }
		}

		/// <summary>
		/// Returns the offset after the last byte of the data.
		/// </summary>
		public int End { get; }

		/// <summary>
		/// Returns the offset of the byte containing the next bit.
		/// </summary>
		public int Position
		{
			get { return (int)(_bitPosition >> 3); }
		}

		/// <summary>
		/// Returns the number of bits read past the end of the data.
		/// </summary>
		public int Overrun { get; private set; }

		/// <summary>
		/// Returns true when the reader has gone so far past the end that the stream must be truncated.
		/// </summary>
		public bool IsExhausted
		{
			get { return this.Overrun > MaxOverrun; }
		}

		#endregion

		#region Methods

		/// <summary>
		/// Reads one bit.
		/// </summary>
		/// <returns>0 or 1; 0 past the end of the data.</returns>
		public int ReadBit()
		{
			var index = _bitPosition >> 3;
			if (index >= this.End)
			{
				this.Overrun++;
				_bitPosition++;
				return 0;
			}

			var bit = (_data[index] >> (7 - (int)(_bitPosition & 7))) & 1;
			_bitPosition++;
			return bit;
		}

		/// <summary>
		/// Reads up to 32 bits as an unsigned number.
		/// </summary>
		/// <param name="count">The number of bits, 0 to 32.</param>
		/// <returns>The value.</returns>
		public uint ReadBits(int count)
		{
			uint value = 0;
			for (var i = 0; i < count; i++)
				value = (value << 1) | (uint)ReadBit();

			return value;
		}

		/// <summary>
		/// Returns the next <paramref name="count"/> bits without consuming them; bits past the end are 0.
		/// </summary>
		/// <param name="count">The number of bits, 1 to 24.</param>
		/// <returns>The bits, right aligned.</returns>
		public int PeekBits(int count)
		{
			var index = (int)(_bitPosition >> 3);
			var shift = (int)(_bitPosition & 7);

			// load 32 bits starting at the current byte; 3 bytes plus the partial one cover 24 bits.
			var window = 0L;
			for (var i = 0; i < 4; i++)
			{
				window <<= 8;
				if (index + i < this.End)
					window |= _data[index + i];
			}

			return (int)((window >> (32 - shift - count)) & ((1L << count) - 1));
		}

		/// <summary>
		/// Consumes <paramref name="count"/> bits.
		/// </summary>
		/// <param name="count">The number of bits.</param>
		public void Skip(int count)
		{
			_bitPosition += count;
			var excess = (_bitPosition >> 3) - this.End;
			if (excess > 0)
				this.Overrun = (int)System.Math.Min(int.MaxValue, excess * 8);
		}

		/// <summary>
		/// Advances to the next byte boundary, if not already on one.
		/// </summary>
		public void AlignToByte()
		{
			_bitPosition = (_bitPosition + 7) & ~7L;
		}

		/// <summary>
		/// Moves the reader to the beginning of the byte at <paramref name="offset"/>.
		/// </summary>
		/// <param name="offset">The byte offset in the buffer.</param>
		public void Seek(int offset)
		{
			_bitPosition = (long)offset << 3;
		}

		#endregion
	}
}
