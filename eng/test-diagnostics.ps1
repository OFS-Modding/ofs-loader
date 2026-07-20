param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path $artifacts (
    'diagnostics-cli-smoke-' + [Guid]::NewGuid().ToString('N'))))
$expectedPrefix = $artifacts + [IO.Path]::DirectorySeparatorChar
if (-not $temporaryRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create diagnostics fixture outside artifacts: $temporaryRoot"
}

$game = Join-Path $temporaryRoot 'steamapps\common\Ore Factory Squad'
$steamApps = Join-Path $temporaryRoot 'steamapps'
$diagnostics = Join-Path $game 'OFS\diagnostics'
$managerProject = Join-Path $repository 'src\OFS.Manager.Cli'
try {
    New-Item -ItemType Directory -Path $game -Force | Out-Null
    New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $game 'Ore Factory Squad.exe') | Out-Null
    New-Item -ItemType File -Path (Join-Path $game 'GameAssembly.dll') | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $steamApps 'appmanifest_4210580.acf'),
        '"AppState" { "appid" "4210580" "name" "Ore Factory Squad" "buildid" "1" "installdir" "Ore Factory Squad" }')

    $statuses = @('loaded', 'disabled', 'quarantined', 'rejected', 'blocked', 'failed')
    $mods = for ($index = 0; $index -lt $statuses.Count; ++$index) {
        $status = $statuses[$index]
        $related = [Collections.Generic.List[string]]::new()
        if ($status -eq 'blocked') { $related.Add('test.provider') }
        [ordered]@{
            id = if ($status -eq 'rejected') { $null } else { "test.diagnostic-$index" }
            name = if ($status -eq 'rejected') { $null } else { "Diagnostic $index" }
            version = if ($status -eq 'rejected') { $null } else { '1.0.0' }
            manifestPath = Join-Path $game "OFS\mods\diagnostic-$index\manifest.json"
            status = $status
            phase = $status
            message = "Fixture $status"
            relatedModIds = $related
        }
    }
    $report = [ordered]@{
        schemaVersion = 1
        sessionId = '0123456789abcdef0123456789abcdef'
        processId = 424242
        startedAtUtc = '2026-07-19T10:00:00Z'
        startupCompletedAtUtc = '2026-07-19T10:00:01Z'
        state = 'ready'
        environment = [ordered]@{
            frameworkVersion = '0.1.0'
            gameVersion = '1.0.2'
            gameBuildFingerprint = ('a' * 64)
            unityVersion = '6000.3.13f1'
            il2CppMetadataVersion = 39
            processArchitecture = 'X64'
            pointerSize = 8
            isVerifiedGameBuild = $true
        }
        discoveredManifestCount = 6
        mods = @($mods)
    }
    $report | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $diagnostics 'last-session.json') -Encoding UTF8

    $resultJson = dotnet run --project $managerProject -c Release --no-build -- `
        mod diagnose $game | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'mod diagnose failed for a valid report.' }
    $result = $resultJson | ConvertFrom-Json
    if (-not $result.available -or $result.isCurrentProcess -or
        $result.counts.loaded -ne 1 -or $result.counts.disabled -ne 1 -or
        $result.counts.quarantined -ne 1 -or $result.counts.rejected -ne 1 -or
        $result.counts.blocked -ne 1 -or $result.counts.failed -ne 1 -or
        $result.counts.problems -ne 4 -or $result.report.state -ne 'ready') {
        throw 'mod diagnose did not classify the valid fixture report.'
    }

    [IO.File]::WriteAllText((Join-Path $diagnostics 'last-session.json'), '{ invalid')
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    dotnet run --project $managerProject -c Release --no-build -- `
        mod diagnose $game 2>$null | Out-Null
    $invalidExit = $LASTEXITCODE
    $ErrorActionPreference = $oldPreference
    if ($invalidExit -eq 0) { throw 'mod diagnose accepted invalid JSON.' }

    Remove-Item -LiteralPath (Join-Path $diagnostics 'last-session.json') -Force
    $missingJson = dotnet run --project $managerProject -c Release --no-build -- `
        mod diagnose $game | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'mod diagnose failed when no report exists.' }
    $missing = $missingJson | ConvertFrom-Json
    if ($missing.available -or $null -ne $missing.report) {
        throw 'mod diagnose did not report the missing state cleanly.'
    }

    Write-Host 'Structured runtime diagnostics CLI verification passed.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolved = (Resolve-Path -LiteralPath $temporaryRoot).Path
        if (-not $resolved.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean diagnostics fixture outside artifacts: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
