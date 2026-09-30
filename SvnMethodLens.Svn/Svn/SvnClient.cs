using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SvnMethodLens.Core.Svn;

public sealed record BlameLine(int LineNumber, int Revision, string Author, DateTime Date)
{
    /// <summary>本地未提交的行，SVN 不会给出修订号与作者。</summary>
    public bool IsLocalChange => Revision <= 0 || string.IsNullOrEmpty(Author);
}

public sealed record CommitInfo(int Revision, string Author, DateTime Date, string Message);

/// <summary>一次具体的增删，行号对应“改动后”的版本。</summary>
public sealed record DiffChange(int Line, bool IsAdd);

/// <summary>
/// 一个 diff hunk。只保留真正发生增删的行号，
/// 不用整个 hunk 区间——因为 hunk 还包含上下文行，会误判到相邻方法上。
/// </summary>
public sealed record DiffHunk(int NewStartLine, int NewLineCount, List<DiffChange> Changes)
{
    public int Added => Changes.Count(c => c.IsAdd);
    public int Removed => Changes.Count(c => !c.IsAdd);
}

/// <summary>某个方法在某个修订版里所处的位置。</summary>
public sealed record VersionMember(int StartLine, int EndLine, string Signature);

/// <summary>
/// 对 SVN 工作副本的轻量封装：行级 blame / 提交日志 / 单修订 diff / 单修订内容。
/// 全部走 svn.exe 的 --xml 输出，不依赖任何 SVN 客户端库。
/// </summary>
public sealed class SvnClient
{
    private readonly string _workingCopy;
    private readonly string _svnExe;

    public SvnClient(string workingCopy, string svnExe = "svn")
    {
        _workingCopy = workingCopy.Replace('/', '\\');
        _svnExe = svnExe;
    }

    /// <summary>
    /// 对单个文件做行级归属。刻意不传 -r，这样 blame 输出的行号
    /// 与工作副本当前内容逐行对齐（本地未提交的行也会被列出）。
    /// </summary>
    public async Task<List<BlameLine>> BlameAsync(string relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var xml = await RunAsync($"blame --xml --non-interactive -- \"{relativePath}\"", ct);

        var result = new List<BlameLine>();
        var doc = XDocument.Parse(xml);
        foreach (var entry in doc.Descendants("entry"))
        {
            var lineNoAttr = entry.Attribute("line-number");
            if (lineNoAttr is null) continue;

            var commit = entry.Element("commit");
            var revision = commit?.Attribute("revision") is { } ra
                ? int.TryParse(ra.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rv) ? rv : 0
                : 0;

            var author = commit?.Element("author")?.Value ?? "";
            var dateText = commit?.Element("date")?.Value;
            var date = DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : DateTime.MinValue;

            result.Add(new BlameLine(
                int.Parse(lineNoAttr.Value, CultureInfo.InvariantCulture), revision, author, date));
        }
        return result;
    }

    /// <summary>取某个路径（默认整个工作副本）的提交日志，用于补全时间线。</summary>
    public async Task<List<CommitInfo>> LogAsync(
        string? relativePath = null,
        int? startRev = null,
        int? endRev = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var range = (startRev, endRev) switch
        {
            (null, null) => "1:HEAD",
            (null, { } e) => $"1:{e}",
            ({ } s, null) => $"{s}:HEAD",
            ({ } s, { } e) => $"{s}:{e}"
        };

        var cmd = $"log --xml -r {range}";
        if (limit.HasValue) cmd += $" --limit {limit.Value}";
        if (!string.IsNullOrEmpty(relativePath)) cmd += $" -- \"{relativePath}\"";

        var xml = await RunAsync(cmd, ct);

        var result = new List<CommitInfo>();
        var doc = XDocument.Parse(xml);
        foreach (var e in doc.Descendants("logentry"))
        {
            if (e.Attribute("revision") is not { } ra) continue;
            if (!int.TryParse(ra.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rev)) continue;

            var author = e.Element("author")?.Value ?? "";
            var dateText = e.Element("date")?.Value;
            var date = DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : DateTime.MinValue;
            var msg = e.Element("msg")?.Value ?? "";

            result.Add(new CommitInfo(rev, author, date, msg.Trim()));
        }
        return result;
    }

    /// <summary>取某个文件在某个修订版里的改动位置，用于还原方法的完整变更时间线。</summary>
    public async Task<List<DiffHunk>> DiffHunksAsync(string relativePath, int revision, CancellationToken ct)
    {
        var text = await RunAsync($"diff -c {revision} -- \"{relativePath}\"", ct);
        return ParseUnifiedDiff(text);
    }

    /// <summary>取出某个路径在指定修订版的内容，用于精确定位当时方法在哪一行。</summary>
    public async Task<string> CatAsync(string relativePath, int revision, CancellationToken ct)
        => await RunAsync($"cat -r {revision} -- \"{relativePath}\"", ct);

    /// <summary>
    /// 解析 unified diff。逐行跟踪“改动后”的行号，
    /// 只记录真正增删的行——上下文行不参与方法归属判断。
    /// </summary>
    public static List<DiffHunk> ParseUnifiedDiff(string diff)
    {
        var hunks = new List<DiffHunk>();
        if (string.IsNullOrWhiteSpace(diff)) return hunks;

        var lines = diff.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (!line.StartsWith("@@")) continue;

            // @@ -oldStart,oldCount +newStart,newCount @@
            var plus = line.IndexOf('+');
            var end = line.LastIndexOf("@@", StringComparison.Ordinal);
            if (plus < 0 || end <= plus) continue;

            var newPart = line.Substring(plus + 1, end - plus - 1).Trim();
            var comma = newPart.IndexOf(',');
            int newStart, newCount;
            if (comma > 0)
            {
                int.TryParse(newPart.Substring(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out newStart);
                int.TryParse(newPart.Substring(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out newCount);
            }
            else
            {
                int.TryParse(newPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out newStart);
                newCount = 1;
            }

            var changes = new List<DiffChange>();
            var cursor = newStart;

            for (var j = i + 1; j < lines.Length; j++)
            {
                var l = lines[j].TrimEnd('\r');
                if (l.StartsWith("@@")) break;

                if (l.StartsWith("+++") || l.StartsWith("---") || l.StartsWith("Index:")) continue;

                if (l.StartsWith("+"))
                {
                    changes.Add(new DiffChange(cursor, true));
                    cursor++;
                }
                else if (l.StartsWith("-"))
                {
                    // 删除的行在新版本里没有位置，用紧随其后的位置近似
                    changes.Add(new DiffChange(cursor, false));
                }
                else if (l.StartsWith(" "))
                {
                    cursor++;
                }
                else if (l.Length == 0 || l.StartsWith("\\"))
                {
                    // 空行（偶尔出现在 patch 里）或 "\ No newline at end of file"
                    continue;
                }
                else
                {
                    cursor++;
                }
            }

            hunks.Add(new DiffHunk(newStart, newCount, changes));
        }
        return hunks;
    }

    public async Task<string> GetWorkingCopyRootAsync(CancellationToken ct)
        => (await RunAsync("info --show-item wc-root", ct)).Trim();

    private async Task<string> RunAsync(string arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = _svnExe,
            Arguments = arguments,
            WorkingDirectory = _workingCopy,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 svn：{_svnExe}");

        // netstandard2.0 没有带 CancellationToken 的 ReadToEndAsync / WaitForExitAsync，
        // 用无参重载 + 同步 WaitForExit 即可（blame 通常很快）。
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"svn {arguments} 失败 (exit {proc.ExitCode}): {stderr.Trim()}");

        return stdout;
    }
}
