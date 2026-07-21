param(
    [string]$Version = '0.2.4',
    [string]$PackagePath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
$packages = Join-Path $repository '.packages'
$destination = Join-Path $packages "OFS.Sdk.$Version.nupkg"
$expected = 'b4ff664d61fd219cc347eb5d1a64197b46da71814fe5b286f4e61bb0f573a70e'

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
