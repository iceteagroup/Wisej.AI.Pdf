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

// CCITT Group 3 and Group 4 fax decoder for the CCITTFaxDecode filter
// (ISO 32000-2, 7.4.6; ITU-T T.4 and T.6).

using System;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// The decode parameters of the <c>CCITTFaxDecode</c> filter (ISO 32000-2, table 11).
	/// </summary>
	internal sealed class CcittParameters
	{
		/// <summary>
		/// The coding scheme: negative for Group 4, 0 for Group 3 one-dimensional, positive for Group 3 mixed one- and two-dimensional.
		/// </summary>
		public int K;

		/// <summary>
		/// True when every line is preceded by an end-of-line code (the decoder also detects them when false).
		/// </summary>
		public bool EndOfLine;

		/// <summary>
		/// True when each encoded line (or, with <see cref="EndOfLine"/>, each EOL) is aligned to a byte boundary.
		/// </summary>
		public bool EncodedByteAlign;

		/// <summary>
		/// The width of the image in pixels.
		/// </summary>
		public int Columns = 1728;

		/// <summary>
		/// The height of the image in pixels; 0 when unknown.
		/// </summary>
		public int Rows;

		/// <summary>
		/// True when the data ends with an end-of-block pattern (RTC or EOFB); false when only <see cref="Rows"/> delimits it.
		/// </summary>
		public bool EndOfBlock = true;

		/// <summary>
		/// True when 1 bits are black in the output; false (the default) when 0 bits are black.
		/// </summary>
		public bool BlackIs1;

		/// <summary>
		/// The number of damaged rows tolerated before an error. The decoder is always tolerant and only keeps this for reference.
		/// </summary>
		public int DamagedRowsBeforeError = 0;
	}

	/// <summary>
	/// Decodes CCITT Group 3 (T.4) and Group 4 (T.6) compressed bilevel images.
	/// </summary>
	/// <remarks>
	/// Lines are decoded as lists of changing elements (the positions where the colour flips), which keeps
	/// the two-dimensional coding cheap and makes rendering a matter of filling runs. Damaged lines are replaced
	/// by the previous line, and decoding resumes at the next EOL code when the stream has them.
	/// </remarks>
	internal static class CcittFaxDecoder
	{
		#region Fields

		/// <summary>
		/// The largest number of columns accepted.
		/// </summary>
		public const int MaxColumns = 1 << 20;

		/// <summary>
		/// The largest output accepted, to protect against decompression bombs.
		/// </summary>
		public const long MaxOutputLength = 512L * 1024 * 1024;

		// special run lengths stored in the run tables.
		/// <summary>The EOL code (000000000001) found where a run was expected.</summary>
		private const int RunEndOfLine = -1;

		/// <summary>The 1D extension code (000000001111) that enters uncompressed mode.</summary>
		private const int RunUncompressed = -2;

		// the 2D mode codes (T.4 table 4); the vertical modes are 0-6 for VL3..VR3.
		/// <summary>Pass mode (P, 0001).</summary>
		private const int ModePass = 7;

		/// <summary>Horizontal mode (H, 001): two run lengths follow.</summary>
		private const int ModeHorizontal = 8;

		/// <summary>2D extension code (0000001xxx), used to enter uncompressed mode.</summary>
		private const int ModeExtension = 9;

		/// <summary>Seven zero bits: the start of an EOL, or an invalid code.</summary>
		private const int ModeZeros = 10;

		// run lookup for white codes, indexed by the next 12 bits: (run << 8) | length, 0 when invalid.
		private static readonly int[] WhiteTable;

		// run lookup for black codes, indexed by the next 13 bits: (run << 8) | length, 0 when invalid.
		private static readonly int[] BlackTable;

		// 2D mode lookup, indexed by the next 7 bits: (mode << 8) | length.
		private static readonly int[] ModeTable;

		#endregion

		#region Constructors

		/// <summary>
		/// Builds the lookup tables. A static constructor runs after the field initializers, so the code lists are ready.
		/// </summary>
		static CcittFaxDecoder()
		{
			WhiteTable = BuildRunTable(true, 12);
			BlackTable = BuildRunTable(false, 13);
			ModeTable = BuildModeTable();
		}

		#endregion

		#region Methods

		/// <summary>
		/// Decodes CCITT fax data.
		/// </summary>
		/// <param name="data">The encoded data.</param>
		/// <param name="parameters">The decode parameters; null for the defaults.</param>
		/// <param name="rows">Receives the number of rows in the result.</param>
		/// <returns>
		/// Packed 1-bit rows of <c>(Columns + 7) / 8</c> bytes, as the PDF filter outputs them: 0 bits are black
		/// unless <see cref="CcittParameters.BlackIs1"/> is true. Never null.
		/// </returns>
		/// <remarks>
		/// With a known <see cref="CcittParameters.Rows"/> the result has at least that many rows (missing rows are white);
		/// rows beyond it are kept only when the data holds valid lines up to an end-of-block pattern.
		/// </remarks>
		public static byte[] Decode(byte[] data, CcittParameters parameters, out int rows)
		{
			parameters = parameters ?? new CcittParameters();
			var columns = parameters.Columns > 0 ? Math.Min(parameters.Columns, MaxColumns) : 1728;

			var decoder = new FaxDecoder(data ?? Array.Empty<byte>(), parameters, columns);
			return decoder.Run(out rows);
		}

		#endregion

		#region Implementation

		// the state of one decoding run.
		private sealed class FaxDecoder
		{
			// the decoding result of one line.
			private enum LineStatus
			{
				// the line was decoded.
				Ok,

				// an invalid code was found.
				Error,

				// an EOL was found before the end of the line.
				PrematureEol,

				// the data ended.
				EndOfData,

				// the end-of-block pattern (RTC or EOFB) or the end of the data was reached before the line.
				EndOfBlock,

				// a Group 3 line without its EOL while the stream uses EOLs: leftover bits of a damaged line.
				MissingEol,
			}

			// the encoded data.
			private readonly byte[] _data;

			// the number of bits in _data.
			private readonly long _totalBits;

			// the decode parameters.
			private readonly CcittParameters _parameters;

			// the width of the image.
			private readonly int _columns;

			// the number of bytes per output row.
			private readonly int _stride;

			// the position of the next bit.
			private long _bit;

			// the changing elements of the reference line, followed by sentinels.
			private int[] _reference;

			// the number of changing elements in _reference.
			private int _referenceCount;

			// the changing elements of the coding line.
			private int[] _coding;

			// the number of changing elements in _coding.
			private int _codingCount;

			// the output rows.
			private byte[] _output;

			// the number of rows in _output.
			private int _rowCount;

			// true once an EOL code was seen, which makes resynchronization possible.
			private bool _seenEol;

			// the position after the EOL of the current line.
			private long _lineDataStart;

			// the number of lines that started with an EOL.
			private int _eolLines;

			// the byte value of a white row.
			private readonly byte _white;

			// constructor.
			public FaxDecoder(byte[] data, CcittParameters parameters, int columns)
			{
				_data = data;
				_totalBits = (long)data.Length * 8;
				_parameters = parameters;
				_columns = columns;
				_stride = (columns + 7) / 8;
				_white = parameters.BlackIs1 ? (byte)0x00 : (byte)0xFF;

				// up to one change per pixel, plus the sentinels.
				_reference = new int[columns + 4];
				_coding = new int[columns + 4];
			}

			// decodes all the rows.
			public byte[] Run(out int rows)
			{
				var expectedRows = Math.Max(0, _parameters.Rows);
				var maxRows = (int)Math.Min(int.MaxValue, MaxOutputLength / _stride);
				if (expectedRows > maxRows)
					expectedRows = maxRows;

				_output = new byte[(long)_stride * Math.Max(16, Math.Min(expectedRows, 1 << 16))];

				// with EncodedByteAlign, Group 4 and EOL-less Group 3 lines start on a byte boundary, while
				// Group 3 lines with EOLs are padded before the EOL (which the zero skipping absorbs).
				var k = _parameters.K;
				var alignFirst = _parameters.EncodedByteAlign && !(_parameters.EndOfLine && k >= 0);

				// the reference line of the first row is all white.
				SetSentinels(_reference, 0);
				_referenceCount = 0;

				while (_rowCount < maxRows)
				{
					if (expectedRows > 0 && _rowCount >= expectedRows && !_parameters.EndOfBlock)
						break;

					if (_bit >= _totalBits)
						break;

					var lineStart = _bit;
					var status = DecodeLine(k, alignFirst);

					// the parameters are often wrong about the alignment: when the line fails, try the other way.
					if (status == LineStatus.Error || status == LineStatus.PrematureEol || status == LineStatus.MissingEol)
					{
						var firstEnd = _bit;
						var firstCount = _codingCount;
						_bit = lineStart;
						var retry = DecodeLine(k, !alignFirst);
						if (retry == LineStatus.Ok || retry == LineStatus.EndOfBlock)
						{
							status = retry;
							alignFirst = !alignFirst;
						}
						else if (status == LineStatus.PrematureEol)
						{
							// keep the first interpretation; redo it, since the retry overwrote the coding line.
							_bit = lineStart;
							status = DecodeLine(k, alignFirst);
						}
						else
						{
							_bit = firstEnd;
							_codingCount = firstCount;
						}
					}

					if (status == LineStatus.EndOfBlock)
						break;

					// skip the garbage up to the next EOL; the damaged line itself was already emitted.
					if (status == LineStatus.MissingEol)
					{
						_bit = Math.Max(_lineDataStart, lineStart + 1);
						if (!SeekEndOfLine())
							break;

						continue;
					}

					if (status == LineStatus.Ok || status == LineStatus.PrematureEol)
					{
						// a line cut short by an EOL is kept: its end is white.
						EmitCodingLine();
						continue;
					}

					// after the expected rows, a failure means trailing junk: stop without a damaged row.
					if (expectedRows > 0 && _rowCount >= expectedRows)
						break;

					if (status == LineStatus.EndOfData && _codingCount == 0)
						break;

					// zero padding at the end of the data is not a damaged line.
					_bit = lineStart;
					if (CountZeros() >= _totalBits - _bit)
						break;

					// a damaged line repeats the previous one (or is white), and decoding resumes at the next EOL.
					EmitPreviousLine();
					if (status == LineStatus.EndOfData)
						break;

					// search after the EOL that started the damaged line, so that decoding always progresses.
					_bit = Math.Max(_lineDataStart, lineStart + 1);
					if (!(_seenEol || _parameters.EndOfLine) || !SeekEndOfLine())
						break;
				}

				// pad to the expected number of rows.
				while (_rowCount < expectedRows)
					FillWhite(AddRow());

				rows = _rowCount;
				var result = new byte[(long)_rowCount * _stride];
				Buffer.BlockCopy(_output, 0, result, 0, result.Length);
				return result;
			}

			// decodes one line: the optional EOL (or the end-of-block pattern), the tag bit and the coded line.
			private LineStatus DecodeLine(int k, bool alignFirst)
			{
				_codingCount = 0;
				if (SkipEndOfLine(alignFirst))
				{
					_seenEol = true;
					_eolLines++;

					// two EOLs in a row are RTC (Group 3) or EOFB (Group 4): the end of the data.
					if (IsEndOfBlock(k))
						return LineStatus.EndOfBlock;
				}
				else if (k >= 0 && _eolLines > 0 && _eolLines * 2 > _rowCount)
				{
					_lineDataStart = _bit;
					return LineStatus.MissingEol;
				}

				if (_bit >= _totalBits)
					return LineStatus.EndOfBlock;

				_lineDataStart = _bit;

				// Group 3 2D: the tag bit after the EOL selects 1D (1) or 2D (0) coding.
				var twoDimensional = k < 0 || (k > 0 && ReadBit() == 0);
				return twoDimensional ? DecodeTwoDimensional() : DecodeOneDimensional();
			}

			// decodes a one-dimensional (modified Huffman) line.
			private LineStatus DecodeOneDimensional()
			{
				_codingCount = 0;
				var a0 = 0;
				var color = 0;

				while (a0 < _columns)
				{
					var run = DecodeRun(color);
					if (run < 0)
					{
						if (run == RunUncompressed)
						{
							if (!DecodeUncompressed(ref a0, ref color))
								return LineStatus.Error;

							continue;
						}

						return run == RunEndOfLine ? LineStatus.PrematureEol : (_bit >= _totalBits ? LineStatus.EndOfData : LineStatus.Error);
					}

					a0 += run;
					if (a0 >= _columns)
						break;

					AddChange(a0);
					color ^= 1;
				}

				return LineStatus.Ok;
			}

			// decodes a two-dimensional (modified READ) line against the reference line (T.4, 4.2).
			private LineStatus DecodeTwoDimensional()
			{
				_codingCount = 0;
				var reference = _reference;
				var a0 = -1;
				var color = 0;
				var index = 0;

				while (a0 < _columns)
				{
					// b1: the first changing element of the reference line after a0 with the colour opposite to a0's.
					// even entries are changes to black, odd entries changes to white.
					while (reference[index] <= a0 && index < _referenceCount)
						index++;

					if ((index & 1) != color)
						index++;

					var b1 = reference[index];
					var b2 = reference[index + 1];

					var entry = ModeTable[Peek(7)];
					var mode = entry >> 8;
					Skip(entry & 0xFF);

					switch (mode)
					{
						case ModePass:
							// the colour of a0 continues below b2.
							a0 = b2;
							break;

						case ModeHorizontal:
						{
							var run1 = DecodeRun(color);
							if (run1 < 0)
								return RunFailure(run1);

							var run2 = DecodeRun(color ^ 1);
							if (run2 < 0)
								return RunFailure(run2);

							var start = Math.Max(a0, 0);
							var a1 = start + run1;
							var a2 = a1 + run2;
							AddChange(a1);
							AddChange(a2);
							a0 = a2;
							break;
						}

						case ModeExtension:
						{
							// only the uncompressed mode extension (0000001 111) is defined.
							if (Peek(3) != 7)
								return LineStatus.Error;

							Skip(3);
							var position = Math.Max(a0, 0);
							if (!DecodeUncompressed(ref position, ref color))
								return LineStatus.Error;

							a0 = position;
							break;
						}

						case ModeZeros:
							if (_bit >= _totalBits)
								return LineStatus.EndOfData;

							// an EOL inside the line means the line was cut short.
							return Peek(12) == 1 ? LineStatus.PrematureEol : LineStatus.Error;

						default:
						{
							// vertical modes: a1 is b1 shifted by -3..+3.
							var a1 = b1 + mode - 3;
							if (a1 < Math.Max(a0, 0))
								return LineStatus.Error;

							AddChange(a1);
							a0 = a1;
							color ^= 1;
							break;
						}
					}

					// the next b1 can be at most one entry behind the current one (after VL modes).
					if (index > 0)
						index--;
				}

				return LineStatus.Ok;
			}

			// maps a failed run decode to the line status.
			private LineStatus RunFailure(int run)
			{
				if (run == RunEndOfLine)
					return LineStatus.PrematureEol;

				return _bit >= _totalBits ? LineStatus.EndOfData : LineStatus.Error;
			}

			// decodes the run length of one colour: make-up codes followed by a terminating code.
			private int DecodeRun(int color)
			{
				var total = 0;
				while (true)
				{
					int entry;
					if (color == 0)
						entry = WhiteTable[Peek(12)];
					else
						entry = BlackTable[Peek(13)];

					var length = entry & 0xFF;
					if (length == 0)
						return int.MinValue;

					var run = entry >> 8;
					if (run < 0)
					{
						// an EOL is left in place for the line loop; the uncompressed mode prefix is consumed.
						if (run == RunUncompressed)
							Skip(length);

						return total == 0 ? run : int.MinValue;
					}

					Skip(length);
					total += run;

					if (run < 64)
						return total;

					// guard against an endless sequence of make-up codes in corrupt data.
					if (total > _columns + 2560)
						return int.MinValue;
				}
			}

			// decodes uncompressed mode (T.4, annex/table 5) from `position` until its exit code.
			private bool DecodeUncompressed(ref int position, ref int color)
			{
				// the colour of the pixels before `position`.
				var current = color;
				while (true)
				{
					if (_bit >= _totalBits)
						return false;

					var bits = Peek(12);
					var zeros = 0;
					while (zeros < 12 && (bits & (0x800 >> zeros)) == 0)
						zeros++;

					if (zeros < 5)
					{
						// 0^n 1: n white pixels and one black pixel.
						Skip(zeros + 1);
						PutPixels(ref position, ref current, 0, zeros);
						PutPixels(ref position, ref current, 1, 1);
					}
					else if (zeros == 5)
					{
						// 000001: five white pixels.
						Skip(6);
						PutPixels(ref position, ref current, 0, 5);
					}
					else if (zeros <= 10)
					{
						// 0^(6+n) 1 T: n white pixels, then back to compressed mode with the colour T.
						Skip(zeros + 1);
						PutPixels(ref position, ref current, 0, zeros - 6);
						var next = ReadBit();
						if (next != current && position < _columns)
							AddChange(position);

						color = next;
						return true;
					}
					else
					{
						return false;
					}
				}
			}

			// appends `count` pixels of `color` in uncompressed mode.
			private void PutPixels(ref int position, ref int current, int color, int count)
			{
				if (count == 0 || position >= _columns)
					return;

				if (color != current)
				{
					AddChange(position);
					current = color;
				}

				position = Math.Min(_columns, position + count);
			}

			// records a changing element of the coding line.
			private void AddChange(int position)
			{
				if (position >= _columns)
					position = _columns;

				var count = _codingCount;
				if (count > 0)
				{
					var last = _coding[count - 1];

					// corrupt data can move backwards: never let the list decrease.
					if (position < last)
						position = last;

					// two changes at the same position cancel out (zero-length run).
					if (position == last)
					{
						_codingCount = count - 1;
						return;
					}
				}

				if (position >= _columns || count >= _columns)
					return;

				_coding[count] = position;
				_codingCount = count + 1;
			}

			// renders the coding line, then makes it the reference line.
			private void EmitCodingLine()
			{
				var row = AddRow();
				FillWhite(row);
				var black = (byte)~_white;
				for (int i = 0; i < _codingCount; i += 2)
				{
					var end = i + 1 < _codingCount ? _coding[i + 1] : _columns;
					FillRun(_output, row, _coding[i], end, black == 0xFF);
				}

				SetSentinels(_coding, _codingCount);
				var swap = _reference;
				_reference = _coding;
				_referenceCount = _codingCount;
				_coding = swap;
			}

			// emits a copy of the previous row (white for the first row); the reference line is unchanged.
			private void EmitPreviousLine()
			{
				var row = AddRow();
				if (_rowCount > 1)
					Buffer.BlockCopy(_output, row - _stride, _output, row, _stride);
				else
					FillWhite(row);
			}

			// appends a row to the output, growing the buffer as needed.
			private int AddRow()
			{
				var offset = (long)_rowCount * _stride;
				if (offset + _stride > _output.Length)
				{
					var size = Math.Min(Math.Max((long)_output.Length * 2, offset + _stride), MaxOutputLength);
					var grown = new byte[size];
					Buffer.BlockCopy(_output, 0, grown, 0, (int)offset);
					_output = grown;
				}

				_rowCount++;
				return (int)offset;
			}

			// skips the fill bits and an EOL code at the current position; returns true when an EOL was found.
			private bool SkipEndOfLine(bool alignFirst)
			{
				if (alignFirst && (_bit & 7) != 0)
				{
					// the line starts on the next byte boundary: skip the fill bits, which must be zeros.
					var aligned = (_bit + 7) & ~7L;
					if (CountZeros() >= aligned - _bit)
						_bit = aligned;
				}

				// an EOL is eleven or more zeros (code plus fill) followed by a 1.
				var zeros = CountZeros();
				if (zeros >= 11 && _bit + zeros < _totalBits)
				{
					_bit += zeros + 1;
					return true;
				}

				// no EOL before the fill bits: the line itself may start on the byte boundary.
				if (!alignFirst && _parameters.EncodedByteAlign && (_bit & 7) != 0)
				{
					var aligned = (_bit + 7) & ~7L;
					if (zeros >= aligned - _bit)
					{
						_bit = aligned;
						zeros = CountZeros();
						if (zeros >= 11 && _bit + zeros < _totalBits)
						{
							_bit += zeros + 1;
							return true;
						}
					}
				}

				// only zeros up to the end of the data: nothing more to decode.
				if (zeros >= 11)
					_bit = _totalBits;

				return false;
			}

			// returns true when another EOL follows (with its tag bit in Group 3 2D): the RTC or EOFB pattern.
			private bool IsEndOfBlock(int k)
			{
				var saved = _bit;
				if (k > 0)
					_bit++;

				var zeros = CountZeros();
				if (zeros >= 11 && _bit + zeros < _totalBits)
					return true;

				// trailing zeros only: also the end.
				if (_bit + zeros >= _totalBits)
					return true;

				_bit = saved;
				return false;
			}

			// moves to the next EOL code after a damaged line; returns false when there is none.
			private bool SeekEndOfLine()
			{
				var zeros = 0;
				while (_bit < _totalBits)
				{
					// skip whole zero bytes quickly.
					if ((_bit & 7) == 0 && _data[_bit >> 3] == 0)
					{
						zeros += 8;
						_bit += 8;
						continue;
					}

					if (ReadBit() == 0)
					{
						zeros++;
						continue;
					}

					if (zeros >= 11)
					{
						// position on the EOL so that the line loop consumes it.
						_bit -= 12;
						return true;
					}

					zeros = 0;
				}

				return false;
			}

			// counts the zero bits at the current position (up to the end of the data).
			private int CountZeros()
			{
				var position = _bit;
				var zeros = 0;
				while (position < _totalBits)
				{
					var value = Peek24At(position);
					if (value != 0)
					{
						var mask = 0x800000;
						while ((value & mask) == 0)
						{
							mask >>= 1;
							zeros++;
						}

						return zeros;
					}

					zeros += 24;
					position += 24;
				}

				return (int)Math.Min(int.MaxValue, _totalBits - _bit);
			}

			// returns the next `count` (1-24) bits without consuming them; zeros past the end.
			private int Peek(int count)
			{
				return Peek24At(_bit) >> (24 - count);
			}

			// returns the 24 bits at `position`.
			private int Peek24At(long position)
			{
				var index = (int)(position >> 3);
				var data = _data;
				uint word;
				if (index + 3 < data.Length)
				{
					word = (uint)(data[index] << 24 | data[index + 1] << 16 | data[index + 2] << 8 | data[index + 3]);
				}
				else
				{
					word = 0;
					for (int i = 0; i < 4; i++)
					{
						word <<= 8;
						if (index + i < data.Length)
							word |= data[index + i];
					}
				}

				return (int)((word << (int)(position & 7)) >> 8);
			}

			// consumes `count` bits.
			private void Skip(int count)
			{
				_bit += count;
			}

			// reads one bit.
			private int ReadBit()
			{
				var value = Peek(1);
				_bit++;
				return value;
			}

			// fills the row at `offset` with white.
			private void FillWhite(int offset)
			{
				var output = _output;
				var end = offset + _stride;
				var white = _white;
				for (int i = offset; i < end; i++)
					output[i] = white;
			}
		}

		// stores three sentinels (= columns) after the changing elements, for the b1/b2 lookup.
		private static void SetSentinels(int[] changes, int count)
		{
			var columns = changes.Length - 4;
			changes[count] = columns;
			changes[count + 1] = columns;
			changes[count + 2] = columns;
		}

		// sets (set == true) or clears the bits [start, end) of a row.
		private static void FillRun(byte[] output, int rowOffset, int start, int end, bool set)
		{
			if (end <= start)
				return;

			var first = rowOffset + (start >> 3);
			var last = rowOffset + ((end - 1) >> 3);
			var firstMask = 0xFF >> (start & 7);
			var lastMask = (0xFF00 >> (((end - 1) & 7) + 1)) & 0xFF;

			if (first == last)
			{
				var mask = firstMask & lastMask;
				output[first] = (byte)(set ? output[first] | mask : output[first] & ~mask);
				return;
			}

			output[first] = (byte)(set ? output[first] | firstMask : output[first] & ~firstMask);
			var value = set ? (byte)0xFF : (byte)0x00;
			for (int i = first + 1; i < last; i++)
				output[i] = value;

			output[last] = (byte)(set ? output[last] | lastMask : output[last] & ~lastMask);
		}

		// builds a run-length lookup table from the code lists of T.4 tables 2 and 3.
		private static int[] BuildRunTable(bool white, int bits)
		{
			var table = new int[1 << bits];
			var codes = white ? WhiteCodes : BlackCodes;
			for (int i = 0; i < codes.Length; i += 3)
				AddCode(table, bits, codes[i], codes[i + 1], codes[i + 2]);

			// the extended make-up codes (1792-2560) are shared by both colours.
			for (int i = 0; i < ExtendedMakeUpCodes.Length; i += 3)
				AddCode(table, bits, ExtendedMakeUpCodes[i], ExtendedMakeUpCodes[i + 1], ExtendedMakeUpCodes[i + 2]);

			// EOL (000000000001) and the 1D uncompressed mode extension (000000001 111).
			AddCode(table, bits, 12, 0x001, RunEndOfLine);
			AddCode(table, bits, 12, 0x00F, RunUncompressed);
			return table;
		}

		// adds one code to a lookup table indexed by `bits` bits.
		private static void AddCode(int[] table, int bits, int length, int code, int run)
		{
			var shift = bits - length;
			var first = code << shift;
			var count = 1 << shift;
			for (int i = 0; i < count; i++)
				table[first + i] = (run << 8) | length;
		}

		// builds the 2D mode table (T.4 table 4).
		private static int[] BuildModeTable()
		{
			var table = new int[128];
			AddCode(table, 7, 1, 0x1, 3);                   // V0       1
			AddCode(table, 7, 3, 0x3, 4);                   // VR1      011
			AddCode(table, 7, 3, 0x2, 2);                   // VL1      010
			AddCode(table, 7, 3, 0x1, ModeHorizontal);      // H        001
			AddCode(table, 7, 4, 0x1, ModePass);            // P        0001
			AddCode(table, 7, 6, 0x3, 5);                   // VR2      000011
			AddCode(table, 7, 6, 0x2, 1);                   // VL2      000010
			AddCode(table, 7, 7, 0x3, 6);                   // VR3      0000011
			AddCode(table, 7, 7, 0x2, 0);                   // VL3      0000010
			AddCode(table, 7, 7, 0x1, ModeExtension);       // 0000001xxx
			table[0] = ModeZeros << 8;                      // 0000000: EOL or invalid (nothing consumed)
			return table;
		}

		// T.4 table 2: white terminating (0-63) and make-up (64-1728) codes, as (length, code, run) triples.
		private static readonly int[] WhiteCodes =
		{
			8, 0x35, 0, 6, 0x07, 1, 4, 0x07, 2, 4, 0x08, 3, 4, 0x0B, 4, 4, 0x0C, 5, 4, 0x0E, 6, 4, 0x0F, 7,
			5, 0x13, 8, 5, 0x14, 9, 5, 0x07, 10, 5, 0x08, 11, 6, 0x08, 12, 6, 0x03, 13, 6, 0x34, 14, 6, 0x35, 15,
			6, 0x2A, 16, 6, 0x2B, 17, 7, 0x27, 18, 7, 0x0C, 19, 7, 0x08, 20, 7, 0x17, 21, 7, 0x03, 22, 7, 0x04, 23,
			7, 0x28, 24, 7, 0x2B, 25, 7, 0x13, 26, 7, 0x24, 27, 7, 0x18, 28, 8, 0x02, 29, 8, 0x03, 30, 8, 0x1A, 31,
			8, 0x1B, 32, 8, 0x12, 33, 8, 0x13, 34, 8, 0x14, 35, 8, 0x15, 36, 8, 0x16, 37, 8, 0x17, 38, 8, 0x28, 39,
			8, 0x29, 40, 8, 0x2A, 41, 8, 0x2B, 42, 8, 0x2C, 43, 8, 0x2D, 44, 8, 0x04, 45, 8, 0x05, 46, 8, 0x0A, 47,
			8, 0x0B, 48, 8, 0x52, 49, 8, 0x53, 50, 8, 0x54, 51, 8, 0x55, 52, 8, 0x24, 53, 8, 0x25, 54, 8, 0x58, 55,
			8, 0x59, 56, 8, 0x5A, 57, 8, 0x5B, 58, 8, 0x4A, 59, 8, 0x4B, 60, 8, 0x32, 61, 8, 0x33, 62, 8, 0x34, 63,
			5, 0x1B, 64, 5, 0x12, 128, 6, 0x17, 192, 7, 0x37, 256, 8, 0x36, 320, 8, 0x37, 384, 8, 0x64, 448,
			8, 0x65, 512, 8, 0x68, 576, 8, 0x67, 640, 9, 0xCC, 704, 9, 0xCD, 768, 9, 0xD2, 832, 9, 0xD3, 896,
			9, 0xD4, 960, 9, 0xD5, 1024, 9, 0xD6, 1088, 9, 0xD7, 1152, 9, 0xD8, 1216, 9, 0xD9, 1280, 9, 0xDA, 1344,
			9, 0xDB, 1408, 9, 0x98, 1472, 9, 0x99, 1536, 9, 0x9A, 1600, 6, 0x18, 1664, 9, 0x9B, 1728
		};

		// T.4 table 3: black terminating (0-63) and make-up (64-1728) codes, as (length, code, run) triples.
		private static readonly int[] BlackCodes =
		{
			10, 0x37, 0, 3, 0x2, 1, 2, 0x3, 2, 2, 0x2, 3, 3, 0x3, 4, 4, 0x3, 5, 4, 0x2, 6, 5, 0x3, 7,
			6, 0x5, 8, 6, 0x4, 9, 7, 0x4, 10, 7, 0x5, 11, 7, 0x7, 12, 8, 0x4, 13, 8, 0x7, 14, 9, 0x18, 15,
			10, 0x17, 16, 10, 0x18, 17, 10, 0x08, 18, 11, 0x67, 19, 11, 0x68, 20, 11, 0x6C, 21, 11, 0x37, 22, 11, 0x28, 23,
			11, 0x17, 24, 11, 0x18, 25, 12, 0xCA, 26, 12, 0xCB, 27, 12, 0xCC, 28, 12, 0xCD, 29, 12, 0x68, 30, 12, 0x69, 31,
			12, 0x6A, 32, 12, 0x6B, 33, 12, 0xD2, 34, 12, 0xD3, 35, 12, 0xD4, 36, 12, 0xD5, 37, 12, 0xD6, 38, 12, 0xD7, 39,
			12, 0x6C, 40, 12, 0x6D, 41, 12, 0xDA, 42, 12, 0xDB, 43, 12, 0x54, 44, 12, 0x55, 45, 12, 0x56, 46, 12, 0x57, 47,
			12, 0x64, 48, 12, 0x65, 49, 12, 0x52, 50, 12, 0x53, 51, 12, 0x24, 52, 12, 0x37, 53, 12, 0x38, 54, 12, 0x27, 55,
			12, 0x28, 56, 12, 0x58, 57, 12, 0x59, 58, 12, 0x2B, 59, 12, 0x2C, 60, 12, 0x5A, 61, 12, 0x66, 62, 12, 0x67, 63,
			10, 0x0F, 64, 12, 0xC8, 128, 12, 0xC9, 192, 12, 0x5B, 256, 12, 0x33, 320, 12, 0x34, 384, 12, 0x35, 448,
			13, 0x6C, 512, 13, 0x6D, 576, 13, 0x4A, 640, 13, 0x4B, 704, 13, 0x4C, 768, 13, 0x4D, 832, 13, 0x72, 896,
			13, 0x73, 960, 13, 0x74, 1024, 13, 0x75, 1088, 13, 0x76, 1152, 13, 0x77, 1216, 13, 0x52, 1280, 13, 0x53, 1344,
			13, 0x54, 1408, 13, 0x55, 1472, 13, 0x5A, 1536, 13, 0x5B, 1600, 13, 0x64, 1664, 13, 0x65, 1728
		};

		// T.4 table 3 (continued): the extended make-up codes shared by both colours.
		private static readonly int[] ExtendedMakeUpCodes =
		{
			11, 0x08, 1792, 11, 0x0C, 1856, 11, 0x0D, 1920, 12, 0x12, 1984, 12, 0x13, 2048, 12, 0x14, 2112,
			12, 0x15, 2176, 12, 0x16, 2240, 12, 0x17, 2304, 12, 0x1C, 2368, 12, 0x1D, 2432, 12, 0x1E, 2496, 12, 0x1F, 2560
		};

		#endregion
	}
}
