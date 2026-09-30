using System;
using System.Collections.Generic;
using System.Text;

namespace SvnMethodLens.Editor
{
    /// <summary>
    /// 编辑器装饰层用的轻量级方法/属性边界扫描器。
    /// 不依赖 Roslyn（避免与 VS 自带 Roslyn 版本冲突），仅做行级启发式分析，
    /// 覆盖常见方法/构造函数/属性声明。精确分析请走 SvnMethodLens.Core（Roslyn）。
    /// 行号均为 1-based。
    /// </summary>
    public sealed class EditorMember
    {
        public string Name;
        public int StartLine;
        public int EndLine;
        public string Kind; // "method" | "ctor" | "property"
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

        public static List<EditorMember> Scan(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var members = new List<EditorMember>();
            var stack = new Stack<(int declLine, bool isMember)>();

            int prevNonEmpty = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var clean = Strip(raw);
                int opens = CountChar(clean, '{');
                int closes = CountChar(clean, '}');

                for (int o = 0; o < opens; o++)
                {
                    // 判定成员声明行：
                    // 1) 当前行自带声明（如 `public void X() {`、`public int X {`）→ 当前行；
                    // 2) 当前行是裸 `{` → 向上找签名语句的起始行（支持多行签名）；
                    // 3) 都不是（namespace/class 由 TypeKeywords 排除）→ 非成员。
                    // StartLine 必须指向签名行，装饰才能画在方法名上方（而不是 `{` 上面）。
                    int declLine = i;
                    bool isMember;
                    var noBraces = clean.Replace("{", "").Trim();

                    if (noBraces.Length > 0 && LooksLikeMember(clean.Trim()))
                    {
                        isMember = true;
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

                        var prevText = Strip(lines[prevNonEmpty]).Trim();
                        isMember = !prevText.EndsWith(";")           // 完整语句不是成员（如 foo(); 后跟 if {）
                            && !Strip(lines[first]).Trim().EndsWith("{")
                            && LooksLikeMember(signature);
                        if (isMember) declLine = first;
                    }
                    else
                    {
                        isMember = false;
                    }
                    stack.Push((declLine, isMember));
                }

                for (int c = 0; c < closes && stack.Count > 0; c++)
                {
                    var top = stack.Pop();
                    if (top.isMember)
                    {
                        var name = ExtractName(lines[top.declLine]);
                        // 调用链（xxx.Add / xxx.Select）不是成员声明
                        if (name.Contains(".")) continue;
                        members.Add(new EditorMember
                        {
                            Name = name,
                            StartLine = top.declLine + 1,
                            EndLine = i + 1,
                            Kind = DeriveKind(lines[top.declLine])
                        });
                    }
                }

                if (raw.Trim().Length > 0) prevNonEmpty = i;
            }

            members.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
            return members;
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
            var firstWord = t.Split(new[] { ' ', '\t', '(' },
                StringSplitOptions.RemoveEmptyEntries);
            string fw = firstWord.Length > 0 ? firstWord[0] : "";
            if (fw == "public" || fw == "private" || fw == "protected" || fw == "internal")
            {
                // 第二词可能是 static/async 等，取第一个像名字的词
                if (firstWord.Length > 1 && firstWord[1] == "static") { }
            }
            // 构造函数：名字与所在类型同名无法在此确定，统一标 method
            if (t.Contains("=>") && !t.Contains("(")) return "property";
            if (!t.Contains("(") && t.EndsWith("{")) return "property";
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
