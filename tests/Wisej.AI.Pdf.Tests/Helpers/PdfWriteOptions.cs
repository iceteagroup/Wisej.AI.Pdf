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

// Test helper: options that control how PdfBuilder serializes a document,
// including deliberate damage used by the repair tests.

namespace Wisej.AI.Pdf.Tests.Helpers
{
	/// <summary>
	/// The form of the cross-reference data written by <see cref="PdfBuilder"/>.
	/// </summary>
	internal enum XrefStyle
	{
		/// <summary>A classic cross-reference table with a trailer dictionary (ISO 32000-2, 7.5.4).</summary>
		Table,

		/// <summary>A cross-reference stream (ISO 32000-2, 7.5.8).</summary>
		Stream,

		/// <summary>A table whose offsets and <c>startxref</c> are wrong: the reader must reconstruct the file.</summary>
		Corrupt,

		/// <summary>No cross-reference data and no trailer at all.</summary>
		None,
	}

	/// <summary>
	/// The standard security handler cipher used by <see cref="PdfEncryption"/>.
	/// </summary>
	internal enum PdfCipher
	{
		/// <summary>RC4 with a 128-bit key: /V 2 /R 3.</summary>
		Rc4,

		/// <summary>AES-128 in CBC mode: /V 4 /R 4 with the AESV2 crypt filter.</summary>
		Aes128,
	}

	/// <summary>
	/// Standard security handler settings (ISO 32000-2, 7.6.4).
	/// </summary>
	internal sealed class PdfEncryption
	{
		/// <summary>
		/// The cipher. Default: <see cref="PdfCipher.Rc4"/>.
		/// </summary>
		public PdfCipher Cipher { get; set; } = PdfCipher.Rc4;

		/// <summary>
		/// The user password; empty to open the document without a password.
		/// </summary>
		public string UserPassword { get; set; } = string.Empty;

		/// <summary>
		/// The owner password; empty to use the user password.
		/// </summary>
		public string OwnerPassword { get; set; } = "owner";

		/// <summary>
		/// The permission flags (/P). Default: everything allowed.
		/// </summary>
		public int Permissions { get; set; } = -4;
	}

	/// <summary>
	/// Options of <see cref="PdfBuilder.ToArray(PdfWriteOptions)"/>.
	/// </summary>
	internal sealed class PdfWriteOptions
	{
		/// <summary>
		/// The form of the cross-reference data. Default: <see cref="XrefStyle.Table"/>.
		/// </summary>
		public XrefStyle Xref { get; set; } = XrefStyle.Table;

		/// <summary>
		/// Store the non-stream objects in an object stream (requires <see cref="XrefStyle.Stream"/>).
		/// </summary>
		public bool UseObjectStreams { get; set; }

		/// <summary>
		/// Leave /Root out of the trailer, so the reader has to find the catalog by scanning.
		/// </summary>
		public bool OmitRoot { get; set; }

		/// <summary>
		/// Encrypt the document; null for none.
		/// </summary>
		public PdfEncryption Encryption { get; set; }

		/// <summary>
		/// Bytes written before the "%PDF-" header, as some broken producers do.
		/// </summary>
		public string Preamble { get; set; }
	}
}
