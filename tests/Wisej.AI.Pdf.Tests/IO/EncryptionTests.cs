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

// Tests of the standard security handler (ISO 32000-2, 7.6.4): RC4-128 and
// AES-128 documents with user and owner passwords.

using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.IO
{
	/// <summary>
	/// Tests of encrypted documents.
	/// </summary>
	public class EncryptionTests
	{
		/// <summary>A document encrypted with an empty user password opens without a password; strings and streams are decrypted.</summary>
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void EmptyUserPassword_OpensAndDecrypts(bool aes)
		{
			var bytes = CreateDocument().ToArray(Options(aes, string.Empty));

			// the clear text must not appear in the file.
			Assert.DoesNotContain("Confidential", PdfText.FromBytes(bytes));

			using (var document = PdfDocument.Open(bytes))
			{
				Assert.True(document.IsEncrypted);
				Assert.Equal("Secret title", document.Info.Title);
				Assert.Contains("Confidential paragraph", document.ExtractText());
			}
		}

		/// <summary>The user password opens a protected document.</summary>
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void UserPassword_Opens(bool aes)
		{
			var bytes = CreateDocument().ToArray(Options(aes, "user"));

			using (var document = PdfDocument.Open(bytes, "user"))
				Assert.Contains("Confidential paragraph", document.ExtractText());
		}

		/// <summary>The owner password also opens a protected document (Algorithm 7).</summary>
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void OwnerPassword_Opens(bool aes)
		{
			var bytes = CreateDocument().ToArray(Options(aes, "user"));

			using (var document = PdfDocument.Open(bytes, "owner"))
				Assert.Equal("Secret title", document.Info.Title);
		}

		/// <summary>A missing or wrong password throws <see cref="PdfPasswordException"/>.</summary>
		[Theory]
		[InlineData(null)]
		[InlineData("wrong")]
		public void WrongPassword_Throws(string password)
		{
			var bytes = CreateDocument().ToArray(Options(true, "user"));

			Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes, password));
		}

		/// <summary>An encrypted file with a damaged cross-reference table is repaired and decrypted.</summary>
		[Fact]
		public void EncryptedAndCorrupt_IsRepairedAndDecrypted()
		{
			var options = Options(false, string.Empty);
			options.Xref = XrefStyle.Corrupt;

			using (var document = PdfDocument.Open(CreateDocument().ToArray(options)))
			{
				Assert.True(document.WasRepaired);
				Assert.Contains("Confidential paragraph", document.ExtractText());
			}
		}

		/// <summary>
		/// Creates a document with a title and a line of text.
		/// </summary>
		/// <returns>The document builder.</returns>
		private static PdfBuilder CreateDocument()
		{
			var builder = new PdfBuilder();
			builder.SetInfo(("Title", "Secret title"));
			builder.AddPage().Text(builder.Font("Helvetica"), 12, 72, 700, "Confidential paragraph");
			return builder;
		}

		/// <summary>
		/// Returns write options that encrypt the document.
		/// </summary>
		/// <param name="aes">True for AES-128, false for RC4-128.</param>
		/// <param name="userPassword">The user password.</param>
		/// <returns>The options.</returns>
		private static PdfWriteOptions Options(bool aes, string userPassword)
		{
			return new PdfWriteOptions
			{
				Encryption = new PdfEncryption { Cipher = aes ? PdfCipher.Aes128 : PdfCipher.Rc4, UserPassword = userPassword, OwnerPassword = "owner" },
			};
		}
	}
}
