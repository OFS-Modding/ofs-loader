$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repository "artifacts"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$probeRoot = Join-Path $artifacts ("publication-audit-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $probeRoot | Out-Null

function Invoke-Audit {
    param([string]$Root, [long]$MaxFileBytes = 5MB, [switch]$RequireLicense)

    & (Join-Path $PSScriptRoot "audit-publication.ps1") `
        -RepositoryRoot $Root `
        -ScanAllFiles `
        -MaxFileBytes $MaxFileBytes `
        -RequireLicense:$RequireLicense
}

function Assert-AuditFails {
    param([string]$Description, [scriptblock]$Action, [string]$ExpectedFragment)

    $failed = $false
    try {
        & $Action
    }
    catch {
        $failed = $true
        if ($_.Exception.Message.IndexOf($ExpectedFragment, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "$Description failed for the wrong reason: $($_.Exception.Message)"
        }
    }
    if (-not $failed) {
        throw "$Description unexpectedly passed."
    }
}

try {
    [IO.File]::WriteAllText((Join-Path $probeRoot "README.md"), "clean fixture`n")
    [IO.File]::WriteAllText((Join-Path $probeRoot "catalog.public.pem"), "-----BEGIN PUBLIC KEY-----`nfixture`n-----END PUBLIC KEY-----`n")
    New-Item -ItemType Directory -Path (Join-Path $probeRoot "artifacts") | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $probeRoot "artifacts\GameAssembly.dll"), [byte[]](1, 2, 3))
    Invoke-Audit $probeRoot

    Assert-AuditFails "Private-key content probe" {
        $privateHeader = "-----BEGIN " + "PRIVATE KEY-----"
        [IO.File]::WriteAllText((Join-Path $probeRoot "innocent.png"), "$privateHeader`nnot-a-real-key`n")
        try { Invoke-Audit $probeRoot } finally { Remove-Item -LiteralPath (Join-Path $probeRoot "innocent.png") -Force }
    } "private key"

    Assert-AuditFails "Game binary probe" {
        [IO.File]::WriteAllBytes((Join-Path $probeRoot "GameAssembly.dll"), [byte[]](1, 2, 3))
        try { Invoke-Audit $probeRoot } finally { Remove-Item -LiteralPath (Join-Path $probeRoot "GameAssembly.dll") -Force }
    } "generated binary"

    Assert-AuditFails "Extensionless AssetBundle probe" {
        [IO.File]::WriteAllBytes(
            (Join-Path $probeRoot "innocent-data"),
            [Text.Encoding]::ASCII.GetBytes("UnityFS`0fixture"))
        try { Invoke-Audit $probeRoot } finally { Remove-Item -LiteralPath (Join-Path $probeRoot "innocent-data") -Force }
    } "Unity AssetBundle"

    Assert-AuditFails "Personal-path probe" {
        $personalPath = 'C:' + '\Users\Example\AppData\LocalLow\Game'
        [IO.File]::WriteAllText((Join-Path $probeRoot "local-path.md"), $personalPath)
        try { Invoke-Audit $probeRoot } finally { Remove-Item -LiteralPath (Join-Path $probeRoot "local-path.md") -Force }
    } "personal Windows path"

    Assert-AuditFails "Oversized-file probe" {
        [IO.File]::WriteAllBytes((Join-Path $probeRoot "large.png"), [byte[]](0, 1, 2, 3, 4))
        try { Invoke-Audit $probeRoot -MaxFileBytes 4 } finally { Remove-Item -LiteralPath (Join-Path $probeRoot "large.png") -Force }
    } "exceeds publication limit"

    Assert-AuditFails "Required-license probe" {
        Invoke-Audit $probeRoot -RequireLicense
    } "no LICENSE"

    [IO.File]::WriteAllText((Join-Path $probeRoot "LICENSE"), "Fixture license only.`n")
    Invoke-Audit $probeRoot -RequireLicense

    # A force-added ignored payload is still publishable from Git's index and
    # must not be able to hide behind the directory exclusion.
    [IO.File]::WriteAllText((Join-Path $probeRoot ".gitignore"), "artifacts/`n")
    & git -C $probeRoot init --quiet
    if ($LASTEXITCODE -ne 0) { throw "Could not initialize publication audit Git fixture." }
    & git -C $probeRoot -c core.autocrlf=false add README.md LICENSE .gitignore catalog.public.pem
    if ($LASTEXITCODE -ne 0) { throw "Could not stage clean publication audit fixture." }
    & git -C $probeRoot -c core.autocrlf=false add --force artifacts/GameAssembly.dll
    if ($LASTEXITCODE -ne 0) { throw "Could not force-stage publication audit payload." }
    Assert-AuditFails "Force-added ignored payload probe" {
        & (Join-Path $PSScriptRoot "audit-publication.ps1") -RepositoryRoot $probeRoot
    } "generated binary"

    Write-Host "Publication audit positive and negative probes passed."
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        $resolvedProbe = (Resolve-Path -LiteralPath $probeRoot).Path
        $resolvedArtifacts = (Resolve-Path -LiteralPath $artifacts).Path
        if (-not $resolvedProbe.StartsWith($resolvedArtifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean publication probe outside artifacts: $resolvedProbe"
        }
        Remove-Item -LiteralPath $resolvedProbe -Recurse -Force
    }
}
