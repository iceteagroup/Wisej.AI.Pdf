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

// Memory and work budget for the decoding of one JBIG2 image.

using System.IO;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Creates the bitmaps of one JBIG2 decoding session and accounts for the work done, within a
	/// budget proportional to the size of the page.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Every size and count in a JBIG2 stream comes from the file, so a corrupt or hostile stream
	/// could ask for huge bitmaps, millions of symbols or billions of symbol instances. Reading past
	/// the end of arithmetically coded data does not stop such loops by itself, because the decoder
	/// then sees an endless run of 0xFF bytes and keeps producing decisions.
	/// </para>
	/// <para>
	/// The allocator rejects any single bitmap above <see cref="MaxPixels"/>, and charges every
	/// allocated pixel and every unit of drawing work to one budget: a fixed amount for the
	/// dictionaries, plus a few times the area of the page once it is known. A valid stream needs
	/// far less, since every pixel of the page is decoded or drawn about once. When the budget runs
	/// out the allocator throws <see cref="InvalidDataException"/>, which the decoder catches per
	/// segment, keeping what was decoded so far.
	/// </para>
	/// </remarks>
	internal sealed class Jbig2Allocator
	{
		#region Fields

		/// <summary>
		/// The largest bitmap allowed, in pixels (100 Mpixels, 12.5 MB).
		/// </summary>
		public const long MaxPixels = 100L * 1000 * 1000;

		/// <summary>
		/// The budget available before the page size is known, in pixels or equivalent units of work.
		/// </summary>
		private const long InitialBudget = 64L * 1000 * 1000;

		/// <summary>
		/// The budget granted for every pixel of the page.
		/// </summary>
		private const long BudgetPerPagePixel = 3;

		/// <summary>The budget still available to the session.</summary>
		private long _remaining = InitialBudget;

		#endregion

		#region Methods

		/// <summary>
		/// Creates a cleared bitmap.
		/// </summary>
		/// <param name="width">The width in pixels.</param>
		/// <param name="height">The height in pixels.</param>
		/// <returns>The new bitmap.</returns>
		/// <exception cref="InvalidDataException">The size is negative, too large or over the session budget.</exception>
		public Jbig2Bitmap Create(long width, long height)
		{
			Reserve(width, height);
			return new Jbig2Bitmap((int)width, (int)height);
		}

		/// <summary>
		/// Accounts for a bitmap of the given size without creating it.
		/// </summary>
		/// <param name="width">The width in pixels.</param>
		/// <param name="height">The height in pixels.</param>
		/// <exception cref="InvalidDataException">The size is negative, too large or over the session budget.</exception>
		public void Reserve(long width, long height)
		{
			var pixels = Measure(width, height);
			if (pixels > MaxPixels)
				throw new InvalidDataException("JBIG2 bitmap too large.");

			Spend(pixels);
		}

		/// <summary>
		/// Increases the budget for a page, or a part of a page, of the given size.
		/// </summary>
		/// <param name="width">The width in pixels.</param>
		/// <param name="height">The height in pixels.</param>
		/// <exception cref="InvalidDataException">The size is negative or too large.</exception>
		public void GrantPage(long width, long height)
		{
			var pixels = Measure(width, height);
			if (pixels > MaxPixels)
				throw new InvalidDataException("JBIG2 page too large.");

			_remaining += BudgetPerPagePixel * pixels;
		}

		/// <summary>
		/// Charges work to the session budget.
		/// </summary>
		/// <param name="units">The amount of work, in pixels decoded or drawn.</param>
		/// <exception cref="InvalidDataException">The session budget is exhausted.</exception>
		public void Spend(long units)
		{
			_remaining -= units;
			if (_remaining < 0)
				throw new InvalidDataException("JBIG2 decoding budget exhausted.");
		}

		#endregion

		#region Implementation

		// validates a bitmap size and returns its cost, counting at least one byte per row so that
		// thin bitmaps are not free.
		private static long Measure(long width, long height)
		{
			if (width < 0 || height < 0 || width > int.MaxValue - 8 || height > int.MaxValue)
				throw new InvalidDataException("Invalid JBIG2 bitmap size.");

			return (width + 8) * height;
		}

		#endregion
	}
}
