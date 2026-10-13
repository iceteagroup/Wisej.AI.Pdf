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

// Interpreter for Type 2 charstrings, the glyph programs of CFF fonts (Adobe Technical Note #5177).

using System;

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// Executes a Type 2 charstring and records its outline, advance width and accent composition.
	/// </summary>
	/// <remarks>
	/// Hints are parsed only to skip the hintmask bytes and to find the width; they do not affect the outline.
	/// An instance is not thread-safe: create one per glyph.
	/// </remarks>
	internal sealed class Type2CharString
	{
		#region Fields

		// the operand stack limit; the spec says 48, 513 tolerates CFF2-era producers.
		private const int StackLimit = 513;

		// the spec limits subroutine nesting to 10 levels.
		private const int MaxSubrDepth = 10;

		// upper bound on executed operators, to stop subroutine loops in corrupt fonts.
		private const int MaxOperations = 100000;

		// the CFF data holding the charstrings and subroutines.
		private readonly byte[] _data;

		// the local subroutines (Private DICT Subrs).
		private readonly CffIndex _localSubrs;

		// the global subroutines (Global Subr INDEX).
		private readonly CffIndex _globalSubrs;

		// the bias added to callsubr operands: 107, 1131 or 32768 depending on the subroutine count (TN #5177, 4.7).
		private readonly int _localBias;

		// the bias added to callgsubr operands, computed the same way from the global subroutine count.
		private readonly int _globalBias;

		// nominalWidthX from the Private DICT: the base of the width operand of a charstring.
		private readonly double _nominalWidthX;

		// the operand stack.
		private readonly double[] _stack = new double[StackLimit];

		// the number of operands on the stack.
		private int _sp;

		// the transient array of the put/get operators.
		private readonly double[] _transient = new double[32];

		// the current point.
		private double _x, _y;

		// the number of stem hints declared, used to size hintmask data.
		private int _stems;

		// true once the first stack-clearing operator has consumed (or not) the width.
		private bool _widthParsed;

		// true after endchar.
		private bool _ended;

		// number of operators executed so far.
		private int _operations;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes an interpreter.
		/// </summary>
		/// <param name="data">The CFF data.</param>
		/// <param name="localSubrs">The local subroutines of the glyph's Private DICT.</param>
		/// <param name="globalSubrs">The global subroutines.</param>
		/// <param name="defaultWidthX">The width used when the charstring has none.</param>
		/// <param name="nominalWidthX">The base added to an explicit width.</param>
		/// <param name="builder">Receives the outline.</param>
		public Type2CharString(byte[] data, CffIndex localSubrs, CffIndex globalSubrs, double defaultWidthX, double nominalWidthX, GlyphPathBuilder builder)
		{
			_data = data;
			_localSubrs = localSubrs ?? CffIndex.Empty;
			_globalSubrs = globalSubrs ?? CffIndex.Empty;
			_localBias = Bias(_localSubrs.Count);
			_globalBias = Bias(_globalSubrs.Count);
			_nominalWidthX = nominalWidthX;
			this.Width = defaultWidthX;
			this.Builder = builder;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the outline builder.
		/// </summary>
		public GlyphPathBuilder Builder { get; }

		/// <summary>
		/// Returns the advance width after <see cref="Execute"/>.
		/// </summary>
		public double Width { get; private set; }

		/// <summary>
		/// Returns true when the glyph ended with the seac form of endchar, an accented composite.
		/// </summary>
		public bool IsSeac { get; private set; }

		/// <summary>
		/// Returns the horizontal offset of the accent of a seac composite.
		/// </summary>
		public double AccentX { get; private set; }

		/// <summary>
		/// Returns the vertical offset of the accent of a seac composite.
		/// </summary>
		public double AccentY { get; private set; }

		/// <summary>
		/// Returns the StandardEncoding code of the base glyph of a seac composite.
		/// </summary>
		public int BaseCode { get; private set; }

		/// <summary>
		/// Returns the StandardEncoding code of the accent glyph of a seac composite.
		/// </summary>
		public int AccentCode { get; private set; }

		#endregion

		#region Methods

		/// <summary>
		/// Returns the subroutine number bias for a subroutine INDEX of <paramref name="count"/> entries.
		/// </summary>
		/// <param name="count">The number of subroutines.</param>
		/// <returns>The bias.</returns>
		public static int Bias(int count)
		{
			return count < 1240 ? 107 : count < 33900 ? 1131 : 32768;
		}

		/// <summary>
		/// Runs the charstring in [<paramref name="start"/>, <paramref name="end"/>).
		/// </summary>
		/// <param name="start">The first byte of the charstring.</param>
		/// <param name="end">The end of the charstring (exclusive).</param>
		public void Execute(int start, int end)
		{
			Run(start, end, 0);
		}

		#endregion

		#region Implementation

		// interprets a charstring or subroutine; returns at 'return', 'endchar' or the end of the data.
		private void Run(int p, int end, int depth)
		{
			end = Math.Min(end, _data.Length);
			while (p < end && !_ended)
			{
				if (++_operations > MaxOperations)
				{
					_ended = true;
					return;
				}

				int b0 = _data[p++];

				// operands.
				if (b0 >= 32)
				{
					double value;
					if (b0 <= 246)
					{
						value = b0 - 139;
					}
					else if (b0 <= 250)
					{
						value = (b0 - 247) * 256 + BigEndian.U8(_data, p++) + 108;
					}
					else if (b0 <= 254)
					{
						value = -(b0 - 251) * 256 - BigEndian.U8(_data, p++) - 108;
					}
					else
					{
						// 16.16 fixed point.
						value = BigEndian.S32(_data, p) / 65536.0;
						p += 4;
					}

					Push(value);
					continue;
				}

				if (b0 == 28)
				{
					Push(BigEndian.S16(_data, p));
					p += 2;
					continue;
				}

				switch (b0)
				{
					case 1: // hstem
					case 3: // vstem
					case 18: // hstemhm
					case 23: // vstemhm
						ParseWidth(_sp % 2 != 0);
						_stems += _sp / 2;
						_sp = 0;
						break;

					case 19: // hintmask
					case 20: // cntrmask
						// operands before the first hintmask are an implicit vstem.
						ParseWidth(_sp % 2 != 0);
						_stems += _sp / 2;
						_sp = 0;
						p += (_stems + 7) / 8;
						break;

					case 21: // rmoveto
						ParseWidth(_sp > 2);
						_x += Arg(0);
						_y += Arg(1);
						this.Builder.MoveTo((float)_x, (float)_y);
						_sp = 0;
						break;

					case 22: // hmoveto
						ParseWidth(_sp > 1);
						_x += Arg(0);
						this.Builder.MoveTo((float)_x, (float)_y);
						_sp = 0;
						break;

					case 4: // vmoveto
						ParseWidth(_sp > 1);
						_y += Arg(0);
						this.Builder.MoveTo((float)_x, (float)_y);
						_sp = 0;
						break;

					case 5: // rlineto
						for (int i = 0; i + 1 < _sp; i += 2)
							LineTo(_stack[i], _stack[i + 1]);

						_sp = 0;
						break;

					case 6: // hlineto
					case 7: // vlineto
					{
						var horizontal = b0 == 6;
						for (int i = 0; i < _sp; i++)
						{
							if (horizontal)
								LineTo(_stack[i], 0);
							else
								LineTo(0, _stack[i]);

							horizontal = !horizontal;
						}

						_sp = 0;
						break;
					}

					case 8: // rrcurveto
						for (int i = 0; i + 5 < _sp; i += 6)
							CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);

						_sp = 0;
						break;

					case 24: // rcurveline
					{
						var i = 0;
						for (; i + 5 < _sp - 2; i += 6)
							CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);

						if (i + 1 < _sp)
							LineTo(_stack[i], _stack[i + 1]);

						_sp = 0;
						break;
					}

					case 25: // rlinecurve
					{
						var i = 0;
						for (; i + 1 < _sp - 6; i += 2)
							LineTo(_stack[i], _stack[i + 1]);

						if (i + 5 < _sp)
							CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);

						_sp = 0;
						break;
					}

					case 26: // vvcurveto
					{
						var i = 0;
						var dx1 = 0.0;
						if (_sp % 2 != 0)
							dx1 = _stack[i++];

						for (; i + 3 < _sp; i += 4)
						{
							CurveTo(dx1, _stack[i], _stack[i + 1], _stack[i + 2], 0, _stack[i + 3]);
							dx1 = 0;
						}
						_sp = 0;
						break;
					}

					case 27: // hhcurveto
					{
						var i = 0;
						var dy1 = 0.0;
						if (_sp % 2 != 0)
							dy1 = _stack[i++];

						for (; i + 3 < _sp; i += 4)
						{
							CurveTo(_stack[i], dy1, _stack[i + 1], _stack[i + 2], _stack[i + 3], 0);
							dy1 = 0;
						}
						_sp = 0;
						break;
					}

					case 30: // vhcurveto
					case 31: // hvcurveto
					{
						var horizontal = b0 == 31;
						for (int i = 0; i + 3 < _sp; i += 4)
						{
							// the optional last operand is the final delta along the other axis.
							var last = i + 5 == _sp ? _stack[i + 4] : 0;
							if (horizontal)
								CurveTo(_stack[i], 0, _stack[i + 1], _stack[i + 2], last, _stack[i + 3]);
							else
								CurveTo(0, _stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], last);

							horizontal = !horizontal;
						}
						_sp = 0;
						break;
					}

					case 10: // callsubr
					case 29: // callgsubr
					{
						if (_sp < 1)
							break;

						var subrs = b0 == 10 ? _localSubrs : _globalSubrs;
						var index = (int)_stack[--_sp] + (b0 == 10 ? _localBias : _globalBias);
						if (depth < MaxSubrDepth && subrs.TryGetRange(index, out var subrStart, out var subrEnd))
							Run(subrStart, subrEnd, depth + 1);
						break;
					}

					case 11: // return
						return;

					case 14: // endchar
						EndChar();
						return;

					case 12:
						EscapedOperator(BigEndian.U8(_data, p++));
						break;

					default:
						// reserved operators: discard the operands.
						_sp = 0;
						break;
				}
			}
		}

		// endchar, with the optional width and the deprecated seac accent composition.
		private void EndChar()
		{
			ParseWidth(_sp == 1 || _sp == 5);
			if (_sp >= 4)
			{
				this.IsSeac = true;
				this.AccentX = _stack[_sp - 4];
				this.AccentY = _stack[_sp - 3];
				this.BaseCode = (int)_stack[_sp - 2];
				this.AccentCode = (int)_stack[_sp - 1];
			}

			_sp = 0;
			_ended = true;
		}

		// the two-byte operators: flex variants and arithmetic.
		private void EscapedOperator(int op)
		{
			switch (op)
			{
				case 34: // hflex
				{
					if (_sp < 7)
						break;

					var s = _stack;
					var y0 = _y;
					CurveTo(s[0], 0, s[1], s[2], s[3], 0);
					CurveTo(s[4], 0, s[5], y0 - _y, s[6], 0);
					break;
				}

				case 35: // flex
				{
					if (_sp < 12)
						break;

					var s = _stack;
					CurveTo(s[0], s[1], s[2], s[3], s[4], s[5]);
					CurveTo(s[6], s[7], s[8], s[9], s[10], s[11]);
					break;
				}

				case 36: // hflex1
				{
					if (_sp < 9)
						break;

					var s = _stack;
					var y0 = _y;
					CurveTo(s[0], s[1], s[2], s[3], s[4], 0);
					CurveTo(s[5], 0, s[6], s[7], s[8], y0 - (_y + s[7]));
					break;
				}

				case 37: // flex1
				{
					if (_sp < 11)
						break;

					var s = _stack;
					var x0 = _x;
					var y0 = _y;
					var dx = s[0] + s[2] + s[4] + s[6] + s[8];
					var dy = s[1] + s[3] + s[5] + s[7] + s[9];

					// the last delta goes along the axis with the larger total change.
					double dx6, dy6;
					if (Math.Abs(dx) > Math.Abs(dy))
					{
						dx6 = s[10];
						dy6 = -dy;
					}
					else
					{
						dx6 = -dx;
						dy6 = s[10];
					}

					CurveTo(s[0], s[1], s[2], s[3], s[4], s[5]);
					var ex = x0 + dx + dx6;
					var ey = y0 + dy + dy6;
					var c1x = _x + s[6];
					var c1y = _y + s[7];
					var c2x = c1x + s[8];
					var c2y = c1y + s[9];
					this.Builder.CurveTo((float)c1x, (float)c1y, (float)c2x, (float)c2y, (float)ex, (float)ey);
					_x = ex;
					_y = ey;
					break;
				}

				case 3: // and
					Binary((a, b) => a != 0 && b != 0 ? 1 : 0);
					return;

				case 4: // or
					Binary((a, b) => a != 0 || b != 0 ? 1 : 0);
					return;

				case 5: // not
					if (_sp >= 1)
						_stack[_sp - 1] = _stack[_sp - 1] == 0 ? 1 : 0;

					return;

				case 9: // abs
					if (_sp >= 1)
						_stack[_sp - 1] = Math.Abs(_stack[_sp - 1]);

					return;

				case 10: // add
					Binary((a, b) => a + b);
					return;

				case 11: // sub
					Binary((a, b) => a - b);
					return;

				case 12: // div
					Binary((a, b) => b != 0 ? a / b : 0);
					return;

				case 14: // neg
					if (_sp >= 1)
						_stack[_sp - 1] = -_stack[_sp - 1];

					return;

				case 15: // eq
					Binary((a, b) => a == b ? 1 : 0);
					return;

				case 18: // drop
					if (_sp >= 1)
						_sp--;

					return;

				case 20: // put
					if (_sp >= 2)
					{
						var index = (int)_stack[_sp - 1];
						if (index >= 0 && index < _transient.Length)
							_transient[index] = _stack[_sp - 2];

						_sp -= 2;
					}
					return;

				case 21: // get
					if (_sp >= 1)
					{
						var index = (int)_stack[_sp - 1];
						_stack[_sp - 1] = index >= 0 && index < _transient.Length ? _transient[index] : 0;
					}
					return;

				case 22: // ifelse
					if (_sp >= 4)
					{
						var v2 = _stack[_sp - 1];
						var v1 = _stack[_sp - 2];
						var s2 = _stack[_sp - 3];
						var s1 = _stack[_sp - 4];
						_sp -= 4;
						Push(v1 <= v2 ? s1 : s2);
					}
					return;

				case 23: // random: any value in (0, 1]; a constant keeps the output deterministic.
					Push(0.5);
					return;

				case 24: // mul
					Binary((a, b) => a * b);
					return;

				case 26: // sqrt
					if (_sp >= 1)
						_stack[_sp - 1] = Math.Sqrt(Math.Abs(_stack[_sp - 1]));

					return;

				case 27: // dup
					if (_sp >= 1)
						Push(_stack[_sp - 1]);

					return;

				case 28: // exch
					if (_sp >= 2)
					{
						var t = _stack[_sp - 1];
						_stack[_sp - 1] = _stack[_sp - 2];
						_stack[_sp - 2] = t;
					}
					return;

				case 29: // index
					if (_sp >= 1)
					{
						var i = (int)_stack[_sp - 1];
						if (i < 0)
							i = 0;

						var source = _sp - 2 - i;
						_stack[_sp - 1] = source >= 0 ? _stack[source] : 0;
					}
					return;

				case 30: // roll
					Roll();
					return;
			}

			// dotsection, flex operators and reserved operators clear the stack.
			_sp = 0;
		}

		// roll: N J roll performs a circular shift of the top N elements by J positions.
		private void Roll()
		{
			if (_sp < 2)
				return;

			var j = (int)_stack[--_sp];
			var n = (int)_stack[--_sp];
			if (n <= 0 || n > _sp)
				return;

			j %= n;
			if (j < 0)
				j += n;

			if (j == 0)
				return;

			var start = _sp - n;
			var copy = new double[n];
			for (int i = 0; i < n; i++)
				copy[(i + j) % n] = _stack[start + i];

			Array.Copy(copy, 0, _stack, start, n);
		}

		// applies a binary operator to the two top operands.
		private void Binary(Func<double, double, double> op)
		{
			if (_sp < 2)
				return;

			var b = _stack[--_sp];
			var a = _stack[_sp - 1];
			_stack[_sp - 1] = op(a, b);
		}

		// takes the width from the bottom of the stack when the first stack-clearing operator has an extra operand.
		private void ParseWidth(bool hasWidth)
		{
			if (_widthParsed)
				return;

			_widthParsed = true;
			if (hasWidth && _sp > 0)
			{
				this.Width = _nominalWidthX + _stack[0];
				Array.Copy(_stack, 1, _stack, 0, _sp - 1);
				_sp--;
			}
		}

		// returns an operand, or 0 when missing.
		private double Arg(int index)
		{
			return index < _sp ? _stack[index] : 0;
		}

		// pushes an operand; on overflow the value is dropped.
		private void Push(double value)
		{
			if (_sp < StackLimit)
				_stack[_sp++] = value;
		}

		// draws a relative line.
		private void LineTo(double dx, double dy)
		{
			_x += dx;
			_y += dy;
			this.Builder.LineTo((float)_x, (float)_y);
		}

		// draws a relative curve: each delta is relative to the previous point.
		private void CurveTo(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
		{
			var x1 = _x + dx1;
			var y1 = _y + dy1;
			var x2 = x1 + dx2;
			var y2 = y1 + dy2;
			_x = x2 + dx3;
			_y = y2 + dy3;
			this.Builder.CurveTo((float)x1, (float)y1, (float)x2, (float)y2, (float)_x, (float)_y);
		}

		#endregion
	}
}
