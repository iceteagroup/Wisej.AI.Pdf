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

// Parser of the PostScript CMap syntax used by embedded CMaps and ToUnicode streams
// (ISO 32000-2, 9.7.5.4 and 9.10.3; Adobe Technical Note #5014 and #5099).

using System;
using System.Collections.Generic;
using System.Text;

namespace Wisej.AI.Pdf.Fonts
{
	/// <content>
	/// The parser of embedded CMap and ToUnicode streams.
	/// </content>
	internal sealed partial class CMap
	{
		#region Fields

		// the maximum depth of usecmap chains, to stop cycles.
		private const int MaxUseCMapDepth = 8;

		// the maximum number of entries expanded from the array form of bfrange.
		private const int MaxArrayEntries = 65536;

		// true when the CMap defines /WMode explicitly, so usecmap does not override it.
		private bool _wModeDefined;

		#endregion

		#region Methods

		/// <summary>
		/// Parses an embedded CMap stream that references another embedded CMap through its <c>/UseCMap</c> entry.
		/// </summary>
		/// <param name="data">The decoded stream bytes.</param>
		/// <param name="useCMap">The CMap referenced by <c>/UseCMap</c>, or null.</param>
		/// <returns>The CMap; an empty CMap when the data is null or unreadable. Never null.</returns>
		public static CMap Parse(byte[] data, CMap useCMap)
		{
			return Parse(data, 0, useCMap);
		}

		#endregion

		#region Implementation

		// parses the CMap, resolving usecmap at the given depth.
		internal static CMap Parse(byte[] data, int depth, CMap useCMap = null)
		{
			var map = new CMap();
			try
			{
				if (data != null && data.Length > 0)
					new Parser(data, map, depth, useCMap).Run();
				else
					map.Complete(new List<CodespaceRange>(), useCMap, null);
			}
			catch (Exception)
			{
				// the parser is defensive, but a CMap must never break text extraction:
				// fall back to an empty map rather than a partially initialized one.
				map = new CMap();
				map.Complete(new List<CodespaceRange>(), null, null);
			}

			return map;
		}

		// finalizes the CMap after the mappings have been added: merges the inherited codespaces,
		// infers missing codespace ranges and builds the lookup tables.
		private void Complete(List<CodespaceRange> codespaces, CMap parent, bool[] codeLengthsSeen)
		{
			if (parent != null && !ReferenceEquals(parent, this))
			{
				_parent = parent;
				if (this.WMode == 0 && parent.WMode == 1 && !_wModeDefined)
					this.WMode = 1;

				if (this.Registry.Length == 0)
				{
					this.Registry = parent.Registry;
					this.Ordering = parent.Ordering;
					this.Supplement = parent.Supplement;
				}

				var inherited = parent.GetCodespaces();
				inherited.AddRange(codespaces);
				codespaces = inherited;
			}

			// a CMap without codespace ranges is broken: derive full ranges from the code lengths
			// used by the mappings, so that ReadCode still splits the strings sensibly.
			if (codespaces.Count == 0 && codeLengthsSeen != null)
			{
				for (int n = 1; n <= MaxCodeLength; n++)
				{
					if (codeLengthsSeen[n])
						codespaces.Add(new CodespaceRange(n, 0, n == 4 ? 0xFFFFFFFF : (1u << (n * 8)) - 1));
				}
			}

			SetCodespaces(codespaces);
			_cids.Build();
			_notdefs.Build();
			_unicode.Build();

			// a CMap that only wraps Identity-H/V (usecmap /Identity-H) behaves as the identity.
			if (parent != null && parent.IsIdentity && _cids.Count == 0 && _notdefs.Count == 0 && codespaces.Count == parent.CodespaceRangeCount)
				this.IsIdentity = true;
		}

		#endregion

		#region Parser

		/// <summary>
		/// Reads the tokens of a CMap program and fills a <see cref="CMap"/>.
		/// </summary>
		/// <remarks>
		/// The parser ignores the operand counts in front of the <c>begin...</c> operators and resynchronizes
		/// on the next operator, because real-world ToUnicode streams frequently have wrong counts,
		/// missing <c>end...</c> operators or stray tokens.
		/// </remarks>
		private sealed class Parser
		{
			/// <summary>
			/// The token kinds of the CMap syntax.
			/// </summary>
			private enum TokenKind
			{
				/// <summary>
				/// No token: the end of the data was reached.
				/// </summary>
				None,

				/// <summary>
				/// A hexadecimal string <c>&lt;...&gt;</c>; the bytes are in <c>_bytes</c>.
				/// </summary>
				Hex,

				/// <summary>
				/// A literal string <c>(...)</c>; the bytes are in <c>_bytes</c>.
				/// </summary>
				Literal,

				/// <summary>
				/// A name <c>/Name</c>; the text without the slash is in <c>_text</c>.
				/// </summary>
				Name,

				/// <summary>
				/// An integer or real number; the value is in <c>_number</c>.
				/// </summary>
				Number,

				/// <summary>
				/// An operator or other bare word, for example <c>beginbfchar</c> or <c>def</c>; the text is in <c>_text</c>.
				/// </summary>
				Keyword,

				/// <summary>
				/// The start of an array, <c>[</c>.
				/// </summary>
				ArrayBegin,

				/// <summary>
				/// The end of an array, <c>]</c>.
				/// </summary>
				ArrayEnd,

				/// <summary>
				/// Any other delimiter, such as <c>&lt;&lt;</c>, <c>&gt;&gt;</c> or a procedure brace; ignored by the parser.
				/// </summary>
				Other
			}

			// the source bytes.
			private readonly byte[] _data;

			// the read position in _data.
			private int _position;

			// the CMap being filled.
			private readonly CMap _map;

			// the depth of the usecmap chain.
			private readonly int _depth;

			// the CMap referenced by /UseCMap in the stream dictionary.
			private CMap _parent;

			// the kind of the current token.
			private TokenKind _kind;

			// the bytes of the current string token (Hex or Literal).
			private byte[] _bytes;

			// the text of the current Name or Keyword token.
			private string _text;

			// the value of the current Number token.
			private double _number;

			// the name token preceding the current one, for "/Key value" pairs.
			private string _lastName;

			// one token of lookahead pushed back by the block readers.
			private bool _pushedBack;

			// the parsed codespace ranges.
			private readonly List<CodespaceRange> _codespaces = new List<CodespaceRange>();

			// the code lengths used by the mappings, to infer missing codespace ranges.
			private readonly bool[] _lengthsSeen = new bool[MaxCodeLength + 1];

			// the destination strings of the bf mappings, in the order they were first seen.
			private readonly List<string> _strings = new List<string>();

			// destination string to its index in _strings, to store identical strings once.
			private readonly Dictionary<string, int> _stringIndex = new Dictionary<string, int>(StringComparer.Ordinal);

			/// <summary>
			/// Initializes the parser.
			/// </summary>
			/// <param name="data">The CMap bytes.</param>
			/// <param name="map">The CMap to fill.</param>
			/// <param name="depth">The depth of the usecmap chain.</param>
			/// <param name="parent">The CMap referenced by /UseCMap, or null.</param>
			public Parser(byte[] data, CMap map, int depth, CMap parent)
			{
				_data = data;
				_map = map;
				_depth = depth;
				_parent = parent;
			}

			/// <summary>
			/// Parses the whole program and completes the CMap.
			/// </summary>
			public void Run()
			{
				while (Next())
				{
					switch (_kind)
					{
						case TokenKind.Keyword:
							HandleKeyword();
							_lastName = null;
							break;

						case TokenKind.Name:
							if (_lastName == "CMapName")
							{
								_map.Name = _text;
								_lastName = null;
							}
							else
							{
								_lastName = _text;
							}

							break;

						case TokenKind.Number:
							HandleNumberValue();
							_lastName = null;
							break;

						case TokenKind.Literal:
						case TokenKind.Hex:
							HandleStringValue();
							_lastName = null;
							break;

						default:
							_lastName = null;
							break;
					}
				}

				_map._unicodeStrings = _strings.ToArray();
				_map.Complete(_codespaces, _parent, _lengthsSeen);
			}

			// "/WMode 1" and "/Supplement 2" pairs, either as "def" definitions or as dictionary entries.
			private void HandleNumberValue()
			{
				if (_lastName == "WMode")
				{
					_map.WMode = (int)_number == 1 ? 1 : 0;
					_map._wModeDefined = true;
				}
				else if (_lastName == "Supplement")
				{
					_map.Supplement = (int)_number;
				}
			}

			// "/Registry (Adobe)" and "/Ordering (Japan1)" pairs.
			private void HandleStringValue()
			{
				if (_lastName == "Registry")
					_map.Registry = Encoding.ASCII.GetString(_bytes);
				else if (_lastName == "Ordering")
					_map.Ordering = Encoding.ASCII.GetString(_bytes);
			}

			// dispatches the operators that open a mapping block, and usecmap.
			private void HandleKeyword()
			{
				switch (_text)
				{
					case "begincodespacerange":
						ReadCodespaceRanges();
						break;

					case "begincidrange":
						ReadRanges(_map._cids);
						break;

					case "begincidchar":
						ReadChars(_map._cids);
						break;

					case "beginnotdefrange":
						ReadRanges(_map._notdefs);
						break;

					case "beginnotdefchar":
						ReadChars(_map._notdefs);
						break;

					case "beginbfrange":
						ReadBfRanges();
						break;

					case "beginbfchar":
						ReadBfChars();
						break;

					case "usecmap":
						if (_lastName != null)
							_map.UseCMap = _lastName;

						if (_lastName != null && _parent == null && _depth < MaxUseCMapDepth)
							_parent = GetPredefined(_lastName, _depth + 1);

						break;
				}
			}

			// <low> <high> pairs.
			private void ReadCodespaceRanges()
			{
				while (true)
				{
					if (!NextCode(out var low, out var lowLength))
						return;

					if (!NextCode(out var high, out var highLength))
						return;

					if (lowLength == 0 || lowLength != highLength)
						continue;

					_codespaces.Add(new CodespaceRange(lowLength, (uint)low, (uint)high));
				}
			}

			// <low> <high> cid triplets (cidrange and notdefrange).
			private void ReadRanges(CMapRangeTable table)
			{
				while (true)
				{
					if (!NextCode(out var low, out var length))
						return;

					if (!NextCode(out var high, out _))
						return;

					if (!NextNumber(out var cid))
						return;

					if (length == 0)
						continue;

					// the high code is read with the length of the low code.
					_lengthsSeen[length] = true;
					table.Add(CMapRangeTable.MakeKey(low, length), CMapRangeTable.MakeKey(high, length), cid);
				}
			}

			// <code> cid pairs (cidchar and notdefchar).
			private void ReadChars(CMapRangeTable table)
			{
				while (true)
				{
					if (!NextCode(out var code, out var length))
						return;

					if (!NextNumber(out var cid))
						return;

					if (length == 0)
						continue;

					_lengthsSeen[length] = true;
					var key = CMapRangeTable.MakeKey(code, length);
					table.Add(key, key, cid);
				}
			}

			// <low> <high> <destination> or <low> <high> [<d1> <d2> ...] triplets.
			private void ReadBfRanges()
			{
				while (true)
				{
					if (!NextCode(out var low, out var length))
						return;

					if (!NextCode(out var high, out _))
						return;

					if (!Next())
						return;

					if (_kind == TokenKind.ArrayBegin)
					{
						// array form: one destination per code, which may be any string.
						var code = (long)(uint)low;
						var count = 0;
						while (Next() && _kind != TokenKind.ArrayEnd)
						{
							if (_kind == TokenKind.Keyword)
							{
								_pushedBack = true;
								break;
							}

							var text = GetDestination();
							if (text != null && code <= (uint)high && count < MaxArrayEntries)
								AddUnicode(code, code, text);

							code++;
							count++;
						}

						continue;
					}

					if (_kind == TokenKind.Keyword)
					{
						_pushedBack = true;
						return;
					}

					var destination = GetDestination();
					if (destination == null || length == 0 || (uint)high < (uint)low)
						continue;

					AddUnicode((uint)low, (uint)high, destination);
				}
			}

			// <code> <destination> pairs.
			private void ReadBfChars()
			{
				while (true)
				{
					if (!NextCode(out var code, out var length))
						return;

					if (!Next())
						return;

					if (_kind == TokenKind.Keyword)
					{
						_pushedBack = true;
						return;
					}

					var destination = GetDestination();
					if (destination != null && length > 0)
						AddUnicode((uint)code, (uint)code, destination);
				}
			}

			// adds a bf mapping, sharing identical destination strings.
			private void AddUnicode(long low, long high, string text)
			{
				if (!_stringIndex.TryGetValue(text, out var index))
				{
					index = _strings.Count;
					_strings.Add(text);
					_stringIndex[text] = index;
				}

				_map._unicode.Add(low, high, index);
			}

			// converts the current token to a destination string: UTF-16BE hex or literal strings,
			// glyph names (allowed by the CMap syntax, written by some producers) or numbers.
			private string GetDestination()
			{
				switch (_kind)
				{
					case TokenKind.Hex:
					case TokenKind.Literal:
						return DecodeUtf16(_bytes);

					case TokenKind.Name:
						return GlyphList.ToUnicode(_text);

					default:
						return null;
				}
			}

			// reads a code (hex or literal string); stops at operators. Returns false at the end of the block.
			private bool NextCode(out int code, out int length)
			{
				code = 0;
				length = 0;
				while (Next())
				{
					if (_kind == TokenKind.Keyword)
					{
						// "endcidrange" and friends end the block; any other operator is processed by the caller.
						if (!_text.StartsWith("end", StringComparison.Ordinal))
							_pushedBack = true;

						return false;
					}

					if (_kind == TokenKind.Hex || _kind == TokenKind.Literal)
					{
						var bytes = _bytes;

						// codes longer than 4 bytes are invalid: keep the length 0 so the entry is skipped.
						if (bytes.Length > 0 && bytes.Length <= MaxCodeLength)
						{
							length = bytes.Length;
							for (int i = 0; i < bytes.Length; i++)
								code = (code << 8) | bytes[i];
						}

						return true;
					}

					// skip stray tokens.
				}

				return false;
			}

			// reads a number operand; stops at operators.
			private bool NextNumber(out int value)
			{
				value = 0;
				while (Next())
				{
					if (_kind == TokenKind.Number)
					{
						value = _number < 0 ? 0 : _number > int.MaxValue ? int.MaxValue : (int)_number;
						return true;
					}

					if (_kind == TokenKind.Keyword)
					{
						if (!_text.StartsWith("end", StringComparison.Ordinal))
							_pushedBack = true;

						return false;
					}

					// a string here means the previous entry was malformed: resynchronize on it.
					if (_kind == TokenKind.Hex || _kind == TokenKind.Literal)
					{
						_pushedBack = true;
						return false;
					}
				}

				return false;
			}

			// decodes UTF-16BE destination bytes; a single byte is taken as a Latin-1 character.
			private static string DecodeUtf16(byte[] bytes)
			{
				if (bytes.Length == 0)
					return null;

				if (bytes.Length == 1)
					return ((char)bytes[0]).ToString();

				var chars = new char[(bytes.Length + 1) / 2];

				// an odd length is padded at the front, so <123> reads as U+0123.
				var odd = bytes.Length & 1;
				var j = 0;
				if (odd == 1)
					chars[j++] = (char)bytes[0];

				for (int i = odd; i + 1 < bytes.Length; i += 2)
					chars[j++] = (char)((bytes[i] << 8) | bytes[i + 1]);

				return new string(chars, 0, j);
			}

			#region Lexer

			// reads the next token; returns false at the end of the data.
			private bool Next()
			{
				if (_pushedBack)
				{
					_pushedBack = false;
					return true;
				}

				var data = _data;
				while (_position < data.Length)
				{
					var c = data[_position];

					if (IsWhitespace(c))
					{
						_position++;
						continue;
					}

					if (c == '%')
					{
						while (_position < data.Length && data[_position] != '\n' && data[_position] != '\r')
							_position++;

						continue;
					}

					switch (c)
					{
						case (byte)'<':
							if (_position + 1 < data.Length && data[_position + 1] == '<')
							{
								_position += 2;
								_kind = TokenKind.Other;
								return true;
							}

							ReadHex();
							return true;

						case (byte)'>':
							_position += _position + 1 < data.Length && data[_position + 1] == '>' ? 2 : 1;
							_kind = TokenKind.Other;
							return true;

						case (byte)'(':
							ReadLiteral();
							return true;

						case (byte)'[':
							_position++;
							_kind = TokenKind.ArrayBegin;
							return true;

						case (byte)']':
							_position++;
							_kind = TokenKind.ArrayEnd;
							return true;

						case (byte)'{':
						case (byte)'}':
						case (byte)')':
							_position++;
							_kind = TokenKind.Other;
							return true;

						case (byte)'/':
							_position++;
							_text = ReadRegular();
							_kind = TokenKind.Name;
							return true;
					}

					var word = ReadRegular();
					if (word.Length == 0)
					{
						// an unexpected delimiter: skip it so the lexer always advances.
						_position++;
						_kind = TokenKind.Other;
						return true;
					}

					if (TryParseNumber(word, out _number))
					{
						_kind = TokenKind.Number;
					}
					else
					{
						_kind = TokenKind.Keyword;
						_text = word;
					}

					return true;
				}

				_kind = TokenKind.None;
				return false;
			}

			// reads a hexadecimal string, ignoring invalid characters; an odd final digit is padded with 0.
			private void ReadHex()
			{
				var data = _data;
				_position++;

				var buffer = new List<byte>(4);
				var high = -1;
				while (_position < data.Length)
				{
					var c = data[_position++];
					if (c == '>')
						break;

					var digit = HexValue(c);
					if (digit < 0)
						continue;

					if (high < 0)
					{
						high = digit;
					}
					else
					{
						buffer.Add((byte)((high << 4) | digit));
						high = -1;
					}
				}

				if (high >= 0)
					buffer.Add((byte)(high << 4));

				_bytes = buffer.ToArray();
				_kind = TokenKind.Hex;
			}

			// reads a literal string with escapes and balanced parentheses (ISO 32000-2, 7.3.4.2).
			private void ReadLiteral()
			{
				var data = _data;
				_position++;

				var buffer = new List<byte>(4);
				var nesting = 1;
				while (_position < data.Length)
				{
					var c = data[_position++];
					if (c == '(')
					{
						nesting++;
					}
					else if (c == ')')
					{
						if (--nesting == 0)
							break;
					}
					else if (c == '\\' && _position < data.Length)
					{
						c = data[_position++];
						switch (c)
						{
							case (byte)'n':
								c = (byte)'\n';
								break;

							case (byte)'r':
								c = (byte)'\r';
								break;

							case (byte)'t':
								c = (byte)'\t';
								break;

							case (byte)'b':
								c = (byte)'\b';
								break;

							case (byte)'f':
								c = (byte)'\f';
								break;

							case (byte)'\r':
								if (_position < data.Length && data[_position] == '\n')
									_position++;

								continue;

							case (byte)'\n':
								continue;

							default:
								if (c >= '0' && c <= '7')
								{
									var value = c - '0';
									for (int i = 0; i < 2 && _position < data.Length && data[_position] >= '0' && data[_position] <= '7'; i++)
										value = (value << 3) | (data[_position++] - '0');

									c = (byte)value;
								}

								break;
						}
					}

					buffer.Add(c);
				}

				_bytes = buffer.ToArray();
				_kind = TokenKind.Literal;
			}

			// reads a run of regular characters (a keyword, a number or the body of a name).
			private string ReadRegular()
			{
				var data = _data;
				var start = _position;
				while (_position < data.Length && !IsWhitespace(data[_position]) && !IsDelimiter(data[_position]))
					_position++;

				return Encoding.ASCII.GetString(data, start, _position - start);
			}

			// parses an integer or real number without allocating.
			private static bool TryParseNumber(string word, out double value)
			{
				value = 0;
				var i = 0;
				var negative = false;
				if (word[0] == '-' || word[0] == '+')
				{
					negative = word[0] == '-';
					i++;
				}

				if (i >= word.Length)
					return false;

				var digits = 0;
				var scale = 0.0;
				for (; i < word.Length; i++)
				{
					var c = word[i];
					if (c >= '0' && c <= '9')
					{
						if (scale == 0)
						{
							value = value * 10 + (c - '0');
						}
						else
						{
							value += (c - '0') * scale;
							scale /= 10;
						}

						digits++;
					}
					else if (c == '.' && scale == 0)
					{
						scale = 0.1;
					}
					else
					{
						return false;
					}
				}

				if (negative)
					value = -value;

				return digits > 0;
			}

			// returns the value of a hexadecimal digit, or -1.
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

			// PDF whitespace characters (ISO 32000-2, table 1).
			private static bool IsWhitespace(byte c)
			{
				return c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '\f' || c == 0;
			}

			// PDF delimiter characters (ISO 32000-2, table 2).
			private static bool IsDelimiter(byte c)
			{
				return c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
			}

			#endregion
		}

		#endregion
	}
}
