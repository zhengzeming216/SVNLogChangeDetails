using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SvnMethodLens.Core.Analysis;

public enum MemberKind
{
    Method,
    Constructor,
    Property,
    LocalFunction,
    Type
}

/// <summary>源码中的一个可归属单元（方法 / 构造函数 / 属性 / 局部函数 / 类型）。</summary>
public sealed record CodeMember(
    MemberKind Kind,
    string Name,
    string Signature,
    string Container,
    int StartLine,
    int EndLine)
{
    public string FullName => string.IsNullOrEmpty(Container) ? Name : $"{Container}.{Name}";
    public int LineCount => EndLine - StartLine + 1;
}

/// <summary>
/// 只用语法层解析，不编译、不加载引用程序集，因此即使项目缺依赖也能扫。
/// 用于从一份 C# 源码文本里取出每个方法/属性的行区间。
/// </summary>
public static class MethodScanner
{
    public static List<CodeMember> Scan(string absolutePath, string? contents = null)
    {
        var text = contents ?? File.ReadAllText(absolutePath);
        var tree = CSharpSyntaxTree.ParseText(
            SourceText.From(text),
            new CSharpParseOptions(LanguageVersion.Latest));

        var root = tree.GetRoot();
        var members = new List<CodeMember>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax m:
                    members.Add(Make(tree, MemberKind.Method, TypeChain(m),
                        m.Identifier.Text + m.ParameterList.ToString(), m, m.AttributeLists));
                    break;

                case ConstructorDeclarationSyntax c:
                    members.Add(Make(tree, MemberKind.Constructor, TypeChain(c),
                        c.Identifier.Text + c.ParameterList.ToString(), c, c.AttributeLists));
                    break;

                case PropertyDeclarationSyntax p:
                    members.Add(Make(tree, MemberKind.Property, TypeChain(p),
                        p.Identifier.Text, p, p.AttributeLists));
                    break;

                case LocalFunctionStatementSyntax lf:
                    members.Add(Make(tree, MemberKind.LocalFunction, TypeChain(lf),
                        lf.Identifier.Text + lf.ParameterList.ToString(), lf, null));
                    break;

                case TypeDeclarationSyntax t: // class / struct / interface / record
                    members.Add(Make(tree, MemberKind.Type, NamespaceOf(t),
                        t.Identifier.Text, t, t.AttributeLists));
                    break;
            }
        }

        // 同类成员按行号排序，便于后续做“外层包含内层”的判断
        members.Sort((a, b) => a.StartLine != b.StartLine
            ? a.StartLine.CompareTo(b.StartLine)
            : b.EndLine.CompareTo(a.EndLine));

        return members;
    }

    private static CodeMember Make(
        SyntaxTree tree,
        MemberKind kind,
        string container,
        string signature,
        SyntaxNode node,
        SyntaxList<AttributeListSyntax>? attributes)
    {
        // 起点带上紧邻的 attribute，这样 [HttpGet] 也算进方法归属范围
        var start = node.SpanStart;
        if (attributes is { Count: > 0 } al && al[0].SpanStart < start)
            start = al[0].SpanStart;

        var span = tree.GetLineSpan(TextSpan.FromBounds(start, node.Span.End));
        return new CodeMember(
            kind,
            ExtractName(signature),
            signature,
            container,
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1);
    }

    private static string ExtractName(string signature)
    {
        var i = signature.IndexOf('(');
        return i > 0 ? signature.Substring(0, i).Trim() : signature.Trim();
    }

    private static string TypeChain(SyntaxNode node)
    {
        var names = new List<string>();
        SyntaxNode? current = node;

        while (current is not null)
        {
            switch (current)
            {
                case TypeDeclarationSyntax t:
                    names.Insert(0, t.Identifier.Text);
                    break;
                case NamespaceDeclarationSyntax ns:
                    names.Insert(0, ns.Name.ToString());
                    break;
                case FileScopedNamespaceDeclarationSyntax fns:
                    names.Insert(0, fns.Name.ToString());
                    break;
            }
            current = current.Parent;
        }
        return string.Join(".", names);
    }

    private static string NamespaceOf(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is NamespaceDeclarationSyntax ns) return ns.Name.ToString();
            if (current is FileScopedNamespaceDeclarationSyntax fns) return fns.Name.ToString();
        }
        return "";
    }

    /// <summary>判断某一行是否算“代码行”——空白与纯注释不计入归属统计。</summary>
    public static bool IsCodeLine(string lineText)
    {
        var t = lineText.Trim();
        if (t.Length == 0) return false;
        if (t.StartsWith("//")) return false;
        if (t.StartsWith("/*") || t.StartsWith("*") || t.StartsWith("*/")) return false;
        if (t.StartsWith("///")) return false;
        return true;
    }
}
