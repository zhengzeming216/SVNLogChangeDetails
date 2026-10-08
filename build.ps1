# 构建并打包 SVNLogChangeDetails VSIX（修复"日志弹框飘走"后使用）
# 用法：右键"使用 PowerShell 运行"，或在 VS 开发人员 PowerShell 中执行
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Push-Location $root

# 1) 找到 dotnet
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "找不到 dotnet，请先安装 .NET SDK 或 VS。" }
Write-Host "使用 dotnet: $dotnet"

# 2) 编译（生成 bin/Release/net48 下的 DLL / pkgdef / files.json）
$vsixProj = Join-Path $root 'SvnMethodLens.VsExt\SvnMethodLens.VsExt.csproj'
Write-Host "==> dotnet build -c Release"
& $dotnet build $vsixProj -c Release
if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }

# 3) 找到 VsixUtil.exe
$vssdkRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.vssdk.buildtools'
$vsixUtil = Get-ChildItem -Path $vssdkRoot -Recurse -Filter VsixUtil.exe -ErrorAction SilentlyContinue |
    Sort-Object FullName | Select-Object -Last 1
if (-not $vsixUtil) { throw "找不到 VsixUtil.exe（需安装 Microsoft.VSSDK.BuildTools）。" }

$outDir      = Join-Path $root 'SvnMethodLens.VsExt\bin\Release\net48'
$filesJson  = Join-Path $outDir 'files.json'
$manifest   = Join-Path $root 'SvnMethodLens.VsExt\source.extension.vsixmanifest'
$dist       = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$vsix       = Join-Path $dist 'SVNLogChangeDetails-1.1.1.vsix'

# 4) 打包。优先用 files.json（VSSDK.BuildTools 生成）；若构建已直接产出 vsix 则直接复制。
if (Test-Path $filesJson) {
    Write-Host "==> VsixUtil package -> $vsix"
    & $vsixUtil.FullName package -outputPath $vsix -sourceManifest $manifest `
        -files $filesJson -installationFolder SvnMethodLens -noValidate
    if ($LASTEXITCODE -ne 0) { throw "打包失败 (exit $LASTEXITCODE)" }
} else {
    $built = Get-ChildItem -Path $outDir -Filter '*.vsix' | Select-Object -First 1
    if (-not $built) { throw "未找到 files.json 也未找到生成的 vsix，请检查构建输出。" }
    Copy-Item $built.FullName $vsix -Force
    Write-Host "==> 复制已生成的 vsix -> $vsix"
}

Write-Host ""
Write-Host "完成： $vsix"
Write-Host "双击该 vsix 安装（建议先在 VS 扩展管理器中卸载旧的 SVNLogChangeDetails 1.1.0），然后重启 VS。"
Pop-Location
