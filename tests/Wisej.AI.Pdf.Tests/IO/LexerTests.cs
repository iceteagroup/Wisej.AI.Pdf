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

// Tests of the tokenizer (ISO 32000-2, 7.2 and 7.3): numbers, strings, names,
// delimiters and comments, including malformed input seen in real files.

using System;
using System.Text;
using Wisej.AI.Pdf.IO;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.IO
{
	/// <summary>
	/// Tests of <see cref="PdfLexer"/>.
	/// </summary>
	public class LexerTests
	{
		#region Numbers

		/// <summary>Integers and reals in all the forms allowed by the specification.</summary>
		[Theory]
		[InlineData("123", 123.0, true)]
		[InlineData("-45", -45.0, true)]
		[InlineData("+6", 6.0, true)]
		[InlineData("0", 0.0, true)]
		[InlineData("3.14", 3.14, false)]
		[InlineData("-.5", -0.5, false)]
		[InlineData("4.", 4.0, false)]
		[InlineData(".25", 0.25, false)]
		public void Number_ValidForms_AreParsed(string text, double expected, bool isInteger)
		{
			var lexer = Lex(text);

			Assert.Equal(PdfTokenType.Number, lexer.Next());
			Assert.Equal(expected, lexer.TokenValue, 9);
			Assert.Equal(isInteger, lexer.TokenIsInteger);
		}

		/// <summary>A doubled sign is accepted as a single number token, and the next token is unaffected.</summary>
		[Fact]
		public void Number_DoubleSign_IsOneNumberToken()
		{
			var lexer = Lex("--5 7");

			Assert.Equal(PdfTokenType.Number, lexer.Next());
			Assert.Equal(5, Math.Abs(lexer.TokenValue));

			Assert.Equal(PdfTokenType.Number, lexer.Next());
			Assert.Equal(7, lexer.TokenValue);
		}

		/// <summary>A number with two decimal points keeps the valid prefix and consumes the rest of the token.</summary>
		[Fact]
		public void Number_TwoDecimalPoints_KeepsPrefixAndConsumesToken()
		{
			var lexer = Lex("1.2.3 /Next");

			Assert.Equal(PdfTokenType.Number, lexer.Next());
			Assert.Equal(1.2, lexer.TokenValue, 9);

			Assert.Equal(PdfTokenType.Name, lexer.Next());
			Assert.Equal("Next", lexer.TokenText);
		}

		/// <summary>Exponent notation is not valid PDF but is written by some producers.</summary>
		[Fact]
		public void Number_ExponentNotation_IsTolerated()
		{
			var lexer = Lex("1e-05 2.5E2");

			Assert.Equal(PdfTokenType.Number, lexer.Next());
			Assert.Equal(1e-05, lexer.TokenValue, 12);

			Assert.Equal(PdfTokenType.Number, lexer.Next());
			Assert.Equal(250, lexer.TokenValue, 9);
		}

		/// <summary>An integer that doesn't fit in 32 bits becomes a real.</summary>
		[Fact]
		public void Number_LargeInteger_BecomesReal()
		{
			var lexer = Lex("12345678901");

			Assert.Equal(PdfTokenType.Number, lexer.Next());
			Assert.False(lexer.TokenIsInteger);
			Assert.Equal(12345678901.0, lexer.TokenValue);
		}

		/// <summary>Digits followed by letters form a keyword, not a number.</summary>
		[Fact]
		public void Number_FollowedByLetters_IsKeyword()
		{
			var lexer = Lex("1st");

			Assert.Equal(PdfTokenType.Keyword, lexer.Next());
			Assert.Equal("1st", lexer.TokenText);
		}

		#endregion

		#region Strings

		/// <summary>Escapes, balanced parentheses and octal codes in literal strings.</summary>
		[Theory]
		[InlineData(@"(plain)", "plain")]
		[InlineData(@"(a\(b\)c)", "a(b)c")]
		[InlineData(@"(nested (balanced) parens)", "nested (balanced) parens")]
		[InlineData(@"(tab\tnew\nline)", "tab\tnew\nline")]
		[InlineData(@"(back\\slash)", "back\\slash")]
		[InlineData(@"(\053\53\0053)", "++\u00053")]
		[InlineData(@"(unknown\q escape)", "unknownq escape")]
		public void LiteralString_Escapes_AreDecoded(string text, string expected)
		{
			var lexer = Lex(text);

			Assert.Equal(PdfTokenType.String, lexer.Next());
			Assert.Equal(expected, lexer.TokenString.ToLatin1());
			Assert.False(lexer.TokenString.IsHex);
		}

		/// <summary>A backslash at the end of a line continues the string; a bare CR LF becomes LF.</summary>
		[Fact]
		public void LiteralString_LineContinuationAndEndOfLine_AreNormalized()
		{
			var lexer = Lex("(one\\\r\ntwo\r\nthree)");

			Assert.Equal(PdfTokenType.String, lexer.Next());
			Assert.Equal("onetwo\nthree", lexer.TokenString.ToLatin1());
		}

		/// <summary>An unterminated literal string returns what was read instead of failing.</summary>
		[Fact]
		public void LiteralString_Unterminated_ReturnsContent()
		{
			var lexer = Lex("(never closed");

			Assert.Equal(PdfTokenType.String, lexer.Next());
			Assert.Equal("never closed", lexer.TokenString.ToLatin1());
			Assert.Equal(PdfTokenType.EndOfInput, lexer.Next());
		}

		/// <summary>Hexadecimal strings ignore whitespace and pad an odd final digit with zero.</summary>
		[Theory]
		[InlineData("<48 65 6c6C6f>", "Hello")]
		[InlineData("<414>", "A@")]
		[InlineData("<>", "")]
		[InlineData("<4 1\n4 2>", "AB")]
		public void HexString_IsDecoded(string text, string expected)
		{
			var lexer = Lex(text);

			Assert.Equal(PdfTokenType.String, lexer.Next());
			Assert.Equal(expected, lexer.TokenString.ToLatin1());
			Assert.True(lexer.TokenString.IsHex);
		}

		/// <summary>Text strings with a UTF-16BE byte order mark are decoded as Unicode.</summary>
		[Fact]
		public void HexString_Utf16TextString_IsDecoded()
		{
			var lexer = Lex(PdfText.TextString("Grüße €"));

			Assert.Equal(PdfTokenType.String, lexer.Next());
			Assert.Equal("Grüße €", lexer.TokenString.ToText());
		}

		#endregion

		#region Names, keywords and delimiters

		/// <summary>Names decode #xx escapes, including UTF-8 sequences.</summary>
		[Theory]
		[InlineData("/Type", "Type")]
		[InlineData("/A#20B", "A B")]
		[InlineData("/Lime#20Green", "Lime Green")]
		[InlineData("/paired#28#29parentheses", "paired()parentheses")]
		[InlineData("/caf#C3#A9", "café")]
		[InlineData("/Bad#zz", "Bad#zz")]
		[InlineData("/", "")]
		public void Name_HexEscapes_AreDecoded(string text, string expected)
		{
			var lexer = Lex(text);

			Assert.Equal(PdfTokenType.Name, lexer.Next());
			Assert.Equal(expected, lexer.TokenText);
		}

		/// <summary>A name ends at the first delimiter, without a space.</summary>
		[Fact]
		public void Name_EndsAtDelimiter()
		{
			var lexer = Lex("/A/B[");

			Assert.Equal(PdfTokenType.Name, lexer.Next());
			Assert.Equal("A", lexer.TokenText);
			Assert.Equal(PdfTokenType.Name, lexer.Next());
			Assert.Equal("B", lexer.TokenText);
			Assert.Equal(PdfTokenType.ArrayStart, lexer.Next());
		}

		/// <summary>Delimiters, keywords and comments produce the expected token sequence.</summary>
		[Fact]
		public void Tokens_DelimitersKeywordsAndComments()
		{
			var lexer = Lex("<< /K [ true ] >> % a comment\r\n{ 1 add } endobj");

			Assert.Equal(PdfTokenType.DictionaryStart, lexer.Next());
			Assert.Equal(PdfTokenType.Name, lexer.Next());
			Assert.Equal(PdfTokenType.ArrayStart, lexer.Next());
			Assert.Equal(PdfTokenType.Keyword, lexer.Next());
			Assert.Equal("true", lexer.TokenText);
			Assert.Equal(PdfTokenType.ArrayEnd, lexer.Next());
			Assert.Equal(PdfTokenType.DictionaryEnd, lexer.Next());
			Assert.Equal(PdfTokenType.ProcedureStart, lexer.Next());
			Assert.Equal(PdfTokenType.Number, lexer.Next());
			Assert.Equal(PdfTokenType.Keyword, lexer.Next());
			Assert.Equal("add", lexer.TokenText);
			Assert.Equal(PdfTokenType.ProcedureEnd, lexer.Next());
			Assert.Equal(PdfTokenType.Keyword, lexer.Next());
			Assert.Equal("endobj", lexer.TokenText);
			Assert.Equal(PdfTokenType.EndOfInput, lexer.Next());
		}

		/// <summary>Stray closing delimiters are skipped and never stall the lexer.</summary>
		[Fact]
		public void Tokens_StrayDelimiters_AlwaysMakeProgress()
		{
			var lexer = Lex(") ) > 42");

			var count = 0;
			while (lexer.Next() != PdfTokenType.EndOfInput && count < 10)
				count++;

			Assert.True(count < 10);
		}

		#endregion

		/// <summary>
		/// Creates a lexer over the Latin-1 bytes of <paramref name="text"/>.
		/// </summary>
		/// <param name="text">The input.</param>
		/// <returns>The lexer.</returns>
		private static PdfLexer Lex(string text) => new PdfLexer(Encoding.Latin1.GetBytes(text));
	}
}
