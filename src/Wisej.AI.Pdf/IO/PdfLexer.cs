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

// Tokenizer for PDF file and content-stream syntax (ISO 32000-2, 7.2).

using System;
using System.IO;
using System.Text;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.IO
{
	/// <summary>
	/// Identifies the kind of token returned by <see cref="PdfLexer"/>.
	/// </summary>
	internal enum PdfTokenType
	{
		/// <summary>End of the input.</summary>
		EndOfInput,

		/// <summary>An integer or real number.</summary>
		Number,

		/// <summary>A literal or hexadecimal string.</summary>
		String,

		/// <summary>A name such as <c>/Type</c>.</summary>
		Name,

		/// <summary>A bare keyword such as <c>obj</c>, <c>true</c> or a content operator.</summary>
		Keyword,

		/// <summary>The <c>[</c> delimiter.</summary>
		ArrayStart,

		/// <summary>The <c>]</c> delimiter.</summary>
		ArrayEnd,

		/// <summary>The <c>&lt;&lt;</c> delimiter.</summary>
		DictionaryStart,

		/// <summary>The <c>&gt;&gt;</c> delimiter.</summary>
		DictionaryEnd,

		/// <summary>A <c>{</c> delimiter (PostScript calculator functions).</summary>
		ProcedureStart,

		/// <summary>A <c>}</c> delimiter (PostScript calculator functions).</summary>
		ProcedureEnd,
	}

	/// <summary>
	/// Splits a byte buffer into PDF tokens.
	/// </summary>
	/// <remarks>
	/// The lexer works directly on a byte array without copying and is deliberately lenient:
	/// malformed tokens are skipped or repaired instead of throwing, because real-world files
	/// frequently violate the specification in small ways.
	/// </remarks>
	internal sealed class PdfLexer
	{
		#region Character classes

		// character class table: 0 = regular, 1 = whitespace, 2 = delimiter.
		private static readonly byte[] _class = BuildClassTable();

		/// <summary>
		/// Builds the character class table from the whitespace and delimiter sets of ISO 32000-2, 7.2.3.
		/// </summary>
		/// <returns>A 256-entry table: 0 = regular, 1 = whitespace, 2 = delimiter.</returns>
		private static byte[] BuildClassTable()
		{
			var table = new byte[256];
			foreach (var c in new byte[] { 0, 9, 10, 12, 13, 32 })
				table[c] = 1;

			foreach (var c in "()<>[]{}/%")
				table[c] = 2;

			return table;
		}

		/// <summary>
		/// Returns true when <paramref name="c"/> is a PDF whitespace character.
		/// </summary>
		/// <param name="c">The byte to test.</param>
		/// <returns>True for NUL, TAB, LF, FF, CR and SPACE.</returns>
		public static bool IsWhitespace(int c) => c >= 0 && c < 256 && _class[c] == 1;

		/// <summary>
		/// Returns true when <paramref name="c"/> is a PDF delimiter character.
		/// </summary>
		/// <param name="c">The byte to test.</param>
		/// <returns>True for <c>( ) &lt; &gt; [ ] { } / %</c>.</returns>
		public static bool IsDelimiter(int c) => c >= 0 && c < 256 && _class[c] == 2;

		/// <summary>
		/// Returns true when <paramref name="c"/> is a regular character (neither whitespace nor delimiter).
		/// </summary>
		/// <param name="c">The byte to test.</param>
		/// <returns>True for regular characters.</returns>
		public static bool IsRegular(int c) => c >= 0 && c < 256 && _class[c] == 0;

		#endregion

		// the buffer being tokenized; it's never copied.
		private readonly byte[] _data;

		// position past the last byte of the range.
		private readonly int _end;

		// current read position.
		private int _pos;

		// reusable buffer used to build names and strings.
		private readonly MemoryStream _bytes = new MemoryStream(64);

		/// <summary>
		/// Initializes a new instance of <see cref="PdfLexer"/> over the whole <paramref name="data"/> buffer.
		/// </summary>
		/// <param name="data">The bytes to tokenize.</param>
		public PdfLexer(byte[] data) : this(data, 0, data.Length) { }

		/// <summary>
		/// Initializes a new instance of <see cref="PdfLexer"/> over a range of <paramref name="data"/>.
		/// </summary>
		/// <param name="data">The bytes to tokenize.</param>
		/// <param name="start">Offset of the first byte.</param>
		/// <param name="length">Number of bytes in the range.</param>
		public PdfLexer(byte[] data, int start, int length)
		{
			_data = data;
			_pos = start;
			_end = Math.Min(data.Length, start + length);
		}

		#region Properties

		/// <summary>
		/// The underlying buffer.
		/// </summary>
		public byte[] Data => _data;

		/// <summary>
		/// The current read position.
		/// </summary>
		public int Position
		{
			get => _pos;
			set => _pos = Math.Max(0, Math.Min(value, _end));
		}

		/// <summary>
		/// The position past the last byte of the range.
		/// </summary>
		public int End => _end;

		/// <summary>
		/// True when there are no more bytes to read.
		/// </summary>
		public bool IsAtEnd => _pos >= _end;

		/// <summary>
		/// The type of the last token read by <see cref="Next"/>.
		/// </summary>
		public PdfTokenType TokenType { get; private set; }

		/// <summary>
		/// The text of the last keyword token, or the name value of the last name token.
		/// </summary>
		public string TokenText { get; private set; }

		/// <summary>
		/// The value of the last number token, boxed as <see cref="int"/> or <see cref="double"/>.
		/// </summary>
		/// <remarks>
		/// The value is boxed on each access: hot paths should use <see cref="TokenValue"/> and <see cref="TokenIsInteger"/>.
		/// </remarks>
		public object TokenNumber => this.TokenIsInteger ? (object)(int)this.TokenValue : this.TokenValue;

		/// <summary>
		/// The value of the last number token.
		/// </summary>
		public double TokenValue { get; private set; }

		/// <summary>
		/// True when the last number token is an integer that fits in <see cref="int"/>.
		/// </summary>
		public bool TokenIsInteger { get; private set; }

		/// <summary>
		/// The value of the last string token.
		/// </summary>
		public PdfString TokenString { get; private set; }

		/// <summary>
		/// The offset of the first byte of the last token.
		/// </summary>
		public int TokenStart { get; private set; }

		#endregion

		#region Methods

		/// <summary>
		/// Reads the next token.
		/// </summary>
		/// <returns>The type of the token; the token value is in the <c>Token*</c> properties.</returns>
		public PdfTokenType Next()
		{
			SkipWhitespaceAndComments();
			this.TokenStart = _pos;

			if (_pos >= _end)
				return this.TokenType = PdfTokenType.EndOfInput;

			var c = _data[_pos];
			switch (c)
			{
				case (byte)'[':
					_pos++;
					return this.TokenType = PdfTokenType.ArrayStart;

				case (byte)']':
					_pos++;
					return this.TokenType = PdfTokenType.ArrayEnd;

				case (byte)'{':
					_pos++;
					return this.TokenType = PdfTokenType.ProcedureStart;

				case (byte)'}':
					_pos++;
					return this.TokenType = PdfTokenType.ProcedureEnd;

				case (byte)'(':
					_pos++;
					this.TokenString = ReadLiteralString();
					return this.TokenType = PdfTokenType.String;

				case (byte)'/':
					_pos++;
					this.TokenText = ReadName();
					return this.TokenType = PdfTokenType.Name;

				case (byte)'<':
					if (_pos + 1 < _end && _data[_pos + 1] == '<')
					{
						_pos += 2;
						return this.TokenType = PdfTokenType.DictionaryStart;
					}
					_pos++;
					this.TokenString = ReadHexString();
					return this.TokenType = PdfTokenType.String;

				case (byte)'>':
					// a lone '>' is invalid; treat it as the end of a dictionary to resynchronize.
					_pos++;
					if (_pos < _end && _data[_pos] == '>')
						_pos++;
					return this.TokenType = PdfTokenType.DictionaryEnd;

				case (byte)')':
					// stray closing parenthesis: skip it.
					_pos++;
					return Next();
			}

			if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.')
			{
				if (TryReadNumber())
					return this.TokenType = PdfTokenType.Number;
			}

			this.TokenText = ReadKeyword();
			return this.TokenType = PdfTokenType.Keyword;
		}

		/// <summary>
		/// Skips whitespace characters and comments.
		/// </summary>
		public void SkipWhitespaceAndComments()
		{
			var data = _data;
			while (_pos < _end)
			{
				var c = data[_pos];
				if (_class[c] == 1)
				{
					_pos++;
				}
				else if (c == '%')
				{
					while (_pos < _end && data[_pos] != '\r' && data[_pos] != '\n')
						_pos++;
				}
				else
				{
					break;
				}
			}
		}

		/// <summary>
		/// Returns the byte at the current position without consuming it.
		/// </summary>
		/// <returns>The byte value, or -1 at the end of the input.</returns>
		public int Peek() => _pos < _end ? _data[_pos] : -1;

		/// <summary>
		/// Returns true when the bytes at <paramref name="position"/> match <paramref name="keyword"/>
		/// and are followed by a non-regular character.
		/// </summary>
		/// <param name="position">The offset to test.</param>
		/// <param name="keyword">The ASCII keyword to match.</param>
		/// <returns>True when the keyword is present at the position as a whole token.</returns>
		public bool MatchesKeyword(int position, string keyword)
		{
			if (position < 0 || position + keyword.Length > _end)
				return false;

			for (int i = 0; i < keyword.Length; i++)
			{
				if (_data[position + i] != keyword[i])
					return false;
			}

			var next = position + keyword.Length;
			return next >= _end || !IsRegular(_data[next]);
		}

		#endregion

		#region Implementation

		/// <summary>
		/// Tries to read a number token at the current position, tolerating repeated signs, extra dots and exponents.
		/// </summary>
		/// <returns>True when a number was read and stored with <see cref="SetNumber"/>; false when the token is a keyword
		/// and the position was left unchanged.</returns>
		private bool TryReadNumber()
		{
			var data = _data;
			var p = _pos;
			var negative = false;

			// leading signs; some producers write "--5" or "+-5".
			while (p < _end && (data[p] == '-' || data[p] == '+'))
			{
				if (data[p] == '-')
					negative = true;
				p++;
			}

			long integer = 0;
			var digits = 0;
			while (p < _end && data[p] >= '0' && data[p] <= '9')
			{
				if (digits < 18)
					integer = integer * 10 + (data[p] - '0');
				digits++;
				p++;
			}

			var isReal = false;
			double fraction = 0;
			if (p < _end && data[p] == '.')
			{
				isReal = true;
				p++;
				double scale = 0.1;
				while (p < _end && data[p] >= '0' && data[p] <= '9')
				{
					fraction += (data[p] - '0') * scale;
					scale *= 0.1;
					digits++;
					p++;
				}

				// tolerate "1.5.3" and "1.-5" garbage by consuming the rest of the token.
				while (p < _end && (data[p] == '.' || (data[p] >= '0' && data[p] <= '9') || data[p] == '-'))
					p++;
			}

			// exponent notation is not valid PDF but appears in the wild (e.g. "1e-05").
			if (p < _end && (data[p] == 'e' || data[p] == 'E') && digits > 0 && p + 1 < _end)
			{
				var q = p + 1;
				var expNegative = false;
				if (data[q] == '-' || data[q] == '+')
				{
					expNegative = data[q] == '-';
					q++;
				}

				if (q < _end && data[q] >= '0' && data[q] <= '9')
				{
					var exponent = 0;
					while (q < _end && data[q] >= '0' && data[q] <= '9')
					{
						exponent = Math.Min(400, exponent * 10 + (data[q] - '0'));
						q++;
					}

					var value = (integer + fraction) * Math.Pow(10, expNegative ? -exponent : exponent);
					_pos = q;
					SetNumber(negative ? -value : value, false);
					return true;
				}
			}

			if (digits == 0)
			{
				// a lone sign or dot: not a number. Treat "-" followed by a delimiter as 0 (seen in broken files).
				if (p > _pos && (p >= _end || !IsRegular(data[p])))
				{
					_pos = p;
					SetNumber(0, true);
					return true;
				}
				return false;
			}

			// a number immediately followed by letters is a keyword, e.g. "1st".
			if (p < _end && IsRegular(data[p]))
				return false;

			_pos = p;

			if (isReal)
			{
				var value = integer + fraction;
				SetNumber(negative ? -value : value, false);
			}
			else if (digits > 18 || integer > int.MaxValue)
			{
				// only the first 18 digits were accumulated: scale the rest in.
				var value = digits > 18 ? integer * Math.Pow(10, digits - 18) : (double)integer;
				SetNumber(negative ? -value : value, false);
			}
			else
			{
				SetNumber(negative ? -integer : integer, true);
			}

			return true;
		}

		/// <summary>
		/// Stores the value of the current number token.
		/// </summary>
		/// <param name="value">The numeric value.</param>
		/// <param name="isInteger">True when the value is an integer that fits in <see cref="int"/>.</param>
		private void SetNumber(double value, bool isInteger)
		{
			this.TokenValue = value;
			this.TokenIsInteger = isInteger;
		}

		/// <summary>
		/// Reads the body of a name token (after the <c>/</c>), decoding <c>#xx</c> escapes (ISO 32000-2, 7.3.5).
		/// </summary>
		/// <returns>The name, decoded as UTF-8 or as Latin-1 when the bytes are not valid UTF-8.</returns>
		private string ReadName()
		{
			var data = _data;
			var bytes = _bytes;
			bytes.SetLength(0);

			var hasHighBytes = false;
			while (_pos < _end)
			{
				var c = data[_pos];
				if (_class[c] != 0)
					break;

				if (c == '#' && _pos + 2 < _end && IsHex(data[_pos + 1]) && IsHex(data[_pos + 2]))
				{
					c = (byte)(HexValue(data[_pos + 1]) * 16 + HexValue(data[_pos + 2]));
					_pos += 3;
				}
				else
				{
					_pos++;
				}

				if (c > 127)
					hasHighBytes = true;

				bytes.WriteByte(c);
			}

			var buffer = bytes.GetBuffer();
			var length = (int)bytes.Length;

			// names are UTF-8 by convention, but fall back to Latin-1 when the bytes are not valid UTF-8.
			if (hasHighBytes)
			{
				try
				{
					return new UTF8Encoding(false, true).GetString(buffer, 0, length);
				}
				catch (DecoderFallbackException)
				{
					return Latin1(buffer, 0, length);
				}
			}

			return Latin1(buffer, 0, length);
		}

		/// <summary>
		/// Reads a run of regular characters as a keyword; consumes one byte when the run is empty to guarantee progress.
		/// </summary>
		/// <returns>The keyword text.</returns>
		private string ReadKeyword()
		{
			var data = _data;
			var start = _pos;
			while (_pos < _end && _class[data[_pos]] == 0)
				_pos++;

			// a delimiter we don't handle: consume it so the caller makes progress.
			if (_pos == start)
				_pos++;

			return Latin1(data, start, _pos - start);
		}

		/// <summary>
		/// Reads the body of a literal string (after the opening parenthesis), handling balanced parentheses,
		/// escapes and end-of-line normalization (ISO 32000-2, 7.3.4.2).
		/// </summary>
		/// <returns>The string; an unterminated string returns the bytes read up to the end of the input.</returns>
		private PdfString ReadLiteralString()
		{
			var data = _data;
			var bytes = _bytes;
			bytes.SetLength(0);

			var depth = 1;
			while (_pos < _end)
			{
				var c = data[_pos++];
				switch (c)
				{
					case (byte)'(':
						depth++;
						bytes.WriteByte(c);
						break;

					case (byte)')':
						if (--depth == 0)
							return new PdfString(bytes.ToArray());
						bytes.WriteByte(c);
						break;

					case (byte)'\r':
						// end-of-line sequences in a literal string are normalized to LF.
						if (_pos < _end && data[_pos] == '\n')
							_pos++;
						bytes.WriteByte((byte)'\n');
						break;

					case (byte)'\\':
						ReadEscape(bytes);
						break;

					default:
						bytes.WriteByte(c);
						break;
				}
			}

			return new PdfString(bytes.ToArray());
		}

		/// <summary>
		/// Decodes the escape sequence that follows a backslash in a literal string (ISO 32000-2, Table 3).
		/// </summary>
		/// <param name="bytes">The buffer that receives the decoded byte, if any.</param>
		private void ReadEscape(MemoryStream bytes)
		{
			if (_pos >= _end)
				return;

			var c = _data[_pos++];
			switch (c)
			{
				case (byte)'n':
					bytes.WriteByte((byte)'\n');
					break;

				case (byte)'r':
					bytes.WriteByte((byte)'\r');
					break;

				case (byte)'t':
					bytes.WriteByte((byte)'\t');
					break;

				case (byte)'b':
					bytes.WriteByte((byte)'\b');
					break;

				case (byte)'f':
					bytes.WriteByte((byte)'\f');
					break;

				case (byte)'\r':
					// line continuation.
					if (_pos < _end && _data[_pos] == '\n')
						_pos++;
					break;

				case (byte)'\n':
					// line continuation.
					break;

				default:
					if (c >= '0' && c <= '7')
					{
						var value = c - '0';
						for (int i = 0; i < 2 && _pos < _end && _data[_pos] >= '0' && _data[_pos] <= '7'; i++)
							value = value * 8 + (_data[_pos++] - '0');

						bytes.WriteByte((byte)value);
					}
					else
					{
						// \( \) \\ and unknown escapes: the backslash is ignored.
						bytes.WriteByte(c);
					}
					break;
			}
		}

		/// <summary>
		/// Reads the body of a hexadecimal string (after the <c>&lt;</c>), ignoring non-hex characters (ISO 32000-2, 7.3.4.3).
		/// </summary>
		/// <returns>The decoded string, flagged as hexadecimal.</returns>
		private PdfString ReadHexString()
		{
			var data = _data;
			var bytes = _bytes;
			bytes.SetLength(0);

			var high = -1;
			while (_pos < _end)
			{
				var c = data[_pos++];
				if (c == '>')
					break;

				if (!IsHex(c))
					continue;

				if (high < 0)
				{
					high = HexValue(c);
				}
				else
				{
					bytes.WriteByte((byte)(high * 16 + HexValue(c)));
					high = -1;
				}
			}

			// an odd number of digits is padded with a final 0.
			if (high >= 0)
				bytes.WriteByte((byte)(high * 16));

			return new PdfString(bytes.ToArray(), true);
		}

		/// <summary>
		/// Returns true when <paramref name="c"/> is a hexadecimal digit.
		/// </summary>
		/// <param name="c">The byte to test.</param>
		/// <returns>True for 0-9, a-f and A-F.</returns>
		public static bool IsHex(int c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

		/// <summary>
		/// Returns the value of the hexadecimal digit <paramref name="c"/>.
		/// </summary>
		/// <param name="c">A hexadecimal digit.</param>
		/// <returns>The value 0-15.</returns>
		public static int HexValue(int c)
		{
			if (c <= '9')
				return c - '0';
			if (c <= 'F')
				return c - 'A' + 10;

			return c - 'a' + 10;
		}

		/// <summary>
		/// Converts a range of bytes to a string, one character per byte.
		/// </summary>
		/// <param name="data">The source buffer.</param>
		/// <param name="start">Offset of the first byte.</param>
		/// <param name="length">Number of bytes.</param>
		/// <returns>The Latin-1 string.</returns>
		public static string Latin1(byte[] data, int start, int length)
		{
			if (length <= 0)
				return string.Empty;

			var chars = new char[length];
			for (int i = 0; i < length; i++)
				chars[i] = (char)data[start + i];

			return new string(chars);
		}

		#endregion
	}
}
