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

// Smooth shadings (ISO 32000-2, 8.7.4.5): function-based, axial and radial
// shadings as paints, and the mesh shadings (free-form and lattice triangle
// meshes, Coons and tensor-product patches) rendered with Gouraud triangles.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Wisej.AI.Pdf.Graphics;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Rendering
{
	/// <summary>
	/// Creates the paints of shading dictionaries.
	/// </summary>
	internal static class Shadings
	{
		// number of entries of the colour tables of axial and radial shadings.
		private const int LutSize = 1024;

		// samples of the colour tables of expensive functions.
		private const int CoarseSamples = 33;

		/// <summary>
		/// Creates the paint of a shading.
		/// </summary>
		/// <param name="shading">The shading dictionary or stream.</param>
		/// <param name="shadingToDevice">Maps the shading space to device space.</param>
		/// <param name="clip">The current clipping region: mesh shadings are rendered only inside it.</param>
		/// <param name="cache">The resource cache, used for the colour space.</param>
		/// <param name="resources">The resources used to resolve a named colour space.</param>
		/// <returns>The paint, or null when the shading is invalid or unsupported.</returns>
		public static Paint Create(PdfDictionary shading, Matrix shadingToDevice, ClipRegion clip, ResourceCache cache, PdfDictionary resources)
		{
			if (shading == null || Math.Abs(shadingToDevice.Determinant) < 1e-12)
				return null;

			var colorSpaceObject = shading.Resolve(PdfNames.ColorSpace, "CS");
			if (colorSpaceObject == null)
				return null;

			var colorSpace = cache.GetColorSpace(colorSpaceObject, resources);
			if (colorSpace.Kind == ColorSpaceKind.Pattern)
				return null;

			var function = PdfFunction.Create(shading.Resolve("Function"));

			switch (shading.GetInt("ShadingType"))
			{
				case 1:
					return CreateFunctionBased(shading, shadingToDevice, colorSpace, function);

				case 2:
					return CreateAxial(shading, shadingToDevice, colorSpace, function);

				case 3:
					return CreateRadial(shading, shadingToDevice, colorSpace, function);

				case 4:
				case 5:
				case 6:
				case 7:
					return shading is PdfStream stream ? MeshShading.Render(stream, shadingToDevice, clip, colorSpace, function) : null;

				default:
					return null;
			}
		}

		/// <summary>
		/// Builds a table of opaque colours for t from <paramref name="t0"/> to <paramref name="t1"/>.
		/// </summary>
		/// <param name="function">The function mapping t to colour components.</param>
		/// <param name="colorSpace">The colour space of the components.</param>
		/// <param name="t0">The first value of t.</param>
		/// <param name="t1">The last value of t.</param>
		/// <param name="size">The number of entries.</param>
		/// <returns>The colours as 0xRRGGBB.</returns>
		public static int[] BuildLut(PdfFunction function, ColorSpace colorSpace, double t0, double t1, int size)
		{
			var lut = new int[size];
			var input = new double[1];
			var output = new double[Math.Max(Math.Max(function.OutputCount, colorSpace.ComponentCount), 1)];

			int Evaluate(double t)
			{
				input[0] = t;
				Array.Clear(output, 0, output.Length);
				try
				{
					function.Evaluate(input, output);
				}
				catch (Exception)
				{
					// a broken function evaluates to the initial colour.
				}

				return colorSpace.ToRgb(output);
			}

			// expensive functions (deep stitching, sampled functions with many inputs) are sampled
			// coarsely and interpolated, so a pathological shading can't take seconds.
			var watch = Stopwatch.StartNew();
			lut[0] = Evaluate(t0);
			var samples = watch.Elapsed.TotalMilliseconds > 0.05 ? Math.Min(size, CoarseSamples) : size;
			if (samples == size)
			{
				for (int i = 1; i < size; i++)
					lut[i] = Evaluate(t0 + (t1 - t0) * i / (size - 1));

				return lut;
			}

			var previous = lut[0];
			var previousIndex = 0;
			for (int s = 1; s < samples; s++)
			{
				var index = (int)((long)s * (size - 1) / (samples - 1));
				var color = Evaluate(t0 + (t1 - t0) * index / (size - 1));
				for (int i = previousIndex + 1; i <= index; i++)
					lut[i] = Lerp(previous, color, (double)(i - previousIndex) / (index - previousIndex));

				previous = color;
				previousIndex = index;
			}

			return lut;
		}

		// linear interpolation between two 0xRRGGBB colours.
		private static int Lerp(int a, int b, double t)
		{
			var r = (int)(((a >> 16) & 255) * (1 - t) + ((b >> 16) & 255) * t + 0.5);
			var g = (int)(((a >> 8) & 255) * (1 - t) + ((b >> 8) & 255) * t + 0.5);
			var bl = (int)((a & 255) * (1 - t) + (b & 255) * t + 0.5);
			return (r << 16) | (g << 8) | bl;
		}

		#region Implementation

		// reads the Domain of the parametric variable t: [0 1] by default.
		private static void GetDomain(PdfDictionary shading, out double t0, out double t1)
		{
			var domain = shading.GetArray(PdfNames.Domain)?.ToNumbers();
			t0 = domain != null && domain.Length >= 2 ? domain[0] : 0;
			t1 = domain != null && domain.Length >= 2 ? domain[1] : 1;
		}

		// reads the Extend flags that continue the shading beyond its start and end.
		private static void GetExtend(PdfDictionary shading, out bool extend0, out bool extend1)
		{
			var extend = shading.GetArray("Extend");
			extend0 = extend != null && extend.Count > 0 && extend.Resolve(0) is bool e0 && e0;
			extend1 = extend != null && extend.Count > 1 && extend.Resolve(1) is bool e1 && e1;
		}

		// creates the paint of an axial shading (type 2), or null when the shading is invalid.
		private static Paint CreateAxial(PdfDictionary shading, Matrix shadingToDevice, ColorSpace colorSpace, PdfFunction function)
		{
			var coords = shading.GetArray("Coords")?.ToNumbers();
			if (coords == null || coords.Length < 4 || function == null)
				return null;

			GetDomain(shading, out var t0, out var t1);
			GetExtend(shading, out var extend0, out var extend1);

			var dx = coords[2] - coords[0];
			var dy = coords[3] - coords[1];
			var length2 = dx * dx + dy * dy;
			if (!(length2 > 0))
				return null;

			// s = ((p - p0) . d) / |d|^2, as a linear function of the device coordinates.
			var inverse = shadingToDevice.Invert();
			var ka = (inverse.A * dx + inverse.B * dy) / length2;
			var kc = (inverse.C * dx + inverse.D * dy) / length2;
			var ke = ((inverse.E - coords[0]) * dx + (inverse.F - coords[1]) * dy) / length2;

			return new AxialPaint(ka, kc, ke, extend0, extend1, BuildLut(function, colorSpace, t0, t1, LutSize));
		}

		// creates the paint of a radial shading (type 3), or null when the shading is invalid.
		private static Paint CreateRadial(PdfDictionary shading, Matrix shadingToDevice, ColorSpace colorSpace, PdfFunction function)
		{
			var coords = shading.GetArray("Coords")?.ToNumbers();
			if (coords == null || coords.Length < 6 || function == null)
				return null;

			GetDomain(shading, out var t0, out var t1);
			GetExtend(shading, out var extend0, out var extend1);

			if (coords[2] < 0 || coords[5] < 0)
				return null;

			return new RadialPaint(shadingToDevice.Invert(), coords, extend0, extend1, BuildLut(function, colorSpace, t0, t1, LutSize));
		}

		// creates the paint of a function-based shading (type 1) by sampling the function on a grid, or null when invalid.
		private static Paint CreateFunctionBased(PdfDictionary shading, Matrix shadingToDevice, ColorSpace colorSpace, PdfFunction function)
		{
			if (function == null)
				return null;

			var domain = shading.GetArray(PdfNames.Domain)?.ToNumbers();
			if (domain == null || domain.Length < 4)
				domain = new double[] { 0, 1, 0, 1 };

			// domain -> device.
			var matrix = Matrix.FromArray(shading.GetArray(PdfNames.Matrix)?.ToNumbers()).Multiply(shadingToDevice);
			if (Math.Abs(matrix.Determinant) < 1e-12)
				return null;

			// sample the function on a grid fine enough for the device size, at most 256 x 256.
			var width = Math.Abs(domain[1] - domain[0]) * matrix.ScaleX;
			var height = Math.Abs(domain[3] - domain[2]) * matrix.ScaleY;
			var columns = (int)Math.Max(2, Math.Min(256, Math.Ceiling(width)));
			var rows = (int)Math.Max(2, Math.Min(256, Math.Ceiling(height)));

			var grid = new int[columns * rows];
			var input = new double[2];
			var output = new double[Math.Max(function.OutputCount, colorSpace.ComponentCount)];
			for (int r = 0; r < rows; r++)
			{
				input[1] = domain[2] + (domain[3] - domain[2]) * (r + 0.5) / rows;
				for (int c = 0; c < columns; c++)
				{
					input[0] = domain[0] + (domain[1] - domain[0]) * (c + 0.5) / columns;
					Array.Clear(output, 0, output.Length);
					try
					{
						function.Evaluate(input, output);
					}
					catch (Exception)
					{
						// keep the initial colour.
					}

					grid[r * columns + c] = colorSpace.ToRgb(output);
				}
			}

			return new FunctionPaint(matrix.Invert(), domain, columns, rows, grid);
		}

		#endregion

		#region Paints

		/// <summary>
		/// Paints an axial shading.
		/// </summary>
		private sealed class AxialPaint : Paint
		{
			// the parameter s along the axis as a linear function of the device coordinates: s = ka * x + kc * y + ke.
			private readonly double _ka, _kc, _ke;

			// the Extend flags.
			private readonly bool _extend0, _extend1;
			// the colour table for s from 0 to 1.
			private readonly int[] _lut;

			/// <summary>
			/// Initializes a new instance of <see cref="AxialPaint"/>.
			/// </summary>
			/// <param name="ka">The x coefficient of s.</param>
			/// <param name="kc">The y coefficient of s.</param>
			/// <param name="ke">The constant term of s.</param>
			/// <param name="extend0">True to extend the shading before the start.</param>
			/// <param name="extend1">True to extend the shading after the end.</param>
			/// <param name="lut">The colour table.</param>
			public AxialPaint(double ka, double kc, double ke, bool extend0, bool extend1, int[] lut)
			{
				_ka = ka;
				_kc = kc;
				_ke = ke;
				_extend0 = extend0;
				_extend1 = extend1;
				_lut = lut;
			}

			/// <inheritdoc/>
			public override void Shade(int y, int x0, int x1, byte[] span)
			{
				var s = _ka * (x0 + 0.5) + _kc * (y + 0.5) + _ke;
				var last = _lut.Length - 1;
				for (int x = x0, o = 0; x < x1; x++, o += 4, s += _ka)
				{
					int index;
					if (s < 0)
						index = _extend0 ? 0 : -1;
					else if (s > 1)
						index = _extend1 ? last : -1;
					else
						index = (int)(s * last + 0.5);

					WriteLut(span, o, _lut, index);
				}
			}
		}

		/// <summary>
		/// Paints a radial shading.
		/// </summary>
		private sealed class RadialPaint : Paint
		{
			// device space -> shading space.
			private readonly Matrix _inverse;

			// the start circle, the deltas to the end circle and the quadratic coefficient a.
			private readonly double _x0, _y0, _r0, _cdx, _cdy, _dr, _a;

			// the Extend flags.
			private readonly bool _extend0, _extend1;
			// the colour table for s from 0 to 1.
			private readonly int[] _lut;

			/// <summary>
			/// Initializes a new instance of <see cref="RadialPaint"/>.
			/// </summary>
			/// <param name="inverse">Maps device space to shading space.</param>
			/// <param name="coords">The circles: x0 y0 r0 x1 y1 r1.</param>
			/// <param name="extend0">True to extend the shading before the start circle.</param>
			/// <param name="extend1">True to extend the shading after the end circle.</param>
			/// <param name="lut">The colour table.</param>
			public RadialPaint(Matrix inverse, double[] coords, bool extend0, bool extend1, int[] lut)
			{
				_inverse = inverse;
				_x0 = coords[0];
				_y0 = coords[1];
				_r0 = coords[2];
				_cdx = coords[3] - coords[0];
				_cdy = coords[4] - coords[1];
				_dr = coords[5] - coords[2];
				_a = _cdx * _cdx + _cdy * _cdy - _dr * _dr;
				_extend0 = extend0;
				_extend1 = extend1;
				_lut = lut;
			}

			/// <inheritdoc/>
			public override void Shade(int y, int x0, int x1, byte[] span)
			{
				var m = _inverse;
				var px = m.A * (x0 + 0.5) + m.C * (y + 0.5) + m.E - _x0;
				var py = m.B * (x0 + 0.5) + m.D * (y + 0.5) + m.F - _y0;
				var last = _lut.Length - 1;

				for (int x = x0, o = 0; x < x1; x++, o += 4, px += m.A, py += m.B)
				{
					// solve |p - c(s)| = r(s) for the largest s with r(s) >= 0.
					var b = px * _cdx + py * _cdy + _r0 * _dr;
					var c = px * px + py * py - _r0 * _r0;
					var index = -1;

					if (Math.Abs(_a) < 1e-9)
					{
						if (b != 0)
							index = Select(c / (2 * b), -1, last);
					}
					else
					{
						var discriminant = b * b - _a * c;
						if (discriminant >= 0)
						{
							var root = Math.Sqrt(discriminant);
							var s1 = (b + root) / _a;
							var s2 = (b - root) / _a;
							index = Select(Math.Max(s1, s2), Math.Min(s1, s2), last);
						}
					}

					WriteLut(span, o, _lut, index);
				}
			}

			// the table index of the first valid solution, or -1.
			private int Select(double first, double second, int last)
			{
				var index = Valid(first, last);
				return index >= 0 ? index : Valid(second, last);
			}

			// the table index of a solution, or -1 when it is outside the shading.
			private int Valid(double s, int last)
			{
				if (double.IsNaN(s) || _r0 + s * _dr < 0)
					return -1;
				if (s < 0)
					return _extend0 ? 0 : -1;
				if (s > 1)
					return _extend1 ? last : -1;

				return (int)(s * last + 0.5);
			}
		}

		/// <summary>
		/// Paints a function-based shading sampled on a grid.
		/// </summary>
		private sealed class FunctionPaint : Paint
		{
			// device space -> domain space.
			private readonly Matrix _inverse;

			// the domain.
			private readonly double _x0, _x1, _y0, _y1;

			// the number of columns and rows of the grid.
			private readonly int _columns, _rows;
			// the colours sampled on the grid over the domain, row by row.
			private readonly int[] _grid;

			/// <summary>
			/// Initializes a new instance of <see cref="FunctionPaint"/>.
			/// </summary>
			/// <param name="inverse">Maps device space to the domain.</param>
			/// <param name="domain">The domain: x0 x1 y0 y1.</param>
			/// <param name="columns">The number of grid columns.</param>
			/// <param name="rows">The number of grid rows.</param>
			/// <param name="grid">The colours, row by row.</param>
			public FunctionPaint(Matrix inverse, double[] domain, int columns, int rows, int[] grid)
			{
				_inverse = inverse;
				_x0 = domain[0];
				_x1 = domain[1];
				_y0 = domain[2];
				_y1 = domain[3];
				_columns = columns;
				_rows = rows;
				_grid = grid;
			}

			/// <inheritdoc/>
			public override void Shade(int y, int x0, int x1, byte[] span)
			{
				var m = _inverse;
				var u = m.A * (x0 + 0.5) + m.C * (y + 0.5) + m.E;
				var v = m.B * (x0 + 0.5) + m.D * (y + 0.5) + m.F;
				var minX = Math.Min(_x0, _x1);
				var maxX = Math.Max(_x0, _x1);
				var minY = Math.Min(_y0, _y1);
				var maxY = Math.Max(_y0, _y1);

				for (int x = x0, o = 0; x < x1; x++, o += 4, u += m.A, v += m.B)
				{
					// outside the domain the shading paints nothing.
					if (u < minX || u > maxX || v < minY || v > maxY || double.IsNaN(u))
					{
						WriteLut(span, o, _grid, -1);
						continue;
					}

					var c = (int)((u - _x0) / (_x1 - _x0) * _columns);
					var r = (int)((v - _y0) / (_y1 - _y0) * _rows);
					c = c < 0 ? 0 : c >= _columns ? _columns - 1 : c;
					r = r < 0 ? 0 : r >= _rows ? _rows - 1 : r;
					WriteLut(span, o, _grid, r * _columns + c);
				}
			}
		}

		// writes an opaque colour of the table, or a transparent pixel for index -1.
		private static void WriteLut(byte[] span, int offset, int[] lut, int index)
		{
			if (index < 0)
			{
				span[offset] = 0;
				span[offset + 1] = 0;
				span[offset + 2] = 0;
				span[offset + 3] = 0;
				return;
			}

			var color = lut[index];
			span[offset] = (byte)(color >> 16);
			span[offset + 1] = (byte)(color >> 8);
			span[offset + 2] = (byte)color;
			span[offset + 3] = 255;
		}

		#endregion
	}

	/// <summary>
	/// Renders mesh shadings (types 4 to 7) into a layer with Gouraud-shaded triangles.
	/// </summary>
	internal sealed class MeshShading
	{
		// the most vertices decoded, a bound for malformed data.
		private const int MaxVertices = 2_000_000;
		// the most subdivisions of each side of a patch.
		private const int MaxPatchDivisions = 48;
		// the most patches decoded.
		private const int MaxPatches = 100_000;

		// the x coordinates of the vertices in device space.
		private readonly List<double> _x = new List<double>();
		// the y coordinates of the vertices in device space.
		private readonly List<double> _y = new List<double>();
		// the red component of the vertices, or the function parameter t of the parametric meshes.
		private readonly List<float> _c0 = new List<float>();
		// the green component of the vertices: 0 in the parametric meshes.
		private readonly List<float> _c1 = new List<float>();
		// the blue component of the vertices: 0 in the parametric meshes.
		private readonly List<float> _c2 = new List<float>();

		// the triangles, three vertex indices each.
		private readonly List<int> _triangles = new List<int>();

		// shading space -> device space.
		private Matrix _matrix;
		// the colour space of the vertex colours.
		private ColorSpace _colorSpace;
		// the function of the parametric meshes, or null.
		private PdfFunction _function;
		// the colour table of the parametric meshes over [_tMin, _tMax].
		private int[] _lut;
		// the range of the function parameter t covered by _lut.
		private double _tMin, _tMax;

		// the shading stream data.
		private byte[] _data;
		// the position of the next bit to read in _data.
		private long _bit;
		// the sizes in bits of the coordinates, the colour components and the edge flags.
		private int _bitsPerCoordinate, _bitsPerComponent, _bitsPerFlag;
		// the Decode ranges of the coordinates and of the colour components.
		private double[] _decode;
		// the number of colour components of each vertex: 1 in the parametric meshes.
		private int _components;
		// the colour components of the vertex being read.
		private double[] _componentBuffer;

		// the RGBA pixels of the layer.
		private byte[] _pixels;
		// the bounds of the layer in device space.
		private int _left, _top, _width, _height;

		/// <summary>
		/// Decodes and renders a mesh shading.
		/// </summary>
		/// <param name="stream">The shading stream.</param>
		/// <param name="shadingToDevice">Maps the shading space to device space.</param>
		/// <param name="clip">The clipping region: only the part inside its bounds is rendered.</param>
		/// <param name="colorSpace">The colour space.</param>
		/// <param name="function">The function of the parametric meshes, or null.</param>
		/// <returns>The paint, or null.</returns>
		public static Paint Render(PdfStream stream, Matrix shadingToDevice, ClipRegion clip, ColorSpace colorSpace, PdfFunction function)
		{
			if (clip.IsEmpty)
				return null;

			var mesh = new MeshShading
			{
				_matrix = shadingToDevice,
				_colorSpace = colorSpace,
				_function = function,
				_left = clip.Left,
				_top = clip.Top,
				_width = clip.Right - clip.Left,
				_height = clip.Bottom - clip.Top,
			};

			try
			{
				if (!mesh.Decode(stream))
					return null;
			}
			catch (Exception)
			{
				// render what was decoded before the error.
			}

			mesh._pixels = new byte[(long)mesh._width * mesh._height * 4 > int.MaxValue ? 0 : mesh._width * mesh._height * 4];
			if (mesh._pixels.Length == 0)
				return null;

			mesh.RenderTriangles();
			return new LayerPaint(new Bitmap(mesh._width, mesh._height, mesh._pixels), mesh._left, mesh._top);
		}

		#region Decoding

		// reads the stream parameters and decodes the vertices and triangles; false when the parameters are invalid.
		private bool Decode(PdfStream stream)
		{
			_data = stream.GetBytes();
			_bitsPerCoordinate = stream.GetInt("BitsPerCoordinate");
			_bitsPerComponent = stream.GetInt("BitsPerComponent");
			_bitsPerFlag = stream.GetInt("BitsPerFlag");
			_decode = stream.GetArray(PdfNames.Decode)?.ToNumbers();
			_components = _function != null ? 1 : _colorSpace.ComponentCount;
			_componentBuffer = new double[Math.Max(_components, _colorSpace.ComponentCount)];

			var type = stream.GetInt("ShadingType");
			if (_bitsPerCoordinate < 1 || _bitsPerCoordinate > 32 || _bitsPerComponent < 1 || _bitsPerComponent > 16
				|| _decode == null || _decode.Length < 4 + 2 * _components)
				return false;

			if (type != 5 && (_bitsPerFlag < 1 || _bitsPerFlag > 8))
				return false;

			if (_function != null)
			{
				_tMin = _decode[4];
				_tMax = _decode[5];
				_lut = Shadings.BuildLut(_function, _colorSpace, _tMin, _tMax, 256);
			}

			switch (type)
			{
				case 4:
					DecodeFreeForm();
					break;

				case 5:
					DecodeLattice(Math.Max(2, stream.GetInt("VerticesPerRow", 2)));
					break;

				default:
					DecodePatches(type == 7);
					break;
			}

			return true;
		}

		// true when the stream has at least the given number of bits left.
		private bool HasData(int bits) => _bit + bits <= (long)_data.Length * 8;

		// reads an unsigned value of the given number of bits, most significant first; bits past the end are 0.
		private uint ReadBits(int count)
		{
			uint value = 0;
			for (int i = 0; i < count; i++)
			{
				var index = _bit >> 3;
				var bit = index < _data.Length ? (_data[index] >> (7 - (int)(_bit & 7))) & 1 : 0;
				value = (value << 1) | (uint)bit;
				_bit++;
			}

			return value;
		}

		// moves the bit position to the next byte boundary.
		private void Align() => _bit = (_bit + 7) & ~7L;

		// reads a value of the given number of bits and maps it linearly to [min, max].
		private double ReadValue(int bits, double min, double max)
		{
			var raw = ReadBits(bits);
			var scale = bits >= 32 ? uint.MaxValue : (double)((1L << bits) - 1);
			return min + raw * (max - min) / scale;
		}

		// reads a point and maps it to device space.
		private void ReadPoint(out double x, out double y)
		{
			var px = ReadValue(_bitsPerCoordinate, _decode[0], _decode[1]);
			var py = ReadValue(_bitsPerCoordinate, _decode[2], _decode[3]);
			x = _matrix.A * px + _matrix.C * py + _matrix.E;
			y = _matrix.B * px + _matrix.D * py + _matrix.F;
		}

		// reads a colour: the function parameter, or the colour converted to RGB.
		private void ReadColor(out float c0, out float c1, out float c2)
		{
			for (int i = 0; i < _components; i++)
				_componentBuffer[i] = ReadValue(_bitsPerComponent, _decode[4 + i * 2], _decode[5 + i * 2]);

			if (_function != null)
			{
				c0 = (float)_componentBuffer[0];
				c1 = c2 = 0;
				return;
			}

			var rgb = _colorSpace.ToRgb(_componentBuffer);
			c0 = (rgb >> 16) & 255;
			c1 = (rgb >> 8) & 255;
			c2 = rgb & 255;
		}

		// appends a vertex and returns its index.
		private int AddVertex(double x, double y, float c0, float c1, float c2)
		{
			_x.Add(x);
			_y.Add(y);
			_c0.Add(c0);
			_c1.Add(c1);
			_c2.Add(c2);
			return _x.Count - 1;
		}

		// the size in bits of a vertex: the coordinates and the colour components.
		private int VertexBits => _bitsPerCoordinate * 2 + _bitsPerComponent * _components;

		// type 4: each vertex has an edge flag that tells how it forms a triangle with the previous ones.
		private void DecodeFreeForm()
		{
			var order = new List<int>();
			var left = 0;
			while (HasData(_bitsPerFlag + VertexBits) && _x.Count < MaxVertices)
			{
				var flag = (int)ReadBits(_bitsPerFlag);
				ReadPoint(out var x, out var y);
				ReadColor(out var c0, out var c1, out var c2);
				Align();

				if (left == 0)
				{
					if (flag == 0)
					{
						left = 3;
					}
					else if (order.Count >= 3 && (flag == 1 || flag == 2))
					{
						// f = 1 continues from the last edge (vb, vc), f = 2 from (va, vc).
						var count = order.Count;
						var first = flag == 1 ? order[count - 2] : order[count - 3];
						var second = order[count - 1];
						order.Add(first);
						order.Add(second);
						left = 1;
					}
					else
					{
						continue;
					}
				}

				order.Add(AddVertex(x, y, c0, c1, c2));
				left--;
			}

			for (int i = 0; i + 2 < order.Count; i += 3)
			{
				_triangles.Add(order[i]);
				_triangles.Add(order[i + 1]);
				_triangles.Add(order[i + 2]);
			}
		}

		// type 5: rows of vertices; adjacent rows form a grid of quadrilaterals split in two triangles.
		private void DecodeLattice(int perRow)
		{
			while (HasData(VertexBits) && _x.Count < MaxVertices)
			{
				ReadPoint(out var x, out var y);
				ReadColor(out var c0, out var c1, out var c2);
				AddVertex(x, y, c0, c1, c2);
			}

			var rows = _x.Count / perRow;
			for (int r = 0; r + 1 < rows; r++)
			{
				for (int c = 0; c + 1 < perRow; c++)
				{
					var v00 = r * perRow + c;
					var v01 = v00 + 1;
					var v10 = v00 + perRow;
					var v11 = v10 + 1;
					_triangles.Add(v00);
					_triangles.Add(v01);
					_triangles.Add(v10);
					_triangles.Add(v01);
					_triangles.Add(v11);
					_triangles.Add(v10);
				}
			}
		}

		// types 6 and 7: Coons and tensor-product patches, subdivided in triangles.
		private void DecodePatches(bool tensor)
		{
			// control points p[i * 4 + j], i along u and j along v, and the corner colours c00, c03, c33, c30.
			var px = new double[16];
			var py = new double[16];
			var colors = new float[4, 3];
			// the boundary in order: p00 p01 p02 p03 p13 p23 p33 p32 p31 p30 p20 p10.
			var boundary = new[] { 0, 1, 2, 3, 7, 11, 15, 14, 13, 12, 8, 4 };
			var hasPrevious = false;
			var patches = 0;
			var list = new List<double[]>();

			while (HasData(_bitsPerFlag) && _x.Count < MaxVertices)
			{
				var flag = (int)ReadBits(_bitsPerFlag);
				var points = (tensor ? 16 : 12) - (flag == 0 ? 0 : 4);
				var colorCount = flag == 0 ? 4 : 2;
				if (!HasData(points * 2 * _bitsPerCoordinate + colorCount * _components * _bitsPerComponent))
					break;

				if (flag != 0)
				{
					if (!hasPrevious)
						break;

					// the shared edge of the previous patch becomes the left edge (u = 0) of this one.
					int a, b, c, d, ca, cb;
					switch (flag)
					{
						case 1: a = 3; b = 7; c = 11; d = 15; ca = 1; cb = 2; break;
						case 2: a = 15; b = 14; c = 13; d = 12; ca = 2; cb = 3; break;
						default: a = 12; b = 8; c = 4; d = 0; ca = 3; cb = 0; break;
					}

					double ax = px[a], ay = py[a], bx = px[b], by = py[b], cx = px[c], cy = py[c], dx = px[d], dy = py[d];
					float r0 = colors[ca, 0], g0 = colors[ca, 1], b0 = colors[ca, 2];
					float r1 = colors[cb, 0], g1 = colors[cb, 1], b1 = colors[cb, 2];
					px[0] = ax; py[0] = ay;
					px[1] = bx; py[1] = by;
					px[2] = cx; py[2] = cy;
					px[3] = dx; py[3] = dy;
					colors[0, 0] = r0; colors[0, 1] = g0; colors[0, 2] = b0;
					colors[1, 0] = r1; colors[1, 1] = g1; colors[1, 2] = b1;
				}

				for (int i = flag == 0 ? 0 : 4; i < 12; i++)
					ReadPoint(out px[boundary[i]], out py[boundary[i]]);

				if (tensor)
				{
					// interior points: p11 p12 p22 p21.
					ReadPoint(out px[5], out py[5]);
					ReadPoint(out px[6], out py[6]);
					ReadPoint(out px[10], out py[10]);
					ReadPoint(out px[9], out py[9]);
				}
				else
				{
					ComputeCoonsInterior(px);
					ComputeCoonsInterior(py);
				}

				for (int i = flag == 0 ? 0 : 2; i < 4; i++)
				{
					ReadColor(out var c0, out var c1, out var c2);
					colors[i, 0] = c0;
					colors[i, 1] = c1;
					colors[i, 2] = c2;
				}

				hasPrevious = true;
				if (++patches > MaxPatches)
					break;

				// keep the patch: control points, then the corner colours.
				var patch = new double[44];
				Array.Copy(px, 0, patch, 0, 16);
				Array.Copy(py, 0, patch, 16, 16);
				for (int i = 0; i < 4; i++)
				{
					patch[32 + i * 3] = colors[i, 0];
					patch[33 + i * 3] = colors[i, 1];
					patch[34 + i * 3] = colors[i, 2];
				}

				list.Add(patch);
			}

			// all the patches use the same subdivision, so the shared edges have the same vertices and no cracks.
			var size = 0.0;
			foreach (var patch in list)
				size = Math.Max(size, GetPatchSize(patch));

			var n = (int)Math.Max(1, Math.Min(MaxPatchDivisions, Math.Ceiling(size / 4)));
			while (n > 1 && (long)list.Count * (n + 1) * (n + 1) > MaxVertices)
				n--;

			foreach (var patch in list)
				SubdividePatch(patch, n);
		}

		// the largest side of the bounding box of the control points.
		private static double GetPatchSize(double[] patch)
		{
			double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
			for (int i = 0; i < 16; i++)
			{
				minX = Math.Min(minX, patch[i]);
				maxX = Math.Max(maxX, patch[i]);
				minY = Math.Min(minY, patch[16 + i]);
				maxY = Math.Max(maxY, patch[16 + i]);
			}

			var size = Math.Max(maxX - minX, maxY - minY);
			return double.IsNaN(size) || double.IsInfinity(size) ? 0 : size;
		}

		// the interior control points of a Coons patch as an equivalent tensor-product patch.
		private static void ComputeCoonsInterior(double[] p)
		{
			p[5] = (-4 * p[0] + 6 * (p[1] + p[4]) - 2 * (p[3] + p[12]) + 3 * (p[13] + p[7]) - p[15]) / 9;
			p[6] = (-4 * p[3] + 6 * (p[2] + p[7]) - 2 * (p[0] + p[15]) + 3 * (p[14] + p[4]) - p[12]) / 9;
			p[9] = (-4 * p[12] + 6 * (p[13] + p[8]) - 2 * (p[15] + p[0]) + 3 * (p[1] + p[11]) - p[3]) / 9;
			p[10] = (-4 * p[15] + 6 * (p[14] + p[11]) - 2 * (p[12] + p[3]) + 3 * (p[2] + p[8]) - p[0]) / 9;
		}

		// evaluates the patch on an n x n grid and adds the triangles.
		private void SubdividePatch(double[] patch, int n)
		{
			double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
			for (int i = 0; i < 16; i++)
			{
				minX = Math.Min(minX, patch[i]);
				maxX = Math.Max(maxX, patch[i]);
				minY = Math.Min(minY, patch[16 + i]);
				maxY = Math.Max(maxY, patch[16 + i]);
			}

			// the control points enclose the patch: skip the patches outside the layer.
			if (maxX < _left || minX > _left + _width || maxY < _top || minY > _top + _height || double.IsNaN(minX))
				return;

			var first = _x.Count;
			var bu = new double[4];
			var bv = new double[4];
			for (int j = 0; j <= n; j++)
			{
				var v = (double)j / n;
				Bernstein(v, bv);
				for (int i = 0; i <= n; i++)
				{
					var u = (double)i / n;
					Bernstein(u, bu);

					double x = 0, y = 0;
					for (int a = 0; a < 4; a++)
					{
						for (int b = 0; b < 4; b++)
						{
							var w = bu[a] * bv[b];
							x += patch[a * 4 + b] * w;
							y += patch[16 + a * 4 + b] * w;
						}
					}

					// bilinear colour between the corners c00 (u0 v0), c03 (u0 v1), c33 (u1 v1) and c30 (u1 v0).
					var w00 = (1 - u) * (1 - v);
					var w03 = (1 - u) * v;
					var w33 = u * v;
					var w30 = u * (1 - v);
					AddVertex(x, y,
						(float)(patch[32] * w00 + patch[35] * w03 + patch[38] * w33 + patch[41] * w30),
						(float)(patch[33] * w00 + patch[36] * w03 + patch[39] * w33 + patch[42] * w30),
						(float)(patch[34] * w00 + patch[37] * w03 + patch[40] * w33 + patch[43] * w30));
				}
			}

			for (int j = 0; j < n; j++)
			{
				for (int i = 0; i < n; i++)
				{
					var v00 = first + j * (n + 1) + i;
					var v01 = v00 + 1;
					var v10 = v00 + n + 1;
					var v11 = v10 + 1;
					_triangles.Add(v00);
					_triangles.Add(v01);
					_triangles.Add(v10);
					_triangles.Add(v01);
					_triangles.Add(v11);
					_triangles.Add(v10);
				}
			}
		}

		// computes the four cubic Bernstein polynomials at t.
		private static void Bernstein(double t, double[] b)
		{
			var s = 1 - t;
			b[0] = s * s * s;
			b[1] = 3 * t * s * s;
			b[2] = 3 * t * t * s;
			b[3] = t * t * t;
		}

		#endregion

		#region Rendering

		// fills all the triangles of the mesh.
		private void RenderTriangles()
		{
			for (int i = 0; i + 2 < _triangles.Count; i += 3)
				FillTriangle(_triangles[i], _triangles[i + 1], _triangles[i + 2]);
		}

		// fills the pixels whose centre is inside the triangle, interpolating the colours (Gouraud shading).
		private void FillTriangle(int a, int b, int c)
		{
			double ax = _x[a] - _left, ay = _y[a] - _top;
			double bx = _x[b] - _left, by = _y[b] - _top;
			double cx = _x[c] - _left, cy = _y[c] - _top;

			var area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
			if (Math.Abs(area) < 1e-12 || double.IsNaN(area))
				return;

			var x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
			var x1 = Math.Min(_width - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
			var y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
			var y1 = Math.Min(_height - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
			if (x0 > x1 || y0 > y1)
				return;

			float ar = _c0[a], ag = _c1[a], ab = _c2[a];
			float br = _c0[b], bg = _c1[b], bb = _c2[b];
			float cr = _c0[c], cg = _c1[c], cb = _c2[c];

			// a tiny tolerance makes adjacent triangles overlap by a hair, so no seams appear between them.
			const double Epsilon = -1e-7;
			var inverseArea = 1 / area;
			for (int y = y0; y <= y1; y++)
			{
				var py = y + 0.5;
				var row = y * _width;
				for (int x = x0; x <= x1; x++)
				{
					var px = x + 0.5;
					var wa = ((bx - px) * (cy - py) - (cx - px) * (by - py)) * inverseArea;
					var wb = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) * inverseArea;
					var wc = 1 - wa - wb;
					if (wa < Epsilon || wb < Epsilon || wc < Epsilon)
						continue;

					int r, g, bl;
					if (_lut != null)
					{
						var t = wa * ar + wb * br + wc * cr;
						var index = _tMax != _tMin ? (int)((t - _tMin) / (_tMax - _tMin) * 255 + 0.5) : 0;
						index = index < 0 ? 0 : index > 255 ? 255 : index;
						var color = _lut[index];
						r = (color >> 16) & 255;
						g = (color >> 8) & 255;
						bl = color & 255;
					}
					else
					{
						r = (int)(wa * ar + wb * br + wc * cr + 0.5);
						g = (int)(wa * ag + wb * bg + wc * cg + 0.5);
						bl = (int)(wa * ab + wb * bb + wc * cb + 0.5);
					}

					var o = (row + x) * 4;
					_pixels[o] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
					_pixels[o + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
					_pixels[o + 2] = (byte)(bl < 0 ? 0 : bl > 255 ? 255 : bl);
					_pixels[o + 3] = 255;
				}
			}
		}

		#endregion
	}
}
