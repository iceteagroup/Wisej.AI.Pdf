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

// Inverse discrete wavelet transform: reversible 5/3 and irreversible 9/7 lifting (ITU-T T.800, Annex F).

using System;
using System.Threading.Tasks;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Reconstructs tile-component samples from their sub-band coefficients.
	/// </summary>
	/// <remarks>
	/// The coefficients are stored in the interleaved (Mallat) layout: the LL band of a level in the
	/// top-left corner, followed by HL on the right, LH below and HH diagonally. Each level is
	/// reconstructed in place with a horizontal pass over the rows followed by a vertical pass (F.3.2).
	/// <para>
	/// The vertical pass works on groups of adjacent columns copied into a small row-major buffer,
	/// so the lifting steps run along contiguous memory instead of striding through the image.
	/// The 5/3 filter operates on integer values stored in floats; the floor divisions are done on
	/// integers so the reconstruction is exact.
	/// </para>
	/// </remarks>
	internal static class JpxWavelet
	{
		#region Constants

		// the symmetric extension needs at most four samples on each side (the 9/7 filter).
		private const int Pad = 4;

		// the number of columns processed together by the vertical pass.
		private const int Group = 32;

		// levels with at least this many samples are filtered on several threads.
		private const int ParallelArea = 1 << 18;

		// the number of rows per work item of the parallel horizontal pass.
		private const int ChunkRows = 64;

		// lifting parameters of the 9/7 filter (Table F.4).

		/// <summary>The first lifting parameter (alpha), undone last.</summary>
		private const float Alpha = -1.586134342059924f;

		/// <summary>The second lifting parameter (beta).</summary>
		private const float Beta = -0.052980118572961f;

		/// <summary>The third lifting parameter (gamma).</summary>
		private const float Gamma = 0.882911075530934f;

		/// <summary>The fourth lifting parameter (delta), undone first.</summary>
		private const float Delta = 0.443506852043971f;

		/// <summary>The scaling factor applied to the low-pass samples.</summary>
		private const float K = 1.230174104914001f;

		/// <summary>The scaling factor applied to the high-pass samples (1 / K).</summary>
		private const float InverseK = 1f / 1.230174104914001f;

		#endregion

		#region Methods

		/// <summary>
		/// Runs the inverse transform from the lowest resolution up to <paramref name="level"/>.
		/// </summary>
		/// <param name="samples">The coefficients in the interleaved layout; receives the samples.</param>
		/// <param name="stride">The row stride of <paramref name="samples"/>.</param>
		/// <param name="resolutions">The resolution levels of the tile-component.</param>
		/// <param name="level">The highest resolution level to reconstruct.</param>
		/// <param name="reversible">True for the 5/3 filter, false for the 9/7 filter.</param>
		public static void Inverse(float[] samples, int stride, JpxResolution[] resolutions, int level, bool reversible)
		{
			if (level <= 0)
				return;

			var top = resolutions[level];
			var lineLength = (Math.Max(top.Width, top.Height) + 2 * Pad) * Group;
			var line = new float[lineLength];

			for (int r = 1; r <= level; r++)
			{
				var resolution = resolutions[r];
				var lower = resolutions[r - 1];
				var width = resolution.Width;
				var height = resolution.Height;
				if (width <= 0 || height <= 0)
					continue;

				if ((long)width * height < ParallelArea || Environment.ProcessorCount < 2)
				{
					Horizontal(samples, stride, resolution, lower, 0, height, line, reversible);
					Vertical(samples, stride, resolution, lower, 0, width, line, reversible);
					continue;
				}

				// large levels: rows and column groups are independent, so split them over several threads.
				var rowChunks = (height + ChunkRows - 1) / ChunkRows;
				Parallel.For(
					0,
					rowChunks,
					() => new float[lineLength],
					(chunk, loop, buffer) =>
					{
						var y0 = chunk * ChunkRows;
						Horizontal(samples, stride, resolution, lower, y0, Math.Min(height, y0 + ChunkRows), buffer, reversible);
						return buffer;
					},
					buffer => { });

				var columnChunks = (width + Group - 1) / Group;
				Parallel.For(
					0,
					columnChunks,
					() => new float[lineLength],
					(chunk, loop, buffer) =>
					{
						var x0 = chunk * Group;
						Vertical(samples, stride, resolution, lower, x0, Math.Min(width, x0 + Group), buffer, reversible);
						return buffer;
					},
					buffer => { });
			}
		}

		#endregion

		#region Implementation

		// horizontal pass over rows [y0, y1) of a level: interleave the low and high halves of each row and filter it.
		private static void Horizontal(float[] samples, int stride, JpxResolution resolution, JpxResolution lower, int y0, int y1, float[] line, bool reversible)
		{
			var width = resolution.Width;
			var lowWidth = lower.Width;
			var lowStart = Pad + (resolution.X0 & 1);
			var highStart = Pad + 1 - (resolution.X0 & 1);

			for (int y = y0; y < y1; y++)
			{
				var row = y * stride;
				for (int i = 0; i < lowWidth; i++)
					line[lowStart + 2 * i] = samples[row + i];
				for (int i = lowWidth; i < width; i++)
					line[highStart + 2 * (i - lowWidth)] = samples[row + i];

				Synthesize(line, width, 1, resolution.X0, reversible);
				Array.Copy(line, Pad, samples, row, width);
			}
		}

		// vertical pass over columns [x0, x1) of a level, Group columns at a time.
		private static void Vertical(float[] samples, int stride, JpxResolution resolution, JpxResolution lower, int x0, int x1, float[] line, bool reversible)
		{
			var height = resolution.Height;
			var lowHeight = lower.Height;
			var lowStart = Pad + (resolution.Y0 & 1);
			var highStart = Pad + 1 - (resolution.Y0 & 1);

			for (int x = x0; x < x1; x += Group)
			{
				var count = Math.Min(Group, x1 - x);
				for (int i = 0; i < height; i++)
				{
					var position = i < lowHeight ? lowStart + 2 * i : highStart + 2 * (i - lowHeight);
					Array.Copy(samples, i * stride + x, line, position * count, count);
				}

				Synthesize(line, height, count, resolution.Y0, reversible);

				for (int i = 0; i < height; i++)
					Array.Copy(line, (Pad + i) * count, samples, i * stride + x, count);
			}
		}

		// 1D_SR (F.3.6): reconstructs `count` interleaved signals of `length` samples starting at
		// coordinate i0, stored from row Pad of `line` with `count` values per row.
		private static void Synthesize(float[] line, int length, int count, int i0, bool reversible)
		{
			if (length == 1)
			{
				// a single high-pass sample was doubled by the forward transform (F.3.7).
				if ((i0 & 1) != 0)
				{
					for (int c = 0; c < count; c++)
						line[Pad * count + c] *= 0.5f;
				}

				return;
			}

			Extend(line, length, count);

			if (reversible)
				Reversible(line, length, count, i0);
			else
				Irreversible(line, length, count, i0);
		}

		// periodic symmetric extension of the signal by Pad samples on each side (F.3.7).
		private static void Extend(float[] line, int length, int count)
		{
			var last = length - 1;
			for (int k = 1; k <= Pad; k++)
			{
				var left = Reflect(-k, last);
				var right = Reflect(last + k, last);
				Array.Copy(line, (Pad + left) * count, line, (Pad - k) * count, count);
				Array.Copy(line, (Pad + right) * count, line, (Pad + last + k) * count, count);
			}
		}

		// mirrors index i into [0, last] without repeating the edge samples.
		private static int Reflect(int i, int last)
		{
			var period = 2 * last;
			i %= period;
			if (i < 0)
				i += period;

			return i <= last ? i : period - i;
		}

		// 1D_FILTR_5-3R (F.3.8.1).
		private static void Reversible(float[] line, int length, int count, int i0)
		{
			// the low-pass (even) samples are at the positions j where j + i0 is even.
			var start = Pad - 1;
			if (((start + i0) & 1) != 0)
				start++;

			// step 1: X(2n) = Y(2n) - floor((Y(2n-1) + Y(2n+1) + 2) / 4).
			for (int j = start; j <= Pad + length; j += 2)
			{
				var current = j * count;
				var previous = current - count;
				var next = current + count;
				for (int c = 0; c < count; c++)
					line[current + c] -= ((int)line[previous + c] + (int)line[next + c] + 2) >> 2;
			}

			// step 2: X(2n+1) = Y(2n+1) + floor((X(2n) + X(2n+2)) / 2).
			start = Pad;
			if (((start + i0) & 1) == 0)
				start++;

			for (int j = start; j < Pad + length; j += 2)
			{
				var current = j * count;
				var previous = current - count;
				var next = current + count;
				for (int c = 0; c < count; c++)
					line[current + c] += ((int)line[previous + c] + (int)line[next + c]) >> 1;
			}
		}

		// 1D_FILTR_9-7I (F.3.8.2).
		private static void Irreversible(float[] line, int length, int count, int i0)
		{
			var total = length + 2 * Pad;

			// the first even (low-pass) and odd (high-pass) positions in the buffer.
			var even = i0 & 1;
			var odd = 1 - even;

			// steps 1 and 2: scale the low-pass samples by K and the high-pass ones by 1/K.
			for (int j = 0; j < total; j++)
			{
				var factor = ((j + i0) & 1) == 0 ? K : InverseK;
				var current = j * count;
				for (int c = 0; c < count; c++)
					line[current + c] *= factor;
			}

			// steps 3 to 6: each lifting step needs one more sample of margin than the next one.
			Lift(line, count, even, 1, total - 1, Delta);
			Lift(line, count, odd, 2, total - 2, Gamma);
			Lift(line, count, even, 3, total - 3, Beta);
			Lift(line, count, odd, 4, total - 4, Alpha);
		}

		// X(j) -= factor * (X(j-1) + X(j+1)) for the positions of the given parity in [from, to).
		private static void Lift(float[] line, int count, int parity, int from, int to, float factor)
		{
			var start = from;
			if ((start & 1) != parity)
				start++;

			for (int j = start; j < to; j += 2)
			{
				var current = j * count;
				var previous = current - count;
				var next = current + count;
				for (int c = 0; c < count; c++)
					line[current + c] -= factor * (line[previous + c] + line[next + c]);
			}
		}

		#endregion
	}
}
