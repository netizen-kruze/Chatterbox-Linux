#Requires -Version 5.1
# Cross-build the Chatterbox Linux release FROM WINDOWS: one self-contained
# linux-x64 file (no Linux toolchain needed) — the .NET runtime, the
# interface and the speech natives are all inside it — written to
# releases\Chatterbox-<version>-linux-x64. The binary installs itself into
# the app grid (--install), so nothing else ships beside it. Linux never
# keeps an executable bit on a downloaded file, so the README's chmod step
# applies to every download regardless of where it was built.
[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\Chatterbox\Chatterbox.csproj'
[xml]$x = Get-Content $proj
$ver = ($x.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $ver) { throw 'No <Version> in the csproj.' }
$out = Join-Path $PSScriptRoot 'publish'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
Write-Host "==> Publishing Chatterbox $ver ($Configuration, linux-x64, self-contained, single file)" -ForegroundColor Cyan
dotnet publish $proj -c $Configuration -r linux-x64 --self-contained -o $out -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$releases = Join-Path $PSScriptRoot 'releases'
New-Item -ItemType Directory -Force $releases | Out-Null
$file = Join-Path $releases "Chatterbox-$ver-linux-x64"
Copy-Item (Join-Path $out 'Chatterbox') $file -Force
$mb = [math]::Round((Get-Item $file).Length / 1MB, 1)
$sha = (Get-FileHash $file -Algorithm SHA256).Hash
Write-Host "==> Done: $file ($mb MB)" -ForegroundColor Green
Write-Host "    SHA-256: $sha"
