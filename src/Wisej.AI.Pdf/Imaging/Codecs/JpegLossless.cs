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

// Huffman-coded lossless JPEG (SOF3) sample decoding (ITU-T T.81, Annex H).

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <content>
	/// Lossless (SOF3) scans: predictive decoding of Huffman-coded sample differences (T.81, Annex H).
	/// </content>
	internal sealed partial class JpegHuffmanDecoder
	{
		#region Implementation

		// decodes one MCU (h x v samples of each component) of a lossless scan.
		private void DecodeLosslessUnit(long unit, int unitsPerLine, bool single)
		{
			var row = (int)(unit / unitsPerLine);
			var column = (int)(unit % unitsPerLine);

			if (single)
			{
				DecodeLosslessSample(_scan.Components[0], column, row);
				return;
			}

			foreach (var component in _scan.Components)
			{
				for (int y = 0; y < component.V; y++)
				{
					for (int x = 0; x < component.H; x++)
						DecodeLosslessSample(component, column * component.H + x, row * component.V + y);
				}
			}
		}

		// decodes the difference of one sample and adds the prediction from its neighbours (H.1.2).
		private void DecodeLosslessSample(JpegComponent component, int x, int y)
		{
			var samples = component.Samples;
			var stride = component.BlocksPerLineAlloc;
			var index = y * stride + x;

			int prediction;
			if (y == component.IntervalRow)
			{
				// first line of the scan or of a restart interval: predict from the left neighbour,
				// and use the mid-range value for the very first sample.
				prediction = x == 0
					? 1 << System.Math.Max(0, _frame.Precision - _scan.Al - 1)
					: samples[index - 1];
			}
			else if (x == 0)
			{
				prediction = samples[index - stride];
			}
			else
			{
				var a = samples[index - 1];
				var b = samples[index - stride];
				var c = samples[index - stride - 1];
				switch (_scan.Ss)
				{
					case 2:
						prediction = b;
						break;

					case 3:
						prediction = c;
						break;

					case 4:
						prediction = a + b - c;
						break;

					case 5:
						prediction = a + ((b - c) >> 1);
						break;

					case 6:
						prediction = b + ((a - c) >> 1);
						break;

					case 7:
						prediction = (a + b) >> 1;
						break;

					default:
						prediction = a;
						break;
				}
			}

			// the difference category 16 has no extra bits and means 32768 (H.1.2.2).
			var size = DecodeSymbol(component.DcTable);
			int difference;
			if (size == 0)
				difference = 0;
			else if (size >= 16)
				difference = 32768;
			else
				difference = ReceiveExtend(size);

			samples[index] = (prediction + difference) & 0xFFFF;
		}

		#endregion
	}
}
