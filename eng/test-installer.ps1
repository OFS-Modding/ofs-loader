param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:OFS_SKIP_CATALOG_SYNC = '1'
$repository = Split-Path -Parent $PSScriptRoot
$release = Get-ChildItem -LiteralPath (Join-Path $repository 'artifacts\release') `
    -Filter 'OFS-Loader-*-win-x64.zip' -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($release)) { throw 'Build the release before testing install.ps1.' }

$fixture = Join-Path $repository ('.work\installer-fixture-' + [Guid]::NewGuid().ToString('N'))
$steamApps = Join-Path $fixture 'steamapps'
$game = Join-Path $steamApps 'common\Ore Factory Squad'
$data = Join-Path $game 'Ore Factory Squad_Data'
try {
    New-Item -ItemType Directory -Path (Join-Path $data 'il2cpp_data\Metadata') -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $game 'Ore Factory Squad.exe'), [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes((Join-Path $game 'GameAssembly.dll'), [byte[]](4, 5, 6))
    [IO.File]::WriteAllBytes((Join-Path $game 'UnityPlayer.dll'), [byte[]](7, 8, 9))
    [IO.File]::WriteAllBytes((Join-Path $data 'il2cpp_data\Metadata\global-metadata.dat'), [byte[]](10, 11))
    [IO.File]::WriteAllBytes((Join-Path $data 'globalgamemanagers'), [byte[]](12, 13))
    [IO.File]::WriteAllText((Join-Path $data 'boot.config'),
        "build-guid=0123456789abcdef0123456789abcdef`n")
    [IO.File]::WriteAllText((Join-Path $steamApps 'appmanifest_4210580.acf'),
        '"AppState" { "appid" "4210580" "name" "Ore Factory Squad" "buildid" "1" "installdir" "Ore Factory Squad" }')
    & (Join-Path $repository 'install.ps1') -ArchivePath $release -GameDirectory $game
    if (-not (Test-Path -LiteralPath (Join-Path $game 'version.dll')) -or
        -not (Test-Path -LiteralPath (Join-Path $game 'OFS\runtime\OFS.Runtime.Entry.dll'))) {
        throw 'Bootstrap installer did not install the fixture payload.'
    }
    $manifestPath = Join-Path $steamApps 'appmanifest_4210580.acf'
    [IO.File]::WriteAllText(
        $manifestPath,
        '"AppState" { "appid" "4210580" "name" "Ore Factory Squad" "buildid" "2" "installdir" "Ore Factory Squad" }')
    $status = dotnet run --project (Join-Path $repository 'src\OFS.Manager.Cli') `
        -c Release --no-build -- bootstrap status $game | Out-String | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $status.state -ne 'game-updated') {
        throw 'Manager did not detect the simulated Steam BuildID change.'
    }
    Write-Host 'BOOTSTRAP_INSTALLER_PASSED: checksum=True, paths=True, discovery=True, update=True, launch=False.'
}
finally {
    if (Test-Path -LiteralPath $fixture) {
        $resolved = (Resolve-Path -LiteralPath $fixture).Path
        $work = (Resolve-Path -LiteralPath (Join-Path $repository '.work')).Path
        if (-not $resolved.StartsWith($work + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean installer fixture outside .work: '$resolved'."
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
