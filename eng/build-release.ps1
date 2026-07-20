param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repository "artifacts\release"
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repository "Directory.Build.props") -Raw
$version = [string]$buildProperties.Project.PropertyGroup.Version
$profile = Get-Content -LiteralPath (Join-Path $PSScriptRoot "release-profile.json") -Raw |
    ConvertFrom-Json
if ($profile.schemaVersion -ne 1 -or $profile.rid -ne "win-x64" -or
    [string]::IsNullOrWhiteSpace([string]$profile.game.fingerprint)) {
    throw "Release profile is invalid."
}

$bootstrap = Join-Path $repository "artifacts\bootstrap\version.dll"
$runtime = Join-Path $repository "artifacts\runtime"
foreach ($required in @($bootstrap, $runtime)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Release input is missing: '$required'. Run eng/verify.ps1 first."
    }
}

$token = [Guid]::NewGuid().ToString("N")
$workRoot = Join-Path $outputRoot ".work-$token"
$managerA = Join-Path $workRoot "manager-a"
$managerB = Join-Path $workRoot "manager-b"
$installerA = Join-Path $workRoot "installer-a"
$installerB = Join-Path $workRoot "installer-b"
$payload = Join-Path $workRoot "payload"
$bundleName = "OFS-Loader-$version-win-x64"
$zipPath = Join-Path $outputRoot "$bundleName.zip"
$firstZip = Join-Path $workRoot "$bundleName.first.zip"
$checksumPath = "$zipPath.sha256"
$installerPath = Join-Path $outputRoot "OFS-Installer-win-x64.exe"
$installerChecksumPath = "$installerPath.sha256"

function Get-NormalizedRelativePath([string]$Base, [string]$Path) {
    $resolvedBase = [IO.Path]::GetFullPath($Base).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $prefix = $resolvedBase + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$resolvedPath' is outside release payload '$resolvedBase'."
    }
    return $resolvedPath.Substring($prefix.Length).Replace('\', '/')
}

function New-DeterministicZip([string]$Source, [string]$Destination, [string]$RootName) {
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($Destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new(
        $stream,
        [IO.Compression.ZipArchiveMode]::Create,
        $false)
    try {
        $timestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File |
                     Sort-Object { Get-NormalizedRelativePath $Source $_.FullName }) {
            $relative = Get-NormalizedRelativePath $Source $file.FullName
            $entry = $archive.CreateEntry(
                "$RootName/$relative",
                [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $timestamp
            $input = [IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try { $input.CopyTo($output) }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    & (Join-Path $PSScriptRoot "publish-manager.ps1") -OutputDirectory $managerA
    & (Join-Path $PSScriptRoot "publish-manager.ps1") -OutputDirectory $managerB
    $managerAHash = (Get-FileHash -LiteralPath (Join-Path $managerA "ofs-manager.exe") -Algorithm SHA256).Hash
    $managerBHash = (Get-FileHash -LiteralPath (Join-Path $managerB "ofs-manager.exe") -Algorithm SHA256).Hash
    if ($managerAHash -ne $managerBHash) {
        throw "OFS Manager single-file publish is not deterministic: $managerAHash != $managerBHash."
    }

    New-Item -ItemType Directory -Path $payload | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $payload "bootstrap") | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $payload "runtime") | Out-Null
    Copy-Item -LiteralPath (Join-Path $managerA "ofs-manager.exe") -Destination $payload
    Copy-Item -LiteralPath $bootstrap -Destination (Join-Path $payload "bootstrap\version.dll")
    Copy-Item -Path (Join-Path $runtime "*") -Destination (Join-Path $payload "runtime") -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "release-README.txt") -Destination (Join-Path $payload "README.txt")
    Copy-Item -LiteralPath (Join-Path $repository "THIRD_PARTY_NOTICES.md") -Destination $payload

    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File |
        Sort-Object { Get-NormalizedRelativePath $payload $_.FullName } |
        ForEach-Object {
            [ordered]@{
                path = Get-NormalizedRelativePath $payload $_.FullName
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    $manifest = [ordered]@{
        schemaVersion = 1
        loaderVersion = $version
        rid = [string]$profile.rid
        game = [ordered]@{
            version = [string]$profile.game.version
            steamBuildId = [string]$profile.game.steamBuildId
            fingerprint = [string]$profile.game.fingerprint
        }
        files = $payloadFiles
    }
    [IO.File]::WriteAllText(
        (Join-Path $payload "release-manifest.json"),
        ($manifest | ConvertTo-Json -Depth 8) + "`n",
        [Text.UTF8Encoding]::new($false))

    $checksumFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File |
        Sort-Object { Get-NormalizedRelativePath $payload $_.FullName })
    $checksumLines = foreach ($file in $checksumFiles) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Get-NormalizedRelativePath $payload $file.FullName)"
    }
    [IO.File]::WriteAllText(
        (Join-Path $payload "SHA256SUMS"),
        ($checksumLines -join "`n") + "`n",
        [Text.UTF8Encoding]::new($false))

    New-DeterministicZip $payload $firstZip $bundleName
    New-DeterministicZip $payload $zipPath $bundleName
    $firstHash = (Get-FileHash -LiteralPath $firstZip -Algorithm SHA256).Hash
    $releaseHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    if ($firstHash -ne $releaseHash) {
        throw "Release ZIP is not deterministic: $firstHash != $releaseHash."
    }
    [IO.File]::WriteAllText(
        $checksumPath,
        "$($releaseHash.ToLowerInvariant())  $([IO.Path]::GetFileName($zipPath))`n",
        [Text.UTF8Encoding]::new($false))

    & (Join-Path $PSScriptRoot "publish-installer.ps1") `
        -PayloadArchive $zipPath -OutputDirectory $installerA
    & (Join-Path $PSScriptRoot "publish-installer.ps1") `
        -PayloadArchive $zipPath -OutputDirectory $installerB
    $installerAPath = Join-Path $installerA "OFS-Installer.exe"
    $installerBPath = Join-Path $installerB "OFS-Installer.exe"
    $installerAHash = (Get-FileHash -LiteralPath $installerAPath -Algorithm SHA256).Hash
    $installerBHash = (Get-FileHash -LiteralPath $installerBPath -Algorithm SHA256).Hash
    if ($installerAHash -ne $installerBHash) {
        throw "OFS Installer single-file publish is not deterministic: $installerAHash != $installerBHash."
    }
    Copy-Item -LiteralPath $installerAPath -Destination $installerPath -Force
    [IO.File]::WriteAllText(
        $installerChecksumPath,
        "$($installerAHash.ToLowerInvariant())  $([IO.Path]::GetFileName($installerPath))`n",
        [Text.UTF8Encoding]::new($false))

    [ordered]@{
        bundle = $zipPath
        bytes = (Get-Item -LiteralPath $zipPath).Length
        sha256 = $releaseHash.ToLowerInvariant()
        installer = $installerPath
        installerBytes = (Get-Item -LiteralPath $installerPath).Length
        installerSha256 = $installerAHash.ToLowerInvariant()
        managerSha256 = $managerAHash.ToLowerInvariant()
        payloadFiles = $payloadFiles.Count + 2
    } | ConvertTo-Json
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        $resolvedWork = (Resolve-Path -LiteralPath $workRoot).Path
        if (-not $resolvedWork.StartsWith(
                $outputRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean release work directory outside output: $resolvedWork"
        }
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
