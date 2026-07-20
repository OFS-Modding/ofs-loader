OFS Loader for Ore Factory Squad
================================

This is an unofficial modding framework for Windows x64. It does not include
game binaries or assets and does not require BepInEx or MelonLoader.

Install
-------

1. Extract the entire ZIP. Do not run the manager from inside the archive.
2. Close Ore Factory Squad.
3. Open PowerShell in this directory.
4. Inspect the detected build:

     .\ofs-manager.exe scan

5. Install the bootstrap, runtime, official key and catalog:

     .\ofs-manager.exe bootstrap install

6. Verify the installation:

     .\ofs-manager.exe bootstrap status

The install command provisions and refreshes the official signed Mod Hub
catalog automatically. Opening the Mod Hub retries after temporary network
failures.

If Steam discovery is unavailable, append the full game directory to these
commands. Example:

  .\ofs-manager.exe bootstrap install "D:\SteamLibrary\steamapps\common\Ore Factory Squad"

Remove
------

Close the game, then run:

  .\ofs-manager.exe bootstrap uninstall

Diagnostics
-----------

After starting the game, inspect the structured result of the last mod-loading
session with:

  .\ofs-manager.exe mod diagnose

The report identifies loaded, disabled, quarantined, rejected, dependency-
blocked and failed mods without requiring manual runtime.log parsing.

Mod authors
-----------

The authoring package and examples are published separately at
https://github.com/OFS-Modding/ofs-sdk.

Security
--------

Mods execute with full process trust. Only install packages from publishers you
trust and verify hashes/signatures before installation.
