param(
    [switch]$SkipNative,
    [string]$ZigPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot

Push-Location $repository
try {
    & (Join-Path $PSScriptRoot 'resolve-sdk.ps1')
    dotnet format OFS.Loader.sln --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw 'Formatting verification failed.' }
    dotnet build OFS.Loader.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Loader build failed.' }
    dotnet run --project tests/OFS.Sdk.SmokeTests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Runtime smoke tests failed.' }
    & (Join-Path $PSScriptRoot 'test-safety.ps1')
    & (Join-Path $PSScriptRoot 'test-diagnostics.ps1')
    if (-not $SkipNative) {
        & (Join-Path $PSScriptRoot 'build-native.ps1') -ZigPath $ZigPath
    }
    & (Join-Path $PSScriptRoot 'publish-runtime.ps1')
    & (Join-Path $PSScriptRoot 'publish-manager.ps1')
    if (-not $SkipNative) {
        & (Join-Path $PSScriptRoot 'test-release.ps1')
        & (Join-Path $PSScriptRoot 'test-installer.ps1')
    }
    Write-Host 'OFS Loader offline verification passed. The game was not launched.'
}
finally {
    Pop-Location
}
