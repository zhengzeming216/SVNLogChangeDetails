# SvnMethodLens 安装脚本 (VS2022 / VS2026)
# 用法:
#   .\install.ps1                      # 默认手动安装到 VS2026 实例 18.0_e5d6eb5b
#   .\install.ps1 -VsInstanceId 17.0_xxxx   # 装到指定 VS 实例
#   .\install.ps1 -UseInstaller        # 用官方 VSIXInstaller (需要以管理员身份运行)
param(
    [string]$VsixPath = "$PSScriptRoot\dist\SvnMethodLens.vsix",
    [string]$VsInstanceId = "18.0_e5d6eb5b",
    [switch]$UseInstaller
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $VsixPath)) {
    Write-Error "找不到 VSIX 包: $VsixPath"
    exit 1
}

$localApp = [Environment]::GetFolderPath("LocalApplicationData")
$extRoot  = Join-Path $localApp "Microsoft\VisualStudio\$VsInstanceId\Extensions"
$extDir   = Join-Path $extRoot "SvnMethodLens"

# 官方安装器路径 (管理员模式, 最干净)
if ($UseInstaller) {
    $installer = "D:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\VSIXInstaller.exe"
    if (-not (Test-Path $installer)) {
        $installer = "D:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\VSIXInstaller.exe"
    }
    if (-not (Test-Path $installer)) {
        Write-Error "找不到 VSIXInstaller.exe，请确认 Visual Studio 安装路径"
        exit 1
    }
    & "$installer" /instanceIds:$VsInstanceId "$VsixPath"
    exit $LASTEXITCODE
}

# ---- 手动安装 (无需管理员, 本机已验证可行) ----
# VSIX 本质是 zip；解压到扩展目录后, 修正 catalog/manifest 里的 extensionDir 为真实路径,
# 再 touch extensions.configurationchanged 让 VS 重新扫描扩展。
Write-Host "手动安装到: $extDir"
New-Item -ItemType Directory -Force -Path $extDir | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($VsixPath, $extDir, $true)

# extensionDir 在 JSON 里用双反斜杠转义
$realDir = $extDir -replace '\\', '\\'
foreach ($f in @('catalog.json', 'manifest.json')) {
    $p = Join-Path $extDir $f
    if (Test-Path $p) {
        $json = Get-Content $p -Raw
        $json = $json -replace '("extensionDir":")[^"]*(")', "`$1$realDir`$2"
        [System.IO.File]::WriteAllText($p, $json)
    }
}

# 强制 VS 重新扫描扩展 (删除 .mpack 缓存 + 触发 configurationchanged)
Get-ChildItem -Path $extDir -Filter *.mpack -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
$touch = Join-Path $extRoot "extensions.configurationchanged"
New-Item -ItemType File -Force -Path $touch | Out-Null

Write-Host "完成。请重启 Visual Studio (实例 $VsInstanceId) 后生效。"
