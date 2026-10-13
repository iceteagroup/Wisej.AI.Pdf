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

// Test helper: shortcuts to parse PDF objects and to extract generated documents.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Wisej.AI.Pdf.IO;
using Wisej.AI.Pdf.Objects;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Helpers
{
	/// <summary>
	/// Shortcuts shared by the tests.
	/// </summary>
	internal static class TestPdf
	{
		/// <summary>
		/// The longest time a single extraction may take before a test fails: guards against hangs.
		/// </summary>
		public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

		/// <summary>
		/// Body text used to fill paragraphs: plain sentences without hyphens or digits.
		/// </summary>
		public static readonly string[] Prose =
		{
			"The quick brown fox jumps over the lazy dog near the river bank.",
			"Every morning the baker opens the shop and sells fresh bread to the town.",
			"Clear writing helps readers understand complex ideas without extra effort.",
			"The committee reviewed the proposal and asked for a revised budget plan.",
			"Small changes in the process can produce large savings over the year.",
			"Students gathered in the library to prepare for the final examination.",
		};

		#region Parsing

		/// <summary>
		/// Parses a direct object written in PDF syntax, without a file.
		/// </summary>
		/// <param name="text">The object, for example "[1 2 /Name]".</param>
		/// <returns>The parsed object.</returns>
		public static object ParseObject(string text)
		{
			var parser = new PdfParser(new PdfLexer(PdfText.ToBytes(text)), null);
			return parser.ParseObject();
		}

		/// <summary>
		/// Parses a stream object from its dictionary entries and data, without a file.
		/// </summary>
		/// <param name="entries">The dictionary entries, without delimiters and without /Length.</param>
		/// <param name="data">The stream data.</param>
		/// <returns>The parsed stream.</returns>
		public static PdfStream ParseStream(string entries, byte[] data)
		{
			var prefix = PdfText.ToBytes($"1 0 obj\n<< {entries} /Length {data.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n");
			var suffix = PdfText.ToBytes("\nendstream\nendobj\n");
			var bytes = prefix.Concat(data).Concat(suffix).ToArray();

			var parser = new PdfParser(new PdfLexer(bytes), null);
			return Assert.IsType<PdfStream>(parser.ParseIndirectObject(null, false));
		}

		/// <summary>
		/// Parses a stream object whose data is text.
		/// </summary>
		/// <param name="entries">The dictionary entries, without delimiters and without /Length.</param>
		/// <param name="data">The stream data as a byte string.</param>
		/// <returns>The parsed stream.</returns>
		public static PdfStream ParseStream(string entries, string data) => ParseStream(entries, PdfText.ToBytes(data));

		#endregion

		#region Extraction

		/// <summary>
		/// Opens a generated document.
		/// </summary>
		/// <param name="builder">The document.</param>
		/// <param name="options">The write options.</param>
		/// <param name="password">The password, for encrypted documents.</param>
		/// <returns>The opened document.</returns>
		public static PdfDocument Open(PdfBuilder builder, PdfWriteOptions options = null, string password = null)
		{
			return PdfDocument.Open(builder.ToArray(options), password);
		}

		/// <summary>
		/// Extracts a generated document, failing the test when the extraction doesn't complete in time.
		/// </summary>
		/// <param name="builder">The document.</param>
		/// <param name="options">The extraction options; null for the defaults.</param>
		/// <returns>The extracted content.</returns>
		public static PdfContent Extract(PdfBuilder builder, PdfExtractionOptions options = null)
		{
			return Extract(builder.ToArray(), options);
		}

		/// <summary>
		/// Extracts a PDF file, failing the test when the extraction doesn't complete in time.
		/// </summary>
		/// <param name="pdf">The PDF file.</param>
		/// <param name="options">The extraction options; null for the defaults.</param>
		/// <returns>The extracted content.</returns>
		public static PdfContent Extract(byte[] pdf, PdfExtractionOptions options = null)
		{
			return RunWithTimeout(() =>
			{
				using (var document = PdfDocument.Open(pdf))
					return document.Extract(options);
			});
		}

		/// <summary>
		/// Runs a function on the thread pool and fails when it doesn't complete within <see cref="Timeout"/>.
		/// </summary>
		/// <typeparam name="T">The type of the result.</typeparam>
		/// <param name="function">The function.</param>
		/// <returns>The result of the function.</returns>
		public static T RunWithTimeout<T>(Func<T> function)
		{
			var task = Task.Run(function);

			// wait on the handle rather than Task.Wait, which would wrap the exceptions of the function.
			if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(Timeout))
				Assert.Fail($"The operation did not complete within {Timeout.TotalSeconds} seconds.");

			return task.GetAwaiter().GetResult();
		}

		/// <summary>
		/// Returns the text blocks of the content, in reading order.
		/// </summary>
		/// <param name="content">The content.</param>
		/// <returns>The paragraphs, headings, captions and code blocks.</returns>
		public static List<PdfTextBlock> TextBlocks(PdfContent content) => content.Blocks.OfType<PdfTextBlock>().ToList();

		/// <summary>
		/// Returns all the runs of the text blocks of the content.
		/// </summary>
		/// <param name="content">The content.</param>
		/// <returns>The runs, in reading order.</returns>
		public static List<PdfTextRun> Runs(PdfContent content) => TextBlocks(content).SelectMany(b => b.Runs).ToList();

		/// <summary>
		/// Collapses runs of whitespace to single spaces, to compare text regardless of line breaks.
		/// </summary>
		/// <param name="text">The text.</param>
		/// <returns>The normalized text.</returns>
		public static string Normalize(string text) => string.Join(" ", text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));

		#endregion
	}
}
