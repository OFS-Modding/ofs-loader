# Contributing

OFS-SDK accepts changes that improve consensual local/co-op modding,
interoperability, authoring APIs, runtime safety or documentation.

## Before opening a pull request

1. Do not commit game binaries, extracted assets, saves, logs, tokens or user data.
2. Keep build-specific observations tied to the documented fingerprint.
3. Preserve main-thread, ownership and rollback invariants.
4. Run `./eng/verify.ps1` on Windows.
5. Document new public SDK contracts and include a sample when practical.
6. Review any `OFS.Sdk` API delta and apply semantic versioning before accepting
   a new `src/OFS.Sdk/PublicAPI.Shipped.txt` baseline.

To inspect the public contract directly:

```powershell
dotnet run --project tests/OFS.Sdk.ApiSurface -- verify src/OFS.Sdk/PublicAPI.Shipped.txt
```

Only after reviewing an intentional API change:

```powershell
dotnet run --project tests/OFS.Sdk.ApiSurface -- accept src/OFS.Sdk/PublicAPI.Shipped.txt
```

Generated wrappers must record their source build and must not redistribute
copyrighted game code or assets. Features intended to bypass DRM, access
unowned content or provide non-consensual public multiplayer advantages are out
of scope.

`verify.ps1` audits every candidate that Git could publish, including ignored
files already force-added to the index. Maintainers preparing a public tag must
run `./eng/verify.ps1 -RequireLicense`; CI enforces the same MIT-license gate.
