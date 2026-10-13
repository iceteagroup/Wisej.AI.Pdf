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

using System;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// The exception thrown when a PDF document cannot be read.
	/// </summary>
	/// <remarks>
	/// Most structural problems in a PDF file are repaired silently. This exception is only
	/// thrown when the data is not a PDF document, or when it is encrypted and the password is wrong.
	/// </remarks>
	public class PdfException : Exception
	{
		/// <summary>
		/// Initializes a new instance of <see cref="PdfException"/>.
		/// </summary>
		/// <param name="message">The message that describes the error.</param>
		public PdfException(string message) : base(message) { }

		/// <summary>
		/// Initializes a new instance of <see cref="PdfException"/>.
		/// </summary>
		/// <param name="message">The message that describes the error.</param>
		/// <param name="innerException">The exception that caused this exception.</param>
		public PdfException(string message, Exception innerException) : base(message, innerException) { }
	}

	/// <summary>
	/// The exception thrown when a PDF document is encrypted and the supplied password is wrong or missing.
	/// </summary>
	public class PdfPasswordException : PdfException
	{
		/// <summary>
		/// Initializes a new instance of <see cref="PdfPasswordException"/>.
		/// </summary>
		/// <param name="message">The message that describes the error.</param>
		public PdfPasswordException(string message) : base(message) { }
	}
}
