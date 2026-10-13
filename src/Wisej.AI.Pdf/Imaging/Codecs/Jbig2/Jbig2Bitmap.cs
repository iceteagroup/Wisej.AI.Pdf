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

// Packed 1-bit bitmap used by the JBIG2 decoder, with the combination operators (ITU T.88, 6.1 and 7.4.1.5).

using System;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// A packed 1-bit bitmap in JBIG2's native polarity: a set bit is a black pixel.
	/// </summary>
	/// <remarks>
	/// Rows are stored top to bottom, <see cref="Stride"/> bytes each, with the leftmost pixel in the
	/// most significant bit. The padding bits after the last pixel of a row are always kept at zero,
	/// because the context-modelling code of the generic region decoder reads whole bytes and relies
	/// on pixels outside the bitmap being 0.
	/// </remarks>
	internal sealed class Jbig2Bitmap
	{
		#region Fields

		/// <summary>Combination operator OR (7.4.1.5).</summary>
		public const int OperatorOr = 0;

		/// <summary>Combination operator AND.</summary>
		public const int OperatorAnd = 1;

		/// <summary>Combination operator XOR.</summary>
		public const int OperatorXor = 2;

		/// <summary>Combination operator XNOR.</summary>
		public const int OperatorXnor = 3;

		/// <summary>Combination operator REPLACE.</summary>
		public const int OperatorReplace = 4;

		#endregion

		#region Constructors

		/// <summary>
		/// Creates a bitmap with all pixels cleared to 0 (white).
		/// </summary>
		/// <param name="width">The width in pixels; must not be negative.</param>
		/// <param name="height">The height in pixels; must not be negative.</param>
		public Jbig2Bitmap(int width, int height)
		{
			this.Width = width;
			this.Height = height;
			this.Stride = (width + 7) >> 3;
			this.Data = new byte[(long)this.Stride * height];
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the width in pixels.
		/// </summary>
		public int Width { get; }

		/// <summary>
		/// Returns the height in pixels. It changes only through <see cref="Resize"/>.
		/// </summary>
		public int Height { get; private set; }

		/// <summary>
		/// Returns the number of bytes per row.
		/// </summary>
		public int Stride { get; }

		/// <summary>
		/// Returns the packed pixel data.
		/// </summary>
		public byte[] Data { get; private set; }

		#endregion

		#region Methods

		/// <summary>
		/// Returns the pixel at the specified position, or 0 when the position is outside the bitmap.
		/// </summary>
		/// <param name="x">The column.</param>
		/// <param name="y">The row.</param>
		/// <returns>1 for a black pixel, 0 for a white one.</returns>
		public int GetPixel(int x, int y)
		{
			if ((uint)x >= (uint)this.Width || (uint)y >= (uint)this.Height)
				return 0;

			return (this.Data[y * this.Stride + (x >> 3)] >> (7 - (x & 7))) & 1;
		}

		/// <summary>
		/// Sets the pixel at the specified position to 1. Positions outside the bitmap are ignored.
		/// </summary>
		/// <param name="x">The column.</param>
		/// <param name="y">The row.</param>
		public void SetPixel(int x, int y)
		{
			if ((uint)x >= (uint)this.Width || (uint)y >= (uint)this.Height)
				return;

			this.Data[y * this.Stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
		}

		/// <summary>
		/// Sets every pixel to <paramref name="value"/>, leaving the row padding bits at zero.
		/// </summary>
		/// <param name="value">0 or 1.</param>
		public void Fill(int value)
		{
			Fill(0, this.Height, value);
		}

		/// <summary>
		/// Changes the height of the bitmap, keeping the existing rows and filling new ones with <paramref name="value"/>.
		/// </summary>
		/// <param name="height">The new height.</param>
		/// <param name="value">The value of the pixels in the added rows.</param>
		/// <remarks>Used for pages whose height is initially unknown (7.4.8.2).</remarks>
		public void Resize(int height, int value)
		{
			if (height == this.Height)
				return;

			var data = new byte[(long)this.Stride * height];
			Buffer.BlockCopy(this.Data, 0, data, 0, Math.Min(this.Data.Length, data.Length));

			var oldHeight = this.Height;
			this.Data = data;
			this.Height = height;

			if (height > oldHeight && value != 0)
				Fill(oldHeight, height, value);
		}

		/// <summary>
		/// Copies the rectangle at (<paramref name="x"/>, <paramref name="y"/>) into a new bitmap.
		/// </summary>
		/// <param name="x">The left edge of the rectangle; it may lie outside the bitmap.</param>
		/// <param name="y">The top edge of the rectangle; it may lie outside the bitmap.</param>
		/// <param name="width">The width of the rectangle.</param>
		/// <param name="height">The height of the rectangle.</param>
		/// <returns>The new bitmap. Pixels outside this bitmap are 0.</returns>
		public Jbig2Bitmap Extract(int x, int y, int width, int height)
		{
			var result = new Jbig2Bitmap(width, height);

			// compose the source onto the empty result shifted by (-x, -y): REPLACE copies the
			// overlapping part, and everything else stays white.
			result.Compose(this, -x, -y, OperatorReplace);
			return result;
		}

		/// <summary>
		/// Combines <paramref name="source"/> into this bitmap with its top-left corner at
		/// (<paramref name="x"/>, <paramref name="y"/>), clipping it to the bounds of this bitmap.
		/// </summary>
		/// <param name="source">The bitmap to combine.</param>
		/// <param name="x">The column of the left edge of <paramref name="source"/>; it may be negative.</param>
		/// <param name="y">The row of the top edge of <paramref name="source"/>; it may be negative.</param>
		/// <param name="op">One of the combination operators: <see cref="OperatorOr"/>, <see cref="OperatorAnd"/>,
		/// <see cref="OperatorXor"/>, <see cref="OperatorXnor"/> or <see cref="OperatorReplace"/>.</param>
		public void Compose(Jbig2Bitmap source, long x, long y, int op)
		{
			if (source == null)
				return;

			// clip the source rectangle against this bitmap, in 64 bits because region
			// offsets come straight from the file and may be close to the 32-bit limits.
			var left = Math.Max(0L, x);
			var right = Math.Min((long)this.Width, x + source.Width);
			var top = Math.Max(0L, y);
			var bottom = Math.Min((long)this.Height, y + source.Height);
			if (left >= right || top >= bottom)
				return;

			var dx0 = (int)left;
			var dx1 = (int)right;
			var offsetX = (int)(x);

			var srcData = source.Data;
			var srcStride = source.Stride;
			var dstData = this.Data;
			var dstStride = this.Stride;

			var firstByte = dx0 >> 3;
			var lastByte = (dx1 - 1) >> 3;

			// masks of the destination bits covered in the first and last byte of every row.
			var firstMask = 0xFF >> (dx0 & 7);
			var lastMask = (0xFF << (7 - ((dx1 - 1) & 7))) & 0xFF;
			if (firstByte == lastByte)
				firstMask &= lastMask;

			for (var row = (int)top; row < (int)bottom; row++)
			{
				var srcRow = (int)(row - y) * srcStride;
				var dstRow = row * dstStride;

				for (var b = firstByte; b <= lastByte; b++)
				{
					// q is the source column that lands on the first bit of destination byte b.
					var q = (b << 3) - offsetX;
					int bits;
					if (q >= 0)
					{
						var sb = srcRow + (q >> 3);
						var shift = q & 7;
						bits = srcData[sb] << shift;
						if (shift != 0 && (q >> 3) + 1 < srcStride)
							bits |= srcData[sb + 1] >> (8 - shift);
					}
					else
					{
						// only the first destination byte can start left of the source.
						bits = srcData[srcRow] >> (-q);
					}

					var mask = b == firstByte ? firstMask : (b == lastByte ? lastMask : 0xFF);
					var d = dstRow + b;
					int dst = dstData[d];
					int value;
					switch (op)
					{
						case OperatorOr:
							value = dst | bits;
							break;

						case OperatorAnd:
							value = dst & bits;
							break;

						case OperatorXor:
							value = dst ^ bits;
							break;

						case OperatorXnor:
							value = ~(dst ^ bits);
							break;

						default:
							value = bits;
							break;
					}

					dstData[d] = (byte)((dst & ~mask) | (value & mask));
				}
			}
		}

		#endregion

		#region Implementation

		// fills the rows [from, to) with the value, keeping the padding bits clear.
		private void Fill(int from, int to, int value)
		{
			var stride = this.Stride;
			var data = this.Data;
			if (value == 0)
			{
				Array.Clear(data, from * stride, (to - from) * stride);
				return;
			}

			var tailBits = this.Width & 7;
			var lastByte = (byte)(tailBits == 0 ? 0xFF : (0xFF << (8 - tailBits)) & 0xFF);
			for (var row = from; row < to; row++)
			{
				var offset = row * stride;
				for (var i = 0; i < stride - 1; i++)
					data[offset + i] = 0xFF;

				if (stride > 0)
					data[offset + stride - 1] = lastByte;
			}
		}

		#endregion
	}
}
