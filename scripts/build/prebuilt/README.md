# Per-RID prebuilt modules

Drop **platform-specific prebuilt modules** here and the build automatically copies
the matching runtime's copy into that package's `bin/modules/`.

These are modules with **native binaries that differ per platform** and have no
source in this repo — chiefly `EncryptionCont` (the native encryption module, whose
`EncryptionContNative.*` differs per OS/arch). Get the per-platform binaries from the
official releases: https://github.com/gigamon-dev/SubspaceServer/releases

> **Not committed.** Binaries you place here are **git-ignored** (see `.gitignore`) —
> they are populated locally per build and are not checked into the repo. Only the
> folder structure (`.gitkeep`) and this README are tracked.

## Layout

```
scripts/build/prebuilt/
├─ win-x64/
│  └─ EncryptionCont/           -> copied to <package>/bin/modules/EncryptionCont/
│     ├─ SS.EncryptionCont.dll
│     ├─ SS.EncryptionCont.deps.json
│     ├─ SS.EncryptionCont.runtimeconfig.json
│     └─ EncryptionContNative.dll     (win native)
├─ linux-x64/
│  └─ EncryptionCont/
│     └─ ... EncryptionContNative.so  (linux native)
├─ linux-arm64/  osx-x64/  osx-arm64/  win-arm64/   (same idea)
```

Each `prebuilt/<rid>/<ModuleName>/` folder is copied verbatim into
`<package>/bin/modules/<ModuleName>/`. Add a matching entry to `conf/Modules.config`
(e.g. `EncryptionCont` is normally uncommented for production).

Only **directories** are copied — loose files (like this README or a `.gitkeep`) are
ignored. An empty RID folder simply contributes nothing to that package.
