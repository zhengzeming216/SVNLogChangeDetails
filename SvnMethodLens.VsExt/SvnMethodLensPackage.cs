using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace SvnMethodLens
{
    /// <summary>
    /// 最小化的 VS 包：本身不提供命令/工具窗口，仅用于让 VS 注册本扩展的 MEF 组件
    /// （装饰层）。装饰层由 MEF 自动加载，不依赖本包的具体逻辑。
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    public sealed class SvnMethodLensPackage : AsyncPackage
    {
        public const string PackageGuidString = "9f1c2b3a-4d5e-4f6a-8b1c-2d3e4f5a6b7c";

        protected override Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            return Task.CompletedTask;
        }
    }
}
