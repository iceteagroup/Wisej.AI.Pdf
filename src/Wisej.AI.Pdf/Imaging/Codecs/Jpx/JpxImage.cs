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

// Decoded JPEG 2000 image returned by the JPXDecode filter (ITU-T T.800, ISO 32000-2, 7.4.9).

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Colour space declared by a JP2 colour specification box (ITU-T T.800, I.5.3.3).
	/// </summary>
	internal enum JpxColorSpace
	{
		/// <summary>No colour specification: raw codestreams or unrecognized enumerated values.</summary>
		Unknown,

		/// <summary>Greyscale (enumerated colour space 17).</summary>
		Gray,

		/// <summary>sRGB (enumerated colour space 16), or sYCC (18) after conversion to RGB.</summary>
		Rgb,

		/// <summary>A YCbCr colour space other than sYCC (enumerated colour spaces 1, 3, 4 and 9); samples are not converted.</summary>
		Ycc,

		/// <summary>CMYK (enumerated colour space 12).</summary>
		Cmyk,

		/// <summary>CIE L*a*b* (enumerated colour space 14).</summary>
		Lab,

		/// <summary>The colour space is described by the ICC profile in <see cref="JpxImage.IccProfile"/>.</summary>
		Icc,
	}

	/// <summary>
	/// The result of decoding a JPEG 2000 codestream or JP2 file.
	/// </summary>
	internal sealed class JpxImage
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
		/// The number of interleaved channels in <see cref="Data"/>, after palette expansion and including alpha.
		/// </summary>
		public int Components;

		/// <summary>
		/// The interleaved 8-bit samples, <see cref="Width"/> × <see cref="Height"/> × <see cref="Components"/> bytes.
		/// </summary>
		/// <remarks>
		/// Higher bit depths are scaled to 8 bits and signed samples are offset to the unsigned range.
		/// </remarks>
		public byte[] Data;

		/// <summary>
		/// The colour space declared by the colour specification box.
		/// </summary>
		public JpxColorSpace ColorSpace;

		/// <summary>
		/// The embedded ICC profile, or null.
		/// </summary>
		public byte[] IccProfile;

		/// <summary>
		/// The index of the opacity channel declared by the channel definition box, or -1 when there is none.
		/// </summary>
		public int AlphaChannel = -1;

		/// <summary>
		/// True when the opacity channel is premultiplied (channel definition type 2).
		/// </summary>
		public bool AlphaPremultiplied;

		/// <summary>
		/// The original precision in bits of each output channel.
		/// </summary>
		public int[] BitDepths;

		/// <summary>
		/// False when the codestream was truncated or corrupt and only part of it could be decoded.
		/// </summary>
		public bool IsComplete;
	}
}
