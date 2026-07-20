param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$env:OFS_SKIP_CATALOG_SYNC = '1'

$repository = Split-Path -Parent $PSScriptRoot
$releaseDirectory = Join-Path $repository "artifacts\release"
& (Join-Path $PSScriptRoot "build-release.ps1") -OutputDirectory $releaseDirectory | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Release build failed." }

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repository "Directory.Build.props") -Raw
$version = [string]$buildProperties.Project.PropertyGroup.Version
$bundleName = "OFS-Loader-$version-win-x64"
$zipPath = Join-Path $releaseDirectory "$bundleName.zip"
$checksumPath = "$zipPath.sha256"
if (-not (Test-Path -LiteralPath $zipPath) -or -not (Test-Path -LiteralPath $checksumPath)) {
    throw "Release ZIP or checksum sidecar is missing."
}

$sidecar = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
if ($sidecar -notmatch '^(?<hash>[a-f0-9]{64})  (?<name>[^/\\]+\.zip)$') {
    throw "Release checksum sidecar has invalid syntax."
}
$actualZipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($Matches.hash -ne $actualZipHash -or $Matches.name -ne [IO.Path]::GetFileName($zipPath)) {
    throw "Release checksum sidecar does not match the ZIP."
}

$probeRoot = Join-Path $releaseDirectory ("release-smoke-" + [Guid]::NewGuid().ToString("N"))
$extractRoot = Join-Path $probeRoot "extracted"
$fixtureRoot = Join-Path $probeRoot "fixture"
$expectedPrefix = [IO.Path]::GetFullPath($releaseDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Resolve-ConfinedPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Contains('\') -or
        $Relative.StartsWith('/', [StringComparison]::Ordinal) -or
        $Relative.Split('/') -contains '..') {
        throw "Release manifest contains unsafe path '$Relative'."
    }
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $resolved = [IO.Path]::GetFullPath((Join-Path $resolvedRoot $Relative.Replace('/', '\')))
    if (-not $resolved.StartsWith(
            $resolvedRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release path escaped extraction root: '$Relative'."
    }
    return $resolved
}

try {
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot
    $bundleRoot = Join-Path $extractRoot $bundleName
    $manifestPath = Join-Path $bundleRoot "release-manifest.json"
    $sumsPath = Join-Path $bundleRoot "SHA256SUMS"
    $manager = Join-Path $bundleRoot "ofs-manager.exe"
    foreach ($required in @($manifestPath, $sumsPath, $manager)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Extracted release is missing '$required'."
        }
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.loaderVersion -ne $version -or
        $manifest.rid -ne "win-x64") {
        throw "Release manifest identity is invalid."
    }
    $declaredPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $manifest.files) {
        $relative = [string]$file.path
        if (-not $declaredPaths.Add($relative)) {
            throw "Release manifest repeats '$relative'."
        }
        $fullPath = Resolve-ConfinedPath $bundleRoot $relative
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Release manifest file is missing: '$relative'."
        }
        $info = Get-Item -LiteralPath $fullPath
        $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($info.Length -ne [long]$file.bytes -or $hash -ne [string]$file.sha256) {
            throw "Release manifest size/hash mismatch for '$relative'."
        }
    }

    $actualFiles = @(Get-ChildItem -LiteralPath $bundleRoot -Recurse -File)
    if ($actualFiles.Count -ne $declaredPaths.Count + 2) {
        throw "Release contains undeclared files or an incomplete manifest."
    }

    $sumEntries = @{}
    foreach ($line in Get-Content -LiteralPath $sumsPath) {
        if ($line -notmatch '^(?<hash>[a-f0-9]{64})  (?<path>.+)$') {
            throw "SHA256SUMS contains an invalid line."
        }
        if ($sumEntries.ContainsKey($Matches.path)) {
            throw "SHA256SUMS repeats '$($Matches.path)'."
        }
        $sumEntries[$Matches.path] = $Matches.hash
    }
    if ($sumEntries.Count -ne $actualFiles.Count - 1) {
        throw "SHA256SUMS does not cover every release file except itself."
    }
    foreach ($entry in $sumEntries.GetEnumerator()) {
        $fullPath = Resolve-ConfinedPath $bundleRoot ([string]$entry.Key)
        $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne [string]$entry.Value) {
            throw "SHA256SUMS mismatch for '$($entry.Key)'."
        }
    }

    $reportedVersion = (& $manager --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $reportedVersion -ne $version) {
        throw "Released manager version '$reportedVersion' differs from '$version'."
    }

    $steamApps = Join-Path $fixtureRoot "steamapps"
    $game = Join-Path $steamApps "common\Ore Factory Squad"
    $data = Join-Path $game "Ore Factory Squad_Data"
    New-Item -ItemType Directory -Path (Join-Path $data "il2cpp_data\Metadata") -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $game "Ore Factory Squad.exe"), [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes((Join-Path $game "GameAssembly.dll"), [byte[]](4, 5, 6))
    [IO.File]::WriteAllBytes((Join-Path $game "UnityPlayer.dll"), [byte[]](7, 8, 9))
    [IO.File]::WriteAllBytes((Join-Path $data "il2cpp_data\Metadata\global-metadata.dat"), [byte[]](10, 11))
    [IO.File]::WriteAllBytes((Join-Path $data "globalgamemanagers"), [byte[]](12, 13))
    [IO.File]::WriteAllText(
        (Join-Path $data "boot.config"),
        "build-guid=0123456789abcdef0123456789abcdef`n")
    [IO.File]::WriteAllText(
        (Join-Path $steamApps "appmanifest_4210580.acf"),
        '"AppState" { "appid" "4210580" "name" "Ore Factory Squad" "buildid" "1" "installdir" "Ore Factory Squad" }')

    Push-Location $fixtureRoot
    try {
        $installJson = & $manager bootstrap install $game | Out-String
        if ($LASTEXITCODE -ne 0) { throw "Released manager bootstrap install failed." }
        $install = $installJson | ConvertFrom-Json
        if ($install.bootstrap.state -ne "installed" -or
            -not (Test-Path -LiteralPath (Join-Path $game "version.dll")) -or
            -not (Test-Path -LiteralPath (Join-Path $game "OFS\runtime\OFS.Runtime.Entry.dll"))) {
            throw "Released manager did not install bootstrap/runtime from adjacent payload."
        }

        $status = (& $manager bootstrap status $game | Out-String) | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $status.state -ne "installed") {
            throw "Released manager did not report the fixture installation as installed."
        }

        $uninstall = (& $manager bootstrap uninstall $game | Out-String) | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $uninstall.state -ne "not-installed") {
            throw "Released manager did not uninstall the fixture cleanly."
        }
        $finalStatus = (& $manager bootstrap status $game | Out-String) | ConvertFrom-Json
        if ($finalStatus.state -ne "not-installed" -or
            (Test-Path -LiteralPath (Join-Path $game "version.dll"))) {
            throw "Released manager left bootstrap state behind after uninstall."
        }
    }
    finally {
        Pop-Location
    }

    Write-Host "Release bundle manifest, checksums and clean install/uninstall verification passed."
    Write-Host "Release SHA-256: $actualZipHash"
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        $resolvedProbe = (Resolve-Path -LiteralPath $probeRoot).Path
        if (-not $resolvedProbe.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean release probe outside artifacts: $resolvedProbe"
        }
        Remove-Item -LiteralPath $resolvedProbe -Recurse -Force
    }
}
