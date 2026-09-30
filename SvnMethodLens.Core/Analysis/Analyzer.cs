using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SvnMethodLens.Core.Svn;

namespace SvnMethodLens.Core.Analysis;

public sealed record AuthorShare(string Author, int Lines, double Percent);

/// <summary>单个方法的归属结论（供 UI / 装饰层使用）。</summary>
public sealed class MethodBlame
{
    public required CodeMember Member { get; init; }
    public required string FilePath { get; init; }

    /// <summary>该方法内的有效代码行数（不含空行与纯注释行）。</summary>
    public int CodeLines { get; set; }

    public List<AuthorShare> Authors { get; set; } = new();

    /// <summary>持有最多代码行的作者——真正“写了这个方法”的人。</summary>
    public string PrimaryAuthor => Authors.Count > 0 ? Authors[0].Author : "";

    /// <summary>最后一次改动这个方法的人。</summary>
    public string LastAuthor { get; set; } = "";

    public DateTime LastModified { get; set; }
    public int LastRevision { get; set; }

    /// <summary>
    /// blame 能在当前代码上看到的不同修订版数量（下限；被覆盖的历史不计入）。
    /// </summary>
    public int ChangeCount { get; set; }

    public List<int> Revisions { get; set; } = new();

    /// <summary>本地尚未提交、还无法归属的行数。</summary>
    public int LocalLines { get; set; }

    public bool HasLocalChanges => LocalLines > 0;

    public List<TimelineEntry> Timeline { get; set; } = new();
}

public sealed record TimelineEntry(
    int Revision, string Author, DateTime Date, string Message, int Added, int Removed);

/// <summary>
/// 缓存“某个文件在某个修订版里的方法布局”。
/// 同一文件的同一修订版只 svn cat 一次，多个方法共享，避免重复调用。
/// </summary>
public sealed class VersionMemberCache
{
    private readonly ConcurrentDictionary<string, List<CodeMember>?> _items = new();

    public async Task<List<CodeMember>?> GetAsync(
        SvnClient svn, string relativePath, int revision, CancellationToken ct)
    {
        var key = $"{relativePath}@{revision}";
        if (_items.TryGetValue(key, out var cached)) return cached;

        List<CodeMember>? result;
        try
        {
            var text = await svn.CatAsync(relativePath, revision, ct);
            result = MethodScanner.Scan(relativePath, text);
        }
        catch
        {
            result = null;
        }

        _items[key] = result;
        return result;
    }
}

public static class Analyzer
{
    /// <summary>把行级 blame 聚合到方法上。</summary>
    public static List<MethodBlame> AnalyzeFile(
        string relativePath,
        string fileText,
        List<CodeMember> members,
        List<BlameLine> blame,
        IReadOnlyDictionary<string, string>? authorMap = null)
    {
        var fileLines = fileText.Replace("\r\n", "\n").Split('\n');
        var blameByLine = new Dictionary<int, BlameLine>();
        foreach (var b in blame) blameByLine[b.LineNumber] = b;

        var results = new List<MethodBlame>();

        foreach (var m in members)
        {
            var perAuthor = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            var revisions = new HashSet<int>();
            var codeLines = 0;
            var localLines = 0;
            var maxRev = 0;
            var lastAuthor = "";
            var lastDate = DateTime.MinValue;

            for (var line = m.StartLine; line <= m.EndLine; line++)
            {
                var text = line - 1 < fileLines.Length ? fileLines[line - 1] : "";
                if (!MethodScanner.IsCodeLine(text)) continue;

                codeLines++;

                if (!blameByLine.TryGetValue(line, out var b)) continue;

                if (b.IsLocalChange)
                {
                    localLines++;
                    continue;
                }

                var author = Map(authorMap, b.Author);
                perAuthor[author] = perAuthor.TryGetValue(author, out var prev) ? prev + 1 : 1;
                revisions.Add(b.Revision);

                if (b.Revision > maxRev)
                {
                    maxRev = b.Revision;
                    lastAuthor = author;
                    lastDate = b.Date;
                }
            }

            // 没有可归属行的成员（例如空方法体）跳过
            if (codeLines == 0 && localLines == 0) continue;

            var owned = codeLines - localLines;
            var shares = perAuthor
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new AuthorShare(kv.Key, kv.Value,
                    owned > 0 ? Math.Round(100.0 * kv.Value / owned, 1) : 0))
                .ToList();

            if (localLines > 0)
            {
                lastAuthor = "本地未提交";
                lastDate = DateTime.Now;
            }

            results.Add(new MethodBlame
            {
                Member = m,
                FilePath = relativePath,
                CodeLines = codeLines,
                Authors = shares,
                LastAuthor = lastAuthor,
                LastModified = lastDate,
                LastRevision = maxRev,
                ChangeCount = revisions.Count,
                Revisions = revisions.OrderBy(x => x).ToList(),
                LocalLines = localLines
            });
        }

        return results;
    }

    /// <summary>
    /// 深度时间线：逐修订版取 diff，只认真正落在这个方法行区间内的增删行。
    /// 默认按“该修订版里方法当时所在的行号”匹配，因此方法被搬动、或在它之前
    /// 插入过代码都不会误判；precise=false 时按当前行号近似（快但可能串到相邻方法）。
    /// </summary>
    public static async Task<List<TimelineEntry>> BuildTimelineAsync(
        SvnClient svn,
        string relativePath,
        CodeMember member,
        IEnumerable<CommitInfo> commits,
        IReadOnlyDictionary<string, string>? authorMap = null,
        bool precise = true,
        VersionMemberCache? cache = null,
        int concurrency = 4,
        CancellationToken ct = default)
    {
        cache ??= new VersionMemberCache();
        var results = new List<TimelineEntry>();

        // netstandard2.0 没有 Parallel.ForEachAsync，用 Task.WhenAll 等价实现
        var tasks = commits.Select(async c =>
        {
            List<DiffHunk> hunks;
            try
            {
                hunks = await svn.DiffHunksAsync(relativePath, c.Revision, ct);
            }
            catch
            {
                return; // 二进制或权限问题，跳过该修订
            }

            if (hunks.Count == 0) return;

            var start = member.StartLine;
            var end = member.EndLine;

            if (precise)
            {
                var members = await cache.GetAsync(svn, relativePath, c.Revision, ct);
                var hit = members?.FirstOrDefault(m =>
                    m.Kind == member.Kind &&
                    m.Name == member.Name &&
                    m.Signature == member.Signature);

                // 该修订版里还不存在（或已删除）这个方法
                if (hit is null) return;

                start = hit.StartLine;
                end = hit.EndLine;
            }

            var added = 0;
            var removed = 0;

            // 只统计真正增删、且落在该方法区间内的行，避免上下文行误判到相邻方法
            foreach (var h in hunks)
            {
                foreach (var ch in h.Changes)
                {
                    if (ch.Line < start || ch.Line > end) continue;
                    if (ch.IsAdd) added++; else removed++;
                }
            }

            if (added == 0 && removed == 0) return;

            lock (results)
            {
                results.Add(new TimelineEntry(
                    c.Revision,
                    Map(authorMap, c.Author),
                    c.Date,
                    c.Message,
                    added,
                    removed));
            }
        });

        await Task.WhenAll(tasks);
        return results.OrderByDescending(e => e.Revision).ToList();
    }

    /// <summary>读取 SVN 用户名 → 真实姓名的映射文件，每行 “svn用户名 = 真实姓名”。</summary>
    public static Dictionary<string, string> LoadAuthorMap(string path)
    {
        var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var key = line.Substring(0, i).Trim();
            var value = line.Substring(i + 1).Trim();
            if (key.Length > 0 && value.Length > 0) map[key] = value;
        }
        return map;
    }

    private static string Map(IReadOnlyDictionary<string, string>? map, string author)
    {
        if (map is null || string.IsNullOrEmpty(author)) return author;
        return map.TryGetValue(author, out var real) ? real : author;
    }
}
