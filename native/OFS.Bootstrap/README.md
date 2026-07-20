# OFS Bootstrap

Native `version.dll` proxy used to enter OFS Framework before Unity initializes managed gameplay.

The proxy forwards the six Version API imports used by `UnityPlayer.dll`, waits for `GameAssembly.dll`, hosts the private self-contained CoreCLR runtime through `hostfxr`, and invokes `OFS.Runtime.Entry`.

Build with the pinned local Zig toolchain:

```powershell
& tools/zig/0.16.0/zig.exe cc `
  -target x86_64-windows `
  -shared `
  -nostdlib `
  -isystem tools/zig/0.16.0/lib/include `
  -isystem tools/zig/0.16.0/lib/libc/include/any-windows-any `
  -I third_party/minhook/include `
  -I third_party/minhook/src `
  -I third_party/minhook/src/hde `
  -O2 -Wall -Wextra -Werror `
  native/OFS.Bootstrap/version_proxy.c `
  third_party/minhook/src/buffer.c `
  third_party/minhook/src/hook.c `
  third_party/minhook/src/trampoline.c `
  third_party/minhook/src/hde/hde64.c `
  native/OFS.Bootstrap/version.def `
  -lkernel32 `
  -o artifacts/bootstrap/version.dll
```

The proxy embeds MinHook 1.3.4 under its BSD-style license. It does not expose
MinHook to mods: the managed runtime only receives a size-versioned private
function table for bootstrap operations.

The size-versioned table also exposes generic `install_detour` / `remove_detour`
operations. The managed SDK wraps them with per-mod ownership, target conflict
detection and rollback when `IOFSMod.Load` fails; mods never link MinHook.
