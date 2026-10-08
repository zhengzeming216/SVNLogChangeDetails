using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SvnMethodLens.Core.Svn;

namespace SvnMethodLens.Editor
{
    /// <summary>单个方法的归属视图模型（供装饰层渲染）。</summary>
    public sealed class MethodBlameView
    {
        public string Name;
        public int StartLine;
        public int EndLine;
        public string Kind;
        public string LastAuthor = "";
        public int ChangeCount;
        public int LastRevision;
        public bool HasLocal;
        public string PrimaryAuthor = "";
        public int PrimaryPct;
        /// <summary>最后一次修改的时间（UTC；本地未提交时为 null）。</summary>
        public DateTime? LastDate;
        /// <summary>该方法涉及的不同作者数量（Git CodeLens 的"N 名作者"。</summary>
        public int AuthorCount;
        /// <summary>该方法涉及的修订号（降序），供点击后的提交列表使用。</summary>
        public List<int> Revisions = new List<int>();
    }

    /// <summary>一次提交的摘要（供点击标注后的弹窗展示）。</summary>
    public sealed class CommitInfo
    {
        public int Revision;
        public string Author = "";
        public DateTime Date;
        public string Message = "";
    }

    /// <summary>提交明细中的一条变更路径（svn log -v 的 path 条目）。</summary>
    public sealed class ChangedPath
    {
        public string Action = "";
        public string Path = "";
    }

    /// <summary>单次提交的明细（含变更文件列表），供「View Commit Details」。</summary>
    public sealed class CommitDetail
    {
        public CommitInfo Commit = new CommitInfo();
        public List<ChangedPath> Paths = new List<ChangedPath>();
    }

    /// <summary>
    /// 装饰层的数据源：解析工作副本根、跑 svn blame、用轻量扫描器切方法、聚合成每方法归属。
    /// 大文件的 svn blame 可能非常慢（几十秒，取决于文件行数 × 修订数），因此：
    /// 1) 结果按「文件路径 + 内容哈希」做内存缓存；
    /// 2) blame 行级数据落盘缓存（%LOCALAPPDATA%\SvnMethodLens\cache），重开文件秒出；
    /// 3) 同一文件同一内容只有一个在途计算，其余调用共享等待。
    /// </summary>
    public sealed class BlameService
    {
        public static readonly BlameService Instance = new BlameService();

        private readonly string _svnExe;
        private readonly string _cacheDir;
        private readonly ConcurrentDictionary<string, string> _wcRoots = new();
        private readonly ConcurrentDictionary<string, List<MethodBlameView>> _results = new();
        private readonly ConcurrentDictionary<string, Task<List<MethodBlameView>>> _inflight = new();
        private readonly ConcurrentDictionary<string, List<CommitInfo>> _logCache = new();
        private readonly ConcurrentDictionary<string, Task<List<CommitInfo>>> _logInflight = new();
        private readonly ConcurrentDictionary<string, List<CommitInfo>> _fileLogCache = new();
        private readonly ConcurrentDictionary<string, Task<List<CommitInfo>>> _fileLogInflight = new();
        private readonly ConcurrentDictionary<string, List<CommitInfo>> _methodLogCache = new();
        private readonly ConcurrentDictionary<string, Task<List<CommitInfo>>> _methodLogInflight = new();

        public BlameService()
        {
            // 优先用本机 TortoiseSVN 自带的 svn，其次 PATH 中的 svn
            var candidate = @"D:\Program Files\TortoiseSVN\bin\svn.exe";
            _svnExe = File.Exists(candidate) ? candidate : "svn";
            _cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SvnMethodLens", "cache");
            try { Directory.CreateDirectory(_cacheDir); } catch { }
        }

        /// <summary>诊断日志：无 GUI 时排查 MEF/装饰层是否真的在跑。</summary>
        public static void Log(string message)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SvnMethodLens");
                Directory.CreateDirectory(dir);
                var p = Path.Combine(dir, "log.txt");
                var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
                var fi = new FileInfo(p);
                if (fi.Exists && fi.Length > 512 * 1024) File.Delete(p); // 防无限增长
                File.AppendAllText(p, line);
            }
            catch { }
        }

        public async Task<List<MethodBlameView>> ComputeAsync(string filePath, string bufferText, CancellationToken ct)
        {
            var key = Sha256(filePath + "|" + bufferText);
            var cacheKey = filePath + "#" + key;

            if (_results.TryGetValue(cacheKey, out var hit)) return hit;

            // 磁盘缓存命中：直接聚合，不跑 svn
            var blameLines = TryLoadBlameCache(key);
            if (blameLines != null)
            {
                Log($"disk-cache hit {Path.GetFileName(filePath)} lines={blameLines.Count}");
                var members = EditorMethodScanner.Scan(bufferText);
                var view = Aggregate(members, blameLines, bufferText);
                _results[cacheKey] = view;
                return view;
            }

            // 在途去重：同一内容只跑一次 svn blame
            var task = _inflight.GetOrAdd(cacheKey, _ =>
                Task.Run(() => ComputeCoreAsync(filePath, bufferText, key)));
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                _inflight.TryRemove(cacheKey, out _);
            }
        }

        private async Task<List<MethodBlameView>> ComputeCoreAsync(string filePath, string bufferText, string key)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var root = GetWcRoot(filePath);
                var rel = ToRelative(root, filePath);

                var svn = new SvnClient(root, _svnExe);
                var blame = await svn.BlameAsync(rel, CancellationToken.None);

                SaveBlameCache(key, blame);

                var members = EditorMethodScanner.Scan(bufferText);
                var view = Aggregate(members, blame, bufferText);
                _results[filePath + "#" + key] = view;
                Log($"blame done {Path.GetFileName(filePath)} in {sw.Elapsed.TotalSeconds:F1}s (lines={blame.Count})");
                if (_results.Count > 64) _results.Clear(); // 简单防膨胀
                return view;
            }
            catch (Exception ex)
            {
                Log($"blame FAILED {Path.GetFileName(filePath)} after {sw.Elapsed.TotalSeconds:F1}s: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 取某方法涉及修订的提交摘要（作者/时间/日志），供点击标注后的弹窗展示。
        /// 按「文件 + 修订列表」缓存，同一方法第二次点击秒开。
        /// </summary>
        public async Task<List<CommitInfo>> GetCommitsAsync(string filePath, IList<int> revisions, int max = 15)
        {
            var revs = (revisions ?? new List<int>()).Take(max).ToList();
            if (revs.Count == 0) return new List<CommitInfo>();

            var key = filePath + "|" + string.Join(",", revs);
            if (_logCache.TryGetValue(key, out var hit)) return hit;

            var task = _logInflight.GetOrAdd(key, _ => Task.Run(() => LoadCommitsAsync(filePath, revs)));
            try
            {
                var r = await task.ConfigureAwait(false);
                _logCache[key] = r;
                return r;
            }
            finally
            {
                _logInflight.TryRemove(key, out _);
            }
        }

        private Task<List<CommitInfo>> LoadCommitsAsync(string filePath, List<int> revs)
        {
            var list = new List<CommitInfo>();
            try
            {
                var root = GetWcRoot(filePath);
                var rel = ToRelative(root, filePath);
                var args = "log --xml --non-interactive -c " +
                           string.Join(",", revs.Select(r => r.ToString()).ToArray()) +
                           " -- \"" + rel + "\"";
                list = ParseLogXml(RunSvn(root, args));
            }
            catch (Exception ex)
            {
                Log($"svn log failed: {ex.Message}");
            }
            return Task.FromResult(list);
        }

        /// <summary>文件级完整提交历史（供「Show all file changes」历史窗口），带缓存。
        /// max&lt;=0 表示不限制条数（拉全量，与 TortoiseSVN 的 log 一致）。</summary>
        public async Task<List<CommitInfo>> GetFileLogAsync(string filePath, int max = 0)
        {
            var key = "file|" + filePath;
            if (_fileLogCache.TryGetValue(key, out var hit)) return hit;
            var task = _fileLogInflight.GetOrAdd(key, _ => Task.Run(() => LoadFileLogAsync(filePath, max)));
            try
            {
                var r = await task.ConfigureAwait(false);
                _fileLogCache[key] = r;
                return r;
            }
            finally
            {
                _fileLogInflight.TryRemove(key, out _);
            }
        }

        private List<CommitInfo> LoadFileLogAsync(string filePath, int max)
        {
            try
            {
                var root = GetWcRoot(filePath);
                var rel = ToRelative(root, filePath);
                // max<=0：不加 --limit，拉取该文件的完整提交历史（之前默认 200 条只到最近几个月）
                var limitArg = max > 0 ? $" --limit {max}" : "";
                return ParseLogXml(RunSvn(root, $"log --xml --non-interactive{limitArg} -- \"{rel}\""));
            }
            catch (Exception ex)
            {
                Log($"svn file log failed: {ex.Message}");
                return new List<CommitInfo>();
            }
        }

        /// <summary>
        /// 方法级完整历史：blame 只给每行"最后一次修改"的修订，早期提交会被后续修改覆盖；
        /// 这里用 svn blame -r 更早版本 + svn cat 沿方法体逐代回溯，收集从创建至今的全部修订。
        /// 结果按「文件 + 起始行 + 方法名」缓存。
        /// </summary>
        public async Task<List<CommitInfo>> GetMethodLogAsync(
            string filePath, string methodName, int startLine, IList<int> knownRevisions)
        {
            var key = filePath + "|" + startLine + "|" + methodName;
            if (_methodLogCache.TryGetValue(key, out var hit)) return hit;
            var task = _methodLogInflight.GetOrAdd(key, _ =>
                Task.Run(() => LoadMethodLogAsync(filePath, methodName, knownRevisions)));
            try
            {
                var r = await task.ConfigureAwait(false);
                _methodLogCache[key] = r;
                return r;
            }
            finally
            {
                _methodLogInflight.TryRemove(key, out _);
            }
        }

        private async Task<List<CommitInfo>> LoadMethodLogAsync(
            string filePath, string methodName, IList<int> knownRevisions)
        {
            var found = new SortedSet<int>();
            try
            {
                var root = GetWcRoot(filePath);
                var rel = ToRelative(root, filePath);
                foreach (var r in knownRevisions ?? new List<int>())
                    if (r > 0) found.Add(r);

                if (!string.IsNullOrEmpty(methodName) && found.Count > 0)
                {
                    var sw = Stopwatch.StartNew();
                    int rev = found.Min, guard = 0;
                    while (rev > 1 && ++guard <= 40 && sw.Elapsed.TotalSeconds < 120)
                    {
                        // blame 给每行修订（无内容），cat 给该版本内容——两者按行号对齐
                        string blameXml, content;
                        try
                        {
                            blameXml = RunSvn(root, $"blame --xml --non-interactive -r {rev - 1} -- \"{rel}\"");
                            content = RunSvn(root, $"cat --non-interactive -r {rev - 1} -- \"{rel}\"");
                        }
                        catch
                        {
                            break; // 版本可能已不存在（文件后来才加入等）
                        }
                        var rangeRevs = MethodRevisionsAtRevision(blameXml, content, methodName);
                        if (rangeRevs.Count == 0) break; // 该版本里已找不到方法 → 到创建提交为止
                        bool added = rangeRevs.Any(r => !found.Contains(r));
                        foreach (var r in rangeRevs) found.Add(r);
                        int min = rangeRevs.Min();
                        if (!added || min >= rev) break; // 没有更早的历史了
                        rev = min;
                    }
                    Log($"method history {methodName}: {found.Count} revisions, walked {guard} epochs, {sw.Elapsed.TotalSeconds:F1}s");
                }
            }
            catch (Exception ex)
            {
                Log($"method log failed: {ex.Message}");
            }

            // 修订号 → 提交摘要（复用全量文件 log）
            try
            {
                var fileLog = await GetFileLogAsync(filePath).ConfigureAwait(false);
                var byRev = new Dictionary<int, CommitInfo>();
                foreach (var c in fileLog) byRev[c.Revision] = c;
                var list = new List<CommitInfo>();
                foreach (var r in found)
                {
                    if (byRev.TryGetValue(r, out var c)) list.Add(c);
                    else list.Add(new CommitInfo { Revision = r, Message = "(r" + r + ")" });
                }
                return list.OrderByDescending(c => c.Revision).ToList();
            }
            catch
            {
                return found.Select(r => new CommitInfo { Revision = r })
                            .OrderByDescending(c => c.Revision).ToList();
            }
        }

        /// <summary>
        /// 在 blame --xml（每行修订）与 cat 内容（按行号对齐）中定位方法声明行，
        /// 大括号配平找方法体范围，返回范围内出现过的全部修订。
        /// </summary>
        private static List<int> MethodRevisionsAtRevision(string blameXml, string content, string methodName)
        {
            var revs = new List<int>();
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(blameXml);
                var commits = doc.Descendants("entry")
                    .Select(e => new
                    {
                        Line = int.TryParse((string)e.Attribute("line-number"), out var n) ? n : 0,
                        Rev = int.TryParse(
                            e.Descendants("commit")
                             .Select(c => (string)c.Attribute("revision"))
                             .FirstOrDefault(),
                            out var r) ? r : 0
                    })
                    .Where(x => x.Line > 0 && x.Rev > 0)
                    .OrderBy(x => x.Line)
                    .ToList();
                var lines = content.Replace("\r\n", "\n").Split('\n');

                // 定位声明行：跳过注释行，找"方法名(" 的第一次出现
                int decl = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = lines[i];
                    var trim = t.TrimStart();
                    if (trim.StartsWith("//") || trim.StartsWith("*") || trim.StartsWith("/*")) continue;
                    int p = t.IndexOf(methodName, StringComparison.Ordinal);
                    if (p < 0) continue;
                    int after = p + methodName.Length;
                    while (after < t.Length && char.IsWhiteSpace(t[after])) after++;
                    if (after < t.Length && t[after] == '(') { decl = i + 1; break; }
                }
                if (decl <= 0) return revs;

                // 大括号配平找方法体结束行
                int end = decl, depth = 0, seenOpen = 0;
                for (int i = decl - 1; i < lines.Length && i < decl + 400; i++)
                {
                    foreach (var ch in lines[i])
                    {
                        if (ch == '{') { depth++; seenOpen++; }
                        else if (ch == '}') depth--;
                    }
                    end = i + 1;
                    if (seenOpen > 0 && depth <= 0) break;
                }

                var inRange = commits.Where(x => x.Line >= decl && x.Line <= end)
                                     .Select(x => x.Rev).Distinct();
                revs.AddRange(inRange);
            }
            catch (Exception ex)
            {
                Log("blame parse failed: " + ex.Message);
            }
            return revs;
        }

        /// <summary>单次提交的变更文件明细（svn log -v），供「View Commit Details」。</summary>
        public async Task<CommitDetail> GetCommitDetailAsync(string filePath, int revision)
        {
            return await Task.Run(() =>
            {
                var d = new CommitDetail();
                try
                {
                    var root = GetWcRoot(filePath);
                    var rel = ToRelative(root, filePath);
                    var xml = RunSvn(root, $"log --xml --non-interactive -v -r {revision} -- \"{rel}\"");
                    var doc = System.Xml.Linq.XDocument.Parse(xml);
                    var e = doc.Descendants("logentry").FirstOrDefault();
                    if (e != null)
                    {
                        d.Commit = ParseOne(e);
                        foreach (var p in e.Descendants("path"))
                            d.Paths.Add(new ChangedPath
                            {
                                Action = (string)p.Attribute("action") ?? "",
                                Path = p.Value
                            });
                    }
                }
                catch (Exception ex)
                {
                    Log($"svn log -v failed: {ex.Message}");
                }
                return d;
            }).ConfigureAwait(false);
        }

        private static List<CommitInfo> ParseLogXml(string xml)
        {
            var list = new List<CommitInfo>();
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            foreach (var e in doc.Descendants("logentry"))
                list.Add(ParseOne(e));
            return list;
        }

        private static CommitInfo ParseOne(System.Xml.Linq.XElement e)
        {
            int rev = 0;
            var revAttr = e.Attribute("revision");
            if (revAttr != null) int.TryParse(revAttr.Value, out rev);
            var authorEl = e.Element("author");
            var dateEl = e.Element("date");
            var msgEl = e.Element("msg");
            var date = DateTime.MinValue;
            if (dateEl != null) DateTime.TryParse(dateEl.Value, out date);
            return new CommitInfo
            {
                Revision = rev,
                Author = authorEl != null ? authorEl.Value : "",
                Date = date,
                Message = msgEl != null ? (msgEl.Value ?? "") : ""
            };
        }

        private string RunSvn(string workingDir, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _svnExe,
                Arguments = args,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var proc = Process.Start(psi))
            {
                var outp = proc.StandardOutput.ReadToEnd();
                var err = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException($"{args} 失败 (exit {proc.ExitCode}): {err.Trim()}");
                return outp;
            }
        }

        #region 磁盘缓存（行级 blame 的 TSV：line \t rev \t author \t dateTicks）

        private string CachePath(string key) => Path.Combine(_cacheDir, key + ".blame");

        private List<BlameLine> TryLoadBlameCache(string key)
        {
            try
            {
                var p = CachePath(key);
                if (!File.Exists(p)) return null;
                var list = new List<BlameLine>();
                foreach (var line in File.ReadAllLines(p))
                {
                    var parts = line.Split('\t');
                    if (parts.Length != 4) continue;
                    if (!int.TryParse(parts[0], out var ln)) continue;
                    if (!int.TryParse(parts[1], out var rev)) continue;
                    long.TryParse(parts[3], out var ticks);
                    list.Add(new BlameLine(ln, rev, parts[2],
                        ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue));
                }
                return list.Count > 0 ? list : null;
            }
            catch { return null; }
        }

        private void SaveBlameCache(string key, List<BlameLine> blame)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var b in blame)
                    sb.Append(b.LineNumber).Append('\t')
                      .Append(b.Revision).Append('\t')
                      .Append((b.Author ?? "").Replace('\t', ' ')).Append('\t')
                      .Append(b.Date == DateTime.MinValue ? "" : b.Date.Ticks.ToString())
                      .AppendLine();
                File.WriteAllText(CachePath(key), sb.ToString());
            }
            catch { }
        }

        private static string Sha256(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        #endregion

        private static List<MethodBlameView> Aggregate(
            List<EditorMember> members, List<BlameLine> blame, string bufferText)
        {
            var fileLines = bufferText.Replace("\r\n", "\n").Split('\n');
            var blameByLine = new Dictionary<int, BlameLine>();
            foreach (var b in blame) blameByLine[b.LineNumber] = b;

            var result = new List<MethodBlameView>();
            foreach (var m in members)
            {
                var perAuthor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var revisions = new HashSet<int>();
                int codeLines = 0, localLines = 0, maxRev = 0;
                string lastAuthor = "";
                DateTime lastDate = DateTime.MinValue;

                for (var line = m.StartLine; line <= m.EndLine; line++)
                {
                    var text = line - 1 < fileLines.Length ? fileLines[line - 1] : "";
                    if (!IsCodeLine(text)) continue;
                    codeLines++;

                    if (!blameByLine.TryGetValue(line, out var b)) continue;

                    if (b.IsLocalChange)
                    {
                        localLines++;
                        continue;
                    }
                    perAuthor.TryGetValue(b.Author, out var prev);
                    perAuthor[b.Author] = prev + 1;
                    revisions.Add(b.Revision);
                    if (b.Revision > maxRev)
                    {
                        maxRev = b.Revision;
                        lastAuthor = b.Author;
                        lastDate = b.Date;
                    }
                }

                if (codeLines == 0 && localLines == 0) continue;

                var owned = codeLines - localLines;
                var top = perAuthor.OrderByDescending(kv => kv.Value).FirstOrDefault();
                if (localLines > 0)
                {
                    lastAuthor = "本地未提交";
                    lastDate = DateTime.Now;
                }

                var revs = new List<int>(revisions);
                revs.Sort();
                revs.Reverse();

                result.Add(new MethodBlameView
                {
                    Name = m.Name,
                    StartLine = m.StartLine,
                    EndLine = m.EndLine,
                    Kind = m.Kind,
                    LastAuthor = lastAuthor,
                    ChangeCount = revisions.Count,
                    LastRevision = maxRev,
                    HasLocal = localLines > 0,
                    PrimaryAuthor = top.Key ?? "",
                    PrimaryPct = owned > 0 && top.Key != null
                        ? (int)Math.Round(100.0 * top.Value / owned) : 0,
                    LastDate = lastDate == DateTime.MinValue ? (DateTime?)null : lastDate,
                    AuthorCount = perAuthor.Count,
                    Revisions = revs
                });
            }
            return result;
        }

        private string GetWcRoot(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath) ?? filePath;
            if (_wcRoots.TryGetValue(dir, out var cached)) return cached;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _svnExe,
                    Arguments = "info --show-item wc-root --non-interactive",
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var proc = Process.Start(psi);
                var root = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode != 0 || root.Length == 0) throw new InvalidOperationException("非 SVN 工作副本");
                _wcRoots[dir] = root;
                return root;
            }
            catch
            {
                _wcRoots[dir] = dir; // 缓存失败结果，避免反复尝试
                throw;
            }
        }

        private static string ToRelative(string root, string filePath)
        {
            // wc-root 来自 svn（正斜杠），filePath 来自 VS（反斜杠），必须统一分隔符再比较
            var normRoot = root.Replace('/', '\\').TrimEnd('\\');
            var normPath = filePath.Replace('/', '\\');
            if (normPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
            {
                var rel = normPath.Substring(normRoot.Length).TrimStart('\\').Replace('\\', '/');
                return string.IsNullOrEmpty(rel) ? Path.GetFileName(filePath) : rel;
            }
            return Path.GetFileName(filePath);
        }

        private static bool IsCodeLine(string lineText)
        {
            var t = lineText.Trim();
            if (t.Length == 0) return false;
            if (t.StartsWith("//")) return false;
            if (t.StartsWith("/*") || t.StartsWith("*") || t.StartsWith("*/")) return false;
            if (t.StartsWith("///")) return false;
            return true;
        }
    }
}
