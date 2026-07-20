# Installation

## Single-executable installer

The recommended installation is the stable release asset:

<https://github.com/OFS-Modding/ofs-loader/releases/latest/download/OFS-Installer-win-x64.exe>

Run it normally to discover the Steam installation, install or update the
loader, and synchronize the official signed catalog. The executable validates
the embedded release manifest and every payload SHA-256 before executing the
embedded manager. It does not launch the game.

For development and unattended environments:

```powershell
.\OFS-Installer-win-x64.exe --game-dir "D:\path\to\Ore Factory Squad"
.\OFS-Installer-win-x64.exe --status --game-dir "D:\path\to\Ore Factory Squad"
.\OFS-Installer-win-x64.exe --extract-only .\loader-tools
.\OFS-Installer-win-x64.exe --manager bootstrap status "D:\path\to\Ore Factory Squad"
```

## Manual and PowerShell installation

Release archives contain `ofs-manager.exe`, the bootstrap, and the managed
runtime. Close the game, extract the archive, then run:

```powershell
./ofs-manager.exe bootstrap install
./ofs-manager.exe bootstrap status
```

The manager discovers Steam libraries automatically and never launches the game.
Installation also provisions and refreshes the official signed mod catalog.
The in-game Mod Hub retries that refresh automatically whenever it is opened.

The public bootstrap installer downloads and verifies the latest release before
performing the same installation:

```powershell
irm https://raw.githubusercontent.com/OFS-Modding/ofs-loader/main/install.ps1 | iex
```
