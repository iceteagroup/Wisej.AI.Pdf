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

// MQ arithmetic decoder and raw (bypass) bit reader of JPEG 2000 tier-1 coding (ITU-T T.800, Annex C).

using System.Runtime.CompilerServices;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Decodes the MQ-coded and raw-coded segments of a code-block.
	/// </summary>
	/// <remarks>
	/// Each context stores a single index into a table of 94 combined states, where the even entries
	/// have MPS = 0 and the odd entries MPS = 1; the transition tables already include the MPS switch,
	/// so a decision costs a handful of array lookups.
	/// </remarks>
	internal sealed class JpxMqDecoder
	{
		#region Tables

		// probability estimates of the 47 states (Table C.2).
		private static readonly ushort[] QeValues =
		{
			0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801,
			0x3001, 0x2401, 0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401,
			0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101,
			0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085,
			0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
		};

		// next state after an MPS (Table C.2).
		private static readonly byte[] NextMps =
		{
			1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20,
			21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40,
			41, 42, 43, 44, 45, 45, 46,
		};

		// next state after an LPS (Table C.2).
		private static readonly byte[] NextLps =
		{
			1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18,
			19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37,
			38, 39, 40, 41, 42, 43, 46,
		};

		// MPS switch flag of each state (Table C.2).
		private static readonly byte[] Switch =
		{
			1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0,
		};

		// the combined states indexed by state * 2 + mps, packed as Qe (bits 0-15), MPS (bit 16),
		// next state after an MPS (bits 17-23) and next state after an LPS (bits 24-30), so that a
		// decision costs a single table lookup.
		private static readonly uint[] States = BuildStates();

		/// <summary>
		/// The number of contexts used by tier-1 decoding: 9 zero-coding, 5 sign-coding, 3 refinement, run-length and uniform (T.800, D.3).
		/// </summary>
		public const int ContextCount = 19;

		/// <summary>
		/// The run-length context of the cleanup pass (T.800, D.3.4).
		/// </summary>
		public const int RunLengthContext = 17;

		/// <summary>
		/// The uniform context, used for the run-length position bits and the segmentation symbols.
		/// </summary>
		public const int UniformContext = 18;

		/// <summary>
		/// Builds the <see cref="States"/> table from the Qe, NMPS, NLPS and SWITCH columns of Table C.2.
		/// </summary>
		/// <returns>The 94 combined states.</returns>
		private static uint[] BuildStates()
		{
			var states = new uint[94];
			for (int i = 0; i < 47; i++)
			{
				for (int mps = 0; mps < 2; mps++)
				{
					var nextMps = NextMps[i] * 2 + mps;
					var nextLps = NextLps[i] * 2 + (Switch[i] != 0 ? 1 - mps : mps);
					states[i * 2 + mps] = QeValues[i] | (uint)mps << 16 | (uint)nextMps << 17 | (uint)nextLps << 24;
				}
			}

			return states;
		}

		#endregion

		#region Fields

		// the current state of each context.
		private readonly int[] _contexts = new int[ContextCount];

		// the coded data.
		private byte[] _data;

		// the position of the next byte to read in _data.
		private int _position;

		// the end of the segment (exclusive); bytes at or after it read as 0xFF.
		private int _end;

		// the code register (C in Annex C).
		private uint _c;

		// the interval register (A in Annex C).
		private uint _a;

		// the number of bits left before the next BYTEIN (CT in Annex C).
		private int _ct;

		// raw mode: the current byte.
		private int _rawByte;

		// raw mode: the number of bits left in _rawByte.
		private int _rawBits;

		#endregion

		#region Methods

		/// <summary>
		/// Resets all contexts to their initial states (Table D.7).
		/// </summary>
		public void ResetContexts()
		{
			for (int i = 0; i < ContextCount; i++)
				_contexts[i] = 0;

			// zero-coding context 0 starts in state 4, run-length in state 3 and uniform in state 46.
			_contexts[0] = 4 * 2;
			_contexts[RunLengthContext] = 3 * 2;
			_contexts[UniformContext] = 46 * 2;
		}

		/// <summary>
		/// Starts decoding an MQ-coded segment (INITDEC, C.3.5).
		/// </summary>
		/// <param name="data">The buffer containing the segment.</param>
		/// <param name="start">The start of the segment.</param>
		/// <param name="end">The end of the segment (exclusive); bytes past the end read as 0xFF.</param>
		public void Start(byte[] data, int start, int end)
		{
			_data = data;
			_position = start;
			_end = end;

			_c = (uint)CurrentByte() << 16;
			ByteIn();
			_c <<= 7;
			_ct -= 7;
			_a = 0x8000;
		}

		/// <summary>
		/// Starts reading a raw (bypass) segment.
		/// </summary>
		/// <param name="data">The buffer containing the segment.</param>
		/// <param name="start">The start of the segment.</param>
		/// <param name="end">The end of the segment (exclusive).</param>
		public void StartRaw(byte[] data, int start, int end)
		{
			_data = data;
			_position = start;
			_end = end;
			_rawByte = 0;
			_rawBits = 0;
		}

		/// <summary>
		/// Decodes one binary decision in context <paramref name="cx"/> (DECODE, C.3.2).
		/// </summary>
		/// <param name="cx">The context label.</param>
		/// <returns>The decoded bit.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int Decode(int cx)
		{
			var contexts = _contexts;
			var entry = States[contexts[cx]];
			var qe = entry & 0xFFFF;
			var mps = (int)(entry >> 16) & 1;
			var a = _a - qe;
			var c = _c;
			int bit;

			// the LPS sub-interval lies at the bottom of the interval (C.3.2).
			if ((c >> 16) < qe)
			{
				// LPS_EXCHANGE: a conditional exchange may still yield the MPS.
				if (a < qe)
				{
					bit = mps;
					contexts[cx] = (int)(entry >> 17) & 0x7F;
				}
				else
				{
					bit = 1 - mps;
					contexts[cx] = (int)(entry >> 24);
				}

				a = qe;
			}
			else
			{
				c -= qe << 16;

				// fast path: MPS without renormalization.
				if ((a & 0x8000) != 0)
				{
					_a = a;
					_c = c;
					return mps;
				}

				// MPS_EXCHANGE.
				if (a < qe)
				{
					bit = 1 - mps;
					contexts[cx] = (int)(entry >> 24);
				}
				else
				{
					bit = mps;
					contexts[cx] = (int)(entry >> 17) & 0x7F;
				}
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
			return bit;
		}

		/// <summary>
		/// Reads one bit of a raw (bypass) segment, removing the stuffed bit after each 0xFF byte.
		/// </summary>
		/// <returns>The bit.</returns>
		public int DecodeRaw()
		{
			if (_rawBits == 0)
			{
				if (_rawByte == 0xFF)
				{
					var next = CurrentByte();
					if (next > 0x8F)
					{
						// a marker: keep feeding 1 bits.
						_rawByte = 0xFF;
						_rawBits = 8;
					}
					else
					{
						_rawByte = next;
						_position++;
						_rawBits = 7;
					}
				}
				else
				{
					_rawByte = CurrentByte();
					_position++;
					_rawBits = 8;
				}
			}

			_rawBits--;
			return (_rawByte >> _rawBits) & 1;
		}

		#endregion

		#region Implementation

		// returns the byte at the current position, or 0xFF past the end of the segment.
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private int CurrentByte()
		{
			return _position < _end ? _data[_position] : 0xFF;
		}

		// BYTEIN (C.3.4): feeds the next byte into the code register, honouring bit stuffing.
		private void ByteIn()
		{
			if (CurrentByte() == 0xFF)
			{
				var next = _position + 1 < _end ? _data[_position + 1] : 0xFF;
				if (next > 0x8F)
				{
					// a marker or the end of the segment: feed 1 bits without advancing.
					_c += 0xFF00;
					_ct = 8;
				}
				else
				{
					_position++;
					_c += (uint)next << 9;
					_ct = 7;
				}
			}
			else
			{
				_position++;
				_c += (uint)CurrentByte() << 8;
				_ct = 8;
			}
		}

		#endregion
	}
}
