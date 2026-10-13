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

// Tests of the file structure (ISO 32000-2, 7.5): cross-reference tables and
// streams, object streams, incremental updates and the repair of damaged files.

using System;
using System.Text;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.IO
{
	/// <summary>
	/// Tests of the cross-reference handling and reconstruction of <see cref="PdfDocument"/>.
	/// </summary>
	public class FileStructureTests
	{
		/// <summary>A file with a classic cross-reference table opens without repair.</summary>
		[Fact]
		public void XrefTable_OpensWithoutRepair()
		{
			using (var document = TestPdf.Open(CreateDocument("Table file"), new PdfWriteOptions { Xref = XrefStyle.Table }))
			{
				Assert.False(document.WasRepaired);
				Assert.Equal(2, document.PageCount);
				Assert.Equal("1.7", document.Version);
				Assert.Contains("Table file", document.ExtractText());
			}
		}

		/// <summary>A file with a cross-reference stream opens without repair.</summary>
		[Fact]
		public void XrefStream_OpensWithoutRepair()
		{
			using (var document = TestPdf.Open(CreateDocument("Stream file"), new PdfWriteOptions { Xref = XrefStyle.Stream }))
			{
				Assert.False(document.WasRepaired);
				Assert.Equal(2, document.PageCount);
				Assert.Contains("Stream file", document.ExtractText());
			}
		}

		/// <summary>Objects stored in an object stream are resolved through the cross-reference stream.</summary>
		[Fact]
		public void ObjectStreams_AreResolved()
		{
			var builder = CreateDocument("Compressed objects");
			builder.SetInfo(("Title", "Packed"));

			using (var document = TestPdf.Open(builder, new PdfWriteOptions { Xref = XrefStyle.Stream, UseObjectStreams = true }))
			{
				Assert.False(document.WasRepaired);
				Assert.Equal(2, document.PageCount);
				Assert.Equal("Packed", document.Info.Title);
				Assert.Contains("Compressed objects", document.ExtractText());
			}
		}

		/// <summary>An incremental update replaces the objects of the previous revision.</summary>
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void IncrementalUpdate_NewestRevisionWins(bool xrefStream)
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var page = builder.AddPage().Text(font, 12, 72, 700, "Original wording");
			builder.SetInfo(("Title", "First"));
			builder.CommitRevision();

			// the second revision changes the content stream and the title, and adds a page.
			page.Content("BT /" + font.Name + " 12 Tf 72 650 Td (Appended wording) Tj ET");
			builder.SetInfo(("Title", "Second"));
			builder.AddPage().Text(font, 12, 72, 700, "Second page");

			var bytes = builder.ToArray(new PdfWriteOptions { Xref = xrefStream ? XrefStyle.Stream : XrefStyle.Table });
			Assert.Equal(2, CountOccurrences(bytes, "%%EOF"));

			using (var document = PdfDocument.Open(bytes))
			{
				Assert.False(document.WasRepaired);
				Assert.Equal(2, document.PageCount);
				Assert.Equal("Second", document.Info.Title);

				var text = document.ExtractText();
				Assert.Contains("Original wording", text);
				Assert.Contains("Appended wording", text);
				Assert.Contains("Second page", text);
			}
		}

		/// <summary>A file whose cross-reference table has wrong offsets is reconstructed by scanning.</summary>
		[Fact]
		public void CorruptXref_IsReconstructed()
		{
			using (var document = TestPdf.Open(CreateDocument("Damaged file"), new PdfWriteOptions { Xref = XrefStyle.Corrupt }))
			{
				Assert.True(document.WasRepaired);
				Assert.Equal(2, document.PageCount);
				Assert.Contains("Damaged file", document.ExtractText());
			}
		}

		/// <summary>A file without any cross-reference data or trailer is reconstructed, finding the catalog by scanning.</summary>
		[Fact]
		public void MissingXrefAndTrailer_IsReconstructed()
		{
			using (var document = TestPdf.Open(CreateDocument("No xref at all"), new PdfWriteOptions { Xref = XrefStyle.None }))
			{
				Assert.True(document.WasRepaired);
				Assert.Equal(2, document.PageCount);
				Assert.Contains("No xref at all", document.ExtractText());
			}
		}

		/// <summary>A trailer without /Root: the catalog is found by scanning the objects.</summary>
		[Fact]
		public void MissingRoot_CatalogFoundByScan()
		{
			using (var document = TestPdf.Open(CreateDocument("Rootless"), new PdfWriteOptions { OmitRoot = true }))
			{
				Assert.Equal(2, document.PageCount);
				Assert.Contains("Rootless", document.ExtractText());
			}
		}

		/// <summary>Junk before the header: offsets are relative to "%PDF-".</summary>
		[Fact]
		public void JunkBeforeHeader_OffsetsAreRelativeToHeader()
		{
			var options = new PdfWriteOptions { Preamble = "garbage written by a mail gateway\r\n" };

			using (var document = TestPdf.Open(CreateDocument("Shifted"), options))
			{
				Assert.False(document.WasRepaired);
				Assert.Contains("Shifted", document.ExtractText());
			}
		}

		/// <summary>A stream with a wrong /Length in a complete file is still read correctly.</summary>
		[Fact]
		public void StreamLengthMismatch_IsRepaired()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var page = builder.AddPage();
			page.Use(font);

			var content = PdfText.ToBytes($"BT /{font.Name} 12 Tf 72 700 Td (Length is wrong) Tj ET");
			var xobject = builder.Register("XObject", builder.AddStream(
				$"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /{font.Name} {font.Reference} >> >>",
				content,
				declaredLength: content.Length + 40));

			page.Draw(xobject, 1, 0, 0, 1, 0, 0);

			using (var document = TestPdf.Open(builder))
				Assert.Contains("Length is wrong", document.ExtractText());
		}

		/// <summary>Data that is not a PDF document throws <see cref="PdfException"/>.</summary>
		[Fact]
		public void NotAPdf_Throws()
		{
			Assert.Throws<PdfException>(() => PdfDocument.Open(Encoding.ASCII.GetBytes("This is a plain text file, not a PDF.")));
		}

		/// <summary>
		/// Creates a two-page document with a line of text on the first page.
		/// </summary>
		/// <param name="text">The text of the first page.</param>
		/// <returns>The document builder.</returns>
		internal static PdfBuilder CreateDocument(string text)
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			builder.AddPage().Text(font, 12, 72, 700, text);
			builder.AddPage().Text(font, 12, 72, 700, "Page two");
			return builder;
		}

		/// <summary>
		/// Counts the occurrences of an ASCII marker in a file.
		/// </summary>
		/// <param name="data">The file.</param>
		/// <param name="marker">The marker.</param>
		/// <returns>The number of occurrences.</returns>
		private static int CountOccurrences(byte[] data, string marker)
		{
			var text = PdfText.FromBytes(data);
			var count = 0;
			for (int i = text.IndexOf(marker, StringComparison.Ordinal); i >= 0; i = text.IndexOf(marker, i + 1, StringComparison.Ordinal))
				count++;

			return count;
		}
	}
}
