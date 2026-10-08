# Build and package SVNLogChangeDetails VSIX
# Run: right-click -> "Run with PowerShell", or in a dev PowerShell
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Push-Location $root

# 1) locate dotnet
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "dotnet not found. Install .NET SDK or VS." }
Write-Host "Using dotnet: $dotnet"

# 2) compile (produces bin/Release/net48 DLL / pkgdef / files.json)
$vsixProj = Join-Path $root 'SvnMethodLens.VsExt\SvnMethodLens.VsExt.csproj'
Write-Host "==> dotnet build -c Release"
& $dotnet build $vsixProj -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

# 3) locate VsixUtil.exe
$vssdkRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.vssdk.buildtools'
$vsixUtil = Get-ChildItem -Path $vssdkRoot -Recurse -Filter VsixUtil.exe -ErrorAction SilentlyContinue |
    Sort-Object FullName | Select-Object -Last 1
if (-not $vsixUtil) { throw "VsixUtil.exe not found (need Microsoft.VSSDK.BuildTools)." }

$outDir     = Join-Path $root 'SvnMethodLens.VsExt\bin\Release\net48'
$filesJson = Join-Path $outDir 'files.json'
$manifest  = Join-Path $root 'SvnMethodLens.VsExt\source.extension.vsixmanifest'
$dist      = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$vsix      = Join-Path $dist 'SVNLogChangeDetails-1.6.0.vsix'

# 4) package
if (Test-Path $filesJson) {
    Write-Host "==> VsixUtil package -> $vsix"
    & $vsixUtil.FullName package -outputPath $vsix -sourceManifest $manifest `
        -files $filesJson -installationFolder SvnMethodLens -noValidate
    if ($LASTEXITCODE -ne 0) { throw "package failed (exit $LASTEXITCODE)" }
} else {
    $built = Get-ChildItem -Path $outDir -Filter '*.vsix' | Select-Object -First 1
    if (-not $built) { throw "No files.json and no generated vsix found." }
    Copy-Item $built.FullName $vsix -Force
    Write-Host "==> copied generated vsix -> $vsix"
}

Write-Host ""
Write-Host "Done: $vsix"
Write-Host "Install it (recommended: uninstall old SVN Log Change Details first in VS Extension Manager), then restart VS."
Pop-Location
