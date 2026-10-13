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

// Tests of the PDF functions (ISO 32000-2, 7.10): sampled, exponential,
// stitching and PostScript calculator functions.

using Wisej.AI.Pdf.Graphics;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Graphics
{
	/// <summary>
	/// Tests of <see cref="PdfFunction"/>.
	/// </summary>
	public class FunctionTests
	{
		#region Type 0: sampled

		/// <summary>A 1-in, 1-out sampled function interpolates linearly between samples.</summary>
		[Theory]
		[InlineData(0.0, 0.0)]
		[InlineData(0.25, 0.25)]
		[InlineData(0.5, 0.5)]
		[InlineData(1.0, 1.0)]
		[InlineData(2.0, 1.0)]
		[InlineData(-1.0, 0.0)]
		public void Sampled_OneInput_InterpolatesAndClipsDomain(double x, double expected)
		{
			var function = Sampled("/Domain [0 1] /Range [0 1] /Size [3] /BitsPerSample 8", new byte[] { 0, 128, 255 });

			Assert.Equal(expected, Evaluate(function, x)[0], 2);
		}

		/// <summary>A 2-in sampled function interpolates bilinearly over the sample grid.</summary>
		[Fact]
		public void Sampled_TwoInputs_Bilinear()
		{
			// samples at (0,0)=0, (1,0)=255, (0,1)=255, (1,1)=0: the centre is half way.
			var function = Sampled("/Domain [0 1 0 1] /Range [0 1] /Size [2 2] /BitsPerSample 8", new byte[] { 0, 255, 255, 0 });

			Assert.Equal(0.0, Evaluate(function, 0, 0)[0], 3);
			Assert.Equal(1.0, Evaluate(function, 1, 0)[0], 3);
			Assert.Equal(1.0, Evaluate(function, 0, 1)[0], 3);
			Assert.Equal(0.5, Evaluate(function, 0.5, 0.5)[0], 3);
		}

		/// <summary>/Decode maps the samples to the output range, with two interleaved outputs.</summary>
		[Fact]
		public void Sampled_DecodeAndTwoOutputs()
		{
			var function = Sampled("/Domain [0 1] /Range [0 10 0 1] /Decode [10 0 0 1] /Size [2] /BitsPerSample 8",
				new byte[] { 0x00, 0xFF, 0xFF, 0x00 });

			var at0 = Evaluate(function, 0);
			var at1 = Evaluate(function, 1);

			Assert.Equal(10.0, at0[0], 3);
			Assert.Equal(1.0, at0[1], 3);
			Assert.Equal(0.0, at1[0], 3);
			Assert.Equal(0.0, at1[1], 3);
		}

		/// <summary>The sample size comes from /BitsPerSample (ISO 32000-2, Table 39): 16-bit samples.</summary>
		[Fact]
		public void Sampled_BitsPerSample16()
		{
			var function = Sampled("/Domain [0 1] /Range [0 10 0 1] /Decode [10 0 0 1] /Size [2] /BitsPerSample 16",
				new byte[] { 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00 });

			var at0 = Evaluate(function, 0);
			var at1 = Evaluate(function, 1);

			Assert.Equal(10.0, at0[0], 3);
			Assert.Equal(1.0, at0[1], 3);
			Assert.Equal(0.0, at1[0], 3);
			Assert.Equal(0.0, at1[1], 3);
		}

		/// <summary>4-bit samples, as used by compact transfer and tint functions.</summary>
		[Fact]
		public void Sampled_BitsPerSample4()
		{
			// samples 0, 15, 0, 15 packed two per byte: the function alternates between 0 and 1.
			var function = Sampled("/Domain [0 1] /Range [0 1] /Size [4] /BitsPerSample 4", new byte[] { 0x0F, 0x0F });

			Assert.Equal(1.0, Evaluate(function, 1.0 / 3)[0], 3);
			Assert.Equal(0.0, Evaluate(function, 2.0 / 3)[0], 3);
		}

		/// <summary>A sampled function without /Range is invalid.</summary>
		[Fact]
		public void Sampled_WithoutRange_IsRejected()
		{
			Assert.Null(Sampled("/Domain [0 1] /Size [2] /BitsPerSample 8", new byte[] { 0, 255 }));
		}

		#endregion

		#region Type 2: exponential

		/// <summary>An exponential function computes C0 + x^N × (C1 − C0) for each output.</summary>
		[Theory]
		[InlineData(0.0, 0.0, 0.0)]
		[InlineData(0.5, 0.25, 0.125)]
		[InlineData(1.0, 1.0, 0.5)]
		public void Exponential_ComputesPower(double x, double first, double second)
		{
			var function = Create("<< /FunctionType 2 /Domain [0 1] /C0 [0 0] /C1 [1 0.5] /N 2 >>");

			var output = Evaluate(function, x);

			Assert.Equal(first, output[0], 6);
			Assert.Equal(second, output[1], 6);
		}

		/// <summary>Without C0 and C1 the defaults are [0] and [1].</summary>
		[Fact]
		public void Exponential_Defaults()
		{
			var function = Create("<< /FunctionType 2 /Domain [0 1] /N 1 >>");

			Assert.Equal(1, function.OutputCount);
			Assert.Equal(0.3, Evaluate(function, 0.3)[0], 6);
		}

		#endregion

		#region Type 3: stitching

		/// <summary>A stitching function selects the sub-function by /Bounds and maps the input with /Encode.</summary>
		[Theory]
		[InlineData(0.0, 0.0)]
		[InlineData(0.25, 0.5)]
		[InlineData(0.5, 1.0)]
		[InlineData(0.75, 0.5)]
		[InlineData(1.0, 0.0)]
		public void Stitching_SelectsAndEncodes(double x, double expected)
		{
			// a rising ramp on [0, 0.5) and a falling ramp on [0.5, 1] (Encode [1 0] reverses the second).
			var function = Create(
				"<< /FunctionType 3 /Domain [0 1] /Bounds [0.5] /Encode [0 1 1 0] /Functions [" +
				"<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> " +
				"<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>] >>");

			Assert.Equal(expected, Evaluate(function, x)[0], 6);
		}

		/// <summary>An array of 1-output functions behaves as one function with several outputs.</summary>
		[Fact]
		public void FunctionArray_CombinesOutputs()
		{
			var function = Create(
				"[<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>]");

			var output = Evaluate(function, 0.25);

			Assert.Equal(2, function.OutputCount);
			Assert.Equal(0.25, output[0], 6);
			Assert.Equal(0.75, output[1], 6);
		}

		#endregion

		#region Type 4: PostScript calculator

		/// <summary>Arithmetic operators.</summary>
		[Theory]
		[InlineData("{ 2 mul 1 add }", 0.25, 1.5)]
		[InlineData("{ 3 sub abs }", 1.0, 2.0)]
		[InlineData("{ 4 div neg }", 1.0, -0.25)]
		[InlineData("{ 7 3 idiv add }", 1.0, 3.0)]
		[InlineData("{ 7 3 mod add }", 1.0, 2.0)]
		[InlineData("{ 2.6 add round }", 0.0, 3.0)]
		[InlineData("{ 2.6 add truncate }", 0.0, 2.0)]
		[InlineData("{ 1.2 add floor }", 0.0, 1.0)]
		[InlineData("{ 1.2 add ceiling }", 0.0, 2.0)]
		[InlineData("{ 16 add sqrt }", 0.0, 4.0)]
		[InlineData("{ 2 exch exp }", 3.0, 8.0)]
		[InlineData("{ 90 add sin }", 0.0, 1.0)]
		[InlineData("{ 1 atan }", 1.0, 45.0)]
		[InlineData("{ 100 add log }", 0.0, 2.0)]
		public void PostScript_Arithmetic(string program, double x, double expected)
		{
			Assert.Equal(expected, Evaluate(PostScript(program, "[-1000 1000]"), x)[0], 6);
		}

		/// <summary>if and ifelse select a branch from a comparison.</summary>
		[Theory]
		[InlineData(0.2, 0.0)]
		[InlineData(0.8, 1.0)]
		public void PostScript_IfElse(double x, double expected)
		{
			var function = PostScript("{ 0.5 gt { 1 } { 0 } ifelse }", "[0 1]");

			Assert.Equal(expected, Evaluate(function, x)[0], 6);
		}

		/// <summary>An if without else leaves the stack unchanged when the condition is false.</summary>
		[Theory]
		[InlineData(0.2, 0.2)]
		[InlineData(0.8, 0.4)]
		public void PostScript_If(double x, double expected)
		{
			var function = PostScript("{ dup 0.5 ge { 2 div } if }", "[0 1]");

			Assert.Equal(expected, Evaluate(function, x)[0], 6);
		}

		/// <summary>Nested conditionals, and boolean operators combining comparisons.</summary>
		[Theory]
		[InlineData(0.1, 0.0)]
		[InlineData(0.5, 0.5)]
		[InlineData(0.9, 1.0)]
		public void PostScript_NestedConditionals(double x, double expected)
		{
			var function = PostScript("{ dup 0.3 lt { pop 0 } { dup 0.7 gt { pop 1 } { dup 0.4 ge exch 0.6 le and { 0.5 } { 0.25 } ifelse } ifelse } ifelse }", "[0 1]");

			Assert.Equal(expected, Evaluate(function, x)[0], 6);
		}

		/// <summary>roll rotates the top n elements of the stack by j positions.</summary>
		[Fact]
		public void PostScript_Roll()
		{
			var function = PostScript("{ pop 1 2 3 3 1 roll }", "[0 10 0 10 0 10]");

			Assert.Equal(new[] { 3.0, 1.0, 2.0 }, Evaluate(function, 0));
		}

		/// <summary>A negative roll rotates in the other direction.</summary>
		[Fact]
		public void PostScript_RollNegative()
		{
			var function = PostScript("{ pop 1 2 3 3 -1 roll }", "[0 10 0 10 0 10]");

			Assert.Equal(new[] { 2.0, 3.0, 1.0 }, Evaluate(function, 0));
		}

		/// <summary>index copies the n-th element below the top; copy and exch manipulate the stack.</summary>
		[Fact]
		public void PostScript_IndexCopyExch()
		{
			var function = PostScript("{ pop 10 20 30 2 index 2 copy pop exch pop }", "[0 100 0 100 0 100 0 100]");

			// 10 20 30 → index: 10 20 30 10 → copy 2: 10 20 30 10 30 10 → pop: 10 20 30 10 30 → exch: 10 20 30 30 10 → pop: 10 20 30 30.
			Assert.Equal(new[] { 10.0, 20.0, 30.0, 30.0 }, Evaluate(function, 0));
		}

		/// <summary>Results outside /Range are clipped.</summary>
		[Fact]
		public void PostScript_OutputIsClippedToRange()
		{
			Assert.Equal(1.0, Evaluate(PostScript("{ 10 mul }", "[0 1]"), 0.5)[0], 6);
		}

		/// <summary>A stack underflow doesn't throw: the function returns what is on the stack.</summary>
		[Fact]
		public void PostScript_StackUnderflow_DoesNotThrow()
		{
			var function = PostScript("{ pop pop pop add }", "[0 1]");

			var output = Evaluate(function, 0.5);

			Assert.Single(output);
		}

		/// <summary>An unknown operator makes the program invalid.</summary>
		[Fact]
		public void PostScript_UnknownOperator_IsRejected()
		{
			Assert.Null(PostScript("{ 1 frobnicate }", "[0 1]"));
		}

		#endregion

		/// <summary>
		/// Creates a function from a direct object.
		/// </summary>
		/// <param name="definition">The function dictionary or array in PDF syntax.</param>
		/// <returns>The function.</returns>
		private static PdfFunction Create(string definition)
		{
			var function = PdfFunction.Create(TestPdf.ParseObject(definition));
			Assert.NotNull(function);
			return function;
		}

		/// <summary>
		/// Creates a sampled (type 0) function.
		/// </summary>
		/// <param name="entries">The dictionary entries, without /FunctionType.</param>
		/// <param name="samples">The sample data.</param>
		/// <returns>The function, or null when the definition is invalid.</returns>
		private static PdfFunction Sampled(string entries, byte[] samples)
		{
			return PdfFunction.Create(TestPdf.ParseStream("/FunctionType 0 " + entries, samples));
		}

		/// <summary>
		/// Creates a PostScript calculator (type 4) function with one input in [0, 1].
		/// </summary>
		/// <param name="program">The program, including the braces.</param>
		/// <param name="range">The /Range array.</param>
		/// <returns>The function, or null when the program is invalid.</returns>
		private static PdfFunction PostScript(string program, string range)
		{
			return PdfFunction.Create(TestPdf.ParseStream($"/FunctionType 4 /Domain [-1000 1000] /Range {range}", program));
		}

		/// <summary>
		/// Evaluates a function.
		/// </summary>
		/// <param name="function">The function.</param>
		/// <param name="input">The input values.</param>
		/// <returns>The output values.</returns>
		private static double[] Evaluate(PdfFunction function, params double[] input)
		{
			var output = new double[function.OutputCount];
			function.Evaluate(input, output);
			return output;
		}
	}
}
