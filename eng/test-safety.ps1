param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path $artifacts (
    'safety-cli-smoke-' + [Guid]::NewGuid().ToString('N'))))
$expectedPrefix = $artifacts + [IO.Path]::DirectorySeparatorChar
if (-not $temporaryRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create safety fixture outside artifacts: $temporaryRoot"
}

$game = Join-Path $temporaryRoot 'steamapps\common\Ore Factory Squad'
$steamApps = Join-Path $temporaryRoot 'steamapps'
$safety = Join-Path $game 'OFS\safety'
$managerProject = Join-Path $repository 'src\OFS.Manager.Cli'
try {
    New-Item -ItemType Directory -Path $game -Force | Out-Null
    New-Item -ItemType Directory -Path $safety -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $game 'Ore Factory Squad.exe') | Out-Null
    New-Item -ItemType File -Path (Join-Path $game 'GameAssembly.dll') | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $steamApps 'appmanifest_4210580.acf'),
        '"AppState" { "appid" "4210580" "name" "Ore Factory Squad" "buildid" "1" "installdir" "Ore Factory Squad" }')

    $entry = @{
        modId = 'test.crashing'
        version = '1.0.0'
        phase = 'mod-load'
        reason = 'Fixture crash'
        manifestPath = (Join-Path $game 'OFS\mods\test.crashing\manifest.json')
        assemblyPath = (Join-Path $game 'OFS\mods\test.crashing\Test.dll')
        sessionId = 'fixture-session'
        detectedAtUtc = '2026-07-19T10:00:00Z'
        occurrences = 1
    }
    @{ schemaVersion = 1; entries = @($entry) } |
        ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $safety 'quarantine.json') -Encoding UTF8
    @{
        schemaVersion = 1
        sessionId = 'fixture-session'
        modId = 'test.crashing'
        version = '1.0.0'
        phase = 'mod-load'
        manifestPath = $entry.manifestPath
        assemblyPath = $entry.assemblyPath
        startedAtUtc = '2026-07-19T09:59:00Z'
        processId = 42
    } |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $safety 'load-journal.json') -Encoding UTF8

    $hotJournal = [ordered]@{
        schemaVersion = 1
        sessionId = 'hot-fixture-session'
        modId = 'test.crashing'
        version = '1.0.0'
        phase = 'callback:hot:event:FrameUpdate'
        manifestPath = $entry.manifestPath
        assemblyPath = $entry.assemblyPath
        startedAtUtc = '2026-07-19T09:59:30Z'
        processId = 42
    }
    $hotPayload = [Text.Encoding]::UTF8.GetBytes(($hotJournal | ConvertTo-Json -Depth 4 -Compress))
    $hotBytes = [byte[]]::new(16 * 1024)
    [Text.Encoding]::ASCII.GetBytes('OFSHOT01').CopyTo($hotBytes, 0)
    [BitConverter]::GetBytes([int]1).CopyTo($hotBytes, 8)
    [BitConverter]::GetBytes([int]$hotPayload.Length).CopyTo($hotBytes, 12)
    $hotHasher = [Security.Cryptography.SHA256]::Create()
    try {
        $hotHasher.ComputeHash($hotPayload).CopyTo($hotBytes, 16)
    }
    finally {
        $hotHasher.Dispose()
    }
    $hotPayload.CopyTo($hotBytes, 64)
    [BitConverter]::GetBytes([int]1).CopyTo($hotBytes, 56)
    [IO.File]::WriteAllBytes((Join-Path $safety 'hot-callback.bin'), $hotBytes)

    $listedJson = dotnet run --project $managerProject -c Release --no-build -- `
        mod quarantine-list $game | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'quarantine-list failed.' }
    $listed = $listedJson | ConvertFrom-Json
    if ($listed.entries.Count -ne 1 -or
        $listed.pendingLoadJournal.modId -ne 'test.crashing' -or
        $listed.pendingHotCallback.phase -ne 'callback:hot:event:FrameUpdate') {
        throw 'quarantine-list did not report entry, pending journal and hot callback.'
    }

    $clearJson = dotnet run --project $managerProject -c Release --no-build -- `
        mod quarantine-clear test.crashing $game | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'quarantine-clear id failed.' }
    $clear = $clearJson | ConvertFrom-Json
    if ($clear.removedModIds -notcontains 'test.crashing' -or
        -not $clear.pendingJournalRemoved -or
        -not $clear.pendingHotCallbackRemoved -or
        (Test-Path -LiteralPath (Join-Path $safety 'hot-callback.bin')) -or
        (Test-Path -LiteralPath (Join-Path $safety 'load-journal.json'))) {
        throw 'quarantine-clear did not remove entry and both matching crash markers.'
    }

    [IO.File]::WriteAllText((Join-Path $safety 'quarantine.json'), '{ invalid')
    [IO.File]::WriteAllText((Join-Path $safety 'load-journal.json'), '{ invalid')
    [IO.File]::WriteAllBytes((Join-Path $safety 'hot-callback.bin'), [byte[]](1, 2, 3))
    dotnet run --project $managerProject -c Release --no-build -- `
        mod quarantine-clear --all $game | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'quarantine-clear --all did not recover corrupt state.' }
    $recoveredJson = dotnet run --project $managerProject -c Release --no-build -- `
        mod quarantine-list $game | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'quarantine-list failed after recovery.' }
    $recovered = $recoveredJson | ConvertFrom-Json
    if ($recovered.entries.Count -ne 0 -or
        $null -ne $recovered.pendingLoadJournal -or
        $null -ne $recovered.pendingHotCallback) {
        throw 'quarantine --all recovery did not produce clean state.'
    }

    Write-Host 'Mod safety CLI verification passed.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolved = (Resolve-Path -LiteralPath $temporaryRoot).Path
        if (-not $resolved.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean safety fixture outside artifacts: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
