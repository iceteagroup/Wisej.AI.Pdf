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

// Builds PDF objects from the tokens produced by PdfLexer.

using System;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.IO
{
	/// <summary>
	/// Parses PDF objects (direct objects and indirect object definitions) from a byte buffer.
	/// </summary>
	internal sealed class PdfParser
	{
		// maximum nesting of arrays and dictionaries, to protect against malicious input.
		private const int MaxDepth = 256;

		// source of the tokens.
		private readonly PdfLexer _lexer;

		// file that owns the parsed objects; null when parsing standalone data such as content streams.
		private readonly PdfFile _file;

		// object being parsed; used to decrypt strings with the right key.
		private PdfReference _owner;

		/// <summary>
		/// Initializes a new instance of <see cref="PdfParser"/>.
		/// </summary>
		/// <param name="lexer">The lexer that supplies the tokens.</param>
		/// <param name="file">The file that owns the objects; used to resolve references and decrypt strings. Can be null.</param>
		public PdfParser(PdfLexer lexer, PdfFile file)
		{
			_lexer = lexer;
			_file = file;
		}

		/// <summary>
		/// The underlying lexer.
		/// </summary>
		public PdfLexer Lexer => _lexer;

		#region Methods

		/// <summary>
		/// Parses the indirect object definition (<c>n g obj ... endobj</c>) at the current position.
		/// </summary>
		/// <param name="expected">The reference the caller expects to find, or null to accept any object.</param>
		/// <param name="decrypt">True to decrypt strings and streams with the object key.</param>
		/// <returns>The parsed object, or null when the bytes at the position are not the expected object.</returns>
		public object ParseIndirectObject(PdfReference expected, bool decrypt)
		{
			var lexer = _lexer;

			if (lexer.Next() != PdfTokenType.Number || !(lexer.TokenNumber is int number))
				return null;

			if (lexer.Next() != PdfTokenType.Number || !(lexer.TokenNumber is int generation))
				return null;

			if (lexer.Next() != PdfTokenType.Keyword || lexer.TokenText != "obj")
				return null;

			if (expected != null && expected.Number != number)
				return null;

			_owner = decrypt ? new PdfReference(number, generation) : null;
			try
			{
				var value = ParseObject(0);

				// check for a stream following a dictionary.
				if (value is PdfDictionary dictionary)
				{
					var position = lexer.Position;
					if (lexer.Next() == PdfTokenType.Keyword && lexer.TokenText == "stream")
						return ReadStream(dictionary, new PdfReference(number, generation), decrypt);

					lexer.Position = position;
				}

				return value;
			}
			finally
			{
				_owner = null;
			}
		}

		/// <summary>
		/// Parses the next direct object.
		/// </summary>
		/// <returns>
		/// The parsed object, a <see cref="PdfOperator"/> for unknown keywords, or null at the end of the input.
		/// </returns>
		public object ParseObject() => ParseObject(0);

		#endregion

		#region Implementation

		/// <summary>
		/// Parses the next direct object at the given nesting depth.
		/// </summary>
		/// <param name="depth">Current nesting depth of arrays and dictionaries.</param>
		/// <returns>
		/// The parsed object, a <see cref="PdfOperator"/> for keywords and unexpected delimiters, or null at the end of the input.
		/// </returns>
		private object ParseObject(int depth)
		{
			var lexer = _lexer;
			switch (lexer.Next())
			{
				case PdfTokenType.EndOfInput:
					return null;

				case PdfTokenType.Number:
					return ParseNumberOrReference();

				case PdfTokenType.String:
					return DecryptString(lexer.TokenString);

				case PdfTokenType.Name:
					return PdfName.Get(lexer.TokenText);

				case PdfTokenType.ArrayStart:
					return ParseArray(depth + 1);

				case PdfTokenType.DictionaryStart:
					return ParseDictionary(depth + 1);

				case PdfTokenType.Keyword:
					switch (lexer.TokenText)
					{
						case "true":
							return true;

						case "false":
							return false;

						case "null":
							return PdfNull.Instance;

						default:
							return PdfOperator.Get(lexer.TokenText);
					}

				case PdfTokenType.ArrayEnd:
					return PdfOperator.Get("]");

				case PdfTokenType.DictionaryEnd:
					return PdfOperator.Get(">>");

				case PdfTokenType.ProcedureStart:
					return PdfOperator.Get("{");

				case PdfTokenType.ProcedureEnd:
					return PdfOperator.Get("}");

				default:
					return null;
			}
		}

		/// <summary>
		/// Completes a number token, looking ahead for <c>g R</c> to form an indirect reference (ISO 32000-2, 7.3.10).
		/// </summary>
		/// <returns>A <see cref="PdfReference"/>, or the boxed number when no reference follows.</returns>
		private object ParseNumberOrReference()
		{
			var lexer = _lexer;
			var first = lexer.TokenNumber;
			if (!(first is int number) || number < 0)
				return first;

			var position = lexer.Position;
			if (lexer.Next() == PdfTokenType.Number && lexer.TokenNumber is int generation && generation >= 0)
			{
				if (lexer.Next() == PdfTokenType.Keyword && lexer.TokenText == "R")
					return new PdfReference(number, generation);
			}

			lexer.Position = position;
			return first;
		}

		/// <summary>
		/// Parses the items of an array after the <c>[</c> token; unterminated arrays end at the next object boundary.
		/// </summary>
		/// <param name="depth">Nesting depth of the array; deeper than <see cref="MaxDepth"/> returns an empty array.</param>
		/// <returns>The array.</returns>
		private PdfArray ParseArray(int depth)
		{
			var array = new PdfArray(_file);
			if (depth > MaxDepth)
				return array;

			var lexer = _lexer;
			while (true)
			{
				var position = lexer.Position;
				var type = lexer.Next();
				if (type == PdfTokenType.ArrayEnd || type == PdfTokenType.EndOfInput)
					break;

				// a dictionary end or an object keyword inside an array means the array was not closed.
				if (type == PdfTokenType.DictionaryEnd || (type == PdfTokenType.Keyword && IsObjectBoundary(lexer.TokenText)))
				{
					lexer.Position = position;
					break;
				}

				lexer.Position = position;
				var item = ParseObject(depth);
				if (item is PdfOperator)
					continue;

				array.Add(item);
			}

			return array;
		}

		/// <summary>
		/// Parses the entries of a dictionary after the <c>&lt;&lt;</c> token; unterminated dictionaries end at the next object boundary.
		/// </summary>
		/// <param name="depth">Nesting depth of the dictionary; deeper than <see cref="MaxDepth"/> returns an empty dictionary.</param>
		/// <returns>The dictionary.</returns>
		private PdfDictionary ParseDictionary(int depth)
		{
			var dictionary = new PdfDictionary(_file);
			if (depth > MaxDepth)
				return dictionary;

			var lexer = _lexer;
			while (true)
			{
				var type = lexer.Next();
				if (type == PdfTokenType.DictionaryEnd || type == PdfTokenType.EndOfInput)
					break;

				if (type == PdfTokenType.Keyword && IsObjectBoundary(lexer.TokenText))
				{
					// the dictionary was not closed; rewind so the caller sees the keyword.
					lexer.Position = lexer.TokenStart;
					break;
				}

				// skip anything that is not a key.
				if (type != PdfTokenType.Name)
					continue;

				var key = PdfName.Get(lexer.TokenText);

				// a key immediately followed by ">>" has no value: store null.
				var position = lexer.Position;
				var next = lexer.Next();
				lexer.Position = position;
				if (next == PdfTokenType.DictionaryEnd)
				{
					dictionary[key] = PdfNull.Instance;
					continue;
				}

				var value = ParseObject(depth);
				if (value is PdfOperator op)
				{
					if (IsObjectBoundary(op.Name))
					{
						lexer.Position = lexer.TokenStart;
						break;
					}

					continue;
				}

				// a null value is equivalent to a missing key.
				if (value != null && !(value is PdfNull))
					dictionary[key] = value;
			}

			return dictionary;
		}

		/// <summary>
		/// Returns true for the keywords that can't appear inside an object and therefore end an unterminated array or dictionary.
		/// </summary>
		/// <param name="keyword">The keyword to test.</param>
		/// <returns>True for <c>obj</c>, <c>endobj</c>, <c>stream</c> and <c>endstream</c>.</returns>
		private static bool IsObjectBoundary(string keyword)
		{
			return keyword == "endobj" || keyword == "stream" || keyword == "obj" || keyword == "endstream";
		}

		/// <summary>
		/// Reads the data of a stream after the <c>stream</c> keyword (ISO 32000-2, 7.3.8), validating <c>/Length</c>
		/// against the position of <c>endstream</c> and decrypting the data when required.
		/// </summary>
		/// <param name="dictionary">The stream dictionary.</param>
		/// <param name="reference">The reference of the stream object.</param>
		/// <param name="decrypt">True to decrypt the data with the object key.</param>
		/// <returns>The stream, with the raw (still encoded) data.</returns>
		private PdfStream ReadStream(PdfDictionary dictionary, PdfReference reference, bool decrypt)
		{
			var lexer = _lexer;
			var data = lexer.Data;
			var p = lexer.Position;

			// the "stream" keyword is followed by CRLF or LF (some writers use CR alone or spaces).
			while (p < lexer.End && data[p] == ' ')
				p++;

			if (p < lexer.End && data[p] == '\r')
				p++;

			if (p < lexer.End && data[p] == '\n')
				p++;

			var start = p;
			var length = -1;

			// /Length can be an indirect reference; resolve it only when it's safe (no recursion into ourselves).
			if (dictionary.TryGetValue(PdfNames.Length, out var lengthValue))
			{
				if (lengthValue is PdfReference lengthReference)
				{
					if (_file != null && !lengthReference.Equals(reference))
						length = PdfConvert.ToInt(_file.GetObject(lengthReference), -1);
				}
				else
				{
					length = PdfConvert.ToInt(lengthValue, -1);
				}
			}

			// validate the declared length by checking that "endstream" follows it.
			if (length < 0 || (long)start + length > lexer.End || !IsEndStreamAt(data, start + length, lexer.End))
				length = FindEndStream(data, start, lexer.End) - start;

			var raw = new byte[Math.Max(0, length)];
			Buffer.BlockCopy(data, start, raw, 0, raw.Length);

			var stream = new PdfStream(_file);
			foreach (var entry in dictionary)
				stream[entry.Key] = entry.Value;

			stream.Reference = reference;
			stream.RawData = raw;

			if (decrypt && _file?.Decryptor != null)
				stream.RawData = _file.Decryptor.DecryptStream(stream, reference);

			// move past "endstream".
			lexer.Position = start + raw.Length;
			var position = lexer.Position;
			if (lexer.Next() != PdfTokenType.Keyword || lexer.TokenText != "endstream")
				lexer.Position = position;

			return stream;
		}

		/// <summary>
		/// Returns true when only whitespace separates <paramref name="position"/> from the <c>endstream</c> keyword.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="position">The position where the stream data is expected to end.</param>
		/// <param name="end">The end of the readable range (exclusive).</param>
		/// <returns>True when <c>endstream</c> follows.</returns>
		private static bool IsEndStreamAt(byte[] data, int position, int end)
		{
			while (position < end && PdfLexer.IsWhitespace(data[position]))
				position++;

			return position + 9 <= end
				&& data[position] == 'e' && data[position + 1] == 'n' && data[position + 2] == 'd'
				&& data[position + 3] == 's' && data[position + 4] == 't' && data[position + 5] == 'r'
				&& data[position + 6] == 'e' && data[position + 7] == 'a' && data[position + 8] == 'm';
		}

		/// <summary>
		/// Scans forward for <c>endstream</c> (or <c>endobj</c> as a last resort) and trims the end-of-line marker before it.
		/// Used when <c>/Length</c> is missing or wrong.
		/// </summary>
		/// <param name="data">The buffer.</param>
		/// <param name="start">Position of the first byte of the stream data.</param>
		/// <param name="end">The end of the readable range (exclusive).</param>
		/// <returns>The position past the last byte of the stream data; <paramref name="end"/> when no keyword is found.</returns>
		private static int FindEndStream(byte[] data, int start, int end)
		{
			var found = IndexOf(data, "endstream", start, end);
			if (found < 0)
				found = IndexOf(data, "endobj", start, end);

			if (found < 0)
				return end;

			if (found > start && data[found - 1] == '\n')
				found--;

			if (found > start && data[found - 1] == '\r')
				found--;

			return found;
		}

		/// <summary>
		/// Finds the first occurrence of an ASCII string in a range of bytes.
		/// </summary>
		/// <param name="data">The buffer to search.</param>
		/// <param name="text">The ASCII text to find.</param>
		/// <param name="start">Offset where the search starts.</param>
		/// <param name="end">Offset where the search ends (exclusive).</param>
		/// <returns>The offset of the first match, or -1.</returns>
		public static int IndexOf(byte[] data, string text, int start, int end)
		{
			var first = (byte)text[0];
			var last = end - text.Length;
			for (int i = start; i <= last; i++)
			{
				if (data[i] != first)
					continue;

				var match = true;
				for (int j = 1; j < text.Length; j++)
				{
					if (data[i + j] != text[j])
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

		/// <summary>
		/// Decrypts a string token in place when parsing an encrypted indirect object.
		/// </summary>
		/// <param name="value">The string as read from the file.</param>
		/// <returns>The same <see cref="PdfString"/> instance, with decrypted bytes when applicable.</returns>
		private PdfString DecryptString(PdfString value)
		{
			if (_owner != null && _file?.Decryptor != null)
				value.Bytes = _file.Decryptor.DecryptString(value.Bytes, _owner);

			return value;
		}

		#endregion
	}
}
