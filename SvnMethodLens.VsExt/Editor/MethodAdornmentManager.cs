using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    }

    /// <summary>
    /// 给方法声明行上方预留一条空带，让标注像 Git CodeLens 一样独占一行，不遮挡代码。
    /// </summary>
    [Export(typeof(ILineTransformSourceProvider))]
    [Name("SvnMethodLensLineTransform")]
    [ContentType("CSharp")]
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
                if (_state.IsMethodStart(line0))
                    return new LineTransform(TotalTopSpace, 0.0, 1.0);
            }
            catch
            {
                // 行未就绪等极端情况：返回恒等变换
            }
            return new LineTransform(0.0, 0.0, 1.0);
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
        private readonly System.Windows.Controls.Primitives.Popup _popup =
            new System.Windows.Controls.Primitives.Popup();

        // 点击瞬间捕获的标签屏幕坐标；Popup 据此固定在稳定锚点上，不再随装饰重绘而漂走
        private Point _popupAnchorScreen = new Point(double.NaN, double.NaN);

        public MethodAdornmentManager(IWpfTextView view, BlameService blame)
        {
            _view = view;
            _blame = blame;
            _layer = view.GetAdornmentLayer("SvnMethodLens");
            _state = LensState.GetOrCreate(view);

            if (_view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                _filePath = doc.FilePath;

            BlameService.Log($"view created: {_filePath ?? "(null)"}");

            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnClosed;
            // 在编辑器任意处按下鼠标时关闭弹框（Git CodeLens 行为）
            _view.VisualElement.PreviewMouseDown += (s, e) => ClosePopup();

            ScheduleRefresh();
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
                _layer.RemoveAllAdornments();

                List<MethodBlameView> data;
                lock (_gate) data = _data;
                if (data.Count == 0) return;

                ITextViewLineCollection lines;
                try { lines = _view.TextViewLines; }
                catch { return; }
                if (lines == null || lines.Count == 0) return;

                int first = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
                int last = lines.LastVisibleLine.Start.GetContainingLine().LineNumber;
                int lo = first - 2, hi = last + 2;

                var snapshot = _view.TextSnapshot;
                // 要求5：找到视口内 CodeLens "N references" 的位置，把标注画到引用后面（同一行）
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

                    // 横向：优先紧贴实际检测到的 CodeLens 引用（"N references / N 个引用"），
                    // 与 Git CodeLens 的排布一致，避免固定预留宽度造成的割裂感；
                    // 引用元素没找到时（CodeLens 关闭/尚未渲染）才退回 "99+ references/个引用" 的预留宽度
                    double labelX;
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
                        // 与 CodeLens 引用完全同字体/字号/颜色：直接抄引用文本元素的字体规格
                        if (best.Family != null && best.FontSize > 0)
                            _lensFont = new LensFont
                            {
                                Family = best.Family,
                                Size = best.FontSize,
                                Brush = best.Foreground
                            };
                    }
                    else
                    {
                        // 要求6：log 与引用一起出现。CodeLens 引用还没渲染时先不画标注，
                        // 安排 250ms 后重试（期间不显示 log），等引用出现的同一时刻再画；
                        // 约 5 秒仍没有引用（CodeLens 被关闭等）才退回预留宽度位置。
                        if (_refWaitAttempts < RefWaitMaxAttempts)
                        {
                            waitingRefs++;
                            ScheduleRefWait();
                            continue;
                        }
                        labelX = IndentLeft(viewLine, snapshotLine, lineStart) + ReferenceReserveWidth();
                    }

                    var label = CreateLabel(m, _lensFont);
                    Canvas.SetLeft(label, labelX);
                    Canvas.SetTop(label, viewLine.Top + 1.0);

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
            }
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
            public Brush Foreground;
        }

        /// <summary>从 CodeLens 引用文本元素抄来的字体规格，让标注与引用完全同字体同字号同颜色。</summary>
        private sealed class LensFont
        {
            public FontFamily Family;
            public double Size;
            public Brush Brush;
        }

        private LensFont _lensFont;              // 最近一次成功抄到的 CodeLens 字体（跨重画缓存）
        private DispatcherTimer _refWaitTimer;   // 等待 CodeLens 引用渲染的重试计时器
        private int _refWaitAttempts;            // 已重试次数
        private const int RefWaitMaxAttempts = 20; // 20 × 250ms ≈ 5 秒后放弃等待，退回预留宽度

        /// <summary>等待 CodeLens 引用渲染的短重试：让标注与引用同一时刻出现，而不是先 log 后引用。</summary>
        private void ScheduleRefWait()
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

        private TextBlock MakeSegment(MethodBlameView m, bool activity, string text, LensFont font)
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
            // 外面包一层圆角 Border 充当高亮框，TextBlock 仍是可命中区域。
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
            return tb;
        }

        /// <summary>悬停/选中态的浅灰高亮框颜色（半透明灰，浅色/深色主题都自然）。</summary>
        private static readonly Brush SegmentHighlightBrush =
            new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));

        /// <summary>悬停/选中态的文字颜色（VS 蓝）。</summary>
        private static readonly Brush SegmentHotBrush =
            new SolidColorBrush(Color.FromRgb(0, 120, 212));

        /// <summary>按悬停/选中状态刷新一个段的外观。</summary>
        private void SetSegmentVisual(TextBlock tb, bool hovered)
        {
            bool active = ReferenceEquals(tb, _activeSegment) && _popup.IsOpen;
            if (tb.Parent is Border host)
                host.Background = (hovered || active) ? SegmentHighlightBrush : Brushes.Transparent;
            tb.Foreground = (hovered || active) ? SegmentHotBrush : (tb.Tag as Brush ?? GrayBrush());
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
            if (m.HasLocal) return Loc.T("Not committed yet", "尚未提交");
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
                    });

                    // 深度历史：blame 只有每行最后一次修改，早期提交被覆盖；
                    // 用 svn blame -r 逐代回溯拿全量（从方法创建至今），完成后刷新弹框
                    try
                    {
                        var full = await _blame.GetMethodLogAsync(path, m.Name, m.StartLine, revisions);
                        if (full.Count > commits.Count)
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

            // 行右键菜单：只保留 View Commit Details
            var menu = new ContextMenu();
            var view = new MenuItem { Header = Loc.T("View Commit Details", "查看提交详情") };
            view.Click += (s, e) => ShowCommitDetail(c);
            menu.Items.Add(view);
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

        /// <summary>「View Commit Details」→ 单次提交的变更文件明细（svn log -v）。</summary>
        private void ShowCommitDetail(CommitInfo c)
        {
            try
            {
                // 先关掉 log 弹框：Popup 常驻顶层，会把刚打开的详情窗口遮住
                ClosePopup();
                var path = _filePath;
                int rev = c.Revision;
                Task.Run(async () =>
                {
                    var detail = await _blame.GetCommitDetailAsync(path, rev);
                    await _view.VisualElement.Dispatcher.InvokeAsync(() => ShowDetailWindow(detail));
                });
            }
            catch (Exception ex)
            {
                BlameService.Log("detail error: " + ex.Message);
            }
        }

        private void ShowDetailWindow(CommitDetail d)
        {
            try
            {
                var win = new Window
                {
                    Title = d.Commit.Revision > 0
                        ? Loc.T($"Commit r{d.Commit.Revision}", $"提交 r{d.Commit.Revision}")
                        : Loc.T("Commit", "提交"),
                    Width = 760,
                    Height = 420
                };
                var grid = new Grid { Margin = new Thickness(8) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var head = new TextBlock
                {
                    Text = $"r{d.Commit.Revision} · {d.Commit.Author} · " +
                           (d.Commit.Date == DateTime.MinValue
                               ? ""
                               : d.Commit.Date.ToLocalTime().ToString("yyyy/M/d HH:mm")),
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    Margin = new Thickness(2, 0, 0, 2)
                };
                Grid.SetRow(head, 0);
                grid.Children.Add(head);

                var msg = new TextBlock
                {
                    Text = (d.Commit.Message ?? "").Trim(),
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(2, 0, 0, 6),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetRow(msg, 1);
                grid.Children.Add(msg);

                var lv = new ListView { FontSize = 11 };
                var gv = new GridView();
                gv.Columns.Add(new GridViewColumn
                {
                    Header = Loc.T("Action", "操作"), Width = 60,
                    DisplayMemberBinding = new System.Windows.Data.Binding("Action")
                });
                gv.Columns.Add(new GridViewColumn
                {
                    Header = Loc.T("Path", "路径"), Width = 620,
                    DisplayMemberBinding = new System.Windows.Data.Binding("Path")
                });
                lv.View = gv;
                lv.ItemsSource = d.Paths.Select(p => new { p.Action, p.Path }).ToList();
                Grid.SetRow(lv, 2);
                grid.Children.Add(lv);

                win.Content = grid;
                win.Show();
            }
            catch (Exception ex)
            {
                BlameService.Log("detail window error: " + ex.Message);
            }
        }

    }
}
