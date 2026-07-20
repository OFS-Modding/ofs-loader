param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadArchive,
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$payload = [IO.Path]::GetFullPath($PayloadArchive)
if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) {
    throw "Installer payload archive is missing: '$payload'."
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repository "artifacts\installer"
}

dotnet publish (Join-Path $repository "src\OFS.Installer\OFS.Installer.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:InstallerPayload=$payload `
    -o $OutputDirectory `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained OFS Installer publish failed."
}

$installer = Join-Path $OutputDirectory "OFS-Installer.exe"
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -File)
if (-not (Test-Path -LiteralPath $installer) -or $files.Count -ne 1) {
    throw "OFS Installer publish must contain exactly one OFS-Installer.exe."
}
$reportedVersion = (& $installer --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($reportedVersion)) {
    throw "Published OFS Installer did not report its version."
}

Write-Host "Self-contained OFS Installer $reportedVersion`: $installer"
