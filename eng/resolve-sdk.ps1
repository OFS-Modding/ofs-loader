param(
    [string]$Version = '0.2.4',
    [string]$PackagePath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
$packages = Join-Path $repository '.packages'
$destination = Join-Path $packages "OFS.Sdk.$Version.nupkg"
$expected = '5c9c5dd27c36ddf3218b9274654cc287e0b23bd41024d958e50078a1c7a5b6cf'

New-Item -ItemType Directory -Path $packages -Force | Out-Null
if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    Copy-Item -LiteralPath ([IO.Path]::GetFullPath($PackagePath)) -Destination $destination -Force
}
elseif (-not (Test-Path -LiteralPath $destination)) {
    $url = "https://github.com/OFS-Modding/ofs-sdk/releases/download/v$Version/OFS.Sdk.$Version.nupkg"
    Invoke-WebRequest -Uri $url -OutFile $destination
}

$actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "OFS SDK package digest mismatch: $actual" }
Write-Host "OFS SDK $Version ready: $destination"
