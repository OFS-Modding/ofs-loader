<p align="center">
  <img src="assets/logo.png" width="128" alt="OFS-Modding">
</p>

# OFS Loader

Native bootstrap, managed runtime, in-game Mod Hub, command-line manager, and
installer for Ore Factory Squad mods.

The loader consumes the versioned `OFS.Sdk` package. It does not contain SDK
source or game files.

## Install

Download and run
[`OFS-Installer-win-x64.exe`](https://github.com/OFS-Modding/ofs-loader/releases/latest/download/OFS-Installer-win-x64.exe).
It locates Ore Factory Squad through Steam, verifies its embedded payload,
installs or updates the loader, and synchronizes the signed official catalog.
It never launches the game.

Developer and automation modes:

```powershell
OFS-Installer-win-x64.exe --game-dir "D:\SteamLibrary\steamapps\common\Ore Factory Squad"
OFS-Installer-win-x64.exe --status --game-dir "D:\path\to\game"
OFS-Installer-win-x64.exe --scan --game-dir "D:\path\to\game"
OFS-Installer-win-x64.exe --extract-only .\ofs-loader-dev
OFS-Installer-win-x64.exe --manager --help
```

The versioned ZIP and PowerShell bootstrap installer remain available for
advanced deployment and recovery workflows.

## Components

- `native/OFS.Bootstrap`: native Windows bootstrap.
- `src/OFS.Runtime.Entry`: managed runtime hosted in the game process.
- `src/OFS.Manager.Cli`: scanning, installation, packages, profiles, and diagnostics.
- `src/OFS.Installer`: verified single-executable user and developer installer.
- `src/OFS.Loader.Core`: loader-owned build fingerprinting shared by runtime and manager.

## Build and test

```powershell
./eng/resolve-sdk.ps1
./eng/verify.ps1 -SkipNative
```

Pass `-ZigPath` or configure Zig 0.16.0 to include the native bootstrap. Offline
tests never launch the game. Real-game validation is intentionally separate and
requires explicit maintainer authorization.

## Official mod catalog

`bootstrap install` provisions the official public key and refreshes the signed
catalog automatically. Opening the in-game Mod Hub retries the refresh, so a
temporary network failure during installation does not require a repair step.
The Mod Hub lists installed and catalog mods in separate views. Package
installation, enable/disable changes, and confirmed uninstallation are staged
safely in-game and applied before mod discovery on the next restart.

The official endpoint is
`https://ofs-modding.github.io/ofs-mod-catalog/catalog.signed.json`. Downloads
are accepted only after the catalog signature, exact package size, SHA-256,
manifest identity and internal AssetBundle index all validate.

For multiplayer, hosts publish their exact required mod profile. A joining
client rejects mismatches before connecting and automatically stages the
matching dependency set only when every requested version is available in the
official pinned catalog. Code or profile changes take effect after restart;
third-party catalogs never trigger unattended multiplayer installation.

This is an unofficial community project and is not affiliated with the game's
developers or publisher.
