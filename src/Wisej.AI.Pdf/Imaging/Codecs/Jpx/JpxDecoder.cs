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

// JPEG 2000 decoder for the JPXDecode filter: JP2 file format boxes (ITU-T T.800, Annex I) and
// conversion of the decoded components to interleaved 8-bit channels (ISO 32000-2, 7.4.9).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Decodes JPEG 2000 images, either raw codestreams or JP2/JPX files.
	/// </summary>
	/// <remarks>
	/// The decoder is fully managed and supports ITU-T T.800 (Part 1): all progression orders and POC
	/// markers, precincts, tiles and tile-parts, packed packet headers (PPM/PPT), every code-block style,
	/// the 5/3 and 9/7 wavelets, RCT/ICT, all quantization styles, ROI maxshift, sub-sampled components,
	/// precisions up to 16 bits, palettes and channel definitions. Truncated codestreams decode as far
	/// as the data goes.
	/// </remarks>
	internal static class JpxDecoder
	{
		#region Nested types

		/// <summary>
		/// The metadata of a JP2 file (T.800, Annex I).
		/// </summary>
		private sealed class FileInfo
		{
			// the codestream location.

			/// <summary>The offset of the codestream in the data, or -1 when none was found.</summary>
			public int CodestreamOffset = -1;

			/// <summary>The length of the codestream, in bytes.</summary>
			public int CodestreamLength;

			// the colour specification box (I.5.3.3).

			/// <summary>True when a usable colour specification box was found.</summary>
			public bool HasColor;

			/// <summary>The specification method: 1 = enumerated, 2 = restricted ICC, 3 = any ICC.</summary>
			public int ColorMethod;

			/// <summary>The enumerated colour space code (EnumCS), when <see cref="ColorMethod"/> is 1.</summary>
			public int EnumeratedColorSpace;

			/// <summary>The embedded ICC profile, when <see cref="ColorMethod"/> is 2 or 3.</summary>
			public byte[] IccProfile;

			// the palette box (I.5.3.4).

			/// <summary>The number of palette entries.</summary>
			public int PaletteEntries;

			/// <summary>The bit depth of each palette column.</summary>
			public int[] PaletteDepths;

			/// <summary>Whether each palette column holds signed values.</summary>
			public bool[] PaletteSigned;

			/// <summary>The entries of each palette column, offset to the unsigned range.</summary>
			public int[][] PaletteValues;

			// the component mapping box (I.5.3.5).

			/// <summary>The codestream component of each channel.</summary>
			public int[] MapComponents;

			/// <summary>The mapping type of each channel: 0 = direct use, 1 = palette mapping.</summary>
			public int[] MapTypes;

			/// <summary>The palette column of each channel, when its mapping type is 1.</summary>
			public int[] MapColumns;

			// the channel definition box (I.5.3.6).

			/// <summary>The channel index of each definition.</summary>
			public int[] DefinitionChannels;

			/// <summary>The channel type of each definition: 0 = colour, 1 = opacity, 2 = premultiplied opacity.</summary>
			public int[] DefinitionTypes;

			/// <summary>The colour association of each definition: 0 = whole image, 1..n = colour index, 65535 = none.</summary>
			public int[] DefinitionAssociations;
		}

		/// <summary>
		/// One output channel: a component, optionally looked up in a palette column.
		/// </summary>
		private struct Channel
		{
			/// <summary>The index of the codestream component.</summary>
			public int Component;

			/// <summary>The palette column that maps the component samples, or -1 for direct use.</summary>
			public int PaletteColumn;
		}

		#endregion

		#region Constants

		// box types (T.800, Table I.2).

		/// <summary>The JP2 header super box ('jp2h').</summary>
		private const int BoxJp2Header = 0x6A703268;

		/// <summary>The contiguous codestream box ('jp2c').</summary>
		private const int BoxCodestream = 0x6A703263;

		/// <summary>The colour specification box ('colr').</summary>
		private const int BoxColor = 0x636F6C72;

		/// <summary>The palette box ('pclr').</summary>
		private const int BoxPalette = 0x70636C72;

		/// <summary>The component mapping box ('cmap').</summary>
		private const int BoxComponentMapping = 0x636D6170;

		/// <summary>The channel definition box ('cdef').</summary>
		private const int BoxChannelDefinition = 0x63646566;

		/// <summary>The largest number of output bytes the decoder agrees to allocate.</summary>
		private const long MaxOutputBytes = 1L << 30;

		#endregion

		#region Methods

		/// <summary>
		/// Decodes a JPEG 2000 codestream or JP2 file.
		/// </summary>
		/// <param name="data">The encoded data.</param>
		/// <param name="reduce">The number of highest resolution levels to discard (0 = full size); allows fast thumbnails.</param>
		/// <returns>The decoded image, or null when the headers can't be parsed.</returns>
		public static JpxImage Decode(byte[] data, int reduce = 0)
		{
			if (data == null || data.Length < 4)
				return null;

			try
			{
				var file = ParseFile(data);
				if (file == null)
					return null;

				var codestream = JpxCodestream.Parse(data, file.CodestreamOffset, file.CodestreamLength, false);
				if (codestream == null)
					return null;

				return DecodeCodestream(codestream, file, Math.Max(0, reduce));
			}
			catch (OutOfMemoryException)
			{
				return null;
			}
			catch (Exception)
			{
				// the decoder must never throw on malformed data.
				return null;
			}
		}

		/// <summary>
		/// Reads the image size and format without decoding the image.
		/// </summary>
		/// <param name="data">The encoded data.</param>
		/// <param name="width">Receives the image width.</param>
		/// <param name="height">Receives the image height.</param>
		/// <param name="components">Receives the number of output channels, after palette expansion.</param>
		/// <param name="bitsPerComponent">Receives the largest original channel precision.</param>
		/// <param name="colorSpace">Receives the colour space declared by the JP2 header.</param>
		/// <returns>True when the headers could be parsed.</returns>
		public static bool TryReadHeader(byte[] data, out int width, out int height, out int components, out int bitsPerComponent, out JpxColorSpace colorSpace)
		{
			width = 0;
			height = 0;
			components = 0;
			bitsPerComponent = 0;
			colorSpace = JpxColorSpace.Unknown;

			if (data == null || data.Length < 4)
				return false;

			try
			{
				var file = ParseFile(data);
				if (file == null)
					return false;

				var codestream = JpxCodestream.Parse(data, file.CodestreamOffset, file.CodestreamLength, true);
				if (codestream == null || codestream.Components == null)
					return false;

				width = codestream.Xsiz - codestream.XOsiz;
				height = codestream.Ysiz - codestream.YOsiz;

				var channels = GetChannels(file, codestream);
				components = channels.Length;
				foreach (var channel in channels)
				{
					var depth = channel.PaletteColumn >= 0 ? file.PaletteDepths[channel.PaletteColumn] : codestream.Components[channel.Component].Precision;
					bitsPerComponent = Math.Max(bitsPerComponent, depth);
				}

				colorSpace = GetColorSpace(file, out _);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		#endregion

		#region Implementation: file format

		// reads a big-endian 16-bit value.
		private static int U16(byte[] data, int position)
		{
			return (data[position] << 8) | data[position + 1];
		}

		// reads a big-endian 32-bit value.
		private static int U32(byte[] data, int position)
		{
			return (data[position] << 24) | (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3];
		}

		// locates the codestream and reads the JP2 metadata; returns null when there is no codestream.
		private static FileInfo ParseFile(byte[] data)
		{
			var file = new FileInfo();

			// a raw codestream starts with SOC.
			if (data[0] == 0xFF && data[1] == 0x4F)
			{
				file.CodestreamOffset = 0;
				file.CodestreamLength = data.Length;
				return file;
			}

			ParseBoxes(data, 0, data.Length, file, 0);

			if (file.CodestreamOffset < 0)
			{
				// no usable jp2c box (broken or unusual wrappers): look for the SOC and SIZ markers.
				for (int i = 0; i + 4 <= data.Length; i++)
				{
					if (data[i] == 0xFF && data[i + 1] == 0x4F && data[i + 2] == 0xFF && data[i + 3] == 0x51)
					{
						file.CodestreamOffset = i;
						file.CodestreamLength = data.Length - i;
						break;
					}
				}
			}

			return file.CodestreamOffset >= 0 ? file : null;
		}

		// walks a sequence of boxes (I.4), descending into the JP2 header super box.
		private static void ParseBoxes(byte[] data, int start, int end, FileInfo file, int depth)
		{
			var position = start;
			while (position + 8 <= end)
			{
				long length = (uint)U32(data, position);
				var type = U32(data, position + 4);
				var headerLength = 8;

				if (length == 1)
				{
					// the extended 64-bit length follows the type.
					if (position + 16 > end)
						break;

					length = ((long)(uint)U32(data, position + 8) << 32) | (uint)U32(data, position + 12);
					headerLength = 16;
				}
				else if (length == 0)
				{
					// the box runs to the end of the file.
					length = end - position;
				}

				// a truncated last box is clamped to the available data.
				if (length < headerLength || length > end - position)
					length = end - position;

				var contentStart = position + headerLength;
				var contentEnd = (int)(position + length);

				switch (type)
				{
					case BoxJp2Header:
						if (depth < 4)
							ParseBoxes(data, contentStart, contentEnd, file, depth + 1);
						break;

					case BoxCodestream:
						if (file.CodestreamOffset < 0 && contentEnd - contentStart >= 4)
						{
							file.CodestreamOffset = contentStart;
							file.CodestreamLength = contentEnd - contentStart;
						}
						break;

					case BoxColor:
						ParseColor(data, contentStart, contentEnd, file);
						break;

					case BoxPalette:
						ParsePalette(data, contentStart, contentEnd, file);
						break;

					case BoxComponentMapping:
						ParseComponentMapping(data, contentStart, contentEnd, file);
						break;

					case BoxChannelDefinition:
						ParseChannelDefinition(data, contentStart, contentEnd, file);
						break;

					// the resolution box and anything else are ignored.
					default:
						break;
				}

				if (length <= 0)
					break;

				position = contentEnd;
			}
		}

		// parses the colour specification box (I.5.3.3); only the first usable one counts.
		private static void ParseColor(byte[] data, int start, int end, FileInfo file)
		{
			if (file.HasColor || end - start < 3)
				return;

			var method = data[start];
			if (method == 1 && end - start >= 7)
			{
				file.HasColor = true;
				file.ColorMethod = 1;
				file.EnumeratedColorSpace = U32(data, start + 3);
			}
			else if ((method == 2 || method == 3) && end - start > 3)
			{
				file.HasColor = true;
				file.ColorMethod = method;
				file.IccProfile = new byte[end - start - 3];
				Buffer.BlockCopy(data, start + 3, file.IccProfile, 0, file.IccProfile.Length);
			}
		}

		// parses the palette box (I.5.3.4).
		private static void ParsePalette(byte[] data, int start, int end, FileInfo file)
		{
			if (file.PaletteValues != null || end - start < 3)
				return;

			var entries = U16(data, start);
			var columns = data[start + 2];
			if (entries < 1 || entries > 1024 || columns < 1 || start + 3 + columns > end)
				return;

			var depths = new int[columns];
			var signed = new bool[columns];
			var values = new int[columns][];
			for (int i = 0; i < columns; i++)
			{
				var b = data[start + 3 + i];
				depths[i] = Math.Min((b & 0x7F) + 1, 32);
				signed[i] = (b & 0x80) != 0;
				values[i] = new int[entries];
			}

			var position = start + 3 + columns;
			for (int e = 0; e < entries; e++)
			{
				for (int i = 0; i < columns; i++)
				{
					var size = (depths[i] + 7) >> 3;
					if (position + size > end)
						return;

					long value = 0;
					for (int k = 0; k < size; k++)
						value = (value << 8) | data[position + k];
					position += size;

					// keep the meaningful bits; signed entries are offset to the unsigned range.
					value &= (1L << depths[i]) - 1;
					if (signed[i])
						value ^= 1L << (depths[i] - 1);

					values[i][e] = (int)Math.Min(value, int.MaxValue);
				}
			}

			file.PaletteEntries = entries;
			file.PaletteDepths = depths;
			file.PaletteSigned = signed;
			file.PaletteValues = values;
		}

		// parses the component mapping box (I.5.3.5).
		private static void ParseComponentMapping(byte[] data, int start, int end, FileInfo file)
		{
			if (file.MapComponents != null)
				return;

			var count = (end - start) / 4;
			file.MapComponents = new int[count];
			file.MapTypes = new int[count];
			file.MapColumns = new int[count];
			for (int i = 0; i < count; i++)
			{
				var position = start + i * 4;
				file.MapComponents[i] = U16(data, position);
				file.MapTypes[i] = data[position + 2];
				file.MapColumns[i] = data[position + 3];
			}
		}

		// parses the channel definition box (I.5.3.6).
		private static void ParseChannelDefinition(byte[] data, int start, int end, FileInfo file)
		{
			if (file.DefinitionChannels != null || end - start < 2)
				return;

			var count = Math.Min(U16(data, start), (end - start - 2) / 6);
			file.DefinitionChannels = new int[count];
			file.DefinitionTypes = new int[count];
			file.DefinitionAssociations = new int[count];
			for (int i = 0; i < count; i++)
			{
				var position = start + 2 + i * 6;
				file.DefinitionChannels[i] = U16(data, position);
				file.DefinitionTypes[i] = U16(data, position + 2);
				file.DefinitionAssociations[i] = U16(data, position + 4);
			}
		}

		// returns the declared colour space; sycc is true for sYCC data that must be converted to RGB.
		private static JpxColorSpace GetColorSpace(FileInfo file, out bool sycc)
		{
			sycc = false;
			if (!file.HasColor)
				return JpxColorSpace.Unknown;

			if (file.ColorMethod != 1)
				return JpxColorSpace.Icc;

			switch (file.EnumeratedColorSpace)
			{
				case 16:
					return JpxColorSpace.Rgb;

				case 17:
					return JpxColorSpace.Gray;

				case 18:
					sycc = true;
					return JpxColorSpace.Rgb;

				case 12:
					return JpxColorSpace.Cmyk;

				case 14:
					return JpxColorSpace.Lab;

				// YCbCr(1), YCbCr(2), YCbCr(3) and PhotoYCC: reported, not converted.
				case 1:
				case 3:
				case 4:
				case 9:
					return JpxColorSpace.Ycc;

				default:
					return JpxColorSpace.Unknown;
			}
		}

		// returns the output channels: the components, or the palette columns selected by the component mapping.
		private static Channel[] GetChannels(FileInfo file, JpxCodestream codestream)
		{
			var componentCount = codestream.Components.Length;
			var channels = new List<Channel>();

			if (file.MapComponents != null && file.MapComponents.Length > 0)
			{
				for (int i = 0; i < file.MapComponents.Length; i++)
				{
					var component = file.MapComponents[i];
					if (component >= componentCount)
						continue;

					var column = -1;
					if (file.MapTypes[i] == 1 && file.PaletteValues != null)
					{
						column = file.MapColumns[i];
						if (column >= file.PaletteValues.Length)
							continue;
					}

					channels.Add(new Channel { Component = component, PaletteColumn = column });
				}
			}
			else if (file.PaletteValues != null)
			{
				// a palette without a component mapping (invalid): apply every column to the first component.
				for (int i = 0; i < file.PaletteValues.Length; i++)
					channels.Add(new Channel { Component = 0, PaletteColumn = i });
			}

			if (channels.Count == 0)
			{
				for (int c = 0; c < componentCount; c++)
					channels.Add(new Channel { Component = c, PaletteColumn = -1 });
			}

			return channels.ToArray();
		}

		#endregion

		#region Implementation: decoding

		// ceil(value / 2^exponent) for non-negative values.
		private static int CeilShift(int value, int exponent)
		{
			if (exponent >= 31)
				return value > 0 ? 1 : 0;

			return (int)(((long)value + (1L << exponent) - 1) >> exponent);
		}

		// ceil(a / b) for non-negative values.
		private static int CeilDiv(int a, int b)
		{
			return (int)(((long)a + b - 1) / b);
		}

		// decodes the tiles and converts the components to the output image.
		private static JpxImage DecodeCodestream(JpxCodestream codestream, FileInfo file, int reduce)
		{
			var componentCount = codestream.Components.Length;

			// discarding resolution levels is limited by the smallest number of decomposition levels.
			var minLevels = int.MaxValue;
			for (int c = 0; c < componentCount; c++)
			{
				minLevels = Math.Min(minLevels, JpxTileDecoder.ResolveStyle(codestream, null, c).Levels);
				foreach (var tile in codestream.Tiles)
				{
					if (tile != null)
						minLevels = Math.Min(minLevels, JpxTileDecoder.ResolveStyle(codestream, tile, c).Levels);
				}
			}

			reduce = Math.Min(reduce, minLevels);

			// allocate the component planes at the reduced size.
			var planes = new JpxComponentPlanes
			{
				Samples = new ushort[componentCount][],
				Widths = new int[componentCount],
				Heights = new int[componentCount],
				X0 = new int[componentCount],
				Y0 = new int[componentCount],
				Precisions = new int[componentCount],
			};

			long total = 0;
			for (int c = 0; c < componentCount; c++)
			{
				var info = codestream.Components[c];
				var x0 = CeilShift(CeilDiv(codestream.XOsiz, info.XRsiz), reduce);
				var y0 = CeilShift(CeilDiv(codestream.YOsiz, info.YRsiz), reduce);
				var x1 = CeilShift(CeilDiv(codestream.Xsiz, info.XRsiz), reduce);
				var y1 = CeilShift(CeilDiv(codestream.Ysiz, info.YRsiz), reduce);
				planes.X0[c] = x0;
				planes.Y0[c] = y0;
				planes.Widths[c] = Math.Max(1, x1 - x0);
				planes.Heights[c] = Math.Max(1, y1 - y0);
				planes.Precisions[c] = Math.Min(info.Precision, 16);

				total += (long)planes.Widths[c] * planes.Heights[c] * 2;
				if (total > MaxOutputBytes)
					return null;
			}

			var width = CeilShift(codestream.Xsiz, reduce) - CeilShift(codestream.XOsiz, reduce);
			var height = CeilShift(codestream.Ysiz, reduce) - CeilShift(codestream.YOsiz, reduce);
			var channels = GetChannels(file, codestream);
			if (width <= 0 || height <= 0 || (long)width * height * channels.Length > MaxOutputBytes)
				return null;

			for (int c = 0; c < componentCount; c++)
				planes.Samples[c] = new ushort[planes.Widths[c] * planes.Heights[c]];

			// decode the tiles; a broken tile leaves its area empty and the others still decode.
			var complete = !codestream.Truncated;
			var decoder = new JpxTileDecoder(codestream, reduce, planes);
			foreach (var tile in codestream.Tiles)
			{
				if (tile == null)
				{
					complete = false;
					continue;
				}

				try
				{
					decoder.Decode(tile);
				}
				catch (OutOfMemoryException)
				{
					throw;
				}
				catch (Exception)
				{
					complete = false;
				}
			}

			if (decoder.Incomplete)
				complete = false;

			var image = BuildImage(codestream, file, planes, channels, width, height, reduce);
			image.IsComplete = complete;
			return image;
		}

		// converts the component planes to interleaved 8-bit channels.
		private static JpxImage BuildImage(JpxCodestream codestream, FileInfo file, JpxComponentPlanes planes, Channel[] channels, int width, int height, int reduce)
		{
			var colorSpace = GetColorSpace(file, out var sycc);
			var order = GetChannelOrder(file, channels.Length, out var alpha, out var premultiplied);
			var channelCount = order.Length;

			var image = new JpxImage
			{
				Width = width,
				Height = height,
				Components = channelCount,
				Data = new byte[width * height * channelCount],
				ColorSpace = colorSpace,
				IccProfile = file.IccProfile,
				AlphaChannel = alpha,
				AlphaPremultiplied = premultiplied,
				BitDepths = new int[channelCount],
			};

			var imageX0 = CeilShift(codestream.XOsiz, reduce);
			var imageY0 = CeilShift(codestream.YOsiz, reduce);
			var columnIndex = new int[width];
			var data = image.Data;

			for (int k = 0; k < channelCount; k++)
			{
				var channel = channels[order[k]];
				var c = channel.Component;
				var info = codestream.Components[c];
				var samples = planes.Samples[c];
				var planeWidth = planes.Widths[c];
				var planeHeight = planes.Heights[c];

				// the 8-bit value of every possible sample: a palette lookup or a precision scaling.
				byte[] table;
				if (channel.PaletteColumn >= 0)
				{
					var depth = file.PaletteDepths[channel.PaletteColumn];
					var column = file.PaletteValues[channel.PaletteColumn];
					var precision = planes.Precisions[c];
					table = new byte[1 << precision];
					for (int i = 0; i < table.Length; i++)
						table[i] = ScaleTo8(column[Math.Min(i, column.Length - 1)], depth);

					image.BitDepths[k] = depth;
				}
				else
				{
					var precision = planes.Precisions[c];
					table = new byte[1 << precision];
					for (int i = 0; i < table.Length; i++)
						table[i] = ScaleTo8(i, precision);

					image.BitDepths[k] = info.Precision;
				}

				// map every output column to a plane column (sub-sampled components are replicated).
				for (int x = 0; x < width; x++)
				{
					var index = (int)(((long)imageX0 + x) / info.XRsiz) - planes.X0[c];
					columnIndex[x] = Math.Max(0, Math.Min(planeWidth - 1, index));
				}

				var mask = table.Length - 1;
				var target = k;
				for (int y = 0; y < height; y++)
				{
					var row = (int)(((long)imageY0 + y) / info.YRsiz) - planes.Y0[c];
					row = Math.Max(0, Math.Min(planeHeight - 1, row)) * planeWidth;
					for (int x = 0; x < width; x++, target += channelCount)
						data[target] = table[samples[row + columnIndex[x]] & mask];
				}
			}

			if (sycc && channelCount >= 3)
				ConvertSyccToRgb(data, channelCount);

			return image;
		}

		// scales a sample of the given precision to 8 bits with rounding.
		private static byte ScaleTo8(int value, int depth)
		{
			if (depth == 8)
				return (byte)Math.Min(value, 255);

			var max = (1L << depth) - 1;
			return (byte)Math.Min(255, (value * 255L + (max >> 1)) / max);
		}

		// returns the order of the output channels according to the channel definition box.
		private static int[] GetChannelOrder(FileInfo file, int count, out int alpha, out bool premultiplied)
		{
			alpha = -1;
			premultiplied = false;

			var order = new int[count];
			for (int i = 0; i < count; i++)
				order[i] = i;

			if (file.DefinitionChannels == null)
				return order;

			// sort key: colour channels by association first, then opacity channels, then the others.
			var keys = new long[count];
			for (int i = 0; i < count; i++)
				keys[i] = (3L << 32) + i;

			for (int d = 0; d < file.DefinitionChannels.Length; d++)
			{
				var channel = file.DefinitionChannels[d];
				if (channel >= count)
					continue;

				var type = file.DefinitionTypes[d];
				var association = file.DefinitionAssociations[d];
				if (type == 0 && association >= 1 && association < 65535)
					keys[channel] = ((long)association << 16) + channel;
				else if (type == 0)
					keys[channel] = (1L << 32) + channel;
				else if (type == 1 || type == 2)
					keys[channel] = (2L << 32) + channel;
			}

			Array.Sort(keys, order);

			for (int k = 0; k < count; k++)
			{
				var channel = order[k];
				for (int d = 0; d < file.DefinitionChannels.Length; d++)
				{
					if (file.DefinitionChannels[d] != channel)
						continue;

					var type = file.DefinitionTypes[d];
					if ((type == 1 || type == 2) && alpha < 0)
					{
						alpha = k;
						premultiplied = type == 2;
					}
				}
			}

			return order;
		}

		// converts the first three channels from sYCC to sRGB in place.
		private static void ConvertSyccToRgb(byte[] data, int channelCount)
		{
			for (int i = 0; i + 2 < data.Length; i += channelCount)
			{
				var y = (float)data[i];
				var cb = data[i + 1] - 128f;
				var cr = data[i + 2] - 128f;
				data[i] = ClampToByte(y + 1.402f * cr);
				data[i + 1] = ClampToByte(y - 0.344136f * cb - 0.714136f * cr);
				data[i + 2] = ClampToByte(y + 1.772f * cb);
			}
		}

		// rounds and clamps a value to a byte.
		private static byte ClampToByte(float value)
		{
			if (value <= 0f)
				return 0;

			if (value >= 255f)
				return 255;

			return (byte)(value + 0.5f);
		}

		#endregion
	}
}
