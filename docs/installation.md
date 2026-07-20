# Installation

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
