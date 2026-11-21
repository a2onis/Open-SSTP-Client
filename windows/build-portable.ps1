#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the portable, self-contained Open SSTP Client for Windows x64 package.

.DESCRIPTION
    Produces dist/OpenSstpWin-win-x64-portable.zip containing:
      OpenSstpWin.exe   - self-contained single file (no .NET install needed)
      wintun.dll        - signed Wintun 0.14.1 L3 tunnel driver DLL (amd64)
      README.txt        - quick start
      LICENSE, WINTUN-LICENSE.txt

    wintun.dll is fetched from wintun.net and verified against the SHA-256 that
    the Wintun project publishes. If wintun.net is unreachable, the CI mirror
    (a byte-identical copy of the 0.14.1 amd64 DLL) is used instead.

.PARAMETER Version
    Version stamped into the archive name (default: project version).

.PARAMETER SkipTests
    Skip `dotnet test`.

.EXAMPLE
    ./build-portable.ps1
#>
[CmdletBinding()]
param(
    [string]$Version = "1.11.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src/OpenSstpWin/OpenSstpWin.csproj"
$testProject = Join-Path $root "tests/OpenSstpWin.Tests/OpenSstpWin.Tests.csproj"
$publishDir = Join-Path $root "publish"
$distDir = Join-Path $root "dist"
$packageName = "OpenSstpWin-win-x64-portable"
$packageDir = Join-Path $distDir $packageName
$zipPath = Join-Path $distDir "$packageName-$Version.zip"

# The official, WireGuard-signed Wintun 0.14.1 amd64 DLL.
$wintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip"
$wintunZipSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51"
$wintunDllSha256 = "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce"
$wintunMirror = "https://api.github.com/repos/wzshiming/wintun/contents/embedded/bin/amd64/wintun.dll"
$wintunLicenseMirror = "https://api.github.com/repos/wzshiming/wintun/contents/embedded/LICENSE.txt"

function Get-Sha256([string]$path) {
    (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

Write-Host "==> Cleaning output folders"
Remove-Item -Recurse -Force $publishDir, $distDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDir, $packageDir | Out-Null

if (-not $SkipTests) {
    Write-Host "==> Running unit tests"
    dotnet test $testProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }
}

Write-Host "==> Publishing self-contained single file (win-x64)"
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -p:DebugType=none `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $publishDir "OpenSstpWin.exe"
if (-not (Test-Path $exe)) { throw "OpenSstpWin.exe was not produced." }
Copy-Item $exe (Join-Path $packageDir "OpenSstpWin.exe")

Write-Host "==> Fetching wintun.dll (0.14.1, amd64)"
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("wintun-" + [guid]::NewGuid())
New-Item -ItemType Directory -Force -Path $temp | Out-Null
$wintunDll = Join-Path $packageDir "wintun.dll"
$wintunLicense = Join-Path $packageDir "WINTUN-LICENSE.txt"

try {
    $zip = Join-Path $temp "wintun.zip"
    $downloaded = $false
    try {
        Invoke-WebRequest -Uri $wintunUrl -OutFile $zip -UseBasicParsing -TimeoutSec 120
        $hash = Get-Sha256 $zip
        if ($hash -ne $wintunZipSha256) {
            throw "wintun-0.14.1.zip SHA-256 mismatch: $hash"
        }
        Expand-Archive -Path $zip -DestinationPath $temp -Force
        Copy-Item (Join-Path $temp "wintun/bin/amd64/wintun.dll") $wintunDll
        Copy-Item (Join-Path $temp "wintun/LICENSE.txt") $wintunLicense
        $downloaded = $true
        Write-Host "    wintun.dll from wintun.net (zip verified)"
    }
    catch {
        Write-Warning "Could not use wintun.net ($($_.Exception.Message)); falling back to the mirror."
    }

    if (-not $downloaded) {
        $headers = @{ Accept = "application/vnd.github.raw"; "User-Agent" = "open-sstp-client-build" }
        if ($env:GITHUB_TOKEN) { $headers["Authorization"] = "Bearer $env:GITHUB_TOKEN" }
        Invoke-WebRequest -Uri $wintunMirror -OutFile $wintunDll -Headers $headers -UseBasicParsing -TimeoutSec 120
        try {
            Invoke-WebRequest -Uri $wintunLicenseMirror -OutFile $wintunLicense -Headers $headers -UseBasicParsing -TimeoutSec 60
        }
        catch {
            Set-Content -Path $wintunLicense -Value "Wintun 0.14.1 is Copyright (C) WireGuard LLC. See https://www.wintun.net/ for the license."
        }
        Write-Host "    wintun.dll from mirror"
    }

    $dllHash = Get-Sha256 $wintunDll
    if ($dllHash -ne $wintunDllSha256) {
        throw "wintun.dll SHA-256 mismatch: $dllHash (expected $wintunDllSha256)"
    }
    Write-Host "    wintun.dll verified ($dllHash)"
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

Write-Host "==> Writing package docs"
Copy-Item (Join-Path $root "README-WINDOWS.txt") (Join-Path $packageDir "README.txt")
$license = Join-Path $root "../LICENSE"
if (Test-Path $license) {
    Copy-Item $license (Join-Path $packageDir "LICENSE")
}

Set-Content -Path (Join-Path $packageDir "VERSION.txt") -Value "Open SSTP Client for Windows $Version (portable, win-x64)"

Write-Host "==> Creating archive"
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force

$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zipPath.sha256" -Value "$sha  $(Split-Path $zipPath -Leaf)"

Write-Host ""
Write-Host "Portable package : $zipPath ($size MB)"
Write-Host "SHA-256          : $sha"
Write-Host "Unpacked folder  : $packageDir"
