using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace SvnMethodLens.Editor
{
    [Guid("A597627C-60AF-49D1-87C8-F1A162267CA1")]
    internal sealed class CommitReviewToolWindow : ToolWindowPane
    {
        private readonly TextBlock _revision;
        private readonly TextBlock _commitLine;
        private readonly TextBlock _avatar;
        private readonly TextBlock _message;
        private readonly TextBlock _author;
        private readonly TextBlock _date;
        private readonly TextBlock _count;
        private readonly TextBlock _statusLeft;
        private readonly TreeView _tree;
        private readonly TextBox _filter;
        private readonly Button _filterToggle;
        private readonly Button _collapseToggle;
        private readonly Grid _filterHost;
        private readonly Border _status;
        private readonly TextBlock _changesChevron;
        private readonly Dictionary<string, TreeViewItem> _folders = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ChangedPath, TreeViewItem> _fileNodes = new Dictionary<ChangedPath, TreeViewItem>();
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<TreeViewItem> _roots = new List<TreeViewItem>();
        private Action<ChangedPath> _onOpen;
        private CommitDetail _detail;
        private ChangedPath _initialPath;
        private bool _suppressSelection;
        private bool _isFiltering;

        public CommitReviewToolWindow() : base(null)
        {
            Caption = Loc.T("SVN Commit", "SVN 提交");
            var root = new Grid { Background = SystemColors.ControlBrush };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 标题栏：与设计稿一致 —— 左侧 "SVN" 标记 + "Commit rXXXX"
            var title = new Border
            {
                Height = 31,
                Background = SystemColors.ControlBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = SystemColors.ControlDarkBrush,
                Padding = new Thickness(10, 0, 8, 0)
            };
            var titlePanel = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            var titleMark = new TextBlock { Text = "SVN", Foreground = new SolidColorBrush(Color.FromRgb(89, 101, 116)), FontSize = 11, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(titleMark, Dock.Left);
            titlePanel.Children.Add(titleMark);
            _commitLine = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            titlePanel.Children.Add(_commitLine);
            title.Child = titlePanel;
            root.Children.Add(title);

            // 提交元信息：ID 行（含 Explain/更多）、Message 标签、说明正文、作者头像行
            var metadata = new StackPanel { Margin = new Thickness(12, 8, 12, 9) };

            var idRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            var idActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var explain = ActionButton(Loc.T("Explain", "说明"), Loc.T("Explain this revision", "说明此修订"));
            explain.Click += (s, e) => ShowInfo(Loc.T("Revision details", "修订详情"), _detail?.Commit?.Message ?? "");
            idActions.Children.Add(explain);
            var more = ActionButton("⋯", Loc.T("More revision actions", "更多操作"));
            more.Width = 26;
            more.Click += (s, e) => ShowRevisionMenu(more);
            idActions.Children.Add(more);
            DockPanel.SetDock(idActions, Dock.Right);
            idRow.Children.Add(idActions);

            var idInline = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            idInline.Children.Add(new TextBlock { Text = "ID:", Foreground = SystemColors.GrayTextBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _revision = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            idInline.Children.Add(_revision);
            idRow.Children.Add(idInline);
            metadata.Children.Add(idRow);

            metadata.Children.Add(new TextBlock { Text = Loc.T("Message", "提交说明"), Foreground = SystemColors.GrayTextBrush, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            _message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7), MaxHeight = 62, TextTrimming = TextTrimming.CharacterEllipsis };
            metadata.Children.Add(_message);

            var byline = new DockPanel { LastChildFill = true };
            var avatarHost = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromRgb(221, 233, 246)),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _avatar = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(23, 101, 163)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            avatarHost.Child = _avatar;
            DockPanel.SetDock(avatarHost, Dock.Left);
            byline.Children.Add(avatarHost);
            _author = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            _date = new TextBlock { Foreground = SystemColors.GrayTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            var dot = new TextBlock { Text = "·", Foreground = SystemColors.GrayTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            var authorHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            authorHost.Children.Add(_author);
            authorHost.Children.Add(dot);
            authorHost.Children.Add(_date);
            byline.Children.Add(authorHost);
            metadata.Children.Add(byline);

            Grid.SetRow(metadata, 1);
            root.Children.Add(metadata);
            root.Children.Add(new Border { Height = 1, Background = SystemColors.ControlDarkBrush, Opacity = .25 });
            Grid.SetRow(root.Children[root.Children.Count - 1], 2);

            // Changes 工具栏：chevron + Changes + 计数，右侧折叠/筛选图标按钮
            var toolbar = new DockPanel { Margin = new Thickness(6, 3, 6, 2), Height = 26, LastChildFill = true };
            _filterToggle = IconButton(Glyph.Filter, Loc.T("Filter changes", "筛选变更"));
            _filterToggle.Click += (s, e) => ToggleFilter();
            DockPanel.SetDock(_filterToggle, Dock.Right);
            toolbar.Children.Add(_filterToggle);
            _collapseToggle = IconButton(Glyph.ChevronUp, Loc.T("Collapse all folders", "折叠所有目录"));
            _collapseToggle.Click += (s, e) => ToggleAllFolders();
            DockPanel.SetDock(_collapseToggle, Dock.Right);
            toolbar.Children.Add(_collapseToggle);

            var changesHeader = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            var changesToggle = new Button
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            var toggleContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _changesChevron = new TextBlock
            {
                Text = Glyph.ChevronDown,
                FontFamily = GlyphFont,
                FontSize = 9,
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 5, 0)
            };
            toggleContent.Children.Add(_changesChevron);
            toggleContent.Children.Add(new TextBlock { Text = Loc.T("Changes", "变更"), FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            changesToggle.Content = toggleContent;
            changesToggle.Click += (s, e) => ToggleTree();
            changesHeader.Children.Add(changesToggle);
            _count = new TextBlock { Foreground = SystemColors.GrayTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
            changesHeader.Children.Add(_count);
            toolbar.Children.Add(changesHeader);
            Grid.SetRow(toolbar, 3);
            root.Children.Add(toolbar);

            _filterHost = new Grid { Margin = new Thickness(8, 2, 8, 4), Visibility = Visibility.Collapsed };
            _filterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _filterHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _filter = new TextBox { Height = 24, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Filter files by name or path", "按文件名或路径筛选") };
            _filter.TextChanged += (s, e) => ApplyFilter();
            _filterHost.Children.Add(_filter);
            var clear = ActionButton("×", Loc.T("Clear filter", "清除筛选"));
            clear.Width = 26;
            clear.Click += (s, e) => _filter.Clear();
            Grid.SetColumn(clear, 1);
            _filterHost.Children.Add(clear);
            Grid.SetRow(_filterHost, 4);
            root.Children.Add(_filterHost);

            _tree = new TreeView { BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(4, 0, 4, 0), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
            _tree.ItemContainerStyle = CreateTreeItemStyle();
            _tree.SelectedItemChanged += OnTreeSelected;
            _tree.SizeChanged += (s, e) => FitRowWidths();
            _tree.Loaded += (s, e) => FitRowWidths();
            Grid.SetRow(_tree, 5);
            root.Children.Add(_tree);

            // 状态栏：左 "N files changed"，右 "✓ Committed · Working copy unchanged"
            _status = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = SystemColors.ControlDarkBrush, Padding = new Thickness(10, 4, 10, 4) };
            var statusPanel = new DockPanel { LastChildFill = true };
            var statusRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            statusRight.Children.Add(new TextBlock
            {
                Text = Glyph.Check,
                FontFamily = GlyphFont,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(32, 115, 60)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            statusRight.Children.Add(new TextBlock { Text = Loc.T("Committed", "已提交"), Foreground = SystemColors.GrayTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            statusRight.Children.Add(new TextBlock { Text = "·", Foreground = SystemColors.GrayTextBrush, FontSize = 11, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            statusRight.Children.Add(new TextBlock { Text = Loc.T("Working copy unchanged", "工作副本未更改"), Foreground = SystemColors.GrayTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(statusRight, Dock.Right);
            statusPanel.Children.Add(statusRight);
            _statusLeft = new TextBlock { Foreground = SystemColors.GrayTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            statusPanel.Children.Add(_statusLeft);
            _status.Child = statusPanel;
            Grid.SetRow(_status, 6);
            root.Children.Add(_status);
            Content = root;
        }

        public void ShowCommit(CommitDetail detail, Action<ChangedPath> onOpen, ChangedPath initialPath = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _detail = detail ?? new CommitDetail();
            _onOpen = onOpen;
            _initialPath = initialPath;
            Caption = Loc.T($"SVN Commit r{_detail.Commit.Revision}", $"SVN 提交 r{_detail.Commit.Revision}");
            _commitLine.Text = "Commit r" + _detail.Commit.Revision;
            _revision.Text = "r" + _detail.Commit.Revision;
            _message.Text = (_detail.Commit.Message ?? "").Trim();
            _author.Text = _detail.Commit.Author ?? "";
            _avatar.Text = Initial(_detail.Commit.Author);
            _date.Text = _detail.Commit.Date == DateTime.MinValue ? "" : _detail.Commit.Date.ToLocalTime().ToString("yyyy/M/d HH:mm");
            // 目录条目（kind=dir）没有可比较内容，不计入文件数
            var fileCount = (_detail.Paths ?? new List<ChangedPath>()).Count(p => !p.IsDirectory);
            _count.Text = fileCount + " " + Loc.T("files", "个文件");
            _statusLeft.Text = Loc.T(fileCount + " files changed", fileCount + " 个文件已更改");
            BuildTree();
            if (_initialPath != null && _fileNodes.TryGetValue(_initialPath, out var initialNode))
            {
                _suppressSelection = true;
                initialNode.IsSelected = true;
                initialNode.BringIntoView();
                _suppressSelection = false;
                _onOpen?.Invoke(_initialPath);
            }
        }

        private void BuildTree()
        {
            _tree.Items.Clear();
            _folders.Clear();
            _fileNodes.Clear();
            _roots.Clear();

            var paths = _detail.Paths ?? new List<ChangedPath>();
            var directoryPrefixes = CollectDirectoryPrefixes(paths);
            foreach (var changed in paths)
            {
                var parts = (changed.Path ?? "").Replace('\\', '/')
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                // 目录条目（svn log -v kind="dir"）只是路径节点：没有可比较内容，
                // 因此不显示 M/A/D，也不能双击打开 diff。
                if (IsDirectoryEntry(changed, directoryPrefixes))
                {
                    EnsureFolder(parts, parts.Length);
                    continue;
                }

                var parent = EnsureFolder(parts, parts.Length - 1);
                var leaf = CreateNode(parts[parts.Length - 1], false, changed, changed.Path ?? "");
                _fileNodes[changed] = leaf;
                AddTreeNode(parent, leaf);
            }

            if (_fileNodes.Count == 0)
                _tree.Items.Add(new TreeViewItem { Header = Loc.T("No changed files", "没有变更文件"), IsEnabled = false });
            foreach (var node in _folders.Values) node.IsExpanded = !_collapsed.Contains((string)node.Tag);
            ApplyFilter();
            _tree.Dispatcher.BeginInvoke(new Action(FitRowWidths), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>所有文件变更的父目录前缀（"/a/b" 形式），用于识别未带 kind 的目录条目。</summary>
        private static HashSet<string> CollectDirectoryPrefixes(List<ChangedPath> paths)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var changed in paths)
            {
                var parts = (changed.Path ?? "").Replace('\\', '/')
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var prefix = "";
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    prefix += "/" + parts[i];
                    set.Add(prefix);
                }
            }
            return set;
        }

        /// <summary>目录条目：kind=dir，或该路径是其他变更路径的父目录（兼容未返回 kind 的旧服务端）。</summary>
        private static bool IsDirectoryEntry(ChangedPath changed, HashSet<string> directoryPrefixes)
        {
            if (changed.IsDirectory) return true;
            var self = (changed.Path ?? "").Replace('\\', '/').Trim('/');
            return self.Length > 0 && directoryPrefixes.Contains("/" + self);
        }

        private TreeViewItem EnsureFolder(string[] parts, int depth)
        {
            TreeViewItem parent = null;
            var prefix = "";
            for (int i = 0; i < depth; i++)
            {
                prefix += "/" + parts[i];
                if (!_folders.TryGetValue(prefix, out var folder))
                {
                    folder = CreateNode(parts[i], true, null, prefix);
                    _folders[prefix] = folder;
                    AddTreeNode(parent, folder);
                }
                parent = folder;
            }
            return parent;
        }

        private void AddTreeNode(TreeViewItem parent, TreeViewItem node)
        {
            if (parent != null)
            {
                parent.Items.Add(node);
            }
            else
            {
                _roots.Add(node);
                _tree.Items.Add(node);
            }
        }

        private static Style CreateTreeItemStyle()
        {
            var style = new Style(typeof(TreeViewItem));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            return style;
        }

        /// <summary>
        /// 默认 TreeViewItem 把 Header 放在 Auto 宽度的列里，Header 不会撑满整行，
        /// 状态字母因此只会紧跟文件名。这里按层级显式给行内容分配宽度，
        /// 让 M/A/D 始终贴住窗格最右侧。
        /// </summary>
        private void FitRowWidths()
        {
            var available = _tree.ActualWidth;
            if (available <= 0) return;
            foreach (var root in _roots) FitRowWidth(root, 0, available);
        }

        private static void FitRowWidth(TreeViewItem node, int depth, double available)
        {
            if (node.Header is Grid row)
                row.Width = Math.Max(60, available - 12 - DpiTrim - IndentPerLevel * depth);
            foreach (var item in node.Items)
                if (item is TreeViewItem child) FitRowWidth(child, depth + 1, available);
        }

        private const double IndentPerLevel = 15.0;
        /// <summary>树左边距 + 可能出现的垂直滚动条。</summary>
        private const double DpiTrim = 22.0;

        private TreeViewItem CreateNode(string name, bool folder, ChangedPath changed, string key)
        {
            var row = new Grid { MinHeight = 27, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

            var icon = new TextBlock
            {
                Text = folder ? Glyph.Folder : Glyph.File,
                FontFamily = GlyphFont,
                FontSize = folder ? 14 : 13,
                Foreground = folder ? new SolidColorBrush(Color.FromRgb(190, 145, 45)) : SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            row.Children.Add(icon);

            var badge = FileBadge(name, folder);
            if (badge != null)
            {
                Grid.SetColumn(badge, 1);
                row.Children.Add(badge);
            }

            var label = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = changed?.Path ?? key,
                Margin = new Thickness(3, 0, 4, 0)
            };
            Grid.SetColumn(label, 2);
            row.Children.Add(label);

            // 只有文件变更才有 M/A/D；目录只是路径容器，不显示任何状态。
            if (changed != null && !changed.IsDirectory)
            {
                var state = new TextBlock
                {
                    Text = (changed.Action ?? "M").ToUpperInvariant(),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    TextAlignment = TextAlignment.Right
                };
                state.Foreground = ActionBrush(changed.Action);
                Grid.SetColumn(state, 3);
                row.Children.Add(state);
            }
            var node = new TreeViewItem { Header = row, Tag = key, IsExpanded = folder, Padding = new Thickness(1, 0, 1, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            node.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromArgb(42, 23, 101, 163));
            node.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(Color.FromArgb(30, 23, 101, 163));
            node.ToolTip = changed?.Path ?? key;
            if (changed != null)
            {
                node.Tag = changed;
                node.MouseDoubleClick += (s, e) => { e.Handled = true; _onOpen?.Invoke(changed); };
                node.ContextMenu = null;
            }
            else if (folder)
            {
                node.Expanded += (s, e) => _collapsed.Remove(key);
                node.Collapsed += (s, e) => _collapsed.Add(key);
                node.ContextMenu = null;
            }
            return node;
        }

        private ContextMenu BuildFileMenu(ChangedPath path)
        {
            var menu = new ContextMenu();
            AddMenu(menu, Loc.T("Open", "打开"), () => _onOpen?.Invoke(path));
            AddMenu(menu, Loc.T("Show Differences", "显示差异"), () => _onOpen?.Invoke(path));
            menu.Items.Add(new Separator());
            AddMenu(menu, Loc.T("Show Log…", "查看日志…"), () => ShowInfo(Loc.T("Show Log", "查看日志"), path.Path));
            AddMenu(menu, Loc.T("Update (this file)", "更新此文件"), () => ShowInfo(Loc.T("Update", "更新"), Loc.T("Use the SVN update command for this path.", "请对该路径执行 SVN 更新命令。")));
            AddMenu(menu, Loc.T("Commit… (1 file)", "提交…（1 个文件）"), () => ShowInfo(Loc.T("Commit", "提交"), Loc.T("This historical revision is read-only in the review pane.", "历史修订审查窗格为只读，不会直接提交。")));
            AddMenu(menu, Loc.T("Revert Changes…", "还原更改…"), () => ShowInfo(Loc.T("Revert", "还原"), Loc.T("This action is not available from a historical commit review.", "历史提交审查中不执行还原操作。")));
            menu.Items.Add(new Separator());
            AddMenu(menu, Loc.T("Reveal in Explorer", "在资源管理器中显示"), () => RevealPath(path));
            return menu;
        }

        private ContextMenu BuildFolderMenu(TreeViewItem folder)
        {
            var menu = new ContextMenu();
            AddMenu(menu, Loc.T("Show Log…", "查看日志…"), () => ShowInfo(Loc.T("Show Log", "查看日志"), folder.ToolTip?.ToString() ?? ""));
            AddMenu(menu, Loc.T("Update", "更新"), () => ShowInfo(Loc.T("Update", "更新"), Loc.T("Use the SVN update command for this folder.", "请对该目录执行 SVN 更新命令。")));
            AddMenu(menu, Loc.T("Commit…", "提交…"), () => ShowInfo(Loc.T("Commit", "提交"), Loc.T("Historical commit details are read-only.", "历史提交详情为只读。")));
            menu.Items.Add(new Separator());
            AddMenu(menu, Loc.T("Reveal in Explorer", "在资源管理器中显示"), () => ShowInfo(Loc.T("Reveal", "定位"), folder.ToolTip?.ToString() ?? ""));
            return menu;
        }

        private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_suppressSelection) return;
            var node = e.NewValue as TreeViewItem;
            if (node?.Tag is ChangedPath path) _onOpen?.Invoke(path);
        }

        private void ApplyFilter()
        {
            var query = _filter.Text?.Trim() ?? "";
            _isFiltering = query.Length > 0;
            foreach (var node in _fileNodes)
            {
                var match = _isFiltering && ((node.Key.Path ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                SetVisible(node.Value, !_isFiltering || match);
                if (match)
                {
                    var parent = ItemsControl.ItemsControlFromItemContainer(node.Value);
                    while (parent is TreeViewItem parentNode)
                    {
                        parentNode.IsExpanded = true;
                        parent = ItemsControl.ItemsControlFromItemContainer(parentNode);
                    }
                }
            }
            foreach (var root in _roots)
            {
                if (!(root.Tag is ChangedPath))
                    SetVisible(root, !_isFiltering || HasVisibleDescendant(root));
            }
        }

        private static bool HasVisibleDescendant(TreeViewItem node)
        {
            foreach (var item in node.Items)
            {
                var child = item as TreeViewItem;
                if (child == null) continue;
                if (child.Visibility == Visibility.Visible) return true;
                if (HasVisibleDescendant(child)) return true;
            }
            return false;
        }

        private static void SetVisible(TreeViewItem node, bool visible) => node.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        private void ToggleFilter()
        {
            var show = _filterHost.Visibility != Visibility.Visible;
            _filterHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) _filter.Focus();
            else { _filter.Clear(); _tree.Focus(); }
        }

        private void ToggleAllFolders()
        {
            var expand = _collapsed.Count > 0;
            foreach (var pair in _folders)
            {
                pair.Value.IsExpanded = expand;
                if (expand) _collapsed.Remove(pair.Key);
                else _collapsed.Add(pair.Key);
            }
            _collapseToggle.ToolTip = expand ? Loc.T("Collapse all folders", "折叠所有目录") : Loc.T("Expand all folders", "展开所有目录");
        }

        private void ToggleTree()
        {
            var expand = _tree.Visibility != Visibility.Visible;
            _tree.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            _changesChevron.Text = expand ? Glyph.ChevronDown : Glyph.ChevronRight;
        }

        private void ShowRevisionMenu(FrameworkElement owner)
        {
            var menu = new ContextMenu();
            AddMenu(menu, Loc.T("Copy revision ID", "复制修订号"), () => Clipboard.SetText("r" + (_detail?.Commit?.Revision ?? 0)));
            AddMenu(menu, Loc.T("Explain revision", "说明此修订"), () => ShowInfo(Loc.T("Revision details", "修订详情"), _detail?.Commit?.Message ?? ""));
            owner.ContextMenu = menu;
            menu.PlacementTarget = owner;
            menu.IsOpen = true;
        }

        private void RevealPath(ChangedPath path)
        {
            try
            {
                var full = path.Path;
                if (!System.IO.Path.IsPathRooted(full)) return;
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + full + "\"");
            }
            catch { }
        }

        private void ShowInfo(string title, string message)
        {
            MessageBox.Show(message ?? "", title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static Button ActionButton(string text, string tooltip)
        {
            return new Button { Content = text, ToolTip = tooltip, Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(1), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(23, 101, 163)), FontSize = 11, MinHeight = 23 };
        }

        private static void AddMenu(ContextMenu menu, string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => action?.Invoke();
            menu.Items.Add(item);
        }

        /// <summary>文件类型小徽标（C# / JS），设计稿中位于文件图标之后。</summary>
        private static Border FileBadge(string name, bool folder)
        {
            string text;
            Brush background;
            if (folder) return null;
            var ext = System.IO.Path.GetExtension(name ?? "").ToLowerInvariant();
            if (ext == ".cs")
            {
                text = "C#";
                background = new SolidColorBrush(Color.FromRgb(32, 115, 60));
            }
            else if (ext == ".js" || ext == ".jsx" || ext == ".mjs")
            {
                text = "JS";
                background = new SolidColorBrush(Color.FromRgb(155, 93, 5));
            }
            else
            {
                return null;
            }
            return new Border
            {
                Background = background,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 8.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White
                }
            };
        }

        /// <summary>作者名首字母，用于圆形头像。</summary>
        private static string Initial(string author)
        {
            var name = (author ?? "").Trim();
            return name.Length == 0 ? "?" : name.Substring(0, 1).ToUpperInvariant();
        }

        /// <summary>带 MDL2 字体图标的工具栏按钮。</summary>
        private static Button IconButton(string glyph, string tooltip)
        {
            return new Button
            {
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = GlyphFont,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(89, 101, 116)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Width = 24,
                Height = 22,
                Margin = new Thickness(1, 0, 0, 0),
                Padding = new Thickness(0),
                ToolTip = tooltip,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
        }

        private static readonly FontFamily GlyphFont = new FontFamily("Segoe MDL2 Assets");

        /// <summary>VS/Windows 自带 Segoe MDL2 Assets 的字形编码。</summary>
        private static class Glyph
        {
            public const string Folder = "\uE8B7";
            public const string File = "\uE7C3";
            public const string ChevronDown = "\uE70D";
            public const string ChevronRight = "\uE76C";
            public const string ChevronUp = "\uE70E";
            public const string Filter = "\uE71C";
            public const string Check = "\uE73E";
        }

        private static Brush ActionBrush(string action)
        {
            switch ((action ?? "M").ToUpperInvariant())
            {
                case "A": return new SolidColorBrush(Color.FromRgb(32, 115, 60));
                case "D": return new SolidColorBrush(Color.FromRgb(174, 55, 49));
                case "R": return new SolidColorBrush(Color.FromRgb(155, 93, 5));
                default: return new SolidColorBrush(Color.FromRgb(155, 93, 5));
            }
        }
    }
}
