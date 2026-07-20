# Installation

Download and run the recommended single-file installer:

<https://github.com/OFS-Modding/ofs-loader/releases/latest/download/OFS-Installer-win-x64.exe>

It discovers the Steam installation, verifies the embedded payload, installs or
updates the loader, and synchronizes the official catalog. It does not launch
the game. Developers can use `--game-dir`, `--status`, `--scan`,
`--extract-only`, or `--manager` for the complete embedded CLI.

## Manual ZIP installation

Close the game, extract the complete loader release, and run:

```powershell
./ofs-manager.exe bootstrap install
./ofs-manager.exe bootstrap status
```

The manager discovers Steam automatically and does not launch the game.
The same install command provisions the official catalog key and refreshes the
catalog. There is no separate catalog setup step.
