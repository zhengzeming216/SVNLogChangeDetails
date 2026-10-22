using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SvnMethodLens.Editor
{
    /// <summary>
    /// JavaScript 方法扫描器（行级启发式，产出与 EditorMethodScanner 相同的 EditorMember）。
    /// 覆盖 GFMSWeb 里常见的写法：
    ///   function foo(a) { }                          —— 函数声明
    ///   var f = function (a) { }                     —— 函数表达式赋值
    ///   const f = (a) => { } / const f = a => { }    —— 箭头函数（块体）
    ///   "name": function (a) { } / name: (a) => { } / name(a) { }
    ///                                               —— 对象字面量方法（jQuery $.extend / $.ajax 主力写法）
    ///   async / static / get / set / * 修饰
    ///   class A [extends B] { }                      —— 类型（类方法在其内部被标注）
    /// 与 C# 扫描器同一策略：只标注"顶层"成员——方法体内的嵌套函数/回调不算独立单元。
    ///
    /// 实现要点：
    ///   1) 先把字符串/注释/正则字面量/模板字符串文本清掉（保留 ${} 插值内的代码与全部换行），
    ///      避免其中的花括号污染成员体配平；
    ///   2) 按语句起点识别声明，向前找函数体 '{'（括号深度回到声明处才算），
    ///      ';' / ',' 回到该深度说明是表达式体（无 {} 的函数）或非函数表达式 → 放弃；
    ///   3) 花括号栈记录每个成员/类型/普通块的起止，闭合时产出成员。
    /// </summary>
    public static class JavaScriptScanner
    {
        // 控制流/关键字：这些开头的一律不是成员声明
        private static readonly HashSet<string> ControlKeywords = new HashSet<string>
        {
            "if","for","while","switch","catch","with","return","typeof","instanceof",
            "new","delete","void","throw","await","yield","else","do","try","finally",
            "in","of","function","class","import","export","default","super","this",
            "var","let","const"
        };

        // 标识符后允许紧跟正则字面量的关键字（其余标识符后的 '/' 一律按除号处理）
        private static readonly HashSet<string> RegexKeywords = new HashSet<string>
        {
            "return","typeof","instanceof","in","of","new","delete","void","do","else",
            "case","throw","yield","await"
        };

        // [export [default]] [async] function [*] name (
        private static readonly Regex FuncDeclRx = new Regex(
            @"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*([A-Za-z_$][\w$]*)\s*\(",
            RegexOptions.Compiled);

        // [export [default]] class Name
        private static readonly Regex ClassRx = new Regex(
            @"^\s*(?:export\s+)?(?:default\s+)?class\s+([A-Za-z_$][\w$]*)",
            RegexOptions.Compiled);

        // 对象字面量方法：["']name["'] : [async] function [*] (
        private static readonly Regex ObjFnRx = new Regex(
            @"^\s*([""']?)([\w$-]+)\1\s*:\s*(?:async\s+)?function\s*\*?\s*\(",
            RegexOptions.Compiled);

        // 对象字面量箭头：["']name["'] : [async] ((…) | ident) =>
        private static readonly Regex ObjArrowRx = new Regex(
            @"^\s*([""']?)([\w$-]+)\1\s*:\s*(?:async\s+)?(?:\([^()]*\)|[A-Za-z_$][\w$]*)\s*=>",
            RegexOptions.Compiled);

        // 赋值式函数：[export] [const|let|var] name = [async] (function [*] [name] ( | ( | ident =>
        private static readonly Regex AssignFnRx = new Regex(
            @"^\s*(?:export\s+)?(?:(?:const|let|var)\s+)?([A-Za-z_$][\w$]*)\s*=\s*(?:async\s+)?(?:function\s*\*?\s*(?:[A-Za-z_$][\w$]*\s*)?\(|\(|[A-Za-z_$][\w$]*\s*=>)",
            RegexOptions.Compiled);

        // 方法简写（class / 对象字面量）：[async|static|get|set] [*] name ( 参数 )
        // 参数内不允许再出现括号/花括号——把 foo(a, function () { 这类调用+回调排除掉
        private static readonly Regex MethodRx = new Regex(
            @"^\s*(?:(?:async|static|get|set|public|private|protected)\s+)*\*?\s*([A-Za-z_$][\w$]*)\s*\(([^{}()]*)\)",
            RegexOptions.Compiled);

        private sealed class JsDecl
        {
            public string Name;
            public bool IsType;     // class
            public bool Ambiguous;  // MethodRx 匹配：既可能是方法简写，也可能是调用语句
            public int DeclLine;    // 0-based
        }

        private sealed class Frame
        {
            public int DeclLine;
            public string Name;
            public bool IsMember;
            public bool IsType;
        }

        public static List<EditorMember> Scan(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<EditorMember>();
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            var cleaned = CleanJs(text);
            var lines = cleaned.Split('\n');
            var members = new List<EditorMember>();
            var stack = new Stack<Frame>();
            int memberDepth = 0;
            JsDecl pending = null;
            int pendingBaseParen = 0, pendingBaseBracket = 0;
            char pendingLastCh = '\0'; // pending 签名（含续行）最后一个非空白字符

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                // pending 跨行保护：签名不可能无限长；续行首不是"开放"结尾（, ( = > : [ {）时，
                // 上一行已是完整语句（ASI），pending 是"调用语句后紧跟块"的误判 → 丢弃。
                // 续行以 } / 控制关键字开头、或本身又是一条新声明 → 同样丢弃。
                if (pending != null && pending.DeclLine < i)
                {
                    bool open = pendingLastCh == ',' || pendingLastCh == '(' || pendingLastCh == '='
                             || pendingLastCh == '>' || pendingLastCh == ':' || pendingLastCh == '['
                             || pendingLastCh == '{';
                    if (!open || i - pending.DeclLine > 40) pending = null;
                    else if (trimmed.StartsWith("}")) pending = null;
                    else
                    {
                        var fw = FirstWord(trimmed);
                        if (fw.Length > 0 && ControlKeywords.Contains(fw)) pending = null;
                        else if (TryMatchDecl(trimmed) != null) pending = null;
                    }
                }

                int paren = 0, bracket = 0, stmtStart = 0;
                for (int c = 0; c <= line.Length; c++)
                {
                    // 语句起点尝试识别声明（只在顶层：方法体内的嵌套函数/回调不标注）
                    if (c == stmtStart && pending == null && memberDepth == 0)
                    {
                        var d = TryMatchDecl(c >= line.Length ? "" : line.Substring(c));
                        if (d != null)
                        {
                            d.DeclLine = i;
                            pending = d;
                            pendingBaseParen = paren;
                            pendingBaseBracket = bracket;
                            pendingLastCh = LastNonWsChar(c >= line.Length ? "" : line.Substring(c));
                        }
                    }
                    if (c == line.Length) break;
                    var ch = line[c];

                    if (ch == '(') paren++;
                    else if (ch == ')') { if (paren > 0) paren--; }
                    else if (ch == '[') bracket++;
                    else if (ch == ']') { if (bracket > 0) bracket--; }
                    else if (ch == '{')
                    {
                        if (pending != null && paren == pendingBaseParen && bracket == pendingBaseBracket)
                        {
                            // 括号深度回到声明处 → 这个 { 就是 pending 的函数体
                            stack.Push(new Frame
                            {
                                DeclLine = pending.DeclLine,
                                Name = pending.Name,
                                IsMember = !pending.IsType,
                                IsType = pending.IsType
                            });
                            if (!pending.IsType) memberDepth++;
                            pending = null;
                        }
                        else
                        {
                            // 对象字面量 / 控制流块 / 参数内的对象 / 调用里的回调：普通块。
                            // ambiguous 的 pending（形如 foo(a,b)，可能是调用）遇到深度不符的 {
                            // 说明它是调用语句（回调/对象参数开始了）→ 丢弃，避免挡住后面的声明。
                            if (pending != null && pending.Ambiguous &&
                                (paren != pendingBaseParen || bracket != pendingBaseBracket))
                                pending = null;
                            stack.Push(new Frame());
                        }
                    }
                    else if (ch == '}')
                    {
                        if (stack.Count > 0)
                        {
                            var f = stack.Pop();
                            if (f.IsMember)
                            {
                                memberDepth--;
                                members.Add(new EditorMember
                                {
                                    Name = f.Name,
                                    StartLine = f.DeclLine + 1,
                                    EndLine = i + 1,
                                    Kind = "method"
                                });
                            }
                            else if (f.IsType)
                            {
                                members.Add(new EditorMember
                                {
                                    Name = f.Name,
                                    StartLine = f.DeclLine + 1,
                                    EndLine = i + 1,
                                    Kind = "type"
                                });
                            }
                        }
                    }

                    // 回到声明深度还没见到 { → 表达式体（=> 单行）或非函数表达式，放弃
                    if (pending != null && (ch == ';' || ch == ',') &&
                        paren == pendingBaseParen && bracket == pendingBaseBracket)
                    {
                        pending = null;
                    }

                    if (ch == '{' || ch == '}' || ch == ';' || ch == ',') stmtStart = c + 1;
                }

                if (pending != null) pendingLastCh = LastNonWsChar(line);
            }

            members.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
            return members;
        }

        private static JsDecl TryMatchDecl(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;

            var m = FuncDeclRx.Match(s);
            if (m.Success) return new JsDecl { Name = m.Groups[1].Value };

            m = ClassRx.Match(s);
            if (m.Success) return new JsDecl { Name = m.Groups[1].Value, IsType = true };

            m = ObjFnRx.Match(s);
            if (m.Success) return new JsDecl { Name = m.Groups[2].Value };

            m = ObjArrowRx.Match(s);
            if (m.Success) return new JsDecl { Name = m.Groups[2].Value };

            m = AssignFnRx.Match(s);
            if (m.Success) return new JsDecl { Name = m.Groups[1].Value };

            m = MethodRx.Match(s);
            if (m.Success)
            {
                var name = m.Groups[1].Value;
                if (!ControlKeywords.Contains(name) && name != "$" && name != "_")
                    return new JsDecl { Name = name, Ambiguous = true };
            }
            return null;
        }

        private static string FirstWord(string t)
        {
            int i = 0, j = 0;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            j = i;
            while (j < t.Length && (char.IsLetterOrDigit(t[j]) || t[j] == '_' || t[j] == '$')) j++;
            return t.Substring(i, j - i);
        }

        private static char LastNonWsChar(string t)
        {
            for (int i = t.Length - 1; i >= 0; i--)
                if (!char.IsWhiteSpace(t[i])) return t[i];
            return '\0';
        }

        #region 清洗器：去掉字符串/注释/正则/模板文本，保留 ${} 插值代码与换行

        /// <summary>
        /// 把字符串字面量、行/块注释、正则字面量、模板字符串的文本部分替换为空格
        /// （保留全部换行以维持行号），但保留模板字符串 ${...} 插值内的代码。
        /// 否则正则里的 \d{3}、JSDoc 的 @param {type}、CSS 里的 } 都会污染花括号配平。
        /// </summary>
        private static string CleanJs(string text)
        {
            var sb = new StringBuilder(text.Length + 16);
            var interp = new Stack<int>();   // 模板 ${} 插值栈：值 = 该插值内已见的 { 深度
            bool inTemplate = false;
            bool inLine = false, inBlock = false, inRegex = false, inS = false, inD = false;
            int regexClass = 0;
            int strStart = 0;                // 当前字符串的起始下标（对象键要原样保留）
            char lastSig = '\0';
            var lastWord = new StringBuilder();

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char n = i + 1 < text.Length ? text[i + 1] : '\0';

                if (inLine)
                {
                    if (c == '\n') { inLine = false; sb.Append('\n'); }
                    continue;
                }
                if (inBlock)
                {
                    if (c == '*' && n == '/') { inBlock = false; i++; }
                    else if (c == '\n') sb.Append('\n');
                    continue;
                }
                if (inS)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'')
                    {
                        inS = false;
                        if (FollowedByColon(text, i + 1)) sb.Append(text, strStart, i - strStart + 1);
                        else sb.Append(' ');
                        lastSig = '\''; lastWord.Length = 0;
                    }
                    else if (c == '\n') sb.Append('\n');
                    continue;
                }
                if (inD)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"')
                    {
                        inD = false;
                        // 对象字面量的字符串键（"name":）必须保留——JS 方法名常以字符串键形式出现，
                        // 被当普通字符串删掉后 "name": function (…) 就再也匹配不上。
                        if (FollowedByColon(text, i + 1)) sb.Append(text, strStart, i - strStart + 1);
                        else sb.Append(' ');
                        lastSig = '"'; lastWord.Length = 0;
                    }
                    else if (c == '\n') sb.Append('\n');
                    continue;
                }
                if (inRegex)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '[') regexClass++;
                    else if (c == ']') { if (regexClass > 0) regexClass--; }
                    else if (c == '\n') { inRegex = false; sb.Append('\n'); continue; } // 正则不跨行
                    else if (c == '/' && regexClass == 0)
                    {
                        inRegex = false;
                        while (i + 1 < text.Length && char.IsLetter(text[i + 1])) i++; // 吃掉标志
                        lastSig = '/'; lastWord.Length = 0;
                    }
                    continue;
                }
                if (inTemplate)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '`') { inTemplate = false; lastSig = '`'; lastWord.Length = 0; continue; }
                    if (c == '$' && n == '{')
                    {
                        inTemplate = false;
                        interp.Push(0);
                        i++;                                  // 吃掉 {
                        sb.Append(' ').Append(' ');
                        lastSig = '{'; lastWord.Length = 0;
                        continue;
                    }
                    if (c == '\n') sb.Append('\n');
                    continue;                                 // 模板文本：丢弃
                }

                // ---- 代码模式 ----
                if (c == '/' && n == '/') { inLine = true; i++; continue; }
                if (c == '/' && n == '*') { inBlock = true; i++; continue; }
                if (c == '"') { inD = true; strStart = i; continue; }
                if (c == '\'') { inS = true; strStart = i; continue; }
                if (c == '`') { inTemplate = true; lastSig = '`'; lastWord.Length = 0; continue; }
                if (c == '/' && RegexAllowed(lastSig, lastWord.ToString()))
                {
                    inRegex = true; regexClass = 0; continue;
                }

                // 插值收尾：} 且栈顶深度为 0 → 回到模板文本（这个 } 不算代码括号）
                if (c == '}' && interp.Count > 0 && interp.Peek() == 0)
                {
                    interp.Pop();
                    inTemplate = true;
                    sb.Append(' ');
                    lastSig = '`'; lastWord.Length = 0;
                    continue;
                }

                sb.Append(c);

                if (c == '{')
                {
                    if (interp.Count > 0) interp.Push(interp.Pop() + 1);
                }
                else if (c == '}' && interp.Count > 0)
                {
                    interp.Push(interp.Pop() - 1);
                }

                if (!char.IsWhiteSpace(c))
                {
                    lastSig = c;
                    if (char.IsLetterOrDigit(c) || c == '_' || c == '$') lastWord.Append(c);
                    else lastWord.Length = 0;
                }
            }
            return sb.ToString();
        }

        /// <summary>字符串结束后（跳过空白）下一个字符是否是 ':'——是则说明这是对象字面量的键，
        /// 需要原样保留（"name": / 'name':），否则按普通字符串删掉。</summary>
        private static bool FollowedByColon(string text, int from)
        {
            int q = from;
            while (q < text.Length && (text[q] == ' ' || text[q] == '\t' || text[q] == '\r')) q++;
            return q < text.Length && text[q] == ':';
        }

        /// <summary>判断 / 是正则起点还是除号：看上一个有意义的 token。</summary>
        private static bool RegexAllowed(char lastSig, string lastWord)
        {
            if (lastSig == '\0') return true;                  // 行首/文件首
            if (lastSig == ')' || lastSig == ']') return false; // ) ] 后是除号
            if (char.IsLetterOrDigit(lastSig) || lastSig == '_' || lastSig == '$')
                return lastWord.Length > 0 && RegexKeywords.Contains(lastWord);
            switch (lastSig)
            {
                case '(': case ',': case '=': case ':': case '[': case '!': case '&': case '|':
                case '?': case '{': case ';': case '+': case '-': case '*': case '%':
                case '^': case '~': case '<': case '>':
                    return true;
                default:
                    return false;
            }
        }

        #endregion
    }
}
