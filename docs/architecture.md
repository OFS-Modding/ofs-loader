# Architecture

The proxy bootstrap starts a self-contained managed runtime. The runtime loads
external mods against `OFS.Sdk`, owns the in-game Mod Hub, and isolates mod
lifecycle failures. The manager discovers Steam, fingerprints builds, installs
the bootstrap, manages packages, and reports compatibility state.

The Mod Hub uses restart-bound transactions for code changes. Installs are
downloaded and verified into a pending area; profile changes and removals are
committed before assembly discovery on the next launch. This avoids attempting
to unload live managed assemblies or deleting files still in use.

Build detection is automatic. Compatibility approval and native remapping are
not: changed IL2CPP layouts or ABI assumptions require review before hooks are
enabled for a new game build.
