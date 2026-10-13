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

// Tests of the document-level API: information dictionary, outlines, page
// labels, page selection, progress and the PdfExtractor helpers.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Wisej.AI.Pdf.Tests.Helpers;
using Wisej.AI.Pdf.Tests.Output;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Document
{
	/// <summary>
	/// Tests of <see cref="PdfDocument"/>, <see cref="PdfDocumentInfo"/>, <see cref="PdfOutline"/> and <see cref="PdfExtractor"/>.
	/// </summary>
	public class DocumentTests
	{
		#region Information and outline

		/// <summary>The information dictionary is decoded, including Unicode strings and dates with time zones.</summary>
		[Fact]
		public void Info_EntriesAndDates()
		{
			var builder = new PdfBuilder();
			builder.AddPage();
			builder.SetInfo(
				("Title", "Résumé — Ünïcödé"),
				("Author", "Jane Doe"),
				("Subject", "Testing"),
				("Keywords", "pdf, tests"),
				("Creator", "PdfBuilder"),
				("Producer", "Wisej.AI.Pdf.Tests"),
				("CreationDate", "D:20240115103000+01'00'"),
				("ModDate", "D:2023"),
				("Custom", "Extra value"));

			using (var document = TestPdf.Open(builder))
			{
				var info = document.Info;
				Assert.Equal("Résumé — Ünïcödé", info.Title);
				Assert.Equal("Jane Doe", info.Author);
				Assert.Equal("Testing", info.Subject);
				Assert.Equal("pdf, tests", info.Keywords);
				Assert.Equal("PdfBuilder", info.Creator);
				Assert.Equal("Wisej.AI.Pdf.Tests", info.Producer);
				Assert.Equal(new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(1)), info.CreationDate);
				Assert.Equal(2023, info.ModificationDate?.Year);
				Assert.Equal("Extra value", info.Entries["Custom"]);
			}
		}

		/// <summary>A document without an information dictionary has empty information.</summary>
		[Fact]
		public void Info_Missing()
		{
			var builder = new PdfBuilder();
			builder.AddPage();

			using (var document = TestPdf.Open(builder))
			{
				Assert.NotNull(document.Info);
				Assert.Null(document.Info.Title);
				Assert.Null(document.Info.CreationDate);
			}
		}

		/// <summary>The outline is read with titles, levels, target pages and children.</summary>
		[Fact]
		public void Outlines_Tree()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			var first = builder.AddPage().Text(font, 12, 72, 700, "Introduction text");
			var second = builder.AddPage().Text(font, 12, 72, 700, "Details text");
			var third = builder.AddPage().Text(font, 12, 72, 700, "Summary text");

			var chapter = builder.AddOutline("Chapter 1", first);
			builder.AddOutline("Section 1.1", second, chapter);
			builder.AddOutline("Section 1.2", third, chapter);
			builder.AddOutline("Chapter 2", third);

			using (var document = TestPdf.Open(builder))
			{
				var outlines = document.Outlines;

				Assert.Equal(new[] { "Chapter 1", "Chapter 2" }, outlines.Select(o => o.Title));
				Assert.Equal(1, outlines[0].Level);
				Assert.Equal(1, outlines[0].PageNumber);
				Assert.Equal(3, outlines[1].PageNumber);

				var children = outlines[0].Children;
				Assert.Equal(new[] { "Section 1.1", "Section 1.2" }, children.Select(o => o.Title));
				Assert.Equal(2, children[0].Level);
				Assert.Equal(new[] { 2, 3 }, children.Select(o => o.PageNumber));
			}
		}

		/// <summary>Page labels with styles, prefixes and start values.</summary>
		[Fact]
		public void PageLabels()
		{
			var builder = new PdfBuilder();
			for (int i = 0; i < 6; i++)
				builder.AddPage();

			builder.AddCatalogEntry("/PageLabels << /Nums [0 << /S /r >> 2 << /S /D >> 4 << /S /A /P (App-) /St 2 >>] >>");

			var content = TestPdf.Extract(builder);

			Assert.Equal(new[] { "i", "ii", "1", "2", "App-B", "App-C" }, content.Pages.Select(p => p.Label));
		}

		#endregion

		#region Pages and options

		/// <summary>Page dimensions follow the media box, the crop box and the user unit.</summary>
		[Fact]
		public void Pages_Dimensions()
		{
			var builder = new PdfBuilder();
			builder.AddPage(595, 842);
			builder.AddPage(612, 792).Entry("/CropBox [36 36 576 756]");
			builder.AddPage(100, 100).Entry("/UserUnit 2");

			using (var document = TestPdf.Open(builder))
			{
				Assert.Equal((595.0, 842.0), (document.Pages[0].Width, document.Pages[0].Height));
				Assert.Equal((540.0, 720.0), (document.Pages[1].Width, document.Pages[1].Height));
				Assert.Equal((200.0, 200.0), (document.Pages[2].Width, document.Pages[2].Height));
			}
		}

		/// <summary>The page selection accepts lists, open ranges and "last".</summary>
		[Theory]
		[InlineData(null, new[] { 1, 2, 3, 4, 5 })]
		[InlineData("2", new[] { 2 })]
		[InlineData("1-2, 4", new[] { 1, 2, 4 })]
		[InlineData("4-", new[] { 4, 5 })]
		[InlineData("-2", new[] { 1, 2 })]
		[InlineData("last", new[] { 5 })]
		[InlineData("3;1;3", new[] { 1, 3 })]
		[InlineData("4-9", new[] { 4, 5 })]
		public void PageSelection(string pages, int[] expected)
		{
			Assert.Equal(expected, new PdfExtractionOptions { Pages = pages }.GetPageNumbers(5));
		}

		/// <summary>An invalid page selection throws <see cref="FormatException"/>.</summary>
		[Fact]
		public void PageSelection_Invalid()
		{
			Assert.Throws<FormatException>(() => new PdfExtractionOptions { Pages = "one" }.GetPageNumbers(5));
		}

		/// <summary>Only the selected pages are extracted; the page count still reports the whole document.</summary>
		[Fact]
		public void Extract_SelectedPages()
		{
			var builder = new PdfBuilder();
			var font = builder.Font("Helvetica");
			for (int p = 1; p <= 4; p++)
				builder.AddPage().Text(font, 12, 72, 700, $"Text of page {p}.");

			using (var document = TestPdf.Open(builder))
			{
				var content = document.Extract(new PdfExtractionOptions { Pages = "2-3" });

				Assert.Equal(4, content.PageCount);
				Assert.Equal(new[] { 2, 3 }, content.Pages.Select(p => p.Number));
				Assert.Equal("Text of page 3.", document.ExtractText("3").Trim());
			}
		}

		/// <summary>The progress callback reports every page.</summary>
		[Fact]
		public void Extract_ReportsProgress()
		{
			var builder = new PdfBuilder();
			for (int p = 0; p < 5; p++)
				builder.AddPage().Text(builder.Font("Helvetica"), 12, 72, 700, "Page");

			var reports = new ConcurrentBag<(int Done, int Total)>();
			TestPdf.Extract(builder, new PdfExtractionOptions { Progress = (done, total) => reports.Add((done, total)) });

			Assert.Equal(5, reports.Count);
			Assert.All(reports, r => Assert.Equal(5, r.Total));
			Assert.Equal(Enumerable.Range(1, 5), reports.Select(r => r.Done).OrderBy(d => d));
		}

		/// <summary>The result is the same with one thread and with many.</summary>
		[Fact]
		public void Extract_IsDeterministicAcrossThreads()
		{
			var builder = LayoutReport();

			var single = TestPdf.Extract(builder, new PdfExtractionOptions { MaxDegreeOfParallelism = 1 }).ToJson();
			var parallel = TestPdf.Extract(builder, new PdfExtractionOptions { MaxDegreeOfParallelism = 8 }).ToJson();

			Assert.Equal(single, parallel);
		}

		#endregion

		#region PdfExtractor

		/// <summary>The one-call helpers read streams and files.</summary>
		[Fact]
		public void Extractor_Helpers()
		{
			var bytes = MarkdownTests.CreateFigureDocument().ToArray();

			Assert.Contains("The quick brown fox", PdfExtractor.ToText(new MemoryStream(bytes)));
			Assert.Contains("![Figure 1: The logo.](images/image-1.png)", PdfExtractor.ToMarkdown(new MemoryStream(bytes)));
			Assert.Contains("<figure>", PdfExtractor.ToHtml(new MemoryStream(bytes)));
			Assert.Contains("\"pageCount\"", PdfExtractor.ToJson(new MemoryStream(bytes)));
			Assert.Single(PdfExtractor.ExtractImages(new MemoryStream(bytes)));
			Assert.Empty(PdfExtractor.ExtractTables(new MemoryStream(bytes)));

			var path = Path.Combine(AppContext.BaseDirectory, "pdf-document-" + Guid.NewGuid().ToString("N") + ".pdf");
			try
			{
				File.WriteAllBytes(path, bytes);
				Assert.Equal(1, PdfExtractor.Extract(path).PageCount);
			}
			finally
			{
				File.Delete(path);
			}
		}

		/// <summary>A non-seekable stream is read to the end.</summary>
		[Fact]
		public void Open_NonSeekableStream()
		{
			var bytes = MarkdownTests.CreateFigureDocument().ToArray();

			using (var document = PdfDocument.Open(new NonSeekableStream(bytes)))
				Assert.Equal(1, document.PageCount);
		}

		/// <summary>Null arguments throw <see cref="ArgumentNullException"/>.</summary>
		[Fact]
		public void Open_NullArguments()
		{
			Assert.Throws<ArgumentNullException>(() => PdfDocument.Open((byte[])null));
			Assert.Throws<ArgumentNullException>(() => PdfDocument.Open((Stream)null));
			Assert.Throws<ArgumentNullException>(() => PdfDocument.Open((string)null));
		}

		/// <summary>An extracted image saves to a stream and to a file, adding the extension.</summary>
		[Fact]
		public void Image_Save()
		{
			var image = Assert.Single(TestPdf.Extract(MarkdownTests.CreateFigureDocument()).Images);

			var stream = new MemoryStream();
			image.Save(stream);
			Assert.Equal(image.Data, stream.ToArray());

			var path = Path.Combine(AppContext.BaseDirectory, "pdf-image-" + Guid.NewGuid().ToString("N"));
			var saved = image.Save(path);
			try
			{
				Assert.EndsWith(".png", saved);
				Assert.Equal(image.Data, File.ReadAllBytes(saved));
			}
			finally
			{
				File.Delete(saved);
			}
		}

		#endregion

		/// <summary>
		/// Creates a multi-page report with headings, lists and tables.
		/// </summary>
		/// <returns>The document builder.</returns>
		private static PdfBuilder LayoutReport()
		{
			var builder = Layout.LayoutTests.CreateReport(4);
			var font = builder.Font("Helvetica");
			foreach (var page in builder.Pages)
				page.Paragraph(font, 11, 80, 600, 14, "• First point of the page", "• Second point of the page");

			return builder;
		}

		/// <summary>
		/// A forward-only, read-only stream, like a network stream.
		/// </summary>
		private sealed class NonSeekableStream : Stream
		{
			// the data being read.
			private readonly MemoryStream _inner;

			/// <summary>
			/// Initializes the stream over a buffer.
			/// </summary>
			/// <param name="data">The data.</param>
			public NonSeekableStream(byte[] data)
			{
				_inner = new MemoryStream(data, false);
			}

			/// <inheritdoc/>
			public override bool CanRead => true;

			/// <inheritdoc/>
			public override bool CanSeek => false;

			/// <inheritdoc/>
			public override bool CanWrite => false;

			/// <inheritdoc/>
			public override long Length => throw new NotSupportedException();

			/// <inheritdoc/>
			public override long Position
			{
				get => throw new NotSupportedException();
				set => throw new NotSupportedException();
			}

			/// <inheritdoc/>
			public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 1000));

			/// <inheritdoc/>
			public override void Flush() { }

			/// <inheritdoc/>
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

			/// <inheritdoc/>
			public override void SetLength(long value) => throw new NotSupportedException();

			/// <inheritdoc/>
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}
	}
}
