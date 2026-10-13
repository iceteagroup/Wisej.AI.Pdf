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

// PDF functions (ISO 32000-2, 7.10): sampled (type 0), exponential (type 2),
// stitching (type 3) and PostScript calculator (type 4).

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.IO;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Graphics
{
	/// <summary>
	/// A PDF function: maps m input values to n output values.
	/// </summary>
	/// <remarks>
	/// Functions are used by tint transforms of Separation and DeviceN colour spaces, shadings and transfer functions.
	/// Instances are immutable after creation and can be shared between threads, except that
	/// <see cref="Evaluate"/> must be given separate output arrays.
	/// </remarks>
	internal abstract class PdfFunction
	{
		/// <summary>
		/// The input domain: pairs of minimum and maximum values.
		/// </summary>
		protected double[] Domain;

		/// <summary>
		/// The output range: pairs of minimum and maximum values, or null when unbounded.
		/// </summary>
		protected double[] Range;

		/// <summary>
		/// The number of input values.
		/// </summary>
		public int InputCount => this.Domain.Length / 2;

		/// <summary>
		/// The number of output values.
		/// </summary>
		public abstract int OutputCount { get; }

		/// <summary>
		/// Evaluates the function.
		/// </summary>
		/// <param name="input">The input values; clipped to the domain.</param>
		/// <param name="output">Receives the output values; must have at least <see cref="OutputCount"/> elements.</param>
		public void Evaluate(double[] input, double[] output)
		{
			var clipped = new double[this.InputCount];
			for (int i = 0; i < clipped.Length; i++)
			{
				var value = i < input.Length ? input[i] : 0;
				clipped[i] = Clip(value, this.Domain[i * 2], this.Domain[i * 2 + 1]);
			}

			EvaluateCore(clipped, output);

			if (this.Range != null)
			{
				for (int i = 0; i < this.OutputCount && i * 2 + 1 < this.Range.Length; i++)
					output[i] = Clip(output[i], this.Range[i * 2], this.Range[i * 2 + 1]);
			}
		}

		/// <summary>
		/// Evaluates the function on inputs already clipped to the domain.
		/// </summary>
		/// <param name="input">The clipped input values.</param>
		/// <param name="output">Receives the output values.</param>
		protected abstract void EvaluateCore(double[] input, double[] output);

		/// <summary>
		/// Clips <paramref name="value"/> to the interval [<paramref name="min"/>, <paramref name="max"/>].
		/// </summary>
		/// <param name="value">The value.</param>
		/// <param name="min">The minimum.</param>
		/// <param name="max">The maximum.</param>
		/// <returns>The clipped value.</returns>
		protected static double Clip(double value, double min, double max)
		{
			if (double.IsNaN(value))
				return min;

			return value < min ? min : value > max ? max : value;
		}

		/// <summary>
		/// Linear interpolation of <paramref name="x"/> from [<paramref name="xMin"/>, <paramref name="xMax"/>] to [<paramref name="yMin"/>, <paramref name="yMax"/>].
		/// </summary>
		/// <param name="x">The value to map.</param>
		/// <param name="xMin">The source minimum.</param>
		/// <param name="xMax">The source maximum.</param>
		/// <param name="yMin">The target minimum.</param>
		/// <param name="yMax">The target maximum.</param>
		/// <returns>The interpolated value.</returns>
		protected static double Interpolate(double x, double xMin, double xMax, double yMin, double yMax)
		{
			if (xMax == xMin)
				return yMin;

			return yMin + (x - xMin) * (yMax - yMin) / (xMax - xMin);
		}

		#region Factory

		/// <summary>
		/// Creates a function from its PDF definition.
		/// </summary>
		/// <param name="definition">A function dictionary or stream, or an array of 1-output functions (combined into one).</param>
		/// <returns>The function, or null when the definition is invalid.</returns>
		public static PdfFunction Create(object definition)
		{
			try
			{
				return Create(definition, 0);
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>
		/// Creates a function from its PDF definition, guarding against circular or deeply nested definitions.
		/// </summary>
		/// <param name="definition">A function dictionary or stream, or an array of 1-output functions.</param>
		/// <param name="depth">The current nesting depth; definitions deeper than 8 levels are rejected.</param>
		/// <returns>The function, or null when the definition is invalid or unsupported.</returns>
		/// <remarks>Errors reading the stream data propagate; <see cref="Create(object)"/> catches them.</remarks>
		private static PdfFunction Create(object definition, int depth)
		{
			if (depth > 8)
				return null;

			if (definition is PdfArray array)
			{
				// an array of n 1-output functions behaves as a single n-output function.
				var functions = new List<PdfFunction>();
				for (int i = 0; i < array.Count; i++)
				{
					var function = Create(array.Resolve(i), depth + 1);
					if (function == null)
						return null;

					functions.Add(function);
				}

				return functions.Count == 0 ? null : new ArrayFunction(functions.ToArray());
			}

			if (!(definition is PdfDictionary dictionary))
				return null;

			var domain = dictionary.GetArray(PdfNames.Domain)?.ToNumbers() ?? new double[] { 0, 1 };
			if (domain.Length < 2)
				domain = new double[] { 0, 1 };

			var range = dictionary.GetArray(PdfNames.Range)?.ToNumbers();

			PdfFunction result;
			switch (dictionary.GetInt(PdfNames.FunctionType, -1))
			{
				case 0:
					result = dictionary is PdfStream stream ? SampledFunction.Create(stream, domain, range) : null;
					break;

				case 2:
					result = new ExponentialFunction(dictionary);
					break;

				case 3:
					result = StitchingFunction.Create(dictionary, domain, depth);
					break;

				case 4:
					result = dictionary is PdfStream program ? PostScriptFunction.Create(program, range) : null;
					break;

				default:
					return null;
			}

			if (result != null)
			{
				result.Domain = domain;
				result.Range = range != null && range.Length >= 2 ? range : null;
			}

			return result;
		}

		#endregion

		#region Type 0: sampled

		/// <summary>
		/// Type 0 function: a table of sample values with multilinear interpolation.
		/// </summary>
		private sealed class SampledFunction : PdfFunction
		{
			/// <summary>The number of samples in each input dimension (<c>/Size</c>).</summary>
			private int[] _size;

			/// <summary>The mapping of each input to sample-table coordinates (<c>/Encode</c>), as min/max pairs.</summary>
			private double[] _encode;

			/// <summary>The mapping of normalized samples to output values (<c>/Decode</c>), as min/max pairs.</summary>
			private double[] _decode;

			/// <summary>The samples normalized to 0..1; the first input dimension varies fastest and the outputs are interleaved.</summary>
			private double[] _samples;

			/// <summary>The number of output values.</summary>
			private int _outputs;

			/// <inheritdoc/>
			public override int OutputCount => _outputs;

			/// <summary>
			/// Creates a sampled function from its stream (ISO 32000-2, 7.10.2).
			/// </summary>
			/// <param name="stream">The function stream holding the sample table.</param>
			/// <param name="domain">The input domain.</param>
			/// <param name="range">The output range; required for type 0 functions.</param>
			/// <returns>The function, or null when the definition is invalid or the table exceeds 16M samples.</returns>
			public static SampledFunction Create(PdfStream stream, double[] domain, double[] range)
			{
				if (range == null || range.Length < 2)
					return null;

				var inputs = domain.Length / 2;
				var outputs = range.Length / 2;
				var size = stream.GetArray(PdfNames.Size);
				if (size == null || size.Count < inputs)
					return null;

				var function = new SampledFunction { _outputs = outputs, _size = new int[inputs] };

				long total = outputs;
				for (int i = 0; i < inputs; i++)
				{
					function._size[i] = Math.Max(1, size.GetInt(i));
					total *= function._size[i];
				}

				// refuse absurd tables.
				if (total > 16 * 1024 * 1024)
					return null;

				function._encode = stream.GetArray(PdfNames.Encode)?.ToNumbers();
				if (function._encode == null || function._encode.Length < inputs * 2)
				{
					function._encode = new double[inputs * 2];
					for (int i = 0; i < inputs; i++)
						function._encode[i * 2 + 1] = function._size[i] - 1;
				}

				function._decode = stream.GetArray(PdfNames.Decode)?.ToNumbers();
				if (function._decode == null || function._decode.Length < outputs * 2)
					function._decode = range;

				// read the samples, big-endian, MSB first.
				var bits = stream.GetInt("BitsPerSample", 8);
				if (bits < 1 || bits > 32 || inputs > 12)
					return null;

				var data = stream.GetBytes();
				var maxValue = bits >= 32 ? uint.MaxValue : (1UL << bits) - 1;
				var samples = new double[total];
				var reader = new BitReader(data);
				for (long i = 0; i < total; i++)
					samples[i] = reader.Read(bits) / (double)maxValue;

				function._samples = samples;
				return function;
			}

			/// <inheritdoc/>
			protected override void EvaluateCore(double[] input, double[] output)
			{
				var inputs = _size.Length;

				// encode the inputs to sample-table coordinates.
				var positions = new double[inputs];
				var floors = new int[inputs];
				var fractions = new double[inputs];
				for (int i = 0; i < inputs; i++)
				{
					var e = Interpolate(input[i], this.Domain[i * 2], this.Domain[i * 2 + 1], _encode[i * 2], _encode[i * 2 + 1]);
					e = Clip(e, 0, _size[i] - 1);
					positions[i] = e;
					floors[i] = Math.Min((int)Math.Floor(e), Math.Max(0, _size[i] - 2));
					fractions[i] = _size[i] == 1 ? 0 : e - floors[i];
				}

				// multilinear interpolation over the 2^inputs corners of the cell.
				var corners = 1 << Math.Min(inputs, 12);
				for (int o = 0; o < _outputs; o++)
				{
					double value = 0;
					for (int corner = 0; corner < corners; corner++)
					{
						double weight = 1;
						long index = 0;
						long stride = _outputs;
						for (int i = 0; i < inputs; i++)
						{
							var high = (corner >> i) & 1;
							var position = floors[i] + high;
							if (position >= _size[i])
								position = _size[i] - 1;

							weight *= high == 1 ? fractions[i] : 1 - fractions[i];
							index += position * stride;
							stride *= _size[i];
						}

						if (weight != 0)
							value += weight * _samples[index + o];
					}

					output[o] = Interpolate(value, 0, 1, _decode[o * 2], _decode[o * 2 + 1]);
				}
			}
		}

		#endregion

		#region Type 2: exponential

		/// <summary>
		/// Type 2 function: <c>C0 + x^N × (C1 − C0)</c>.
		/// </summary>
		private sealed class ExponentialFunction : PdfFunction
		{
			/// <summary>The output at x = 0 (<c>/C0</c>).</summary>
			private readonly double[] _c0;

			/// <summary>The output at x = 1 (<c>/C1</c>).</summary>
			private readonly double[] _c1;

			/// <summary>The interpolation exponent (<c>/N</c>).</summary>
			private readonly double _n;

			/// <summary>
			/// Initializes an exponential interpolation function (ISO 32000-2, 7.10.3).
			/// </summary>
			/// <param name="dictionary">The function dictionary.</param>
			public ExponentialFunction(PdfDictionary dictionary)
			{
				_c0 = dictionary.GetArray(PdfNames.C0)?.ToNumbers() ?? new double[] { 0 };
				_c1 = dictionary.GetArray(PdfNames.C1)?.ToNumbers() ?? new double[] { 1 };
				_n = dictionary.GetNumber(PdfNames.N, 1);

				if (_c1.Length != _c0.Length)
				{
					var length = Math.Min(_c0.Length, _c1.Length);
					Array.Resize(ref _c0, length);
					Array.Resize(ref _c1, length);
				}
			}

			/// <inheritdoc/>
			public override int OutputCount => _c0.Length;

			/// <inheritdoc/>
			protected override void EvaluateCore(double[] input, double[] output)
			{
				var x = input[0];
				var power = _n == 1 ? x : Math.Pow(x, _n);
				if (double.IsNaN(power) || double.IsInfinity(power))
					power = 0;

				for (int i = 0; i < _c0.Length; i++)
					output[i] = _c0[i] + power * (_c1[i] - _c0[i]);
			}
		}

		#endregion

		#region Type 3: stitching

		/// <summary>
		/// Type 3 function: combines k 1-input functions over sub-domains.
		/// </summary>
		private sealed class StitchingFunction : PdfFunction
		{
			/// <summary>The sub-functions (<c>/Functions</c>), one per sub-domain.</summary>
			private PdfFunction[] _functions;

			/// <summary>The k - 1 boundaries between the sub-domains (<c>/Bounds</c>), in increasing order.</summary>
			private double[] _bounds;

			/// <summary>The mapping of each sub-domain to its function's domain (<c>/Encode</c>), as min/max pairs.</summary>
			private double[] _encode;

			/// <inheritdoc/>
			public override int OutputCount => _functions[0].OutputCount;

			/// <summary>
			/// Creates a stitching function from its dictionary (ISO 32000-2, 7.10.4).
			/// </summary>
			/// <param name="dictionary">The function dictionary.</param>
			/// <param name="domain">The input domain (unused here: the factory assigns it after creation).</param>
			/// <param name="depth">The current nesting depth, used when creating the sub-functions.</param>
			/// <returns>The function, or null when the definition or any sub-function is invalid.</returns>
			public static StitchingFunction Create(PdfDictionary dictionary, double[] domain, int depth)
			{
				var array = dictionary.GetArray(PdfNames.Functions);
				if (array == null || array.Count == 0)
					return null;

				var functions = new PdfFunction[array.Count];
				for (int i = 0; i < functions.Length; i++)
				{
					functions[i] = PdfFunction.Create(array.Resolve(i), depth + 1);
					if (functions[i] == null)
						return null;
				}

				var bounds = dictionary.GetArray(PdfNames.Bounds)?.ToNumbers() ?? new double[0];
				var encode = dictionary.GetArray(PdfNames.Encode)?.ToNumbers();
				if (encode == null || encode.Length < functions.Length * 2)
				{
					encode = new double[functions.Length * 2];
					for (int i = 0; i < functions.Length; i++)
						encode[i * 2 + 1] = 1;
				}

				return new StitchingFunction { _functions = functions, _bounds = bounds, _encode = encode };
			}

			/// <inheritdoc/>
			protected override void EvaluateCore(double[] input, double[] output)
			{
				var x = input[0];

				// find the sub-domain that contains x.
				var k = 0;
				while (k < _bounds.Length && k < _functions.Length - 1 && x >= _bounds[k])
					k++;

				var low = k == 0 ? this.Domain[0] : _bounds[k - 1];
				var high = k >= _bounds.Length ? this.Domain[1] : _bounds[k];
				var encoded = Interpolate(x, low, high, _encode[k * 2], _encode[k * 2 + 1]);

				_functions[k].Evaluate(new[] { encoded }, output);
			}
		}

		#endregion

		#region Array of functions

		/// <summary>
		/// Combines n single-output functions into one n-output function.
		/// </summary>
		private sealed class ArrayFunction : PdfFunction
		{
			/// <summary>The single-output functions, one per output value.</summary>
			private readonly PdfFunction[] _functions;

			/// <summary>
			/// Initializes a function that evaluates each of <paramref name="functions"/> and collects their first output.
			/// </summary>
			/// <param name="functions">The functions; at least one. Each one clips the input to its own domain.</param>
			public ArrayFunction(PdfFunction[] functions)
			{
				_functions = functions;
				this.Domain = new double[] { double.MinValue, double.MaxValue };
				var inputs = functions[0].InputCount;
				this.Domain = new double[inputs * 2];
				for (int i = 0; i < inputs; i++)
				{
					this.Domain[i * 2] = double.MinValue;
					this.Domain[i * 2 + 1] = double.MaxValue;
				}
			}

			/// <inheritdoc/>
			public override int OutputCount => _functions.Length;

			/// <inheritdoc/>
			protected override void EvaluateCore(double[] input, double[] output)
			{
				var single = new double[Math.Max(1, _functions[0].OutputCount)];
				for (int i = 0; i < _functions.Length; i++)
				{
					_functions[i].Evaluate(input, single);
					output[i] = single[0];
				}
			}
		}

		#endregion
	}

	/// <summary>
	/// Reads unsigned big-endian values of 1 to 32 bits from a byte array.
	/// </summary>
	internal sealed class BitReader
	{
		/// <summary>The source bytes.</summary>
		private readonly byte[] _data;

		/// <summary>The position of the next bit to read, counted from the most significant bit of the first byte.</summary>
		private long _bitPosition;

		/// <summary>
		/// Initializes a new instance of <see cref="BitReader"/>.
		/// </summary>
		/// <param name="data">The source bytes.</param>
		public BitReader(byte[] data)
		{
			_data = data;
		}

		/// <summary>
		/// Reads the next <paramref name="bits"/> bits; reads past the end return zeros.
		/// </summary>
		/// <param name="bits">The number of bits to read, 1 to 32.</param>
		/// <returns>The value.</returns>
		public uint Read(int bits)
		{
			uint value = 0;
			for (int i = 0; i < bits; i++)
			{
				var byteIndex = _bitPosition >> 3;
				var bit = byteIndex < _data.Length ? (_data[byteIndex] >> (7 - (int)(_bitPosition & 7))) & 1 : 0;
				value = (value << 1) | (uint)bit;
				_bitPosition++;
			}

			return value;
		}

		/// <summary>
		/// Skips to the next byte boundary.
		/// </summary>
		public void Align() => _bitPosition = (_bitPosition + 7) & ~7L;
	}
}
