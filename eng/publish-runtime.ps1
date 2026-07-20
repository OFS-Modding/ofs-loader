param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repository "artifacts\runtime"
}

dotnet publish (Join-Path $repository "src\OFS.Runtime.Entry\OFS.Runtime.Entry.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained runtime publish failed."
}

$runtimeConfig = Join-Path $OutputDirectory "OFS.Runtime.Entry.runtimeconfig.json"
$configuration = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
if ($null -ne $configuration.runtimeOptions.PSObject.Properties["framework"]) {
    throw "Runtime publish unexpectedly depends on a machine-wide .NET framework."
}

Write-Host "Self-contained managed runtime: $OutputDirectory"
