﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClangSharp;
using SealangSharp;

namespace Generator
{
	public class FunctionVisitor : BaseVisitor
	{
		private static readonly string[] _toSkip =
		{
			"stbi__malloc",
			"stbi_image_free",
			"stbi_failure_reason",
			"stbi__err",
			"stbi_is_hdr_from_memory",
			"stbi_is_hdr_from_callbacks"
		};

		private CXCursor _functionStatement;
		private string _returnType;
		private string _functionName;


		public FunctionVisitor(CXTranslationUnit translationUnit, TextWriter writer)
			: base(translationUnit, writer)
		{
		}

		private CXChildVisitResult Visit(CXCursor cursor, CXCursor parent, IntPtr data)
		{
			if (cursor.IsInSystemHeader())
			{
				return CXChildVisitResult.CXChildVisit_Continue;
			}

			var curKind = clang.getCursorKind(cursor);

			// look only at function decls
			if (curKind == CXCursorKind.CXCursor_FunctionDecl)
			{
				// Skip empty declarations
				var body = cursor.FindChild(CXCursorKind.CXCursor_CompoundStmt);
				if (!body.HasValue)
				{
					return CXChildVisitResult.CXChildVisit_Continue;
				}

				_functionStatement = body.Value;

				_functionName = clang.getCursorSpelling(cursor).ToString();

				if (_toSkip.Contains(_functionName))
				{
					return CXChildVisitResult.CXChildVisit_Continue;
				}

				Logger.Info("Processing function {0}", _functionName);

				ProcessFunction(cursor);
			}

			return CXChildVisitResult.CXChildVisit_Recurse;
		}

		private CursorProcessResult ProcessChildByIndex(CXCursor cursor, int index)
		{
			return Process(cursor.EnsureChildByIndex(index));
		}

		private CursorProcessResult ProcessPossibleChildByIndex(CXCursor cursor, int index)
		{
			var childCursor = cursor.GetChildByIndex(index);
			if (childCursor == null)
			{
				return null;
			}

			return Process(childCursor.Value);
		}

		internal void AppendGZ(CursorProcessResult crp)
		{
			var info = crp.Info;
			if (info.Kind == CXCursorKind.CXCursor_BinaryOperator)
			{
				return;
			}

			if (info.Kind == CXCursorKind.CXCursor_UnaryOperator)
			{
				bool left;
				var type = sealang.cursor_getUnaryOpcode(info.Cursor);

				if (type == UnaryOperatorKind.Not)
				{
					var sub = ProcessChildByIndex(crp.Info.Cursor, 0);
					crp.Expression = sub.Expression + "== 0";

					return;
				}
			}

			if (info.Type.kind.IsPrimitiveNumericType())
			{
				crp.Expression = "(" + crp.Expression + ") != 0";
			}

			if (info.Type.IsPointer())
			{
				crp.Expression = "(" + crp.Expression + ") != null";
			}
		}

		private string InternalProcess(CursorInfo info)
		{
			switch (info.Kind)
			{
				case CXCursorKind.CXCursor_UnaryExpr:
				{
					var expr = ProcessPossibleChildByIndex(info.Cursor, 0);

					if (expr != null)
					{
						if (info.Type.kind == CXTypeKind.CXType_ULongLong)
						{
							return expr.Expression + ".Size";
						}

						return expr.Expression;
					}

					var tokens = info.Cursor.Tokenize(_translationUnit);
					return string.Join(" ", tokens);
				}
				case CXCursorKind.CXCursor_DeclRefExpr:
					return info.Spelling;
				case CXCursorKind.CXCursor_CompoundAssignOperator:
				case CXCursorKind.CXCursor_BinaryOperator:
					{
					var a = ProcessChildByIndex(info.Cursor, 0);
					var b = ProcessChildByIndex(info.Cursor, 1);
					var type = sealang.cursor_getBinaryOpcode(info.Cursor);

					if (type.IsLogicalBinaryOperator())
					{
						AppendGZ(a);
						AppendGZ(b);
					}

					if (type == BinaryOperatorKind.Assign)
					{
						// Explicity cast right to left
						if (!info.Type.IsPointer())
						{

							b.Expression = "(" + info.CsType + ") (" + b.Expression + ")";
						}
					}

					var str = sealang.cursor_getOperatorString(info.Cursor);
					return a.Expression + " " + str + " " + b.Expression;
				}
				case CXCursorKind.CXCursor_UnaryOperator:
				{
					var a = ProcessChildByIndex(info.Cursor, 0);

					var type = sealang.cursor_getUnaryOpcode(info.Cursor);

					if (type == UnaryOperatorKind.Deref && a.Info.Kind == CXCursorKind.CXCursor_UnaryOperator)
					{
						// Handle "*ptr++" case
						var aa = ProcessChildByIndex(a.Info.Cursor, 0);
						return aa.Expression + ".GetAndMove()";
					}

					if (type == UnaryOperatorKind.Deref && a.Info.IsPointer && !a.Info.IsRecord)
					{
						a.Expression = a.Expression + ".CurrentValue";
					}

/*					if (type == "*" || type == "&")
					{
						type = string.Empty;
					}*/

					var str = sealang.cursor_getOperatorString(info.Cursor).ToString();
					var left = type.IsUnaryOperatorPre();

					switch (type)
					{
						case UnaryOperatorKind.AddrOf:
						case UnaryOperatorKind.Deref:
							str = string.Empty;
							break;
					}

					if (left)
					{
						return str + a.Expression;
					}

					return a.Expression + str;
				}

				case CXCursorKind.CXCursor_CallExpr:
				{
					var size = info.Cursor.GetChildrenCount();

					var functionExpr = ProcessChildByIndex(info.Cursor, 0);
					var functionName = functionExpr.Expression;

					// Retrieve arguments
					var args = new List<string>();
					for (var i = 1; i < size; ++i)
					{
						var argExpr = ProcessChildByIndex(info.Cursor, i);

						args.Add(argExpr.Expression);
					}

					functionName = functionName.Replace("(", string.Empty).Replace(")", string.Empty);

					var sb = new StringBuilder();
					sb.Append(functionName + "(");
					sb.Append(string.Join(", ", args));
					sb.Append(")");

					return sb.ToString();
				}
				case CXCursorKind.CXCursor_ReturnStmt:
				{
					var child = ProcessPossibleChildByIndex(info.Cursor, 0);
					var ret = child.GetExpression();

					if (_returnType != "void" && !_returnType.StartsWith("Pointer"))
					{
						ret = "(" + _returnType + ")(" + ret + ")";
					}

					var exp = string.IsNullOrEmpty(ret) ? "return" : "return " + ret;

					return exp;
				}
				case CXCursorKind.CXCursor_IfStmt:
				{
					var conditionExpr = ProcessChildByIndex(info.Cursor, 0);
					AppendGZ(conditionExpr);

					var executionExpr = ProcessChildByIndex(info.Cursor, 1);
					var elseExpr = ProcessPossibleChildByIndex(info.Cursor, 2);

					var expr = "if (" + conditionExpr.Expression + ") " + executionExpr.Expression;

					if (elseExpr != null)
					{
						expr += " else " + elseExpr.Expression;
					}

					return expr;
				}
				case CXCursorKind.CXCursor_ForStmt:
				{
					var size = info.Cursor.GetChildrenCount();

					CursorProcessResult execution = null, start = null, condition = null, it = null;
					switch (size)
					{
						case 1:
							execution = ProcessChildByIndex(info.Cursor, 0);
							break;
						case 2:
							start = ProcessChildByIndex(info.Cursor, 0);
							execution = ProcessChildByIndex(info.Cursor, 1);
							break;
						case 3:
							start = ProcessChildByIndex(info.Cursor, 0);
							it = ProcessChildByIndex(info.Cursor, 1);
							execution = ProcessChildByIndex(info.Cursor, 2);
							break;
						case 4:
							start = ProcessChildByIndex(info.Cursor, 0);
							condition = ProcessChildByIndex(info.Cursor, 1);
							it = ProcessChildByIndex(info.Cursor, 2);
							execution = ProcessChildByIndex(info.Cursor, 3);
							break;
					}
					return "for (" + start.GetExpression() + "; " + condition.GetExpression() + "; " + it.GetExpression() + ")" + execution.GetExpression();
				}

				case CXCursorKind.CXCursor_CaseStmt:
				{
					var expr = ProcessChildByIndex(info.Cursor, 0);
					var execution = ProcessChildByIndex(info.Cursor, 1);
					return "case " + expr.Expression + ":" + execution.Expression;
				}

				case CXCursorKind.CXCursor_SwitchStmt:
				{
					var expr = ProcessChildByIndex(info.Cursor, 0);
					var execution = ProcessChildByIndex(info.Cursor, 1);
					return "switch (" + expr.Expression + ")" + execution.Expression;
				}

				case CXCursorKind.CXCursor_LabelRef:
					return info.Spelling;
				case CXCursorKind.CXCursor_GotoStmt:
				{
					var label = ProcessChildByIndex(info.Cursor, 0);

					return "goto " + label.Expression;
				}

				case CXCursorKind.CXCursor_LabelStmt:
				{
					var sb = new StringBuilder();

					sb.Append(info.Spelling);
					sb.Append(":;\n");

					var size = info.Cursor.GetChildrenCount();
					for (var i = 0; i < size; ++i)
					{
						var child = ProcessChildByIndex(info.Cursor, i);
						sb.Append(child.Expression);
					}

					return sb.ToString();
				}

				case CXCursorKind.CXCursor_ConditionalOperator:
				{
					var condition = ProcessChildByIndex(info.Cursor, 0);
					var a = ProcessChildByIndex(info.Cursor, 1);
					var b = ProcessChildByIndex(info.Cursor, 2);

					if (condition.Info.IsPrimitiveNumericType)
					{
						condition.Expression = condition.Expression + " > 0";
					}

					return condition.Expression + "?" + a.Expression + ":" + b.Expression;
				}
				case CXCursorKind.CXCursor_MemberRefExpr:
				{
					var a = ProcessChildByIndex(info.Cursor, 0);
					return a.Expression + "." + info.Spelling;
				}
				case CXCursorKind.CXCursor_IntegerLiteral:
				case CXCursorKind.CXCursor_FloatingLiteral:
				{
					return sealang.cursor_getLiteralString(info.Cursor).ToString();
				}
				case CXCursorKind.CXCursor_CharacterLiteral:
					return info.Spelling;
				case CXCursorKind.CXCursor_StringLiteral:
					return info.Spelling.StartsWith("L") ? info.Spelling.Substring(1) : info.Spelling;
				case CXCursorKind.CXCursor_VarDecl:
				{
					CursorProcessResult rvalue = null;
					var size = info.Cursor.GetChildrenCount();
					if (size > 0)
					{
						rvalue = ProcessPossibleChildByIndex(info.Cursor, size - 1);
					}

					var expr = info.CsType + " " + info.Spelling;

					if (rvalue != null && !string.IsNullOrEmpty(rvalue.Expression))
					{
						if (!info.IsPointer)
						{
							expr += " = (" + info.CsType + ")(" + rvalue.Expression + ")";
						}
						else
						{
							expr += " = " + rvalue.Expression;
						}
					}
					else if (info.IsRecord)
					{
						expr += " = new " + info.CsType + "()";
					}

					return expr;
				}
				case CXCursorKind.CXCursor_DeclStmt:
				{
					var sb = new StringBuilder();
					var size = info.Cursor.GetChildrenCount();
					for (var i = 0; i < size; ++i)
					{
						var exp = ProcessChildByIndex(info.Cursor, i);
						exp.Expression = exp.Expression.EnsureStatementFinished();
						sb.Append(exp.Expression);
					}

					return sb.ToString();
				}
				case CXCursorKind.CXCursor_CompoundStmt:
				{
					var sb = new StringBuilder();
					sb.Append("{\n");

					var size = info.Cursor.GetChildrenCount();
					for (var i = 0; i < size; ++i)
					{
						var exp = ProcessChildByIndex(info.Cursor, i);
						exp.Expression = exp.Expression.EnsureStatementFinished();
						sb.Append(exp.Expression);
					}

					sb.Append("}\n");

					var fullExp = sb.ToString();

					return fullExp;
				}

				case CXCursorKind.CXCursor_ArraySubscriptExpr:
				{
					var var = ProcessChildByIndex(info.Cursor, 0);
					var expr = ProcessChildByIndex(info.Cursor, 1);

					return var.Expression + "[" + expr.Expression + "]";
				}

				case CXCursorKind.CXCursor_InitListExpr:
				{
					var sb = new StringBuilder();

					sb.Append("{ ");
					var size = info.Cursor.GetChildrenCount();
					for (var i = 0; i < size; ++i)
					{
						var exp = ProcessChildByIndex(info.Cursor, i);
						sb.Append(exp.Expression);

						if (i < size - 1)
						{
							sb.Append(", ");
						}
					}

					sb.Append(" }");
					return sb.ToString();
				}

				case CXCursorKind.CXCursor_ParenExpr:
				{
					var expr = ProcessPossibleChildByIndex(info.Cursor, 0);

					return "(" + expr.GetExpression() + ")";
				}

				case CXCursorKind.CXCursor_BreakStmt:
					return "break";

				case CXCursorKind.CXCursor_CStyleCastExpr:
				{
					var size = info.Cursor.GetChildrenCount();
					var child = ProcessChildByIndex(info.Cursor, size - 1);

					var expr = child.Expression;
					if (!info.IsCPointer || !child.Info.IsPrimitiveNumericType) return expr;

					if (info.IsRecord)
					{
						expr = "new " + info.CsType + "(" + expr + ")";
					}
					else
					{
						expr = "null /*" + expr + "*/";
					}

					return expr;
				}

				default:
				{
					// Return last child
					var size = info.Cursor.GetChildrenCount();

					if (size == 0)
					{
						return string.Empty;
					}

					var expr = ProcessPossibleChildByIndex(info.Cursor, size - 1);

					return expr.GetExpression();
				}
			}
		}

		private CursorProcessResult Process(CXCursor cursor)
		{
			var info = new CursorInfo(cursor);

			var expr = InternalProcess(info);

			return new CursorProcessResult(info)
			{
				Expression = expr
			};
		}

		private CXChildVisitResult VisitFunctionBody(CXCursor cursor, CXCursor parent, IntPtr data)
		{
			var res = Process(cursor);

			if (!string.IsNullOrEmpty(res.Expression))
			{
				IndentedWriteLine(res.Expression.EnsureStatementFinished());
			}

			return CXChildVisitResult.CXChildVisit_Continue;
		}

		private void ProcessFunction(CXCursor cursor)
		{
			WriteFunctionStart(cursor);

			_indentLevel++;

			clang.visitChildren(_functionStatement, VisitFunctionBody, new CXClientData(IntPtr.Zero));

			// DumpCursor(cursor);

			_indentLevel--;

			IndentedWriteLine("}");
			_writer.WriteLine();
		}

		private void WriteFunctionStart(CXCursor cursor)
		{
			var functionType = clang.getCursorType(cursor);
			var functionName = clang.getCursorSpelling(cursor).ToString();
			var returnType = clang.getCursorResultType(cursor);

			_returnType = returnType.ToCSharpTypeString();
			IndentedWrite("private static " + _returnType);

			_writer.Write(" " + functionName + "(");

			var numArgTypes = clang.getNumArgTypes(functionType);
			for (uint i = 0; i < numArgTypes; ++i)
			{
				ArgumentHelper(functionType, clang.Cursor_getArgument(cursor, i), i);
			}

			_writer.WriteLine(")");
			IndentedWriteLine("{");
		}

		private void ArgumentHelper(CXType functionType, CXCursor paramCursor, uint index)
		{
			var numArgTypes = clang.getNumArgTypes(functionType);
			var type = clang.getArgType(functionType, index);

			var spelling = clang.getCursorSpelling(paramCursor).ToString();

			var name = spelling.FixSpecialWords();
			var typeName = type.ToCSharpTypeString();


			_writer.Write(typeName);
			_writer.Write(" ");

			_writer.Write(name);

			if (index != numArgTypes - 1)
			{
				_writer.Write(", ");
			}
		}

		public override void Run()
		{
			clang.visitChildren(clang.getTranslationUnitCursor(_translationUnit), Visit, new CXClientData(IntPtr.Zero));
		}
	}
}