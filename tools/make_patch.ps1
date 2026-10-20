# Builds an incremental update archive between two published versions.
#
# Only files whose SHA256 differs from the old manifest are copied into the
# archive, plus a _patch.json describing the target hashes and any files that
# must be deleted. A full self-contained publish is ~700 files but a normal
# release only touches the handful of assemblies that carry a version stamp,
# so the patch stays around a megabyte instead of a hundred.
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OldManifest,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$StageDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path $PublishDir).Path.TrimEnd('\', '/')
$outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path (Get-Location).Path $Out }

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "nm-manifest-$([guid]::NewGuid().ToString('N')).json"
& (Join-Path $PSScriptRoot 'write_manifest.ps1') -PublishDir $root -Version $Version -Out $scratch | Out-Null
$newManifest = Get-Content -LiteralPath $scratch -Raw | ConvertFrom-Json
Remove-Item -LiteralPath $scratch -Force

$previous = Get-Content -LiteralPath (Resolve-Path $OldManifest).Path -Raw | ConvertFrom-Json

$oldHashes = @{}
foreach ($file in @($previous.files)) { $oldHashes[$file.path] = $file.sha256 }

if ($StageDir) {
    if (Test-Path $StageDir) { Remove-Item -LiteralPath $StageDir -Recurse -Force }
} else {
    $StageDir = Join-Path ([System.IO.Path]::GetTempPath()) "nm-patch-stage-$([guid]::NewGuid().ToString('N'))"
}
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null
$StageDir = (Resolve-Path $StageDir).Path

$entries = @()
foreach ($file in @($newManifest.files)) {
    if ($oldHashes.ContainsKey($file.path) -and $oldHashes[$file.path] -eq $file.sha256) { continue }

    $source = Join-Path $root $file.path
    $target = Join-Path $StageDir $file.path
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force

    $entries += [ordered]@{ path = $file.path; sha256 = $file.sha256 }
}

$newPaths = @{}
foreach ($file in @($newManifest.files)) { $newPaths[$file.path] = $true }
$delete = @(@($previous.files) | Where-Object { -not $newPaths.ContainsKey($_.path) } | ForEach-Object { $_.path })

$meta = [ordered]@{
    from   = [string]$previous.version
    to     = [string]$newManifest.version
    delete = @($delete)
    files  = @($entries)
}
Set-Content -LiteralPath (Join-Path $StageDir '_patch.json') -Value ($meta | ConvertTo-Json -Depth 4 -Compress) -Encoding utf8NoBOM

if (Test-Path $outPath) { Remove-Item -LiteralPath $outPath -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $StageDir,
    $outPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

$size = (Get-Item -LiteralPath $outPath).Length
Write-Host "patch $outPath : $($entries.Count) files changed, $($delete.Count) deleted, $([math]::Round($size / 1MB, 2)) MB"
