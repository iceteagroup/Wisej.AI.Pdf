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

// Tests of the object parser (ISO 32000-2, 7.3): arrays, dictionaries,
// references, streams and the repair of malformed objects.

using System.Text;
using Wisej.AI.Pdf.IO;
using Wisej.AI.Pdf.Objects;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.IO
{
	/// <summary>
	/// Tests of <see cref="PdfParser"/>.
	/// </summary>
	public class ParserTests
	{
		/// <summary>Nested arrays and dictionaries keep their structure and value types.</summary>
		[Fact]
		public void Parse_NestedArraysAndDictionaries()
		{
			var value = TestPdf.ParseObject("<< /A [1 [2.5 (x)] << /B /Name >>] /C true /D false /E <<>> >>");

			var dictionary = Assert.IsType<PdfDictionary>(value);
			var a = dictionary.GetArray("A");
			Assert.Equal(3, a.Count);
			Assert.Equal(1, a[0]);

			var inner = Assert.IsType<PdfArray>(a[1]);
			Assert.Equal(2.5, inner[0]);
			Assert.Equal("x", Assert.IsType<PdfString>(inner[1]).ToLatin1());
			Assert.Equal("Name", a.GetDictionary(2).GetNameValue("B"));

			Assert.True(dictionary.GetBool("C"));
			Assert.False(dictionary.GetBool("D", true));
			Assert.Empty(dictionary.GetDictionary("E"));
		}

		/// <summary>"n g R" forms a reference; numbers that are not followed by "R" stay numbers.</summary>
		[Fact]
		public void Parse_References()
		{
			var array = Assert.IsType<PdfArray>(TestPdf.ParseObject("[1 0 R 2 5 R 3 4 5]"));

			Assert.Equal(new PdfReference(1, 0), array[0]);
			Assert.Equal(new PdfReference(2, 5), array[1]);
			Assert.Equal(new object[] { 3, 4, 5 }, array.GetRange(2, 3));
		}

		/// <summary>A null value is equivalent to a missing key; a key without value is kept as null.</summary>
		[Fact]
		public void Parse_NullValues()
		{
			var dictionary = Assert.IsType<PdfDictionary>(TestPdf.ParseObject("<< /A null /B 1 /C >>"));

			Assert.False(dictionary.Has("A"));
			Assert.Equal(1, dictionary.GetInt("B"));
			Assert.True(dictionary.Has("C"));
			Assert.Null(dictionary.Resolve("C"));
		}

		/// <summary>An array that is not closed before "endobj" ends there, keeping its items.</summary>
		[Fact]
		public void Parse_UnclosedArray_StopsAtObjectBoundary()
		{
			var parser = new PdfParser(new PdfLexer(Encoding.ASCII.GetBytes("7 0 obj [1 2 3 endobj")), null);

			var array = Assert.IsType<PdfArray>(parser.ParseIndirectObject(null, false));
			Assert.Equal(new object[] { 1, 2, 3 }, array);
		}

		/// <summary>Garbage between dictionary entries is skipped.</summary>
		[Fact]
		public void Parse_DictionaryWithGarbage_KeepsValidEntries()
		{
			var dictionary = Assert.IsType<PdfDictionary>(TestPdf.ParseObject("<< /A 1 garbage 2 /B (two) ] /C /Three >>"));

			Assert.Equal(1, dictionary.GetInt("A"));
			Assert.Equal("two", dictionary.GetText("B"));
			Assert.Equal("Three", dictionary.GetNameValue("C"));
		}

		/// <summary>Deeply nested arrays are bounded instead of overflowing the stack.</summary>
		[Fact]
		public void Parse_DeepNesting_DoesNotOverflow()
		{
			var text = new string('[', 100000) + new string(']', 100000);

			var value = TestPdf.RunWithTimeout(() => TestPdf.ParseObject(text));

			Assert.IsType<PdfArray>(value);
		}

		/// <summary>The stream data is read using the declared /Length.</summary>
		[Fact]
		public void Stream_CorrectLength_ReadsData()
		{
			var stream = TestPdf.ParseStream("/Type /Test", "Hello\nWorld");

			Assert.Equal("Hello\nWorld", Encoding.ASCII.GetString(stream.GetRawBytes()));
			Assert.True(stream.IsType("Test"));
		}

		/// <summary>A /Length that is too long or too short is repaired by finding "endstream".</summary>
		[Theory]
		[InlineData(999)]
		[InlineData(3)]
		[InlineData(-1)]
		public void Stream_WrongLength_IsRepaired(int declaredLength)
		{
			var text = $"1 0 obj\n<< /Length {declaredLength} >>\nstream\r\nHello stream\r\nendstream\nendobj\n";
			var parser = new PdfParser(new PdfLexer(Encoding.ASCII.GetBytes(text)), null);

			var stream = Assert.IsType<PdfStream>(parser.ParseIndirectObject(null, false));

			Assert.Equal("Hello stream", Encoding.ASCII.GetString(stream.GetRawBytes()));
		}

		/// <summary>A stream without "endstream" takes the data up to "endobj".</summary>
		[Fact]
		public void Stream_MissingEndStream_StopsAtEndObj()
		{
			var text = "1 0 obj\n<< >>\nstream\nabc\nendobj\n";
			var parser = new PdfParser(new PdfLexer(Encoding.ASCII.GetBytes(text)), null);

			var stream = Assert.IsType<PdfStream>(parser.ParseIndirectObject(null, false));

			Assert.Equal("abc", Encoding.ASCII.GetString(stream.GetRawBytes()));
		}

		/// <summary>An indirect object with a different number than expected is rejected.</summary>
		[Fact]
		public void IndirectObject_WrongNumber_IsRejected()
		{
			var parser = new PdfParser(new PdfLexer(Encoding.ASCII.GetBytes("5 0 obj 42 endobj")), null);

			Assert.Null(parser.ParseIndirectObject(new PdfReference(6, 0), false));
		}

		/// <summary>The decoded data of a stream goes through its filter chain, with abbreviated filter names.</summary>
		[Fact]
		public void Stream_GetBytes_AppliesFilterChain()
		{
			var data = Encoders.Ascii85(PdfBuilder.Deflate(Encoding.ASCII.GetBytes("filtered content")));
			var stream = TestPdf.ParseStream("/Filter [/A85 /Fl]", data);

			Assert.Equal("filtered content", Encoding.ASCII.GetString(stream.GetBytes()));
		}
	}
}
