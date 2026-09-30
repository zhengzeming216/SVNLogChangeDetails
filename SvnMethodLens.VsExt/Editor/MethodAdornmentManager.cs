using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace SvnMethodLens.Editor
{
    /// <summary>
    /// 监听 C# 文档视图的创建，为每个视图挂上方法级 SVN 归属装饰层。
    /// 渲染策略（对齐 VS CodeLens 的做法）：
    ///  1) ILineTransformSource 在方法声明行上方预留一条空带（约 17px）；
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

        // 我们标注自身需要的高度
        private const double LabelBand = 17.0;

        // 让位给 VS 自带"N 个引用"（CodeLens）的高度：它的行变换与我们的是
        // Max 合并而非相加，且它贴着文字上沿绘制（TextTop - 高度），
        // 所以必须把空带加高，才能两行并存（引用在下、Svn 标注在上）。
        private double CodeLensAllowance => Math.Max(12.0, _view.LineHeight * 0.8);

        private double TotalTopSpace => LabelBand + CodeLensAllowance;

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
        private readonly System.Windows.Controls.Primitives.Popup _popup =
            new System.Windows.Controls.Primitives.Popup();

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

            ScheduleRefresh();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            // 文本变化才重算 blame；每次布局都补画视口内的标注（滚动靠这个续画）
            if (e.NewSnapshot != e.OldSnapshot)
                ScheduleRefresh();
            RedrawVisible();
        }

        private void OnClosed(object sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
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
                int added = 0, failed = 0, skipped = 0;
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

                    var label = CreateLabel(m);
                    Canvas.SetLeft(label, IndentLeft(viewLine, snapshotLine, lineStart));
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

                // 日志降噪：只在异常或每 25 次重画时输出一行
                _redrawTick++;
                if (failed > 0 || _redrawTick % 25 == 1)
                    BlameService.Log($"visible redraw: view={System.IO.Path.GetFileName(_filePath)} " +
                                     $"viewport=[{first},{last}] added={added} failed={failed} skipped={skipped}" +
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

        private UIElement CreateLabel(MethodBlameView m)
        {
            var tb = new TextBlock
            {
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
                Cursor = System.Windows.Input.Cursors.Hand,
                Text = Format(m)
            };
            // 与 VS 自带"N 个引用"（CodeLens）同款灰色，且跟随深/浅色主题
            try
            {
                tb.SetResourceReference(TextBlock.ForegroundProperty,
                    Microsoft.VisualStudio.Shell.VsBrushes.GrayTextKey);
            }
            catch
            {
                tb.Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128));
            }
            tb.MouseLeftButtonUp += (s, e) => { ShowPopup(m, tb); e.Handled = true; };
            return tb;
        }

        /// <summary>Git CodeLens 风格文案：作者，多久之前 · N 名作者，M 次更改。</summary>
        private static string Format(MethodBlameView m)
        {
            if (m.HasLocal)
                return $"本地未提交改动 · 主作者 {m.PrimaryAuthor} · {m.AuthorCount} 名作者";

            var when = m.LastDate.HasValue ? TimeAgo(m.LastDate.Value) : "";
            var head = string.IsNullOrEmpty(when) ? m.LastAuthor : $"{m.LastAuthor}，{when}";
            return $"{head} · {m.AuthorCount} 名作者，{m.ChangeCount} 次更改（r{m.LastRevision}）";
        }

        private static string TimeAgo(DateTime utc)
        {
            try
            {
                var span = DateTime.UtcNow - (utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime());
                if (span.TotalMinutes < 1) return "刚刚";
                if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
                if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
                if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
                if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} 个月前";
                return $"{(int)(span.TotalDays / 365)} 年前";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>点击标注显示该方法的提交历史（按需 svn log，带缓存）。</summary>
        private void ShowPopup(MethodBlameView m, UIElement anchor)
        {
            try
            {
                _popup.IsOpen = false;

                var panel = new StackPanel { Orientation = Orientation.Vertical };
                panel.Children.Add(new TextBlock
                {
                    Text = "正在读取提交历史…",
                    FontSize = 11,
                    Foreground = Brushes.Gray
                });
                var border = new Border
                {
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8),
                    Child = panel
                };
                try
                {
                    // 跟随 VS 主题（浅色/深色）
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
                _popup.Child = border;
                _popup.PlacementTarget = anchor;
                _popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                _popup.StaysOpen = false;
                _popup.AllowsTransparency = true;
                _popup.IsOpen = true;

                var path = _filePath;
                var revisions = m.Revisions;
                Task.Run(async () =>
                {
                    var commits = await _blame.GetCommitsAsync(path, revisions);
                    await _view.VisualElement.Dispatcher.InvokeAsync(() => FillPopup(panel, m, commits));
                });
            }
            catch (Exception ex)
            {
                BlameService.Log("popup error: " + ex.Message);
            }
        }

        private static void FillPopup(StackPanel panel, MethodBlameView m, List<CommitInfo> commits)
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(m.Name) ? "该方法" : m.Name,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4)
            });

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

            foreach (var c in commits)
            {
                var head = new TextBlock
                {
                    FontSize = 11,
                    TextWrapping = TextWrapping.NoWrap,
                    Margin = new Thickness(0, 2, 0, 0),
                    Text = $"r{c.Revision} · {c.Author} · " +
                           (c.Date == DateTime.MinValue ? "" : c.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                };
                panel.Children.Add(head);

                var msg = (c.Message ?? "").Trim();
                if (msg.Length > 0)
                {
                    var nl = msg.IndexOf('\n');
                    if (nl >= 0) msg = msg.Substring(0, nl);
                    if (msg.Length > 80) msg = msg.Substring(0, 80) + "…";
                    panel.Children.Add(new TextBlock
                    {
                        Text = msg,
                        FontSize = 11,
                        Foreground = Brushes.Gray,
                        TextWrapping = TextWrapping.NoWrap
                    });
                }
            }
        }
    }
}
