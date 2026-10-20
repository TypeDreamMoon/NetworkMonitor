# Writes a file manifest (path, size, sha256) for a published directory.
# The client and the patch builder both work off this file, so it must be
# produced from exactly the same directory that gets zipped and shipped.
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path $PublishDir).Path.TrimEnd('\', '/')
$files = Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    [ordered]@{
        path   = $relative
        size   = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$manifest = [ordered]@{
    version = $Version
    files   = @($files)
}

$json = $manifest | ConvertTo-Json -Depth 4 -Compress
Set-Content -LiteralPath $Out -Value $json -Encoding utf8NoBOM

Write-Host "manifest $Out : $(@($files).Count) files"
