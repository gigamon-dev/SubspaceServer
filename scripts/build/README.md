# Zone packaging

Builds ready-to-run **zone** folders for Subspace Server .NET — one standalone
package per platform, so you deploy only the one that matches your target.

- `build-zone.ps1` — PowerShell 7+ (`pwsh`); runs on Windows, Linux, and macOS.
- `build-zone.sh` — Bash twin for Linux/macOS/CI (or Git Bash on Windows).

Both produce identical output.

## Quick start

```bash
# All six platforms into ./zone/<rid>
pwsh scripts/build/build-zone.ps1

# One platform, with a distributable archive
pwsh scripts/build/build-zone.ps1 -Runtime win-x64 -Archive
```

```bash
# Bash equivalents
scripts/build/build-zone.sh
scripts/build/build-zone.sh --runtime win-x64 --archive
```

## Output layout

Packages are written to `zone/` at the repo root, one subfolder per runtime:

```
zone/
├─ win-x64/     ┐
├─ linux-x64/   ├─ each is a complete, standalone zone (see below)
├─ osx-arm64/   ┘
└─ <rid>/
   ├─ bin/                     published host: SubspaceServer.dll, SS.Core/Packets/Utilities,
   │  │                        shared NuGet deps, and this RID's native assets (SkiaSharp, SQLite)
   │  └─ modules/<Name>/       plug-in modules: Replay, Matchmaking (+ prebuilt/<rid> overlays)
   ├─ conf/ arenas/ maps/            content from src/SubspaceServer/Zone
   ├─ data/ log/ tmp/ recordings/ clients/   runtime dirs, shipped empty
   ├─ LICENSE  news.txt  obscene.txt  scrty  scrty1
   └─ run-server.cmd + run-server.ps1  (Windows)  /  run-server.sh  (Linux, macOS)
```

Supported RIDs: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

## Options

| PowerShell | Bash | Default | Meaning |
|---|---|---|---|
| `-Runtime <rid\|all>` | `-r, --runtime` | `all` | RID(s) to build. |
| `-Configuration` | `-c, --configuration` | `Release` | Build configuration. |
| `-SelfContained` | `--self-contained` | off | Bundle the .NET runtime (no prerequisite on target; larger). |
| `-Output <dir>` | `-o, --output` | `./zone` | Output root (one subfolder per RID). |
| `-Archive` | `--archive` | off | Also emit a `.tar.gz` per RID (preserves the executable bit). |
| `-IncludeContinuum` | `--include-continuum` | off | Include `clients/Continuum.exe`. |
| `-Clean` | `--clean` | off | Wipe each per-RID folder first. |
| `-Source` | `--source` | off | Also emit `SubspaceServer-<version>.tar.gz`, the GitHub-style source archive. |
| `-SourceRef <ref>` | `--source-ref <ref>` | `HEAD` | Git ref archived by `-Source`. |

## Notes

- **Framework-dependent by default.** The target machine must have the **.NET 10
  runtime** installed. Pass `-SelfContained` to bundle the runtime instead.
- **`Continuum.exe` is excluded by default** (copyrighted client binary). Place a
  copy of Continuum v0.40 into `clients/` before running, or pass
  `-IncludeContinuum` to copy the one from the Zone template.
- **Package curation is driven by [`package-exclude.txt`](package-exclude.txt).**
  Everything under `src/SubspaceServer/Zone/` (plus built modules, `LICENSE`, and the
  `run-server.*` launchers) is assembled, then that file prunes what shouldn't ship —
  git artifacts (`**/.gitignore`), runtime working dirs (`data/ log/ tmp/ recordings/`
  shipped empty), and the reference `Example` module. New Zone content is included
  automatically; edit the exclude file to change what's omitted. (`.pdb` symbols are
  stripped during build.)
- **`LICENSE`** (from the repo root) is added to every package. **Launchers** match the
  target OS: Windows packages get `run-server.cmd` + `run-server.ps1`; Linux/macOS
  packages get `run-server.sh` (PowerShell isn't standard on those, so `run-server.ps1`
  is Windows-only).
- **Prebuilt / native modules go in [`prebuilt/<rid>/`](prebuilt/README.md).** Modules
  with per-platform native binaries and no source here (chiefly `EncryptionCont`) are
  dropped into `scripts/build/prebuilt/<rid>/<ModuleName>/`; the build copies the
  matching runtime's copy into each package's `bin/modules/` automatically. Get the
  per-platform binaries from the official releases.
- **Plug-in modules are auto-discovered.** Any `*.csproj` under `src/` with
  `<EnableDynamicLoading>true</EnableDynamicLoading>` (the marker every module
  carries — see the plug-in guide in `CLAUDE.md`) is built and packaged. Add a new
  custom module that follows the convention and it is included automatically, with
  no change to these scripts. Its `bin/modules/<name>` subfolder name comes from the
  project's own `<OutDir>`, so make sure the module sets
  `<OutDir>$(SolutionDir)SubspaceServer\Zone\bin\modules\<name></OutDir>` (as the
  built-in modules do) and add a matching entry to `conf/Modules.config`.
- Plug-in modules are pure-managed, so they are built once and copied into every
  package. The host is published **per-RID** so each package gets the correct
  native libraries.
- **`-Source` mirrors GitHub's "Source code (tar.gz)".** It runs `git archive`, so
  it contains only **committed** files at the ref (default `HEAD`) under a
  `SubspaceServer-<version>/` folder — no `bin/`, `obj/`, `.git`, or untracked
  files. Commit your work (and tag, e.g. `--source-ref v4.0.0`) before using it for
  a release.

## Running a package

```bash
cd zone/linux-x64
./run-server.sh          # framework-dependent: needs .NET 10 installed
```

```bat
cd zone\win-x64
run-server.cmd
```
