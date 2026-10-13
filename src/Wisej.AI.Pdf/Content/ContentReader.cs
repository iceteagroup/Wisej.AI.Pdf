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

// Reads operators and operands from content streams (ISO 32000-2, 7.8),
// including inline images (8.9.7).

using System;
using Wisej.AI.Pdf.Filters;
using Wisej.AI.Pdf.IO;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Content
{
	/// <summary>
	/// An operand of a content stream operator.
	/// </summary>
	/// <remarks>
	/// Numbers are stored unboxed in <see cref="Number"/>; all the other values are in <see cref="Value"/>.
	/// </remarks>
	internal struct Operand
	{
		/// <summary>
		/// True when the operand is a number.
		/// </summary>
		public bool IsNumber;

		/// <summary>
		/// The value of a numeric operand.
		/// </summary>
		public double Number;

		/// <summary>
		/// The value of a non-numeric operand: <see cref="PdfName"/>, <see cref="PdfString"/>, <see cref="PdfArray"/>, <see cref="PdfDictionary"/> or <see cref="bool"/>.
		/// </summary>
		public object Value;
	}

	/// <summary>
	/// An inline image read from a content stream.
	/// </summary>
	internal sealed class InlineImage
	{
		/// <summary>
		/// The image dictionary with abbreviated keys expanded.
		/// </summary>
		public PdfDictionary Dictionary;

		/// <summary>
		/// The image data, still encoded with the image filters.
		/// </summary>
		public byte[] Data;
	}

	/// <summary>
	/// Reads a content stream as a sequence of operators with their operands.
	/// </summary>
	internal sealed class ContentReader
	{
		// operators with more operands than this are malformed; extra operands are dropped.
		private const int MaxOperands = 64;

		// the tokenizer of the content stream.
		private readonly PdfLexer _lexer;
		// parses arrays and dictionaries operands.
		private readonly PdfParser _parser;
		// the operands of the current operator.
		private readonly Operand[] _operands = new Operand[MaxOperands];
		// the number of operands in _operands.
		private int _count;

		/// <summary>
		/// Initializes a new instance of <see cref="ContentReader"/>.
		/// </summary>
		/// <param name="data">The decoded content stream.</param>
		/// <param name="file">The file used to resolve references in operands; can be null.</param>
		public ContentReader(byte[] data, PdfFile file)
		{
			_lexer = new PdfLexer(data);
			_parser = new PdfParser(_lexer, file);
		}

		#region Properties

		/// <summary>
		/// The operands of the last operator read.
		/// </summary>
		public Operand[] Operands => _operands;

		/// <summary>
		/// The number of operands of the last operator read.
		/// </summary>
		public int OperandCount => _count;

		/// <summary>
		/// The inline image read by the last <c>BI</c> operator.
		/// </summary>
		public InlineImage InlineImage { get; private set; }

		/// <summary>
		/// The current position in the content stream.
		/// </summary>
		public int Position => _lexer.Position;

		#endregion

		#region Methods

		/// <summary>
		/// Reads the next operator and its operands.
		/// </summary>
		/// <param name="op">Receives the operator keyword.</param>
		/// <returns>False at the end of the stream.</returns>
		public bool Read(out string op)
		{
			_count = 0;
			var lexer = _lexer;

			while (true)
			{
				switch (lexer.Next())
				{
					case PdfTokenType.EndOfInput:
						op = null;
						return false;

					case PdfTokenType.Number:
						Push(new Operand { IsNumber = true, Number = lexer.TokenValue });
						break;

					case PdfTokenType.String:
						Push(new Operand { Value = lexer.TokenString });
						break;

					case PdfTokenType.Name:
						Push(new Operand { Value = PdfName.Get(lexer.TokenText) });
						break;

					case PdfTokenType.ArrayStart:
					case PdfTokenType.DictionaryStart:
						// let the object parser read the whole array or dictionary.
						lexer.Position = lexer.TokenStart;
						Push(new Operand { Value = _parser.ParseObject() });
						break;

					case PdfTokenType.Keyword:
						{
							var keyword = lexer.TokenText;
							switch (keyword)
							{
								case "true":
									Push(new Operand { Value = true });
									break;

								case "false":
									Push(new Operand { Value = false });
									break;

								case "null":
									Push(new Operand { Value = null });
									break;

								case "BI":
									op = "BI";
									this.InlineImage = ReadInlineImage();
									return true;

								default:
									op = keyword;
									return true;
							}
						}
						break;

					default:
						// stray delimiters are ignored.
						break;
				}
			}
		}

		/// <summary>
		/// Returns the numeric operand at <paramref name="index"/>.
		/// </summary>
		/// <param name="index">The operand index.</param>
		/// <returns>The value, or 0 when the operand is missing or not a number.</returns>
		public double GetNumber(int index) => index < _count && _operands[index].IsNumber ? _operands[index].Number : 0;

		/// <summary>
		/// Returns the operand at <paramref name="index"/> as an object.
		/// </summary>
		/// <param name="index">The operand index.</param>
		/// <returns>The value; numbers are boxed as <see cref="double"/>.</returns>
		public object GetValue(int index)
		{
			if (index >= _count)
				return null;

			return _operands[index].IsNumber ? _operands[index].Number : _operands[index].Value;
		}

		/// <summary>
		/// Returns the operand at <paramref name="index"/> as a name.
		/// </summary>
		/// <param name="index">The operand index.</param>
		/// <returns>The name, or null.</returns>
		public PdfName GetName(int index) => index < _count ? _operands[index].Value as PdfName : null;

		/// <summary>
		/// Returns true when the last <paramref name="count"/> operands are all numbers.
		/// </summary>
		/// <param name="count">The number of operands required.</param>
		/// <returns>True when there are at least <paramref name="count"/> numeric operands.</returns>
		public bool HasNumbers(int count)
		{
			if (_count < count)
				return false;

			for (int i = _count - count; i < _count; i++)
			{
				if (!_operands[i].IsNumber)
					return false;
			}

			return true;
		}

		#endregion

		#region Implementation

		/// <summary>
		/// Adds an operand, dropping the oldest when there are too many.
		/// </summary>
		/// <param name="operand">The operand.</param>
		private void Push(Operand operand)
		{
			// keep the last MaxOperands operands: operators use the operands nearest to them.
			if (_count == MaxOperands)
			{
				Array.Copy(_operands, 1, _operands, 0, MaxOperands - 1);
				_count--;
			}

			_operands[_count++] = operand;
		}

		/// <summary>
		/// Reads an inline image after BI: the dictionary up to ID, then the data up to EI.
		/// </summary>
		/// <returns>The inline image, or null at the end of the stream.</returns>
		private InlineImage ReadInlineImage()
		{
			var lexer = _lexer;
			var dictionary = new PdfDictionary();

			// key/value pairs up to the "ID" keyword.
			while (true)
			{
				var type = lexer.Next();
				if (type == PdfTokenType.EndOfInput)
					return null;

				if (type == PdfTokenType.Keyword && lexer.TokenText == "ID")
					break;

				if (type != PdfTokenType.Name)
					continue;

				var key = ExpandKey(lexer.TokenText);
				var value = _parser.ParseObject();
				if (value is PdfOperator op)
				{
					if (op.Name == "ID")
						break;

					continue;
				}

				dictionary[PdfName.Get(key)] = value;
			}

			// a single whitespace separates ID from the data.
			var data = lexer.Data;
			var start = lexer.Position;
			if (start < lexer.End && PdfLexer.IsWhitespace(data[start]))
				start++;

			var end = FindInlineImageEnd(dictionary, data, start, lexer.End);

			var bytes = new byte[Math.Max(0, end - start)];
			Buffer.BlockCopy(data, start, bytes, 0, bytes.Length);

			// skip "EI".
			lexer.Position = end;
			var position = lexer.Position;
			if (lexer.Next() != PdfTokenType.Keyword || lexer.TokenText != "EI")
				lexer.Position = position;

			return new InlineImage { Dictionary = dictionary, Data = bytes };
		}

		// finds the end of the inline image data: the position of the "EI" operator.
		private static int FindInlineImageEnd(PdfDictionary dictionary, byte[] data, int start, int end)
		{
			// uncompressed images have a computable size; use it when it lands right before "EI".
			var expected = GetUncompressedLength(dictionary);
			if (expected > 0 && start + expected <= end && IsEndOfImage(data, start + expected, end))
				return start + expected;

			// /Length (PDF 2.0) gives the exact size.
			var length = dictionary.GetInt(PdfNames.Length, -1);
			if (length > 0 && start + length <= end && IsEndOfImage(data, start + length, end))
				return start + length;

			// scan for "EI" delimited by whitespace and followed by what looks like content operators.
			for (int p = start; p + 1 < end; p++)
			{
				if (data[p] == 'E' && data[p + 1] == 'I'
					&& (p == start || PdfLexer.IsWhitespace(data[p - 1]))
					&& (p + 2 >= end || PdfLexer.IsWhitespace(data[p + 2]) || PdfLexer.IsDelimiter(data[p + 2]))
					&& FollowedByOperators(data, p + 2, end))
				{
					// the whitespace before EI is not part of the data.
					return p > start && PdfLexer.IsWhitespace(data[p - 1]) ? p - 1 : p;
				}
			}

			return end;
		}

		// true when "EI" (optionally preceded by whitespace) is at the position.
		private static bool IsEndOfImage(byte[] data, int position, int end)
		{
			while (position < end && PdfLexer.IsWhitespace(data[position]))
				position++;

			return position + 1 < end && data[position] == 'E' && data[position + 1] == 'I'
				&& (position + 2 >= end || !PdfLexer.IsRegular(data[position + 2]));
		}

		// binary image data can contain "EI" by chance: check that the following bytes are plausible content.
		private static bool FollowedByOperators(byte[] data, int position, int end)
		{
			var limit = Math.Min(end, position + 64);
			var lexer = new PdfLexer(data, position, limit - position);

			for (int i = 0; i < 4; i++)
			{
				var start = lexer.Position;
				var type = lexer.Next();
				if (type == PdfTokenType.EndOfInput)
					return true;

				// binary garbage produces bytes outside the printable ASCII range.
				for (int p = start; p < lexer.Position; p++)
				{
					var c = data[p];
					if (c > 127 || (c < 32 && !PdfLexer.IsWhitespace(c)))
						return false;
				}

				if (type == PdfTokenType.Keyword && lexer.TokenText.Length > 3 && lexer.TokenText != "true" && lexer.TokenText != "false" && lexer.TokenText != "null")
					return false;
			}

			return true;
		}

		// size of the data when the image has no filter.
		private static int GetUncompressedLength(PdfDictionary dictionary)
		{
			if (dictionary.Has(PdfNames.Filter))
				return -1;

			var width = dictionary.GetInt(PdfNames.Width);
			var height = dictionary.GetInt(PdfNames.Height);
			var isMask = dictionary.GetBool(PdfNames.ImageMask);
			var bits = isMask ? 1 : dictionary.GetInt(PdfNames.BitsPerComponent, 8);

			var components = 1;
			if (!isMask)
			{
				var colorSpace = dictionary.Resolve(PdfNames.ColorSpace);
				var name = (colorSpace as PdfName)?.Value ?? ((colorSpace as PdfArray)?.GetName(0)?.Value);
				switch (name)
				{
					case "DeviceRGB":
					case "RGB":
					case "CalRGB":
					case "Lab":
						components = 3;
						break;

					case "DeviceCMYK":
					case "CMYK":
						components = 4;
						break;

					case "DeviceGray":
					case "G":
					case "CalGray":
					case "Indexed":
					case "I":
						components = 1;
						break;

					default:
						// named colour spaces in the resources: unknown size.
						return -1;
				}
			}

			if (width <= 0 || height <= 0 || bits <= 0)
				return -1;

			return (width * components * bits + 7) / 8 * height;
		}

		// expands the abbreviated keys allowed in inline image dictionaries.
		private static string ExpandKey(string key)
		{
			switch (key)
			{
				case "BPC": return "BitsPerComponent";
				case "CS": return "ColorSpace";
				case "D": return "Decode";
				case "DP": return "DecodeParms";
				case "F": return "Filter";
				case "H": return "Height";
				case "IM": return "ImageMask";
				case "I": return "Interpolate";
				case "W": return "Width";
				case "L": return "Length";
				default: return key;
			}
		}

		#endregion
	}
}
