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

// Result of decoding a DCTDecode (JPEG) stream (ISO 32000-2, 7.4.8).

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// The decoded samples of a JPEG image, with the colour information the PDF image module needs.
	/// </summary>
	internal sealed class JpegImage
	{
		/// <summary>
		/// The width of the image in pixels.
		/// </summary>
		public int Width;

		/// <summary>
		/// The height of the image in pixels.
		/// </summary>
		public int Height;

		/// <summary>
		/// The number of interleaved samples per pixel: 1 (gray), 3 (RGB), 4 (CMYK) or, in unusual files, any other value.
		/// </summary>
		public int Components;

		/// <summary>
		/// The interleaved 8-bit samples, row-major, without row padding (<see cref="Width"/> * <see cref="Components"/> bytes per row).
		/// </summary>
		public byte[] Data;

		/// <summary>
		/// True when the stream contains an Adobe APP14 marker. Adobe applications write CMYK JPEG data inverted,
		/// so the caller uses this flag to decide whether to invert four-component samples.
		/// </summary>
		public bool HasAdobeMarker;

		/// <summary>
		/// The transform flag of the Adobe APP14 marker (0 = none, 1 = YCbCr, 2 = YCCK), or -1 when the marker is absent.
		/// </summary>
		public int AdobeTransform = -1;

		/// <summary>
		/// False when the data was truncated or corrupt; the area that could not be decoded is filled with neutral samples.
		/// </summary>
		public bool IsComplete;
	}
}
