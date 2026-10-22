using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SvnMethodLens
{
    /// <summary>
    /// 最小化的 VS 包：本身不提供命令/工具窗口，仅用于让 VS 注册本扩展的 MEF 组件
    /// （装饰层）。装饰层由 MEF 自动加载，不依赖本包的具体逻辑。
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideToolWindow(typeof(SvnMethodLens.Editor.CommitReviewToolWindow),
        Style = VsDockStyle.Tabbed,
        Window = Microsoft.VisualStudio.Shell.Interop.ToolWindowGuids80.SolutionExplorer,
        Orientation = ToolWindowOrientation.Right,
        DockedWidth = 360)]
    [Guid(PackageGuidString)]
    public sealed class SvnMethodLensPackage : AsyncPackage
    {
        public const string PackageGuidString = "9f1c2b3a-4d5e-4f6a-8b1c-2d3e4f5a6b7c";

        /// <summary>包实例（v1.11.3）：文档页窗格需要包作为 OLE IServiceProvider 站点。</summary>
        public static SvnMethodLensPackage Instance { get; private set; }

        private uint _rdtEventsCookie;
        private DiffDocumentCloseListener _diffCloseListener;
        internal int _diffSessionGeneration;
        internal readonly System.Collections.Generic.HashSet<string> _expectedDiffDocuments = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly System.Collections.Generic.HashSet<string> _observedDiffDocuments = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly System.Collections.Generic.HashSet<string> _openDiffDocuments = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool _diffReviewActive;
        internal bool _diffReviewHasOpenedDocument;
        internal bool _diffReviewAutoCloseEnabled;
        private IVsWindowFrame _activeDiffFrame;
        internal bool _diffReplacementPending;
        private int _diffOpenRequestGeneration;

        internal int BeginDiffOpenRequest(int session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return session == _diffSessionGeneration ? ++_diffOpenRequestGeneration : -1;
        }

        internal bool IsCurrentDiffOpenRequest(int session, int request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return session == _diffSessionGeneration && request == _diffOpenRequestGeneration;
        }

        /// <summary>Replace the current comparison instead of accumulating one document tab per changed file.</summary>
        internal void PrepareDiffReplacement(int session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (session != _diffSessionGeneration || !_diffReviewActive) return;

            // Forget the previous pair before closing it. Its asynchronous RDT unlock events
            // must not affect the replacement pair's close decision.
            _expectedDiffDocuments.Clear();
            _observedDiffDocuments.Clear();
            _openDiffDocuments.Clear();
            _diffReplacementPending = true;
            SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW REPLACE session={session}; closePrevious={_activeDiffFrame != null}");
            var previous = _activeDiffFrame;
            _activeDiffFrame = null;
            if (previous != null)
            {
                try
                {
                    var hr = previous.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                    SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW REPLACE-CLOSE session={session}; hr=0x{hr:X8}");
                }
                catch (Exception ex)
                {
                    SvnMethodLens.Editor.BlameService.Log("DIFFREVIEW REPLACE-CLOSE-ERROR " + ex.Message);
                }
            }
        }

        internal void SetActiveDiffFrame(int session, IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (session == _diffSessionGeneration)
            {
                _activeDiffFrame = frame;
                _diffReplacementPending = false;
            }
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Instance = this;
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var rdt = GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (rdt != null)
            {
                _diffCloseListener = new DiffDocumentCloseListener(this, rdt);
                var adviseHr = rdt.AdviseRunningDocTableEvents(_diffCloseListener, out _rdtEventsCookie);
                if (adviseHr < 0) Marshal.ThrowExceptionForHR(adviseHr);
            }
        }

        internal int BeginDiffReviewSession()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var session = ++_diffSessionGeneration;
            _expectedDiffDocuments.Clear();
            _observedDiffDocuments.Clear();
            _openDiffDocuments.Clear();
            _diffReviewActive = true;
            _diffReviewHasOpenedDocument = false;
            _diffReviewAutoCloseEnabled = true;
            _activeDiffFrame = null;
            _diffReplacementPending = false;
            _diffOpenRequestGeneration = 0;
            SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW BEGIN session={session}; autoClose=true");
            return session;
        }

        internal void RegisterExpectedDiffDocument(int session, string leftFile, string rightFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (session != _diffSessionGeneration) return;
            if (!string.IsNullOrEmpty(leftFile)) _expectedDiffDocuments.Add(NormalizeMoniker(leftFile));
            if (!string.IsNullOrEmpty(rightFile)) _expectedDiffDocuments.Add(NormalizeMoniker(rightFile));
            SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW EXPECTED session={session}; left={NormalizeMoniker(leftFile ?? "")}; right={NormalizeMoniker(rightFile ?? "")}; expected={_expectedDiffDocuments.Count}");
        }

        internal async Task CloseCommitReviewAsync(int session)
        {
            await Task.Delay(500);
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (session != _diffSessionGeneration || !_diffReviewActive || _diffReplacementPending || !_diffReviewHasOpenedDocument || _openDiffDocuments.Count != 0)
            {
                SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW CLOSE-CHECK session={session}; current={_diffSessionGeneration}; active={_diffReviewActive}; opened={_diffReviewHasOpenedDocument}; open={_openDiffDocuments.Count}; expected={_expectedDiffDocuments.Count}; autoClose={_diffReviewAutoCloseEnabled}; result=keep");
                return;
            }
            if (!_diffReviewAutoCloseEnabled)
            {
                SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW CLOSE-CHECK session={session}; all tracked diffs closed; autoClose disabled during diagnosis; result=keep");
                return;
            }
            _diffReviewActive = false;
            SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW HIDE session={session}; reason=all tracked diff documents closed");
            var pane = FindToolWindow(typeof(SvnMethodLens.Editor.CommitReviewToolWindow), 0, false);
            var frame = pane?.Frame as IVsWindowFrame;
            if (frame != null)
            {
                var hr = frame.Hide();
                SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW HIDE-RESULT session={session}; hr=0x{hr:X8}");
            }
            else
            {
                SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW HIDE-SKIPPED session={session}; pane/frame missing");
            }
        }

        internal static string NormalizeMoniker(string path)
        {
            try { return System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar); }
            catch { return path ?? ""; }
        }

        public async Task ShowCommitReviewAsync(SvnMethodLens.Editor.CommitDetail detail, Action<SvnMethodLens.Editor.ChangedPath> onOpen, SvnMethodLens.Editor.ChangedPath initialPath = null)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var pane = await ShowToolWindowAsync(typeof(SvnMethodLens.Editor.CommitReviewToolWindow), 0, true, CancellationToken.None);
            if (pane == null) throw new InvalidOperationException("无法创建 SVN 提交审查工具窗口。");
            ((SvnMethodLens.Editor.CommitReviewToolWindow)pane).ShowCommit(detail, onOpen, initialPath);
        }
    }

    internal sealed class DiffDocumentCloseListener : IVsRunningDocTableEvents
    {
        private readonly SvnMethodLensPackage _package;
        private readonly IVsRunningDocumentTable _rdt;

        public DiffDocumentCloseListener(SvnMethodLensPackage package, IVsRunningDocumentTable rdt)
        {
            _package = package;
            _rdt = rdt;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint lockType, uint readLocksRemaining, uint editLocksRemaining)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                uint flags, readLocks, editLocks;
                string moniker;
                IVsHierarchy hierarchy;
                uint itemId;
                IntPtr docData;
                if (_rdt.GetDocumentInfo(docCookie, out flags, out readLocks, out editLocks, out moniker, out hierarchy, out itemId, out docData) >= 0 &&
                    !string.IsNullOrEmpty(moniker))
                {
                    var key = SvnMethodLensPackage.NormalizeMoniker(moniker);
                    SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW RDT-UNLOCK cookie={docCookie}; lockType={lockType}; readRemaining={readLocksRemaining}; editRemaining={editLocksRemaining}; moniker={key}; observed={_package._observedDiffDocuments.Contains(key)}; active={_package._diffReviewActive}");
                    if (readLocksRemaining == 0 && editLocksRemaining == 0 && _package._observedDiffDocuments.Remove(key))
                    {
                        _package._openDiffDocuments.Remove(key);
                        SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW RDT-DOCUMENT-CLOSED cookie={docCookie}; moniker={key}; open={_package._openDiffDocuments.Count}; expected={_package._expectedDiffDocuments.Count}");
                        if (_package._diffReviewActive && _package._diffReviewHasOpenedDocument &&
                            _package._openDiffDocuments.Count == 0 && _package._expectedDiffDocuments.Count == 0)
                        {
                            var session = _package._diffSessionGeneration;
                            _ = _package.JoinableTaskFactory.RunAsync(async () => await _package.CloseCommitReviewAsync(session));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SvnMethodLens.Editor.BlameService.Log("diff document close listener failed: " + ex.Message);
            }
            return 0;
        }

        public int OnAfterFirstDocumentLock(uint docCookie, uint lockType, uint readLocksRemaining, uint editLocksRemaining)
        {
            SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW RDT-FIRST-LOCK cookie={docCookie}; lockType={lockType}; readRemaining={readLocksRemaining}; editRemaining={editLocksRemaining}");
            TrackOpenedDiff(docCookie);
            return 0;
        }

        private void TrackOpenedDiff(uint docCookie)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                uint flags, readLocks, editLocks;
                string moniker;
                IVsHierarchy hierarchy;
                uint itemId;
                IntPtr docData;
                if (_rdt.GetDocumentInfo(docCookie, out flags, out readLocks, out editLocks, out moniker, out hierarchy, out itemId, out docData) < 0 || string.IsNullOrEmpty(moniker)) return;
                var key = SvnMethodLensPackage.NormalizeMoniker(moniker);
            if (_package._expectedDiffDocuments.Remove(key))
            {
                _package._diffReplacementPending = false;
                _package._observedDiffDocuments.Add(key);
                _package._openDiffDocuments.Add(key);
                _package._diffReviewHasOpenedDocument = true;
                SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW RDT-TRACK cookie={docCookie}; moniker={key}; source=expected; expected={_package._expectedDiffDocuments.Count}; open={_package._openDiffDocuments.Count}");
            }
            else if (_package._observedDiffDocuments.Contains(key))
            {
                _package._openDiffDocuments.Add(key);
                SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW RDT-TRACK cookie={docCookie}; moniker={key}; source=observed-again; open={_package._openDiffDocuments.Count}");
            }
            else if (_package._diffReviewActive)
            {
                SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW RDT-UNMATCHED cookie={docCookie}; moniker={key}; expected={_package._expectedDiffDocuments.Count}");
            }
            }
            catch (Exception ex)
            {
                SvnMethodLens.Editor.BlameService.Log("diff document lock listener failed: " + ex.Message);
            }
        }

        public int OnAfterSave(uint docCookie) => 0;
        public int OnAfterAttributeChange(uint docCookie, uint attributes) => 0;
        public int OnBeforeDocumentWindowShow(uint docCookie, int firstShow, IVsWindowFrame frame)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW RDT-SHOW cookie={docCookie}; firstShow={firstShow}");
                TrackOpenedDiff(docCookie);
            }
            catch (Exception ex)
            {
                SvnMethodLens.Editor.BlameService.Log("diff document show listener failed: " + ex.Message);
            }
            return 0;
        }

        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame frame)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                uint flags, readLocks, editLocks;
                string moniker;
                IVsHierarchy hierarchy;
                uint itemId;
                IntPtr docData;
                if (_rdt.GetDocumentInfo(docCookie, out flags, out readLocks, out editLocks, out moniker, out hierarchy, out itemId, out docData) >= 0)
                    SvnMethodLens.Editor.BlameService.Log($"DIFFREVIEW RDT-HIDE cookie={docCookie}; moniker={SvnMethodLensPackage.NormalizeMoniker(moniker ?? "")}; read={readLocks}; edit={editLocks}; observed={_package._observedDiffDocuments.Contains(SvnMethodLensPackage.NormalizeMoniker(moniker ?? ""))}; open={_package._openDiffDocuments.Count}");
            }
            catch (Exception ex)
            {
                SvnMethodLens.Editor.BlameService.Log("DIFFREVIEW RDT-HIDE-ERROR " + ex);
            }
            return 0;
        }
    }
}
