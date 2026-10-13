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

// Managed JPEG decoder for the DCTDecode filter (ISO 32000-2, 7.4.8; ITU-T T.81, Annex B).

using System;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Decodes JPEG streams: baseline, extended sequential, progressive and lossless frames, with
	/// Huffman or arithmetic coding, 8 or 12-bit precision and any sampling factors.
	/// </summary>
	/// <remarks>
	/// The decoder is built for damaged real-world data: it skips garbage before SOI and between markers,
	/// supplies the standard Huffman tables when DHT is missing, reads the frame height from a DNL marker,
	/// resynchronizes on restart markers and returns whatever was decoded from truncated streams.
	/// </remarks>
	internal static class JpegDecoder
	{
		#region Methods

		/// <summary>
		/// Decodes a JPEG stream to interleaved 8-bit samples.
		/// </summary>
		/// <param name="data">The JPEG stream.</param>
		/// <param name="colorTransform">The <c>/ColorTransform</c> decode parameter, or -1 when it is not specified.</param>
		/// <returns>The decoded image; null when the stream has no usable frame header.</returns>
		/// <remarks>
		/// Three components are converted from YCbCr to RGB unless the Adobe marker says the transform is 0,
		/// <paramref name="colorTransform"/> is 0, or the stream has neither an Adobe nor a JFIF marker and its
		/// components are named 'R', 'G' and 'B'; a <paramref name="colorTransform"/> of 1 always converts.
		/// Four components are converted from YCCK to CMYK when the Adobe transform is 2 or <paramref name="colorTransform"/>
		/// is 1; Adobe-inverted CMYK samples are returned as stored (see <see cref="JpegImage.HasAdobeMarker"/>).
		/// </remarks>
		public static JpegImage Decode(byte[] data, int colorTransform = -1)
		{
			if (data == null || data.Length < 4)
				return null;

			try
			{
				return DecodeCore(data, colorTransform);
			}
			catch (OutOfMemoryException)
			{
				return null;
			}
			catch (Exception)
			{
				// a defensive net: the decoders guard every access, but a damaged stream must never fail the page.
				return null;
			}
		}

		/// <summary>
		/// Reads the frame header of a JPEG stream without decoding it.
		/// </summary>
		/// <param name="data">The JPEG stream.</param>
		/// <param name="width">Receives the image width.</param>
		/// <param name="height">Receives the image height (from the DNL marker when the frame header has 0).</param>
		/// <param name="components">Receives the number of components.</param>
		/// <param name="bitsPerSample">Receives the sample precision.</param>
		/// <param name="isProgressive">Receives true for progressive frames.</param>
		/// <param name="adobeTransform">Receives the transform flag of the Adobe APP14 marker, or -1.</param>
		/// <returns>True when a frame header was found.</returns>
		public static bool TryReadHeader(byte[] data, out int width, out int height, out int components, out int bitsPerSample, out bool isProgressive, out int adobeTransform)
		{
			width = 0;
			height = 0;
			components = 0;
			bitsPerSample = 0;
			isProgressive = false;
			adobeTransform = -1;

			if (data == null || data.Length < 4)
				return false;

			var found = false;
			var position = FindStart(data);
			while (true)
			{
				var marker = NextMarker(data, ref position);
				if (marker < 0 || marker == 0xD9)
					break;

				if (!HasLength(marker))
					continue;

				var segmentEnd = SegmentEnd(data, position);
				var start = position + 2;

				if (marker == 0xEE)
				{
					adobeTransform = ReadAdobeTransform(data, start, segmentEnd, adobeTransform);
				}
				else if (IsFrameMarker(marker) && !found && segmentEnd - start >= 6)
				{
					found = true;
					bitsPerSample = data[start];
					height = (data[start + 1] << 8) | data[start + 2];
					width = (data[start + 3] << 8) | data[start + 4];
					components = data[start + 5];
					isProgressive = marker == 0xC2 || marker == 0xC6 || marker == 0xCA || marker == 0xCE;

					if (height == 0)
						height = FindDnlHeight(data, segmentEnd);
				}
				else if (marker == 0xDA)
				{
					// the Adobe marker, if any, precedes the first scan.
					break;
				}

				position = segmentEnd;
			}

			return found && width > 0;
		}

		#endregion

		#region Implementation

		// parses the marker segments, decodes the scans and renders the image.
		private static JpegImage DecodeCore(byte[] data, int colorTransform)
		{
			var tables = new JpegTables();
			JpegFrame frame = null;
			JpegHuffmanDecoder huffman = null;
			JpegArithmeticDecoder arithmetic = null;
			var hasJfif = false;
			var adobeTransform = -1;
			var hasAdobe = false;
			var sawEoi = false;

			var position = FindStart(data);
			while (true)
			{
				var marker = NextMarker(data, ref position);
				if (marker < 0)
					break;

				if (marker == 0xD9)
				{
					// stop at the end of the first image: some producers append thumbnails or junk.
					if (frame != null && frame.IsAllocated)
					{
						sawEoi = true;
						break;
					}

					continue;
				}

				// SOI, RSTn and TEM have no segment.
				if (!HasLength(marker))
					continue;

				var segmentEnd = SegmentEnd(data, position);
				var start = position + 2;
				position = segmentEnd;

				switch (marker)
				{
					case 0xC4:
						ReadHuffmanTables(data, start, segmentEnd, tables);
						break;

					case 0xCC:
						ReadArithmeticConditioning(data, start, segmentEnd, tables);
						break;

					case 0xDB:
						ReadQuantTables(data, start, segmentEnd, tables);
						break;

					case 0xDD:
						if (segmentEnd - start >= 2)
							tables.RestartInterval = (data[start] << 8) | data[start + 1];

						break;

					case 0xE0:
						if (segmentEnd - start >= 5 && data[start] == 'J' && data[start + 1] == 'F' && data[start + 2] == 'I' && data[start + 3] == 'F' && data[start + 4] == 0)
							hasJfif = true;

						break;

					case 0xEE:
						var transform = ReadAdobeTransform(data, start, segmentEnd, -2);
						if (transform != -2)
						{
							hasAdobe = true;
							adobeTransform = transform;
						}

						break;

					case 0xDA:
						if (frame == null)
							break;

						var scan = ReadScanHeader(data, start, segmentEnd, frame, tables);
						if (scan == null)
							break;

						if (!frame.IsAllocated)
						{
							// the height may be defined by a DNL marker after the first scan (B.2.5).
							if (frame.Height == 0)
								frame.Height = FindDnlHeight(data, segmentEnd);

							if (!frame.Allocate())
								return null;
						}

						// arithmetic-coded lossless frames are not supported: leave the samples neutral.
						if (frame.IsLossless && frame.IsArithmetic)
						{
							frame.IsComplete = false;
							break;
						}

						JpegEntropyDecoder decoder;
						if (frame.IsArithmetic)
							decoder = arithmetic ?? (arithmetic = new JpegArithmeticDecoder(data, data.Length));
						else
							decoder = huffman ?? (huffman = new JpegHuffmanDecoder(data, data.Length));

						try
						{
							position = decoder.Decode(frame, tables, scan, segmentEnd);
						}
						catch (IndexOutOfRangeException)
						{
							// corrupt scan parameters that slipped through validation: keep what was decoded.
							frame.IsComplete = false;
							position = data.Length;
						}

						break;

					default:
						if (IsFrameMarker(marker) && frame == null)
						{
							frame = ReadFrameHeader(data, start, segmentEnd, marker);
						}
						break;
				}
			}

			if (frame == null)
				return null;

			if (!frame.IsAllocated)
			{
				// a frame without any scan: render it neutral rather than failing.
				if (frame.Height == 0 || !frame.Allocate())
					return null;

				frame.IsComplete = false;
			}

			var complete = frame.IsComplete;
			foreach (var component in frame.Components)
			{
				if (!component.IsCovered)
					complete = false;
			}

			// a progressive stream without EOI has most likely lost its last scans.
			if (frame.IsProgressive && !sawEoi)
				complete = false;

			var conversion = ChooseConversion(frame, colorTransform, hasJfif, hasAdobe, adobeTransform);
			return new JpegImage
			{
				Width = frame.Width,
				Height = frame.Height,
				Components = frame.Components.Length,
				Data = JpegOutput.Render(frame, tables, conversion),
				HasAdobeMarker = hasAdobe,
				AdobeTransform = adobeTransform,
				IsComplete = complete,
			};
		}

		// applies the PDF rules for /ColorTransform and the Adobe and JFIF markers.
		private static JpegOutput.ColorConversion ChooseConversion(JpegFrame frame, int colorTransform, bool hasJfif, bool hasAdobe, int adobeTransform)
		{
			var components = frame.Components;
			if (components.Length == 3)
			{
				if (colorTransform == 1)
					return JpegOutput.ColorConversion.YCbCrToRgb;

				if (colorTransform == 0 || (hasAdobe && adobeTransform == 0))
					return JpegOutput.ColorConversion.None;

				// without any marker, components named R, G, B are stored as RGB.
				if (!hasAdobe && !hasJfif && components[0].Id == 'R' && components[1].Id == 'G' && components[2].Id == 'B')
					return JpegOutput.ColorConversion.None;

				return JpegOutput.ColorConversion.YCbCrToRgb;
			}

			if (components.Length == 4)
			{
				if (colorTransform == 1 || (hasAdobe && adobeTransform == 2))
					return JpegOutput.ColorConversion.YcckToCmyk;
			}

			return JpegOutput.ColorConversion.None;
		}

		// reads a SOFn segment.
		private static JpegFrame ReadFrameHeader(byte[] data, int start, int end, int marker)
		{
			if (end - start < 6)
				return null;

			int count = data[start + 5];
			if (count == 0)
				return null;

			var frame = new JpegFrame
			{
				Marker = marker,
				Precision = data[start],
				Height = (data[start + 1] << 8) | data[start + 2],
				Width = (data[start + 3] << 8) | data[start + 4],
				IsProgressive = marker == 0xC2 || marker == 0xC6 || marker == 0xCA || marker == 0xCE,
				IsArithmetic = marker >= 0xC9,
				IsLossless = marker == 0xC3 || marker == 0xC7 || marker == 0xCB || marker == 0xCF,
			};

			if (frame.Width == 0)
				return null;

			// only 8 and 12-bit DCT precisions exist; treat anything else as the nearest one.
			if (!frame.IsLossless)
				frame.Precision = frame.Precision > 8 ? 12 : 8;
			else if (frame.Precision < 2 || frame.Precision > 16)
				frame.Precision = 8;

			// a truncated segment keeps the components that are complete.
			count = Math.Min(count, (end - start - 6) / 3);
			if (count == 0)
				return null;

			frame.Components = new JpegComponent[count];
			for (int i = 0; i < count; i++)
			{
				var p = start + 6 + i * 3;
				var h = data[p + 1] >> 4;
				var v = data[p + 1] & 15;
				frame.Components[i] = new JpegComponent
				{
					Id = data[p],
					H = h >= 1 && h <= 4 ? h : 1,
					V = v >= 1 && v <= 4 ? v : 1,
					QuantIndex = data[p + 2] & 3,
				};
			}

			return frame;
		}

		// reads a SOS segment; returns null when the scan can't be decoded.
		private static JpegScan ReadScanHeader(byte[] data, int start, int end, JpegFrame frame, JpegTables tables)
		{
			if (end - start < 1)
				return null;

			var count = data[start];
			if (count == 0 || count > 4 || end - start < 1 + count * 2 + 3)
				return null;

			var components = new JpegComponent[count];
			for (int i = 0; i < count; i++)
			{
				var p = start + 1 + i * 2;
				var component = frame.FindComponent(data[p]);

				// broken encoders write wrong identifiers: fall back to the position in the frame.
				if (component == null && count == frame.Components.Length)
					component = frame.Components[i];

				if (component == null || Array.IndexOf(components, component) >= 0)
					return null;

				var dc = data[p + 1] >> 4 & 3;
				var ac = data[p + 1] & 3;
				var chroma = Array.IndexOf(frame.Components, component) > 0;

				component.DcTableIndex = dc;
				component.AcTableIndex = ac;

				// streams without DHT (MJPEG style) use the standard tables of Annex K.
				component.DcTable = tables.DcTables[dc] ?? (tables.DcTables[dc] = JpegHuffmanTable.CreateDefault(false, dc != 0 || chroma));
				component.AcTable = tables.AcTables[ac] ?? (tables.AcTables[ac] = JpegHuffmanTable.CreateDefault(true, ac != 0 || chroma));

				// latch the quantization table in force when the component is first decoded (libjpeg behaviour).
				if (component.Quant == null && tables.QuantTables[component.QuantIndex] != null)
					component.Quant = (ushort[])tables.QuantTables[component.QuantIndex].Clone();

				component.IsScanned = true;
				components[i] = component;
			}

			var q = start + 1 + count * 2;
			var scan = new JpegScan
			{
				Components = components,
				Ss = data[q],
				Se = data[q + 1],
				Ah = data[q + 2] >> 4,
				Al = data[q + 2] & 15,
			};

			if (frame.IsLossless)
				return scan;

			if (!frame.IsProgressive)
			{
				// sequential scans always cover the whole block, whatever the header says.
				scan.Ss = 0;
				scan.Se = 63;
				scan.Ah = 0;
				scan.Al = 0;
				return scan;
			}

			// validate the progressive parameters (G.1.1.1.1): invalid scans are skipped.
			if (scan.Se > 63 || scan.Ss > scan.Se || scan.Al > 13 || (scan.Ss == 0 && scan.Se != 0) || (scan.Ss > 0 && count != 1))
				return null;

			return scan;
		}

		// reads a DHT segment.
		private static void ReadHuffmanTables(byte[] data, int start, int end, JpegTables tables)
		{
			var p = start;
			while (p + 17 <= end)
			{
				var tableClass = data[p] >> 4;
				var index = data[p] & 3;

				var total = 0;
				for (int i = 1; i <= 16; i++)
					total += data[p + i];

				if (total > 256)
					return;

				var available = Math.Min(total, end - (p + 17));
				var table = JpegHuffmanTable.Create(data, p + 1, data, p + 17, available);
				if (tableClass == 0)
					tables.DcTables[index] = table;
				else
					tables.AcTables[index] = table;

				p += 17 + total;
			}
		}

		// reads a DQT segment, converting the tables from zigzag to natural order.
		private static void ReadQuantTables(byte[] data, int start, int end, JpegTables tables)
		{
			var p = start;
			while (p < end)
			{
				var wide = data[p] >> 4 != 0;
				var index = data[p] & 3;
				var size = wide ? 128 : 64;
				p++;

				if (p + size > end)
					return;

				var table = new ushort[64];
				for (int i = 0; i < 64; i++)
				{
					var value = wide ? (data[p + 2 * i] << 8) | data[p + 2 * i + 1] : data[p + i];

					// a zero quantizer would zero the coefficient; 1 is the closest valid value.
					table[JpegTables.ZigZag[i]] = (ushort)Math.Max(1, value);
				}

				tables.QuantTables[index] = table;
				p += size;
			}
		}

		// reads a DAC segment (B.2.4.3).
		private static void ReadArithmeticConditioning(byte[] data, int start, int end, JpegTables tables)
		{
			for (int p = start; p + 1 < end; p += 2)
			{
				var tableClass = data[p] >> 4;
				var index = data[p] & 3;
				var value = data[p + 1];
				if (tableClass == 0)
				{
					tables.ArithmeticDcL[index] = (byte)(value & 15);
					tables.ArithmeticDcU[index] = (byte)Math.Max(value & 15, value >> 4);
				}
				else
				{
					tables.ArithmeticAcK[index] = (byte)Math.Max(1, Math.Min(63, (int)value));
				}
			}
		}

		// reads the transform flag of an APP14 segment; returns `fallback` when it is not an Adobe segment.
		private static int ReadAdobeTransform(byte[] data, int start, int end, int fallback)
		{
			if (end - start >= 12 && data[start] == 'A' && data[start + 1] == 'd' && data[start + 2] == 'o' && data[start + 3] == 'b' && data[start + 4] == 'e')
				return data[start + 11];

			// a short Adobe segment: the marker is there, the transform is unknown (treated as the default).
			if (end - start >= 5 && data[start] == 'A' && data[start + 1] == 'd' && data[start + 2] == 'o' && data[start + 3] == 'b' && data[start + 4] == 'e')
				return fallback == -2 ? -1 : fallback;

			return fallback;
		}

		// returns the position of the first marker: the SOI if present, otherwise the first plausible marker.
		private static int FindStart(byte[] data)
		{
			for (int i = 0; i + 1 < data.Length; i++)
			{
				if (data[i] == 0xFF && data[i + 1] == 0xD8)
					return i;
			}

			// no SOI: start at the first marker that can begin a JPEG header.
			for (int i = 0; i + 1 < data.Length; i++)
			{
				if (data[i] == 0xFF && data[i + 1] >= 0xC0 && data[i + 1] != 0xFF)
					return i;
			}

			return data.Length;
		}

		// finds the next marker from `position`, skipping garbage and fill bytes; returns its code
		// and sets `position` to the byte after it, or returns -1 at the end of the data.
		private static int NextMarker(byte[] data, ref int position)
		{
			var p = position;
			while (p + 1 < data.Length)
			{
				if (data[p] == 0xFF)
				{
					var code = data[p + 1];
					if (code != 0 && code != 0xFF)
					{
						position = p + 2;
						return code;
					}
				}

				p++;
			}

			position = data.Length;
			return -1;
		}

		// returns true for the markers followed by a length field.
		private static bool HasLength(int marker)
		{
			return !(marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0xD9);
		}

		// returns true for the SOFn markers of non-differential frames. The differential frames of the
		// hierarchical process (SOF5-7, SOF13-15) only refine the first frame, which is decoded on its own.
		private static bool IsFrameMarker(int marker)
		{
			switch (marker)
			{
				case 0xC0:
				case 0xC1:
				case 0xC2:
				case 0xC3:
				case 0xC9:
				case 0xCA:
				case 0xCB:
					return true;

				default:
					return false;
			}
		}

		// returns the end of the segment whose length field is at `position`, clamped to the data.
		private static int SegmentEnd(byte[] data, int position)
		{
			if (position + 1 >= data.Length)
				return data.Length;

			var length = (data[position] << 8) | data[position + 1];
			if (length < 2)
				return position + 2;

			return (int)Math.Min(data.Length, (long)position + length);
		}

		// looks for a DNL marker after the first scan and returns the number of lines it defines, or 0.
		private static int FindDnlHeight(byte[] data, int position)
		{
			for (int i = position; i + 5 < data.Length; i++)
			{
				if (data[i] == 0xFF && data[i + 1] == 0xDC && data[i + 2] == 0 && data[i + 3] == 4)
					return (data[i + 4] << 8) | data[i + 5];
			}

			return 0;
		}

		#endregion
	}
}
