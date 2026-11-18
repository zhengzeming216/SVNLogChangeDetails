using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace SvnMethodLens.Editor
{
    /// <summary>
    /// 按当前线程 UI 区域语言返回中/英文案：
    /// VS 英文界面显示英文（对齐 Git CodeLens 英文），中文界面显示中文（对齐 Git CodeLens 中文）。
    /// VS 语言切换需要重启才生效，所以静态缓存是安全的。
    /// </summary>
    internal static class Loc
    {
        public static readonly bool Zh = InitZh();

        private static bool InitZh()
        {
            try
            {
                return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
            }
            catch
            {
                return false;
            }
        }

        public static string T(string en, string zh) => Zh ? zh : en;
    }

    /// <summary>监听 C# 文档视图的创建，为每个视图挂上方法级 SVN 归属装饰层。
    /// 渲染策略（对齐 VS CodeLens 的做法）：
    ///  1) ILineTransformSource 在方法声明行上方预留一条空带（与 CodeLens"引用"共享，标注画在引用后面）；
    ///  2) 每次布局变化只把"当前视口内"的方法标注加入装饰层（滚动时自然补画），
    ///     因为 AddAdornment 只对已排版（formatted）的行生效，对未排版行会失败。
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("CSharp")]
    [ContentType("javascript")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class TextViewListener : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("SvnMethodLens")]
        [Order(After = PredefinedAdornmentLayers.Selection)]
        internal AdornmentLayerDefinition LayerDefinition { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            new MethodAdornmentManager(textView, BlameService.Instance);
        }
    }

    /// <summary>
    /// 每视图共享状态：哪些行是方法声明行（0-based）。
    /// 装饰管理器（数据到达后）与行变换源（布局期间）都读写它。
    /// </summary>
    internal sealed class LensState
    {
        private static readonly object Key = new();
        private readonly object _gate = new();
        private HashSet<int> _starts = new();

        public long Version { get; private set; }

        public static LensState GetOrCreate(ITextView view)
        {
            LensState s;
            if (!view.Properties.TryGetProperty(Key, out s) || s == null)
            {
                s = new LensState();
                view.Properties[Key] = s;
            }
            return s;
        }

        public void SetStarts(IEnumerable<int> starts0)
        {
            lock (_gate)
            {
                _starts = new HashSet<int>(starts0);
                Version++;
            }
        }

        public bool IsMethodStart(int line0)
        {
            lock (_gate) return _starts.Contains(line0);
        }

        // v1.10.6：标注数据是否就绪。未就绪前绝不预留空带，
        // 否则 svn blame 计算期间（大文件可能要几十秒）方法行上方会一直留着空带 → 可见的"空隙"。
        // 数据就绪后会 ForceRelayout，行变换重新要空间并由标注填充。
        private bool _hasData;
        public bool HasData
        {
            get { lock (_gate) return _hasData; }
            set { lock (_gate) _hasData = value; }
        }
    }

    /// <summary>
    /// 给方法声明行上方预留一条空带，让标注像 Git CodeLens 一样独占一行，不遮挡代码。
    /// </summary>
    [Export(typeof(ILineTransformSourceProvider))]
    [Name("SvnMethodLensLineTransform")]
    [ContentType("CSharp")]
    [ContentType("javascript")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class LensLineTransformSourceProvider : ILineTransformSourceProvider
    {
        public ILineTransformSource Create(IWpfTextView textView)
            => new LensLineTransformSource(LensState.GetOrCreate(textView), textView);
    }

    internal sealed class LensLineTransformSource : ILineTransformSource
    {
        private readonly LensState _state;
        private readonly IWpfTextView _view;

        public LensLineTransformSource(LensState state, IWpfTextView view)
        {
            _state = state;
            _view = view;
        }

        // 标注与 VS 自带"N 个引用"（CodeLens）共享同一条带：行变换按 Max 合并，
        // 我们只需保证带高不小于自身高度；CodeLens 在时两者同行显示（标注在引用后面）。
        private double TotalTopSpace => Math.Max(15.0, _view.LineHeight * 0.85);

        public LineTransform GetLineTransform(ITextViewLine line, double suggestedTopChange, ViewRelativePosition affinity)
        {
            try
            {
                int line0 = line.Start.GetContainingLine().LineNumber;
                // v1.10.6：只有"数据已就绪"的方法行才预留空带。
                // 数据没来时返回恒等变换，方法行上方不留空隙（避免 1.10.4 那种"标注没了但空带还在"的间隙）。
                // 且只对该缓冲行的"第一个视觉行"预留：开启自动换行后一行签名会被拆成多个视觉行，
                // 若每个片段都预留，换行的两段之间就会出现一条空白（用户看到的"签名中间的空隙"）。
                var containing = line.Start.GetContainingLine();
                if (_state.IsMethodStart(containing.LineNumber)
                    && _state.HasData
                    && line.Start == containing.Start)
                    return new LineTransform(TotalTopSpace, 0.0, 1.0);
            }
            catch
            {
                // 行未就绪等极端情况：返回恒等变换
            }
            return new LineTransform(0.0, 0.0, 1.0);
        }
    }

    /// <summary>
    /// 提交明细的文档页窗格（v1.11.3）：WindowPane 自带 IVsWindowPane 实现与 WPF Content 宿主，
    /// 由 ShowCommitPage 通过 IVsUIShell.CreateDocumentWindow 开成编辑区标签页
    /// ——与 Git CodeLens 的提交详情页/差异页一样占满整个编辑区，而不是漂浮的小窗口。
    /// </summary>
    internal sealed class CommitDetailsPane : Microsoft.VisualStudio.Shell.WindowPane
    {
        public CommitDetailsPane(UIElement content)
        {
            Content = content;
        }
    }

    internal sealed class MethodAdornmentManager : IDisposable
    {
        private readonly IWpfTextView _view;
        private readonly BlameService _blame;
        private readonly IAdornmentLayer _layer;
        private readonly LensState _state;
        private readonly string _filePath;
        private readonly object _gate = new();
        private List<MethodBlameView> _data = new();
        private long _lastRelayoutVersion;
        private string _queuedText;
        private bool _pumping;
        private bool _disposed;
        private int _redrawTick;
        private double _lastViewportTop = double.NaN;
        // v1.11.0：JS 视图没有 CodeLens 引用，标注直接按预留宽度绘制，不等引用
        private bool _isJs;
        private readonly System.Windows.Controls.Primitives.Popup _popup =
            new System.Windows.Controls.Primitives.Popup();

        // 点击瞬间捕获的标签屏幕坐标；Popup 据此固定在稳定锚点上，不再随装饰重绘而漂走
        private Point _popupAnchorScreen = new Point(double.NaN, double.NaN);

        // v1.10.4：工作副本修订号轮询。
        // svn commit / update / revert 都不改文件内容，编辑器文本快照不变 → OnLayoutChanged
        // 不会触发 ScheduleRefresh，标注会一直停留在提交前的旧数据（"Not committed yet"）。
        // 这里定时比对修订号，变了就强制重算一次。
        private System.Threading.Timer _revTimer;
        private string _lastRev = " ";

        private void StartRevisionWatch()
        {
            if (_filePath == null) return;
            _lastRev = _blame.GetFileRevision(_filePath) ?? "";
            _revTimer = new System.Threading.Timer(_ => CheckRevisionChanged(),
                null, 4000, 4000);
        }

        private void CheckRevisionChanged()
        {
            try
            {
                if (_disposed || _filePath == null) return;
                var rev = _blame.GetFileRevision(_filePath);
                if (string.IsNullOrEmpty(rev)) return;
                if (rev == _lastRev) return;

                _lastRev = rev;
                BlameService.Log($"wc revision changed -> {rev}, recompute blame");

                // 清掉该文件的旧结果，让 ComputeAsync 重新跑 svn blame；
                // 再排一次刷新（走 UI 线程，_queuedText 会取当前快照）。
                _blame.Invalidate(_filePath);
                _view.VisualElement.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (_disposed) return;
                        ScheduleRefresh();
                    }));
            }
            catch (Exception ex)
            {
                BlameService.Log("revision watch error: " + ex.Message);
            }
        }

        public MethodAdornmentManager(IWpfTextView view, BlameService blame)
        {
            _view = view;
            _blame = blame;
            _layer = view.GetAdornmentLayer("SvnMethodLens");
            _state = LensState.GetOrCreate(view);

            // v1.11.1：JS 视图同样有 CodeLens "N references"（VS 对 JS/TS 也提供引用指示器，
            // 之前的"JS 没有引用"假设是错的——直接画左侧会被引用文字压住）。
            // 标注与引用对齐；引用渲染时机不稳，用"先画左侧 + 轮询等引用"策略（见 RedrawVisible）。
            // 注意：ContentType 在 ITextBuffer 上，IWpfTextView 没有这个属性。
            try { _isJs = _view.TextBuffer.ContentType.IsOfType("javascript"); } catch { _isJs = false; }

            if (_view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                _filePath = doc.FilePath;

            BlameService.Log($"view created: {_filePath ?? "(null)"}");

            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnClosed;
            // 在编辑器任意处按下鼠标时关闭弹框（Git CodeLens 行为）
            _view.VisualElement.PreviewMouseDown += (s, e) => ClosePopup();

            ScheduleRefresh();
            StartRevisionWatch();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            // 文本变化才重算 blame；每次布局都补画视口内的标注（滚动靠这个续画）
            if (e.NewSnapshot != e.OldSnapshot)
                ScheduleRefresh();
            // 滚动时关闭详情弹框，避免它飘在已经滚走的代码上
            if (_popup.IsOpen && !double.IsNaN(_lastViewportTop) &&
                Math.Abs(_view.ViewportTop - _lastViewportTop) > 0.5)
            {
                _popup.IsOpen = false;
            }
            _lastViewportTop = _view.ViewportTop;
            RedrawVisible();
        }

        private void OnClosed(object sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
            try
            {
                if (_hostWindow != null) _hostWindow.Deactivated -= OnHostWindowDeactivated;
            }
            catch { }
            try { _refWaitTimer?.Stop(); } catch { }
            try { _popup.IsOpen = false; _popup.Child = null; } catch { }
            try { _revTimer?.Dispose(); } catch { }
            lock (_gate) _disposed = true;
        }

        private void ScheduleRefresh()
        {
            if (_filePath == null) return;
            var snapshotText = _view.TextSnapshot.GetText();
            lock (_gate) _queuedText = snapshotText; // 永远只保留最新文本

            // 轻微延迟合并初始打开时的连续 LayoutChanged；不取消在途计算
            Task.Run(async () =>
            {
                try { await Task.Delay(300); }
                catch { }
                Pump();
            });
        }

        /// <summary>
        /// 排水模式：只要还有排队的文本就一直算，算完画。
        /// 大文件 svn blame 可能要几十秒，期间绝不重启计算（否则永远画不出来），
        /// 新文本只会在本轮算完后再跑一次。
        /// </summary>
        private void Pump()
        {
            lock (_gate)
            {
                if (_pumping || _disposed) return;
                _pumping = true;
            }

            Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        string text;
                        lock (_gate)
                        {
                            if (_disposed) return;
                            text = _queuedText;
                            _queuedText = null;
                        }
                        if (text == null) break;

                        try
                        {
                            var result = await _blame.ComputeAsync(_filePath, text, CancellationToken.None);

                            var starts = new List<int>(result.Count);
                            foreach (var m in result)
                            {
                                int idx = m.StartLine - 1;
                                if (idx >= 0) starts.Add(idx);
                            }
                            long version;
                            lock (_gate)
                            {
                                _data = result;
                                _state.HasData = true;
                                _state.SetStarts(starts);
                                version = _state.Version;
                            }

                            BlameService.Log($"data ready: {_filePath} methods={result.Count} v={version}");
                            await _view.VisualElement.Dispatcher.InvokeAsync(() =>
                            {
                                RedrawVisible();
                                if (version != Interlocked.Read(ref _lastRelayoutVersion))
                                {
                                    Interlocked.Exchange(ref _lastRelayoutVersion, version);
                                    ForceRelayout(); // 让行变换源重新要空间
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            BlameService.Log($"compute error: {_filePath}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    lock (_gate) _pumping = false;
                }

                // 排水期间又有新文本进来：再排一次
                bool more;
                lock (_gate) more = !_disposed && _queuedText != null;
                if (more) Pump();
            });
        }

        /// <summary>
        /// 只把"当前视口附近"的方法标注加入装饰层。
        /// AddAdornment(TextRelative) 只对已排版的行生效；视口外的行由下一次滚动布局补画。
        /// </summary>
        private void RedrawVisible()
        {
            try
            {
                List<MethodBlameView> data;
                lock (_gate) data = _data;
                if (data.Count == 0) return;

                ITextViewLineCollection lines;
                try { lines = _view.TextViewLines; }
                catch { return; }
                if (lines == null || lines.Count == 0) return;

                // v1.10.5：取首/尾可见行会抛 ObjectDisposedException（视图正在重建/格式化行已失效）。
                // 这段必须放在 RemoveAllAdornments 之前——否则异常发生时标注已被清空，
                // 而这轮又直接 return，用户看到的就是"标注全没了"，直到下一次布局变化才可能补上。
                int first, last;
                try
                {
                    first = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
                    last = lines.LastVisibleLine.Start.GetContainingLine().LineNumber;
                }
                catch (Exception ex)
                {
                    BlameService.Log("viewport lines unavailable, will retry: " + ex.GetType().Name);
                    ScheduleRedrawRetry();
                    return;
                }

                _layer.RemoveAllAdornments();

                int lo = first - 2, hi = last + 2;

                var snapshot = _view.TextSnapshot;
                // 要求5：找到视口内 CodeLens "N references" 的位置，把标注画到引用后面（同一行）。
                // JS 视图同样有引用指示器（v1.11.1 修正了"JS 没有引用"的错误假设）。
                var refEdges = CollectReferenceEdges();
                int added = 0, failed = 0, skipped = 0, waitingRefs = 0;
                string firstFail = null;

                foreach (var m in data)
                {
                    int idx = m.StartLine - 1;
                    if (idx < lo || idx > hi)
                    {
                        skipped++;
                        continue;
                    }
                    if (idx < 0 || idx >= snapshot.LineCount) continue;

                    var snapshotLine = snapshot.GetLineFromLineNumber(idx);
                    var lineStart = new SnapshotPoint(snapshot, snapshotLine.Start);
                    var span = new SnapshotSpan(snapshot, lineStart, 0);

                    // 关键：TextRelative 装饰必须自己设置 Canvas 坐标后再 AddAdornment，
                    // 编辑器只负责后续布局时的平移跟随；不设坐标会全部堆到 (0,0)。
                    var viewLine = lines.GetTextViewLineContainingBufferPosition(lineStart);
                    if (viewLine == null) { skipped++; continue; }

                    // v1.9.0：折叠（outlining）区域内的成员不画标注。
                    // 被折叠的行不可见，其 buffer 位置会被映射到折叠区首行（如 #region 行），
                    // 若按 m.StartLine 继续画，标注就会堆叠在折叠行上（用户看到的"方法收起来还显示 log"）。
                    // 判断方法：取回的行是否真的以该成员声明的行开头，不是就说明它被折叠遮住了。
                    try
                    {
                        if (viewLine.Start.GetContainingLine().LineNumber != idx) { skipped++; continue; }
                    }
                    catch { skipped++; continue; }

                    // 横向：优先紧贴实际检测到的 CodeLens 引用（"N references / N 个引用"），
                    // 与 Git CodeLens 的排布一致，避免固定预留宽度造成的割裂感；
                    // 引用元素没找到时（CodeLens 关闭/尚未渲染）才退回 "99+ references/个引用" 的预留宽度
                    double labelX, labelY;
                    RefEdge best = null;
                    foreach (var r in refEdges)
                    {
                        if (r.Top >= viewLine.Top - 6 && r.Top <= viewLine.Top + 28 &&
                            (best == null || r.Right > best.Right))
                            best = r;
                    }
                    if (best != null)
                    {
                        labelX = best.Right + 6;
                        // v1.8.1：纵向与引用文字严格对齐——用引用元素的顶边作为标注顶边
                        // （原来固定 viewLine.Top + 1.0，会比引用低几个像素，看起来高度不一致）
                        labelY = best.Top;
                        // 与 CodeLens 引用完全同字体/字号/颜色/行高：直接抄引用文本元素的规格
                        if (best.Family != null && best.FontSize > 0)
                            _lensFont = new LensFont
                            {
                                Family = best.Family,
                                Size = best.FontSize,
                                LineHeight = best.LineHeight,
                                Stacking = best.Stacking,
                                Brush = best.Foreground
                            };
                    }
                    else
                    {
                        // 要求6：log 与引用一起出现。CodeLens 引用还没渲染时先不画标注，
                        // 安排 250ms 后重试（期间不显示 log），等引用出现的同一时刻再画；
                        // 约 5 秒仍没有引用（CodeLens 被关闭等）才退回预留宽度位置。
                        if (_isJs)
                        {
                            // v1.11.1：JS 的引用指示器渲染时机不稳（有的文件有、有的迟迟不出现，
                            // 甚至始终不出现）。策略：立即在左侧画出标注（先给用户看到，也不与随后
                            // 出现的引用抢位），同时继续轮询——前 6 次快试（1.5s），之后 1s 慢轮询；
                            // 引用一旦出现，下一轮重画就把标注改到引用后面。轮询在引用就位或视图关闭时停。
                            labelX = IndentLeft(viewLine, snapshotLine, lineStart) + 8.0;
                            labelY = viewLine.Top + 1.0;
                            waitingRefs++;
                            ScheduleRefWait(_refWaitAttempts >= JsRefWaitMaxAttempts);
                        }
                        else if (_refWaitAttempts < RefWaitMaxAttempts)
                        {
                            waitingRefs++;
                            ScheduleRefWait();
                            continue;
                        }
                        else
                        {
                            labelX = IndentLeft(viewLine, snapshotLine, lineStart) + ReferenceReserveWidth();
                            labelY = viewLine.Top + 1.0;
                        }
                    }

                    var label = CreateLabel(m, _lensFont);
                    Canvas.SetLeft(label, labelX);
                    Canvas.SetTop(label, labelY);

                    try
                    {
                        if (_layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, label, null))
                            added++;
                        else
                        {
                            failed++;
                            firstFail = firstFail ?? "AddAdornment returned false";
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        firstFail = firstFail ?? ex.Message;
                    }
                }

                // 引用已全部就位：停止等待重试并重置预算，下次（滚动到新区域）重新等满
                if (waitingRefs == 0 && _refWaitTimer != null && _refWaitTimer.IsEnabled)
                {
                    _refWaitTimer.Stop();
                    _refWaitAttempts = 0;
                }

                // 日志降噪：只在异常或每 25 次重画时输出一行
                _redrawTick++;
                if (failed > 0 || _redrawTick % 25 == 1)
                    BlameService.Log($"visible redraw: view={System.IO.Path.GetFileName(_filePath)} " +
                                     $"viewport=[{first},{last}] added={added} failed={failed} skipped={skipped} " +
                                     $"waitingRefs={waitingRefs} attempt={_refWaitAttempts}" +
                                     (firstFail != null ? " fail=\"" + firstFail + "\"" : ""));
            }
            catch (Exception ex)
            {
                BlameService.Log("redraw EXCEPTION: " + ex);
                // v1.10.5：整轮异常同样安排重试，避免"清空了却没补画"的空窗一直留着
                ScheduleRedrawRetry();
            }
        }

        private int _redrawRetryPending;

        /// <summary>
        /// 视图行还没准备好时稍后重试补画（v1.10.5）。
        /// 典型场景：blame 数据刚算完，视图正在重建格式化行，此时取FirstVisibleLine
        /// 会抛 ObjectDisposedException。若不重试，这一轮标注就永远是空的。
        /// </summary>
        private void ScheduleRedrawRetry()
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _redrawRetryPending, 1) == 1) return; // 已有一次在排队

            _view.VisualElement.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_disposed) { Interlocked.Exchange(ref _redrawRetryPending, 0); return; }
                    try
                    {
                        // 让 UI 先把待处理的格式化/布局跑完，再画
                        _view.VisualElement.Dispatcher.Invoke(
                            () => { }, DispatcherPriority.ApplicationIdle);
                        RedrawVisible();
                    }
                    catch (Exception ex)
                    {
                        BlameService.Log("redraw retry failed: " + ex.GetType().Name);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _redrawRetryPending, 0);
                    }
                }), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// 数据到位后强制一次重新布局，让 ILineTransformSource 对新方法行补要空间。
        /// 用"重新显示当前第一行"的方式，不产生滚动跳变。
        /// </summary>
        private void ForceRelayout()
        {
            try
            {
                var lines = _view.TextViewLines;
                if (lines == null || lines.Count == 0) return;
                var line = lines.FirstVisibleLine;
                double dist = Math.Max(0, line.Top - _view.ViewportTop);
                _view.DisplayTextLineContainingBufferPosition(line.Start, dist, ViewRelativePosition.Top);
            }
            catch
            {
                // 视图尚未布局完成等情况：忽略，滚动/编辑后会自然重排
            }
        }

        /// <summary>标注的横向位置：对齐到代码缩进（方法名所在的列）。</summary>
        private static double IndentLeft(ITextViewLine viewLine, ITextSnapshotLine snapshotLine, SnapshotPoint lineStart)
        {
            var text = snapshotLine.GetText();
            int i = 0;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            if (i >= text.Length) return viewLine.TextLeft;
            try
            {
                return viewLine.GetCharacterBounds(lineStart + i).Left;
            }
            catch
            {
                return viewLine.TextLeft;
            }
        }

        /// <summary>CodeLens "N references" 在文本坐标里的位置（用于把标注排到引用后面）。</summary>
        private sealed class RefEdge
        {
            public double Top;
            public double Right;
            public FontFamily Family;   // 引用文本的字体（抄给标注用，保证外观一致）
            public double FontSize;
            public double LineHeight;   // 引用文本的行高（NaN = 自动），抄过来保证行框高度一致
            public LineStackingStrategy Stacking;
            public Brush Foreground;
        }

        /// <summary>从 CodeLens 引用文本元素抄来的字体规格，让标注与引用完全同字体同字号同颜色同行高。</summary>
        private sealed class LensFont
        {
            public FontFamily Family;
            public double Size;
            public double LineHeight;   // NaN = 自动
            public LineStackingStrategy Stacking;
            public Brush Brush;
        }

        private LensFont _lensFont;              // 最近一次成功抄到的 CodeLens 字体（跨重画缓存）
        private DispatcherTimer _refWaitTimer;   // 等待 CodeLens 引用渲染的重试计时器
        private int _refWaitAttempts;            // 已重试次数
        private const int RefWaitMaxAttempts = 20; // 20 × 250ms ≈ 5 秒后放弃等待，退回预留宽度
        private const int JsRefWaitMaxAttempts = 6;  // v1.11.1：JS 前 6 次快试（1.5s），之后转 1s 慢轮询

        /// <summary>等待 CodeLens 引用渲染的短重试：让标注与引用同一时刻出现，而不是先 log 后引用。</summary>
        private void ScheduleRefWait(bool slow = false)
        {
            if (_refWaitTimer == null)
            {
                _refWaitTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _refWaitTimer.Tick += (s, e) =>
                {
                    _refWaitTimer.Stop();
                    _refWaitAttempts++;
                    RedrawVisible();
                };
            }
            // v1.11.1：JS 快试次数用完后转入 1s 慢轮询，等迟迟不渲染的引用出现后重新对齐
            _refWaitTimer.Interval = slow ? TimeSpan.FromMilliseconds(1000)
                                          : TimeSpan.FromMilliseconds(250);
            if (!_refWaitTimer.IsEnabled) _refWaitTimer.Start();
        }

        /// <summary>遍历视觉树，收集视口内所有"N references"文本的右边界（文本坐标系）。</summary>
        private List<RefEdge> CollectReferenceEdges()
        {
            var list = new List<RefEdge>();
            try { Collect(_view.VisualElement, list); } catch { }
            return list;
        }

        private void Collect(DependencyObject d, List<RefEdge> list)
        {
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                if (child is TextBlock tb &&
                    !string.IsNullOrEmpty(tb.Text) &&
                    (tb.Text.IndexOf("reference", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     tb.Text.Contains("引用")) &&
                    tb.ActualWidth > 0)
                {
                    try
                    {
                        var tl = tb.TransformToVisual(_view.VisualElement).Transform(new Point(0, 0));
                        // 视口坐标 → 文本坐标（与 ITextViewLine.Top / Canvas 坐标同一坐标系）
                        list.Add(new RefEdge
                        {
                            Top = tl.Y + _view.ViewportTop,
                            Right = tl.X + tb.ActualWidth + _view.ViewportLeft,
                            Family = tb.FontFamily,
                            FontSize = tb.FontSize,
                            LineHeight = tb.LineHeight,
                            Stacking = tb.LineStackingStrategy,
                            Foreground = tb.Foreground
                        });
                    }
                    catch { }
                }
                Collect(child, list);
            }
        }

        private UIElement CreateLabel(MethodBlameView m, LensFont font)
        {
            // Git CodeLens 结构：两段式标签，各自可点击
            //   段1「Baker, 42 days ago」（时间）→ Team Activity 时间线；
            //   段2「2 authors, 2 changes」→ 提交表格（log）
            // 首部固定加 " | "：与左侧 CodeLens 引用区分隔
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Tag = "SvnMethodLensLabel" };
            panel.Children.Add(MakeSeparator(" | ", font));
            panel.Children.Add(MakeSegment(m, true, FormatHead(m), font));
            panel.Children.Add(MakeSeparator(" | ", font));
            panel.Children.Add(MakeSegment(m, false, FormatTail(m), font));
            return panel;
        }

        private static double? _refReserveWidth;

        /// <summary>
        /// CodeLens 引用区的预留宽度：按最坏情况 "99+ references / 99+ 个引用" 的文字宽度设计。
        /// 之前的做法是在视觉树里找引用文本的右边界，但中文 VS 显示的是「N 个引用」，
        /// 只匹配英文 "reference" 会找不到 → 标注回退到缩进位置，直接压在引用上。
        /// </summary>
        private static double ReferenceReserveWidth()
        {
            if (!_refReserveWidth.HasValue)
            {
                double w = 0;
                foreach (var s in new[] { "99+ references", "99+ 个引用" })
                    w = Math.Max(w, MeasureTextWidth(s, 11));
                _refReserveWidth = w + 12; // 文字宽 + 间距
            }
            return _refReserveWidth.Value;
        }

        private static double MeasureTextWidth(string text, double fontSize)
        {
            try
            {
                var ft = new FormattedText(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    fontSize,
                    Brushes.Black,
                    1.0);
                return ft.Width;
            }
            catch
            {
                return text.Length * fontSize * 0.6;
            }
        }

        private TextBlock MakeSeparator(string text, LensFont font)
        {
            var sep = new TextBlock { Text = text };
            ApplyLensFont(sep, font);
            return sep;
        }

        /// <summary>当前弹框对应的段（CodeLens 选中态：保持浅灰高亮框 + 蓝色文字）。</summary>
        private TextBlock _activeSegment;

        private Border MakeSegment(MethodBlameView m, bool activity, string text, LensFont font)
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                Cursor = Cursors.Hand,
                Text = text
            };
            ApplyLensFont(tb, font);
            tb.Tag = tb.Foreground; // 记住原始颜色（CodeLens 灰），悬停/选中结束后恢复

            // v1.8.0：对齐 Git CodeLens 引用的悬停/选中样式——
            //   悬停 → 文字变蓝 + 浅灰圆角高亮框（不再是下划线）；
            //   弹框打开期间（选中态）→ 保持同样高亮，收起后恢复灰色。
            // TextBlock 放进圆角 Border 充当高亮框；返回 Border 加入面板
            // （注意：WPF 一个元素只能有一个逻辑父级，不能把 tb 再加进 StackPanel）。
            var host = new Border
            {
                Child = tb,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 0)
            };
            host.Tag = "SvnMethodLensSegment";
            host.MouseEnter += (s, e) => SetSegmentVisual(tb, true);
            host.MouseLeave += (s, e) => SetSegmentVisual(tb, false);
            tb.MouseLeftButtonUp += (s, e) => { OpenPopup(m, tb, activity); e.Handled = true; };
            return host;
        }

        /// <summary>悬停/选中态的浅灰高亮框颜色（半透明灰，浅色/深色主题都自然）。</summary>
        private static readonly Brush SegmentHighlightBrush =
            new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));

        /// <summary>点击选中后的文字颜色（黑色）。</summary>
        private static readonly Brush SegmentActiveBrush = Brushes.Black;

        /// <summary>按悬停/选中状态刷新一个段的外观：悬停只加背景；点击选中后文字变黑。</summary>
        private void SetSegmentVisual(TextBlock tb, bool hovered)
        {
            bool active = ReferenceEquals(tb, _activeSegment) && _popup.IsOpen;
            if (tb.Parent is Border host)
                host.Background = (hovered || active) ? SegmentHighlightBrush : Brushes.Transparent;
            tb.Foreground = active ? SegmentActiveBrush : (tb.Tag as Brush ?? GrayBrush());
        }

        /// <summary>弹框切换到某段时更新选中态；弹框收起时传 null 复原。</summary>
        private void SetActiveSegment(TextBlock tb)
        {
            var old = _activeSegment;
            _activeSegment = tb;
            if (old != null) { try { SetSegmentVisual(old, false); } catch { } }
            if (tb != null) { try { SetSegmentVisual(tb, false); } catch { } }
        }

        private static Brush _grayBrush;

        /// <summary>
        /// 让标注与 CodeLens 引用完全同字体/字号/颜色：优先用从引用文本元素抄来的规格。
        /// 之前的两个毛病：
        ///  1) 不显式设 FontFamily → 装饰元素继承编辑器的等宽字体（Consolas），行高与引用文字（UI 字体）对不上；
        ///  2) Foreground 依赖 VsBrushes.GrayTextKey 资源解析，在装饰层里解析不到时落到默认黑色，比引用深。
        /// 抄不到 CodeLens 字体（引用尚未渲染/被关闭）时退回 Segoe UI 11px + 灰色。
        /// </summary>
        private void ApplyLensFont(TextBlock tb, LensFont font)
        {
            var f = font ?? _lensFont;
            if (f != null && f.Family != null && f.Size > 0)
            {
                tb.FontFamily = f.Family;
                tb.FontSize = f.Size;
                // v1.8.1：连行高策略一起抄，否则行框高度和引用不一致（表现为两者上下错位）
                if (!double.IsNaN(f.LineHeight) && f.LineHeight > 0)
                {
                    tb.LineHeight = f.LineHeight;
                    tb.LineStackingStrategy = f.Stacking;
                }
                if (f.Brush != null) tb.Foreground = f.Brush;
                return;
            }
            tb.FontFamily = new FontFamily("Segoe UI");
            tb.FontSize = 11;
            tb.Foreground = GrayBrush();
        }

        private static Brush GrayBrush()
        {
            if (_grayBrush == null)
            {
                Brush b = null;
                try
                {
                    b = System.Windows.Application.Current?.TryFindResource(
                            Microsoft.VisualStudio.Shell.VsBrushes.GrayTextKey) as Brush;
                }
                catch { }
                if (b == null) b = new SolidColorBrush(Color.FromRgb(128, 128, 128));
                _grayBrush = b;
            }
            return _grayBrush;
        }

        /// <summary>Git CodeLens 风格文案（段1：作者, 多久之前）。中文用全角逗号，与 Git 中文版一致。</summary>
        private static string FormatHead(MethodBlameView m)
        {
            if (m.HasLocal)
            {
                // 方法里混有本地未提交的行（v1.10.3）：不再把已提交信息整个盖掉。
                //   有已提交历史 → 「本地未提交 · marvin，2 天前」：未提交状态 + 上次提交的作者/时间；
                //   整个方法都是本地新增 → 「尚未提交」。
                if (!string.IsNullOrEmpty(m.LastAuthor))
                {
                    var committed = m.LastDate.HasValue ? TimeAgo(m.LastDate.Value) : "";
                    var who = string.IsNullOrEmpty(committed)
                        ? m.LastAuthor
                        : Loc.Zh ? $"{m.LastAuthor}，{committed}" : $"{m.LastAuthor}, {committed}";
                    return Loc.T("Uncommitted", "本地未提交") + " · " + who;
                }
                return Loc.T("Not committed yet", "尚未提交");
            }
            if (string.IsNullOrEmpty(m.LastAuthor)) return "";
            var when = m.LastDate.HasValue ? TimeAgo(m.LastDate.Value) : "";
            if (string.IsNullOrEmpty(when)) return m.LastAuthor;
            return Loc.Zh ? $"{m.LastAuthor}，{when}" : $"{m.LastAuthor}, {when}";
        }

        /// <summary>Git CodeLens 风格文案（段2：N 名作者, M 项更改）。中文无复数变化。</summary>
        private static string FormatTail(MethodBlameView m)
        {
            if (Loc.Zh)
                return $"{m.AuthorCount} 名作者，{m.ChangeCount} 项更改";
            return $"{m.AuthorCount} {Pl(m.AuthorCount, "author", "authors")}, " +
                   $"{m.ChangeCount} {Pl(m.ChangeCount, "change", "changes")}";
        }

        private static string Pl(int n, string one, string many) => n == 1 ? one : many;

        private static string TimeAgo(DateTime utc)
        {
            try
            {
                var span = DateTime.UtcNow - (utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime());
                if (span.TotalMinutes < 1) return Loc.T("just now", "刚刚");
                if (span.TotalHours < 1)
                    return Loc.Zh
                        ? $"{(int)span.TotalMinutes} 分钟前"
                        : Pl((int)span.TotalMinutes, "1 minute ago", $"{(int)span.TotalMinutes} minutes ago");
                if (span.TotalDays < 1)
                    return Loc.Zh
                        ? $"{(int)span.TotalHours} 小时前"
                        : Pl((int)span.TotalHours, "1 hour ago", $"{(int)span.TotalHours} hours ago");
                if (span.TotalDays < 30)
                    return Loc.Zh
                        ? $"{(int)span.TotalDays} 天前"
                        : Pl((int)span.TotalDays, "1 day ago", $"{(int)span.TotalDays} days ago");
                if (span.TotalDays < 365)
                    return Loc.Zh
                        ? $"{(int)(span.TotalDays / 30)} 个月前"
                        : Pl((int)(span.TotalDays / 30), "1 month ago", $"{(int)(span.TotalDays / 30)} months ago");
                return Loc.Zh
                    ? $"{(int)(span.TotalDays / 365)} 年前"
                    : Pl((int)(span.TotalDays / 365), "1 year ago", $"{(int)(span.TotalDays / 365)} years ago");
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 打开弹框（点击式）：activity=true 显示 Team Activity 时间线，false 显示提交表格（log）。
        /// 不再使用"鼠标离开自动关闭"——右键菜单弹出会给原 Popup 触发 MouseLeave，
        /// 导致准备右击看详情时弹框先消失（已修复的 bug）。关闭只发生在：
        /// 点击编辑器、滚动、点击另一段（重建内容）、视图关闭。
        /// </summary>
        private void OpenPopup(MethodBlameView m, FrameworkElement anchor, bool activity)
        {
            try
            {
                _popup.IsOpen = false;

                var panel = new StackPanel { Orientation = Orientation.Vertical };
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Loading…", "加载中…"),
                    FontSize = 11,
                    Foreground = Brushes.Gray
                });
                // 稳定锚点（防飘走）：捕获打开瞬间的屏幕坐标，把 Popup 钉到稳定的文档视图元素上，
                // 装饰标签被重绘移除/重建也不会拖动弹框。
                try
                {
                    _popupAnchorScreen = anchor.PointToScreen(new Point(0, Math.Max(anchor.ActualHeight, 14)));
                    _popupAnchorTopScreen = anchor.PointToScreen(new Point(0, 0));
                }
                catch
                {
                    _popupAnchorScreen = new Point(double.NaN, double.NaN);
                    _popupAnchorTopScreen = new Point(double.NaN, double.NaN);
                }

                // Git CodeLens 式：优先往标签上方弹（带向下小箭头指向标签），上方放不下才往下
                _popupAbove = PredictAbove();

                _popup.PlacementTarget = _view.VisualElement;
                _popup.Placement = PlacementMode.Custom;
                _popup.CustomPopupPlacementCallback = PlacePopup;
                _popup.StaysOpen = true;
                _popup.AllowsTransparency = true;
                _popup.Child = BuildPopupShell(panel);
                _popup.IsOpen = true;
                SetPopupModal(true); // 弹框打开期间屏蔽编辑器操作（滚动/按键/点击），收起后恢复
                SetActiveSegment(anchor as TextBlock); // 选中态：点击的段保持高亮，与 CodeLens 一致
                HookWindowDeactivation();

                var path = _filePath;
                var revisions = m.Revisions;
                Task.Run(async () =>
                {
                    // 首屏：blame 修订（最新 30 个）立刻显示
                    var commits = await _blame.GetCommitsAsync(path, revisions.OrderByDescending(r => r).Take(30).ToList(), 30);
                    await _view.VisualElement.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_popup.IsOpen) return;
                        if (activity) FillActivity(panel, m, commits);
                        else FillCommits(panel, m, commits);
                        // 首屏只是快速预览；明确告知还有完整 log 核对在进行，
                        // 避免把中间结果误当成最终记录。
                        panel.Children.Add(new TextBlock
                        {
                            Text = Loc.T("Verifying against the complete file log...",
                                         "正在按完整提交日志核对方法历史…"),
                            FontSize = 11,
                            Foreground = Brushes.Gray,
                            Margin = new Thickness(0, 6, 0, 0)
                        });
                    });

                    // 完整历史：以文件全量 svn log 为候选，逐修订用 diff 判定是否改过该方法。
                    // 结果无条件替换首屏，保证记录完整性（首屏只是 blame 集合的快速预览）。
                    try
                    {
                        var full = await _blame.GetMethodLogAsync(path, m.Name, m.StartLine, revisions);
                        // 完整结果以「文件全量 log + 逐修订 diff 判定」为准，无条件替换首屏：
                        // 首屏只是 blame 集合的快速预览，条数可能更多也可能更少，不能按数量比较。
                        BlameService.Log($"popup history {m.Name}: first={commits.Count}, complete={full.Count}");
                        await _view.VisualElement.Dispatcher.InvokeAsync(() =>
                        {
                            if (!_popup.IsOpen) return;
                            if (activity) FillActivity(panel, m, full);
                            else FillCommits(panel, m, full);
                        });
                    }
                    catch (Exception ex)
                    {
                        BlameService.Log("deep history error: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                BlameService.Log("popup error: " + ex.Message);
            }
        }

        private void ClosePopup()
        {
            SetPopupModal(false);
            _popup.IsOpen = false;
            SetActiveSegment(null); // 复原选中态高亮
        }

        // ---- VS 主窗口失焦时关闭弹框（对齐 CodeLens 引用弹框：点其他应用/文件夹即收起）----

        private Window _hostWindow;

        private void HookWindowDeactivation()
        {
            if (_hostWindow != null) return;
            try
            {
                _hostWindow = Window.GetWindow(_view.VisualElement);
                if (_hostWindow != null)
                    _hostWindow.Deactivated += OnHostWindowDeactivated;
            }
            catch { }
        }

        private void OnHostWindowDeactivated(object sender, EventArgs e)
        {
            // 只有 VS 本体失活才关；VS 自己弹的子窗口（历史/详情）不算失焦
            ClosePopup();
        }

        // ---- 弹框打开期间的模态屏蔽：滚动/按键/点击编辑器都被拦截，收起弹框后才恢复 ----

        private void SetPopupModal(bool on)
        {
            var el = _view.VisualElement;
            if (on)
            {
                el.PreviewMouseWheel += BlockViewWheel;
                el.PreviewKeyDown += BlockViewKey;
                el.PreviewMouseLeftButtonDown += BlockViewClick;
                el.PreviewMouseRightButtonDown += BlockViewClick;
            }
            else
            {
                el.PreviewMouseWheel -= BlockViewWheel;
                el.PreviewKeyDown -= BlockViewKey;
                el.PreviewMouseLeftButtonDown -= BlockViewClick;
                el.PreviewMouseRightButtonDown -= BlockViewClick;
            }
        }

        private void BlockViewWheel(object sender, MouseWheelEventArgs e) => e.Handled = true;

        private void BlockViewKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ClosePopup(); // Esc 也能收起
            }
            e.Handled = true; // 其余按键（翻页/方向/输入）一律屏蔽
        }

        private void BlockViewClick(object sender, MouseButtonEventArgs e)
        {
            // 点到标注的另一段：放行，让段自身的 MouseLeftButtonUp 完成弹框切换
            if (IsInsideLensLabel(e.OriginalSource)) return;
            // 点编辑器 = 仅收起弹框，本次点击不产生选中/改光标等效果
            ClosePopup();
            e.Handled = true;
        }

        /// <summary>点击源是否在 SVN 标注标签内部（通过标签根元素 Tag 识别）。</summary>
        private static bool IsInsideLensLabel(object source)
        {
            var dep = source as DependencyObject;
            while (dep != null)
            {
                if (dep is FrameworkElement fe && "SvnMethodLensLabel".Equals(fe.Tag as string))
                    return true;
                dep = VisualTreeHelper.GetParent(dep);
            }
            return false;
        }

        // ---- Git CodeLens 式弹框外壳：跟随主题的边框 + 指向标签的小箭头 ----

        private bool _popupAbove = true;
        private Point _popupAnchorTopScreen;

        /// <summary>粗估弹框高度后判断上方是否放得下；放不下则往下弹。</summary>
        private bool PredictAbove()
        {
            try
            {
                if (double.IsNaN(_popupAnchorTopScreen.X)) return true;
                var workArea = SystemParameters.WorkArea;
                return _popupAnchorTopScreen.Y - 360 > workArea.Top; // 360 ≈ 弹框最大高度 + 箭头 + 余量
            }
            catch { return true; }
        }

        /// <summary>弹框外壳：内容边框 + 指向标签的三角小箭头（上方弹时箭头在下沿，反之在上沿）。</summary>
        private UIElement BuildPopupShell(FrameworkElement content)
        {
            var border = ThemedBorder(content, new Thickness(10));
            var root = new Grid();
            var caret = new System.Windows.Shapes.Polygon
            {
                Points = _popupAbove
                    ? new PointCollection(new[] { new Point(0, 0), new Point(12, 0), new Point(6, 8) })
                    : new PointCollection(new[] { new Point(0, 8), new Point(12, 8), new Point(6, 0) }),
                StrokeThickness = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(16, 0, 0, 0)
            };
            try
            {
                caret.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                    Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBackgroundKey);
                caret.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                    Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBorderKey);
            }
            catch
            {
                caret.Fill = Brushes.White;
                caret.Stroke = new SolidColorBrush(Color.FromRgb(190, 190, 190));
            }

            if (_popupAbove)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(border, 0);
                Grid.SetRow(caret, 1);
            }
            else
            {
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(caret, 0);
                Grid.SetRow(border, 1);
            }
            root.Children.Add(border);
            root.Children.Add(caret);
            return root;
        }

        /// <summary>跟随 VS 主题的弹框容器（浅色/深色）。</summary>
        private static Border ThemedBorder(FrameworkElement child, Thickness padding)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = padding,
                Child = child
            };
            try
            {
                border.SetResourceReference(Border.BackgroundProperty,
                    Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBackgroundKey);
                border.SetResourceReference(Border.BorderBrushProperty,
                    Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBorderKey);
            }
            catch
            {
                border.Background = Brushes.White;
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(190, 190, 190));
            }
            return border;
        }

        /// <summary>
        /// 用点击瞬间捕获的标签屏幕坐标把弹框钉在固定位置。
        /// PlacementTarget 是稳定的文档视图（VisualElement），因此装饰标签被移除/重建也不会让弹框漂移。
        /// </summary>
        private CustomPopupPlacement[] PlacePopup(Size popupSize, Size targetSize, Point offset)
        {
            try
            {
                if (!double.IsNaN(_popupAnchorScreen.X))
                {
                    var targetTopLeft = _view.VisualElement.PointToScreen(new Point(0, 0));
                    double x = _popupAnchorScreen.X - targetTopLeft.X;
                    var workArea = SystemParameters.WorkArea;
                    bool above = _popupAbove;

                    // 上方弹：弹框底沿在标签顶上方（箭头指向标签）；下方弹：顶沿在标签底下方
                    double ScreenY(bool up) => up
                        ? _popupAnchorTopScreen.Y - popupSize.Height - 2
                        : _popupAnchorScreen.Y + 2;

                    // 预期方向放不下则翻面
                    if (above && ScreenY(true) - targetTopLeft.Y < workArea.Top - targetTopLeft.Y)
                        above = false;
                    if (!above && _popupAnchorScreen.Y + popupSize.Height + 2 > workArea.Bottom)
                        above = true;

                    double y = ScreenY(above) - targetTopLeft.Y;
                    return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None) };
                }
            }
            catch
            {
                // 落到默认位置
            }
            return new[] { new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.None) };
        }

        private static readonly SolidColorBrush RowHoverBrush =
            new SolidColorBrush(Color.FromArgb(28, 0, 122, 204));

        private static readonly Color[] Palette =
        {
            Color.FromRgb(31, 119, 180), Color.FromRgb(214, 39, 40),
            Color.FromRgb(44, 160, 44),  Color.FromRgb(148, 103, 189),
            Color.FromRgb(255, 127, 14), Color.FromRgb(23, 190, 207),
            Color.FromRgb(227, 119, 194), Color.FromRgb(188, 189, 34)
        };

        /// <summary>提交表格弹框（Git CodeLens 结构：表格 + Show all file changes + Changes in months）。</summary>
        private void FillCommits(StackPanel panel, MethodBlameView m, List<CommitInfo> commits)
        {
            panel.Children.Clear();

            if (commits.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "未取到提交历史（非 SVN 工作副本或无权限）",
                    FontSize = 11,
                    Foreground = Brushes.Gray
                });
                return;
            }

            // v1.10.3：同 FillActivity——下表只是已提交历史，本地未提交的改动不在其中。
            if (m.HasLocal)
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Uncommitted local changes are not included below",
                                 "本地未提交的改动未计入以下历史"),
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 4)
                });

            // 列：Revision | Description | Author | Date
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

            AddHeaderRow(grid,
                Loc.T("Revision", "提交 ID"),
                Loc.T("Description", "说明"),
                Loc.T("Author", "作者"),
                Loc.T("Date", "日期"));

            var rowsHost = new StackPanel();
            Grid.SetRow(rowsHost, 1);
            // 关键：数据行宿主必须横跨全部 4 列；否则会被塞进第 0 列（76px 的
            // Revision 列）里，Description/Author/Date 全部被裁掉只剩修订号。
            Grid.SetColumnSpan(rowsHost, grid.ColumnDefinitions.Count);
            grid.Children.Add(rowsHost);

            var scroll = new ScrollViewer
            {
                MaxHeight = 240,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = grid
            };
            panel.Children.Add(scroll);

            // 底栏：左侧「Show all file changes」链接，右侧「Changes in months」过滤
            var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = false };

            var link = new TextBlock { FontSize = 11 };
            var hl = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(Loc.T("Show all file changes", "显示所有的文件更改")))
            { FontSize = 11 };
            hl.Click += (s, e) => ShowFileHistory();
            link.Inlines.Add(hl);
            DockPanel.SetDock(link, Dock.Left);
            bottom.Children.Add(link);

            var filterPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            filterPanel.Children.Add(new TextBlock
            {
                Text = Loc.T("Changes in months: ", "过去几个月的更改: "),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray
            });
            // 默认显示全部历史（含方法创建起的深度回溯）；填月份数可只看最近 N 个月
            var monthsBox = new TextBox { Text = "", Width = 36, FontSize = 11 };
            filterPanel.Children.Add(monthsBox);
            DockPanel.SetDock(filterPanel, Dock.Right);
            bottom.Children.Add(filterPanel);
            panel.Children.Add(bottom);

            Action rebuild = () =>
            {
                int months = 0;
                int.TryParse(monthsBox.Text.Trim(), out months);
                if (months <= 0) months = 1200;
                var cutoff = DateTime.Now.AddMonths(-months);
                rowsHost.Children.Clear();
                foreach (var c in commits)
                {
                    var local = c.Date == DateTime.MinValue ? DateTime.MinValue : c.Date.ToLocalTime();
                    if (local != DateTime.MinValue && local < cutoff) continue;
                    rowsHost.Children.Add(BuildCommitRow(m, c));
                }
            };
            monthsBox.TextChanged += (s, e) => rebuild();
            rebuild();
        }

        /// <summary>表头固定放在第 0 行（此前用 RowDefinitions.Count 取行号会把表头排到数据下面）。</summary>
        private static void AddHeaderRow(Grid grid, params string[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
                AddCell(grid, 0, i, cells[i], gray: true);
        }

        private static void AddCell(Grid grid, int row, int col, string text, bool gray = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (gray) tb.Foreground = Brushes.Gray;
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        private Border BuildCommitRow(MethodBlameView m, CommitInfo c)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

            var msg = (c.Message ?? "").Replace("\r", "").Replace("\n", " ");
            if (msg.Length > 60) msg = msg.Substring(0, 60) + "…";

            AddCell(grid, 0, 0, "r" + c.Revision);
            AddCell(grid, 0, 1, msg);
            AddCell(grid, 0, 2, c.Author, gray: true);
            AddCell(grid, 0, 3,
                c.Date == DateTime.MinValue ? "" : c.Date.ToLocalTime().ToString("yyyy/M/d"), gray: true);

            var row = new Border
            {
                Child = grid,
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 1, 0, 1)
            };
            row.MouseEnter += (s, e) => row.Background = RowHoverBrush;
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;

            // 双击记录 → 更新 VS 右侧 SVN 提交审查窗格；选择文件后在编辑区打开差异。
            row.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount != 2) return;
                e.Handled = true;
                ShowCommitDetail(c);
            };

            // 行右键菜单：查看提交详情（右侧停靠审查窗格）+ 直接打开当前文件 diff。
            var menu = new ContextMenu();
            var view = new MenuItem { Header = Loc.T("View Commit Details", "查看提交详情") };
            view.Click += (s, e) => ShowCommitDetail(c);
            menu.Items.Add(view);
            var showChanges = new MenuItem { Header = Loc.T("Show changes", "查看变更") };
            showChanges.Click += (s, e) => ShowChangesForCommit(c);
            menu.Items.Add(showChanges);
            row.ContextMenu = menu;
            return row;
        }

        /// <summary>Team Activity 图表（Git CodeLens 结构：标题 + 按作者着色的散点时间轴 + 图例）。</summary>
        private void FillActivity(StackPanel panel, MethodBlameView m, List<CommitInfo> commits)
        {
            panel.Children.Clear();
            var dated = commits
                .Where(c => c.Date != DateTime.MinValue)
                .OrderBy(c => c.Date)
                .ToList();

            if (dated.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "未取到提交历史（非 SVN 工作副本或无权限）",
                    FontSize = 11,
                    Foreground = Brushes.Gray
                });
                return;
            }

            var span = dated[dated.Count - 1].Date - dated[0].Date;
            int overDays = Math.Max(1, (int)Math.Ceiling(span.TotalDays));
            var authors = dated.Select(c => c.Author).Distinct().ToList();

            panel.Children.Add(new TextBlock
            {
                Text = Loc.Zh
                    ? $"团队活动: {authors.Count} 名作者在 {overDays} 天内进行的 {dated.Count} 项更改"
                    : $"Team Activity: {dated.Count} {Pl(dated.Count, "change", "changes")} by " +
                      $"{authors.Count} {Pl(authors.Count, "author", "authors")} over {overDays} days",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6)
            });

            // v1.10.3：方法有本地未提交行时，明确告诉用户下图只是「已提交」历史，
            // 避免出现"标注说尚未提交、点开却全是具体的人"这种困惑。
            if (m.HasLocal)
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Uncommitted local changes are not included below",
                                 "本地未提交的改动未计入以下历史"),
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 6)
                });

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var canvas = BuildActivityCanvas(dated, authors);
            Grid.SetColumn(canvas, 0);
            content.Children.Add(canvas);

            var legend = BuildLegend(dated, authors);
            Grid.SetColumn(legend, 1);
            content.Children.Add(legend);

            panel.Children.Add(content);
        }

        private UIElement BuildActivityCanvas(List<CommitInfo> dated, List<string> authors)
        {
            double w = 350;
            double laneH = 22;
            double plotH = Math.Max(60, authors.Count * laneH); // 散点绘图区
            double axisH = 32;                                  // 底部时间轴区域（刻度数字 + Days ago）
            double h = plotH + axisH;

            var canvas = new Canvas
            {
                Width = w,
                Height = h,
                Background = Brushes.Transparent
            };

            var now = DateTime.UtcNow;
            double maxDays = 1;
            foreach (var c in dated)
            {
                var d = (now - c.Date.ToUniversalTime()).TotalDays;
                if (d > maxDays) maxDays = d;
            }

            double axisY = plotH - 6;
            Func<double, double> xOf = days => 8 + (1 - days / maxDays) * (w - 30);

            var axisBrush = new SolidColorBrush(Color.FromRgb(140, 140, 140));

            // 轴（右侧=最近，向左越旧）
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 4, Y1 = axisY, X2 = w - 4, Y2 = axisY,
                Stroke = axisBrush, StrokeThickness = 1
            });

            // 要求4：底部时间轴——刻度短线 + 天数数字（和参考截图一致）
            double[] steps = { 1, 2, 5, 10, 20, 30, 60, 90, 180, 365 };
            double step = steps.Last(s => maxDays / s <= 8);
            for (double d = 0; d <= maxDays + 0.5; d += step)
            {
                double x = xOf(d);
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = x, Y1 = axisY, X2 = x, Y2 = axisY + 4,
                    Stroke = axisBrush, StrokeThickness = 1
                });
                var lbl = new TextBlock
                {
                    Text = ((int)Math.Round(d)).ToString(),
                    FontSize = 9,
                    Foreground = Brushes.Gray
                };
                double lx = Math.Min(Math.Max(x - 8, 0), w - 18);
                Canvas.SetLeft(lbl, lx);
                Canvas.SetTop(lbl, axisY + 6);
                canvas.Children.Add(lbl);
            }

            // 右下角标题「Days ago」
            var cap = new TextBlock
            {
                Text = Loc.T("Days ago", "天前"),
                FontSize = 9,
                Foreground = Brushes.Gray,
                Width = w - 12,
                TextAlignment = TextAlignment.Right
            };
            Canvas.SetLeft(cap, 6);
            Canvas.SetTop(cap, axisY + 18);
            canvas.Children.Add(cap);

            // 每个提交一个点：x=天数，y=作者泳道
            var authorColor = new Dictionary<string, Color>();
            for (int i = 0; i < authors.Count; i++)
                authorColor[authors[i]] = Palette[i % Palette.Length];

            double dotTop = 4;
            foreach (var c in dated)
            {
                double days = (now - c.Date.ToUniversalTime()).TotalDays;
                double x = xOf(Math.Min(days, maxDays));
                double y = dotTop + Array.IndexOf(authors.ToArray(), c.Author) * laneH + laneH / 2;
                if (y > axisY - 6) y = axisY - 6;
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 9,
                    Height = 9,
                    Fill = new SolidColorBrush(authorColor[c.Author]),
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    ToolTip = $"r{c.Revision} · {c.Author} · {c.Date.ToLocalTime():yyyy/M/d}"
                };
                Canvas.SetLeft(dot, x - 4.5);
                Canvas.SetTop(dot, y - 4.5);
                canvas.Children.Add(dot);
            }
            return canvas;
        }

        private static UIElement BuildLegend(List<CommitInfo> dated, List<string> authors)
        {
            var legend = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            for (int i = 0; i < authors.Count; i++)
            {
                var count = dated.Count(c => c.Author == authors[i]);
                var item = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                item.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(Palette[i % Palette.Length]),
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                item.Children.Add(new TextBlock
                {
                    Text = $"{authors[i]} ({count})",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                });
                legend.Children.Add(item);
            }
            return legend;
        }

        /// <summary>「Show all file changes」→ 文件级历史窗口（Revision/Author/Date/Message）。</summary>
        private void ShowFileHistory()
        {
            try
            {
                // 先关掉 log 弹框：Popup 常驻顶层，会把刚打开的历史窗口遮住
                ClosePopup();
                var win = new Window
                {
                    Title = Loc.T(
                        $"History - {System.IO.Path.GetFileName(_filePath)}",
                        $"历史 - {System.IO.Path.GetFileName(_filePath)}"),
                    Width = 820,
                    Height = 440
                };
                var grid = new Grid { Margin = new Thickness(8) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var header = new TextBlock
                {
                    Text = Loc.T("File History (SVN log)", "文件历史 (SVN 日志)"),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(2, 0, 0, 6)
                };
                Grid.SetRow(header, 0);
                grid.Children.Add(header);

                var lv = new ListView { FontSize = 11 };
                var gv = new GridView();
                gv.Columns.Add(new GridViewColumn
                {
                    Header = Loc.T("Revision", "版本"), Width = 80,
                    DisplayMemberBinding = new System.Windows.Data.Binding("RevisionText")
                });
                gv.Columns.Add(new GridViewColumn
                {
                    Header = Loc.T("Author", "作者"), Width = 110,
                    DisplayMemberBinding = new System.Windows.Data.Binding("Author")
                });
                gv.Columns.Add(new GridViewColumn
                {
                    Header = Loc.T("Date", "日期"), Width = 150,
                    DisplayMemberBinding = new System.Windows.Data.Binding("DateText")
                });
                gv.Columns.Add(new GridViewColumn
                {
                    Header = Loc.T("Message", "说明"), Width = 430,
                    DisplayMemberBinding = new System.Windows.Data.Binding("Message")
                });
                lv.View = gv;
                Grid.SetRow(lv, 1);
                grid.Children.Add(lv);

                win.Content = grid;
                win.Show();

                var path = _filePath;
                Task.Run(async () =>
                {
                    var log = await _blame.GetFileLogAsync(path);
                    await _view.VisualElement.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            lv.ItemsSource = log.Select(c => new
                            {
                                RevisionText = "r" + c.Revision,
                                c.Author,
                                DateText = c.Date == DateTime.MinValue
                                    ? ""
                                    : c.Date.ToLocalTime().ToString("yyyy/M/d HH:mm:ss"),
                                Message = (c.Message ?? "").Replace("\r", "").Replace("\n", " ")
                            }).ToList();
                        }
                        catch { /* 窗口已关闭等情况 */ }
                    });
                });
            }
            catch (Exception ex)
            {
                BlameService.Log("history window error: " + ex.Message);
            }
        }

        /// <summary>
        /// 弹框行右键「查看变更」：**直接**打开差异，不经过明细窗口。
        /// 明细窗口在这里只是"查出这次提交改了哪些文件"的中间产物，把它弹出来会让用户
        /// 看到明细窗口闪一下就消失（v1.10.1 及之前的行为）。
        /// 只有确实定位不到路径时才退回明细窗口，让用户自己挑。
        /// </summary>
        private void ShowChangesForCommit(CommitInfo c)
        {
            try
            {
                ClosePopup();
                var filePath = _filePath;
                int rev = c.Revision;
                var caption = Loc.T($"Commit r{rev}", $"提交 r{rev}");
                Task.Run(async () =>
                {
                    var detail = await _blame.GetCommitDetailAsync(filePath, rev);
                    var target = PickTargetPath(detail);
                    await _view.VisualElement.Dispatcher.InvokeAsync(() =>
                    {
                        if (target == null) OpenCommitDetail(caption, detail); // 没有可定位的文件：开明细页让用户自己挑
                        else ShowRevisionChanges(rev, target);
                    });
                });
            }
            catch (Exception ex)
            {
                BlameService.Log("show changes (direct) error: " + ex.Message);
            }
        }

        /// <summary>只在提交包含当前方法所在文件时返回该路径，不把其它文件误认为当前方法的变更。</summary>
        private static ChangedPath PickCurrentPath(CommitDetail detail, string filePath)
        {
            if (detail == null || detail.Paths == null || string.IsNullOrEmpty(filePath)) return null;
            var currentName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(currentName)) return null;
            return detail.Paths.FirstOrDefault(changed =>
                string.Equals(Path.GetFileName((changed.Path ?? "").Replace('/', Path.DirectorySeparatorChar)),
                              currentName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>优先选"当前打开的这个文件"在本次提交里的那一行，否则取第一条。</summary>
        private ChangedPath PickTargetPath(CommitDetail d)
        {
            if (d == null || d.Paths.Count == 0) return null;
            var own = System.IO.Path.GetFileName(_filePath ?? "");
            if (!string.IsNullOrEmpty(own))
            {
                var match = d.Paths.FirstOrDefault(p =>
                    System.IO.Path.GetFileName(p.Path).Equals(own, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return d.Paths[0];
        }

        /// <summary>查看提交详情：在 Visual Studio 右侧打开 SVN 提交审查窗格，不创建独立浮窗。</summary>
        private void ShowCommitDetail(CommitInfo c)
        {
            try
            {
                ClosePopup();
                var path = _filePath;
                int rev = c.Revision;
                Task.Run(async () =>
                {
                    var detail = await _blame.GetCommitDetailAsync(path, rev);
                    var initialPath = PickCurrentPath(detail, path);
                    await _view.VisualElement.Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            var package = SvnMethodLens.SvnMethodLensPackage.Instance;
                            if (package == null)
                            {
                                var shell = Microsoft.VisualStudio.Shell.Package.GetGlobalService(
                                    typeof(Microsoft.VisualStudio.Shell.Interop.SVsShell)) as Microsoft.VisualStudio.Shell.Interop.IVsShell;
                                if (shell != null && shell.LoadPackage(
                                    new Guid(SvnMethodLens.SvnMethodLensPackage.PackageGuidString), out var loaded) == 0)
                                    package = loaded as SvnMethodLens.SvnMethodLensPackage;
                            }
                            if (package == null) throw new InvalidOperationException("Unable to load the SVN package.");
                            var diffSession = package.BeginDiffReviewSession();
                            await package.ShowCommitReviewAsync(detail, changed => ShowRevisionChanges(rev, changed, diffSession), initialPath);
                        }
                        catch (Exception ex)
                        {
                            BlameService.Log("commit review tool window error: " + ex);
                            MessageBox.Show(
                                Loc.T("Unable to open the SVN commit review pane. See %LOCALAPPDATA%\\SvnMethodLens\\log.txt for details.",
                                     "无法打开 SVN 提交审查窗格。详情见 %LOCALAPPDATA%\\SvnMethodLens\\log.txt。"),
                                Loc.T("SVN Commit", "SVN 提交"), MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                BlameService.Log("detail error: " + ex.Message);
            }
        }

        /// <summary>
        /// 用 IVsUIShell.CreateDocumentWindow 把内容开成文档页（编辑区标签页）。
        /// 返回窗格对象（供后续替换 Content 填数据）；失败返回 null，由调用方退回普通窗口。
        /// 签名经反射 Microsoft.VisualStudio.Shell.Interop 17.x 实锤（2026-10-21）。
        /// </summary>
        private string MakeCommitDocumentKey(string filePath, int revision)
        {
            var identity = System.IO.Path.GetFullPath(filePath ?? "").ToLowerInvariant();
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(identity));
                var key = BitConverter.ToString(bytes, 0, 8).Replace("-", "").ToLowerInvariant();
                return System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "SvnMethodLensCommit-" + key + "-r" + revision + ".svncommit");
            }
        }

        private CommitDetailsPane ShowCommitPage(string documentKey, string caption, UIElement content)
        {
            BlameService.Log($"CreateDocumentWindow begin key={documentKey} caption={caption}");
            try
            {
                var pane = new CommitDetailsPane(content);
                var punkDocView = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(pane);
                try
                {
                    var shell = (Microsoft.VisualStudio.Shell.Interop.IVsUIShell)
                        Microsoft.VisualStudio.Shell.Package.GetGlobalService(
                            typeof(Microsoft.VisualStudio.Shell.Interop.SVsUIShell));
                    if (shell == null)
                    {
                        BlameService.Log("SVsUIShell unavailable, fallback to window");
                        return null;
                    }

                    var editorType = Guid.Empty;
                    var cmdUi = Guid.Empty;
                    var defPos = new int[1];
                    // 站点优先用本扩展的包（Package 实现 OLE IServiceProvider，经反射实锤）；
                    // 取不到就传 null——窗格内容不依赖服务也能显示。
                    var site = GetPackageSite();
                    int hr = shell.CreateDocumentWindow(
                        0,                                    // grfCDW：GUID_NULL 的自定义编辑器窗格
                        documentKey,                          // pszMkDocument：稳定且唯一的本地 RDT 身份
                        null,                                 // pUIH：不属于任何项目
                        0,                                    // itemid：无层级时使用默认项标识
                        punkDocView,
                        IntPtr.Zero,                          // punkDocData：单视图文档
                        ref editorType,
                        null,                                 // pszPhysicalView
                        ref cmdUi,
                        site,                                 // psp：窗格站点
                        null,                                 // pszOwnerCaption
                        caption,                              // pszEditorCaption：标签文字
                        defPos,
                        out var frame);
                    if (hr >= 0 && frame != null)
                    {
                        var showHr = frame.Show();
                        if (showHr >= 0) return pane;
                        BlameService.Log($"document frame Show failed hr=0x{showHr:X}");
                    }
                    BlameService.Log($"CreateDocumentWindow failed hr=0x{hr:X}, frameNull={frame == null}");
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.Release(punkDocView);
                }
            }
            catch (Exception ex)
            {
                BlameService.Log("commit document page error: " + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// 文档页窗格的 OLE IServiceProvider 站点：优先本扩展的包实例（Package 实现 OLE IServiceProvider）；
        /// 包还没初始化就强制加载一次。都失败返回 null（调用方传 null，窗格不依赖服务）。
        /// </summary>
        private static Microsoft.VisualStudio.OLE.Interop.IServiceProvider GetPackageSite()
        {
            try
            {
                if (SvnMethodLens.SvnMethodLensPackage.Instance != null)
                    return SvnMethodLens.SvnMethodLensPackage.Instance;
                var shell = (Microsoft.VisualStudio.Shell.Interop.IVsShell)
                    Microsoft.VisualStudio.Shell.Package.GetGlobalService(
                        typeof(Microsoft.VisualStudio.Shell.Interop.SVsShell));
                if (shell != null &&
                    shell.LoadPackage(new Guid(SvnMethodLens.SvnMethodLensPackage.PackageGuidString), out var pkg) == 0 &&
                    pkg is Microsoft.VisualStudio.OLE.Interop.IServiceProvider sp)
                    return sp;
            }
            catch (Exception ex)
            {
                BlameService.Log("package site error: " + ex.Message);
            }
            return null;
        }

        private static UIElement BuildLoadingContent(string text)
        {
            var panel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            panel.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Brushes.Gray });
            return panel;
        }

        /// <summary>构建 Git 提交页面布局：编辑区文档页右侧承载提交信息与分层变更树。</summary>
        private UIElement BuildCommitDetailContent(CommitDetail d)
        {
            var root = new Grid { Background = SystemColors.WindowBrush };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

            // 提交内容是审查页主体，不显示空白编辑画布；变更树与提交元信息位于右侧栏。
            var main = new Grid { Background = SystemColors.WindowBrush, Margin = new Thickness(18, 14, 18, 12) };
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var title = new TextBlock
            {
                Text = Loc.T("Commit r", "提交 r") + d.Commit.Revision,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            };
            main.Children.Add(title);
            var message = new TextBlock
            {
                Text = (d.Commit.Message ?? "").Trim(),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(message, 1);
            main.Children.Add(message);
            var blankState = new TextBlock
            {
                Text = Loc.T("Select a changed file from the list to view its changes.", "从右侧变更列表选择文件以查看差异。"),
                FontSize = 12,
                Foreground = SystemColors.GrayTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 80, 0, 0)
            };
            Grid.SetRow(blankState, 2);
            main.Children.Add(blankState);
            Grid.SetColumn(main, 0);
            root.Children.Add(main);

            var side = new Border
            {
                Background = SystemColors.ControlBrush,
                BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1, 0, 0, 0),
                Margin = new Thickness(0)
            };
            var sideContent = new Grid();
            side.Child = sideContent;
            sideContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sideContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sideContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(side, 1);
            root.Children.Add(side);

            var header = new TextBlock
            {
                Text = Loc.T("Commit", "提交"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(12, 10, 8, 7)
            };
            Grid.SetRow(header, 0);
            sideContent.Children.Add(header);

            var summary = new StackPanel { Margin = new Thickness(12, 0, 10, 10) };
            summary.Children.Add(new TextBlock
            {
                Text = "ID: r" + d.Commit.Revision,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            summary.Children.Add(new TextBlock
            {
                Text = d.Commit.Author,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2)
            });
            summary.Children.Add(new TextBlock
            {
                Text = d.Commit.Date == DateTime.MinValue ? "" : d.Commit.Date.ToLocalTime().ToString("yyyy/M/d HH:mm"),
                FontSize = 11,
                Foreground = SystemColors.GrayTextBrush
            });
            Grid.SetRow(summary, 1);
            sideContent.Children.Add(summary);

            var treePanel = new Grid();
            treePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            treePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var tree = new TreeView { FontSize = 11, BorderThickness = new Thickness(0) };
            var treeTitle = new TextBlock
            {
                Text = Loc.T("Changes", "更改"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(12, 9, 8, 5)
            };
            Grid.SetRow(treeTitle, 0);
            treePanel.Children.Add(treeTitle);
            var rootNode = new TreeViewItem
            {
                Header = Loc.T("Changes", "更改") + " (" + d.Paths.Count + ")",
                IsExpanded = true
            };
            var folders = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var changed in d.Paths.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
            {
                var parts = (changed.Path ?? "").Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                TreeViewItem parent = rootNode;
                var current = "";
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    current += "/" + parts[i];
                    if (!folders.TryGetValue(current, out var folder))
                    {
                        folder = new TreeViewItem { Header = parts[i], IsExpanded = true };
                        folders[current] = folder;
                        parent.Items.Add(folder);
                    }
                    parent = folder;
                }

                var file = new TreeViewItem
                {
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = changed.Action ?? "M", Width = 22, Foreground = SystemColors.GrayTextBrush },
                        new TextBlock { Text = parts[parts.Length - 1] }
                    }
                },
                Tag = changed,
                ToolTip = changed.Path
                };
                file.MouseDoubleClick += (s, e) =>
                {
                    if (file.Tag is ChangedPath p) ShowRevisionChanges(d.Commit.Revision, p);
                    e.Handled = true;
                };
                var menu = new ContextMenu();
                var miDiff = new MenuItem { Header = Loc.T("Show changes", "查看变更") };
                miDiff.Click += (s, e) => { if (file.Tag is ChangedPath p) ShowRevisionChanges(d.Commit.Revision, p); };
                menu.Items.Add(miDiff);
                var miUnified = new MenuItem { Header = Loc.T("Show changes as unified diff", "以 Unified Diff 查看") };
                miUnified.Click += (s, e) => { if (file.Tag is ChangedPath p) ShowRevisionUnifiedDiff(d.Commit.Revision, p); };
                menu.Items.Add(miUnified);
                menu.Items.Add(new Separator());
                var miOpen = new MenuItem { Header = Loc.T("Open this revision", "打开该修订的此文件") };
                miOpen.Click += (s, e) => { if (file.Tag is ChangedPath p) OpenRevisionFile(d.Commit.Revision, p); };
                menu.Items.Add(miOpen);
                file.ContextMenu = menu;
                parent.Items.Add(file);
            }
            if (d.Paths.Count == 0)
            {
                rootNode.Items.Add(new TreeViewItem { Header = Loc.T("No changed files", "没有变更文件"), IsEnabled = false });
            }
            tree.Items.Add(rootNode);
            Grid.SetRow(tree, 1);
            tree.Margin = new Thickness(8, 0, 0, 0);
            treePanel.Children.Add(tree);
            Grid.SetRow(treePanel, 2);
            sideContent.Children.Add(treePanel);
            return root;
        }

        /// <summary>数据已在手时直接开明细文档页；文档页开不起 → 普通窗口兜底。</summary>
        private void OpenCommitDetail(string caption, CommitDetail detail)
        {
            var page = ShowCommitPage(MakeCommitDocumentKey(_filePath, detail.Commit.Revision), caption, BuildCommitDetailContent(detail));
            if (page == null) ShowDetailWindow(caption, detail);
        }

        /// <summary>兜底：文档页开不起来时，用普通窗口显示提交明细。</summary>
        private void ShowDetailWindow(string caption, CommitDetail d)
        {
            try
            {
                var win = new Window
                {
                    Title = caption,
                    Width = 960,
                    Height = 560
                };
                win.Content = BuildCommitDetailContent(d);
                win.Show();
            }
            catch (Exception ex)
            {
                BlameService.Log("detail window error: " + ex.Message);
            }
        }

        /// <summary>
        /// 「查看变更」：把该修订前后两个版本在 VS 的差异比较器里并排打开
        /// （对齐 TortoiseSVN 的 Show changes）。
        /// </summary>
        private void ShowRevisionChanges(int revision, ChangedPath p, int diffSession = 0)
        {
            if (p == null) return;
            var filePath = _filePath;
            var repoPath = p.Path;
            var action = p.Action;

            var reviewPackage = diffSession != 0 ? SvnMethodLens.SvnMethodLensPackage.Instance : null;
            var requestGeneration = 0;
            if (diffSession != 0 && Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
                requestGeneration = reviewPackage?.BeginDiffOpenRequest(diffSession) ?? -1;

            Task.Run(async () =>
            {
                // 新增文件：该修订之前没有内容 → 左侧为空
                string left = null;
                if (!string.Equals(action, "A", StringComparison.OrdinalIgnoreCase) && revision > 1)
                    left = await _blame.GetPathContentAsync(filePath, repoPath, revision - 1);
                var right = await _blame.GetPathContentAsync(filePath, repoPath, revision);

                await _view.VisualElement.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (diffSession != 0 && (requestGeneration < 0 || reviewPackage == null ||
                            !reviewPackage.IsCurrentDiffOpenRequest(diffSession, requestGeneration)))
                        {
                            BlameService.Log($"DIFFREVIEW SKIP-STALE-OPEN session={diffSession}; request={requestGeneration}");
                            return;
                        }
                        if (diffSession != 0) reviewPackage.PrepareDiffReplacement(diffSession);
                        if (left == null && right == null)
                        {
                            MessageBox.Show(
                                Loc.T("Cannot fetch this revision from SVN.", "无法从 SVN 取到该修订的内容。"),
                                "SVN Log Change Details", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }
                        var name = System.IO.Path.GetFileName(repoPath);
                        var package = diffSession != 0 ? SvnMethodLens.SvnMethodLensPackage.Instance : null;
                        if (diffSession != 0) package?.PrepareDiffReplacement(diffSession);
                        var l = WriteRevisionTemp(name, revision - 1, left ?? "");
                        var r = WriteRevisionTemp(name, revision, right ?? "");
                        // 差异页标签对齐 Git 的格式：文件名 (修订)，如 AirRateController.cs (r5607904)
                        var caption = $"{name} (r{revision})";
                        if (diffSession != 0)
                            package?.RegisterExpectedDiffDocument(diffSession, l, r);
                        BlameService.Log($"DIFFREVIEW OPEN-REQUEST session={diffSession}; revision={revision}; path={repoPath}; left={l}; right={r}");
                        // 并排差异打不开（服务不可用等）就退回该修订的 unified diff 文本
                        if (!OpenVsDiff(l, r, caption, $"r{revision - 1}", $"r{revision}", diffSession))
                            ShowRevisionUnifiedDiff(revision, p);
                    }
                    catch (Exception ex)
                    {
                        BlameService.Log("show changes error: " + ex.Message);
                    }
                });
            });
        }

        /// <summary>把某个修订的文本写到临时文件（保留原扩展名，VS 差异视图才会有语法高亮）。</summary>
        private static string WriteRevisionTemp(string fileName, int revision, string text)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SvnMethodLens", "diff");
            Directory.CreateDirectory(dir);
            var tag = revision > 0 ? "r" + revision : "empty";
            // A unique moniker per comparison prevents delayed RDT unlock events from
            // an old/replaced diff being mistaken for the currently displayed pair.
            var file = System.IO.Path.Combine(dir, $"{tag}-{Guid.NewGuid():N}-{fileName}");
            File.WriteAllText(file, text ?? "", new UTF8Encoding(false));
            return file;
        }

        /// <summary>
        /// 用 VS 自带的差异比较器并排打开两个文件。
        /// 返回 false 表示服务不可用，调用方应退回 svn diff 文本视图。
        /// </summary>
        private bool OpenVsDiff(string leftFile, string rightFile, string caption,
                                string leftLabel, string rightLabel, int diffSession = 0)
        {
            // 差异服务只能在 UI 线程调用；万一不在就切回去（分析器 VSTHRD010 也要求这一点）
            if (!Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(
                    () => OpenVsDiff(leftFile, rightFile, caption, leftLabel, rightLabel)));
                return true; // 已经排到 UI 线程，按成功处理
            }
            try
            {
                var svc = Microsoft.VisualStudio.Shell.Package.GetGlobalService(
                    typeof(Microsoft.VisualStudio.Shell.Interop.SVsDifferenceService))
                    as Microsoft.VisualStudio.Shell.Interop.IVsDifferenceService;
                if (svc != null)
                {
                    var frame = svc.OpenComparisonWindow2(leftFile, rightFile, caption, caption,
                        leftLabel, rightLabel, null, null, 0);
                    var package = SvnMethodLens.SvnMethodLensPackage.Instance;
                    if (frame != null && package != null && diffSession != 0)
                        package.SetActiveDiffFrame(diffSession, frame);
                    return frame != null;
                }
                BlameService.Log("difference service unavailable, falling back to unified diff");
            }
            catch (Exception ex)
            {
                BlameService.Log("OpenVsDiff failed: " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// 「以 Unified Diff 查看变更」：跑 svn diff -c REV，把补丁文本显示在自己的窗口里
        /// （对齐 TortoiseSVN 的 Show changes as unified diff）。
        /// </summary>
        private void ShowRevisionUnifiedDiff(int revision, ChangedPath p)
        {
            if (p == null) return;
            var filePath = _filePath;
            var repoPath = p.Path;
            var name = System.IO.Path.GetFileName(repoPath);
            var title = string.Format(Loc.T("{0} - r{1} unified diff", "{0} - r{1} 的 unified diff"),
                name, revision);

            Task.Run(async () =>
            {
                var text = await _blame.GetPathDiffTextAsync(filePath, repoPath, revision);
                await _view.VisualElement.Dispatcher.InvokeAsync(() =>
                {
                    if (text == null)
                    {
                        MessageBox.Show(
                            Loc.T("Cannot fetch the diff of this revision.",
                                  "无法取到该修订的 diff。"),
                            "SVN Log Change Details", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    if (text.Trim().Length == 0)
                    {
                        MessageBox.Show(
                            Loc.T("This revision does not change this file.",
                                  "该修订未改动此文件。"),
                            "SVN Log Change Details", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    ShowDiffTextWindow(title, text);
                });
            });
        }

        /// <summary>打开某修订下该文件的内容（只读查看，临时文件形式）。</summary>
        private void OpenRevisionFile(int revision, ChangedPath p)
        {
            if (p == null) return;
            var filePath = _filePath;
            var repoPath = p.Path;
            Task.Run(async () =>
            {
                var text = await _blame.GetPathContentAsync(filePath, repoPath, revision);
                await _view.VisualElement.Dispatcher.InvokeAsync(() =>
                {
                    if (text == null)
                    {
                        MessageBox.Show(Loc.T("Cannot fetch this revision from SVN.", "无法从 SVN 取到该修订的内容。"),
                            "SVN Log Change Details", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    var f = WriteRevisionTemp(System.IO.Path.GetFileName(repoPath), revision, text);
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = f,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex) { BlameService.Log("open revision file failed: " + ex.Message); }
                });
            });
        }

        /// <summary>统一 diff 文本窗口：等宽字体 + 增删行着色 + 复制按钮。</summary>
        private void ShowDiffTextWindow(string title, string text)
        {
            try
            {
                var win = new Window { Title = title, Width = 980, Height = 560 };
                var grid = new Grid { Margin = new Thickness(8) };
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var rtb = new RichTextBox
                {
                    IsReadOnly = true,
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    FontSize = 11.5,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    IsDocumentEnabled = true
                };
                var doc = new FlowDocument { PagePadding = new Thickness(2) };
                var para = new Paragraph { Margin = new Thickness(0), LineHeight = 15 };
                var add = new SolidColorBrush(Color.FromRgb(0x1B, 0x7F, 0x37));
                var del = new SolidColorBrush(Color.FromRgb(0xC0, 0x28, 0x28));
                var hunk = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
                var meta = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70));

                foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
                {
                    Brush brush = null;
                    if (raw.StartsWith("+++") || raw.StartsWith("---") || raw.StartsWith("===") ||
                        raw.StartsWith("Index:") || raw.StartsWith("Property changes"))
                        brush = meta;
                    else if (raw.StartsWith("@@")) brush = hunk;
                    else if (raw.StartsWith("+")) brush = add;
                    else if (raw.StartsWith("-")) brush = del;

                    var run = new Run(raw + "\n");
                    if (brush != null) run.Foreground = brush;
                    para.Inlines.Add(run);
                }
                doc.Blocks.Add(para);
                rtb.Document = doc;
                Grid.SetRow(rtb, 0);
                grid.Children.Add(rtb);

                var bar = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 8, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                var copy = new Button
                {
                    Content = Loc.T("Copy", "复制"),
                    Padding = new Thickness(12, 3, 12, 3),
                    MinWidth = 72
                };
                copy.Click += (s, e) =>
                {
                    try { Clipboard.SetText(text); } catch { }
                };
                bar.Children.Add(copy);
                Grid.SetRow(bar, 1);
                grid.Children.Add(bar);

                win.Content = grid;
                win.Show();
            }
            catch (Exception ex)
            {
                BlameService.Log("diff text window error: " + ex.Message);
            }
        }

    }
}
