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

// Entry point of the library: opens a PDF document and exposes its pages,
// metadata and navigation structures.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// Represents a PDF document opened for content extraction.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Open a document with one of the <see cref="Open(Stream, string)"/> overloads, then use the
	/// extraction methods to retrieve text, tables, images and structured content.
	/// </para>
	/// <para>
	/// Damaged files are repaired transparently whenever possible. Encrypted documents are opened
	/// with the empty user password, or with the user or owner password when provided.
	/// </para>
	/// <code><![CDATA[
	/// using (var document = PdfDocument.Open("report.pdf"))
	/// {
	///     Console.WriteLine($"{document.Info.Title}: {document.PageCount} pages");
	/// }
	/// ]]></code>
	/// </remarks>
	public sealed class PdfDocument : IDisposable
	{
		/// <summary>The pages in document order, collected from the page tree when the document is opened.</summary>
		private readonly List<PdfPage> _pages = new List<PdfPage>();

		/// <summary>The document information, loaded on first access.</summary>
		private PdfDocumentInfo _info;

		/// <summary>The top-level outline items, loaded on first access.</summary>
		private IReadOnlyList<PdfOutline> _outlines;

		/// <summary>The page labels keyed by 1-based page number, loaded on first access.</summary>
		private Dictionary<int, string> _pageLabels;

		/// <summary>
		/// Initializes a document over a parsed file and loads its page tree.
		/// </summary>
		/// <param name="file">The parsed PDF file.</param>
		private PdfDocument(PdfFile file)
		{
			this.File = file;
			LoadPages();
		}

		#region Open

		/// <summary>
		/// Opens a PDF document from a stream.
		/// </summary>
		/// <param name="stream">The stream to read. It is read to the end and can be closed after this method returns.</param>
		/// <param name="password">The user or owner password of an encrypted document; null for none.</param>
		/// <returns>The opened document.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
		/// <exception cref="PdfException">The data is not a PDF document.</exception>
		/// <exception cref="PdfPasswordException">The document is encrypted and the password is wrong or missing.</exception>
		public static PdfDocument Open(Stream stream, string password = null)
		{
			if (stream == null)
				throw new ArgumentNullException(nameof(stream));

			if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment) && segment.Offset == 0 && segment.Count == segment.Array.Length)
				return Open(segment.Array, password);

			using (var copy = new MemoryStream(stream.CanSeek ? (int)Math.Max(0, stream.Length - stream.Position) : 1024 * 64))
			{
				stream.CopyTo(copy);
				return Open(copy.ToArray(), password);
			}
		}

		/// <summary>
		/// Opens a PDF document from a byte array.
		/// </summary>
		/// <param name="data">The bytes of the PDF file. The array is used directly and must not be modified while the document is open.</param>
		/// <param name="password">The user or owner password of an encrypted document; null for none.</param>
		/// <returns>The opened document.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
		/// <exception cref="PdfException">The data is not a PDF document.</exception>
		/// <exception cref="PdfPasswordException">The document is encrypted and the password is wrong or missing.</exception>
		public static PdfDocument Open(byte[] data, string password = null)
		{
			if (data == null)
				throw new ArgumentNullException(nameof(data));

			return new PdfDocument(new PdfFile(data, password));
		}

		/// <summary>
		/// Opens a PDF document from a file.
		/// </summary>
		/// <param name="path">The path of the file.</param>
		/// <param name="password">The user or owner password of an encrypted document; null for none.</param>
		/// <returns>The opened document.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
		/// <exception cref="PdfException">The file is not a PDF document.</exception>
		/// <exception cref="PdfPasswordException">The document is encrypted and the password is wrong or missing.</exception>
		public static PdfDocument Open(string path, string password = null)
		{
			if (path == null)
				throw new ArgumentNullException(nameof(path));

			return Open(System.IO.File.ReadAllBytes(path), password);
		}

		#endregion

		#region Properties

		/// <summary>
		/// The underlying file.
		/// </summary>
		internal PdfFile File { get; }

		/// <summary>
		/// The cache of parsed fonts and colour spaces.
		/// </summary>
		internal ResourceCache ResourceCache { get; } = new ResourceCache();

		/// <summary>
		/// The document catalog.
		/// </summary>
		internal PdfDictionary Catalog => this.File.Catalog;

		/// <summary>
		/// The number of pages.
		/// </summary>
		public int PageCount => _pages.Count;

		/// <summary>
		/// The pages of the document, in order.
		/// </summary>
		public IReadOnlyList<PdfPage> Pages => _pages;

		/// <summary>
		/// The PDF version declared in the file header, for example "1.7".
		/// </summary>
		public string Version => this.File.Version;

		/// <summary>
		/// True when the document is encrypted.
		/// </summary>
		public bool IsEncrypted => this.File.Decryptor != null;

		/// <summary>
		/// True when the file was damaged and had to be repaired while opening.
		/// </summary>
		public bool WasRepaired => this.File.WasRepaired;

		/// <summary>
		/// True when the document is a tagged PDF with a logical structure tree.
		/// </summary>
		/// <remarks>
		/// Tagged documents carry semantic information (headings, paragraphs, tables, figures
		/// with alternate text) that the extractor uses to improve the results.
		/// </remarks>
		public bool IsTagged => this.Catalog.GetDictionary(PdfNames.StructTreeRoot) != null;

		/// <summary>
		/// The natural language of the document, for example "en-US", or null when not specified.
		/// </summary>
		public string Language => this.Catalog.GetText(PdfNames.Lang);

		/// <summary>
		/// The document information: title, author, dates, etc.
		/// </summary>
		public PdfDocumentInfo Info => _info ?? (_info = PdfDocumentInfo.Load(this));

		/// <summary>
		/// The document outline (bookmarks), or an empty list when the document has none.
		/// </summary>
		public IReadOnlyList<PdfOutline> Outlines => _outlines ?? (_outlines = PdfOutline.Load(this));

		#endregion

		#region Methods

		/// <summary>
		/// Returns the 1-based page number of the page dictionary <paramref name="pageReference"/>.
		/// </summary>
		/// <param name="pageReference">A page dictionary or a reference to it.</param>
		/// <returns>The page number, or 0 when not found.</returns>
		internal int GetPageNumber(object pageReference)
		{
			var dictionary = PdfFile.Resolve(this.File, pageReference) as PdfDictionary;
			if (dictionary == null)
				return 0;

			foreach (var page in _pages)
			{
				if (ReferenceEquals(page.Dictionary, dictionary))
					return page.Number;
			}

			return 0;
		}

		/// <summary>
		/// Returns the label of a page.
		/// </summary>
		/// <param name="number">The 1-based page number.</param>
		/// <returns>The page label, or the page number as text when the document doesn't define labels.</returns>
		internal string GetPageLabel(int number)
		{
			if (_pageLabels == null)
				_pageLabels = LoadPageLabels();

			return _pageLabels.TryGetValue(number, out var label) ? label : number.ToString(CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Releases the resources used by the document.
		/// </summary>
		public void Dispose()
		{
			_pages.Clear();
		}

		#endregion

		#region Extraction

		/// <summary>
		/// Extracts the content of the document: text with formatting and structure, tables and images.
		/// </summary>
		/// <param name="options">The extraction options; null for the defaults.</param>
		/// <returns>The extracted content.</returns>
		/// <exception cref="OperationCanceledException">The extraction was canceled through <see cref="PdfExtractionOptions.CancellationToken"/>.</exception>
		/// <exception cref="FormatException"><see cref="PdfExtractionOptions.Pages"/> is not a valid page list.</exception>
		/// <remarks>
		/// <code><![CDATA[
		/// using (var document = PdfDocument.Open("report.pdf"))
		/// {
		///     var content = document.Extract();
		///     Console.WriteLine(content.ToMarkdown());
		/// }
		/// ]]></code>
		/// </remarks>
		public PdfContent Extract(PdfExtractionOptions options = null)
		{
			return new Extraction.ExtractionEngine(this, options ?? new PdfExtractionOptions()).Run();
		}

		/// <summary>
		/// Extracts the text of the document in reading order, without images.
		/// </summary>
		/// <param name="pages">The pages to extract, for example "1-3, 5"; null for all the pages.</param>
		/// <returns>The plain text.</returns>
		public string ExtractText(string pages = null)
		{
			return Extract(new PdfExtractionOptions { Pages = pages, ExtractImages = false }).ToText();
		}

		/// <summary>
		/// Extracts the tables of the document.
		/// </summary>
		/// <param name="pages">The pages to extract, for example "1-3, 5"; null for all the pages.</param>
		/// <returns>The tables in reading order.</returns>
		public IReadOnlyList<PdfTable> ExtractTables(string pages = null)
		{
			return Extract(new PdfExtractionOptions { Pages = pages, ExtractImages = false }).Tables;
		}

		/// <summary>
		/// Extracts the images and figures of the document.
		/// </summary>
		/// <param name="options">The image options; null for the defaults.</param>
		/// <param name="pages">The pages to extract, for example "1-3, 5"; null for all the pages.</param>
		/// <returns>The distinct images in order of appearance.</returns>
		public IReadOnlyList<PdfImage> ExtractImages(PdfImageOptions options = null, string pages = null)
		{
			return Extract(new PdfExtractionOptions { Pages = pages, Images = options ?? new PdfImageOptions() }).Images;
		}

		#endregion

		#region Page tree

		/// <summary>
		/// Collects the pages by walking the page tree depth-first (ISO 32000-2, 7.7.3), falling back
		/// to a scan of all the page objects when the tree is broken or empty.
		/// </summary>
		private void LoadPages()
		{
			var root = this.Catalog.GetDictionary(PdfNames.Pages);
			var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

			if (root != null)
				Walk(root, null, null, null, 0, visited, 0);

			// the page tree is broken or empty: collect the page objects directly.
			if (_pages.Count == 0)
				CollectOrphanPages(visited);
		}

		/// <summary>
		/// Visits a page tree node, adding the pages it contains and propagating the inheritable
		/// attributes (ISO 32000-2, 7.7.3.4) to its descendants.
		/// </summary>
		/// <param name="node">The page tree node or page object.</param>
		/// <param name="resources">The inherited <c>/Resources</c>, or null.</param>
		/// <param name="mediaBox">The inherited <c>/MediaBox</c>, or null.</param>
		/// <param name="cropBox">The inherited <c>/CropBox</c>, or null.</param>
		/// <param name="rotation">The inherited <c>/Rotate</c> value, in degrees.</param>
		/// <param name="visited">The nodes already visited, used to break cycles.</param>
		/// <param name="depth">The current depth; nodes deeper than 64 levels are ignored.</param>
		private void Walk(PdfDictionary node, PdfDictionary resources, double[] mediaBox, double[] cropBox, int rotation, HashSet<PdfDictionary> visited, int depth)
		{
			if (depth > 64 || !visited.Add(node))
				return;

			// inheritable attributes.
			resources = node.GetDictionary(PdfNames.Resources) ?? resources;
			mediaBox = node.GetArray(PdfNames.MediaBox)?.ToNumbers() ?? mediaBox;
			cropBox = node.GetArray(PdfNames.CropBox)?.ToNumbers() ?? cropBox;
			if (node.Has(PdfNames.Rotate))
				rotation = node.GetInt(PdfNames.Rotate);

			var kids = node.GetArray(PdfNames.Kids);

			// a node is a page when it says so, or when it has no kids but has content (missing /Type).
			var isPage = node.IsType("Page") || (kids == null && (node.Has(PdfNames.Contents) || node.Has(PdfNames.MediaBox)) && !node.IsType("Pages"));
			if (isPage)
			{
				_pages.Add(new PdfPage(this, _pages.Count + 1, node, resources, mediaBox, cropBox, rotation));
				return;
			}

			if (kids == null)
				return;

			for (int i = 0; i < kids.Count; i++)
			{
				if (kids.GetDictionary(i) is PdfDictionary kid)
					Walk(kid, resources, mediaBox, cropBox, rotation, visited, depth + 1);
			}
		}

		/// <summary>
		/// Adds every <c>/Type /Page</c> object in the file that the page tree walk didn't reach.
		/// </summary>
		/// <param name="visited">The nodes already visited by the page tree walk.</param>
		private void CollectOrphanPages(HashSet<PdfDictionary> visited)
		{
			foreach (var dictionary in this.File.EnumerateObjects<PdfDictionary>())
			{
				if (dictionary.IsType("Page") && !visited.Contains(dictionary))
					Walk(dictionary, null, null, null, 0, visited, 0);
			}
		}

		#endregion

		#region Page labels

		/// <summary>
		/// Builds the page labels from the <c>/PageLabels</c> number tree (ISO 32000-2, 12.4.2).
		/// </summary>
		/// <returns>The labels keyed by 1-based page number; pages outside any range are omitted.</returns>
		private Dictionary<int, string> LoadPageLabels()
		{
			var labels = new Dictionary<int, string>();
			var tree = this.Catalog.GetDictionary("PageLabels");
			if (tree == null)
				return labels;

			// collect the label ranges: page index -> label dictionary.
			var ranges = new SortedDictionary<int, PdfDictionary>();
			PdfNumberTree.Collect(tree, (key, value) =>
			{
				if (PdfFile.Resolve(this.File, value) is PdfDictionary dictionary)
					ranges[key] = dictionary;
			});

			if (ranges.Count == 0)
				return labels;

			var starts = new List<int>(ranges.Keys);
			for (int r = 0; r < starts.Count; r++)
			{
				var start = starts[r];
				var end = r + 1 < starts.Count ? starts[r + 1] : _pages.Count;
				var range = ranges[start];

				var style = range.GetNameValue(PdfNames.S);
				var prefix = range.GetText("P") ?? string.Empty;
				var first = range.GetInt("St", 1);

				for (int index = start; index < end && index < _pages.Count; index++)
				{
					var value = first + index - start;
					labels[index + 1] = prefix + FormatLabel(style, value);
				}
			}

			return labels;
		}

		/// <summary>
		/// Formats the numeric portion of a page label.
		/// </summary>
		/// <param name="style">The numbering style: <c>D</c>, <c>R</c>, <c>r</c>, <c>A</c> or <c>a</c>; any other value omits the number.</param>
		/// <param name="value">The page value within the range.</param>
		/// <returns>The formatted number, or an empty string.</returns>
		private static string FormatLabel(string style, int value)
		{
			switch (style)
			{
				case "D": return value.ToString(CultureInfo.InvariantCulture);
				case "R": return ToRoman(value).ToUpperInvariant();
				case "r": return ToRoman(value);
				case "A": return ToLetters(value).ToUpperInvariant();
				case "a": return ToLetters(value);
				default: return string.Empty;
			}
		}

		/// <summary>
		/// Formats <paramref name="value"/> as lowercase roman numerals.
		/// </summary>
		/// <param name="value">The value.</param>
		/// <returns>The roman numerals, or decimal digits when the value is outside 1..3999.</returns>
		private static string ToRoman(int value)
		{
			if (value <= 0 || value >= 4000)
				return value.ToString(CultureInfo.InvariantCulture);

			var numerals = new[] { "m", "cm", "d", "cd", "c", "xc", "l", "xl", "x", "ix", "v", "iv", "i" };
			var values = new[] { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };

			var sb = new StringBuilder();
			for (int i = 0; i < values.Length; i++)
			{
				while (value >= values[i])
				{
					sb.Append(numerals[i]);
					value -= values[i];
				}
			}

			return sb.ToString();
		}

		/// <summary>
		/// Formats <paramref name="value"/> as lowercase letters: a..z, aa..zz, aaa..zzz, as defined by the PDF specification.
		/// </summary>
		/// <param name="value">The value.</param>
		/// <returns>The letters, or an empty string when the value is not positive.</returns>
		private static string ToLetters(int value)
		{
			if (value <= 0)
				return string.Empty;

			var letter = (char)('a' + (value - 1) % 26);
			return new string(letter, (value - 1) / 26 + 1);
		}

		#endregion
	}

	/// <summary>
	/// Compares objects by reference; used for cycle detection in object graphs.
	/// </summary>
	internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>, IEqualityComparer<PdfDictionary>
	{
		/// <summary>
		/// The shared instance.
		/// </summary>
		public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

		/// <inheritdoc/>
		public new bool Equals(object x, object y) => ReferenceEquals(x, y);

		/// <inheritdoc/>
		public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);

		/// <inheritdoc/>
		public bool Equals(PdfDictionary x, PdfDictionary y) => ReferenceEquals(x, y);

		/// <inheritdoc/>
		public int GetHashCode(PdfDictionary obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}
}
