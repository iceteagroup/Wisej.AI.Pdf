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

// Interpreter for Type 1 charstrings (Adobe Type 1 Font Format, chapter 6), including
// the flex and hint replacement OtherSubrs (chapter 8).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// Executes a decrypted Type 1 charstring and records its outline, side bearing, advance width and
	/// accent composition.
	/// </summary>
	/// <remarks>
	/// Hint operators are parsed and ignored. An instance is not thread-safe: create one per glyph.
	/// </remarks>
	internal sealed class Type1CharString
	{
		#region Fields

		// the operand stack limit is 24 in the spec; a larger stack tolerates sloppy producers.
		private const int StackLimit = 64;

		// the spec limits subroutine nesting to 10 levels.
		private const int MaxSubrDepth = 10;

		// upper bound on executed operators, to stop subroutine loops in corrupt fonts.
		private const int MaxOperations = 100000;

		// the decrypted subroutines; entries may be null.
		private readonly IReadOnlyList<byte[]> _subrs;

		// the operand stack.
		private readonly double[] _stack = new double[StackLimit];

		// the number of operands on the stack.
		private int _sp;

		// the PostScript interpreter stack used to return OtherSubr results to 'pop'.
		private readonly Stack<double> _psStack = new Stack<double>();

		// the current point.
		private double _x, _y;

		// flex state: true between OtherSubrs 1 and 0 (Type 1 Font Format, 8.3).
		private bool _flexing;

		// the flex reference point and control points recorded by OtherSubr 2, as x, y pairs.
		private readonly List<double> _flexPoints = new List<double>(14);

		// true after endchar or seac.
		private bool _ended;

		// number of operators executed so far.
		private int _operations;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes an interpreter.
		/// </summary>
		/// <param name="subrs">The decrypted subroutines.</param>
		/// <param name="builder">Receives the outline.</param>
		public Type1CharString(IReadOnlyList<byte[]> subrs, GlyphPathBuilder builder)
		{
			_subrs = subrs ?? Array.Empty<byte[]>();
			this.Builder = builder;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the outline builder.
		/// </summary>
		public GlyphPathBuilder Builder { get; }

		/// <summary>
		/// Returns the advance width set by hsbw or sbw.
		/// </summary>
		public double Width { get; private set; }

		/// <summary>
		/// Returns the left side bearing set by hsbw or sbw.
		/// </summary>
		public double SideBearingX { get; private set; }

		/// <summary>
		/// Returns true when the glyph is a seac accented composite.
		/// </summary>
		public bool IsSeac { get; private set; }

		/// <summary>
		/// Returns the horizontal offset of the accent origin, already adjusted for the side bearings.
		/// </summary>
		public double AccentX { get; private set; }

		/// <summary>
		/// Returns the vertical offset of the accent origin.
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
		/// Runs a decrypted charstring, without the lenIV prefix.
		/// </summary>
		/// <param name="charString">The charstring bytes.</param>
		public void Execute(byte[] charString)
		{
			if (charString != null)
				Run(charString, 0);
		}

		#endregion

		#region Implementation

		// interprets a charstring or subroutine; returns at 'return', 'endchar' or the end of the data.
		private void Run(byte[] data, int depth)
		{
			var p = 0;
			while (p < data.Length && !_ended)
			{
				if (++_operations > MaxOperations)
				{
					_ended = true;
					return;
				}

				int v = data[p++];
				if (v >= 32)
				{
					double value;
					if (v <= 246)
					{
						value = v - 139;
					}
					else if (v <= 250)
					{
						value = (v - 247) * 256 + BigEndian.U8(data, p++) + 108;
					}
					else if (v <= 254)
					{
						value = -(v - 251) * 256 - BigEndian.U8(data, p++) - 108;
					}
					else
					{
						// a 32-bit signed integer (not 16.16 fixed as in Type 2).
						value = BigEndian.S32(data, p);
						p += 4;
					}

					Push(value);
					continue;
				}

				switch (v)
				{
					case 1: // hstem
					case 3: // vstem
						_sp = 0;
						break;

					case 4: // vmoveto
						MoveTo(0, Arg(0));
						break;

					case 5: // rlineto
						LineTo(Arg(0), Arg(1));
						break;

					case 6: // hlineto
						LineTo(Arg(0), 0);
						break;

					case 7: // vlineto
						LineTo(0, Arg(0));
						break;

					case 8: // rrcurveto
						CurveTo(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4), Arg(5));
						break;

					case 9: // closepath
						this.Builder.ClosePath();
						_sp = 0;
						break;

					case 10: // callsubr
					{
						if (_sp < 1)
							break;

						var index = (int)_stack[--_sp];
						if (depth < MaxSubrDepth && index >= 0 && index < _subrs.Count && _subrs[index] != null)
							Run(_subrs[index], depth + 1);
						break;
					}

					case 11: // return
						return;

					case 13: // hsbw
						this.SideBearingX = Arg(0);
						this.Width = Arg(1);
						_x = this.SideBearingX;
						_y = 0;
						_sp = 0;
						break;

					case 14: // endchar
						_sp = 0;
						_ended = true;
						return;

					case 21: // rmoveto
						MoveTo(Arg(0), Arg(1));
						break;

					case 22: // hmoveto
						MoveTo(Arg(0), 0);
						break;

					case 30: // vhcurveto
						CurveTo(0, Arg(0), Arg(1), Arg(2), Arg(3), 0);
						break;

					case 31: // hvcurveto
						CurveTo(Arg(0), 0, Arg(1), Arg(2), 0, Arg(3));
						break;

					case 12:
						EscapedOperator(BigEndian.U8(data, p++));
						break;

					default:
						_sp = 0;
						break;
				}
			}
		}

		// the two-byte operators.
		private void EscapedOperator(int op)
		{
			switch (op)
			{
				case 6: // seac
					if (_sp >= 5)
					{
						// the accent origin is offset by the composite's side bearing minus the accent's.
						this.IsSeac = true;
						this.AccentX = _stack[_sp - 4] + this.SideBearingX - _stack[_sp - 5];
						this.AccentY = _stack[_sp - 3];
						this.BaseCode = (int)_stack[_sp - 2];
						this.AccentCode = (int)_stack[_sp - 1];
					}

					_sp = 0;
					_ended = true;
					return;

				case 7: // sbw
					this.SideBearingX = Arg(0);
					this.Width = Arg(2);
					_x = this.SideBearingX;
					_y = Arg(1);
					_sp = 0;
					return;

				case 12: // div
					if (_sp >= 2)
					{
						var b = _stack[--_sp];
						var a = _stack[_sp - 1];
						_stack[_sp - 1] = b != 0 ? a / b : 0;
					}
					return;

				case 16: // callothersubr
					CallOtherSubr();
					return;

				case 17: // pop: moves an OtherSubr result back to the operand stack.
					Push(_psStack.Count > 0 ? _psStack.Pop() : 0);
					return;

				case 33: // setcurrentpoint
					if (_sp >= 2)
					{
						_x = _stack[_sp - 2];
						_y = _stack[_sp - 1];
					}
					_sp = 0;
					return;
			}

			// dotsection, vstem3, hstem3 and reserved operators.
			_sp = 0;
		}

		// callothersubr: emulates the standard OtherSubrs 0-3 and passes the others' arguments through.
		private void CallOtherSubr()
		{
			if (_sp < 2)
			{
				_sp = 0;
				return;
			}

			var number = (int)_stack[--_sp];
			var count = (int)_stack[--_sp];
			if (count < 0 || count > _sp)
				count = _sp;

			var args = new double[count];
			Array.Copy(_stack, _sp - count, args, 0, count);
			_sp -= count;

			switch (number)
			{
				case 0:
				{
					// end of flex: draw the two curves through the six points after the reference point.
					_flexing = false;
					var f = _flexPoints;
					if (f.Count >= 14)
					{
						this.Builder.CurveTo((float)f[2], (float)f[3], (float)f[4], (float)f[5], (float)f[6], (float)f[7]);
						this.Builder.CurveTo((float)f[8], (float)f[9], (float)f[10], (float)f[11], (float)f[12], (float)f[13]);
						_x = f[12];
						_y = f[13];
					}
					else if (count >= 3)
					{
						_x = args[1];
						_y = args[2];
						this.Builder.LineTo((float)_x, (float)_y);
					}

					// 'pop pop setcurrentpoint' follows: the first pop returns x.
					_psStack.Push(_y);
					_psStack.Push(_x);
					_flexPoints.Clear();
					break;
				}

				case 1:
					// start of flex: the following moves only record points.
					_flexing = true;
					_flexPoints.Clear();
					break;

				case 2:
					// record a flex point.
					if (_flexing && _flexPoints.Count < 14)
					{
						_flexPoints.Add(_x);
						_flexPoints.Add(_y);
					}
					break;

				case 3:
					// hint replacement: return the subroutine number, so 'pop callsubr' runs it.
					_psStack.Push(count > 0 ? args[0] : 3);
					break;

				default:
				{
					// multiple master blends (14-18) return their first k arguments, the default instance values;
					// other OtherSubrs return their arguments unchanged.
					var results = count;
					switch (number)
					{
						case 14:
							results = Math.Min(count, 1);
							break;

						case 15:
							results = Math.Min(count, 2);
							break;

						case 16:
							results = Math.Min(count, 3);
							break;

						case 17:
							results = Math.Min(count, 4);
							break;

						case 18:
							results = Math.Min(count, 6);
							break;
					}

					for (int i = results - 1; i >= 0; i--)
						_psStack.Push(args[i]);
					break;
				}
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

		// relative move; during flex the point is only tracked, not drawn.
		private void MoveTo(double dx, double dy)
		{
			_x += dx;
			_y += dy;
			_sp = 0;
			if (!_flexing)
				this.Builder.MoveTo((float)_x, (float)_y);
		}

		// relative line.
		private void LineTo(double dx, double dy)
		{
			_x += dx;
			_y += dy;
			_sp = 0;
			this.Builder.LineTo((float)_x, (float)_y);
		}

		// relative curve: each delta is relative to the previous point.
		private void CurveTo(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
		{
			var x1 = _x + dx1;
			var y1 = _y + dy1;
			var x2 = x1 + dx2;
			var y2 = y1 + dy2;
			_x = x2 + dx3;
			_y = y2 + dy3;
			_sp = 0;
			this.Builder.CurveTo((float)x1, (float)y1, (float)x2, (float)y2, (float)_x, (float)_y);
		}

		#endregion
	}
}
