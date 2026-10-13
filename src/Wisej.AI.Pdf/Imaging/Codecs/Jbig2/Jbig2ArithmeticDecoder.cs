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

// MQ arithmetic decoder and the integer decoding procedures of JBIG2 (ITU T.88, Annex A and Annex E).

using System.Runtime.CompilerServices;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// The MQ arithmetic decoder of ITU T.88 Annex E, with the integer (IAx) and symbol ID (IAID)
	/// decoding procedures of Annex A.
	/// </summary>
	/// <remarks>
	/// Context statistics are stored one byte per context: the state index in the upper seven bits
	/// and the MPS value in the lowest bit, so a new context array starts in state 0 with MPS 0 as
	/// required by the standard. Reading past the end of the data supplies 0xFF bytes (E.3.4), which
	/// the decoder treats as a marker; <see cref="Overrun"/> counts them so that callers can stop
	/// decoding loops on truncated streams.
	/// </remarks>
	internal sealed class Jbig2ArithmeticDecoder
	{
		#region Fields

		/// <summary>
		/// The value returned by <see cref="DecodeInteger"/> for the out-of-band value.
		/// </summary>
		public const int Oob = int.MinValue;

		/// <summary>
		/// The number of synthetic bytes after which <see cref="IsExhausted"/> reports the data as consumed.
		/// </summary>
		private const int MaxOverrun = 32;

		// Table E.1: probability estimate (Qe), next index after MPS and LPS, and the MPS switch flag.
		private static readonly int[] Qe =
		{
			0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801,
			0x3001, 0x2401, 0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401,
			0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101,
			0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085,
			0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
		};

		// next state after an MPS decision.
		private static readonly byte[] Nmps =
		{
			1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20,
			21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40,
			41, 42, 43, 44, 45, 45, 46,
		};

		// next state after an LPS decision.
		private static readonly byte[] Nlps =
		{
			1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18,
			19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37,
			38, 39, 40, 41, 42, 43, 46,
		};

		// 1 when the MPS sense is switched after an LPS decision.
		private static readonly byte[] Switch =
		{
			1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0,
		};

		// the four columns of Table E.1 packed into one entry per state, so that a decision needs a
		// single table load: Qe in bits 0-15, NMPS in bits 16-21, NLPS in bits 22-27, SWITCH in bit 28.
		private static readonly int[] States = PackStates();

		/// <summary>The compressed data.</summary>
		private readonly byte[] _data;

		/// <summary>The end (exclusive) of the compressed data.</summary>
		private readonly int _end;

		/// <summary>The position of the current byte B.</summary>
		private int _position;

		/// <summary>The code register C (software convention of Annex E).</summary>
		private uint _c;

		/// <summary>The interval register A.</summary>
		private int _a;

		/// <summary>The bit counter CT.</summary>
		private int _ct;

		#endregion

		#region Constructors

		/// <summary>
		/// Creates a decoder positioned at the beginning of the arithmetically coded data (INITDEC, E.3.5).
		/// </summary>
		/// <param name="data">The buffer containing the data.</param>
		/// <param name="start">The offset of the first byte.</param>
		/// <param name="end">The offset after the last byte.</param>
		public Jbig2ArithmeticDecoder(byte[] data, int start, int end)
		{
			_data = data;
			_end = end;
			_position = start;

			_c = (uint)(ReadByte(_position) ^ 0xFF) << 16;
			ByteIn();
			_c <<= 7;
			_ct -= 7;
			_a = 0x8000;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the number of bytes the decoder has read beyond the end of its data.
		/// </summary>
		public int Overrun { get; private set; }

		/// <summary>
		/// Returns true when the decoder has read so far beyond the end of the data that the stream must be truncated.
		/// </summary>
		public bool IsExhausted
		{
			get { return this.Overrun > MaxOverrun; }
		}

		#endregion

		#region Methods

		/// <summary>
		/// Decodes one binary decision (DECODE, E.3.2).
		/// </summary>
		/// <param name="contexts">The context statistics.</param>
		/// <param name="cx">The index of the context in <paramref name="contexts"/>.</param>
		/// <returns>The decoded bit.</returns>
		/// <remarks>
		/// Only the most frequent case, an MPS decision without renormalization, is handled here, so
		/// that the method is small enough to be inlined into the bitmap decoding loops.
		/// </remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int DecodeBit(byte[] contexts, int cx)
		{
			int state = contexts[cx];
			var a = _a - (States[state >> 1] & 0xFFFF);
			if ((a & 0x8000) != 0 && (_c >> 16) < (uint)a)
			{
				_a = a;
				return state & 1;
			}

			return DecodeBitSlow(contexts, cx, state, a);
		}

		/// <summary>
		/// Decodes a signed integer with the IAx procedure of A.2.
		/// </summary>
		/// <param name="contexts">The 512 contexts of the integer type (IADH, IADW, ...).</param>
		/// <returns>The value, or <see cref="Oob"/> for the out-of-band value.</returns>
		/// <remarks>Values outside the 32-bit range, which only corrupt data produce, are clamped.</remarks>
		public int DecodeInteger(byte[] contexts)
		{
			var prev = 1;
			var sign = DecodeIntegerBits(contexts, ref prev, 1);

			long value;
			if (DecodeIntegerBits(contexts, ref prev, 1) == 0)
				value = DecodeIntegerBits(contexts, ref prev, 2);
			else if (DecodeIntegerBits(contexts, ref prev, 1) == 0)
				value = DecodeIntegerBits(contexts, ref prev, 4) + 4;
			else if (DecodeIntegerBits(contexts, ref prev, 1) == 0)
				value = DecodeIntegerBits(contexts, ref prev, 6) + 20;
			else if (DecodeIntegerBits(contexts, ref prev, 1) == 0)
				value = DecodeIntegerBits(contexts, ref prev, 8) + 84;
			else if (DecodeIntegerBits(contexts, ref prev, 1) == 0)
				value = DecodeIntegerBits(contexts, ref prev, 12) + 340;
			else
				value = (uint)DecodeIntegerBits(contexts, ref prev, 32) + 4436L;

			if (sign == 0)
				return value > int.MaxValue ? int.MaxValue : (int)value;

			// a negative zero is the out-of-band value.
			if (value == 0)
				return Oob;

			return value > int.MaxValue ? -int.MaxValue : (int)-value;
		}

		/// <summary>
		/// Decodes a symbol ID with the IAID procedure of A.3.
		/// </summary>
		/// <param name="contexts">The 2^<paramref name="codeLength"/> IAID contexts.</param>
		/// <param name="codeLength">The symbol code length SBSYMCODELEN.</param>
		/// <returns>The symbol ID.</returns>
		public int DecodeId(byte[] contexts, int codeLength)
		{
			var prev = 1;
			for (var i = 0; i < codeLength; i++)
				prev = (prev << 1) | DecodeBit(contexts, prev);

			return prev - (1 << codeLength);
		}

		#endregion

		#region Implementation

		// the rest of DECODE (E.3.2): exchanges and renormalization.
		private int DecodeBitSlow(byte[] contexts, int cx, int state, int a)
		{
			var mps = state & 1;
			var entry = States[state >> 1];
			var qe = entry & 0xFFFF;

			// the registers are kept in locals: the decision is the hottest path of the decoder.
			var c = _c;
			int d;

			if ((c >> 16) < (uint)a)
			{
				// MPS_EXCHANGE.
				if (a < qe)
				{
					d = 1 - mps;
					contexts[cx] = (byte)((((entry >> 22) & 0x3F) << 1) | (mps ^ ((entry >> 28) & 1)));
				}
				else
				{
					d = mps;
					contexts[cx] = (byte)((((entry >> 16) & 0x3F) << 1) | mps);
				}
			}
			else
			{
				// LPS_EXCHANGE.
				c -= (uint)a << 16;
				if (a < qe)
				{
					d = mps;
					contexts[cx] = (byte)((((entry >> 16) & 0x3F) << 1) | mps);
				}
				else
				{
					d = 1 - mps;
					contexts[cx] = (byte)((((entry >> 22) & 0x3F) << 1) | (mps ^ ((entry >> 28) & 1)));
				}

				a = qe;
			}

			// RENORMD.
			var ct = _ct;
			do
			{
				if (ct == 0)
				{
					_c = c;
					ByteIn();
					c = _c;
					ct = _ct;
				}

				a <<= 1;
				c <<= 1;
				ct--;
			}
			while ((a & 0x8000) == 0);

			_a = a;
			_c = c;
			_ct = ct;
			return d;
		}

		// reads bits for the integer procedure, updating PREV as described in A.2.
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private int DecodeIntegerBits(byte[] contexts, ref int prev, int count)
		{
			var value = 0;
			for (var i = 0; i < count; i++)
			{
				var bit = DecodeBit(contexts, prev);
				prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
				value = (value << 1) | bit;
			}

			return value;
		}

		// builds the packed state table from the columns of Table E.1.
		private static int[] PackStates()
		{
			var states = new int[Qe.Length];
			for (var i = 0; i < states.Length; i++)
				states[i] = Qe[i] | (Nmps[i] << 16) | (Nlps[i] << 22) | (Switch[i] << 28);

			return states;
		}

		// BYTEIN (E.3.4).
		private void ByteIn()
		{
			if (ReadByte(_position) == 0xFF)
			{
				if (ReadByte(_position + 1) > 0x8F)
				{
					// a marker or the end of the data: the coded data is over, and 1 bits are fed
					// without advancing.
					_ct = 8;
					if (_position + 1 >= _end)
						this.Overrun++;
				}
				else
				{
					_position++;
					_c += 0xFE00u - ((uint)ReadByte(_position) << 9);
					_ct = 7;
				}
			}
			else
			{
				_position++;
				_c += 0xFF00u - ((uint)ReadByte(_position) << 8);
				_ct = 8;
			}
		}

		// returns the byte at the position, or 0xFF past the end of the data.
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private int ReadByte(int position)
		{
			return position < _end ? _data[position] : 0xFF;
		}

		#endregion
	}
}
