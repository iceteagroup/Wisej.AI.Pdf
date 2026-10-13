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

// Type 4 (PostScript calculator) functions (ISO 32000-2, 7.10.5).

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.IO;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Graphics
{
	/// <summary>
	/// A type 4 function: a program written in a subset of the PostScript language.
	/// </summary>
	/// <remarks>
	/// The program is compiled once into a flat list of instructions with jump targets for
	/// <c>if</c>/<c>ifelse</c>, then executed with a small value stack.
	/// </remarks>
	internal sealed class PostScriptFunction : PdfFunction
	{
		#region Nested types

		/// <summary>
		/// Opcodes of the compiled program.
		/// </summary>
		private enum Op : byte
		{
			/// <summary>Pushes <see cref="Instruction.Value"/> on the stack.</summary>
			Push,

			/// <summary>Pops a boolean and jumps to <see cref="Instruction.Value"/> when it is false.</summary>
			JumpIfFalse,

			/// <summary>Jumps unconditionally to <see cref="Instruction.Value"/>.</summary>
			Jump,

			// the remaining opcodes are the PostScript operators of the same name (ISO 32000-2, Table 42).

			// arithmetic operators.
			Abs, Add, Atan, Ceiling, Cos, Cvi, Cvr, Div, Exp, Floor, Idiv, Ln, Log, Mod, Mul, Neg, Round, Sin, Sqrt, Sub, Truncate,

			// relational, boolean and bitwise operators.
			And, Bitshift, Eq, False, Ge, Gt, Le, Lt, Ne, Not, Or, True, Xor,

			// stack operators.
			Copy, Dup, Exch, Index, Pop, Roll,
		}

		/// <summary>
		/// A compiled instruction.
		/// </summary>
		private struct Instruction
		{
			/// <summary>The operation.</summary>
			public Op Op;

			/// <summary>The operand: the value for <see cref="Op.Push"/>, the target for jumps.</summary>
			public double Value;
		}

		#endregion

		/// <summary>The maximum stack depth defined by the specification.</summary>
		private const int MaxStack = 100;

		/// <summary>The compiled program.</summary>
		private Instruction[] _program;

		/// <summary>The number of output values, from <c>/Range</c>.</summary>
		private int _outputs;

		/// <summary>The operators of the type 4 subset, keyed by name.</summary>
		private static readonly Dictionary<string, Op> Keywords = new Dictionary<string, Op>(StringComparer.Ordinal)
		{
			["abs"] = Op.Abs,
			["add"] = Op.Add,
			["atan"] = Op.Atan,
			["ceiling"] = Op.Ceiling,
			["cos"] = Op.Cos,
			["cvi"] = Op.Cvi,
			["cvr"] = Op.Cvr,
			["div"] = Op.Div,
			["exp"] = Op.Exp,
			["floor"] = Op.Floor,
			["idiv"] = Op.Idiv,
			["ln"] = Op.Ln,
			["log"] = Op.Log,
			["mod"] = Op.Mod,
			["mul"] = Op.Mul,
			["neg"] = Op.Neg,
			["round"] = Op.Round,
			["sin"] = Op.Sin,
			["sqrt"] = Op.Sqrt,
			["sub"] = Op.Sub,
			["truncate"] = Op.Truncate,
			["and"] = Op.And,
			["bitshift"] = Op.Bitshift,
			["eq"] = Op.Eq,
			["false"] = Op.False,
			["ge"] = Op.Ge,
			["gt"] = Op.Gt,
			["le"] = Op.Le,
			["lt"] = Op.Lt,
			["ne"] = Op.Ne,
			["not"] = Op.Not,
			["or"] = Op.Or,
			["true"] = Op.True,
			["xor"] = Op.Xor,
			["copy"] = Op.Copy,
			["dup"] = Op.Dup,
			["exch"] = Op.Exch,
			["index"] = Op.Index,
			["pop"] = Op.Pop,
			["roll"] = Op.Roll,
		};

		/// <inheritdoc/>
		public override int OutputCount => _outputs;

		/// <summary>
		/// Compiles the type 4 function program in <paramref name="stream"/>.
		/// </summary>
		/// <param name="stream">The function stream.</param>
		/// <param name="range">The output range; required for type 4 functions.</param>
		/// <returns>The function, or null when the program is invalid.</returns>
		public static PostScriptFunction Create(PdfStream stream, double[] range)
		{
			if (range == null || range.Length < 2)
				return null;

			var lexer = new PdfLexer(stream.GetBytes());
			if (lexer.Next() != PdfTokenType.ProcedureStart)
				return null;

			var program = new List<Instruction>();
			if (!Compile(lexer, program, 0))
				return null;

			return new PostScriptFunction { _program = program.ToArray(), _outputs = range.Length / 2 };
		}

		// compiles the body of a procedure up to its closing brace.
		private static bool Compile(PdfLexer lexer, List<Instruction> program, int depth)
		{
			if (depth > 32)
				return false;

			while (true)
			{
				switch (lexer.Next())
				{
					case PdfTokenType.Number:
						program.Add(new Instruction { Op = Op.Push, Value = PdfConvert.ToNumber(lexer.TokenNumber) });
						break;

					case PdfTokenType.ProcedureEnd:
						return true;

					case PdfTokenType.ProcedureStart:
						{
							// "{ proc } if" or "{ proc1 } { proc2 } ifelse".
							var jumpIfFalse = program.Count;
							program.Add(new Instruction { Op = Op.JumpIfFalse });
							if (!Compile(lexer, program, depth + 1))
								return false;

							var position = lexer.Position;
							var next = lexer.Next();
							if (next == PdfTokenType.ProcedureStart)
							{
								var jump = program.Count;
								program.Add(new Instruction { Op = Op.Jump });
								program[jumpIfFalse] = new Instruction { Op = Op.JumpIfFalse, Value = program.Count };

								if (!Compile(lexer, program, depth + 1))
									return false;

								program[jump] = new Instruction { Op = Op.Jump, Value = program.Count };

								if (lexer.Next() != PdfTokenType.Keyword || lexer.TokenText != "ifelse")
									return false;
							}
							else
							{
								program[jumpIfFalse] = new Instruction { Op = Op.JumpIfFalse, Value = program.Count };
								if (next != PdfTokenType.Keyword || lexer.TokenText != "if")
								{
									lexer.Position = position;
									return false;
								}
							}
						}
						break;

					case PdfTokenType.Keyword:
						if (!TryGetOp(lexer.TokenText, out var op))
							return false;

						program.Add(new Instruction { Op = op });
						break;

					default:
						return false;
				}
			}
		}

		/// <summary>
		/// Maps a PostScript operator name to its opcode.
		/// </summary>
		/// <param name="keyword">The operator name.</param>
		/// <param name="op">Receives the opcode.</param>
		/// <returns>True when the operator is part of the type 4 subset.</returns>
		private static bool TryGetOp(string keyword, out Op op)
		{
			return Keywords.TryGetValue(keyword, out op);
		}

		/// <inheritdoc/>
		protected override void EvaluateCore(double[] input, double[] output)
		{
			// booleans are represented as 1 (true) and 0 (false).
			var stack = new double[MaxStack];
			var sp = 0;
			for (int i = 0; i < input.Length && sp < MaxStack; i++)
				stack[sp++] = input[i];

			try
			{
				Run(stack, ref sp);
			}
			catch (Exception)
			{
				// stack underflow or overflow: the outputs are whatever is on the stack.
				sp = Math.Max(0, Math.Min(sp, MaxStack));
			}

			// the outputs are the top n values of the stack.
			var start = sp - _outputs;
			for (int i = 0; i < _outputs; i++)
				output[i] = start + i >= 0 ? stack[start + i] : 0;
		}

		/// <summary>
		/// Executes the compiled program on the value stack.
		/// </summary>
		/// <param name="s">The value stack, <see cref="MaxStack"/> elements; booleans are 1 and 0.</param>
		/// <param name="sp">The stack pointer (number of values on the stack), updated in place.</param>
		/// <exception cref="IndexOutOfRangeException">The program underflows or overflows the stack.</exception>
		private void Run(double[] s, ref int sp)
		{
			var program = _program;
			var pc = 0;
			while (pc < program.Length)
			{
				var instruction = program[pc++];
				switch (instruction.Op)
				{
					case Op.Push:
						s[sp++] = instruction.Value;
						break;

					case Op.JumpIfFalse:
						if (s[--sp] == 0)
							pc = (int)instruction.Value;
						break;

					case Op.Jump:
						pc = (int)instruction.Value;
						break;

					// arithmetic.
					case Op.Abs:
						s[sp - 1] = Math.Abs(s[sp - 1]);
						break;

					case Op.Add:
						sp--;
						s[sp - 1] += s[sp];
						break;

					case Op.Sub:
						sp--;
						s[sp - 1] -= s[sp];
						break;

					case Op.Mul:
						sp--;
						s[sp - 1] *= s[sp];
						break;

					case Op.Div:
						sp--;
						s[sp - 1] = s[sp] == 0 ? 0 : s[sp - 1] / s[sp];
						break;

					case Op.Idiv:
						sp--;
						s[sp - 1] = s[sp] == 0 ? 0 : Math.Truncate(s[sp - 1] / s[sp]);
						break;

					case Op.Mod:
						sp--;
						s[sp - 1] = s[sp] == 0 ? 0 : Math.Truncate(s[sp - 1]) % Math.Truncate(s[sp]);
						break;

					case Op.Neg:
						s[sp - 1] = -s[sp - 1];
						break;

					case Op.Ceiling:
						s[sp - 1] = Math.Ceiling(s[sp - 1]);
						break;

					case Op.Floor:
						s[sp - 1] = Math.Floor(s[sp - 1]);
						break;

					case Op.Round:
						s[sp - 1] = Math.Floor(s[sp - 1] + 0.5);
						break;

					case Op.Truncate:
						s[sp - 1] = Math.Truncate(s[sp - 1]);
						break;

					case Op.Cvi:
						s[sp - 1] = Math.Truncate(s[sp - 1]);
						break;

					case Op.Cvr:
						break;

					case Op.Sqrt:
						s[sp - 1] = Math.Sqrt(Math.Max(0, s[sp - 1]));
						break;

					case Op.Sin:
						// angles are in degrees.
						s[sp - 1] = Math.Sin(s[sp - 1] * Math.PI / 180);
						break;

					case Op.Cos:
						s[sp - 1] = Math.Cos(s[sp - 1] * Math.PI / 180);
						break;

					case Op.Atan:
						{
							// "num den atan" returns an angle in degrees, 0..360.
							sp--;
							var angle = Math.Atan2(s[sp - 1], s[sp]) * 180 / Math.PI;
							s[sp - 1] = angle < 0 ? angle + 360 : angle;
						}
						break;

					case Op.Exp:
						sp--;
						s[sp - 1] = Math.Pow(s[sp - 1], s[sp]);
						break;

					case Op.Ln:
						s[sp - 1] = s[sp - 1] > 0 ? Math.Log(s[sp - 1]) : 0;
						break;

					case Op.Log:
						s[sp - 1] = s[sp - 1] > 0 ? Math.Log10(s[sp - 1]) : 0;
						break;

					// relational, boolean and bitwise.
					case Op.Eq:
						sp--;
						s[sp - 1] = s[sp - 1] == s[sp] ? 1 : 0;
						break;

					case Op.Ne:
						sp--;
						s[sp - 1] = s[sp - 1] != s[sp] ? 1 : 0;
						break;

					case Op.Ge:
						sp--;
						s[sp - 1] = s[sp - 1] >= s[sp] ? 1 : 0;
						break;

					case Op.Gt:
						sp--;
						s[sp - 1] = s[sp - 1] > s[sp] ? 1 : 0;
						break;

					case Op.Le:
						sp--;
						s[sp - 1] = s[sp - 1] <= s[sp] ? 1 : 0;
						break;

					case Op.Lt:
						sp--;
						s[sp - 1] = s[sp - 1] < s[sp] ? 1 : 0;
						break;

					case Op.True:
						s[sp++] = 1;
						break;

					case Op.False:
						s[sp++] = 0;
						break;

					case Op.And:
						sp--;
						s[sp - 1] = (long)s[sp - 1] & (long)s[sp];
						break;

					case Op.Or:
						sp--;
						s[sp - 1] = (long)s[sp - 1] | (long)s[sp];
						break;

					case Op.Xor:
						sp--;
						s[sp - 1] = (long)s[sp - 1] ^ (long)s[sp];
						break;

					case Op.Not:
						// for booleans "not" is logical; for integers it is bitwise.
						s[sp - 1] = s[sp - 1] == 0 ? 1 : s[sp - 1] == 1 ? 0 : ~(long)s[sp - 1];
						break;

					case Op.Bitshift:
						{
							sp--;
							var shift = (int)s[sp];
							var value = (long)s[sp - 1];
							s[sp - 1] = shift >= 0 ? value << Math.Min(shift, 63) : value >> Math.Min(-shift, 63);
						}
						break;

					// stack.
					case Op.Pop:
						sp--;
						break;

					case Op.Dup:
						s[sp] = s[sp - 1];
						sp++;
						break;

					case Op.Exch:
						{
							var t = s[sp - 1];
							s[sp - 1] = s[sp - 2];
							s[sp - 2] = t;
						}
						break;

					case Op.Copy:
						{
							var n = (int)s[--sp];
							if (n < 0 || n > sp)
								throw new IndexOutOfRangeException();

							Array.Copy(s, sp - n, s, sp, n);
							sp += n;
						}
						break;

					case Op.Index:
						{
							var n = (int)s[sp - 1];
							s[sp - 1] = s[sp - 2 - n];
						}
						break;

					case Op.Roll:
						{
							var j = (int)s[--sp];
							var n = (int)s[--sp];
							if (n <= 0 || n > sp)
								break;

							j %= n;
							if (j < 0)
								j += n;

							var segment = new double[n];
							Array.Copy(s, sp - n, segment, 0, n);
							for (int i = 0; i < n; i++)
								s[sp - n + (i + j) % n] = segment[i];
						}
						break;
				}
			}
		}
	}
}
