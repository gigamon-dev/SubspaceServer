#!/bin/bash
#
# build-zone.sh - build ready-to-run Subspace Server .NET "zone" packages,
# one standalone folder per runtime (RID). Bash twin of build-zone.ps1.
#
# Run with --help for usage. Framework-dependent by default (targets need the
# .NET 10 runtime installed).

set -euo pipefail

ALL_RIDS=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)

usage() {
  cat <<'EOF'
build-zone.sh - build ready-to-run Subspace Server .NET "zone" packages,
one standalone folder per runtime (RID).

USAGE
  scripts/build/build-zone.sh [options]

OPTIONS
  -r, --runtime <rid|all>    Runtime(s) to build; repeat for several. Default: all
  -c, --configuration <cfg>  Build configuration. Default: Release
  -o, --output <dir>         Output root; one <rid> subfolder each. Default: <repo>/zone
      --self-contained       Bundle the .NET runtime (no prerequisite on target)
      --archive              Also produce a .tar.gz per runtime
      --include-continuum    Include clients/Continuum.exe (excluded by default)
      --clean                Remove each per-RID folder before building
      --source               Also produce the GitHub-style source tarball (git archive)
      --source-ref <ref>     Git ref for --source. Default: HEAD
  -h, --help                 Show this help and exit

RUNTIMES
  win-x64  win-arm64  linux-x64  linux-arm64  osx-x64  osx-arm64

EXAMPLES
  scripts/build/build-zone.sh                          # all runtimes -> ./zone
  scripts/build/build-zone.sh -r linux-x64 --archive   # one runtime + archive
  scripts/build/build-zone.sh -r win-x64 -r osx-arm64  # a specific pair

Framework-dependent by default: targets need the .NET 10 runtime installed.
clients/Continuum.exe is excluded by default (copyrighted client binary).
EOF
}

# Plug-in module projects are discovered automatically (see discover_plugins below):
# any csproj under src/ with <EnableDynamicLoading>true</EnableDynamicLoading>.

# --- Defaults --------------------------------------------------------------

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
HOST_PROJECT="$REPO_ROOT/src/SubspaceServer/SubspaceServer.csproj"
ZONE_TEMPLATE="$REPO_ROOT/src/SubspaceServer/Zone"
LICENSE_FILE="$REPO_ROOT/LICENSE"
STARTUP_BASH="$REPO_ROOT/scripts/startup/bash/run-server.sh"
STARTUP_CMD="$REPO_ROOT/scripts/startup/cmd/run-server.cmd"
STARTUP_PWSH="$REPO_ROOT/scripts/startup/powershell/run-server.ps1"
EXCLUDE_FILE="$SCRIPT_DIR/package-exclude.txt"
PREBUILT_DIR="$SCRIPT_DIR/prebuilt"

RUNTIMES=()
CONFIGURATION="Release"
SELF_CONTAINED=0
OUTPUT=""
ARCHIVE=0
INCLUDE_CONTINUUM=0
CLEAN=0
SOURCE=0
SOURCE_REF="HEAD"

# --- Parse args ------------------------------------------------------------

while [[ $# -gt 0 ]]; do
  case "$1" in
    -r|--runtime)          RUNTIMES+=("$2"); shift 2 ;;
    -c|--configuration)    CONFIGURATION="$2"; shift 2 ;;
    --self-contained)      SELF_CONTAINED=1; shift ;;
    -o|--output)           OUTPUT="$2"; shift 2 ;;
    --archive)             ARCHIVE=1; shift ;;
    --include-continuum)   INCLUDE_CONTINUUM=1; shift ;;
    --clean)               CLEAN=1; shift ;;
    --source)              SOURCE=1; shift ;;
    --source-ref)          SOURCE_REF="$2"; shift 2 ;;
    -h|--help)             usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; echo "Try 'scripts/build/build-zone.sh --help'." >&2; exit 2 ;;
  esac
done

[[ -z "$OUTPUT" ]] && OUTPUT="$REPO_ROOT/zone"

if [[ ${#RUNTIMES[@]} -eq 0 || ( ${#RUNTIMES[@]} -eq 1 && "${RUNTIMES[0]}" == "all" ) ]]; then
  RIDS=("${ALL_RIDS[@]}")
else
  RIDS=("${RUNTIMES[@]}")
  for rid in "${RIDS[@]}"; do
    if [[ ! " ${ALL_RIDS[*]} " == *" $rid "* ]]; then
      echo "Unknown runtime identifier: $rid. Supported: ${ALL_RIDS[*]}" >&2
      exit 2
    fi
  done
fi

if [[ $SELF_CONTAINED -eq 1 ]]; then SC="true"; else SC="false"; fi

# --- Helpers ---------------------------------------------------------------

run_dotnet() {
  echo "  > dotnet $*"
  dotnet "$@"
}

# Discover plug-in module projects: any csproj under src/ that opts into dynamic
# loading - the marker every module carries (see the plug-in guide in CLAUDE.md).
# Auto-includes custom modules without editing this script. Prints one path per line.
discover_plugins() {
  while IFS= read -r -d '' proj; do
    if grep -Eiq '<EnableDynamicLoading>[[:space:]]*true[[:space:]]*</EnableDynamicLoading>' "$proj"; then
      printf '%s\n' "$proj"
    fi
  done < <(find "$REPO_ROOT/src" -name '*.csproj' -print0 | sort -z)
}

copy_zone_template() {
  local dest="$1"
  mkdir -p "$dest"
  # Copy every top-level entry except the dev 'bin' folder.
  for entry in "$ZONE_TEMPLATE"/* "$ZONE_TEMPLATE"/.[!.]*; do
    [[ -e "$entry" ]] || continue
    local name; name="$(basename "$entry")"
    [[ "$name" == "bin" ]] && continue
    cp -R "$entry" "$dest/"
  done
  if [[ $INCLUDE_CONTINUUM -eq 0 && -f "$dest/clients/Continuum.exe" ]]; then
    rm -f "$dest/clients/Continuum.exe"
  fi
  mkdir -p "$dest/log" "$dest/tmp" "$dest/recordings" "$dest/data"
}

write_launcher() {
  local dest="$1" rid="$2"
  local is_win=0
  [[ "$rid" == win-* ]] && is_win=1

  if [[ $SELF_CONTAINED -eq 1 ]]; then
    if [[ $is_win -eq 1 ]]; then
      cat > "$dest/run-server.cmd" <<'EOF'
@echo off
REM Startup script for a self-contained Subspace Server .NET zone package.
:START
ECHO %DATE% %TIME%: Starting Subspace Server .NET...
bin\SubspaceServer.exe
IF %ERRORLEVEL% EQU 1 GOTO START
IF %ERRORLEVEL% EQU 3 GOTO START
ECHO %DATE% %TIME%: Subspace Server .NET exited (code %ERRORLEVEL%).
EOF
    else
      cat > "$dest/run-server.sh" <<'EOF'
#!/bin/bash
# Startup script for a self-contained Subspace Server .NET zone package.
cd "$(dirname "$0")" || exit 2
chmod +x ./bin/SubspaceServer 2>/dev/null
while true; do
  echo "$(date '+%Y-%m-%d %H:%M:%S'): Starting Subspace Server .NET"
  ./bin/SubspaceServer
  EXIT=$?
  if [ $EXIT -ne 1 ] && [ $EXIT -ne 3 ]; then
    echo "$(date '+%Y-%m-%d %H:%M:%S'): Subspace Server .NET exited (code $EXIT)."
    break
  fi
done
EOF
      chmod +x "$dest/run-server.sh"
    fi
    return
  fi

  # Framework-dependent: reuse the maintained startup scripts. Windows packages get
  # run-server.cmd + run-server.ps1; unix packages get run-server.sh (PowerShell isn't
  # standard on Linux/macOS).
  if [[ $is_win -eq 1 ]]; then
    cp "$STARTUP_CMD" "$dest/run-server.cmd"
    cp "$STARTUP_PWSH" "$dest/run-server.ps1"
  else
    cp "$STARTUP_BASH" "$dest/run-server.sh"
    chmod +x "$dest/run-server.sh"
  fi
}

# Overlay per-RID prebuilt modules (native EncryptionCont, etc.) into bin/modules.
# Only module DIRECTORIES under prebuilt/<rid>/ are copied (loose files ignored).
copy_prebuilt() {
  local modules_dir="$1" rid="$2"
  local rid_dir="$PREBUILT_DIR/$rid"
  [[ -d "$rid_dir" ]] || return 0
  local m
  for m in "$rid_dir"/*/; do
    [[ -d "$m" ]] || continue
    cp -R "${m%/}" "$modules_dir/"
    echo "    + prebuilt module: $(basename "${m%/}")"
  done
}

# Prune an assembled package per package-exclude.txt. See that file for the grammar.
prune_package() {
  local root="$1"
  [[ -f "$EXCLUDE_FILE" ]] || return 0
  local raw line name d
  while IFS= read -r raw || [[ -n "$raw" ]]; do
    line="${raw%%#*}"                                        # strip trailing comment
    line="$(printf '%s' "$line" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"  # trim
    [[ -z "$line" ]] && continue
    if [[ "$line" == '**/'* ]]; then                         # basename at any depth
      name="${line#**/}"
      find "$root" -name "$name" -exec rm -rf {} + 2>/dev/null || true
    elif [[ "$line" == */'*' ]]; then                        # empty a directory
      d="${line%/*}"
      [[ -d "$root/$d" ]] && find "$root/$d" -mindepth 1 -exec rm -rf {} + 2>/dev/null || true
    elif [[ "$line" == */ ]]; then                           # remove a directory
      rm -rf "$root/${line%/}" 2>/dev/null || true
    else                                                     # remove a file/dir
      rm -rf "$root/$line" 2>/dev/null || true
    fi
  done < "$EXCLUDE_FILE"
}

# --- Build plug-in modules once --------------------------------------------

# Point SolutionDir at a staging root so each plug-in's own <OutDir>
# ($(SolutionDir)SubspaceServer/Zone/bin/modules/<name>) resolves there with its
# reference semantics intact (Private=false keeps SS.Core/SS.Packets out of the
# module folder) - a global '-o'/OutDir override would dump core assemblies in.
STAGE_ROOT="$OUTPUT/_stage"
MODULES_SOURCE="$STAGE_ROOT/SubspaceServer/Zone/bin/modules"
echo "==> Building plug-in modules ($CONFIGURATION)"

# Discover plug-in projects (bash 3.2-compatible: no 'mapfile').
PLUGIN_PROJECTS=()
while IFS= read -r line; do
  [[ -n "$line" ]] && PLUGIN_PROJECTS+=("$line")
done < <(discover_plugins)

echo "    Discovered ${#PLUGIN_PROJECTS[@]} plug-in project(s):"
for proj in "${PLUGIN_PROJECTS[@]}"; do echo "      - $(basename "${proj%.csproj}")"; done
if [[ ${#PLUGIN_PROJECTS[@]} -eq 0 ]]; then
  echo "    (warning) No csproj with <EnableDynamicLoading>true</EnableDynamicLoading> found." >&2
fi

rm -rf "$STAGE_ROOT"
mkdir -p "$STAGE_ROOT"

for proj in "${PLUGIN_PROJECTS[@]}"; do
  echo "--> $(basename "${proj%.csproj}")"
  run_dotnet build "$proj" -c "$CONFIGURATION" -p:SolutionDir="$STAGE_ROOT/" --nologo
done

find "$MODULES_SOURCE" -name '*.pdb' -delete 2>/dev/null || true
find "$MODULES_SOURCE" -type d -name ref -exec rm -rf {} + 2>/dev/null || true

# Report the module folders that were actually produced.
if [[ -d "$MODULES_SOURCE" ]]; then
  echo "    Packaged module folder(s): $(ls -1 "$MODULES_SOURCE" 2>/dev/null | tr '\n' ' ')"
fi

# --- Build one zone package per RID ----------------------------------------

for rid in "${RIDS[@]}"; do
  echo ""
  echo "==> Packaging zone for $rid"
  zone_dir="$OUTPUT/$rid"

  if [[ $CLEAN -eq 1 && -d "$zone_dir" ]]; then rm -rf "$zone_dir"; fi

  # 1) Zone template.
  copy_zone_template "$zone_dir"

  # 2) Publish host into bin/.
  bin_dir="$zone_dir/bin"
  run_dotnet publish "$HOST_PROJECT" -c "$CONFIGURATION" -r "$rid" \
    --self-contained "$SC" -o "$bin_dir" --nologo
  find "$bin_dir" -maxdepth 1 -name '*.pdb' -delete 2>/dev/null || true

  # 3) Copy plug-in modules into bin/modules/<Name>.
  # Folder names come from each project's own OutDir, so copy whatever was built.
  modules_dir="$bin_dir/modules"
  mkdir -p "$modules_dir"
  if [[ -d "$MODULES_SOURCE" ]]; then
    for mf in "$MODULES_SOURCE"/*/; do
      [[ -d "$mf" ]] || continue
      cp -R "${mf%/}" "$modules_dir/"
    done
  fi

  # 3b) Overlay per-RID prebuilt modules (native EncryptionCont, etc.).
  copy_prebuilt "$modules_dir" "$rid"

  # 4) Launchers + LICENSE.
  write_launcher "$zone_dir" "$rid"
  [[ -f "$LICENSE_FILE" ]] && cp "$LICENSE_FILE" "$zone_dir/LICENSE"

  # 5) Prune the assembled package per package-exclude.txt.
  prune_package "$zone_dir"

  # 6) Optional archive (.tar.gz for every RID: no extra tools, and it preserves
  #    the executable bit on the apphost / run-server.sh).
  if [[ $ARCHIVE -eq 1 ]]; then
    version="4.0.0"
    out="$OUTPUT/SubspaceServer-$version-$rid.tar.gz"
    rm -f "$out"
    tar -czf "$out" -C "$zone_dir" .
    echo "    archive: $out"
  fi

  echo "    done: $zone_dir"
done

# Source archive (GitHub-style "Source code (tar.gz)").
if [[ $SOURCE -eq 1 ]]; then
  echo ""
  echo "==> Source archive ($SOURCE_REF)"
  if ! command -v git >/dev/null 2>&1; then
    echo "    (warning) git not found; skipping source archive." >&2
  else
    version="4.0.0"
    src_out="$OUTPUT/SubspaceServer-$version.tar.gz"
    case "$src_out" in /*) ;; *) src_out="$PWD/$src_out" ;; esac
    rm -f "$src_out"
    # Reproduces GitHub's source archive: tracked files at $SOURCE_REF only, under a
    # SubspaceServer-<version>/ top folder, honoring .gitattributes export-ignore.
    git -C "$REPO_ROOT" archive --format=tar.gz --prefix="SubspaceServer-$version/" "$SOURCE_REF" -o "$src_out"
    echo "    source archive: $src_out"
  fi
fi

rm -rf "$STAGE_ROOT"

echo ""
echo "All done. Packages in: $OUTPUT"
if [[ $SELF_CONTAINED -eq 0 ]]; then
  echo "Note: framework-dependent build - targets need the .NET 10 runtime installed."
fi
if [[ $INCLUDE_CONTINUUM -eq 0 ]]; then
  echo "Note: clients/Continuum.exe was NOT included. Place a copy there before running."
fi
