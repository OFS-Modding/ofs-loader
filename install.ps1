param(
    [string]$Repository = 'OFS-Modding/ofs-loader',
    [string]$Version = 'latest',
    [string]$GameDirectory = '',
    [string]$ArchivePath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('ofs-installer-' + [Guid]::NewGuid().ToString('N'))
$archive = ''
$checksum = ''

function Assert-SafeArchive([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::OpenRead($Path)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains('\') -or
                $name.StartsWith('/') -or $name.Split('/') -contains '..') {
                throw "Release archive contains an unsafe path: '$name'."
            }
            $null = $roots.Add($name.Split('/')[0])
        }
        if ($roots.Count -ne 1) { throw 'Release archive must contain exactly one root directory.' }
    }
    finally {
        $zip.Dispose()
        $stream.Dispose()
    }
}

function Invoke-Manager([string]$Manager, [string[]]$Arguments) {
    & $Manager @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ofs-manager failed: $($Arguments -join ' ') (exit code $LASTEXITCODE)."
    }
}

try {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        $headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'ofs-installer' }
        if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
            $headers.Authorization = "Bearer $env:GITHUB_TOKEN"
        }
        $endpoint = if ($Version -eq 'latest') {
            "https://api.github.com/repos/$Repository/releases/latest"
        }
        else {
            "https://api.github.com/repos/$Repository/releases/tags/$Version"
        }
        $release = Invoke-RestMethod -Uri $endpoint -Headers $headers
        $zipAsset = @($release.assets | Where-Object name -match '^OFS-Loader-.+-win-x64\.zip$')
        $sumAsset = @($release.assets | Where-Object name -match '^OFS-Loader-.+-win-x64\.zip\.sha256$')
        if ($zipAsset.Count -ne 1 -or $sumAsset.Count -ne 1) {
            throw 'Release does not contain exactly one loader ZIP and checksum.'
        }
        $archive = Join-Path $workRoot $zipAsset[0].name
        $checksum = "$archive.sha256"
        Invoke-WebRequest $zipAsset[0].browser_download_url -Headers $headers -OutFile $archive
        Invoke-WebRequest $sumAsset[0].browser_download_url -Headers $headers -OutFile $checksum
    }
    else {
        $archive = [IO.Path]::GetFullPath($ArchivePath)
        $checksum = "$archive.sha256"
    }

    if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or
        -not (Test-Path -LiteralPath $checksum -PathType Leaf)) {
        throw 'Release ZIP or checksum sidecar is missing.'
    }
    $sidecar = (Get-Content -LiteralPath $checksum -Raw).Trim()
    if ($sidecar -notmatch '^(?<hash>[a-fA-F0-9]{64})  (?<name>[^/\\]+\.zip)$') {
        throw 'Release checksum sidecar has invalid syntax.'
    }
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actual -ne $Matches.hash -or [IO.Path]::GetFileName($archive) -ne $Matches.name) {
        throw 'Release ZIP does not match its SHA-256 sidecar.'
    }

    Assert-SafeArchive $archive
    $extract = Join-Path $workRoot 'release'
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $manager = Get-ChildItem -LiteralPath $extract -Filter 'ofs-manager.exe' -Recurse -File |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($manager)) { throw 'Release manager is missing.' }
    $gameArguments = @()
    if (-not [string]::IsNullOrWhiteSpace($GameDirectory)) {
        $gameArguments += [IO.Path]::GetFullPath($GameDirectory)
    }
    Invoke-Manager $manager (@('scan') + $gameArguments)
    Invoke-Manager $manager (@('bootstrap', 'install') + $gameArguments)
    Invoke-Manager $manager (@('bootstrap', 'status') + $gameArguments)
    Write-Host 'OFS Loader installed. The official catalog is ready; the game was not launched.'
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        $resolved = (Resolve-Path -LiteralPath $workRoot).Path
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        if (-not $resolved.StartsWith($temp + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolved).StartsWith('ofs-installer-',
                [StringComparison]::Ordinal)) {
            throw "Refusing to clean unexpected installer directory '$resolved'."
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
