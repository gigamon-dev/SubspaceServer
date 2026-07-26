<#
    Runs on Windows PowerShell 5.1+ and PowerShell 7+ (pwsh, cross-platform).

.SYNOPSIS
    Builds ready-to-run Subspace Server .NET "zone" folders, one self-contained
    package per target runtime (RID).

.DESCRIPTION
    Produces, for each requested runtime identifier, a complete zone directory:

        <Output>/<rid>/
        |- bin/                     the published host (SubspaceServer.dll + SS.Core/Packets/Utilities
        |   |                       + shared NuGet + this RID's native assets [SkiaSharp, SQLite])
        |   \- modules/<Name>/      each plug-in module's private assemblies
        |- conf/ arenas/ maps/ data/ clients/ log/ tmp/ recordings/   (from the Zone template)
        |- news.txt obscene.txt scrty scrty1
        \- run-server.(sh|cmd)      launcher with the exit-code/recycle loop

    Each <rid> folder is fully self-describing and contains ONLY that platform's
    binaries, so a user deploys exactly the one package that matches their target.

    The build is framework-dependent: the target machine must have the .NET 10
    runtime installed. The host is still published per-RID so the correct native
    libraries (SkiaSharp, Microsoft.Data.Sqlite / SQLitePCLRaw) are included.

.PARAMETER Runtime
    One or more RIDs to build, or 'all'. Default: all supported RIDs.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER SelfContained
    Bundle the .NET runtime into each package (no runtime prerequisite on the
    target). Larger output. Default: off (framework-dependent).

.PARAMETER Output
    Root output directory. Default: <repo>/zone (one subfolder per RID).

.PARAMETER Archive
    Also produce a .tar.gz archive per RID (no extra tools; preserves the executable bit).

.PARAMETER IncludeContinuum
    Copy clients/Continuum.exe into the package. Off by default (it is a
    copyrighted client binary that should not be redistributed).

.PARAMETER Clean
    Delete the per-RID output folder before building.

.PARAMETER Source
    Also produce the GitHub-style source tarball via 'git archive':
    zone/SubspaceServer-<version>.tar.gz, a SubspaceServer-<version>/ folder of
    tracked source only (no bin/obj/.git). Reflects committed files at -SourceRef.

.PARAMETER SourceRef
    Git ref (tag/branch/commit) archived by -Source. Default: HEAD.

.EXAMPLE
    pwsh scripts/build/build-zone.ps1
    Builds every supported RID into ./zone/<rid>.

.EXAMPLE
    pwsh scripts/build/build-zone.ps1 -Runtime win-x64 -Archive
    Builds only win-x64 and produces zone/win-x64 plus a .tar.gz.
#>
[CmdletBinding()]
param(
    [string[]] $Runtime = @('all'),
    [string]   $Configuration = 'Release',
    [switch]   $SelfContained,
    [string]   $Output,
    [switch]   $Archive,
    [switch]   $IncludeContinuum,
    [switch]   $Clean,
    [switch]   $Source,
    [string]   $SourceRef = 'HEAD',
    [Alias('h')]
    [switch]   $Help
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Show-Usage {
    Write-Host @'
build-zone.ps1 - build ready-to-run Subspace Server .NET "zone" packages,
one standalone folder per runtime (RID).

USAGE
  .\scripts\build\build-zone.ps1 [options]

OPTIONS
  -Runtime <rid,...|all>     Runtime(s) to build (comma-separated), or 'all'. Default: all
  -Configuration <cfg>       Build configuration. Default: Release
  -Output <dir>              Output root; one <rid> subfolder each. Default: <repo>\zone
  -SelfContained             Bundle the .NET runtime (no prerequisite on target)
  -Archive                   Also produce a .tar.gz per runtime
  -IncludeContinuum          Include clients\Continuum.exe (excluded by default)
  -Clean                     Remove each per-RID folder before building
  -Source                    Also produce the GitHub-style source tarball (git archive)
  -SourceRef <ref>           Git ref for -Source. Default: HEAD
  -Help                      Show this help and exit

RUNTIMES
  win-x64  win-arm64  linux-x64  linux-arm64  osx-x64  osx-arm64

EXAMPLES
  .\scripts\build\build-zone.ps1                          # all runtimes -> .\zone
  .\scripts\build\build-zone.ps1 -Runtime win-x64 -Archive
  .\scripts\build\build-zone.ps1 -Runtime win-x64,osx-arm64

Framework-dependent by default: targets need the .NET 10 runtime installed.
clients\Continuum.exe is excluded by default (copyrighted client binary).
'@
}

if ($Help) { Show-Usage; return }

# --- Constants -------------------------------------------------------------

$AllRids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')

# --- Paths -----------------------------------------------------------------

$RepoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
$HostProject = Join-Path $RepoRoot 'src/SubspaceServer/SubspaceServer.csproj'
$ZoneTemplate = Join-Path $RepoRoot 'src/SubspaceServer/Zone'
$LicenseFile = Join-Path $RepoRoot 'LICENSE'
$StartupBash = Join-Path $RepoRoot 'scripts/startup/bash/run-server.sh'
$StartupCmd = Join-Path $RepoRoot 'scripts/startup/cmd/run-server.cmd'
$StartupPwsh = Join-Path $RepoRoot 'scripts/startup/powershell/run-server.ps1'
$ExcludeFile = Join-Path $PSScriptRoot 'package-exclude.txt'
$PrebuiltDir = Join-Path $PSScriptRoot 'prebuilt'

if (-not $Output) { $Output = Join-Path $RepoRoot 'zone' }

# Resolve the requested RID list. Normalize first so comma-separated values work
# both as a native array (-Runtime a,b) and as one string (e.g. via powershell.exe -File).
$Runtime = @($Runtime | ForEach-Object { $_ -split ',' } | Where-Object { $_ -ne '' } | ForEach-Object { $_.Trim() })
if ($Runtime.Count -eq 1 -and $Runtime[0] -eq 'all') {
    $Rids = $AllRids
}
else {
    $Rids = $Runtime
    $unknown = $Rids | Where-Object { $_ -notin $AllRids }
    if ($unknown) {
        throw "Unknown runtime identifier(s): $($unknown -join ', '). Supported: $($AllRids -join ', ')."
    }
}

# --- Helpers ---------------------------------------------------------------

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]] $Arguments)
    Write-Host "  > dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

function Get-PluginProject {
    # A plug-in module is any csproj under src/ that opts into dynamic loading -
    # the marker every module carries (see the plug-in guide in CLAUDE.md).
    # This auto-includes custom modules without editing this script.
    $srcDir = Join-Path $RepoRoot 'src'
    Get-ChildItem -LiteralPath $srcDir -Recurse -Filter '*.csproj' -File |
        Where-Object {
            (Get-Content -LiteralPath $_.FullName -Raw) -match '(?is)<EnableDynamicLoading>\s*true\s*</EnableDynamicLoading>'
        } |
        Sort-Object FullName |
        Select-Object -ExpandProperty FullName
}

function Copy-ZoneTemplate {
    param([string] $Destination)

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    # Copy every top-level entry of the Zone template EXCEPT the dev 'bin' folder
    # (which holds IDE-built modules). The host publish + module copy repopulate bin.
    Get-ChildItem -LiteralPath $ZoneTemplate -Force |
        Where-Object { $_.Name -ne 'bin' } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
        }

    if (-not $IncludeContinuum) {
        $continuum = Join-Path $Destination 'clients/Continuum.exe'
        if (Test-Path -LiteralPath $continuum) {
            Remove-Item -LiteralPath $continuum -Force
        }
    }

    # Ensure runtime working directories exist (they may be empty in the template).
    foreach ($d in @('log', 'tmp', 'recordings', 'data')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $Destination $d) | Out-Null
    }
}

function Copy-Prebuilt {
    # Overlay per-RID prebuilt modules (e.g. native EncryptionCont) into bin/modules.
    # Only module DIRECTORIES under prebuilt/<rid>/ are copied (loose files ignored).
    param([string] $ModulesDir, [string] $Rid)

    $ridDir = Join-Path $PrebuiltDir $Rid
    if (-not (Test-Path -LiteralPath $ridDir)) { return }
    Get-ChildItem -LiteralPath $ridDir -Directory | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $ModulesDir -Recurse -Force
        Write-Host "    + prebuilt module: $($_.Name)" -ForegroundColor DarkGray
    }
}

function Remove-Excluded {
    # Prune the assembled package per package-exclude.txt. See that file for grammar.
    param([string] $Root)

    if (-not (Test-Path -LiteralPath $ExcludeFile)) { return }
    foreach ($raw in Get-Content -LiteralPath $ExcludeFile) {
        $line = ($raw -replace '#.*$', '').Trim()
        if (-not $line) { continue }

        if ($line.StartsWith('**/')) {
            $name = $line.Substring(3)
            Get-ChildItem -LiteralPath $Root -Recurse -Force -Filter $name -ErrorAction SilentlyContinue |
                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        }
        elseif ($line.EndsWith('/*')) {
            $d = Join-Path $Root ($line.Substring(0, $line.Length - 2))
            if (Test-Path -LiteralPath $d) {
                Get-ChildItem -LiteralPath $d -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        elseif ($line.EndsWith('/')) {
            $p = Join-Path $Root ($line.TrimEnd('/'))
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue }
        }
        else {
            $p = Join-Path $Root $line
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}

function Write-Launcher {
    param([string] $Destination, [string] $Rid)

    $isWindows = $Rid.StartsWith('win-')

    if ($SelfContained) {
        # Self-contained: invoke the native apphost directly.
        if ($isWindows) {
            $cmd = @'
@echo off
REM Startup script for a self-contained Subspace Server .NET zone package.
REM Restarts the server on recycle (exit code 1) / OOM (3).
:START
ECHO %DATE% %TIME%: Starting Subspace Server .NET...
bin\SubspaceServer.exe
IF %ERRORLEVEL% EQU 1 GOTO START
IF %ERRORLEVEL% EQU 3 GOTO START
ECHO %DATE% %TIME%: Subspace Server .NET exited (code %ERRORLEVEL%).
'@
            Set-Content -LiteralPath (Join-Path $Destination 'run-server.cmd') -Value $cmd -Encoding ascii
        }
        else {
            $sh = @'
#!/bin/bash
# Startup script for a self-contained Subspace Server .NET zone package.
# Restarts the server on recycle (exit code 1) / OOM (3).
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
'@
            $target = Join-Path $Destination 'run-server.sh'
            # Write with LF line endings and no BOM.
            [System.IO.File]::WriteAllText($target, ($sh -replace "`r`n", "`n"))
        }
        return
    }

    # Framework-dependent: reuse the maintained startup scripts (they invoke
    # 'dotnet ./bin/SubspaceServer.dll' / 'bin\SubspaceServer.exe'). Windows packages
    # get run-server.cmd + run-server.ps1; unix packages get run-server.sh (PowerShell
    # isn't standard on Linux/macOS).
    if ($isWindows) {
        Copy-Item -LiteralPath $StartupCmd -Destination (Join-Path $Destination 'run-server.cmd') -Force
        Copy-Item -LiteralPath $StartupPwsh -Destination (Join-Path $Destination 'run-server.ps1') -Force
    }
    else {
        $target = Join-Path $Destination 'run-server.sh'
        $content = Get-Content -LiteralPath $StartupBash -Raw
        [System.IO.File]::WriteAllText($target, ($content -replace "`r`n", "`n"))
    }
}

# --- Discover & build plug-in modules once (managed, RID-agnostic) ---------

# Each plug-in's csproj sets <OutDir>$(SolutionDir)SubspaceServer\Zone\bin\modules\<name>.
# By pointing SolutionDir at a staging root we let each project's own OutDir + reference
# semantics run unchanged (Private=false keeps SS.Core/SS.Packets OUT of the module folder),
# which a global '-o'/OutDir override would break by dumping core assemblies into the folder.
$PluginProjects = @(Get-PluginProject)
$StageRoot = Join-Path $Output '_stage'
$ModulesSource = Join-Path $StageRoot 'SubspaceServer/Zone/bin/modules'

Write-Host "==> Building plug-in modules ($Configuration)" -ForegroundColor Cyan
$pluginNames = $PluginProjects | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }
Write-Host ("    Discovered $($PluginProjects.Count) plug-in project(s): " + ($pluginNames -join ', '))
if ($PluginProjects.Count -eq 0) {
    Write-Warning "No plug-in projects found (no csproj with <EnableDynamicLoading>true</EnableDynamicLoading>)."
}

if (Test-Path -LiteralPath $StageRoot) { Remove-Item -LiteralPath $StageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $StageRoot | Out-Null

foreach ($proj in $PluginProjects) {
    Write-Host "--> $([System.IO.Path]::GetFileNameWithoutExtension($proj))" -ForegroundColor Cyan
    Invoke-Dotnet @('build', $proj, '-c', $Configuration, "-p:SolutionDir=$StageRoot/", '--nologo')
}

# Strip symbols / ref-assembly folders from the staged modules before packaging.
if (Test-Path -LiteralPath $ModulesSource) {
    Get-ChildItem -LiteralPath $ModulesSource -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -LiteralPath $ModulesSource -Recurse -Directory -Filter 'ref' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
}

# Collect the module folders that were actually produced (folder names come from
# each project's own OutDir, so this captures whatever the projects built).
$ModuleFolders = @()
if (Test-Path -LiteralPath $ModulesSource) {
    $ModuleFolders = @(Get-ChildItem -LiteralPath $ModulesSource -Directory)
}
Write-Host ("    Packaged $($ModuleFolders.Count) module folder(s): " + (($ModuleFolders | ForEach-Object { $_.Name }) -join ', '))

# Warn if a discovered project produced nothing under the modules staging folder
# (i.e. it doesn't follow the OutDir convention: Zone\bin\modules\<name>).
if ($PluginProjects.Count -gt $ModuleFolders.Count) {
    Write-Warning "Some discovered plug-in project(s) produced no module folder. Ensure each sets <OutDir> to `$(SolutionDir)SubspaceServer\Zone\bin\modules\<name>."
}

# --- Build one zone package per RID ----------------------------------------

foreach ($rid in $Rids) {
    Write-Host ""
    Write-Host "==> Packaging zone for $rid" -ForegroundColor Green

    $zoneDir = Join-Path $Output $rid
    if ($Clean -and (Test-Path -LiteralPath $zoneDir)) {
        Remove-Item -LiteralPath $zoneDir -Recurse -Force
    }

    # 1) Zone template (conf, arenas, maps, data, ...).
    Copy-ZoneTemplate -Destination $zoneDir

    # 2) Publish the host into bin/ for this RID.
    $binDir = Join-Path $zoneDir 'bin'
    $scValue = if ($SelfContained) { 'true' } else { 'false' }
    Invoke-Dotnet @(
        'publish', $HostProject,
        '-c', $Configuration,
        '-r', $rid,
        '--self-contained', $scValue,
        '-o', $binDir,
        '--nologo'
    )
    # Trim symbols from the shipped package.
    Get-ChildItem -LiteralPath $binDir -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

    # 3) Copy plug-in modules into bin/modules/<Name>.
    $modulesDir = Join-Path $binDir 'modules'
    New-Item -ItemType Directory -Force -Path $modulesDir | Out-Null
    foreach ($mf in $ModuleFolders) {
        Copy-Item -LiteralPath $mf.FullName -Destination $modulesDir -Recurse -Force
    }

    # 3b) Overlay per-RID prebuilt modules (native EncryptionCont, etc.).
    Copy-Prebuilt -ModulesDir $modulesDir -Rid $rid

    # 4) Launchers + LICENSE.
    Write-Launcher -Destination $zoneDir -Rid $rid
    if (Test-Path -LiteralPath $LicenseFile) {
        Copy-Item -LiteralPath $LicenseFile -Destination (Join-Path $zoneDir 'LICENSE') -Force
    }

    # 5) Prune the assembled package per package-exclude.txt.
    Remove-Excluded -Root $zoneDir

    # 6) Optional archive (.tar.gz for every RID: no extra tools, and it preserves
    #    the executable bit on the apphost / run-server.sh).
    if ($Archive) {
        $version = '4.0.0'
        $tar = Join-Path $Output "SubspaceServer-$version-$rid.tar.gz"
        if (Test-Path -LiteralPath $tar) { Remove-Item -LiteralPath $tar -Force }
        # Use Windows' own tar.exe (bsdtar, ships with Win10 1803+) rather than whatever
        # 'tar' is first on PATH - a GNU tar (e.g. from Git Bash) misreads the drive-letter
        # colon in C:\... as an rsh host. Falls back to PATH 'tar' on non-Windows / if absent.
        $tarExe = 'tar'
        if ($env:OS -eq 'Windows_NT') {
            $sysTar = Join-Path $env:SystemRoot 'System32\tar.exe'
            if (Test-Path -LiteralPath $sysTar) { $tarExe = $sysTar }
        }
        & $tarExe -czf $tar -C $zoneDir '.'
        if ($LASTEXITCODE -ne 0) { throw "tar exited with code $LASTEXITCODE." }
        Write-Host "    archive: $tar" -ForegroundColor DarkGray
    }

    Write-Host "    done: $zoneDir" -ForegroundColor Green
}

# --- Source archive (GitHub-style "Source code (tar.gz)") ------------------

if ($Source) {
    Write-Host ""
    Write-Host "==> Source archive ($SourceRef)" -ForegroundColor Green
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Warning "git not found on PATH; skipping source archive."
    }
    else {
        $version = '4.0.0'
        $srcOut = [System.IO.Path]::GetFullPath((Join-Path $Output "SubspaceServer-$version.tar.gz"))
        if (Test-Path -LiteralPath $srcOut) { Remove-Item -LiteralPath $srcOut -Force }
        # 'git archive' reproduces GitHub's source archive exactly: tracked files at
        # $SourceRef only, under a SubspaceServer-<version>/ top folder, honoring any
        # .gitattributes export-ignore rules. Uses git's own gzip (no external tar).
        & git -C $RepoRoot archive --format=tar.gz --prefix="SubspaceServer-$version/" $SourceRef -o $srcOut
        if ($LASTEXITCODE -ne 0) { throw "git archive exited with code $LASTEXITCODE." }
        Write-Host "    source archive: $srcOut" -ForegroundColor DarkGray
    }
}

# --- Cleanup staging -------------------------------------------------------

Remove-Item -LiteralPath $StageRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "All done. Packages in: $Output" -ForegroundColor Green
if (-not $SelfContained) {
    Write-Host "Note: framework-dependent build - targets need the .NET 10 runtime installed." -ForegroundColor Yellow
}
if (-not $IncludeContinuum) {
    Write-Host "Note: clients/Continuum.exe was NOT included. Place a copy there before running." -ForegroundColor Yellow
}
