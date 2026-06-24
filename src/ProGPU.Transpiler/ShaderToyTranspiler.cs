using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text;

namespace ProGPU.Transpiler
{
    public enum TokenType
    {
        Keyword,
        Identifier,
        FloatLiteral,
        IntLiteral,
        BoolLiteral,
        Operator,
        Punctuation,
        EOF
    }

    public class Token
    {
        public TokenType Type { get; }
        public string Value { get; }
        public int Line { get; }
        public int Index { get; }
        public Token(TokenType type, string value, int line, int index)
        {
            Type = type;
            Value = value;
            Line = line;
            Index = index;
        }
    }

    public abstract class AstNode { }

    public abstract class Expression : AstNode
    {
        public string ResolvedType { get; set; } = "void";
    }

    public abstract class Statement : AstNode { }

    public class StructMember
    {
        public string Type { get; }
        public string Name { get; }
        public StructMember(string type, string name)
        {
            Type = type;
            Name = name;
        }
    }

    public class FunctionParameter
    {
        public string Modifier { get; }
        public string Type { get; }
        public string Name { get; }
        public FunctionParameter(string modifier, string type, string name)
        {
            Modifier = modifier;
            Type = type;
            Name = name;
        }
    }

    public class StructDeclaration : AstNode
    {
        public string Name { get; }
        public List<StructMember> Members { get; }
        public StructDeclaration(string name, List<StructMember> members)
        {
            Name = name;
            Members = members;
        }
    }

    public class FunctionDeclaration : AstNode
    {
        public string ReturnType { get; }
        public string Name { get; }
        public List<FunctionParameter> Parameters { get; }
        public BlockStatement Body { get; }
        public FunctionDeclaration(string returnType, string name, List<FunctionParameter> parameters, BlockStatement body)
        {
            ReturnType = returnType;
            Name = name;
            Parameters = parameters;
            Body = body;
        }
    }

    public class LiteralExpression : Expression
    {
        public object Value { get; }
        public LiteralExpression(object value, string type)
        {
            Value = value;
            ResolvedType = type;
        }
    }

    public class IdentifierExpression : Expression
    {
        public string Name { get; }
        public IdentifierExpression(string name)
        {
            Name = name;
        }
    }

    public class UnaryExpression : Expression
    {
        public string Op { get; }
        public Expression Operand { get; }
        public bool IsPostfix { get; }
        public UnaryExpression(string op, Expression operand, bool isPostfix)
        {
            Op = op;
            Operand = operand;
            IsPostfix = isPostfix;
        }
    }

    public class BinaryExpression : Expression
    {
        public string Op { get; }
        public Expression Left { get; }
        public Expression Right { get; }
        public BinaryExpression(string op, Expression left, Expression right)
        {
            Op = op;
            Left = left;
            Right = right;
        }
    }

    public class TernaryExpression : Expression
    {
        public Expression Condition { get; }
        public Expression TrueBranch { get; }
        public Expression FalseBranch { get; }
        public TernaryExpression(Expression condition, Expression trueBranch, Expression falseBranch)
        {
            Condition = condition;
            TrueBranch = trueBranch;
            FalseBranch = falseBranch;
        }
    }

    public class CallExpression : Expression
    {
        public string Callee { get; }
        public List<Expression> Arguments { get; }
        public CallExpression(string callee, List<Expression> arguments)
        {
            Callee = callee;
            Arguments = arguments;
        }
    }

    public class MemberAccessExpression : Expression
    {
        public Expression Base { get; }
        public string Member { get; }
        public MemberAccessExpression(Expression baseExpr, string member)
        {
            Base = baseExpr;
            Member = member;
        }
    }

    public class ArrayAccessExpression : Expression
    {
        public Expression Base { get; }
        public Expression Index { get; }
        public ArrayAccessExpression(Expression baseExpr, Expression index)
        {
            Base = baseExpr;
            Index = index;
        }
    }

    public class AssignmentExpression : Expression
    {
        public string Op { get; }
        public Expression Left { get; }
        public Expression Right { get; }
        public AssignmentExpression(string op, Expression left, Expression right)
        {
            Op = op;
            Left = left;
            Right = right;
        }
    }

    public class BlockStatement : Statement
    {
        public List<Statement> Statements { get; }
        public BlockStatement(List<Statement> statements)
        {
            Statements = statements;
        }
    }

    public class ExpressionStatement : Statement
    {
        public Expression Expression { get; }
        public ExpressionStatement(Expression expression)
        {
            Expression = expression;
        }
    }

    public class VariableDeclarationStatement : Statement
    {
        public string Type { get; }
        public string Name { get; }
        public Expression Initializer { get; }
        public int? ArraySize { get; }
        public bool IsConst { get; set; }
        public bool IsUniform { get; set; }
        public VariableDeclarationStatement(string type, string name, Expression initializer, int? arraySize = null)
        {
            Type = type;
            Name = name;
            Initializer = initializer;
            ArraySize = arraySize;
        }
    }

    public class IfStatement : Statement
    {
        public Expression Condition { get; }
        public Statement ThenBranch { get; }
        public Statement ElseBranch { get; }
        public IfStatement(Expression condition, Statement thenBranch, Statement elseBranch)
        {
            Condition = condition;
            ThenBranch = thenBranch;
            ElseBranch = elseBranch;
        }
    }

    public class ForStatement : Statement
    {
        public Statement Initializer { get; }
        public Expression Condition { get; }
        public Expression Increment { get; }
        public Statement Body { get; }
        public ForStatement(Statement initializer, Expression condition, Expression increment, Statement body)
        {
            Initializer = initializer;
            Condition = condition;
            Increment = increment;
            Body = body;
        }
    }

    public class ReturnStatement : Statement
    {
        public Expression Expression { get; }
        public ReturnStatement(Expression expression)
        {
            Expression = expression;
        }
    }

    public class BreakStatement : Statement { }

    public class ContinueStatement : Statement { }

    public class StructType
    {
        public string Name { get; }
        public Dictionary<string, string> Members { get; } = new Dictionary<string, string>();
        public StructType(string name)
        {
            Name = name;
        }
    }

    public class Scope
    {
        public Scope Parent { get; }
        public Dictionary<string, string> Variables { get; } = new Dictionary<string, string>();
        public Scope(Scope parent = null)
        {
            Parent = parent;
        }
        public void Declare(string name, string type)
        {
            Variables[name] = type;
        }
        public string Lookup(string name)
        {
            if (Variables.TryGetValue(name, out var type)) return type;
            return Parent?.Lookup(name);
        }
    }

    public static class ShaderToyTranspiler
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "const", "uniform", "struct", "void", "float", "int", "uint", "bool",
            "vec2", "vec3", "vec4", "mat2", "mat3", "mat4",
            "ivec2", "ivec3", "ivec4", "uvec2", "uvec3", "uvec4",
            "bvec2", "bvec3", "bvec4",
            "if", "else", "for", "return", "break", "continue", "in", "out", "inout",
            "true", "false"
        };

        private static bool IsKeyword(string val) => Keywords.Contains(val);

        private static bool IsPunctuation(string val)
        {
            return val == "(" || val == ")" || val == "{" || val == "}" ||
                   val == "[" || val == "]" || val == "," || val == ";" || val == ".";
        }

        public static bool IsGlsl(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            return source.Contains("void mainImage") ||
                   source.Contains("vec3 ") ||
                   source.Contains("vec2 ") ||
                   source.Contains("vec4 ") ||
                   source.Contains("float ") ||
                   source.Contains("uniform ");
        }

        public static string GetUniqueWgslName(string name, List<string> paramTypes)
        {
            if (name == "mainImage") return name;
            if (paramTypes == null || paramTypes.Count == 0) return $"{name}_void";
            var parts = new List<string>();
            foreach (var t in paramTypes)
            {
                parts.Add(t.Replace(" ", "_").Replace("<", "_").Replace(">", "_").Replace(",", "_"));
            }
            return $"{name}_{string.Join("_", parts)}";
        }

        public static string Translate(string glsl)
        {
            if (string.IsNullOrEmpty(glsl)) return glsl;

            // Normalize line endings
            string source = glsl.Replace("\r", "");

            // 0. Preprocessing (macro expansion)
            source = Preprocess(source);

            // 1. Lexical Analysis
            var tokens = Tokenize(source);

            // 2. Syntax Analysis
            var structs = new Dictionary<string, StructType>();
            var userFunctions = new Dictionary<string, string>();
            var parser = new ParserState(tokens, structs, userFunctions);
            var ast = parser.Parse();

            // 3. Semantic Analysis / Type Resolution
            var resolver = new TypeResolver(structs, userFunctions);
            resolver.Resolve(ast);

            // 4. Code Generation
            var generator = new Generator(structs, userFunctions);
            return generator.Generate(ast);
        }

        private struct ParamMacro
        {
            public string Name;
            public List<string> Params;
            public string Body;
        }

        private static List<string> SplitArgs(string argsStr)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            int parenCount = 0;
            for (int i = 0; i < argsStr.Length; i++)
            {
                char c = argsStr[i];
                if (c == '(') parenCount++;
                else if (c == ')') parenCount--;

                if (c == ',' && parenCount == 0)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            if (current.Length > 0)
            {
                result.Add(current.ToString().Trim());
            }
            return result;
        }

        private static string ExpandParamMacro(string source, ParamMacro macro)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < source.Length)
            {
                if (i + macro.Name.Length <= source.Length && source.Substring(i, macro.Name.Length) == macro.Name)
                {
                    bool wordBoundaryBefore = (i == 0 || !char.IsLetterOrDigit(source[i - 1]) && source[i - 1] != '_');
                    if (wordBoundaryBefore)
                    {
                        int next = i + macro.Name.Length;
                        while (next < source.Length && char.IsWhiteSpace(source[next]))
                        {
                            next++;
                        }
                        if (next < source.Length && source[next] == '(')
                        {
                            int argStart = next + 1;
                            int parenCount = 1;
                            int argEnd = argStart;
                            while (argEnd < source.Length && parenCount > 0)
                            {
                                if (source[argEnd] == '(') parenCount++;
                                else if (source[argEnd] == ')') parenCount--;
                                argEnd++;
                            }

                            if (parenCount == 0)
                            {
                                string argsStr = source.Substring(argStart, argEnd - 1 - argStart);
                                var args = SplitArgs(argsStr);

                                string expandedBody = macro.Body;
                                for (int pIdx = 0; pIdx < Math.Min(macro.Params.Count, args.Count); pIdx++)
                                {
                                    string paramPattern = @"\b" + Regex.Escape(macro.Params[pIdx]) + @"\b";
                                    expandedBody = Regex.Replace(expandedBody, paramPattern, args[pIdx]);
                                }

                                sb.Append(expandedBody);
                                i = argEnd;
                                continue;
                            }
                        }
                    }
                }

                sb.Append(source[i]);
                i++;
            }
            return sb.ToString();
        }

        private class BlockState
        {
            public bool ParentIsActive { get; set; }
            public bool CurrentBranchActive { get; set; }
            public bool AnyBranchWasActive { get; set; }
            public bool IsActive => ParentIsActive && CurrentBranchActive;
        }

        private static bool EvaluateBooleanExpression(string expr)
        {
            expr = expr.Replace(" ", "").Replace("\t", "");
            string last;
            do
            {
                last = expr;
                expr = expr.Replace("!1", "0");
                expr = expr.Replace("!0", "1");
                expr = expr.Replace("(1)", "1");
                expr = expr.Replace("(0)", "0");
                expr = expr.Replace("1&&1", "1");
                expr = expr.Replace("1&&0", "0");
                expr = expr.Replace("0&&1", "0");
                expr = expr.Replace("0&&0", "0");
                expr = expr.Replace("1||1", "1");
                expr = expr.Replace("1||0", "1");
                expr = expr.Replace("0||1", "1");
                expr = expr.Replace("0||0", "0");
            } while (expr != last);

            return expr.Contains('1') && !expr.Contains('0');
        }

        private static bool EvaluateCondition(string expr, HashSet<string> definedMacros)
        {
            expr = expr.Trim();
            if (expr == "0" || expr.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (expr == "1" || expr.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;

            var definedRegex = new Regex(@"defined\s*\(\s*([a-zA-Z0-9_]+)\s*\)|defined\s+([a-zA-Z0-9_]+)", RegexOptions.Compiled);
            expr = definedRegex.Replace(expr, m =>
            {
                string macro = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
                return definedMacros.Contains(macro) ? "1" : "0";
            });

            var idRegex = new Regex(@"\b[a-zA-Z_][a-zA-Z0-9_]*\b", RegexOptions.Compiled);
            expr = idRegex.Replace(expr, m =>
            {
                string word = m.Value;
                if (word == "true" || word == "1") return "1";
                if (word == "false" || word == "0") return "0";
                if (definedMacros.Contains(word))
                {
                    return "1";
                }
                return "0";
            });

            try
            {
                return EvaluateBooleanExpression(expr);
            }
            catch
            {
                return expr.Contains('1');
            }
        }

        private static string Preprocess(string source)
        {
            var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var simpleDefines = new Dictionary<string, string>();
            var paramDefines = new List<ParamMacro>();
            var definedMacros = new HashSet<string>();
            var stateStack = new Stack<BlockState>();
            var newLines = new List<string>();

            var paramDefineRegex = new Regex(@"^#define\s+([a-zA-Z_][a-zA-Z0-9_]*)\(([^)]*)\)\s+(.*)$", RegexOptions.Compiled);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#"))
                {
                    bool isCurrentlyActive = (stateStack.Count == 0) ? true : stateStack.Peek().IsActive;

                    if (trimmed.StartsWith("#ifdef"))
                    {
                        var macroName = trimmed.Substring("#ifdef".Length).Trim();
                        int commentIdx = macroName.IndexOf("//");
                        if (commentIdx >= 0) macroName = macroName.Substring(0, commentIdx).Trim();
                        int multilineCommentIdx = macroName.IndexOf("/*");
                        if (multilineCommentIdx >= 0) macroName = macroName.Substring(0, multilineCommentIdx).Trim();

                        bool cond = definedMacros.Contains(macroName);
                        stateStack.Push(new BlockState { ParentIsActive = isCurrentlyActive, CurrentBranchActive = cond, AnyBranchWasActive = cond });
                        newLines.Add("");
                    }
                    else if (trimmed.StartsWith("#ifndef"))
                    {
                        var macroName = trimmed.Substring("#ifndef".Length).Trim();
                        int commentIdx = macroName.IndexOf("//");
                        if (commentIdx >= 0) macroName = macroName.Substring(0, commentIdx).Trim();
                        int multilineCommentIdx = macroName.IndexOf("/*");
                        if (multilineCommentIdx >= 0) macroName = macroName.Substring(0, multilineCommentIdx).Trim();

                        bool cond = !definedMacros.Contains(macroName);
                        stateStack.Push(new BlockState { ParentIsActive = isCurrentlyActive, CurrentBranchActive = cond, AnyBranchWasActive = cond });
                        newLines.Add("");
                    }
                    else if (trimmed.StartsWith("#if"))
                    {
                        var expr = trimmed.Substring("#if".Length).Trim();
                        int commentIdx = expr.IndexOf("//");
                        if (commentIdx >= 0) expr = expr.Substring(0, commentIdx).Trim();
                        int multilineCommentIdx = expr.IndexOf("/*");
                        if (multilineCommentIdx >= 0) expr = expr.Substring(0, multilineCommentIdx).Trim();

                        bool cond = EvaluateCondition(expr, definedMacros);
                        stateStack.Push(new BlockState { ParentIsActive = isCurrentlyActive, CurrentBranchActive = cond, AnyBranchWasActive = cond });
                        newLines.Add("");
                    }
                    else if (trimmed.StartsWith("#elif"))
                    {
                        var expr = trimmed.Substring("#elif".Length).Trim();
                        int commentIdx = expr.IndexOf("//");
                        if (commentIdx >= 0) expr = expr.Substring(0, commentIdx).Trim();
                        int multilineCommentIdx = expr.IndexOf("/*");
                        if (multilineCommentIdx >= 0) expr = expr.Substring(0, multilineCommentIdx).Trim();

                        if (stateStack.Count > 0)
                        {
                            var state = stateStack.Peek();
                            if (state.AnyBranchWasActive)
                            {
                                state.CurrentBranchActive = false;
                            }
                            else
                            {
                                bool cond = EvaluateCondition(expr, definedMacros);
                                state.CurrentBranchActive = cond;
                                if (cond) state.AnyBranchWasActive = true;
                            }
                        }
                        newLines.Add("");
                    }
                    else if (trimmed.StartsWith("#else"))
                    {
                        if (stateStack.Count > 0)
                        {
                            var state = stateStack.Peek();
                            state.CurrentBranchActive = !state.AnyBranchWasActive;
                            state.AnyBranchWasActive = true;
                        }
                        newLines.Add("");
                    }
                    else if (trimmed.StartsWith("#endif"))
                    {
                        if (stateStack.Count > 0)
                        {
                            stateStack.Pop();
                        }
                        newLines.Add("");
                    }
                    else if (trimmed.StartsWith("#define"))
                    {
                        if (isCurrentlyActive)
                        {
                            var match = paramDefineRegex.Match(trimmed);
                            if (match.Success)
                            {
                                var macroName = match.Groups[1].Value;
                                var paramsStr = match.Groups[2].Value;
                                var body = match.Groups[3].Value;

                                var parameters = new List<string>();
                                foreach (var p in paramsStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                                {
                                    parameters.Add(p.Trim());
                                }

                                paramDefines.Add(new ParamMacro { Name = macroName, Params = parameters, Body = body });
                                definedMacros.Add(macroName);
                            }
                            else
                            {
                                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 2)
                                {
                                    string key = parts[1];
                                    definedMacros.Add(key);

                                    if (parts.Length >= 3)
                                    {
                                        string val = string.Join(" ", parts, 2, parts.Length - 2);
                                        int commentIdx = val.IndexOf("//");
                                        if (commentIdx >= 0) val = val.Substring(0, commentIdx).Trim();
                                        int multilineCommentIdx = val.IndexOf("/*");
                                        if (multilineCommentIdx >= 0) val = val.Substring(0, multilineCommentIdx).Trim();
                                        simpleDefines[key] = val.Trim();
                                    }
                                    else
                                    {
                                        simpleDefines[key] = "";
                                    }
                                }
                            }
                        }
                        newLines.Add("");
                    }
                    else if (trimmed.StartsWith("#undef"))
                    {
                        if (isCurrentlyActive)
                        {
                            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                string key = parts[1];
                                definedMacros.Remove(key);
                                simpleDefines.Remove(key);
                            }
                        }
                        newLines.Add("");
                    }
                    else
                    {
                        newLines.Add("");
                    }
                }
                else if (trimmed.StartsWith("precision"))
                {
                    newLines.Add("");
                }
                else
                {
                    bool isCurrentlyActive = (stateStack.Count == 0) ? true : stateStack.Peek().IsActive;
                    if (isCurrentlyActive)
                    {
                        newLines.Add(line);
                    }
                    else
                    {
                        newLines.Add("");
                    }
                }
            }

            string result = string.Join("\n", newLines);

            foreach (var macro in paramDefines)
            {
                result = ExpandParamMacro(result, macro);
            }

            var sortedKeys = new List<string>(simpleDefines.Keys);
            sortedKeys.Sort((a, b) => b.Length.CompareTo(a.Length));

            foreach (var key in sortedKeys)
            {
                string val = sortedKeys.Contains(simpleDefines[key]) ? sortedKeys.IndexOf(simpleDefines[key]) < sortedKeys.IndexOf(key) ? simpleDefines[simpleDefines[key]] : simpleDefines[key] : simpleDefines[key];
                string pattern = @"\b" + Regex.Escape(key) + @"\b";
                result = Regex.Replace(result, pattern, val);
            }

            return result;
        }

        public static List<Token> Tokenize(string source)
        {
            var tokens = new List<Token>();
            var regex = new Regex(
                @"(?<comment>//.*|/\*[\s\S]*?\*/)" +
                @"|(?<float>\b\d+\.\d*(?:[eE][+-]?\d+)?[fF]?|\.\d+(?:[eE][+-]?\d+)?[fF]?|\b\d+(?:[eE][+-]?\d+)[fF]?\b|\b\d+[fF]\b)" +
                @"|(?<hex>\b0[xX][0-9a-fA-F]+\b)" +
                @"|(?<int>\b\d+[uU]?\b)" +
                @"|(?<id>\b[a-zA-Z_][a-zA-Z0-9_]*\b)" +
                @"|(?<op>\+\+|--|\+=|-=|\*=|\/=|==|!=|<=|>=|&&|\|\||[+\-*/%<>=!?:\(\)\{\}\[\],;\.&|^~])" +
                @"|(?<space>\s+)",
                RegexOptions.Compiled
            );

            int currentLine = 1;
            int lastIndex = 0;

            foreach (Match match in regex.Matches(source))
            {
                if (match.Index > lastIndex)
                {
                    var skipped = source.Substring(lastIndex, match.Index - lastIndex);
                    if (!string.IsNullOrWhiteSpace(skipped))
                    {
                        throw new Exception($"Lexical error: Unrecognized sequence '{skipped}' at line {currentLine}");
                    }
                    foreach (char c in skipped)
                    {
                        if (c == '\n') currentLine++;
                    }
                }

                string val = match.Value;

                if (match.Groups["space"].Success)
                {
                    foreach (char c in val)
                    {
                        if (c == '\n') currentLine++;
                    }
                }
                else if (match.Groups["comment"].Success)
                {
                    foreach (char c in val)
                    {
                        if (c == '\n') currentLine++;
                    }
                }
                else if (match.Groups["float"].Success)
                {
                    tokens.Add(new Token(TokenType.FloatLiteral, val, currentLine, match.Index));
                }
                else if (match.Groups["hex"].Success)
                {
                    tokens.Add(new Token(TokenType.IntLiteral, val, currentLine, match.Index));
                }
                else if (match.Groups["int"].Success)
                {
                    tokens.Add(new Token(TokenType.IntLiteral, val, currentLine, match.Index));
                }
                else if (match.Groups["id"].Success)
                {
                    var type = IsKeyword(val) ? TokenType.Keyword : TokenType.Identifier;
                    tokens.Add(new Token(type, val, currentLine, match.Index));
                }
                else if (match.Groups["op"].Success)
                {
                    var type = IsPunctuation(val) ? TokenType.Punctuation : TokenType.Operator;
                    tokens.Add(new Token(type, val, currentLine, match.Index));
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < source.Length)
            {
                var remaining = source.Substring(lastIndex);
                if (!string.IsNullOrWhiteSpace(remaining))
                {
                    throw new Exception($"Lexical error: Unrecognized sequence '{remaining}' at line {currentLine}");
                }
            }

            tokens.Add(new Token(TokenType.EOF, "", currentLine, source.Length));
            return tokens;
        }

        public static string MapType(string glslType)
        {
            return glslType switch
            {
                "float" => "f32",
                "vec2" => "vec2<f32>",
                "vec3" => "vec3<f32>",
                "vec4" => "vec4<f32>",
                "ivec2" => "vec2<i32>",
                "ivec3" => "vec3<i32>",
                "ivec4" => "vec4<i32>",
                "uvec2" => "vec2<u32>",
                "uvec3" => "vec3<u32>",
                "uvec4" => "vec4<u32>",
                "bvec2" => "vec2<bool>",
                "bvec3" => "vec3<bool>",
                "bvec4" => "vec4<bool>",
                "mat2" => "mat2x2<f32>",
                "mat3" => "mat3x3<f32>",
                "mat4" => "mat4x4<f32>",
                "int" => "i32",
                "uint" => "u32",
                "bool" => "bool",
                "void" => "void",
                _ => glslType // custom struct
            };
        }

        public static readonly HashSet<string> ReservedKeywords = new HashSet<string>
        {
            "ref", "let", "var", "fn", "ptr", "bitcast", "enable", "override", "select", "from"
        };

        public static string EscapeIdentifier(string name)
        {
            if (ReservedKeywords.Contains(name)) return name + "_";
            return name;
        }

        public static string ResolveIdentifier(string name)
        {
            return name switch
            {
                "iResolution" => "inputs.iResolution",
                "iTime" => "inputs.iTime",
                "iTimeDelta" => "inputs.iTimeDelta",
                "iFrameRate" => "inputs.iFrameRate",
                "iFrame" => "inputs.iFrame",
                "iMouse" => "inputs.iMouse",
                "iDate" => "inputs.iDate",
                _ => EscapeIdentifier(name)
            };
        }
    }

    public class ParserState
    {
        private readonly List<Token> _tokens;
        private readonly Dictionary<string, StructType> _structs;
        private readonly Dictionary<string, string> _userFunctions;
        private int _currentTokenIndex = 0;

        public ParserState(List<Token> tokens, Dictionary<string, StructType> structs, Dictionary<string, string> userFunctions)
        {
            _tokens = tokens;
            _structs = structs;
            _userFunctions = userFunctions;
        }

        public bool IsAtEnd() => Peek().Type == TokenType.EOF;

        private Token Peek() => _tokens[_currentTokenIndex];
        private Token Previous() => _tokens[_currentTokenIndex - 1];

        private Token NextToken()
        {
            if (!IsAtEnd()) _currentTokenIndex++;
            return Previous();
        }

        private bool Check(TokenType type)
        {
            if (IsAtEnd()) return false;
            return Peek().Type == type;
        }

        private bool Check(TokenType type, string value)
        {
            if (IsAtEnd()) return false;
            return Peek().Type == type && Peek().Value == value;
        }

        private bool Match(TokenType type)
        {
            if (Check(type))
            {
                NextToken();
                return true;
            }
            return false;
        }

        private bool Match(TokenType type, string value)
        {
            if (Check(type, value))
            {
                NextToken();
                return true;
            }
            return false;
        }

        private Token Consume(TokenType type, string errMsg)
        {
            if (Check(type)) return NextToken();
            throw new Exception($"{errMsg} at line {Peek().Line}, found '{Peek().Value}'");
        }

        private Token Consume(TokenType type, string value, string errMsg)
        {
            if (Check(type, value)) return NextToken();
            throw new Exception($"{errMsg} at line {Peek().Line}, found '{Peek().Value}'");
        }

        private bool IsTypeName(Token token)
        {
            if (token.Type == TokenType.Keyword)
            {
                string val = token.Value;
                return val == "float" || val == "int" || val == "uint" || val == "bool" ||
                       val == "vec2" || val == "vec3" || val == "vec4" ||
                       val == "ivec2" || val == "ivec3" || val == "ivec4" ||
                       val == "uvec2" || val == "uvec3" || val == "uvec4" ||
                       val == "bvec2" || val == "bvec3" || val == "bvec4" ||
                       val == "mat2" || val == "mat3" || val == "mat4" ||
                       val == "void";
            }
            if (token.Type == TokenType.Identifier)
            {
                return _structs.ContainsKey(token.Value);
            }
            return false;
        }

        private string ConsumeType()
        {
            if (Match(TokenType.Keyword))
            {
                string baseType = Previous().Value;
                if (Match(TokenType.Punctuation, "[") && Match(TokenType.Punctuation, "]"))
                {
                    return baseType + "[]";
                }
                return baseType;
            }
            if (Match(TokenType.Identifier))
            {
                string structType = Previous().Value;
                if (_structs.ContainsKey(structType))
                {
                    if (Match(TokenType.Punctuation, "[") && Match(TokenType.Punctuation, "]"))
                    {
                        return structType + "[]";
                    }
                    return structType;
                }
                _currentTokenIndex--;
            }
            throw new Exception($"Expected type name, found '{Peek().Value}' at line {Peek().Line}");
        }

        public List<AstNode> Parse()
        {
            var nodes = new List<AstNode>();
            while (!IsAtEnd())
            {
                nodes.AddRange(ParseTopLevelDeclaration());
            }
            return nodes;
        }

        public List<AstNode> ParseTopLevelDeclaration()
        {
            if (Match(TokenType.Keyword, "struct"))
            {
                return new List<AstNode> { ParseStructDeclaration() };
            }

            bool isConst = Match(TokenType.Keyword, "const");
            bool isUniform = Match(TokenType.Keyword, "uniform");

            string type = ConsumeType();
            string name = Consume(TokenType.Identifier, "Expected declaration name").Value;

            if (Check(TokenType.Punctuation, "("))
            {
                return new List<AstNode> { ParseFunctionDeclaration(type, name) };
            }

            var decls = new List<AstNode>();
            string currentVarName = name;
            while (true)
            {
                int? arraySize = null;
                if (Match(TokenType.Punctuation, "["))
                {
                    arraySize = ParseArraySizeAfterOpenBracket();
                }

                Expression initializer = null;
                if (Match(TokenType.Operator, "="))
                {
                    initializer = ParseExpression();
                }

                decls.Add(new VariableDeclarationStatement(type, currentVarName, initializer, arraySize)
                {
                    IsConst = isConst,
                    IsUniform = isUniform
                });

                if (!Match(TokenType.Punctuation, ","))
                {
                    break;
                }
                currentVarName = Consume(TokenType.Identifier, "Expected variable name").Value;
            }

            Consume(TokenType.Punctuation, ";", "Expected ';' after global variable declaration");

            return decls;
        }

        private StructDeclaration ParseStructDeclaration()
        {
            string name = Consume(TokenType.Identifier, "Expected struct name").Value;
            Consume(TokenType.Punctuation, "{", "Expected '{' after struct name");

            var members = new List<StructMember>();
            while (!Check(TokenType.Punctuation, "}"))
            {
                string mType = ConsumeType();
                string mName = Consume(TokenType.Identifier, "Expected member name").Value;
                Consume(TokenType.Punctuation, ";", "Expected ';' after struct member");
                members.Add(new StructMember(mType, mName));
            }
            Consume(TokenType.Punctuation, "}", "Expected '}' at the end of struct");
            Consume(TokenType.Punctuation, ";", "Expected ';' after struct declaration");

            var decl = new StructDeclaration(name, members);
            var st = new StructType(name);
            foreach (var m in members)
            {
                st.Members[m.Name] = m.Type;
            }
            _structs[name] = st;
            return decl;
        }

        private FunctionDeclaration ParseFunctionDeclaration(string returnType, string name)
        {
            Consume(TokenType.Punctuation, "(", "Expected '(' after function name");
            var parameters = new List<FunctionParameter>();
            if (!Check(TokenType.Punctuation, ")"))
            {
                do
                {
                    string modifier = "";
                    if (Match(TokenType.Keyword, "in") || Match(TokenType.Keyword, "out") || Match(TokenType.Keyword, "inout"))
                    {
                        modifier = Previous().Value;
                    }
                    string type = ConsumeType();
                    string pName = Consume(TokenType.Identifier, "Expected parameter name").Value;
                    parameters.Add(new FunctionParameter(modifier, type, pName));
                } while (Match(TokenType.Punctuation, ","));
            }
            Consume(TokenType.Punctuation, ")", "Expected ')' after function parameters");

            var body = ParseBlockStatement();

            var decl = new FunctionDeclaration(returnType, name, parameters, body);
            var paramTypes = new List<string>();
            foreach (var p in parameters) paramTypes.Add(p.Type);
            string signatureKey = $"{name}({string.Join(",", paramTypes)})";
            _userFunctions[signatureKey] = returnType;
            _userFunctions[name] = returnType;
            return decl;
        }

        private BlockStatement ParseBlockStatement()
        {
            Consume(TokenType.Punctuation, "{", "Expected '{' to start block");
            var statements = new List<Statement>();
            while (!Check(TokenType.Punctuation, "}"))
            {
                statements.AddRange(ParseStatement());
            }
            Consume(TokenType.Punctuation, "}", "Expected '}' to end block");
            return new BlockStatement(statements);
        }

        private List<Statement> ParseStatement()
        {
            if (Match(TokenType.Punctuation, "{"))
            {
                _currentTokenIndex--;
                return new List<Statement> { ParseBlockStatement() };
            }

            if (Match(TokenType.Keyword, "if"))
            {
                Consume(TokenType.Punctuation, "(", "Expected '(' after 'if'");
                var cond = ParseExpression();
                Consume(TokenType.Punctuation, ")", "Expected ')' after if condition");

                var thenStmts = ParseStatement();
                Statement thenBranch = thenStmts.Count == 1 ? thenStmts[0] : new BlockStatement(thenStmts);

                Statement elseBranch = null;
                if (Match(TokenType.Keyword, "else"))
                {
                    var elseStmts = ParseStatement();
                    elseBranch = elseStmts.Count == 1 ? elseStmts[0] : new BlockStatement(elseStmts);
                }

                return new List<Statement> { new IfStatement(cond, thenBranch, elseBranch) };
            }

            if (Match(TokenType.Keyword, "for"))
            {
                Consume(TokenType.Punctuation, "(", "Expected '(' after 'for'");

                Statement initializer = null;
                if (!Match(TokenType.Punctuation, ";"))
                {
                    if (IsTypeName(Peek()))
                    {
                        string type = ConsumeType();
                        var decls = ParseVariableDeclarationStatement(type, false, false);
                        if (decls.Count == 1) initializer = decls[0];
                        else initializer = new BlockStatement(decls);
                    }
                    else
                    {
                        var expr = ParseExpression();
                        Consume(TokenType.Punctuation, ";", "Expected ';' after initializer expression");
                        initializer = new ExpressionStatement(expr);
                    }
                }

                Expression condition = null;
                if (!Match(TokenType.Punctuation, ";"))
                {
                    condition = ParseExpression();
                    Consume(TokenType.Punctuation, ";", "Expected ';' after condition expression");
                }

                Expression increment = null;
                if (!Match(TokenType.Punctuation, ")"))
                {
                    increment = ParseExpression();
                    Consume(TokenType.Punctuation, ")", "Expected ')' after increment expression");
                }

                var bodyStmts = ParseStatement();
                Statement body = bodyStmts.Count == 1 ? bodyStmts[0] : new BlockStatement(bodyStmts);

                return new List<Statement> { new ForStatement(initializer, condition, increment, body) };
            }

            if (Match(TokenType.Keyword, "return"))
            {
                Expression expr = null;
                if (!Match(TokenType.Punctuation, ";"))
                {
                    expr = ParseExpression();
                    Consume(TokenType.Punctuation, ";", "Expected ';' after return expression");
                }
                return new List<Statement> { new ReturnStatement(expr) };
            }

            if (Match(TokenType.Keyword, "break"))
            {
                Consume(TokenType.Punctuation, ";", "Expected ';' after break");
                return new List<Statement> { new BreakStatement() };
            }

            if (Match(TokenType.Keyword, "continue"))
            {
                Consume(TokenType.Punctuation, ";", "Expected ';' after continue");
                return new List<Statement> { new ContinueStatement() };
            }

            bool isConst = Match(TokenType.Keyword, "const");
            if (IsTypeName(Peek()))
            {
                string type = ConsumeType();
                return ParseVariableDeclarationStatement(type, isConst, false);
            }
            if (isConst)
            {
                throw new Exception($"Expected type name after 'const' at line {Peek().Line}");
            }

            var expression = ParseExpression();
            Consume(TokenType.Punctuation, ";", "Expected ';' after expression statement");
            return new List<Statement> { new ExpressionStatement(expression) };
        }

        private List<Statement> ParseVariableDeclarationStatement(string type, bool isConst, bool isUniform)
        {
            var decls = new List<Statement>();
            do
            {
                string name = Consume(TokenType.Identifier, "Expected variable name").Value;
                int? arraySize = null;

                if (Match(TokenType.Punctuation, "["))
                {
                    arraySize = ParseArraySizeAfterOpenBracket();
                }

                Expression initializer = null;
                if (Match(TokenType.Operator, "="))
                {
                    initializer = ParseExpression();
                }

                decls.Add(new VariableDeclarationStatement(type, name, initializer, arraySize)
                {
                    IsConst = isConst,
                    IsUniform = isUniform
                });

            } while (Match(TokenType.Punctuation, ","));

            Consume(TokenType.Punctuation, ";", "Expected ';' after variable declaration");
            return decls;
        }

        private int ParseArraySizeAfterOpenBracket()
        {
            var sizeExpr = ParseExpression();
            Consume(TokenType.Punctuation, "]", "Expected ']' after array size");

            if (sizeExpr is LiteralExpression { Value: int size } && size > 0)
            {
                return size;
            }

            throw new NotSupportedException("Only positive integer literal ShaderToy array sizes are supported.");
        }

        private Expression ParsePrimary()
        {
            if (Match(TokenType.FloatLiteral))
            {
                string val = Previous().Value.TrimEnd('f', 'F');
                if (val.EndsWith(".")) val += "0";
                else if (val.StartsWith(".")) val = "0" + val;
                return new LiteralExpression(double.Parse(val, System.Globalization.CultureInfo.InvariantCulture), "float");
            }
            if (Match(TokenType.IntLiteral))
            {
                string val = Previous().Value.TrimEnd('u', 'U');
                if (val.StartsWith("0x") || val.StartsWith("0X"))
                {
                    return new LiteralExpression(Convert.ToInt32(val, 16), "int");
                }
                if (val.StartsWith("0") && val.Length > 1)
                {
                    return new LiteralExpression(Convert.ToInt32(val, 8), "int");
                }
                return new LiteralExpression(int.Parse(val), "int");
            }
            if (Match(TokenType.Keyword, "true") || Match(TokenType.Keyword, "false"))
            {
                return new LiteralExpression(Previous().Value == "true", "bool");
            }

            if (Match(TokenType.Identifier) || Match(TokenType.Keyword))
            {
                return new IdentifierExpression(Previous().Value);
            }

            if (Match(TokenType.Punctuation, "("))
            {
                var expr = ParseExpression();
                Consume(TokenType.Punctuation, ")", "Expected ')' after expression");
                return expr;
            }

            if (Match(TokenType.Operator, "+") || Match(TokenType.Operator, "-") || Match(TokenType.Operator, "!") || Match(TokenType.Operator, "~") ||
                Match(TokenType.Operator, "++") || Match(TokenType.Operator, "--"))
            {
                var op = Previous().Value;
                var operand = ParseExpression(13);
                return new UnaryExpression(op, operand, false);
            }

            throw new Exception($"Unexpected token: '{Peek().Value}' at line {Peek().Line}");
        }

        private Expression ParseExpression(int minPrecedence = 0)
        {
            var left = ParsePrimary();

            while (true)
            {
                if (Check(TokenType.Punctuation, "("))
                {
                    left = ParseCall(left);
                    continue;
                }
                if (Check(TokenType.Punctuation, "["))
                {
                    left = ParseArrayAccess(left);
                    continue;
                }
                if (Match(TokenType.Punctuation, "."))
                {
                    var member = Consume(TokenType.Identifier, "Expected member name after '.'").Value;
                    left = new MemberAccessExpression(left, member);
                    continue;
                }
                if (Match(TokenType.Operator, "++") || Match(TokenType.Operator, "--"))
                {
                    left = new UnaryExpression(Previous().Value, left, true);
                    continue;
                }

                if (!IsOperator(Peek())) break;

                var op = Peek().Value;
                int prec = GetPrecedence(op);
                if (prec < minPrecedence) break;

                NextToken(); // Consume operator

                if (op == "?")
                {
                    var thenBranch = ParseExpression(0);
                    Consume(TokenType.Operator, ":", "Expected ':' in ternary operator");
                    var elseBranch = ParseExpression(prec); // Right-associative
                    left = new TernaryExpression(left, thenBranch, elseBranch);
                }
                else if (IsAssignmentOperator(op))
                {
                    var right = ParseExpression(prec); // Right-associative
                    left = new AssignmentExpression(op, left, right);
                }
                else
                {
                    var right = ParseExpression(prec + 1); // Left-associative
                    left = new BinaryExpression(op, left, right);
                }
            }

            return left;
        }

        private Expression ParseCall(Expression callee)
        {
            Consume(TokenType.Punctuation, "(");
            var args = new List<Expression>();
            if (!Check(TokenType.Punctuation, ")"))
            {
                do
                {
                    args.Add(ParseExpression());
                } while (Match(TokenType.Punctuation, ","));
            }
            Consume(TokenType.Punctuation, ")", "Expected ')' after function arguments");

            if (callee is IdentifierExpression id)
            {
                return new CallExpression(id.Name, args);
            }
            throw new Exception("Callee must be an identifier or type name constructor");
        }

        private Expression ParseArrayAccess(Expression baseExpr)
        {
            Consume(TokenType.Punctuation, "[");
            var index = ParseExpression();
            Consume(TokenType.Punctuation, "]", "Expected ']' after array index");
            return new ArrayAccessExpression(baseExpr, index);
        }

        private static int GetPrecedence(string op)
        {
            return op switch
            {
                "," => 1,
                "=" or "+=" or "-=" or "*=" or "/=" => 2,
                "?" => 3,
                "||" => 4,
                "&&" => 5,
                "|" => 6,
                "^" => 7,
                "&" => 8,
                "==" or "!=" => 9,
                "<" or ">" or "<=" or ">=" => 10,
                "+" or "-" => 11,
                "*" or "/" or "%" => 12,
                _ => 0
            };
        }

        private static bool IsOperator(Token token)
        {
            if (token.Type != TokenType.Operator) return false;
            var val = token.Value;
            return val == "," || val == "=" || val == "+=" || val == "-=" ||
                   val == "*=" || val == "/=" || val == "?" || val == "||" ||
                   val == "&&" || val == "|" || val == "^" || val == "&" ||
                   val == "==" || val == "!=" || val == "<" || val == ">" ||
                   val == "<=" || val == ">=" || val == "+" || val == "-" ||
                   val == "*" || val == "/" || val == "%";
        }

        private static bool IsAssignmentOperator(string op)
        {
            return op == "=" || op == "+=" || op == "-=" || op == "*=" || op == "/=";
        }
    }

    public class TypeResolver
    {
        private readonly Dictionary<string, StructType> _structs;
        private readonly Dictionary<string, string> _userFunctions;
        private Scope _currentScope;

        public TypeResolver(Dictionary<string, StructType> structs, Dictionary<string, string> userFunctions)
        {
            _structs = structs;
            _userFunctions = userFunctions;
            _currentScope = new Scope();
        }

        public void Resolve(List<AstNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is FunctionDeclaration f)
                {
                    var paramTypes = new List<string>();
                    foreach (var p in f.Parameters) paramTypes.Add(p.Type);
                    string signatureKey = $"{f.Name}({string.Join(",", paramTypes)})";
                    _userFunctions[signatureKey] = f.ReturnType;
                    _userFunctions[f.Name] = f.ReturnType;
                }
            }

            foreach (var node in nodes)
            {
                ResolveNode(node);
            }
        }

        private void ResolveNode(AstNode node)
        {
            if (node is StructDeclaration)
            {
                return;
            }
            if (node is FunctionDeclaration f)
            {
                var parentScope = _currentScope;
                _currentScope = new Scope(parentScope);

                foreach (var p in f.Parameters)
                {
                    _currentScope.Declare(p.Name, p.Type);
                }

                ResolveStatement(f.Body);

                _currentScope = parentScope;
                return;
            }
            if (node is VariableDeclarationStatement v)
            {
                if (v.Initializer != null)
                {
                    ResolveExpression(v.Initializer);
                }
                _currentScope.Declare(v.Name, GetDeclarationResolvedType(v));
                return;
            }
        }

        private void ResolveStatement(Statement stmt)
        {
            if (stmt is BlockStatement b)
            {
                var parentScope = _currentScope;
                _currentScope = new Scope(parentScope);
                foreach (var s in b.Statements)
                {
                    ResolveStatement(s);
                }
                _currentScope = parentScope;
            }
            else if (stmt is ExpressionStatement e)
            {
                ResolveExpression(e.Expression);
            }
            else if (stmt is VariableDeclarationStatement v)
            {
                if (v.Initializer != null)
                {
                    ResolveExpression(v.Initializer);
                }
                _currentScope.Declare(v.Name, GetDeclarationResolvedType(v));
            }
            else if (stmt is IfStatement ifs)
            {
                ResolveExpression(ifs.Condition);
                ResolveStatement(ifs.ThenBranch);
                if (ifs.ElseBranch != null)
                {
                    ResolveStatement(ifs.ElseBranch);
                }
            }
            else if (stmt is ForStatement fors)
            {
                var parentScope = _currentScope;
                _currentScope = new Scope(parentScope);

                if (fors.Initializer != null) ResolveStatement(fors.Initializer);
                if (fors.Condition != null) ResolveExpression(fors.Condition);
                if (fors.Increment != null) ResolveExpression(fors.Increment);

                ResolveStatement(fors.Body);

                _currentScope = parentScope;
            }
            else if (stmt is ReturnStatement r)
            {
                if (r.Expression != null)
                {
                    ResolveExpression(r.Expression);
                }
            }
        }

        private void ResolveExpression(Expression expr)
        {
            if (expr is LiteralExpression lit)
            {
                return;
            }
            if (expr is IdentifierExpression id)
            {
                string type = _currentScope.Lookup(id.Name);
                if (type == null)
                {
                    type = id.Name switch
                    {
                        "iResolution" => "vec3",
                        "iTime" => "float",
                        "iTimeDelta" => "float",
                        "iFrameRate" => "float",
                        "iFrame" => "int",
                        "iMouse" => "vec4",
                        "iDate" => "vec4",
                        _ => "float"
                    };
                }
                expr.ResolvedType = type;
            }
            else if (expr is UnaryExpression u)
            {
                ResolveExpression(u.Operand);
                expr.ResolvedType = u.Operand.ResolvedType;
            }
            else if (expr is BinaryExpression bin)
            {
                ResolveExpression(bin.Left);
                ResolveExpression(bin.Right);

                var t0 = bin.Left.ResolvedType;
                var t1 = bin.Right.ResolvedType;
                if (bin.Op == "==" || bin.Op == "!=" || bin.Op == "<" || bin.Op == ">" || bin.Op == "<=" || bin.Op == ">=" || bin.Op == "&&" || bin.Op == "||")
                {
                    expr.ResolvedType = "bool";
                }
                else
                {
                    if (IsVector(t0)) expr.ResolvedType = t0;
                    else if (IsVector(t1)) expr.ResolvedType = t1;
                    else expr.ResolvedType = t0;
                }
            }
            else if (expr is TernaryExpression ter)
            {
                ResolveExpression(ter.Condition);
                ResolveExpression(ter.TrueBranch);
                ResolveExpression(ter.FalseBranch);
                expr.ResolvedType = ter.TrueBranch.ResolvedType;
            }
            else if (expr is CallExpression call)
            {
                var argTypes = new List<string>();
                foreach (var arg in call.Arguments)
                {
                    ResolveExpression(arg);
                    argTypes.Add(arg.ResolvedType);
                }
                expr.ResolvedType = GetFunctionReturnType(call.Callee, argTypes);
            }
            else if (expr is MemberAccessExpression mem)
            {
                ResolveExpression(mem.Base);
                var baseType = mem.Base.ResolvedType;
                if (IsVector(baseType))
                {
                    int sz = mem.Member.Length;
                    mem.ResolvedType = sz switch
                    {
                        1 => "float",
                        2 => "vec2",
                        3 => "vec3",
                        4 => "vec4",
                        _ => "float"
                    };
                }
                else if (_structs.TryGetValue(baseType, out var st))
                {
                    if (st.Members.TryGetValue(mem.Member, out var memberType))
                    {
                        mem.ResolvedType = memberType;
                    }
                    else
                    {
                        mem.ResolvedType = "float";
                    }
                }
                else
                {
                    mem.ResolvedType = "float";
                }
            }
            else if (expr is ArrayAccessExpression arr)
            {
                ResolveExpression(arr.Base);
                ResolveExpression(arr.Index);
                var baseType = arr.Base.ResolvedType;
                if (IsVector(baseType))
                {
                    arr.ResolvedType = "float";
                }
                else if (baseType.EndsWith("[]"))
                {
                    arr.ResolvedType = baseType.Substring(0, baseType.Length - 2);
                }
                else
                {
                    arr.ResolvedType = "float";
                }
            }
            else if (expr is AssignmentExpression assign)
            {
                ResolveExpression(assign.Left);
                ResolveExpression(assign.Right);
                expr.ResolvedType = assign.Left.ResolvedType;
            }
        }

        private static bool IsVector(string type) => type != null && (type.StartsWith("vec") || type.StartsWith("ivec") || type.StartsWith("uvec") || type.StartsWith("bvec"));

        private static string GetDeclarationResolvedType(VariableDeclarationStatement declaration)
        {
            return declaration.ArraySize.HasValue
                ? $"{declaration.Type}[]"
                : declaration.Type;
        }

        private string GetFunctionReturnType(string name, List<string> argTypes)
        {
            if (name == "cos" || name == "sin" || name == "abs" || name == "tan" || name == "sqrt" || name == "log" || name == "exp" || name == "floor" || name == "ceil" || name == "fract" || name == "sign" || name == "normalize")
            {
                return argTypes.Count > 0 ? argTypes[0] : "float";
            }
            if (name == "length" || name == "distance" || name == "dot") return "float";
            if (name == "cross") return "vec3";
            if (name == "reflect" || name == "refract") return argTypes.Count > 0 ? argTypes[0] : "vec3";
            if (name == "min" || name == "max" || name == "clamp" || name == "mix" || name == "step" || name == "smoothstep")
            {
                return GetVectorizedBuiltinReturnType(argTypes);
            }
            if (name == "atan" || name == "atan2")
            {
                return argTypes.Count > 0 ? argTypes[0] : "float";
            }
            if (name == "pow") return argTypes.Count > 0 ? argTypes[0] : "float";

            string signatureKey = $"{name}({string.Join(",", argTypes)})";
            if (_userFunctions.TryGetValue(signatureKey, out var returnType)) return returnType;
            if (_userFunctions.TryGetValue(name, out var fallbackType)) return fallbackType;

            if (name == "vec2") return "vec2";
            if (name == "vec3") return "vec3";
            if (name == "vec4") return "vec4";
            if (name == "ivec2") return "ivec2";
            if (name == "ivec3") return "ivec3";
            if (name == "ivec4") return "ivec4";
            if (name == "uvec2") return "uvec2";
            if (name == "uvec3") return "uvec3";
            if (name == "uvec4") return "uvec4";
            if (name == "bvec2") return "bvec2";
            if (name == "bvec3") return "bvec3";
            if (name == "bvec4") return "bvec4";
            if (name == "mat2") return "mat2";
            if (name == "mat3") return "mat3";
            if (name == "mat4") return "mat4";

            return "float";
        }

        private static string GetVectorizedBuiltinReturnType(List<string> argTypes)
        {
            foreach (var type in argTypes)
            {
                if (IsVector(type))
                {
                    return type;
                }
            }

            return argTypes.Count > 0 ? argTypes[0] : "float";
        }
    }

    public class Generator
    {
        private readonly Dictionary<string, StructType> _structs;
        private readonly Dictionary<string, string> _userFunctions;
        private readonly List<VariableDeclarationStatement> _globalVarsWithInitializers = new();
        private readonly Dictionary<string, FunctionDeclaration> _userFunctionDecls = new();
        private FunctionDeclaration? _currentFunction = null;

        public Generator(Dictionary<string, StructType> structs, Dictionary<string, string> userFunctions)
        {
            _structs = structs;
            _userFunctions = userFunctions;
        }

        public string Generate(List<AstNode> nodes)
        {
            _globalVarsWithInitializers.Clear();
            _userFunctionDecls.Clear();
            foreach (var node in nodes)
            {
                if (node is FunctionDeclaration f)
                {
                    _userFunctionDecls[f.Name] = f;
                    var paramTypes = new List<string>();
                    foreach (var p in f.Parameters) paramTypes.Add(p.Type);
                    string signatureKey = $"{f.Name}({string.Join(",", paramTypes)})";
                    _userFunctionDecls[signatureKey] = f;
                }
            }

            var sb = new StringBuilder();

            sb.AppendLine("// Auto-generated from GLSL by ProGPU Compiler");
            sb.AppendLine("fn wgsl_mod_ff(x: f32, y: f32) -> f32 { return x - y * floor(x / y); }");
            sb.AppendLine("fn wgsl_mod_v2f(x: vec2<f32>, y: f32) -> vec2<f32> { return x - y * floor(x / y); }");
            sb.AppendLine("fn wgsl_mod_v3f(x: vec3<f32>, y: f32) -> vec3<f32> { return x - y * floor(x / y); }");
            sb.AppendLine("fn wgsl_mod_v4f(x: vec4<f32>, y: f32) -> vec4<f32> { return x - y * floor(x / y); }");
            sb.AppendLine("fn wgsl_mod_v2v2(x: vec2<f32>, y: vec2<f32>) -> vec2<f32> { return x - y * floor(x / y); }");
            sb.AppendLine("fn wgsl_mod_v3v3(x: vec3<f32>, y: vec3<f32>) -> vec3<f32> { return x - y * floor(x / y); }");
            sb.AppendLine("fn wgsl_mod_v4v4(x: vec4<f32>, y: vec4<f32>) -> vec4<f32> { return x - y * floor(x / y); }");
            sb.AppendLine();

            foreach (var node in nodes)
            {
                if (node is VariableDeclarationStatement v && v.Initializer != null)
                {
                    _globalVarsWithInitializers.Add(v);
                }
            }

            foreach (var node in nodes)
            {
                if (node is StructDeclaration s)
                {
                    sb.AppendLine(GenerateStruct(s));
                }
                else if (node is FunctionDeclaration f)
                {
                    sb.AppendLine(GenerateFunction(f));
                }
                else if (node is VariableDeclarationStatement v)
                {
                    sb.AppendLine(GenerateGlobalVariable(v));
                }
            }

            return sb.ToString();
        }

        private string GenerateStruct(StructDeclaration node)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"struct {node.Name} {{");
            foreach (var member in node.Members)
            {
                sb.AppendLine($"    {ResolveIdentifier(member.Name)}: {MapType(member.Type)},");
            }
            sb.AppendLine("};");
            return sb.ToString();
        }

        private string GenerateGlobalVariable(VariableDeclarationStatement node)
        {
            string typeStr = MapDeclarationType(node);
            return $"var<private> {ResolveIdentifier(node.Name)}: {typeStr};";
        }

        private string GenerateFunction(FunctionDeclaration node)
        {
            _currentFunction = node;
            string resultStr;
            if (node.Name == "mainImage")
            {
                string colorName = "fragColor";
                string coordName = "fragCoord";
                if (node.Parameters.Count >= 1) colorName = ResolveIdentifier(node.Parameters[0].Name);
                if (node.Parameters.Count >= 2) coordName = ResolveIdentifier(node.Parameters[1].Name);

                var sb = new StringBuilder();
                sb.AppendLine($"fn mainImage({coordName}: vec2<f32>) -> vec4<f32> {{");
                sb.AppendLine($"    var {colorName}: vec4<f32>;");
                
                foreach (var v in _globalVarsWithInitializers)
                {
                    sb.AppendLine($"    {ResolveIdentifier(v.Name)} = {GenerateExpression(v.Initializer)};");
                }

                foreach (var stmt in node.Body.Statements)
                {
                    sb.Append(GenerateStatement(stmt, "    "));
                }
                sb.AppendLine($"    return {colorName};");
                sb.AppendLine("}");
                resultStr = sb.ToString();
            }
            else
            {
                var paramsList = new List<string>();
                var mutableCopies = new List<string>();
                foreach (var p in node.Parameters)
                {
                    string argName = $"{ResolveIdentifier(p.Name)}_arg";
                    bool isOut = p.Modifier == "out" || p.Modifier == "inout";
                    if (isOut)
                    {
                        paramsList.Add($"{argName}: ptr<function, {MapType(p.Type)}>");
                        mutableCopies.Add($"    var {ResolveIdentifier(p.Name)} = *{argName};");
                    }
                    else
                    {
                        paramsList.Add($"{argName}: {MapType(p.Type)}");
                        mutableCopies.Add($"    var {ResolveIdentifier(p.Name)} = {argName};");
                    }
                }
                var paramTypes = new List<string>();
                foreach (var p in node.Parameters) paramTypes.Add(p.Type);
                string wgslName = ShaderToyTranspiler.GetUniqueWgslName(node.Name, paramTypes);
                string ret = MapType(node.ReturnType);
                string retStr = ret == "void" ? "" : $" -> {ret}";
                var sb = new StringBuilder();
                sb.AppendLine($"fn {wgslName}({string.Join(", ", paramsList)}){retStr} {{");
                foreach (var copy in mutableCopies)
                {
                    sb.AppendLine(copy);
                }
                foreach (var stmt in node.Body.Statements)
                {
                    sb.Append(GenerateStatement(stmt, "    "));
                }

                bool endsWithReturn = false;
                if (node.Body.Statements.Count > 0)
                {
                    var lastStmt = node.Body.Statements[node.Body.Statements.Count - 1];
                    if (lastStmt is ReturnStatement) endsWithReturn = true;
                }

                if (!endsWithReturn)
                {
                    foreach (var p in node.Parameters)
                    {
                        if (p.Modifier == "out" || p.Modifier == "inout")
                        {
                            string argName = $"{ResolveIdentifier(p.Name)}_arg";
                            sb.AppendLine($"    *{argName} = {ResolveIdentifier(p.Name)};");
                        }
                    }
                }

                sb.AppendLine("}");
                resultStr = sb.ToString();
            }
            _currentFunction = null;
            return resultStr;
        }

        private string GenerateStatement(Statement stmt, string indent)
        {
            if (stmt is BlockStatement b)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"{indent}{{");
                foreach (var s in b.Statements)
                {
                    sb.Append(GenerateStatement(s, indent + "    "));
                }
                sb.AppendLine($"{indent}}}");
                return sb.ToString();
            }
            if (stmt is ExpressionStatement e)
            {
                return $"{indent}{GenerateStatementExpression(e.Expression)};\n";
            }
            if (stmt is VariableDeclarationStatement v)
            {
                string typeStr = MapDeclarationType(v);
                if (v.Initializer != null)
                {
                    if (v.Initializer is AssignmentExpression assign)
                    {
                        string assignStmt = $"{indent}{GenerateExpression(assign)};\n";
                        string declStmt = $"{indent}var {ResolveIdentifier(v.Name)}: {typeStr} = {GenerateExpression(assign.Left)};\n";
                        return assignStmt + declStmt;
                    }
                    return $"{indent}var {ResolveIdentifier(v.Name)}: {typeStr} = {GenerateExpression(v.Initializer)};\n";
                }
                else
                {
                    return $"{indent}var {ResolveIdentifier(v.Name)}: {typeStr};\n";
                }
            }
            if (stmt is IfStatement ifs)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"{indent}if ({GenerateExpression(ifs.Condition)}) {{");
                sb.Append(GenerateStatement(ifs.ThenBranch, indent + "    "));
                if (ifs.ElseBranch != null)
                {
                    sb.AppendLine($"{indent}}} else {{");
                    sb.Append(GenerateStatement(ifs.ElseBranch, indent + "    "));
                }
                sb.AppendLine($"{indent}}}");
                return sb.ToString();
            }
            if (stmt is ForStatement fors)
            {
                string initPrelude = "";
                string initStr = GenerateForInitializer(fors.Initializer, indent + "    ", out initPrelude);
                string condStr = fors.Condition != null ? GenerateExpression(fors.Condition) : "";
                string incrStr = "";
                if (fors.Increment != null)
                {
                    if (fors.Increment is UnaryExpression u && (u.Op == "++" || u.Op == "--"))
                    {
                        incrStr = GenerateIncrementDecrementAssignment(u);
                    }
                    else
                    {
                        incrStr = GenerateExpression(fors.Increment);
                    }
                }

                var sb = new StringBuilder();
                string forIndent = indent;
                string bodyIndent = indent + "    ";
                if (initPrelude.Length > 0)
                {
                    sb.AppendLine($"{indent}{{");
                    sb.Append(initPrelude);
                    forIndent = indent + "    ";
                    bodyIndent = indent + "        ";
                }

                sb.AppendLine($"{forIndent}for ({initStr}; {condStr}; {incrStr}) {{");
                if (fors.Body is BlockStatement block)
                {
                    foreach (var s in block.Statements)
                    {
                        sb.Append(GenerateStatement(s, bodyIndent));
                    }
                }
                else
                {
                    sb.Append(GenerateStatement(fors.Body, bodyIndent));
                }
                sb.AppendLine($"{forIndent}}}");
                if (initPrelude.Length > 0)
                {
                    sb.AppendLine($"{indent}}}");
                }

                return sb.ToString();
            }
            if (stmt is ReturnStatement r)
            {
                var copyBacks = new StringBuilder();
                if (_currentFunction != null && _currentFunction.Name != "mainImage")
                {
                    foreach (var p in _currentFunction.Parameters)
                    {
                        if (p.Modifier == "out" || p.Modifier == "inout")
                        {
                            string argName = $"{ResolveIdentifier(p.Name)}_arg";
                            copyBacks.Append($"{indent}*{argName} = {ResolveIdentifier(p.Name)};\n");
                        }
                    }
                }
                if (r.Expression != null)
                {
                    return $"{copyBacks}{indent}return {GenerateExpression(r.Expression)};\n";
                }
                if (_currentFunction != null && _currentFunction.Name == "mainImage")
                {
                    var colorName = _currentFunction.Parameters.Count >= 1
                        ? ResolveIdentifier(_currentFunction.Parameters[0].Name)
                        : "fragColor";
                    return $"{indent}return {colorName};\n";
                }
                else
                {
                    return $"{copyBacks}{indent}return;\n";
                }
            }
            if (stmt is BreakStatement)
            {
                return $"{indent}break;\n";
            }
            if (stmt is ContinueStatement)
            {
                return $"{indent}continue;\n";
            }
            return "";
        }

        private string GenerateForInitializer(Statement initializer, string preludeIndent, out string prelude)
        {
            prelude = "";
            if (initializer == null)
            {
                return "";
            }

            if (initializer is VariableDeclarationStatement vd)
            {
                return GenerateForInitializerDeclaration(vd);
            }

            if (initializer is ExpressionStatement es)
            {
                return GenerateStatementExpression(es.Expression);
            }

            if (initializer is BlockStatement block)
            {
                var sb = new StringBuilder();
                foreach (var statement in block.Statements)
                {
                    if (statement is not VariableDeclarationStatement)
                    {
                        throw new NotSupportedException("Only variable declarations are supported in multi-declaration for-loop initializers.");
                    }

                    sb.Append(GenerateStatement(statement, preludeIndent));
                }

                prelude = sb.ToString();
                return "";
            }

            throw new NotSupportedException($"Unsupported for-loop initializer statement type '{initializer.GetType().Name}'.");
        }

        private string GenerateForInitializerDeclaration(VariableDeclarationStatement declaration)
        {
            string typeStr = MapDeclarationType(declaration);
            return declaration.Initializer != null
                ? $"var {ResolveIdentifier(declaration.Name)}: {typeStr} = {GenerateExpression(declaration.Initializer)}"
                : $"var {ResolveIdentifier(declaration.Name)}: {typeStr}";
        }

        private string GenerateExpression(Expression expr)
        {
            if (expr is LiteralExpression lit)
            {
                if (lit.Value is double d)
                {
                    var str = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    if (!str.Contains(".") && !str.Contains("e") && !str.Contains("E"))
                    {
                        str += ".0";
                    }
                    return str;
                }
                if (lit.Value is int i)
                {
                    return i.ToString();
                }
                if (lit.Value is bool b)
                {
                    return b ? "true" : "false";
                }
                return lit.Value.ToString();
            }
            if (expr is IdentifierExpression id)
            {
                return ResolveIdentifier(id.Name);
            }
            if (expr is UnaryExpression u)
            {
                string operand = GenerateExpression(u.Operand);
                if (IsIncrementDecrement(u.Op))
                {
                    throw new NotSupportedException(
                        "GLSL embedded increment/decrement expressions are not supported by the ShaderToy WGSL transpiler. Use increment/decrement only as a standalone statement or for-loop increment.");
                }

                if (u.IsPostfix)
                {
                    return operand + u.Op;
                }
                else
                {
                    return u.Op + operand;
                }
            }
            if (expr is BinaryExpression bin)
            {
                string left = GenerateExpression(bin.Left);
                string right = GenerateExpression(bin.Right);
                BroadcastVectorScalarAddSub(bin, ref left, ref right);
                return $"({left} {bin.Op} {right})";
            }
            if (expr is TernaryExpression ter)
            {
                string cond = GenerateExpression(ter.Condition);
                string trueVal = GenerateExpression(ter.TrueBranch);
                string falseVal = GenerateExpression(ter.FalseBranch);
                return $"select({falseVal}, {trueVal}, {cond})";
            }
            if (expr is CallExpression call)
            {
                if (call.Callee == "mod")
                {
                    var t0 = call.Arguments[0].ResolvedType;
                    var t1 = call.Arguments[1].ResolvedType;
                    string helperName = "wgsl_mod_ff";
                    if (t0 == "vec2" && t1 == "float") helperName = "wgsl_mod_v2f";
                    else if (t0 == "vec3" && t1 == "float") helperName = "wgsl_mod_v3f";
                    else if (t0 == "vec4" && t1 == "float") helperName = "wgsl_mod_v4f";
                    else if (t0 == "vec2" && t1 == "vec2") helperName = "wgsl_mod_v2v2";
                    else if (t0 == "vec3" && t1 == "vec3") helperName = "wgsl_mod_v3v3";
                    else if (t0 == "vec4" && t1 == "vec4") helperName = "wgsl_mod_v4v4";

                    var a0 = GenerateExpression(call.Arguments[0]);
                    var a1 = GenerateExpression(call.Arguments[1]);
                    return $"{helperName}({a0}, {a1})";
                }

                if (call.Callee == "atan")
                {
                    if (call.Arguments.Count == 2)
                    {
                        var a0 = GenerateExpression(call.Arguments[0]);
                        var a1 = GenerateExpression(call.Arguments[1]);
                        return $"atan2({a0}, {a1})";
                    }
                }

                if (call.Callee == "inversesqrt")
                {
                    var a0 = GenerateExpression(call.Arguments[0]);
                    return $"inverseSqrt({a0})";
                }

                if (call.Callee == "min" || call.Callee == "max")
                {
                    var arguments = GenerateVectorBroadcastedArguments(call);
                    return $"{call.Callee}({arguments[0]}, {arguments[1]})";
                }

                if (call.Callee == "step")
                {
                    var arguments = GenerateVectorBroadcastedArguments(call);
                    return $"step({arguments[0]}, {arguments[1]})";
                }

                if (call.Callee == "clamp" ||
                    call.Callee == "mix" ||
                    call.Callee == "smoothstep")
                {
                    var arguments = GenerateVectorBroadcastedArguments(call);
                    return $"{call.Callee}({arguments[0]}, {arguments[1]}, {arguments[2]})";
                }

                if (_userFunctions.ContainsKey(call.Callee))
                {
                    var argTypes = new List<string>();
                    foreach (var arg in call.Arguments)
                    {
                        argTypes.Add(arg.ResolvedType);
                    }
                    string signatureKey = $"{call.Callee}({string.Join(",", argTypes)})";
                    string wgslName;
                    FunctionDeclaration? targetDecl = null;
                    if (_userFunctionDecls.TryGetValue(signatureKey, out var decl))
                    {
                        targetDecl = decl;
                        wgslName = ShaderToyTranspiler.GetUniqueWgslName(call.Callee, argTypes);
                    }
                    else
                    {
                        var matchingKeys = new List<string>();
                        foreach (var key in _userFunctionDecls.Keys)
                        {
                            if (key.StartsWith(call.Callee + "(")) matchingKeys.Add(key);
                        }
                        if (matchingKeys.Count > 0)
                        {
                            string bestKey = matchingKeys[0];
                            foreach (var key in matchingKeys)
                            {
                                int commaCount = 0;
                                foreach (char c in key) if (c == ',') commaCount++;
                                int paramCount = key.EndsWith("()") ? 0 : commaCount + 1;
                                if (paramCount == call.Arguments.Count)
                                {
                                    bestKey = key;
                                    break;
                                }
                            }
                            if (_userFunctionDecls.TryGetValue(bestKey, out var bestDecl))
                            {
                                targetDecl = bestDecl;
                            }
                            int start = bestKey.IndexOf('(');
                            int end = bestKey.LastIndexOf(')');
                            string typeListStr = bestKey.Substring(start + 1, end - start - 1);
                            var types = new List<string>();
                            if (!string.IsNullOrEmpty(typeListStr))
                            {
                                foreach (var t in typeListStr.Split(',')) types.Add(t.Trim());
                            }
                            wgslName = ShaderToyTranspiler.GetUniqueWgslName(call.Callee, types);
                        }
                        else
                        {
                            wgslName = call.Callee;
                        }
                    }
                    var argsList = new List<string>();
                    for (int idx = 0; idx < call.Arguments.Count; idx++)
                    {
                        var a = call.Arguments[idx];
                        string aExpr = GenerateExpression(a);
                        if (targetDecl != null && idx < targetDecl.Parameters.Count)
                        {
                            var p = targetDecl.Parameters[idx];
                            if (p.Modifier == "out" || p.Modifier == "inout")
                            {
                                aExpr = $"&{aExpr}";
                            }
                        }
                        argsList.Add(aExpr);
                    }
                    return $"{wgslName}({string.Join(", ", argsList)})";
                }

                string calleeName = MapType(call.Callee);
                var args = new List<string>();
                foreach (var a in call.Arguments)
                {
                    args.Add(GenerateExpression(a));
                }
                return $"{ResolveIdentifier(calleeName)}({string.Join(", ", args)})";
            }
            if (expr is MemberAccessExpression mem)
            {
                return $"{GenerateExpression(mem.Base)}.{ResolveIdentifier(mem.Member)}";
            }
            if (expr is ArrayAccessExpression arr)
            {
                return $"{GenerateExpression(arr.Base)}[{GenerateExpression(arr.Index)}]";
            }
            if (expr is AssignmentExpression assign)
            {
                if (assign.Left is MemberAccessExpression memLeft && IsSwizzle(memLeft.Member))
                {
                    string baseExpr = GenerateExpression(memLeft.Base);
                    string rhsVal = GenerateExpression(assign.Right);
                    string op = assign.Op;
                    string fullRhs;
                    if (op == "=")
                    {
                        fullRhs = rhsVal;
                    }
                    else
                    {
                        string cleanOp = op.TrimEnd('=');
                        if ((cleanOp == "+" || cleanOp == "-") &&
                            IsVector(memLeft.ResolvedType) &&
                            IsScalar(assign.Right.ResolvedType))
                        {
                            rhsVal = $"{MapType(memLeft.ResolvedType)}({rhsVal})";
                        }

                        fullRhs = $"{baseExpr}.{memLeft.Member} {cleanOp} ({rhsVal})";
                    }
                    
                    var sb = new StringBuilder();
                    sb.AppendLine("{");
                    sb.AppendLine($"        let _tmp = {fullRhs};");
                    for (int i = 0; i < memLeft.Member.Length; i++)
                    {
                        char c = memLeft.Member[i];
                        string comp = GetSwizzleComponent(i);
                        sb.AppendLine($"        {baseExpr}.{c} = _tmp.{comp};");
                    }
                    sb.Append("    }");
                    return sb.ToString();
                }

                string left = GenerateExpression(assign.Left);
                string right = GenerateExpression(assign.Right);
                if ((assign.Op == "+=" || assign.Op == "-=") &&
                    IsVector(assign.Left.ResolvedType) &&
                    IsScalar(assign.Right.ResolvedType))
                {
                    right = $"{MapType(assign.Left.ResolvedType)}({right})";
                }

                return $"{left} {assign.Op} {right}";
            }
            return "";
        }

        private string GenerateStatementExpression(Expression expression)
        {
            if (expression is UnaryExpression unary && IsIncrementDecrement(unary.Op))
            {
                return GenerateIncrementDecrementAssignment(unary);
            }

            return GenerateExpression(expression);
        }

        private string GenerateIncrementDecrementAssignment(UnaryExpression expression)
        {
            string op = expression.Op == "++" ? "+" : "-";
            string operand = GenerateExpression(expression.Operand);
            return $"{operand} = {operand} {op} 1";
        }

        private static bool IsIncrementDecrement(string op) => op == "++" || op == "--";

        private static void BroadcastVectorScalarAddSub(BinaryExpression expression, ref string left, ref string right)
        {
            if (expression.Op != "+" && expression.Op != "-")
            {
                return;
            }

            string leftType = expression.Left.ResolvedType;
            string rightType = expression.Right.ResolvedType;
            if (IsVector(leftType) && IsScalar(rightType))
            {
                right = $"{MapType(leftType)}({right})";
            }
            else if (IsScalar(leftType) && IsVector(rightType))
            {
                left = $"{MapType(rightType)}({left})";
            }
        }

        private List<string> GenerateVectorBroadcastedArguments(CallExpression call)
        {
            var arguments = new List<string>(call.Arguments.Count);
            string? targetVectorType = null;
            foreach (var argument in call.Arguments)
            {
                if (targetVectorType == null && IsVector(argument.ResolvedType))
                {
                    targetVectorType = argument.ResolvedType;
                }

                arguments.Add(GenerateExpression(argument));
            }

            if (targetVectorType == null)
            {
                return arguments;
            }

            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (IsScalar(call.Arguments[i].ResolvedType))
                {
                    arguments[i] = $"{MapType(targetVectorType)}({arguments[i]})";
                }
            }

            return arguments;
        }

        private static bool IsSwizzle(string member)
        {
            if (member.Length <= 1) return false;
            foreach (char c in member)
            {
                if (c != 'x' && c != 'y' && c != 'z' && c != 'w' &&
                    c != 'r' && c != 'g' && c != 'b' && c != 'a')
                {
                    return false;
                }
            }
            return true;
        }

        private static string GetSwizzleComponent(int index)
        {
            return index switch
            {
                0 => "x",
                1 => "y",
                2 => "z",
                3 => "w",
                _ => "x"
            };
        }

        private static bool IsVector(string type) => type != null && (type.StartsWith("vec") || type.StartsWith("ivec") || type.StartsWith("uvec") || type.StartsWith("bvec"));

        private static bool IsScalar(string type) => type == "float" || type == "int" || type == "uint";

        private static string MapDeclarationType(VariableDeclarationStatement declaration)
        {
            var elementType = MapType(declaration.Type);
            return declaration.ArraySize.HasValue
                ? $"array<{elementType}, {declaration.ArraySize.Value}>"
                : elementType;
        }

        private static string MapType(string glslType) => ShaderToyTranspiler.MapType(glslType);
        private static string ResolveIdentifier(string name) => ShaderToyTranspiler.ResolveIdentifier(name);
    }
}
