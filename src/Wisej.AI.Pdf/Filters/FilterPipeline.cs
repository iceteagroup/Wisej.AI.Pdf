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

// Applies the chain of stream filters (ISO 32000-2, 7.4).

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Filters
{
	/// <summary>
	/// Describes an image codec filter that was not applied by <see cref="FilterPipeline.Decode"/>.
	/// </summary>
	internal struct PendingImageFilter
	{
		/// <summary>
		/// The filter name: <c>DCTDecode</c>, <c>JPXDecode</c>, <c>JBIG2Decode</c> or <c>CCITTFaxDecode</c>; null when all the filters were applied.
		/// </summary>
		public string Name;

		/// <summary>
		/// The decode parameters of the filter, or null.
		/// </summary>
		public PdfDictionary Parameters;
	}

	/// <summary>
	/// Decodes stream data through its <c>/Filter</c> chain.
	/// </summary>
	internal static class FilterPipeline
	{
		/// <summary>
		/// Delegate used to decode image codec filters to raw samples.
		/// </summary>
		/// <param name="filter">The name of the image filter.</param>
		/// <param name="data">The encoded data.</param>
		/// <param name="parameters">The filter decode parameters, or null.</param>
		/// <param name="stream">The image stream dictionary.</param>
		/// <returns>The decoded samples.</returns>
		public delegate byte[] ImageCodec(string filter, byte[] data, PdfDictionary parameters, PdfStream stream);

		/// <summary>
		/// The decoder for image codec filters, installed by the imaging module.
		/// </summary>
		public static ImageCodec ImageDecoder;

		/// <summary>
		/// Maximum size of decoded data, to protect against decompression bombs.
		/// </summary>
		public const int MaxDecodedLength = 512 * 1024 * 1024;

		/// <summary>
		/// Decodes the data of <paramref name="stream"/>.
		/// </summary>
		/// <param name="stream">The stream to decode.</param>
		/// <param name="decodeImageFilters">True to also apply the image codec filters.</param>
		/// <param name="pending">Receives the image codec filter that was not applied, if any.</param>
		/// <returns>The decoded data; never null.</returns>
		public static byte[] Decode(PdfStream stream, bool decodeImageFilters, out PendingImageFilter pending)
		{
			pending = default;

			var data = stream.GetRawBytes();
			var filters = GetFilters(stream, out var parameters);
			for (int i = 0; i < filters.Count; i++)
			{
				var name = filters[i];
				var parms = parameters[i];

				if (IsImageCodec(name))
				{
					// image codecs must be the last filter in the chain.
					if (!decodeImageFilters || ImageDecoder == null)
					{
						pending.Name = name;
						pending.Parameters = parms;
						return data;
					}

					data = ImageDecoder(name, data, parms, stream);
					continue;
				}

				data = Apply(name, data, parms);
			}

			return data;
		}

		/// <summary>
		/// Returns the filter names and their parameters, normalizing abbreviated names.
		/// </summary>
		/// <param name="dictionary">The stream dictionary (or inline image dictionary).</param>
		/// <param name="parameters">Receives the decode parameters for each filter (null entries when absent).</param>
		/// <returns>The full filter names in application order.</returns>
		public static List<string> GetFilters(PdfDictionary dictionary, out List<PdfDictionary> parameters)
		{
			var names = new List<string>(2);
			parameters = new List<PdfDictionary>(2);

			var filter = dictionary.Resolve(PdfNames.Filter, PdfNames.F);
			var parms = dictionary.Resolve(PdfNames.DecodeParms, PdfNames.DP);

			if (filter is PdfName single)
			{
				names.Add(NormalizeName(single.Value));
				parameters.Add(parms is PdfArray a ? a.GetDictionary(0) : parms as PdfDictionary);
			}
			else if (filter is PdfArray array)
			{
				var parmsArray = parms as PdfArray;
				for (int i = 0; i < array.Count; i++)
				{
					var name = array.GetName(i);
					if (name == null)
						continue;

					names.Add(NormalizeName(name.Value));
					parameters.Add(parmsArray != null ? parmsArray.GetDictionary(i) : (i == 0 ? parms as PdfDictionary : null));
				}
			}

			return names;
		}

		/// <summary>
		/// Expands the abbreviated filter names allowed in inline images.
		/// </summary>
		/// <param name="name">The filter name.</param>
		/// <returns>The full filter name.</returns>
		public static string NormalizeName(string name)
		{
			switch (name)
			{
				case "AHx": return "ASCIIHexDecode";
				case "A85": return "ASCII85Decode";
				case "LZW": return "LZWDecode";
				case "Fl": return "FlateDecode";
				case "RL": return "RunLengthDecode";
				case "CCF": return "CCITTFaxDecode";
				case "DCT": return "DCTDecode";
				default: return name;
			}
		}

		/// <summary>
		/// Returns true when <paramref name="name"/> is an image codec filter.
		/// </summary>
		/// <param name="name">The full filter name.</param>
		/// <returns>True for DCTDecode, JPXDecode, JBIG2Decode and CCITTFaxDecode.</returns>
		public static bool IsImageCodec(string name)
		{
			return name == "DCTDecode" || name == "JPXDecode" || name == "JBIG2Decode" || name == "CCITTFaxDecode";
		}

		/// <summary>
		/// Applies a single non-image filter.
		/// </summary>
		/// <param name="name">The full filter name.</param>
		/// <param name="data">The encoded data.</param>
		/// <param name="parameters">The decode parameters, or null.</param>
		/// <returns>The decoded data. Unknown filters return the data unchanged.</returns>
		public static byte[] Apply(string name, byte[] data, PdfDictionary parameters)
		{
			switch (name)
			{
				case "FlateDecode":
					return Predictor.Apply(FlateFilter.Decode(data), parameters);

				case "LZWDecode":
					return Predictor.Apply(LzwFilter.Decode(data, parameters?.GetInt("EarlyChange", 1) ?? 1), parameters);

				case "ASCIIHexDecode":
					return AsciiHexFilter.Decode(data);

				case "ASCII85Decode":
					return Ascii85Filter.Decode(data);

				case "RunLengthDecode":
					return RunLengthFilter.Decode(data);

				default:
					// Crypt (decryption is handled by the security handler) and unknown filters.
					return data;
			}
		}
	}
}
