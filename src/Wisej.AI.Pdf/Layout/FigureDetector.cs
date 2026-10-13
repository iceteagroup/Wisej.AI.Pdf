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

// Figure detection: images, image tiles and vector graphics (charts,
// diagrams) together with the text labels drawn inside them.

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.Content;

namespace Wisej.AI.Pdf.Layout
{
	/// <summary>
	/// Detects figures on a page.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Images that touch each other are grouped into one figure: producers often split large pictures
	/// into tiles or strips. Images covering the whole page behind OCR text (scanned documents) are not
	/// reported as figures, since their content is the text itself.
	/// </para>
	/// <para>
	/// Vector graphics are clustered by proximity. A cluster becomes a figure when it is large and complex
	/// enough (curves, many paths, shadings) and doesn't simply frame a paragraph. Short text lines inside a
	/// vector figure (axis labels, legends, diagram text) are attached to the figure.
	/// </para>
	/// </remarks>
	internal sealed class FigureDetector
	{
		#region Nested types

		/// <summary>
		/// A cluster of painted objects.
		/// </summary>
		private sealed class Cluster
		{
			/// <summary>The bounds in the reading frame.</summary>
			public PdfRect Bounds;

			/// <summary>The image placements.</summary>
			public readonly List<ImagePlacement> Images = new List<ImagePlacement>();

			/// <summary>The number of paths.</summary>
			public int PathCount;

			/// <summary>True when vector graphics were merged into an image cluster: the figure must be rendered.</summary>
			public bool HasVectors;

			/// <summary>The number of thin lines (connectors, arrows) in the cluster.</summary>
			public int ConnectorCount;

			/// <summary>The number of path points.</summary>
			public int PointCount;

			/// <summary>True when some path has curves.</summary>
			public bool HasCurves;

			/// <summary>The number of shadings.</summary>
			public int ShadingCount;

			/// <summary>The number of distinct colours used.</summary>
			public HashSet<int> Colors = new HashSet<int>();
		}

		#endregion

		// clusters closer than this many points are merged.
		private const double ClusterGap = 8;

		// the objects painted on the page.
		private readonly PageObjects _objects;
		// the orientation of the reading frame.
		private readonly int _orientation;
		// the page width in display space.
		private readonly double _width;
		// the page height in display space.
		private readonly double _height;
		// the minimum width and height of a figure.
		private readonly double _minSize;

		/// <summary>
		/// Initializes a new instance of <see cref="FigureDetector"/>.
		/// </summary>
		/// <param name="objects">The objects painted on the page.</param>
		/// <param name="orientation">The orientation of the reading frame.</param>
		/// <param name="width">The page width.</param>
		/// <param name="height">The page height.</param>
		/// <param name="minSize">The minimum width and height of a figure, in points.</param>
		public FigureDetector(PageObjects objects, int orientation, double width, double height, double minSize)
		{
			_objects = objects;
			_orientation = orientation;
			_width = width;
			_height = height;
			_minSize = minSize;
		}

		#region Methods

		/// <summary>
		/// Detects the figures and consumes the text lines that belong to them.
		/// </summary>
		/// <param name="lines">The lines in this orientation.</param>
		/// <param name="tables">The tables already detected, whose graphics are excluded.</param>
		/// <returns>The figure blocks.</returns>
		public List<LayoutBlock> Detect(List<TextLine> lines, List<LayoutBlock> tables)
		{
			var result = new List<LayoutBlock>();
			var hasText = false;
			foreach (var line in lines)
			{
				if (!line.IsConsumed && line.CharCount > 0)
				{
					hasText = true;
					break;
				}
			}

			var imageClusters = ClusterImages(hasText);
			var vectorClusters = ClusterVectors(tables);

			// a vector cluster touching image clusters joins them into one figure: diagrams made of pictures
			// linked by arrows, or pictures with vector annotations, are a single figure.
			foreach (var vector in vectorClusters)
			{
				var touching = imageClusters.FindAll(image => image.Bounds.Inflate(ClusterGap).IntersectsWith(vector.Bounds));
				if (touching.Count == 0)
				{
					if (IsVectorFigure(vector, lines))
					{
						var block = CreateBlock(vector, lines);
						if (block != null)
						{
							block.IsVectorFigure = true;
							result.Add(block);
						}
					}

					continue;
				}

				var target = touching[0];
				for (int i = 1; i < touching.Count; i++)
				{
					target.Bounds = target.Bounds.Union(touching[i].Bounds);
					target.Images.AddRange(touching[i].Images);
					imageClusters.Remove(touching[i]);
				}

				target.Bounds = target.Bounds.Union(vector.Bounds);
				target.PathCount += vector.PathCount;
				target.ConnectorCount += vector.ConnectorCount;
				target.HasVectors = true;
			}

			foreach (var image in imageClusters)
			{
				if (image.Bounds.Width >= _minSize && image.Bounds.Height >= _minSize)
				{
					var block = CreateBlock(image, lines);
					if (block != null)
						result.Add(block);
				}
			}

			return result;
		}

		#endregion

		#region Images

		/// <summary>
		/// Groups the visible images, merging touching tiles.
		/// </summary>
		/// <param name="hasText">True when the page has text, which excludes page-size scans.</param>
		/// <returns>The image clusters.</returns>
		private List<Cluster> ClusterImages(bool hasText)
		{
			var pageArea = _width * _height;
			var clusters = new List<Cluster>();

			foreach (var image in _objects.Images)
			{
				if (image.IsClipped || image.Alpha <= 0)
					continue;

				var bounds = ToFrame(image.VisibleBounds);
				if (bounds.IsEmpty)
					continue;

				// a scanned page under its OCR text is not a figure.
				if (hasText && bounds.Area > pageArea * 0.75)
					continue;

				var cluster = new Cluster { Bounds = bounds };
				cluster.Images.Add(image);
				clusters.Add(cluster);
			}

			// merge touching images (tiles and strips of the same picture).
			MergeTouching(clusters, 1.5);
			return clusters;
		}

		#endregion

		#region Vector graphics

		/// <summary>
		/// Groups the vector graphics and shadings that are not page decorations or table borders.
		/// </summary>
		/// <param name="tables">The tables already detected.</param>
		/// <returns>The vector clusters.</returns>
		private List<Cluster> ClusterVectors(List<LayoutBlock> tables)
		{
			var pageArea = _width * _height;
			var clusters = new List<Cluster>();

			foreach (var path in _objects.Paths)
			{
				if (path.IsClipped)
					continue;

				var bounds = ToFrame(path.Bounds);

				// page backgrounds and frames are decoration.
				if (bounds.Area > pageArea * 0.6)
					continue;

				// thin lines join figures (arrows and connectors link the parts of a diagram), but long ones are
				// separators across the page and short isolated ones are underlines: neither makes a figure alone.
				var isThin = !path.HasCurves && (bounds.Width < 2.5 || bounds.Height < 2.5);
				if (isThin && (bounds.Width > _width * 0.4 || bounds.Height > _height * 0.4))
					continue;

				// white fills paint the background.
				if (path.IsFilled && !path.IsStroked && path.FillColor == 0xFFFFFF)
					continue;

				// graphics of detected tables (borders, shaded cells) belong to the table.
				if (IsInsideTable(bounds, tables))
					continue;

				var cluster = new Cluster
				{
					Bounds = bounds,
					PathCount = isThin ? 0 : 1,
					ConnectorCount = isThin ? 1 : 0,
					PointCount = isThin ? 0 : path.PointCount,
					HasCurves = path.HasCurves,
				};

				if (!isThin)
					cluster.Colors.Add(path.IsFilled ? path.FillColor : path.StrokeColor);
				clusters.Add(cluster);
			}

			foreach (var shading in _objects.Shadings)
			{
				if (shading.IsClipped)
					continue;

				var bounds = ToFrame(shading.Bounds);
				if (bounds.Area > pageArea * 0.6 || IsInsideTable(bounds, tables))
					continue;

				clusters.Add(new Cluster { Bounds = bounds, ShadingCount = 1 });
			}

			MergeTouching(clusters, ClusterGap);
			return clusters;
		}

		// true when the bounds lie inside a detected table.
		private static bool IsInsideTable(PdfRect bounds, List<LayoutBlock> tables)
		{
			foreach (var table in tables)
			{
				if (table.Bounds.Inflate(3).Contains(bounds))
					return true;
			}

			return false;
		}

		// a vector cluster is a figure when it's big and graphically rich, and isn't a frame around prose.
		private bool IsVectorFigure(Cluster cluster, List<TextLine> lines)
		{
			var bounds = cluster.Bounds;
			if (bounds.Width < Math.Max(_minSize, 36) || bounds.Height < Math.Max(_minSize, 24))
				return false;

			var rich = cluster.HasCurves || cluster.ShadingCount > 0 || cluster.PathCount >= 6 || cluster.PointCount >= 24 || cluster.Colors.Count >= 3;
			if (!rich)
				return false;

			// a box containing long text lines is a text frame (callout, sidebar), not a figure.
			var inside = 0;
			var longLines = 0;
			foreach (var line in lines)
			{
				if (line.IsConsumed || !bounds.Contains(line.Bounds, 2))
					continue;

				inside++;
				if (line.Words.Count > 8)
					longLines++;
			}

			if (longLines >= 2 || (inside > 0 && longLines * 2 >= inside))
				return false;

			// text covering a large part of the area: a text box (code sample, note), not a drawing with labels.
			double textArea = 0;
			foreach (var line in lines)
			{
				if (!line.IsConsumed && bounds.Contains(line.Bounds, 2))
					textArea += line.Bounds.Area;
			}

			if (textArea > bounds.Area * 0.2)
				return false;

			return true;
		}

		#endregion

		#region Helpers

		// merges clusters whose bounds are within "gap" of each other, until stable.
		private static void MergeTouching(List<Cluster> clusters, double gap)
		{
			var merged = true;
			while (merged)
			{
				merged = false;
				for (int i = 0; i < clusters.Count; i++)
				{
					for (int j = i + 1; j < clusters.Count; j++)
					{
						if (!clusters[i].Bounds.Inflate(gap).IntersectsWith(clusters[j].Bounds))
							continue;

						var a = clusters[i];
						var b = clusters[j];
						a.Bounds = a.Bounds.Union(b.Bounds);
						a.Images.AddRange(b.Images);
						a.PathCount += b.PathCount;
						a.ConnectorCount += b.ConnectorCount;
						a.PointCount += b.PointCount;
						a.HasCurves |= b.HasCurves;
						a.ShadingCount += b.ShadingCount;
						a.Colors.UnionWith(b.Colors);

						clusters.RemoveAt(j);
						j--;
						merged = true;
					}
				}
			}
		}

		/// <summary>
		/// Creates the figure block of a cluster and attaches the short text lines drawn over it.
		/// </summary>
		/// <param name="cluster">The cluster.</param>
		/// <param name="lines">The lines of the page.</param>
		/// <returns>The figure block.</returns>
		private LayoutBlock CreateBlock(Cluster cluster, List<TextLine> lines)
		{
			var block = new LayoutBlock { Kind = LayoutBlockKind.Figure, Bounds = cluster.Bounds };
			block.IsVectorFigure = cluster.HasVectors && cluster.PathCount + cluster.ConnectorCount >= 3;
			block.Images.AddRange(cluster.Images);
			block.Images.Sort((a, b) => a.Index.CompareTo(b.Index));

			// text drawn over the figure (labels, legends) belongs to it.
			foreach (var line in lines)
			{
				if (line.IsConsumed || !cluster.Bounds.Contains(line.Bounds, 1.5))
					continue;

				// long lines over a picture are a text overlay, keep them in the flow.
				if (line.Words.Count > 12)
					continue;

				line.IsConsumed = true;
				block.FigureText.Add(line);
			}

			// vector drawings often have labels just outside their shapes (arrow labels, axis titles): short lines
			// touching the drawing are part of it and extend its bounds, so they are not cut off the rendered image.
			if (block.IsVectorFigure || cluster.Images.Count == 0)
			{
				var area = block.Bounds;
				foreach (var line in lines)
				{
					if (line.IsConsumed || line.Words.Count > 5 || line.Bounds.Width > area.Width * 0.6)
						continue;

					if (!line.Bounds.IntersectsWith(area.Inflate(Math.Max(4, line.FontSize * 0.8))))
						continue;

					line.IsConsumed = true;
					block.FigureText.Add(line);
					block.Bounds = block.Bounds.Union(line.Bounds);
				}
			}

			return block;
		}

		// converts a display rectangle to the reading frame.
		private PdfRect ToFrame(PdfRect rect) => ReadingFrame.ToFrame(_orientation, rect, _width, _height);

		#endregion
	}
}
