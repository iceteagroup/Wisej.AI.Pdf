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

// Entry point of the managed rasterizer: renders pages, or regions of pages
// such as vector figures, to RGBA images.

using System;
using Wisej.AI.Pdf.Content;
using Wisej.AI.Pdf.Imaging;

namespace Wisej.AI.Pdf.Rendering
{
	/// <summary>
	/// Renders PDF pages to images with a managed, dependency-free rasterizer.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The main purpose is to turn vector figures (charts, diagrams, drawings) into images, so they are
	/// not lost by the extraction; rendering whole pages is useful for vision models.
	/// </para>
	/// <para>
	/// Supported: anti-aliased paths with the non-zero and even-odd rules, strokes with caps, joins and
	/// dashes, clipping paths, constant alpha, images and inline images, text with embedded fonts (or
	/// substituted system fonts) in all rendering modes, Type 3 fonts, axial, radial, function-based and
	/// mesh shadings, tiling patterns, forms, optional content and annotation appearances, constant alpha,
	/// soft masks and the separable blend modes. Transparency groups are painted directly with their group
	/// alpha and soft mask (non-isolated, non-knockout); non-embedded fonts are substituted with system fonts.
	/// </para>
	/// <para>
	/// The methods never throw: malformed content is skipped. They are thread-safe for different pages.
	/// </para>
	/// </remarks>
	internal static class PageRenderer
	{
		// the largest canvas, in pixels; larger requests are rendered at a lower resolution.
		private const long MaxPixels = 100_000_000;

		// the maximum number of operators executed for a rendering.
		private const int MaxOperators = 5_000_000;

		/// <summary>
		/// Renders the part of a page inside <paramref name="region"/>.
		/// </summary>
		/// <param name="page">The page.</param>
		/// <param name="objects">The interpreted page; not used by the renderer, which interprets the content itself. Can be null.</param>
		/// <param name="region">The region in display space: points, top-left origin, page rotation applied (the space of <see cref="PdfPage.Width"/> and <see cref="PdfPage.Height"/>).</param>
		/// <param name="dpi">The resolution in dots per inch.</param>
		/// <returns>The RGBA image on a white background, or null when the region is empty.</returns>
		public static RasterImage RenderRegion(PdfPage page, PageObjects objects, PdfRect region, int dpi)
		{
			if (page == null || region.IsEmpty || double.IsNaN(region.Width) || double.IsNaN(region.Height))
				return null;

			var scale = Math.Max(1, Math.Min(4800, dpi)) / 72.0;

			// bound the size of the canvas.
			var pixels = region.Width * scale * region.Height * scale;
			if (pixels > MaxPixels)
				scale *= Math.Sqrt(MaxPixels / pixels);

			var width = Math.Max(1, (int)Math.Ceiling(region.Width * scale - 1e-6));
			var height = Math.Max(1, (int)Math.Ceiling(region.Height * scale - 1e-6));
			if ((long)width * height > MaxPixels)
				return null;

			Canvas canvas;
			try
			{
				canvas = new Canvas(width, height);
			}
			catch (Exception)
			{
				// out of memory.
				return null;
			}

			canvas.Clear(0xFFFFFF);

			try
			{
				// display space -> canvas pixels.
				var toCanvas = new Matrix(scale, 0, 0, scale, -region.Left * scale, -region.Top * scale);
				var deviceMatrix = page.DisplayMatrix.Multiply(toCanvas);

				// nothing is painted outside the visible page.
				var clip = new ClipRegion(
					Math.Max(0, -region.Left * scale), Math.Max(0, -region.Top * scale),
					Math.Min(width, (page.Width - region.Left) * scale), Math.Min(height, (page.Height - region.Top) * scale),
					null);

				if (!clip.IsEmpty)
				{
					var context = new RenderContext(page, MaxOperators);
					new Renderer(context, canvas).RenderPage(deviceMatrix, clip);
				}
			}
			catch (Exception)
			{
				// return what was rendered before the error.
			}

			return canvas.ToRasterImage();
		}

		/// <summary>
		/// Renders a whole page.
		/// </summary>
		/// <param name="page">The page.</param>
		/// <param name="dpi">The resolution in dots per inch.</param>
		/// <returns>The RGBA image on a white background, or null when the page can't be rendered.</returns>
		public static RasterImage RenderPage(PdfPage page, int dpi)
		{
			if (page == null)
				return null;

			return RenderRegion(page, null, new PdfRect(0, 0, page.Width, page.Height), dpi);
		}
	}
}
