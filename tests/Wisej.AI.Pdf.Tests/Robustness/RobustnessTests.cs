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

// Robustness tests: garbage, truncated and mutated files, empty streams,
// reference cycles and hostile sizes must never hang, crash or exhaust memory.

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Wisej.AI.Pdf.Tests.Helpers;
using Wisej.AI.Pdf.Tests.Output;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Robustness
{
	/// <summary>
	/// Tests that malformed input fails cleanly with <see cref="PdfException"/> or is extracted as far as possible.
	/// </summary>
	public class RobustnessTests
	{
		#region Garbage and truncation

		/// <summary>Random bytes are rejected with <see cref="PdfException"/>.</summary>
		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(100)]
		[InlineData(10000)]
		public void RandomBytes_ThrowPdfException(int length)
		{
			var data = new byte[length];
			new Random(length).NextBytes(data);

			Assert.Throws<PdfException>(() => TestPdf.RunWithTimeout(() => PdfDocument.Open(data)));
		}

		/// <summary>A PDF header followed by garbage either fails with <see cref="PdfException"/> or yields an empty result.</summary>
		[Theory]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		public void HeaderWithGarbage_FailsCleanly(int seed)
		{
			var garbage = new byte[5000];
			new Random(seed).NextBytes(garbage);
			var data = PdfText.ToBytes("%PDF-1.7\n").Concat(garbage).ToArray();

			AssertFailsCleanlyOrExtracts(data);
		}

		/// <summary>A valid file truncated at any point fails cleanly or extracts what is left.</summary>
		[Fact]
		public void TruncatedFiles_FailCleanlyOrExtract()
		{
			var complete = MarkdownTests.CreateFigureDocument().ToArray(new PdfWriteOptions { Xref = XrefStyle.Stream });

			for (int percent = 5; percent < 100; percent += 5)
				AssertFailsCleanlyOrExtracts(complete.Take(complete.Length * percent / 100).ToArray());
		}

		/// <summary>Random byte mutations of a valid file never cause anything but <see cref="PdfException"/>.</summary>
		[Fact]
		public void MutatedFiles_FailCleanlyOrExtract()
		{
			var builder = MarkdownTests.CreateFigureDocument();
			builder.SetInfo(("Title", "Fuzzing target"));
			var original = builder.ToArray();

			var random = new Random(12345);
			for (int round = 0; round < 60; round++)
			{
				var data = (byte[])original.Clone();
				for (int i = 0; i < 1 + round % 8; i++)
					data[random.Next(data.Length)] = (byte)random.Next(256);

				AssertFailsCleanlyOrExtracts(data);
			}
		}

		#endregion

		#region Degenerate documents

		/// <summary>A page with an empty content stream has no blocks.</summary>
		[Fact]
		public void EmptyContentStream()
		{
			var builder = new PdfBuilder();
			builder.AddPage();

			var content = TestPdf.Extract(builder);

			Assert.Single(content.Pages);
			Assert.Empty(content.Blocks);
			Assert.Equal("\n", content.ToMarkdown());
			JsonDocumentIsValid(content.ToJson());
		}

		/// <summary>A document without pages opens and extracts nothing.</summary>
		[Fact]
		public void NoPages()
		{
			var content = TestPdf.Extract(new PdfBuilder());

			Assert.Equal(0, content.PageCount);
			Assert.Empty(content.Pages);
		}

		/// <summary>Malformed operators in a content stream are skipped; the rest of the page is extracted.</summary>
		[Fact]
		public void MalformedOperators_AreSkipped()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Use(font).Content(
				$"BT /{font.Name} 12 Tf 72 700 Td (Before) Tj ET\n" +
				"1 2 cm Tj Tf ] >> } Q Q Q re re S Do /Missing Do BI ID EI 1 0 0 rg /Name sc 5 0 R Td\n" +
				$"BT /{font.Name} 12 Tf 72 650 Td (After) Tj ET");

			var text = TestPdf.Normalize(TestPdf.Extract(builder).ToText());

			Assert.Contains("Before", text);
			Assert.Contains("After", text);
		}

		/// <summary>A content stream that references a missing font still produces text with a fallback font.</summary>
		[Fact]
		public void MissingFont_UsesFallback()
		{
			var builder = new PdfBuilder();
			builder.AddPage().Content("BT /NoSuchFont 12 Tf 72 700 Td (Fallback text) Tj ET");

			Assert.Contains("Fallback text", TestPdf.Extract(builder).ToText());
		}

		/// <summary>A form XObject that draws itself is not followed forever.</summary>
		[Fact]
		public void RecursiveForm_Terminates()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var number = builder.Reserve();
			var form = builder.Register("XObject", number);
			builder.SetStream(number,
				$"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /{font.Name} {font.Reference} >> /XObject << /{form.Name} {form.Reference} >> >>",
				PdfText.ToBytes($"BT /{font.Name} 12 Tf 72 700 Td (Recursive) Tj ET /{form.Name} Do"));

			builder.AddPage().Draw(form, 1, 0, 0, 1, 0, 0);

			Assert.Contains("Recursive", TestPdf.Extract(builder).ToText());
		}

		/// <summary>A page tree whose /Kids point back to the root doesn't loop.</summary>
		[Fact]
		public void PageTreeCycle_Terminates()
		{
			var builder = new PdfBuilder();
			var page = builder.AddPage().Text(builder.Font("Helvetica"), 12, 72, 700, "Only page");
			var bytes = PdfText.FromBytes(builder.ToArray());

			// the page tree is object 2: add itself to its kids (the later offsets shift and are recovered by scanning).
			var kids = $"/Kids [{page.Reference}]";
			Assert.Contains(kids, bytes);
			var patched = bytes.Replace(kids, $"/Kids [{page.Reference} 2 0 R]");

			var content = TestPdf.Extract(PdfText.ToBytes(patched));

			Assert.Equal(1, content.PageCount);
			Assert.Contains("Only page", content.ToText());
		}

		/// <summary>A stream whose /Length refers to the stream itself is read by scanning for "endstream".</summary>
		[Fact]
		public void SelfReferencingLength()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var page = builder.AddPage().Use(font);
			var bytes = PdfText.FromBytes(builder.ToArray());

			// the content stream is the object after the page; give it a /Length that refers to itself.
			var number = page.Number + 1;
			var marker = $"{number} 0 obj\n<<  /Length 0 >>\nstream\n\nendstream";
			Assert.Contains(marker, bytes);

			var content = $"BT /{font.Name} 12 Tf 72 700 Td (Self length) Tj ET";
			var patched = bytes.Replace(marker, $"{number} 0 obj\n<< /Length {number} 0 R >>\nstream\n{content}\nendstream");

			Assert.Contains("Self length", TestPdf.Extract(PdfText.ToBytes(patched)).ToText());
		}

		/// <summary>An image that declares a huge size but carries almost no data doesn't exhaust memory.</summary>
		[Fact]
		public void HugeImageWithoutData_IsBounded()
		{
			var builder = new PdfBuilder();
			var image = builder.Image(12000, 12000, "/DeviceGray", 1, new byte[] { 0xAA });
			builder.AddPage().Text(builder.Font("Helvetica"), 11, 72, 720, "Hostile image below.").Draw(image, 72, 300, 200, 200);

			var before = GC.GetTotalAllocatedBytes(precise: true);
			TestPdf.Extract(builder);
			var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

			Assert.True(allocated < 100L * 1024 * 1024, $"{allocated / (1024 * 1024)} MB allocated for a {builder.ToArray().Length}-byte file.");
		}

		/// <summary>Extraction honours cancellation.</summary>
		[Fact]
		public void Cancellation_Throws()
		{
			var builder = new PdfBuilder();
			for (int p = 0; p < 5; p++)
				builder.AddPage().Text(builder.Font("Helvetica"), 12, 72, 700, "Page text");

			using (var source = new CancellationTokenSource())
			using (var document = PdfDocument.Open(builder.ToArray()))
			{
				source.Cancel();
				Assert.ThrowsAny<OperationCanceledException>(() => document.Extract(new PdfExtractionOptions { CancellationToken = source.Token }));
			}
		}

		#endregion

		/// <summary>
		/// Asserts that a damaged file either fails with <see cref="PdfException"/> or is extracted, in time,
		/// with outputs that are well formed.
		/// </summary>
		/// <param name="data">The file.</param>
		private static void AssertFailsCleanlyOrExtracts(byte[] data)
		{
			try
			{
				var content = TestPdf.Extract(data);
				content.ToMarkdown();
				content.ToHtml();
				content.ToText();
				JsonDocumentIsValid(content.ToJson());
			}
			catch (PdfException)
			{
				// a clean failure is acceptable.
			}
		}

		/// <summary>
		/// Asserts that a JSON text parses.
		/// </summary>
		/// <param name="json">The JSON text.</param>
		private static void JsonDocumentIsValid(string json)
		{
			using (JsonDocument.Parse(json))
			{
			}
		}
	}
}
