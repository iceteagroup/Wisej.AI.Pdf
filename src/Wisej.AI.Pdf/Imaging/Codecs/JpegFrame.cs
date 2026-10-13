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

// JPEG frame, component and scan state (ITU-T T.81, A.1.1, B.2.2 and B.2.3).

using System;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// One image component of a JPEG frame, with its decoded coefficients or samples.
	/// </summary>
	internal sealed class JpegComponent
	{
		#region Fields

		/// <summary>
		/// The component identifier (Ci).
		/// </summary>
		public int Id;

		/// <summary>
		/// The horizontal sampling factor (Hi), 1-4.
		/// </summary>
		public int H;

		/// <summary>
		/// The vertical sampling factor (Vi), 1-4.
		/// </summary>
		public int V;

		/// <summary>
		/// The index of the quantization table (Tqi).
		/// </summary>
		public int QuantIndex;

		/// <summary>
		/// The quantization table latched when the component first appeared in a scan (natural order).
		/// </summary>
		public ushort[] Quant;

		/// <summary>
		/// The width of the component in samples: ceil(X * Hi / Hmax).
		/// </summary>
		public int Width;

		/// <summary>
		/// The height of the component in samples: ceil(Y * Vi / Vmax).
		/// </summary>
		public int Height;

		/// <summary>
		/// The number of 8x8 blocks that cover <see cref="Width"/> (samples in lossless mode).
		/// </summary>
		public int BlocksPerLine;

		/// <summary>
		/// The number of 8x8 blocks that cover <see cref="Height"/> (samples in lossless mode).
		/// </summary>
		public int BlocksPerColumn;

		/// <summary>
		/// The number of blocks per line in storage, padded to whole MCUs.
		/// </summary>
		public int BlocksPerLineAlloc;

		/// <summary>
		/// The number of block rows in storage, padded to whole MCUs.
		/// </summary>
		public int BlocksPerColumnAlloc;

		/// <summary>
		/// The quantized DCT coefficients of all the blocks, 64 per block in natural order.
		/// </summary>
		public short[] Coefficients;

		/// <summary>
		/// The decoded samples of a lossless frame.
		/// </summary>
		public int[] Samples;

		/// <summary>
		/// The decoded 8-bit plane, filled when the image is rendered.
		/// </summary>
		public byte[] Plane;

		/// <summary>
		/// The number of bytes per row of <see cref="Plane"/>.
		/// </summary>
		public int PlaneStride;

		/// <summary>
		/// True once a scan covering every block of this component completed without truncation.
		/// </summary>
		public bool IsCovered;

		/// <summary>
		/// True when the component took part in at least one scan.
		/// </summary>
		public bool IsScanned;

		/// <summary>
		/// The DC Huffman table of the current scan.
		/// </summary>
		public JpegHuffmanTable DcTable;

		/// <summary>
		/// The AC Huffman table of the current scan.
		/// </summary>
		public JpegHuffmanTable AcTable;

		/// <summary>
		/// The DC table index of the current scan (used by the arithmetic decoder).
		/// </summary>
		public int DcTableIndex;

		/// <summary>
		/// The AC table index of the current scan (used by the arithmetic decoder).
		/// </summary>
		public int AcTableIndex;

		/// <summary>
		/// The DC predictor of the current scan.
		/// </summary>
		public int DcPredictor;

		/// <summary>
		/// The arithmetic DC conditioning context of the current scan.
		/// </summary>
		public int DcContext;

		/// <summary>
		/// The first sample row of the current restart interval (lossless prediction).
		/// </summary>
		public int IntervalRow;

		#endregion
	}

	/// <summary>
	/// The parameters of one scan (SOS segment).
	/// </summary>
	internal sealed class JpegScan
	{
		/// <summary>
		/// The components in the scan, in scan order.
		/// </summary>
		public JpegComponent[] Components;

		/// <summary>
		/// Start of spectral selection, or the predictor in lossless mode.
		/// </summary>
		public int Ss;

		/// <summary>
		/// End of spectral selection.
		/// </summary>
		public int Se;

		/// <summary>
		/// Successive approximation bit position high.
		/// </summary>
		public int Ah;

		/// <summary>
		/// Successive approximation bit position low, or the point transform in lossless mode.
		/// </summary>
		public int Al;
	}

	/// <summary>
	/// The frame header (SOF segment) of a JPEG image and its components.
	/// </summary>
	internal sealed class JpegFrame
	{
		#region Fields

		/// <summary>
		/// Upper bound of the number of samples a frame may hold, to protect against decompression bombs.
		/// </summary>
		public const long MaxSamples = 512L * 1024 * 1024;

		/// <summary>
		/// The SOF marker code (0xC0-0xCF).
		/// </summary>
		public int Marker;

		/// <summary>
		/// True for progressive frames (SOF2, SOF6, SOF10, SOF14).
		/// </summary>
		public bool IsProgressive;

		/// <summary>
		/// True for arithmetic-coded frames (SOF9-SOF15).
		/// </summary>
		public bool IsArithmetic;

		/// <summary>
		/// True for lossless frames (SOF3, SOF7, SOF11, SOF15).
		/// </summary>
		public bool IsLossless;

		/// <summary>
		/// The sample precision in bits (P).
		/// </summary>
		public int Precision;

		/// <summary>
		/// The number of lines (Y); 0 until a DNL marker defines it.
		/// </summary>
		public int Height;

		/// <summary>
		/// The number of samples per line (X).
		/// </summary>
		public int Width;

		/// <summary>
		/// The frame components.
		/// </summary>
		public JpegComponent[] Components;

		/// <summary>
		/// The largest horizontal sampling factor.
		/// </summary>
		public int MaxH;

		/// <summary>
		/// The largest vertical sampling factor.
		/// </summary>
		public int MaxV;

		/// <summary>
		/// The number of MCUs per line in interleaved scans.
		/// </summary>
		public int McusPerLine;

		/// <summary>
		/// The number of MCU rows in interleaved scans.
		/// </summary>
		public int McusPerColumn;

		/// <summary>
		/// True after <see cref="Allocate"/> succeeded.
		/// </summary>
		public bool IsAllocated;

		/// <summary>
		/// False when any scan was truncated or damaged.
		/// </summary>
		public bool IsComplete = true;

		/// <summary>
		/// The number of image rows decoded before a sequential scan was truncated; <see cref="int.MaxValue"/> when nothing was lost.
		/// </summary>
		public int ValidRows = int.MaxValue;

		#endregion

		#region Methods

		/// <summary>
		/// Computes the component geometry and allocates the coefficient (or sample) buffers.
		/// </summary>
		/// <returns>True on success; false when the geometry is invalid or too large.</returns>
		public bool Allocate()
		{
			if (this.IsAllocated)
				return true;

			if (this.Width <= 0 || this.Height <= 0 || this.Components == null || this.Components.Length == 0)
				return false;

			if ((long)this.Width * this.Height * this.Components.Length > MaxSamples)
				return false;

			this.MaxH = 1;
			this.MaxV = 1;
			foreach (var component in this.Components)
			{
				this.MaxH = Math.Max(this.MaxH, component.H);
				this.MaxV = Math.Max(this.MaxV, component.V);
			}

			// a lossless "block" is a single sample.
			var blockSize = this.IsLossless ? 1 : 8;
			this.McusPerLine = CeilDiv(this.Width, blockSize * this.MaxH);
			this.McusPerColumn = CeilDiv(this.Height, blockSize * this.MaxV);

			long total = 0;
			foreach (var component in this.Components)
			{
				component.Width = CeilDiv(this.Width * component.H, this.MaxH);
				component.Height = CeilDiv(this.Height * component.V, this.MaxV);
				component.BlocksPerLine = CeilDiv(component.Width, blockSize);
				component.BlocksPerColumn = CeilDiv(component.Height, blockSize);
				component.BlocksPerLineAlloc = this.McusPerLine * component.H;
				component.BlocksPerColumnAlloc = this.McusPerColumn * component.V;

				var units = (long)component.BlocksPerLineAlloc * component.BlocksPerColumnAlloc * blockSize * blockSize;
				total += units;
				if (units > int.MaxValue || total > MaxSamples * 2)
					return false;
			}

			foreach (var component in this.Components)
			{
				var units = component.BlocksPerLineAlloc * component.BlocksPerColumnAlloc * blockSize * blockSize;
				if (this.IsLossless)
					component.Samples = new int[units];
				else
					component.Coefficients = new short[units];
			}

			this.IsAllocated = true;
			return true;
		}

		/// <summary>
		/// Returns the component with the specified identifier.
		/// </summary>
		/// <param name="id">The component identifier.</param>
		/// <returns>The component, or null.</returns>
		public JpegComponent FindComponent(int id)
		{
			foreach (var component in this.Components)
			{
				if (component.Id == id)
					return component;
			}

			return null;
		}

		/// <summary>
		/// Divides rounding up.
		/// </summary>
		/// <param name="value">The dividend (non-negative).</param>
		/// <param name="divisor">The divisor (positive).</param>
		/// <returns>ceil(value / divisor).</returns>
		public static int CeilDiv(int value, int divisor)
		{
			return (int)(((long)value + divisor - 1) / divisor);
		}

		#endregion
	}
}
