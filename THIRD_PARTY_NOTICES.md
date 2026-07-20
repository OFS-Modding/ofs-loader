# Third-party notices

## MinHook

The native bootstrap embeds MinHook 1.3.4. MinHook is distributed under its
BSD-style license; the vendored copyright and license text are preserved in
[`third_party/minhook/LICENSE.txt`](third_party/minhook/LICENSE.txt).

No MinHook API is exposed directly to mods. OFS-SDK wraps it with a
size-versioned native bridge and managed ownership handles.
