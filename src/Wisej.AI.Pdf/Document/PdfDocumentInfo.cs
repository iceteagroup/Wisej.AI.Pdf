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

// Document information dictionary and XMP metadata.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// Describes a PDF document: title, author, subject, keywords, producer and dates.
	/// </summary>
	/// <remarks>
	/// Values are read from the document information dictionary. When a value is missing there,
	/// it is taken from the XMP metadata stream, which modern producers sometimes fill exclusively.
	/// </remarks>
	public sealed class PdfDocumentInfo
	{
		/// <summary>
		/// Prevents external instantiation: instances are created by <see cref="Load"/>.
		/// </summary>
		private PdfDocumentInfo()
		{
		}

		#region Properties

		/// <summary>
		/// The title of the document.
		/// </summary>
		public string Title { get; private set; }

		/// <summary>
		/// The name of the person who created the document.
		/// </summary>
		public string Author { get; private set; }

		/// <summary>
		/// The subject of the document.
		/// </summary>
		public string Subject { get; private set; }

		/// <summary>
		/// Keywords associated with the document.
		/// </summary>
		public string Keywords { get; private set; }

		/// <summary>
		/// The application that created the original document.
		/// </summary>
		public string Creator { get; private set; }

		/// <summary>
		/// The application that converted the document to PDF.
		/// </summary>
		public string Producer { get; private set; }

		/// <summary>
		/// The date and time the document was created, or null.
		/// </summary>
		public DateTimeOffset? CreationDate { get; private set; }

		/// <summary>
		/// The date and time the document was last modified, or null.
		/// </summary>
		public DateTimeOffset? ModificationDate { get; private set; }

		/// <summary>
		/// All the entries of the information dictionary, including custom ones.
		/// </summary>
		public IReadOnlyDictionary<string, string> Entries { get; private set; }

		/// <summary>
		/// The raw XMP metadata packet, or null when the document has none.
		/// </summary>
		public string Xmp { get; private set; }

		#endregion

		#region Loading

		/// <summary>
		/// Reads the document information of <paramref name="document"/>.
		/// </summary>
		/// <param name="document">The document.</param>
		/// <returns>The document information; never null.</returns>
		internal static PdfDocumentInfo Load(PdfDocument document)
		{
			var info = new PdfDocumentInfo();
			var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			var dictionary = document.File.Trailer?.GetDictionary(PdfNames.Info);
			if (dictionary != null)
			{
				foreach (var key in dictionary.Keys)
				{
					var value = dictionary.Resolve(key);
					var text = value is PdfString s ? s.ToText() : value is PdfName n ? n.Value : null;
					if (!string.IsNullOrWhiteSpace(text))
						entries[key.Value] = text.Trim();
				}
			}

			info.Entries = entries;
			info.Title = Get(entries, "Title");
			info.Author = Get(entries, "Author");
			info.Subject = Get(entries, "Subject");
			info.Keywords = Get(entries, "Keywords");
			info.Creator = Get(entries, "Creator");
			info.Producer = Get(entries, "Producer");
			info.CreationDate = ParseDate(Get(entries, "CreationDate"));
			info.ModificationDate = ParseDate(Get(entries, "ModDate"));

			// XMP metadata fills the gaps.
			try
			{
				var metadata = document.Catalog.GetStream(PdfNames.Metadata);
				if (metadata != null)
				{
					info.Xmp = Encoding.UTF8.GetString(metadata.GetBytes()).Trim('\0', ' ', '\r', '\n');
					info.ApplyXmp(info.Xmp);
				}
			}
			catch (Exception)
			{
				// invalid XMP is ignored.
			}

			return info;
		}

		/// <summary>
		/// Returns the entry <paramref name="key"/> of the information dictionary.
		/// </summary>
		/// <param name="entries">The entries of the information dictionary.</param>
		/// <param name="key">The entry name.</param>
		/// <returns>The value, or null when missing.</returns>
		private static string Get(Dictionary<string, string> entries, string key) => entries.TryGetValue(key, out var value) ? value : null;

		/// <summary>
		/// Fills the properties still missing from the information dictionary with the
		/// Dublin Core, PDF and XMP basic properties of the XMP packet.
		/// </summary>
		/// <param name="xmp">The XMP packet; null or empty is ignored.</param>
		/// <exception cref="XmlException">The packet is not well-formed XML; the caller ignores it.</exception>
		private void ApplyXmp(string xmp)
		{
			if (string.IsNullOrEmpty(xmp))
				return;

			var xml = new XmlDocument { XmlResolver = null };
			using (var reader = XmlReader.Create(new System.IO.StringReader(xmp), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
				xml.Load(reader);

			var ns = new XmlNamespaceManager(xml.NameTable);
			ns.AddNamespace("dc", "http://purl.org/dc/elements/1.1/");
			ns.AddNamespace("pdf", "http://ns.adobe.com/pdf/1.3/");
			ns.AddNamespace("xmp", "http://ns.adobe.com/xap/1.0/");
			ns.AddNamespace("rdf", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");

			string Text(string xpath)
			{
				var node = xml.SelectSingleNode(xpath, ns);
				var value = node?.InnerText?.Trim();
				return string.IsNullOrEmpty(value) ? null : value;
			}

			this.Title = this.Title ?? Text("//dc:title//rdf:li") ?? Text("//dc:title");
			this.Author = this.Author ?? Text("//dc:creator//rdf:li") ?? Text("//dc:creator");
			this.Subject = this.Subject ?? Text("//dc:description//rdf:li");
			this.Keywords = this.Keywords ?? Text("//pdf:Keywords") ?? Text("//@pdf:Keywords");
			this.Producer = this.Producer ?? Text("//pdf:Producer") ?? Text("//@pdf:Producer");
			this.Creator = this.Creator ?? Text("//xmp:CreatorTool") ?? Text("//@xmp:CreatorTool");

			if (this.CreationDate == null && DateTimeOffset.TryParse(Text("//xmp:CreateDate") ?? Text("//@xmp:CreateDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
				this.CreationDate = created;

			if (this.ModificationDate == null && DateTimeOffset.TryParse(Text("//xmp:ModifyDate") ?? Text("//@xmp:ModifyDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var modified))
				this.ModificationDate = modified;
		}

		/// <summary>
		/// Parses a PDF date string: <c>D:YYYYMMDDHHmmSSOHH'mm'</c>, where all the parts after the year are optional.
		/// </summary>
		/// <param name="value">The date string.</param>
		/// <returns>The date, or null when the string is not a valid date.</returns>
		internal static DateTimeOffset? ParseDate(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return null;

			value = value.Trim();
			if (value.StartsWith("D:", StringComparison.Ordinal))
				value = value.Substring(2);

			var p = 0;
			int Read(int digits, int defaultValue)
			{
				if (p + digits > value.Length)
					return defaultValue;

				var result = 0;
				for (int i = 0; i < digits; i++)
				{
					var c = value[p + i];
					if (c < '0' || c > '9')
						return defaultValue;

					result = result * 10 + (c - '0');
				}

				p += digits;
				return result;
			}

			var year = Read(4, -1);
			if (year < 1)
				return null;

			var month = Read(2, 1);
			var day = Read(2, 1);
			var hour = Read(2, 0);
			var minute = Read(2, 0);
			var second = Read(2, 0);

			var offset = TimeSpan.Zero;
			if (p < value.Length && (value[p] == '+' || value[p] == '-'))
			{
				var sign = value[p] == '-' ? -1 : 1;
				p++;

				var offsetHours = Read(2, 0);
				if (p < value.Length && value[p] == '\'')
					p++;

				var offsetMinutes = Read(2, 0);
				offset = new TimeSpan(sign * offsetHours, sign * offsetMinutes, 0);
			}

			try
			{
				return new DateTimeOffset(year, Clamp(month, 1, 12), Clamp(day, 1, DateTime.DaysInMonth(year, Clamp(month, 1, 12))),
					Clamp(hour, 0, 23), Clamp(minute, 0, 59), Clamp(second, 0, 59), offset);
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		/// <summary>
		/// Clamps <paramref name="value"/> to the interval [<paramref name="min"/>, <paramref name="max"/>].
		/// </summary>
		/// <param name="value">The value.</param>
		/// <param name="min">The minimum.</param>
		/// <param name="max">The maximum.</param>
		/// <returns>The clamped value.</returns>
		private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

		#endregion
	}
}
