using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SvnMethodLens.Editor
{
    /// <summary>
    /// 编辑器装饰层用的轻量级成员边界扫描器（不依赖 Roslyn，避免与 VS 自带 Roslyn 版本冲突）。
    /// 行级启发式分析，覆盖：类型声明（class/struct/interface/enum/record）、
    /// 方法/构造函数、属性（块体 / 表达式体 / 单行自动属性）。
    /// 精确分析请走 SvnMethodLens.Core（Roslyn）。行号均为 1-based。
    /// </summary>
    public sealed class EditorMember
    {
        public string Name;
        public int StartLine;
        public int EndLine;
        public string Kind; // "method" | "ctor" | "property" | "type"
    }

    public static class EditorMethodScanner
    {
        private static readonly HashSet<string> ControlKeywords = new HashSet<string>
        {
            "if", "for", "foreach", "while", "switch", "lock", "using", "do",
            "try", "catch", "finally", "fixed", "unsafe", "else", "new", "return", "throw", "await"
        };
        private static readonly HashSet<string> TypeKeywords = new HashSet<string>
        {
            "class", "struct", "interface", "enum", "namespace", "record"
        };

        /// <summary>类型声明前允许出现的修饰符（判断 "class/enum/..." 前面只有修饰符）。</summary>
        private static readonly HashSet<string> TypeModifiers = new HashSet<string>
        {
            "public", "private", "protected", "internal", "static", "sealed", "abstract",
            "partial", "unsafe", "new", "readonly", "ref", "file"
        };

        /// <summary>单行自动属性：public string X { get; set; }</summary>
        private static readonly Regex AutoPropertyRx = new Regex(
            @"^(?:\[[^\]]*\]\s*)*" +                                          // 特性
            @"(?:(?:public|private|protected|internal|static|virtual|override|sealed|abstract|new|readonly|required|partial|extern|unsafe|volatile|async)\s+)*" +
            @"[\w\.<>,\[\]\?\s]+\s([A-Za-z_]\w*)\s*\{\s*(?:get|set|init)\s*;[^{}]*\}\s*;?\s*$",
            RegexOptions.Compiled);

        private static readonly Regex TypeDeclRx = new Regex(
            @"(?:record\s+(?:struct|class)\s+|class\s+|struct\s+|interface\s+|enum\s+)([A-Za-z_]\w*)",
            RegexOptions.Compiled);

        private enum BraceKind { None, Member, Type }

        private sealed class Frame
        {
            public int DeclLine;
            public BraceKind Kind;
        }

        public static List<EditorMember> Scan(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var members = new List<EditorMember>();
            var stack = new Stack<Frame>();
            // 当前已打开的"成员块"层数。>0 表示我们正在某个方法/属性体内部，
            // 此时再出现的"像方法的块"是局部函数/嵌套访问器，不应作为独立标注。
            // 注意：类型（class/enum）的花括号不增加该层数，否则类里的方法都会被吞掉。
            int memberDepth = 0;
            var memberDepthAtLine = new int[lines.Length];

            int prevNonEmpty = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                memberDepthAtLine[i] = memberDepth;
                var raw = lines[i];
                var clean = Strip(raw);
                int opens = CountChar(clean, '{');
                int closes = CountChar(clean, '}');

                for (int o = 0; o < opens; o++)
                {
                    // 判定声明行（签名可能跨多行）：
                    // 1) 当前行自带声明（如 `public void X() {`、`public int X {`）→ 当前行；
                    // 2) 当前行是裸 `{` → 向上拼接多行签名作为声明行；
                    // StartLine 必须指向签名行，装饰才能画在名字上方而不是 `{` 上面。
                    int declLine = i;
                    string sig = null;
                    var noBraces = clean.Replace("{", "").Trim();

                    if (noBraces.Length > 0)
                    {
                        sig = clean.Trim();
                    }
                    else if (prevNonEmpty >= 0 && prevNonEmpty != i)
                    {
                        // 从 { 上方最近的非空行向上拼接多行签名（上行以 , ( = => 结尾即为续行）
                        int first = prevNonEmpty;
                        while (first > 0)
                        {
                            var above = Strip(lines[first - 1]).Trim();
                            if (above.Length == 0) break;
                            if (above.EndsWith(",") || above.EndsWith("(") ||
                                above.EndsWith("=") || above.EndsWith("=>"))
                                first--;
                            else break;
                        }

                        var joined = new StringBuilder();
                        for (int j = first; j <= prevNonEmpty; j++)
                        {
                            if (joined.Length > 0) joined.Append(' ');
                            joined.Append(Strip(lines[j]).Trim());
                        }
                        var signature = joined.ToString();

                        // 完整语句（以 ; 结尾）或块起始行（以 { 结尾）都不是成员声明
                        var prevText = Strip(lines[prevNonEmpty]).Trim();
                        if (prevText.EndsWith(";") || Strip(lines[first]).Trim().EndsWith("{"))
                            sig = null;
                        else { sig = signature; declLine = first; }
                    }

                    var kind = BraceKind.None;
                    if (sig != null && memberDepth == 0)
                    {
                        if (LooksLikeType(sig)) kind = BraceKind.Type;
                        else if (LooksLikeMember(sig) || LooksLikePropertyHead(sig)) kind = BraceKind.Member;
                    }

                    if (kind == BraceKind.Member) memberDepth++;
                    stack.Push(new Frame { DeclLine = declLine, Kind = kind });
                }

                for (int c = 0; c < closes && stack.Count > 0; c++)
                {
                    var top = stack.Pop();
                    if (top.Kind == BraceKind.Member) memberDepth--;

                    if (top.Kind == BraceKind.Member)
                    {
                        var name = ExtractName(lines[top.DeclLine]);
                        // 调用链（xxx.Add / xxx.Select）不是成员声明
                        if (name.Contains(".")) continue;
                        members.Add(new EditorMember
                        {
                            Name = name,
                            StartLine = top.DeclLine + 1,
                            EndLine = i + 1,
                            Kind = DeriveKind(lines[top.DeclLine])
                        });
                    }
                    else if (top.Kind == BraceKind.Type)
                    {
                        var name = ExtractTypeName(lines[top.DeclLine]);
                        if (string.IsNullOrEmpty(name) || name.Contains(".")) continue;
                        members.Add(new EditorMember
                        {
                            Name = name,
                            StartLine = top.DeclLine + 1,
                            EndLine = i + 1,
                            Kind = "type"
                        });
                    }
                }

                if (raw.Trim().Length > 0) prevNonEmpty = i;
            }

            // 第二趟：单行成员（`public string X { get; set; }`、`public int X => ...;`）。
            // 这些行花括号自平衡，第一趟的"见到 { 才判定"机制看不到它们。
            var seen = new HashSet<int>();
            foreach (var m in members) seen.Add(m.StartLine);
            for (int i = 0; i < lines.Length; i++)
            {
                if (seen.Contains(i + 1)) continue;
                if (memberDepthAtLine[i] != 0) continue; // 方法体内部的语句不是独立成员
                var t = Strip(lines[i]).Trim();
                if (t.Length == 0) continue;
                var name = TrySingleLineMember(t, out var kind);
                if (string.IsNullOrEmpty(name) || name.Contains(".")) continue;
                members.Add(new EditorMember { Name = name, StartLine = i + 1, EndLine = i + 1, Kind = kind });
            }

            members.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
            return members;
        }

        /// <summary>单行成员识别：自动属性 / 表达式体属性 / 表达式体方法。</summary>
        private static string TrySingleLineMember(string t, out string kind)
        {
            kind = null;

            var m = AutoPropertyRx.Match(t);
            if (m.Success)
            {
                kind = "property";
                return m.Groups[1].Value;
            }

            if (t.EndsWith(";") && t.Contains("=>"))
            {
                int idx = t.IndexOf("=>", StringComparison.Ordinal);
                var before = t.Substring(0, idx);
                if (before.Contains("=")) return null;             // 字段初始化/lambda 赋值，不是声明
                if (ControlKeywords.Contains(FirstWord(before))) return null;
                if (!before.Contains("("))
                {
                    kind = "property";
                    return ExtractName(before);
                }
                if (before.Contains(")"))
                {
                    kind = "method";
                    return ExtractName(t);
                }
            }
            return null;
        }

        /// <summary>裸 `{` 上方的签名是否是"修饰符 + 类型 + 名字"形式的属性/字段头（多行属性声明）。</summary>
        private static bool LooksLikePropertyHead(string sig)
        {
            if (string.IsNullOrEmpty(sig)) return false;
            if (sig.Contains("(") || sig.Contains("=") || sig.Contains(";") || sig.Contains("{")) return false;
            if (LooksLikeType(sig)) return false;
            var words = sig.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return false;
            if (ControlKeywords.Contains(words[0]) || TypeKeywords.Contains(words[0])) return false;
            return Regex.IsMatch(words[words.Length - 1], @"^[A-Za-z_]\w*$");
        }

        /// <summary>是否是类型声明（class / struct / interface / enum / record），且前面只有修饰符。</summary>
        private static bool LooksLikeType(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            foreach (var kw in TypeKeywords)
            {
                if (kw == "namespace") continue;
                var m = Regex.Match(t, @"(^|\s)" + kw + @"(\s|$)");
                if (!m.Success) continue;
                var before = t.Substring(0, m.Index).Trim();
                if (before.Length == 0) return true;
                var words = before.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                bool ok = true;
                foreach (var w in words)
                {
                    if (!TypeModifiers.Contains(w.Trim('[', ']'))) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }

        private static string ExtractTypeName(string line)
        {
            var t = Strip(line).Trim();
            var m = TypeDeclRx.Match(t);
            if (m.Success) return m.Groups[1].Value;
            return ExtractName(t);
        }

        private static string FirstWord(string t)
        {
            var parts = t.Split(new[] { ' ', '\t', '(', '<', '[', ':' },
                StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0].TrimStart('[', ']') : "";
        }

        private static bool LooksLikeMember(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            var firstWord = t.Split(new[] { ' ', '\t', '(', '<', '[', ':' },
                StringSplitOptions.RemoveEmptyEntries);
            string fw = firstWord.Length > 0 ? firstWord[0] : "";
            if (ControlKeywords.Contains(fw) || TypeKeywords.Contains(fw)) return false;

            if (t.Contains("=>"))
            {
                int idx = t.IndexOf("=>", System.StringComparison.Ordinal);
                string before = t.Substring(0, idx);
                // 表达式体属性/运算符（无 '('），lambda（有 '('）跳过
                return !before.Contains("(");
            }

            if (t.Contains("(") && t.Contains(")"))
            {
                // 赋值 / lambda（如 `var x = Foo(`、`x => x.Foo(`）不是成员声明
                int ip = t.IndexOf('(');
                int ie = t.IndexOf('=');
                if (ie >= 0 && ie < ip) return false;
                return true; // 方法 / 构造函数 / 局部函数 / 运算符
            }

            // 属性：标识符后直接跟 '{'
            if (t.TrimEnd().EndsWith("{") || t.Trim().EndsWith("{"))
                return true;

            return false;
        }

        private static string DeriveKind(string line)
        {
            var t = line.Trim();
            // 构造函数：名字与所在类型同名无法在此确定，统一标 method
            if (t.Contains("=>") && !t.Contains("(")) return "property";
            if (!t.Contains("(")) return "property"; // `public string Name` / `public string Name {`
            return "method";
        }

        private static string ExtractName(string line)
        {
            var t = line.Trim();
            int p = t.IndexOf('(');
            string head = p >= 0 ? t.Substring(0, p) : t;
            head = head.Replace("{", "").Replace("=>", "").Trim();
            var parts = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var name = parts.Length > 0 ? parts[parts.Length - 1] : head;
            int g = name.IndexOf('<');
            if (g > 0) name = name.Substring(0, g);
            return name;
        }

        /// <summary>去掉字符串字面量与注释，避免误数其中的花括号。</summary>
        private static string Strip(string line)
        {
            var sb = new StringBuilder();
            bool inStr = false, inCh = false, inLineC = false, inBlock = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                char n = i + 1 < line.Length ? line[i + 1] : '\0';
                if (inLineC) { if (c == '\n') inLineC = false; continue; }
                if (inBlock) { if (c == '*' && n == '/') { inBlock = false; i++; } continue; }
                if (inStr) { if (c == '"' && (i == 0 || line[i - 1] != '\\')) inStr = false; continue; }
                if (inCh) { if (c == '\'' && (i == 0 || line[i - 1] != '\\')) inCh = false; continue; }
                if (c == '/' && n == '/') { inLineC = true; continue; }
                if (c == '/' && n == '*') { inBlock = true; i++; continue; }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inCh = true; continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static int CountChar(string s, char c)
        {
            int n = 0;
            foreach (var ch in s) if (ch == c) n++;
            return n;
        }
    }
}
