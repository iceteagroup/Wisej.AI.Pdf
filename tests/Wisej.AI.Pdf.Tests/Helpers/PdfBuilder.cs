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

// Test helper that writes small, valid PDF files: pages, fonts, images,
// annotations, outlines, incremental updates, xref streams, object streams,
// encryption and deliberately damaged cross-reference data.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Wisej.AI.Pdf.Tests.Helpers
{
	/// <summary>
	/// Builds PDF documents in memory for the tests.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Objects are written in PDF syntax as "byte strings" (see <see cref="PdfText"/>), which keeps
	/// the tests close to the specification. Pages, the catalog and the outline are generated
	/// when the document is serialized.
	/// </para>
	/// <para>
	/// <see cref="CommitRevision"/> starts an incremental update: the objects changed afterwards
	/// are appended to the file in a new section whose trailer points to the previous one.
	/// </para>
	/// <code><![CDATA[
	/// var pdf = new PdfBuilder();
	/// var font = pdf.Font("Helvetica");
	/// pdf.AddPage().Text(font, 12, 72, 720, "Hello");
	/// var bytes = pdf.ToArray();
	/// ]]></code>
	/// </remarks>
	internal sealed class PdfBuilder
	{
		#region Nested types

		// the state of an object in one revision of the file.
		private sealed class ObjectVersion
		{
			// the revision that introduced this version.
			public int Revision;

			// the object body, or the entries of the stream dictionary (without << >> and /Length).
			public string Body;

			// the stream data; null for objects that are not streams.
			public byte[] Data;

			// a /Length value that differs from the real data length, to test stream repair.
			public int? DeclaredLength;

			// returns true when the other version has the same body, declared length and stream data.
			public bool HasSameContent(ObjectVersion other)
			{
				if (this.Body != other.Body || this.DeclaredLength != other.DeclaredLength)
					return false;

				if (this.Data == null || other.Data == null)
					return this.Data == other.Data;

				return this.Data.AsSpan().SequenceEqual(other.Data);
			}
		}

		// an object as it is written in one section of the file.
		private sealed class WrittenObject
		{
			// the object number.
			public int Number;

			// the version of the object written in the section.
			public ObjectVersion Version;
		}

		#endregion

		// the trailer /ID: fixed, so the generated files are deterministic.
		private static readonly byte[] DocumentId =
		{
			0x57, 0x69, 0x73, 0x65, 0x6A, 0x2E, 0x41, 0x49, 0x2E, 0x50, 0x64, 0x66, 0x54, 0x65, 0x73, 0x74,
		};

		// the PDF version written in the header.
		private readonly string _version;

		// object versions by object number, oldest first.
		private readonly SortedDictionary<int, List<ObjectVersion>> _objects = new SortedDictionary<int, List<ObjectVersion>>();

		// the pages, in document order.
		private readonly List<PdfPageBuilder> _pages = new List<PdfPageBuilder>();

		// the top-level outline items.
		private readonly List<PdfOutlineItem> _outlines = new List<PdfOutlineItem>();

		// the Standard 14 font resources, by base font name.
		private readonly Dictionary<string, PdfResource> _fonts = new Dictionary<string, PdfResource>(StringComparer.Ordinal);

		// additional catalog entries in PDF syntax.
		private readonly List<string> _catalogEntries = new List<string>();

		// the object number of the document catalog.
		private readonly int _catalog;

		// the object number of the root page tree node.
		private readonly int _pageTree;

		// the object number of the outline root; 0 until the first outline item is added.
		private int _outlineRoot;

		// the object number of the document information dictionary; 0 when there is none.
		private int _info;

		// the next free object number.
		private int _nextNumber = 1;

		// the current revision: 0 for the original file, then one per incremental update.
		private int _revision;

		// the counter that makes the resource names unique.
		private int _resourceCounter;

		/// <summary>
		/// Initializes a new builder.
		/// </summary>
		/// <param name="version">The PDF version written in the header.</param>
		public PdfBuilder(string version = "1.7")
		{
			_version = version;
			_catalog = Reserve();
			_pageTree = Reserve();
		}

		#region Properties

		/// <summary>
		/// The pages added so far.
		/// </summary>
		public IReadOnlyList<PdfPageBuilder> Pages => _pages;

		#endregion

		#region Objects

		/// <summary>
		/// Reserves an object number to be defined later with <see cref="SetObject"/> or <see cref="SetStream"/>.
		/// </summary>
		/// <returns>The object number.</returns>
		public int Reserve() => _nextNumber++;

		/// <summary>
		/// Adds an object.
		/// </summary>
		/// <param name="body">The object in PDF syntax, for example "&lt;&lt; /Type /Font ... &gt;&gt;".</param>
		/// <returns>The object number.</returns>
		public int AddObject(string body)
		{
			var number = Reserve();
			SetObject(number, body);
			return number;
		}

		/// <summary>
		/// Defines or replaces an object in the current revision.
		/// </summary>
		/// <param name="number">The object number.</param>
		/// <param name="body">The object in PDF syntax.</param>
		public void SetObject(int number, string body)
		{
			Store(number, new ObjectVersion { Body = body });
		}

		/// <summary>
		/// Adds a stream object.
		/// </summary>
		/// <param name="entries">The entries of the stream dictionary, without the delimiters and without /Length.</param>
		/// <param name="data">The stream data.</param>
		/// <param name="compress">True to compress the data with FlateDecode.</param>
		/// <param name="declaredLength">A wrong /Length to write instead of the real one, to test stream repair.</param>
		/// <returns>The object number.</returns>
		public int AddStream(string entries, byte[] data, bool compress = false, int? declaredLength = null)
		{
			var number = Reserve();
			SetStream(number, entries, data, compress, declaredLength);
			return number;
		}

		/// <summary>
		/// Defines or replaces a stream object in the current revision.
		/// </summary>
		/// <param name="number">The object number.</param>
		/// <param name="entries">The entries of the stream dictionary, without the delimiters and without /Length.</param>
		/// <param name="data">The stream data.</param>
		/// <param name="compress">True to compress the data with FlateDecode.</param>
		/// <param name="declaredLength">A wrong /Length to write instead of the real one.</param>
		public void SetStream(int number, string entries, byte[] data, bool compress = false, int? declaredLength = null)
		{
			if (compress)
			{
				entries = (entries + " /Filter /FlateDecode").Trim();
				data = Deflate(data);
			}

			Store(number, new ObjectVersion { Body = entries ?? string.Empty, Data = data, DeclaredLength = declaredLength });
		}

		#endregion

		#region Resources

		/// <summary>
		/// Returns a Standard 14 font with WinAnsiEncoding (Symbol and ZapfDingbats keep their built-in encoding).
		/// </summary>
		/// <param name="baseFont">The font name, for example "Helvetica", "Times-Bold" or "Courier".</param>
		/// <returns>The font resource; the same instance for the same name.</returns>
		public PdfResource Font(string baseFont)
		{
			if (_fonts.TryGetValue(baseFont, out var font))
				return font;

			var symbolic = baseFont == "Symbol" || baseFont == "ZapfDingbats";
			var encoding = symbolic ? string.Empty : " /Encoding /WinAnsiEncoding";
			font = Register("Font", AddObject($"<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont}{encoding} >>"));
			_fonts[baseFont] = font;
			return font;
		}

		/// <summary>
		/// Wraps an existing object as a named resource.
		/// </summary>
		/// <param name="category">The resource category, for example "Font" or "XObject".</param>
		/// <param name="number">The object number.</param>
		/// <returns>The resource, with a unique name.</returns>
		public PdfResource Register(string category, int number)
		{
			var prefix = category == "Font" ? "F" : category == "XObject" ? "X" : category.Substring(0, 2);
			return new PdfResource(category, prefix + (++_resourceCounter).ToString(CultureInfo.InvariantCulture), number);
		}

		/// <summary>
		/// Adds an image XObject.
		/// </summary>
		/// <param name="width">The width in samples.</param>
		/// <param name="height">The height in samples.</param>
		/// <param name="colorSpace">The colour space in PDF syntax, for example "/DeviceRGB"; null to omit it (stencil masks).</param>
		/// <param name="bitsPerComponent">The bits per component.</param>
		/// <param name="samples">The sample data, rows padded to whole bytes.</param>
		/// <param name="extra">Additional dictionary entries, for example "/Decode [1 0]".</param>
		/// <param name="compress">True to compress the samples with FlateDecode.</param>
		/// <returns>The image resource.</returns>
		public PdfResource Image(int width, int height, string colorSpace, int bitsPerComponent, byte[] samples, string extra = null, bool compress = true)
		{
			var entries = new StringBuilder("/Type /XObject /Subtype /Image");
			entries.Append(" /Width ").Append(width).Append(" /Height ").Append(height);
			if (colorSpace != null)
				entries.Append(" /ColorSpace ").Append(colorSpace);
			entries.Append(" /BitsPerComponent ").Append(bitsPerComponent);
			if (!string.IsNullOrEmpty(extra))
				entries.Append(' ').Append(extra);

			return Register("XObject", AddStream(entries.ToString(), samples, compress));
		}

		/// <summary>
		/// Adds a stencil mask image (/ImageMask true), painted with the current fill colour.
		/// </summary>
		/// <param name="width">The width in samples.</param>
		/// <param name="height">The height in samples.</param>
		/// <param name="bits">The 1-bit samples, rows padded to whole bytes.</param>
		/// <param name="extra">Additional dictionary entries, for example "/Decode [1 0]".</param>
		/// <returns>The image resource.</returns>
		public PdfResource StencilMask(int width, int height, byte[] bits, string extra = null)
		{
			return Image(width, height, null, 1, bits, ("/ImageMask true " + extra).Trim());
		}

		/// <summary>
		/// Adds a JPEG image XObject (DCTDecode).
		/// </summary>
		/// <param name="jpeg">The JPEG file.</param>
		/// <param name="width">The width in pixels.</param>
		/// <param name="height">The height in pixels.</param>
		/// <param name="colorSpace">The colour space, for example "/DeviceRGB".</param>
		/// <param name="extra">Additional dictionary entries.</param>
		/// <returns>The image resource.</returns>
		public PdfResource JpegImage(byte[] jpeg, int width, int height, string colorSpace, string extra = null)
		{
			var entries = $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode {extra}".Trim();
			return Register("XObject", AddStream(entries, jpeg));
		}

		/// <summary>
		/// Adds a form XObject.
		/// </summary>
		/// <param name="content">The content stream of the form.</param>
		/// <param name="bbox">The bounding box: left, bottom, right, top.</param>
		/// <param name="matrix">The form matrix; null for the identity.</param>
		/// <param name="resources">The resources used by the content.</param>
		/// <returns>The form resource.</returns>
		public PdfResource Form(string content, double[] bbox, double[] matrix = null, params PdfResource[] resources)
		{
			var entries = "/Type /XObject /Subtype /Form /BBox " + PdfText.Array(bbox);
			if (matrix != null)
				entries += " /Matrix " + PdfText.Array(matrix);

			entries += " /Resources " + FormatResources(resources);
			return Register("XObject", AddStream(entries, PdfText.ToBytes(content)));
		}

		/// <summary>
		/// Formats a resource dictionary.
		/// </summary>
		/// <param name="resources">The resources.</param>
		/// <returns>The dictionary in PDF syntax.</returns>
		public static string FormatResources(IEnumerable<PdfResource> resources)
		{
			var categories = new SortedDictionary<string, StringBuilder>(StringComparer.Ordinal);
			foreach (var resource in resources)
			{
				if (!categories.TryGetValue(resource.Category, out var entries))
					categories[resource.Category] = entries = new StringBuilder();

				entries.Append(" /").Append(resource.Name).Append(' ').Append(resource.Reference);
			}

			var sb = new StringBuilder("<<");
			foreach (var category in categories)
				sb.Append(" /").Append(category.Key).Append(" <<").Append(category.Value).Append(" >>");

			return sb.Append(" >>").ToString();
		}

		#endregion

		#region Document structure

		/// <summary>
		/// Adds a page.
		/// </summary>
		/// <param name="width">The width of the media box, in points.</param>
		/// <param name="height">The height of the media box, in points.</param>
		/// <returns>The page builder.</returns>
		public PdfPageBuilder AddPage(double width = 612, double height = 792)
		{
			var page = new PdfPageBuilder(this, Reserve(), Reserve(), width, height);
			_pages.Add(page);
			return page;
		}

		/// <summary>
		/// Sets the document information dictionary.
		/// </summary>
		/// <param name="entries">The entries: key (without slash) and text value.</param>
		public void SetInfo(params (string Key, string Value)[] entries)
		{
			var sb = new StringBuilder("<<");
			foreach (var (key, value) in entries)
				sb.Append(" /").Append(key).Append(' ').Append(PdfText.TextString(value));

			if (_info == 0)
				_info = Reserve();

			SetObject(_info, sb.Append(" >>").ToString());
		}

		/// <summary>
		/// Adds an item to the document outline (bookmarks).
		/// </summary>
		/// <param name="title">The title.</param>
		/// <param name="page">The target page.</param>
		/// <param name="parent">The parent item; null for a top-level item.</param>
		/// <returns>The new item.</returns>
		public PdfOutlineItem AddOutline(string title, PdfPageBuilder page, PdfOutlineItem parent = null)
		{
			if (_outlineRoot == 0)
				_outlineRoot = Reserve();

			var item = new PdfOutlineItem(Reserve(), title, page, parent);
			if (parent == null)
				_outlines.Add(item);
			else
				parent.Children.Add(item);

			return item;
		}

		/// <summary>
		/// Adds an entry to the document catalog, for example "/PageLabels &lt;&lt; ... &gt;&gt;".
		/// </summary>
		/// <param name="entry">The key and value in PDF syntax.</param>
		public void AddCatalogEntry(string entry)
		{
			_catalogEntries.Add(entry);
		}

		/// <summary>
		/// Closes the current revision: the changes made afterwards are written as an incremental update.
		/// </summary>
		public void CommitRevision()
		{
			Materialize();
			_revision++;
		}

		#endregion

		#region Serialization

		/// <summary>
		/// Writes the document.
		/// </summary>
		/// <param name="options">The write options; null for a plain file with a cross-reference table.</param>
		/// <returns>The bytes of the PDF file.</returns>
		public byte[] ToArray(PdfWriteOptions options = null)
		{
			options = options ?? new PdfWriteOptions();
			Materialize();

			if (options.UseObjectStreams && (options.Xref != XrefStyle.Stream || _revision > 0 || options.Encryption != null))
				throw new InvalidOperationException("Object streams are supported in single-revision, unencrypted files with an xref stream.");

			var security = options.Encryption != null ? new PdfSecurityHandler(options.Encryption, DocumentId) : null;

			var output = new MemoryStream();
			if (options.Preamble != null)
				Write(output, options.Preamble);

			// all offsets are relative to the header.
			var origin = output.Length;
			Write(output, "%PDF-" + _version + "\n%âãÏÓ\n");

			var size = _nextNumber;
			var previousXref = -1L;
			for (int revision = 0; revision <= _revision; revision++)
			{
				var section = GetSection(revision);
				if (section.Count == 0 && revision > 0)
					continue;

				// write-time objects (encryption dictionary, object and xref streams) take numbers after the document's.
				var encryptNumber = 0;
				if (security != null && revision == 0)
				{
					encryptNumber = size++;
					section.Add(new WrittenObject { Number = encryptNumber, Version = new ObjectVersion { Body = "<< " + security.DictionaryEntries + " >>" } });
				}

				var offsets = new SortedDictionary<int, long>();
				var compressed = new Dictionary<int, (int Stream, int Index)>();

				if (options.UseObjectStreams)
				{
					var streamNumber = size++;
					section = PackObjectStream(section, streamNumber, compressed);
				}

				foreach (var item in section)
				{
					offsets[item.Number] = output.Length - origin;
					WriteObject(output, item, item.Number == encryptNumber ? null : security);
				}

				var trailer = BuildTrailer(options, previousXref, encryptNumber);
				var xrefOffset = output.Length - origin;
				switch (options.Xref)
				{
					case XrefStyle.Stream:
						{
							var xrefNumber = size++;
							offsets[xrefNumber] = xrefOffset;
							WriteXrefStream(output, xrefNumber, size, offsets, compressed, trailer, revision == 0);
						}
						break;

					case XrefStyle.Corrupt:
						{
							// every offset is wrong, and startxref points into the middle of the first object.
							var garbage = new SortedDictionary<int, long>();
							foreach (var entry in offsets)
								garbage[entry.Key] = entry.Value + 1000003;

							WriteXrefTable(output, garbage, size, trailer, revision == 0);
							xrefOffset = 20;
						}
						break;

					case XrefStyle.None:
						continue;

					default:
						WriteXrefTable(output, offsets, size, trailer, revision == 0);
						break;
				}

				Write(output, "startxref\n" + xrefOffset.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
				previousXref = xrefOffset;
			}

			return output.ToArray();
		}

		/// <summary>
		/// Compresses data with the zlib format, as written by FlateDecode encoders.
		/// </summary>
		/// <param name="data">The data.</param>
		/// <returns>The zlib stream.</returns>
		public static byte[] Deflate(byte[] data)
		{
			using (var output = new MemoryStream())
			{
				using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
					zlib.Write(data, 0, data.Length);

				return output.ToArray();
			}
		}

		#endregion

		#region Implementation

		// records a version of an object in the current revision.
		private void Store(int number, ObjectVersion version)
		{
			version.Revision = _revision;
			if (!_objects.TryGetValue(number, out var versions))
			{
				_objects[number] = new List<ObjectVersion> { version };
				return;
			}

			// replace the version of the current revision, or append one when the content changed.
			var last = versions[versions.Count - 1];
			if (last.Revision == _revision)
				versions[versions.Count - 1] = version;
			else if (!last.HasSameContent(version))
				versions.Add(version);
		}

		// generates the catalog, page tree, pages and outline from the current state.
		private void Materialize()
		{
			var kids = new StringBuilder();
			foreach (var page in _pages)
			{
				page.Materialize(_pageTree);
				kids.Append(page.Reference).Append(' ');
			}

			SetObject(_pageTree, $"<< /Type /Pages /Kids [{kids.ToString().Trim()}] /Count {_pages.Count} >>");

			var catalog = new StringBuilder($"<< /Type /Catalog /Pages {PdfText.Reference(_pageTree)}");
			if (_outlines.Count > 0)
			{
				MaterializeOutline(_outlineRoot, null, _outlines);
				catalog.Append(" /Outlines ").Append(PdfText.Reference(_outlineRoot));
			}

			foreach (var entry in _catalogEntries)
				catalog.Append(' ').Append(entry);

			SetObject(_catalog, catalog.Append(" >>").ToString());
		}

		// writes the outline items of one level, and the node that holds them.
		private int MaterializeOutline(int parentNumber, PdfOutlineItem parent, List<PdfOutlineItem> items)
		{
			var count = items.Count;
			for (int i = 0; i < items.Count; i++)
			{
				var item = items[i];
				var sb = new StringBuilder("<< /Title ").Append(PdfText.TextString(item.Title));
				sb.Append(" /Parent ").Append(PdfText.Reference(parentNumber));
				if (i > 0)
					sb.Append(" /Prev ").Append(PdfText.Reference(items[i - 1].Number));
				if (i + 1 < items.Count)
					sb.Append(" /Next ").Append(PdfText.Reference(items[i + 1].Number));

				if (item.Children.Count > 0)
				{
					var children = MaterializeOutline(item.Number, item, item.Children);
					count += children;
					sb.Append(" /First ").Append(PdfText.Reference(item.Children[0].Number));
					sb.Append(" /Last ").Append(PdfText.Reference(item.Children[item.Children.Count - 1].Number));
					sb.Append(" /Count ").Append(children);
				}

				sb.Append(" /Dest [").Append(item.Page.Reference).Append(" /XYZ 0 ").Append(PdfText.Number(item.Page.Height)).Append(" 0]");
				SetObject(item.Number, sb.Append(" >>").ToString());
			}

			if (parent == null)
			{
				SetObject(parentNumber, $"<< /Type /Outlines /First {PdfText.Reference(items[0].Number)} /Last {PdfText.Reference(items[items.Count - 1].Number)} /Count {count} >>");
			}

			return count;
		}

		// the objects introduced by a revision, by object number.
		private List<WrittenObject> GetSection(int revision)
		{
			var section = new List<WrittenObject>();
			foreach (var entry in _objects)
			{
				foreach (var version in entry.Value)
				{
					if (version.Revision == revision)
						section.Add(new WrittenObject { Number = entry.Key, Version = version });
				}
			}

			return section;
		}

		// builds the trailer entries other than /Size: /Root, /Info, /Encrypt, /Prev and /ID.
		private string BuildTrailer(PdfWriteOptions options, long previousXref, int encryptNumber)
		{
			var sb = new StringBuilder();
			if (!options.OmitRoot)
				sb.Append(" /Root ").Append(PdfText.Reference(_catalog));
			if (_info != 0)
				sb.Append(" /Info ").Append(PdfText.Reference(_info));
			if (encryptNumber != 0)
				sb.Append(" /Encrypt ").Append(PdfText.Reference(encryptNumber));
			if (previousXref >= 0)
				sb.Append(" /Prev ").Append(previousXref.ToString(CultureInfo.InvariantCulture));

			var id = PdfText.Hex(DocumentId);
			sb.Append(" /ID [").Append(id).Append(' ').Append(id).Append(']');
			return sb.ToString();
		}

		// writes an indirect object, encrypting its strings and stream data when a security handler is given.
		private static void WriteObject(Stream output, WrittenObject item, PdfSecurityHandler security)
		{
			var version = item.Version;
			Write(output, item.Number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");

			var body = security != null ? security.EncryptStrings(item.Number, version.Body) : version.Body;
			if (version.Data == null)
			{
				Write(output, body + "\nendobj\n");
				return;
			}

			// cross-reference streams are never encrypted.
			var data = security != null && !body.Contains("/Type /XRef") ? security.Encrypt(item.Number, version.Data) : version.Data;
			var length = version.DeclaredLength ?? data.Length;
			Write(output, "<< " + body + " /Length " + length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
			output.Write(data, 0, data.Length);
			Write(output, "\nendstream\nendobj\n");
		}

		// moves the objects that are not streams into a new object stream.
		private static List<WrittenObject> PackObjectStream(List<WrittenObject> section, int streamNumber, Dictionary<int, (int Stream, int Index)> compressed)
		{
			var header = new StringBuilder();
			var bodies = new StringBuilder();
			var remaining = new List<WrittenObject>();
			var index = 0;
			foreach (var item in section)
			{
				if (item.Version.Data != null)
				{
					remaining.Add(item);
					continue;
				}

				header.Append(item.Number).Append(' ').Append(bodies.Length).Append(' ');
				bodies.Append(item.Version.Body).Append('\n');
				compressed[item.Number] = (streamNumber, index++);
			}

			var first = header.Length;
			var data = PdfText.ToBytes(header.ToString() + bodies);
			var entries = $"/Type /ObjStm /N {index} /First {first} /Filter /FlateDecode";
			remaining.Add(new WrittenObject { Number = streamNumber, Version = new ObjectVersion { Body = entries, Data = Deflate(data) } });
			return remaining;
		}

		// writes a classic cross-reference table followed by its trailer.
		private static void WriteXrefTable(Stream output, SortedDictionary<int, long> offsets, int size, string trailer, bool includeFreeHead)
		{
			var sb = new StringBuilder("xref\n");
			foreach (var (first, numbers) in GetSubsections(offsets, includeFreeHead))
			{
				sb.Append(first).Append(' ').Append(numbers.Count).Append('\n');
				foreach (var number in numbers)
				{
					// each entry is exactly 20 bytes, including the two-character end of line.
					if (number == 0)
						sb.Append("0000000000 65535 f\r\n");
					else
						sb.Append(offsets[number].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n\r\n");
				}
			}

			sb.Append("trailer\n<< /Size ").Append(size).Append(trailer).Append(" >>\n");
			Write(output, sb.ToString());
		}

		// writes a cross-reference stream whose dictionary also carries the trailer entries.
		private static void WriteXrefStream(Stream output, int number, int size, SortedDictionary<int, long> offsets, Dictionary<int, (int Stream, int Index)> compressed, string trailer, bool includeFreeHead)
		{
			var all = new SortedDictionary<int, long>(offsets);
			foreach (var entry in compressed)
				all[entry.Key] = 0;

			// rows of W [1 4 2]: type, offset or object stream number, generation or index.
			var rows = new MemoryStream();
			var index = new StringBuilder();
			foreach (var (first, numbers) in GetSubsections(all, includeFreeHead))
			{
				index.Append(first).Append(' ').Append(numbers.Count).Append(' ');
				foreach (var n in numbers)
				{
					if (n == 0)
						WriteRow(rows, 0, 0, 65535);
					else if (compressed.TryGetValue(n, out var location))
						WriteRow(rows, 2, location.Stream, location.Index);
					else
						WriteRow(rows, 1, offsets[n], 0);
				}
			}

			var data = Deflate(rows.ToArray());
			var entries = $"/Type /XRef /Size {size} /W [1 4 2] /Index [{index.ToString().Trim()}]{trailer} /Filter /FlateDecode";
			Write(output, number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n<< " + entries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
			output.Write(data, 0, data.Length);
			Write(output, "\nendstream\nendobj\n");
		}

		// writes one cross-reference stream row with field widths 1, 4 and 2, big-endian.
		private static void WriteRow(Stream rows, int type, long field2, int field3)
		{
			rows.WriteByte((byte)type);
			for (int shift = 24; shift >= 0; shift -= 8)
				rows.WriteByte((byte)(field2 >> shift));

			rows.WriteByte((byte)(field3 >> 8));
			rows.WriteByte((byte)field3);
		}

		// groups object numbers into runs of consecutive numbers.
		private static List<(int First, List<int> Numbers)> GetSubsections(SortedDictionary<int, long> offsets, bool includeFreeHead)
		{
			var numbers = new List<int>(offsets.Keys);
			if (includeFreeHead)
				numbers.Insert(0, 0);

			var result = new List<(int, List<int>)>();
			foreach (var number in numbers)
			{
				if (result.Count > 0)
				{
					var (first, run) = result[result.Count - 1];
					if (first + run.Count == number)
					{
						run.Add(number);
						continue;
					}
				}

				result.Add((number, new List<int> { number }));
			}

			return result;
		}

		// writes a string to the output as PDF bytes.
		private static void Write(Stream output, string text)
		{
			var bytes = PdfText.ToBytes(text);
			output.Write(bytes, 0, bytes.Length);
		}

		#endregion
	}
}
