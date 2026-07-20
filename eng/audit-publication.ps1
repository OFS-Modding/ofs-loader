param(
    [string]$RepositoryRoot = "",
    [switch]$ScanAllFiles,
    [switch]$RequireLicense,
    [long]$MaxFileBytes = 5MB
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

$root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Publication root does not exist: $root"
}
if ($MaxFileBytes -le 0) {
    throw "MaxFileBytes must be greater than zero."
}

$excludedDirectoryNames = @(
    ".git", ".work", ".tools", ".secrets", ".vs",
    "artifacts", "bin", "obj", "logs", "crashdumps"
)
$forbiddenExtensions = @(
    ".7z", ".assets", ".bundle", ".dll", ".dmp", ".dylib", ".exe",
    ".key", ".log", ".nupkg", ".ofmod", ".p12", ".pdb", ".pfx",
    ".rar", ".ress", ".sav", ".save", ".so", ".unity3d", ".zip"
)
$forbiddenFileNames = @(
    ".env", "assembly-csharp.dll", "gameassembly.dll", "global-metadata.dat",
    "ore factory squad.exe", "player-prev.log", "player.log", "save.gz",
    "unityplayer.dll"
)

function Convert-ToRelativePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Candidate escapes publication root: $fullPath"
    }

    return $fullPath.Substring($prefix.Length).Replace("\", "/")
}

function Test-IsExcluded([string]$RelativePath) {
    $segments = $RelativePath -split "/"
    foreach ($segment in $segments) {
        if ($excludedDirectoryNames -contains $segment.ToLowerInvariant()) {
            return $true
        }
    }

    return $false
}

function Get-PublicationCandidates {
    if ($ScanAllFiles) {
        return @(
            Get-ChildItem -LiteralPath $root -File -Recurse -Force |
                ForEach-Object { Convert-ToRelativePath $_.FullName } |
                Where-Object { -not (Test-IsExcluded $_) }
        )
    }

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "git is required unless -ScanAllFiles is specified."
    }

    Push-Location $root
    try {
        & git rev-parse --is-inside-work-tree 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Publication root is not a Git work tree: $root"
        }

        $paths = @(& git -c core.quotepath=false ls-files --cached --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) {
            throw "git ls-files failed while enumerating publication candidates."
        }

        return @($paths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    finally {
        Pop-Location
    }
}

$candidatePaths = @(Get-PublicationCandidates | Sort-Object -Unique)
$violations = [Collections.Generic.List[string]]::new()
$contentScannedFiles = 0

foreach ($relativePath in $candidatePaths) {
    $normalized = $relativePath.Replace("\", "/").TrimStart("/")
    if (Test-IsExcluded $normalized) {
        # Ignored generated state may still be returned if it was force-added. A
        # force-added path is in the index and must therefore be audited.
        $tracked = $false
        if (-not $ScanAllFiles) {
            Push-Location $root
            try {
                & git ls-files --error-unmatch -- $relativePath 2>$null | Out-Null
                $tracked = $LASTEXITCODE -eq 0
            }
            finally {
                Pop-Location
            }
        }
        if (-not $tracked) { continue }
    }

    $fullPath = [IO.Path]::GetFullPath((Join-Path $root $relativePath))
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        $violations.Add("${normalized}: path escapes the repository root")
        continue
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        # A deleted index entry is not publication payload.
        continue
    }

    $item = Get-Item -LiteralPath $fullPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        $violations.Add("${normalized}: reparse points/symlinks are not accepted in publication payload")
        continue
    }

    $lowerName = $item.Name.ToLowerInvariant()
    $lowerExtension = $item.Extension.ToLowerInvariant()
    if ($forbiddenFileNames -contains $lowerName -or
        $forbiddenExtensions -contains $lowerExtension -or
        ($lowerName.StartsWith(".env.") -and $lowerName -ne ".env.example")) {
        $violations.Add("${normalized}: generated binary, game data, save/log/dump, archive, or private-key container")
        continue
    }

    if ($normalized -match "(?i)(^|/)(saves?|crashes?|crash reports?)(/|$)" -or
        $normalized -match "(?i)(^|/)managed/assembly-csharp\.dll$") {
        $violations.Add("${normalized}: game/user runtime data directory")
        continue
    }

    if ($item.Length -gt $MaxFileBytes) {
        $violations.Add("${normalized}: $($item.Length) bytes exceeds publication limit $MaxFileBytes")
        continue
    }

    # Unity AssetBundles are often extensionless. Detect their file signature
    # instead of relying on naming conventions so generated bundles cannot be
    # committed by accident under an arbitrary path.
    if ($item.Length -ge 7) {
        $stream = [IO.File]::OpenRead($fullPath)
        try {
            $signatureBytes = [byte[]]::new(8)
            $signatureLength = $stream.Read($signatureBytes, 0, $signatureBytes.Length)
            $signature = [Text.Encoding]::ASCII.GetString(
                $signatureBytes,
                0,
                $signatureLength)
            if ($signature.StartsWith("UnityFS", [StringComparison]::Ordinal) -or
                $signature.StartsWith("UnityRaw", [StringComparison]::Ordinal) -or
                $signature.StartsWith("UnityWeb", [StringComparison]::Ordinal)) {
                $violations.Add("${normalized}: generated Unity AssetBundle binary")
                continue
            }
        }
        finally {
            $stream.Dispose()
        }
    }

    # Scan every accepted payload, independent of its name. ReadAllText detects
    # BOMs and replaces invalid byte sequences, which preserves the ASCII token
    # and path signatures we care about even in a renamed or binary-looking file.
    $content = [IO.File]::ReadAllText($fullPath)
    $contentScannedFiles++
    $secretPatterns = @(
        @{ Name = "private key"; Pattern = "-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----" },
        @{ Name = "GitHub token"; Pattern = "(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})" },
        @{ Name = "AWS access key"; Pattern = "(?:AKIA|ASIA)[0-9A-Z]{16}" },
        @{ Name = "Slack token"; Pattern = "xox[baprs]-[A-Za-z0-9-]{20,}" },
        @{ Name = "Discord webhook"; Pattern = "https://(?:canary\.|ptb\.)?discord(?:app)?\.com/api/webhooks/[0-9]+/[A-Za-z0-9._-]+" },
        @{ Name = "personal Windows path"; Pattern = "(?i)[A-Z]:\\Users\\[^\\\r\n]+\\" },
        @{ Name = "personal Unix path"; Pattern = "(?i)(?:/Users|/home)/[^/\s]+/" }
    )
    foreach ($secretPattern in $secretPatterns) {
        if ([Text.RegularExpressions.Regex]::IsMatch($content, $secretPattern.Pattern)) {
            $violations.Add("${normalized}: contains $($secretPattern.Name)")
        }
    }
}

$licenseNames = @("COPYING", "LICENSE", "LICENSE.md", "LICENSE.txt")
$hasLicense = $false
foreach ($licenseName in $licenseNames) {
    if (Test-Path -LiteralPath (Join-Path $root $licenseName) -PathType Leaf) {
        $hasLicense = $true
        break
    }
}
if (-not $hasLicense -and $RequireLicense) {
    $violations.Add("repository root: no LICENSE/COPYING file; owner license choice is required for public release")
}

if ($violations.Count -gt 0) {
    $details = ($violations | Sort-Object -Unique | ForEach-Object { " - $_" }) -join [Environment]::NewLine
    throw "Publication audit failed with $($violations.Count) violation(s):$([Environment]::NewLine)$details"
}

if (-not $hasLicense) {
    Write-Warning "No LICENSE/COPYING file is present. The audit allows local development, but a public release must run with -RequireLicense."
}

Write-Host "Publication audit passed: $($candidatePaths.Count) candidate file(s), $contentScannedFiles content scan(s), max $MaxFileBytes bytes."
