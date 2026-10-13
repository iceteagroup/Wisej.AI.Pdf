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

// Produces the image of a figure: a single picture (oriented as on the page),
// a picture reassembled from tiles, or a rendered vector drawing.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Wisej.AI.Pdf.Content;
using Wisej.AI.Pdf.Layout;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Imaging
{
	/// <summary>
	/// An extracted image with the hash of its content, used for de-duplication.
	/// </summary>
	internal sealed class ExtractedImage
	{
		/// <summary>The image.</summary>
		public PdfImage Image;

		/// <summary>The hash of the encoded image data.</summary>
		public string Hash;
	}

	/// <summary>
	/// Extracts the images of figure blocks.
	/// </summary>
	/// <remarks>
	/// This class is thread-safe: decoded images are cached per document so a picture repeated on
	/// many pages (a logo, a background) is decoded only once.
	/// </remarks>
	internal sealed class FigureImageExtractor
	{
		#region Fields

		/// <summary>The document owning the images; supplies the shared resource cache.</summary>
		private readonly PdfDocument _document;

		/// <summary>The options controlling rendering, resizing and the output format.</summary>
		private readonly PdfImageOptions _options;

		// decoded images by XObject stream (and fill colour for stencil masks).
		private readonly Dictionary<(PdfStream, int), Lazy<DecodedImage>> _cache = new Dictionary<(PdfStream, int), Lazy<DecodedImage>>();

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new instance of <see cref="FigureImageExtractor"/>.
		/// </summary>
		/// <param name="document">The document.</param>
		/// <param name="options">The image options.</param>
		public FigureImageExtractor(PdfDocument document, PdfImageOptions options)
		{
			_document = document;
			_options = options;
		}

		#endregion

		#region Methods

		/// <summary>
		/// Extracts the image of a figure.
		/// </summary>
		/// <param name="page">The page.</param>
		/// <param name="objects">The objects painted on the page.</param>
		/// <param name="block">The figure block.</param>
		/// <param name="isImageOnly">True when the page has no text (scanned page).</param>
		/// <returns>The image, or null when the figure can't be extracted.</returns>
		public ExtractedImage Extract(PdfPage page, PageObjects objects, LayoutBlock block, bool isImageOnly)
		{
			RasterImage raster = null;
			byte[] jpeg = null;
			var lossySource = false;
			var kind = PdfImageKind.Picture;

			if (block.IsVectorFigure || (block.Images.Count > 0 && HasVectorOverlay(objects, block)))
			{
				if (!_options.RenderVectorFigures)
					return null;

				raster = Rendering.PageRenderer.RenderRegion(page, objects, block.DisplayBounds, _options.RenderResolution);
				kind = PdfImageKind.VectorFigure;
			}
			else if (block.Images.Count == 1)
			{
				var placement = block.Images[0];
				var decoded = DecodePlacement(placement);
				if (decoded?.Raster == null)
					return null;

				GetOrientation(placement.Transform, out var quarterTurns, out var mirror);
				raster = decoded.Raster.Transform(quarterTurns, mirror);

				// the original JPEG is kept when nothing changed.
				if (decoded.Jpeg != null && quarterTurns == 0 && !mirror && _options.Format != PdfImageFormat.Png)
					jpeg = decoded.Jpeg;

				lossySource = IsLossy(placement);
			}
			else if (block.Images.Count > 1)
			{
				raster = Compose(block, out lossySource);
			}

			if (raster == null)
				return null;

			if (isImageOnly && block.DisplayBounds.Area > page.Width * page.Height * 0.5)
				kind = PdfImageKind.PageScan;

			// bound the size of the result.
			var pixels = (long)raster.Width * raster.Height;
			if (_options.MaxPixels > 0 && pixels > _options.MaxPixels)
			{
				var scale = Math.Sqrt((double)_options.MaxPixels / pixels);
				raster = raster.Resize((int)(raster.Width * scale), (int)(raster.Height * scale));
				jpeg = null;
			}

			var image = new PdfImage
			{
				PageNumber = page.Number,
				Bounds = block.DisplayBounds,
				Width = raster.Width,
				Height = raster.Height,
				Kind = kind,
				ResourceName = block.Images.Count > 0 ? block.Images[0].Name : null,
			};

			Encode(image, raster, jpeg, lossySource);
			return new ExtractedImage { Image = image, Hash = ComputeHash(image.Data) };
		}

		#endregion

		#region Decoding

		/// <summary>
		/// Decodes the image of a placement, sharing the result between placements of the same XObject.
		/// </summary>
		/// <param name="placement">The image placement.</param>
		/// <returns>The decoded image, or null when the image can't be decoded.</returns>
		/// <remarks>
		/// Stencil masks are cached per fill colour, since the colour is baked into the decoded pixels.
		/// </remarks>
		private DecodedImage DecodePlacement(ImagePlacement placement)
		{
			var cache = _document.ResourceCache;

			// inline images are not shared: decode them directly.
			if (placement.Stream == null)
			{
				var inline = placement.Inline;
				return inline == null ? null : ImageDecoder.Decode(inline.Dictionary, inline.Data, placement.Resources, cache, placement.FillColor, _options);
			}

			var stream = placement.Stream;
			var isStencil = stream.GetBool(PdfNames.ImageMask);
			var key = (stream, isStencil ? placement.FillColor : 0);

			Lazy<DecodedImage> lazy;
			lock (_cache)
			{
				if (!_cache.TryGetValue(key, out lazy))
				{
					lazy = new Lazy<DecodedImage>(() => ImageDecoder.Decode(stream, stream.GetRawBytes(), placement.Resources, cache, placement.FillColor, _options));
					_cache[key] = lazy;
				}
			}

			return lazy.Value;
		}

		// JPEG and JPEG 2000 sources are photographic: re-encode them as JPEG to keep files small.
		private static bool IsLossy(ImagePlacement placement)
		{
			var filters = Filters.FilterPipeline.GetFilters(placement.Dictionary, out _);
			return filters.Contains("DCTDecode") || filters.Contains("JPXDecode");
		}

		#endregion

		#region Orientation

		/// <summary>
		/// Determines the quarter turns and mirroring that make the image appear as on the page.
		/// </summary>
		/// <param name="transform">The image space to display space transformation.</param>
		/// <param name="quarterTurns">Receives the clockwise quarter turns.</param>
		/// <param name="mirror">Receives true when the image must be mirrored (before rotating).</param>
		internal static void GetOrientation(Matrix transform, out int quarterTurns, out bool mirror)
		{
			quarterTurns = 0;
			mirror = false;

			// image row 0 is at the top of the unit square (y = 1). The axes of the image in display space:
			var origin = transform.Transform(0, 1);
			var xAxis = Direction(transform.Transform(1, 1), origin);
			var yAxis = Direction(transform.Transform(0, 0), origin);

			// skewed or arbitrarily rotated images are left as they are.
			if (xAxis == null || yAxis == null)
				return;

			// try the 8 combinations of mirroring and rotation.
			for (int m = 0; m < 2; m++)
			{
				for (int q = 0; q < 4; q++)
				{
					var x = Apply((1, 0), m == 1, q);
					var y = Apply((0, 1), m == 1, q);
					if (x == xAxis.Value && y == yAxis.Value)
					{
						quarterTurns = q;
						mirror = m == 1;
						return;
					}
				}
			}
		}

		// the axis-aligned unit direction from a to b, or null when not axis-aligned.
		private static (int, int)? Direction(PdfPoint b, PdfPoint a)
		{
			var dx = b.X - a.X;
			var dy = b.Y - a.Y;
			var length = Math.Sqrt(dx * dx + dy * dy);
			if (length < 1e-9)
				return null;

			dx /= length;
			dy /= length;
			if (Math.Abs(dx) > 0.995)
				return (Math.Sign(dx), 0);
			if (Math.Abs(dy) > 0.995)
				return (0, Math.Sign(dy));

			return null;
		}

		// applies a mirror then q clockwise quarter turns (y-down coordinates) to a direction.
		private static (int, int) Apply((int X, int Y) v, bool mirror, int q)
		{
			if (mirror)
				v = (-v.X, v.Y);

			for (int i = 0; i < q; i++)
				v = (-v.Y, v.X);

			return v;
		}

		#endregion

		#region Composition

		// reassembles a figure made of several images (tiles, strips, layered pictures) in display space.
		private RasterImage Compose(LayoutBlock block, out bool lossySource)
		{
			lossySource = false;
			var bounds = block.DisplayBounds;
			if (bounds.IsEmpty)
				return null;

			// the resolution of the canvas is the highest resolution of the parts.
			var decodedParts = new List<(ImagePlacement Placement, RasterImage Raster)>();
			double scale = 0;
			foreach (var placement in block.Images)
			{
				var decoded = DecodePlacement(placement);
				if (decoded?.Raster == null)
					continue;

				GetOrientation(placement.Transform, out var quarterTurns, out var mirror);
				var raster = decoded.Raster.Transform(quarterTurns, mirror);
				decodedParts.Add((placement, raster));

				if (placement.Bounds.Width > 0)
					scale = Math.Max(scale, raster.Width / placement.Bounds.Width);

				lossySource |= IsLossy(placement);
			}

			if (decodedParts.Count == 0 || scale <= 0)
				return null;

			// bound the canvas before allocating it.
			var width = (int)Math.Ceiling(bounds.Width * scale);
			var height = (int)Math.Ceiling(bounds.Height * scale);
			var limit = _options.MaxPixels > 0 ? _options.MaxPixels : 40_000_000;
			if ((long)width * height > limit)
			{
				var factor = Math.Sqrt((double)limit / ((long)width * height));
				scale *= factor;
				width = (int)(width * factor);
				height = (int)(height * factor);
			}

			var canvas = new RasterImage(Math.Max(1, width), Math.Max(1, height));
			foreach (var (placement, raster) in decodedParts)
			{
				var target = placement.Bounds;
				var x = (int)Math.Round((target.Left - bounds.Left) * scale);
				var y = (int)Math.Round((target.Top - bounds.Top) * scale);
				var w = (int)Math.Round((target.Right - bounds.Left) * scale) - x;
				var h = (int)Math.Round((target.Bottom - bounds.Top) * scale) - y;
				canvas.Draw(raster, x, y, w, h);
			}

			return canvas;
		}

		// vector graphics drawn over a picture (arrows, callouts) require rendering the whole figure.
		private static bool HasVectorOverlay(PageObjects objects, LayoutBlock block)
		{
			var lastImage = 0;
			foreach (var image in block.Images)
				lastImage = Math.Max(lastImage, image.Index);

			var count = 0;
			foreach (var path in objects.Paths)
			{
				if (path.Index > lastImage && !path.IsClipped && block.DisplayBounds.Contains(path.Bounds, 1)
					&& path.Bounds.Width > 4 && path.Bounds.Height > 4)
				{
					count++;
				}
			}

			return count >= 3;
		}

		#endregion

		#region Encoding

		/// <summary>
		/// Encodes the raster into <see cref="PdfImage.Data"/> and sets the MIME type and transparency flag.
		/// </summary>
		/// <param name="image">The image receiving the encoded data.</param>
		/// <param name="raster">The pixels to encode.</param>
		/// <param name="jpeg">The original JPEG data to reuse as is, or null.</param>
		/// <param name="lossySource">True when the source is JPEG or JPEG 2000, which favours JPEG output in automatic mode.</param>
		private void Encode(PdfImage image, RasterImage raster, byte[] jpeg, bool lossySource)
		{
			var transparent = raster.HasTransparency;
			image.HasTransparency = transparent;

			if (jpeg != null && _options.Format != PdfImageFormat.Png)
			{
				image.Data = jpeg;
				image.MimeType = "image/jpeg";
				return;
			}

			var useJpeg = _options.Format == PdfImageFormat.Jpeg || (_options.Format == PdfImageFormat.Auto && lossySource && !transparent);
			if (useJpeg)
			{
				image.Data = new JpegEncoder(90).Encode(raster);
				image.MimeType = "image/jpeg";
				image.HasTransparency = false;
			}
			else
			{
				image.Data = PngEncoder.Encode(raster);
				image.MimeType = "image/png";
			}
		}

		/// <summary>
		/// Computes the hash used to detect identical images.
		/// </summary>
		/// <param name="data">The encoded image data.</param>
		/// <returns>The Base64 SHA-1 hash of <paramref name="data"/>.</returns>
		private static string ComputeHash(byte[] data)
		{
			using (var sha = SHA1.Create())
				return Convert.ToBase64String(sha.ComputeHash(data));
		}

		#endregion
	}
}
