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

// Type 1 font programs in PFA or PFB form (Adobe Type 1 Font Format; ISO 32000-2, 9.9 FontFile).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// A Type 1 font: the cleartext font dictionary and the eexec-encrypted Private dictionary with
	/// the Subrs and CharStrings.
	/// </summary>
	/// <remarks>
	/// The PDF <c>/Length1</c> and <c>/Length2</c> entries are ignored because they are often wrong:
	/// the encrypted section is located by the <c>eexec</c> keyword and its encoding (hex or binary) is
	/// detected from the data.
	/// </remarks>
	internal sealed class Type1Font : FontProgram
	{
		#region Nested types

		/// <summary>
		/// A minimal PostScript tokenizer over bytes that can also hand out raw binary data.
		/// </summary>
		private sealed class Lexer
		{
			// the data being scanned.
			private readonly byte[] _data;

			/// <summary>
			/// Initializes a lexer.
			/// </summary>
			/// <param name="data">The data to scan.</param>
			public Lexer(byte[] data)
			{
				_data = data;
			}

			/// <summary>
			/// Gets or sets the current position.
			/// </summary>
			public int Position { get; set; }

			/// <summary>
			/// Returns the next token, or null at the end of the data. Names keep their leading slash;
			/// strings are returned with their parentheses; delimiters are returned as single tokens.
			/// </summary>
			/// <returns>The token, or null.</returns>
			public string Next()
			{
				var d = _data;
				var p = this.Position;

				// skip whitespace and comments.
				while (p < d.Length)
				{
					var c = d[p];
					if (c == '%')
					{
						while (p < d.Length && d[p] != '\r' && d[p] != '\n')
							p++;
					}
					else if (IsWhitespace(c))
					{
						p++;
					}
					else
					{
						break;
					}
				}

				if (p >= d.Length)
				{
					this.Position = p;
					return null;
				}

				var start = p;
				var ch = d[p];
				string token;
				switch (ch)
				{
					case (byte)'(':
						p = SkipString(p);
						token = Text(start, Math.Min(p, d.Length) - start);
						break;

					case (byte)'<':
						if (p + 1 < d.Length && d[p + 1] == '<')
						{
							p += 2;
							token = "<<";
						}
						else
						{
							while (p < d.Length && d[p] != '>')
								p++;

							p++;
							token = Text(start, Math.Min(p, d.Length) - start);
						}
						break;

					case (byte)'>':
						p += p + 1 < d.Length && d[p + 1] == '>' ? 2 : 1;
						token = Text(start, p - start);
						break;

					case (byte)'[':
					case (byte)']':
					case (byte)'{':
					case (byte)'}':
						p++;
						token = ((char)ch).ToString();
						break;

					default:
						// a name or a regular token: up to the next whitespace or delimiter.
						p++;
						while (p < d.Length && !IsWhitespace(d[p]) && !IsDelimiter(d[p]))
							p++;

						token = Text(start, p - start);
						break;
				}

				this.Position = p;
				return token;
			}

			/// <summary>
			/// Returns the next token as a number.
			/// </summary>
			/// <param name="value">Receives the number.</param>
			/// <returns>False when the next token is not a number.</returns>
			public bool NextNumber(out double value)
			{
				return TryParseNumber(Next(), out value);
			}

			/// <summary>
			/// Reads <paramref name="length"/> raw bytes after the single space that follows an RD token.
			/// </summary>
			/// <param name="length">The number of bytes.</param>
			/// <returns>The bytes, or null when the data is truncated.</returns>
			public byte[] ReadBinary(int length)
			{
				var start = this.Position + 1;
				if (length < 0 || start + length > _data.Length)
				{
					this.Position = _data.Length;
					return null;
				}

				var bytes = new byte[length];
				Buffer.BlockCopy(_data, start, bytes, 0, length);
				this.Position = start + length;
				return bytes;
			}

			// skips a balanced, possibly nested, string with backslash escapes.
			private int SkipString(int p)
			{
				var depth = 0;
				while (p < _data.Length)
				{
					var c = _data[p++];
					if (c == '\\')
						p++;
					else if (c == '(')
						depth++;
					else if (c == ')' && --depth == 0)
						break;
				}

				return p;
			}

			// returns a range of bytes as Latin-1 text.
			private string Text(int start, int length)
			{
				var chars = new char[length];
				for (int i = 0; i < length; i++)
					chars[i] = (char)_data[start + i];

				return new string(chars);
			}
		}

		/// <summary>
		/// A decoded glyph: outline and advance width.
		/// </summary>
		private sealed class GlyphRecord
		{
			/// <summary>
			/// The outline.
			/// </summary>
			public GlyphPath Path;

			/// <summary>
			/// The advance width in glyph space.
			/// </summary>
			public double Width;
		}

		#endregion

		#region Fields

		// the initial keys of the Type 1 encryption (Adobe Type 1 Font Format, 7.1).

		/// <summary>The initial key r of the eexec encryption of the private part of the font.</summary>
		private const int EexecKey = 55665;

		/// <summary>The initial key r of the charstring and Subrs encryption.</summary>
		private const int CharStringKey = 4330;

		// the limit of nested seac compositions.
		private const int MaxSeacDepth = 4;

		// the largest Subrs or CharStrings count accepted, to bound allocations.
		private const int MaxEntries = 65536;

		// the glyph names, by glyph id.
		private readonly List<string> _names = new List<string>();

		// the encrypted charstrings, by glyph id.
		private readonly List<byte[]> _charStrings = new List<byte[]>();

		// glyph name to glyph id.
		private readonly Dictionary<string, int> _nameToGlyph = new Dictionary<string, int>(StringComparer.Ordinal);

		// the encrypted subroutines, then decrypted once the final lenIV is known.
		private byte[][] _subrs = Array.Empty<byte[]>();

		// the number of random bytes at the start of each charstring; -1 means not encrypted.
		private int _lenIV = 4;

		// the explicit /Encoding array, or null for StandardEncoding.
		private string[] _encoding;

		// the decoded glyphs.
		private GlyphCache<GlyphRecord> _glyphs;

		// the cache factory, allocated once.
		private readonly Func<int, GlyphRecord> _decode;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes an empty font.
		/// </summary>
		private Type1Font()
		{
			_decode = gid => Decode(gid, 0);
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns <see cref="FontProgramKind.Type1"/>.
		/// </summary>
		public override FontProgramKind Kind => FontProgramKind.Type1;

		/// <summary>
		/// Returns the explicit <c>/Encoding</c> array, or null when the font uses StandardEncoding.
		/// </summary>
		public override string[] BuiltInEncoding => _encoding;

		#endregion

		#region Methods

		/// <summary>
		/// Parses a Type 1 font in PFB or PFA form.
		/// </summary>
		/// <param name="data">The font file.</param>
		/// <returns>The font, or null when no glyphs could be read.</returns>
		public static Type1Font TryParse(byte[] data)
		{
			try
			{
				if (data == null || data.Length < 16)
					return null;

				var font = new Type1Font();
				return font.Parse(data) ? font : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>
		/// Returns the name of a glyph.
		/// </summary>
		/// <param name="glyphId">The glyph id: the order of the CharStrings, with .notdef first.</param>
		/// <returns>The glyph name, or null.</returns>
		public override string GetGlyphName(int glyphId)
		{
			return glyphId >= 0 && glyphId < _names.Count ? _names[glyphId] : null;
		}

		/// <summary>
		/// Returns the id of a named glyph.
		/// </summary>
		/// <param name="glyphName">The glyph name.</param>
		/// <returns>The glyph id, or -1.</returns>
		public override int GetGlyphId(string glyphName)
		{
			return glyphName != null && _nameToGlyph.TryGetValue(glyphName, out var id) ? id : -1;
		}

		/// <summary>
		/// Returns the advance width from the glyph's hsbw or sbw operator.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The width in glyph space, or 0.</returns>
		public override double GetAdvanceWidth(int glyphId)
		{
			if (glyphId < 0 || glyphId >= this.GlyphCount)
				return 0;

			return _glyphs.Get(glyphId, _decode).Width;
		}

		/// <summary>
		/// Returns the outline of a glyph.
		/// </summary>
		/// <param name="glyphId">The glyph id.</param>
		/// <returns>The outline, or <see cref="GlyphPath.Empty"/>.</returns>
		public override GlyphPath GetGlyphPath(int glyphId)
		{
			if (glyphId < 0 || glyphId >= this.GlyphCount)
				return GlyphPath.Empty;

			return _glyphs.Get(glyphId, _decode).Path;
		}

		/// <summary>
		/// Decrypts eexec or charstring data.
		/// </summary>
		/// <param name="data">The encrypted bytes.</param>
		/// <param name="offset">The first byte to decrypt.</param>
		/// <param name="length">The number of bytes to decrypt.</param>
		/// <param name="key">The initial key: 55665 for eexec, 4330 for charstrings.</param>
		/// <param name="skip">The number of leading random bytes to drop from the result.</param>
		/// <returns>The plain bytes.</returns>
		public static byte[] Decrypt(byte[] data, int offset, int length, int key, int skip)
		{
			skip = Math.Max(0, Math.Min(skip, length));
			var result = new byte[length - skip];
			var r = key;
			for (int i = 0; i < length; i++)
			{
				var cipher = data[offset + i];
				var plain = (byte)(cipher ^ (r >> 8));
				r = ((cipher + r) * 52845 + 22719) & 0xFFFF;
				if (i >= skip)
					result[i - skip] = plain;
			}

			return result;
		}

		#endregion

		#region Implementation

		// splits the font into its cleartext and encrypted parts and parses both.
		private bool Parse(byte[] data)
		{
			byte[] clear, encrypted;
			if (data[0] == 0x80 && data[1] == 0x01)
				SplitPfb(data, out clear, out encrypted);
			else
				SplitPfa(data, out clear, out encrypted);

			ParseCleartext(clear);

			if (encrypted != null && encrypted.Length > 4)
			{
				var plain = DecryptSection(encrypted);
				ParsePrivate(plain);
			}
			else
			{
				// a font without eexec section keeps its Private dictionary in clear.
				ParsePrivate(clear);
			}

			// a binary section that starts with whitespace bytes was cut wrongly: retry at the nearby offsets.
			if (_charStrings.Count == 0 && data[0] != 0x80)
				RetryBinarySection(data);

			if (_charStrings.Count == 0)
				return false;

			// decrypt the subroutines now that lenIV is final.
			for (int i = 0; i < _subrs.Length; i++)
			{
				if (_subrs[i] != null)
					_subrs[i] = DecryptCharString(_subrs[i]);
			}

			this.GlyphCount = _charStrings.Count;
			_glyphs = new GlyphCache<GlyphRecord>(this.GlyphCount);
			return true;
		}

		// decrypts the bytes after 'eexec' at each of the first few offsets until the CharStrings are found.
		private void RetryBinarySection(byte[] data)
		{
			var eexec = IndexOf(data, "eexec", 0);
			if (eexec < 0)
				return;

			for (int start = eexec + 5; start < eexec + 12 && start < data.Length && _charStrings.Count == 0; start++)
			{
				var plain = Decrypt(data, start, data.Length - start, EexecKey, 4);
				if (IndexOf(plain, "/CharStrings", 0) >= 0)
				{
					_lenIV = 4;
					_subrs = Array.Empty<byte[]>();
					ParsePrivate(plain);
				}
			}
		}

		// PFB: segments of 0x80, a type byte (1 ASCII, 2 binary, 3 end) and a little-endian length.
		private static void SplitPfb(byte[] data, out byte[] clear, out byte[] encrypted)
		{
			var clearPart = new List<byte>();
			var binaryPart = new List<byte>();
			var p = 0;
			while (p + 6 <= data.Length && data[p] == 0x80)
			{
				var type = data[p + 1];
				if (type == 3)
					break;

				var length = data[p + 2] | (data[p + 3] << 8) | (data[p + 4] << 16) | (data[p + 5] << 24);
				p += 6;
				if (length < 0 || length > data.Length - p)
					length = data.Length - p;

				// the ASCII segments after the binary one hold only the trailer (zeros and cleartomark).
				if (type == 1 && binaryPart.Count == 0)
					AddRange(clearPart, data, p, length);
				else if (type == 2)
					AddRange(binaryPart, data, p, length);

				p += length;
			}

			clear = clearPart.ToArray();
			encrypted = binaryPart.ToArray();

			// some PFB files hold hex in the binary segment, or have the eexec data in the ASCII one.
			if (encrypted.Length == 0)
				SplitPfa(clear, out clear, out encrypted);
			else
				encrypted = DecodeHexIfNeeded(encrypted, 0) ?? encrypted;
		}

		// appends a range of bytes to a list.
		private static void AddRange(List<byte> list, byte[] data, int offset, int length)
		{
			for (int i = 0; i < length; i++)
				list.Add(data[offset + i]);
		}

		// PFA or raw PDF FontFile: the encrypted part starts after 'eexec' and whitespace.
		private static void SplitPfa(byte[] data, out byte[] clear, out byte[] encrypted)
		{
			var eexec = IndexOf(data, "currentfile eexec", 0);
			eexec = eexec >= 0 ? eexec + 12 : IndexOf(data, "eexec", 0);
			if (eexec < 0)
			{
				clear = data;
				encrypted = null;
				return;
			}

			var start = eexec + 5;
			clear = new byte[start];
			Buffer.BlockCopy(data, 0, clear, 0, start);

			// skip the end of line; binary data may start with whitespace bytes, so only skip CR/LF and a few blanks.
			var p = start;
			while (p < data.Length && (data[p] == '\r' || data[p] == '\n' || data[p] == ' ' || data[p] == '\t') && p - start < 4)
				p++;

			var hex = DecodeHexIfNeeded(data, p);
			if (hex != null)
			{
				encrypted = hex;
				return;
			}

			encrypted = new byte[data.Length - p];
			Buffer.BlockCopy(data, p, encrypted, 0, encrypted.Length);
		}

		// returns the decoded bytes when the data at offset is hex text, otherwise null.
		private static byte[] DecodeHexIfNeeded(byte[] data, int offset)
		{
			// the spec's test: the first four bytes are hex digits.
			for (int i = 0; i < 4; i++)
			{
				if (offset + i >= data.Length || HexValue(data[offset + i]) < 0)
					return null;
			}

			var result = new List<byte>((data.Length - offset) / 2);
			var high = -1;
			for (int p = offset; p < data.Length; p++)
			{
				var c = data[p];
				if (IsWhitespace(c))
					continue;

				var v = HexValue(c);
				if (v < 0)
					break;

				if (high < 0)
				{
					high = v;
				}
				else
				{
					result.Add((byte)((high << 4) | v));
					high = -1;
				}
			}

			return result.ToArray();
		}

		// decrypts the eexec section; when the hex/binary guess produced garbage, the other reading is tried.
		private static byte[] DecryptSection(byte[] encrypted)
		{
			var plain = Decrypt(encrypted, 0, encrypted.Length, EexecKey, 4);
			if (IndexOf(plain, "/CharStrings", 0) >= 0 || IndexOf(plain, "/Subrs", 0) >= 0)
				return plain;

			// the binary data may have been mistaken for hex or the other way round.
			var hex = DecodeHexIfNeeded(encrypted, 0);
			if (hex != null)
			{
				var other = Decrypt(hex, 0, hex.Length, EexecKey, 4);
				if (IndexOf(other, "/CharStrings", 0) >= 0)
					return other;
			}

			return plain;
		}

		// reads the font dictionary entries of the cleartext part.
		private void ParseCleartext(byte[] clear)
		{
			var lexer = new Lexer(clear);
			string token;
			while ((token = lexer.Next()) != null)
			{
				switch (token)
				{
					case "/FontName":
					{
						var name = lexer.Next();
						if (name != null && name.StartsWith("/", StringComparison.Ordinal) && this.FontName == null)
							this.FontName = name.Substring(1);
						break;
					}

					case "/FontMatrix":
					{
						var values = ReadNumberArray(lexer, 6);
						if (IsValidMatrix(values))
							this.FontMatrix = values;
						break;
					}

					case "/FontBBox":
					{
						var values = ReadNumberArray(lexer, 4);
						if (values != null && values.Length == 4)
							this.FontBBox = values;
						break;
					}

					case "/Weight":
					{
						var weight = lexer.Next();
						if (weight != null && weight.StartsWith("(", StringComparison.Ordinal))
							this.Weight = ParseWeight(weight.Trim('(', ')'));
						break;
					}

					case "/ItalicAngle":
						if (lexer.NextNumber(out var angle) && angle != 0)
							this.IsItalic = true;
						break;

					case "/Encoding":
						ParseEncoding(lexer);
						break;
				}
			}
		}

		// reads '/Encoding StandardEncoding def' or an explicit array of 'dup code /name put' entries.
		private void ParseEncoding(Lexer lexer)
		{
			var start = lexer.Position;
			var first = lexer.Next();
			if (first == null || first == "StandardEncoding")
				return;

			var encoding = new string[256];
			var found = false;
			string previous2 = null, previous1 = null;
			string token = first;
			while (token != null)
			{
				// 'def' or 'readonly def' ends the array; the loop may also hit the next key on broken fonts.
				if (token == "def" || token == "/FontName" || token == "/FontMatrix" || token == "currentdict")
					break;

				// match 'dup <code> /<name> put'.
				if (token == "put" && previous1 != null && previous1.StartsWith("/", StringComparison.Ordinal) &&
					TryParseNumber(previous2, out var code) && code >= 0 && code < 256)
				{
					encoding[(int)code] = previous1.Substring(1);
					found = true;
				}

				previous2 = previous1;
				previous1 = token;
				token = lexer.Next();
			}

			if (found)
				_encoding = encoding;
			else
				lexer.Position = start;
		}

		// reads up to 'count' numbers enclosed in [ ] or { }.
		private static double[] ReadNumberArray(Lexer lexer, int count)
		{
			var open = lexer.Next();
			if (open != "[" && open != "{")
				return null;

			var values = new List<double>();
			string token;
			while ((token = lexer.Next()) != null && token != "]" && token != "}")
			{
				if (TryParseNumber(token, out var value))
					values.Add(value);
				else
					return null;
			}

			return values.Count == count ? values.ToArray() : null;
		}

		// reads lenIV, Subrs and CharStrings from the decrypted Private dictionary.
		private void ParsePrivate(byte[] plain)
		{
			var lexer = new Lexer(plain);
			string token;
			while ((token = lexer.Next()) != null)
			{
				switch (token)
				{
					case "/lenIV":
						if (lexer.NextNumber(out var lenIV) && lenIV >= -1 && lenIV < 64)
							_lenIV = (int)lenIV;
						break;

					case "/Subrs":
						if (_subrs.Length == 0)
							ParseSubrs(lexer);
						break;

					case "/CharStrings":
						if (_charStrings.Count == 0)
							ParseCharStrings(lexer);
						break;

					case "/FontName":
					{
						// some fonts only name themselves in the encrypted part.
						var name = lexer.Next();
						if (name != null && name.StartsWith("/", StringComparison.Ordinal) && this.FontName == null)
							this.FontName = name.Substring(1);
						break;
					}
				}
			}
		}

		// reads '/Subrs n array' followed by 'dup i len RD bytes NP' entries.
		private void ParseSubrs(Lexer lexer)
		{
			if (!lexer.NextNumber(out var count) || count < 0)
				return;

			var subrs = new byte[(int)Math.Min(count, MaxEntries)][];
			while (true)
			{
				var mark = lexer.Position;
				var token = lexer.Next();
				if (token == null)
					break;

				if (token == "array" || token == "NP" || token == "|" || token == "noaccess" || token == "put")
					continue;

				if (token != "dup")
				{
					lexer.Position = mark;
					break;
				}

				if (!lexer.NextNumber(out var index) || !lexer.NextNumber(out var length))
					break;

				// the RD token (often 'RD' or '-|') is followed by exactly one space and the binary data.
				lexer.Next();
				var bytes = lexer.ReadBinary((int)length);
				if (bytes == null)
					break;

				if (index >= 0 && index < subrs.Length)
					subrs[(int)index] = bytes;
			}

			_subrs = subrs;
		}

		// reads '/CharStrings n dict dup begin' followed by '/name len RD bytes ND' entries, up to 'end'.
		private void ParseCharStrings(Lexer lexer)
		{
			string token;
			while ((token = lexer.Next()) != null && token != "end")
			{
				if (!token.StartsWith("/", StringComparison.Ordinal))
					continue;

				var mark = lexer.Position;
				if (!lexer.NextNumber(out var length))
				{
					lexer.Position = mark;
					continue;
				}

				lexer.Next();
				var bytes = lexer.ReadBinary((int)length);
				if (bytes == null)
					break;

				var name = token.Substring(1);
				if (_nameToGlyph.ContainsKey(name) || _charStrings.Count >= MaxEntries)
					continue;

				_nameToGlyph.Add(name, _names.Count);
				_names.Add(name);
				_charStrings.Add(bytes);
			}

			// move .notdef to glyph 0, the conventional position.
			if (_nameToGlyph.TryGetValue(".notdef", out var notdef) && notdef != 0)
			{
				Swap(_names, 0, notdef);
				Swap(_charStrings, 0, notdef);
				_nameToGlyph[_names[0]] = 0;
				_nameToGlyph[_names[notdef]] = notdef;
			}
		}

		// swaps two list entries.
		private static void Swap<T>(List<T> list, int a, int b)
		{
			var t = list[a];
			list[a] = list[b];
			list[b] = t;
		}

		// decrypts a charstring or subroutine with the final lenIV.
		private byte[] DecryptCharString(byte[] data)
		{
			if (_lenIV < 0)
				return data;

			return Decrypt(data, 0, data.Length, CharStringKey, _lenIV);
		}

		// runs the charstring of a glyph; never throws.
		private GlyphRecord Decode(int glyphId, int depth)
		{
			var record = new GlyphRecord { Path = GlyphPath.Empty };
			try
			{
				var builder = new GlyphPathBuilder();
				var interpreter = new Type1CharString(_subrs, builder);
				interpreter.Execute(DecryptCharString(_charStrings[glyphId]));
				record.Width = interpreter.Width;

				if (interpreter.IsSeac && depth < MaxSeacDepth)
				{
					var standard = Encodings.Standard;
					var baseCode = interpreter.BaseCode;
					var accentCode = interpreter.AccentCode;
					if (baseCode >= 0 && baseCode < standard.Length && accentCode >= 0 && accentCode < standard.Length)
					{
						var baseGid = GetGlyphId(standard[baseCode]);
						var accentGid = GetGlyphId(standard[accentCode]);
						if (baseGid >= 0)
							builder.Append(Decode(baseGid, depth + 1).Path, 0, 0);

						if (accentGid >= 0)
							builder.Append(Decode(accentGid, depth + 1).Path, (float)interpreter.AccentX, (float)interpreter.AccentY);
					}
				}

				record.Path = builder.ToPath();
			}
			catch (Exception)
			{
				// keep what was decoded: an empty outline.
			}

			return record;
		}

		// parses a PostScript number, including radix numbers such as 8#377.
		private static bool TryParseNumber(string token, out double value)
		{
			value = 0;
			if (string.IsNullOrEmpty(token))
				return false;

			if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
				return true;

			var hash = token.IndexOf('#');
			if (hash > 0 && int.TryParse(token.Substring(0, hash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var radix) && radix >= 2 && radix <= 36)
			{
				long result = 0;
				for (int i = hash + 1; i < token.Length; i++)
				{
					var digit = char.IsDigit(token[i]) ? token[i] - '0' : char.ToLowerInvariant(token[i]) - 'a' + 10;
					if (digit < 0 || digit >= radix || result > int.MaxValue)
						return false;

					result = result * radix + digit;
				}

				value = result;
				return true;
			}

			return false;
		}

		// finds an ASCII string in bytes.
		private static int IndexOf(byte[] data, string value, int start)
		{
			var first = (byte)value[0];
			for (int i = Math.Max(0, start); i <= data.Length - value.Length; i++)
			{
				if (data[i] != first)
					continue;

				var match = true;
				for (int k = 1; k < value.Length; k++)
				{
					if (data[i + k] != value[k])
					{
						match = false;
						break;
					}
				}

				if (match)
					return i;
			}

			return -1;
		}

		// returns the value of a hex digit, or -1.
		private static int HexValue(byte c)
		{
			if (c >= '0' && c <= '9')
				return c - '0';

			if (c >= 'a' && c <= 'f')
				return c - 'a' + 10;

			if (c >= 'A' && c <= 'F')
				return c - 'A' + 10;

			return -1;
		}

		// PostScript whitespace.
		private static bool IsWhitespace(byte c)
		{
			return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == 0;
		}

		// PostScript delimiters.
		private static bool IsDelimiter(byte c)
		{
			return c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
		}

		#endregion
	}
}
