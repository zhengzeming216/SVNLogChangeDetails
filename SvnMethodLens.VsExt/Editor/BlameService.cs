using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// 提交明细中的一条变更路径（svn log -v 的 path 条目）。
    /// 注意：必须是**属性**——WPF 的 Binding 不认字段，写成字段会让明细列表整列空白。
    /// </summary>
    public sealed class ChangedPath
    {
        public string Action { get; set; } = "";
        public string Path { get; set; } = "";
        /// <summary>"file" / "dir"：svn log -v 的 path kind；目录条目没有可比较的文件内容。</summary>
        public string Kind { get; set; } = "";

        public bool IsDirectory =>
            string.Equals(Kind, "dir", StringComparison.OrdinalIgnoreCase);
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
        private int _unresolvedMethodRevisions;

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

        /// <summary>按文件类型选成员扫描器：JS 系走 JavaScriptScanner，其余走 C# 行级扫描器。
        /// v1.11.0：JS 与 C# 共用同一套 blame/聚合/弹窗链路，只是切成员边界。</summary>
        private static List<EditorMember> ScanMembers(string filePath, string text)
        {
            switch (Path.GetExtension(filePath)?.ToLowerInvariant())
            {
                case ".js":
                case ".jsx":
                case ".mjs":
                case ".cjs":
                    return JavaScriptScanner.Scan(text);
                default:
                    return EditorMethodScanner.Scan(text);
            }
        }

        public async Task<List<MethodBlameView>> ComputeAsync(string filePath, string bufferText, CancellationToken ct)
        {
            // v1.11.0：压缩文件（*.min.js）里的函数名都是单字母，标注没有意义，
            // 且大文件 blame 很慢——直接返回空，连 svn 都不跑。
            if (filePath != null && filePath.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
                return new List<MethodBlameView>();

            // v1.10.4：缓存键必须带上工作副本修订号。
            // 之前只用「路径 + 文本内容」做键，而 svn commit / update / revert 都不改文件内容，
            // 于是提交后文本哈希不变 → 直接命中提交前算出的旧 blame → 标注一直停在
            // "Not committed yet"，明明已经提交却看不到最新记录。
            var rev = GetFileRevision(filePath);
            var key = Sha256(filePath + "|" + rev + "|" + bufferText);
            var cacheKey = filePath + "#" + key;

            if (_results.TryGetValue(cacheKey, out var hit)) return hit;

            // 磁盘缓存命中：直接聚合，不跑 svn
            var blameLines = TryLoadBlameCache(key);
            if (blameLines != null)
            {
                Log($"disk-cache hit {Path.GetFileName(filePath)} lines={blameLines.Count}");
                var members = ScanMembers(filePath, bufferText);
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

                var members = ScanMembers(filePath, bufferText);
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
        /// 方法级完整历史（完整性优先）：
        /// 1) 候选集是**该文件的完整 svn log**，每次打开强制刷新，不截断、不以 blame 集合为准；
        /// 2) 对每个修订取 -c 的增删行，并在**该修订版本**里重新扫描同名方法的行号范围
        ///    （方法会被搬动，拿当前行号去比历史 diff 会误判）；
        /// 3) 只有增删行真正落在方法范围内，才算「改过这个方法」。
        /// 这样即使某次提交后来被覆盖、或方法整体位移，也不会漏记录。
        /// </summary>
        public Task<List<CommitInfo>> GetMethodLogAsync(
            string filePath, string methodName, int startLine, IList<int> knownRevisions)
        {
            return Task.Run(async () =>
            {
                var root = GetWcRoot(filePath);
                var rel = ToRelative(root, filePath);

                // 完整 log 是唯一候选来源；强制刷新，避免新提交被旧缓存挡住。
                var fileLog = LoadFileLogFresh(filePath, root, rel);
                _fileLogCache["file|" + filePath] = fileLog;
                var head = fileLog.Count == 0 ? 0 : fileLog.Max(c => c.Revision);

                var key = filePath + "|" + startLine + "|" + methodName + "|" + head;
                if (_methodLogCache.TryGetValue(key, out var hit)) return hit;

                // 完整历史要跑几百次 svn diff/cat，结果落盘：第二次打开秒出。
                // 缓存 key 带 head revision，文件有新提交时自动失效。
                var disk = TryLoadMethodHistoryCache(key, fileLog);
                if (disk != null)
                {
                    _methodLogCache[key] = disk;
                    return disk;
                }

                var task = _methodLogInflight.GetOrAdd(key, _ =>
                    BuildMethodHistoryAsync(filePath, root, rel, methodName, knownRevisions, fileLog));
                try
                {
                    var r = await task.ConfigureAwait(false);
                    _methodLogCache[key] = r;
                    SaveMethodHistoryCache(key, r);
                    return r;
                }
                finally
                {
                    _methodLogInflight.TryRemove(key, out _);
                }
            });
        }

        private List<CommitInfo> TryLoadMethodHistoryCache(string key, List<CommitInfo> fileLog)
        {
            try
            {
                var path = Path.Combine(_cacheDir, "method-history-" + Sha256(key) + ".txt");
                if (!File.Exists(path)) return null;
                var revs = File.ReadAllLines(path)
                    .Select(l => int.TryParse(l.Trim(), out var r) ? r : 0)
                    .Where(r => r > 0).ToList();
                if (revs.Count == 0) return null;
                var byRev = new Dictionary<int, CommitInfo>();
                foreach (var c in fileLog) byRev[c.Revision] = c;
                return revs.OrderByDescending(r => r).Select(r =>
                    byRev.TryGetValue(r, out var c)
                        ? c
                        : new CommitInfo { Revision = r, Message = "(r" + r + ")" }).ToList();
            }
            catch
            {
                return null;
            }
        }

        private void SaveMethodHistoryCache(string key, List<CommitInfo> result)
        {
            try
            {
                Directory.CreateDirectory(_cacheDir);
                var path = Path.Combine(_cacheDir, "method-history-" + Sha256(key) + ".txt");
                File.WriteAllLines(path, result.Select(c => c.Revision.ToString()));
            }
            catch { }
        }

        private List<CommitInfo> LoadFileLogFresh(string filePath, string root, string rel)
        {
            try
            {
                return ParseLogXml(RunSvn(root, $"log --xml --non-interactive -- \"{rel}\""));
            }
            catch (Exception ex)
            {
                Log($"method history file log refresh failed: {ex.Message}");
                return _fileLogCache.TryGetValue("file|" + filePath, out var old)
                    ? old
                    : new List<CommitInfo>();
            }
        }

        private async Task<List<CommitInfo>> BuildMethodHistoryAsync(
            string filePath, string root, string rel, string methodName,
            IList<int> knownRevisions, List<CommitInfo> fileLog)
        {
            var sw = Stopwatch.StartNew();

            // 完整候选：文件 log ∪ blame 中出现过的修订（后者防止 log 因路径改名漏项）
            var byRev = new Dictionary<int, CommitInfo>();
            foreach (var c in fileLog) byRev[c.Revision] = c;
            foreach (var r in knownRevisions ?? new List<int>())
                if (r > 0 && !byRev.ContainsKey(r))
                    byRev[r] = new CommitInfo { Revision = r, Message = "(r" + r + ")" };
            var candidates = byRev.Values.OrderByDescending(c => c.Revision).ToList();

            var membersByRev = new ConcurrentDictionary<int, List<EditorMember>>();
            var keep = new ConcurrentBag<CommitInfo>();
            var gate = new SemaphoreSlim(8);
            Interlocked.Exchange(ref _unresolvedMethodRevisions, 0);

            var tasks = candidates.Select(async c =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var touches = await Task.Run(() => RevisionTouchesMethod(
                        filePath, root, rel, c.Revision, methodName, membersByRev))
                        .ConfigureAwait(false);
                    if (touches) keep.Add(c);
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);

            var unresolved = Interlocked.Exchange(ref _unresolvedMethodRevisions, 0);
            var result = keep.OrderByDescending(c => c.Revision).ToList();
            Log($"method history {methodName}: kept {result.Count}/{candidates.Count} " +
                $"(full file log={fileLog.Count} entries), unresolved={unresolved}, " +
                $"{sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }

        /// <summary>判断某修订的增删行是否落在「该修订版本里」同名方法的范围内。</summary>
        private bool RevisionTouchesMethod(string filePath, string root, string rel,
            int revision, string methodName, ConcurrentDictionary<int, List<EditorMember>> membersByRev)
        {
            try
            {
                var diff = RunSvn(root,
                    $"diff -c {revision} --internal-diff --non-interactive -- \"{rel}\"");
                var changedLines = SvnClient.ParseUnifiedDiff(diff)
                    .SelectMany(h => h.Changes)
                    .Select(ch => ch.Line)
                    .ToList();
                if (changedLines.Count == 0) return false;

                var members = membersByRev.GetOrAdd(revision, _ =>
                    ScanMembers(filePath, RunSvn(root,
                        $"cat --non-interactive -r {revision} -- \"{rel}\"")));

                var matches = members.Where(m =>
                    string.Equals(m.Name, methodName, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0) return false;

                // 同名重载/同名调用点可能不止一处：任一范围命中即算。
                // 完整性优先，宁可多保留一条，也不让真实改过该方法的提交被漏掉。
                return matches.Any(m => changedLines.Any(l => l >= m.StartLine && l <= m.EndLine));
            }
            catch (Exception ex)
            {
                // 无法判定（权限/二进制/路径改名）时保守保留，绝不静默丢弃。
                Interlocked.Increment(ref _unresolvedMethodRevisions);
                Log($"method history {methodName}: r{revision} unresolved: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// [已弃用，保留作历史参考] 旧版逐代回溯用的辅助方法：
        /// 在 blame --xml（每行修订）与 cat 内容（按行号对齐）中定位方法声明行，
        /// 大括号配平找方法体范围，返回范围内出现过的全部修订。
        /// 现在的完整历史改走 BuildMethodHistoryAsync（完整 log + 逐修订 diff 判定），
        /// 这个方法不再被调用，因为跳跃式采样会漏掉被覆盖的中间提交。
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

                // 定位声明行：跳过注释行，找"方法名(" 的第一次出现。
                // v1.11.0：兼容 JS 声明风格——除 name( 外还认：
                //   "name": / name:          （对象字面量方法，jQuery 主力写法）
                //   name = function / name = (…) => / name = ident =>   （赋值式函数）
                int decl = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = lines[i];
                    var trim = t.TrimStart();
                    if (trim.StartsWith("//") || trim.StartsWith("*") || trim.StartsWith("/*")) continue;
                    int p = t.IndexOf(methodName, StringComparison.Ordinal);
                    if (p < 0) continue;
                    // 标识符边界：前面不能紧跟标识符字符（避免 save 命中 saved/unsaved）
                    if (p > 0 && (char.IsLetterOrDigit(t[p - 1]) || t[p - 1] == '_' || t[p - 1] == '$'))
                        continue;
                    int after = p + methodName.Length;
                    while (after < t.Length && char.IsWhiteSpace(t[after])) after++;
                    if (after >= t.Length) continue;

                    bool hit = t[after] == '(';
                    if (!hit && (t[after] == '"' || t[after] == '\''))
                    {
                        int q = after + 1;
                        while (q < t.Length && char.IsWhiteSpace(t[q])) q++;
                        hit = q < t.Length && t[q] == ':';
                    }
                    if (!hit && t[after] == ':') hit = true;
                    if (!hit && t[after] == '=')
                    {
                        int q = after + 1;
                        while (q < t.Length && char.IsWhiteSpace(t[q])) q++;
                        if (q < t.Length)
                        {
                            var rest = t.Substring(q);
                            hit = rest.StartsWith("function", StringComparison.Ordinal)
                               || rest.StartsWith("(", StringComparison.Ordinal)
                               || Regex.IsMatch(rest, @"^[A-Za-z_$][\w$]*\s*=>");
                        }
                    }
                    if (hit) { decl = i + 1; break; }
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
                                Path = p.Value,
                                Kind = (string)p.Attribute("kind") ?? ""
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

        /// <summary>
        /// 把 svn log -v 里的仓库路径（如 /code/GFMSServices/Transport/TransportServices.cs）
        /// 映射成 svn 能直接操作的目标：优先返回本地工作副本路径；不在本工作副本范围内
        /// （同一次提交可能改了别的项目）则退回仓库 URL。
        /// </summary>
        public string ResolveTarget(string filePath, string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath)) return null;
            try
            {
                var root = GetWcRoot(filePath);
                var rel = ToRelative(root, filePath).Replace('\\', '/');
                var relUrl = GetSvnItem(root, "relative-url", rel);   // ^/code/GFMSServices/Transport/TransportServices.cs
                var wanted = repoPath.TrimStart('/');

                if (!string.IsNullOrEmpty(relUrl) && relUrl.StartsWith("^/", StringComparison.Ordinal))
                {
                    var repoOfFile = relUrl.Substring(2);
                    if (repoOfFile.EndsWith(rel, StringComparison.OrdinalIgnoreCase))
                    {
                        // wc-root 对应的仓库前缀：文件仓库路径去掉它的工作副本相对路径
                        var prefix = repoOfFile.Substring(0, repoOfFile.Length - rel.Length);
                        if (wanted.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var local = Path.Combine(root, wanted.Substring(prefix.Length).Replace('/', '\\'));
                            if (File.Exists(local) || Directory.Exists(local)) return local;
                        }
                    }
                }

                var reposRoot = GetSvnItem(root, "repos-root-url", rel);
                if (!string.IsNullOrEmpty(reposRoot))
                    return reposRoot.TrimEnd('/') + "/" + wanted.TrimStart('/');
            }
            catch (Exception ex)
            {
                Log("resolve target failed: " + ex.Message);
            }
            return null;
        }

        /// <summary>svn info --show-item 的小缓存（工作副本根 + 相对路径 → 值）。</summary>
        private readonly ConcurrentDictionary<string, string> _svnItems = new();

        private string GetSvnItem(string workingDir, string item, string relativePath)
        {
            var key = workingDir + "|" + item + "|" + relativePath;
            if (_svnItems.TryGetValue(key, out var hit)) return hit;
            string value = null;
            try
            {
                value = RunSvn(workingDir,
                    $"info --show-item {item} --non-interactive -- \"{relativePath}\"").Trim();
            }
            catch (Exception ex)
            {
                Log($"svn info --show-item {item} failed: {ex.Message}");
            }
            _svnItems[key] = value;
            return value;
        }

        /// <summary>
        /// 取工作副本里某个文件当前的修订号（"svn info --show-item revision"）。
        /// 提交 / 更新 / 还原都会让它变化，而文件内容可能一字不变——
        /// 所以它必须进入 blame 缓存键，否则提交后仍会命中提交前算出的旧 blame
        /// （表现为明明已提交，标注还写"Not committed yet"）。
        /// 带 TTL 的短缓存：轮询时不至于每次都拉起一个 svn 进程。
        /// </summary>
        public string GetFileRevision(string filePath)
        {
            try
            {
                var root = GetWcRoot(filePath);
                if (string.IsNullOrEmpty(root)) return "";
                var rel = ToRelative(root, filePath);
                if (_revCache.TryGetValue(filePath, out var cached)
                    && (DateTime.UtcNow - cached.Item1).TotalSeconds < 5)
                    return cached.Item2;

                var rev = RunSvn(root, "info --show-item revision --non-interactive -- \"" + rel + "\"")
                            .Trim();
                _revCache[filePath] = (DateTime.UtcNow, rev);
                return rev;
            }
            catch (Exception ex)
            {
                Log("revision probe failed: " + ex.Message);
                return "";
            }
        }

        private readonly ConcurrentDictionary<string, (DateTime, string)> _revCache = new();

        /// <summary>强制让某文件的内存 blame 结果失效（提交、更新、还原后调用）。</summary>
        public void Invalidate(string filePath)
        {
            // 只清这个文件的条目（键形如 "路径#内容哈希"），别波及其他打开的文件
            if (string.IsNullOrEmpty(filePath)) return;
            var prefix = filePath + "#";
            foreach (var k in _results.Keys)
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    _results.TryRemove(k, out _);
            _revCache.TryRemove(filePath, out _);
        }

        /// <summary>
        /// 取某次提交里某个文件的内容。取不到（该修订里文件还不存在/已被删）返回 null，
        /// 由调用方当作"空版本"处理——新增文件就是这种情况。
        /// </summary>
        public Task<string> GetPathContentAsync(string filePath, string repoPath, int revision)
        {
            return Task.Run(() =>
            {
                try
                {
                    var target = ResolveTarget(filePath, repoPath);
                    if (target == null) return null;
                    var root = GetWcRoot(filePath);
                    var svn = new SvnClient(root, _svnExe);
                    return svn.CatTargetAsync(target, revision, CancellationToken.None)
                              .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log($"cat r{revision} {repoPath} failed: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>取某次提交对某个文件的 unified diff 文本（文件不存在等情况下返回 null）。</summary>
        public Task<string> GetPathDiffTextAsync(string filePath, string repoPath, int revision)
        {
            return Task.Run(() =>
            {
                try
                {
                    var target = ResolveTarget(filePath, repoPath);
                    if (target == null) return null;
                    var root = GetWcRoot(filePath);
                    var svn = new SvnClient(root, _svnExe);
                    return svn.DiffTextAsync(target, revision, CancellationToken.None)
                              .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log($"diff -c {revision} {repoPath} failed: {ex.Message}");
                    return null;
                }
            });
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
                // lastAuthor/lastDate 只记录「已提交行」里的最新修订：方法混有本地未提交行时，
                // HasLocal 负责表达未提交状态，作者/时间仍保留上次提交的信息（v1.10.3：
                // 之前这里会把两者盖成本地未提交/Now，导致段1只剩"尚未提交"，
                // 与段2的 "N authors, M changes" 自相矛盾）。
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
