param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repository "artifacts\manager"
}

dotnet publish (Join-Path $repository "src\OFS.Manager.Cli\OFS.Manager.Cli.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -o $OutputDirectory `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained OFS Manager publish failed."
}

$manager = Join-Path $OutputDirectory "ofs-manager.exe"
$referenceDocumentation = Join-Path $OutputDirectory "OFS.Sdk.xml"
if (Test-Path -LiteralPath $referenceDocumentation) {
    [IO.File]::Delete($referenceDocumentation)
}
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -File)
if (-not (Test-Path -LiteralPath $manager) -or $files.Count -ne 1) {
    throw "OFS Manager publish must contain exactly one ofs-manager.exe."
}

$reportedVersion = & $manager --version | Out-String
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($reportedVersion)) {
    throw "Published OFS Manager did not report its version."
}

Write-Host "Self-contained OFS Manager $($reportedVersion.Trim()): $manager"
