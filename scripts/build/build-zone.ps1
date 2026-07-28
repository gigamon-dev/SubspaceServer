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
    Root output directory. Default: <repo>/zone (one <rid> subfolder per RID).
    With a single -Runtime and an explicit -Output that is NOT already a build root
    (has no <rid> subfolder), -Output is treated as the zone itself and updated in
    place - so '-Runtime linux-x64 -Output C:\srv\myzone' updates C:\srv\myzone directly.

.PARAMETER Archive
    Also produce a .tar.gz archive per RID (no extra tools; preserves the executable bit).

.PARAMETER IncludeContinuum
    Copy clients/Continuum.exe into the package. Off by default (it is a
    copyrighted client binary that should not be redistributed).

.PARAMETER Clean
    Delete the per-RID output folder before building (only meaningful with -Full).

.PARAMETER Full
    Assemble the COMPLETE package: Zone content (conf, arenas, maps, ...), launchers,
    LICENSE, plus the compiled code. Use this to create a package or refresh one from
    scratch. WITHOUT -Full (the default), only the compiled code (bin/ + bin/modules/)
    is (re)built in place, leaving an existing deployment's conf/maps/data untouched.

.PARAMETER Source
    Also produce the GitHub-style source tarball via 'git archive':
    zone/SubspaceServer-<version>.tar.gz, a SubspaceServer-<version>/ folder of
    tracked source only (no bin/obj/.git). Reflects committed files at -SourceRef.

.PARAMETER SourceRef
    Git ref (tag/branch/commit) archived by -Source. Default: HEAD.

.EXAMPLE
    pwsh scripts/build/build-zone.ps1 -Full
    Assembles a complete package for every supported RID into ./zone/<rid>.

.EXAMPLE
    pwsh scripts/build/build-zone.ps1 -Runtime win-x64
    Updates only the code (bin/) of an existing ./zone/win-x64 deployment.

.EXAMPLE
    pwsh scripts/build/build-zone.ps1 -Runtime win-x64 -Full -Archive
    Builds the complete win-x64 package and produces zone/win-x64 plus a .tar.gz.
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
    [switch]   $Full,
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
  -IncludeContinuum          Include clients\Continuum.exe (excluded by default; -Full)
  -Clean                     Remove each per-RID folder before building (with -Full)
  -Full                      Assemble the COMPLETE package (Zone content + launchers +
                             LICENSE + code). Default builds only bin/ (code + modules),
                             keeping an existing deployment's conf/maps/etc. untouched.
  -Source                    Also produce the GitHub-style source tarball (git archive)
  -SourceRef <ref>           Git ref for -Source. Default: HEAD
  -Help                      Show this help and exit

RUNTIMES
  win-x64  win-arm64  linux-x64  linux-arm64  osx-x64  osx-arm64

EXAMPLES
  .\scripts\build\build-zone.ps1 -Full                    # complete packages -> .\zone\<rid>
  .\scripts\build\build-zone.ps1 -Runtime win-x64         # update only code in .\zone\win-x64
  .\scripts\build\build-zone.ps1 -Runtime win-x64,osx-arm64 -Full

Default updates only the code (bin/); pass -Full to assemble a complete package.
With one runtime + an explicit -Output that has no <rid> subfolder, -Output is updated
in place (no <rid> subfolder) - e.g. -Runtime linux-x64 -Output C:\srv\myzone.
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
$StartupBashSC = Join-Path $RepoRoot 'scripts/startup/bash/run-server-selfcontained.sh'
$StartupPwshSC = Join-Path $RepoRoot 'scripts/startup/powershell/run-server-selfcontained.ps1'
$ExcludeFile = Join-Path $PSScriptRoot 'package-exclude.txt'
$PrebuiltDir = Join-Path $PSScriptRoot 'prebuilt'

$OutputExplicit = [bool]$Output
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

# Auto in-place: with an explicit -Output and exactly one runtime, update -Output directly
# when it is NOT already a build root (has no <rid> subfolder). Otherwise use a <rid> subfolder.
$InPlace = $false
$ArchiveDir = $Output
if ($OutputExplicit -and $Rids.Count -eq 1 -and
    -not (Test-Path -LiteralPath (Join-Path $Output $Rids[0]) -PathType Container)) {
    $InPlace = $true
    $ArchiveDir = Split-Path -Parent $Output
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
    # the marker every module carries (see doc/developer-guide.md).
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
    # -BinOnly restricts pruning to bin/ rules (used by code-only mode so it never
    # touches the deployment's conf/arenas/maps/etc.).
    param([string] $Root, [switch] $BinOnly)

    if (-not (Test-Path -LiteralPath $ExcludeFile)) { return }
    foreach ($raw in Get-Content -LiteralPath $ExcludeFile) {
        $line = ($raw -replace '#.*$', '').Trim()
        if (-not $line) { continue }
        if ($BinOnly -and -not $line.StartsWith('bin/')) { continue }

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

function Get-TarString {
    param([byte[]] $Bytes, [int] $Offset, [int] $Size)
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $Size; $i++) {
        $c = $Bytes[$Offset + $i]
        if ($c -eq 0) { break }
        [void]$sb.Append([char]$c)
    }
    $sb.ToString()
}

function Get-TarOctal {
    param([byte[]] $Bytes, [int] $Offset, [int] $Size)
    $s = (Get-TarString $Bytes $Offset $Size).Trim()
    if (-not $s) { return [int64]0 }
    [Convert]::ToInt64($s, 8)
}

function Test-TarEntryExec {
    # Which package files should be executable (0755) in a unix tar.
    param([string] $Path)
    $bn = [System.IO.Path]::GetFileName($Path)
    if ($bn -eq 'run-server.sh' -or $bn -eq 'run-server.ps1') { return $true }
    if ($bn -eq 'SubspaceServer' -or $bn -eq 'createdump') { return $true }  # apphost + diagnostics
    if ($bn -like '*.so' -or $bn -like '*.so.*' -or $bn -like '*.dylib') { return $true }  # native libs
    return $false
}

function Set-TarChecksum {
    param([byte[]] $Bytes, [int] $HeaderOffset)
    # Checksum is computed with the 8-byte checksum field (148..155) taken as spaces.
    for ($i = 148; $i -lt 156; $i++) { $Bytes[$HeaderOffset + $i] = 0x20 }
    $sum = 0
    for ($i = 0; $i -lt 512; $i++) { $sum += $Bytes[$HeaderOffset + $i] }
    $oct = [Convert]::ToString($sum, 8).PadLeft(6, '0')
    for ($j = 0; $j -lt 6; $j++) { $Bytes[$HeaderOffset + 148 + $j] = [byte][char]$oct[$j] }
    $Bytes[$HeaderOffset + 148 + 6] = 0      # NUL
    $Bytes[$HeaderOffset + 148 + 7] = 0x20   # space
}

function Set-TarExecBits {
    # Rewrite the mode field to 0755 for executable entries in an (uncompressed) tar,
    # recomputing each patched header's checksum. Needed because a tar created on
    # Windows can't carry a Unix execute bit (NTFS has none).
    param([string] $TarPath)
    $bytes = [System.IO.File]::ReadAllBytes($TarPath)
    $pos = 0
    while ($pos + 512 -le $bytes.Length) {
        # End-of-archive marker is an all-zero block.
        $allZero = $true
        for ($i = 0; $i -lt 512; $i++) { if ($bytes[$pos + $i] -ne 0) { $allZero = $false; break } }
        if ($allZero) { break }

        $name = Get-TarString $bytes $pos 100
        $prefix = Get-TarString $bytes ($pos + 345) 155
        $full = if ($prefix) { "$prefix/$name" } else { $name }
        $typeflag = $bytes[$pos + 156]
        $size = Get-TarOctal $bytes ($pos + 124) 12

        # Only regular files ('0' or NUL typeflag).
        if (($typeflag -eq 0x30 -or $typeflag -eq 0) -and (Test-TarEntryExec $full)) {
            $mode = [byte[]](0x30, 0x30, 0x30, 0x30, 0x37, 0x35, 0x35, 0x00)  # "0000755\0"
            [Array]::Copy($mode, 0, $bytes, $pos + 100, 8)
            Set-TarChecksum $bytes $pos
        }

        $dataBlocks = [math]::Ceiling($size / 512.0)
        $pos += 512 + ([int]$dataBlocks * 512)
    }
    [System.IO.File]::WriteAllBytes($TarPath, $bytes)
}

function Compress-GZipFile {
    param([string] $InPath, [string] $OutPath)
    $in = [System.IO.File]::OpenRead($InPath)
    try {
        $out = [System.IO.File]::Create($OutPath)
        try {
            $gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
            try { $in.CopyTo($gz) } finally { $gz.Dispose() }
        }
        finally { $out.Dispose() }
    }
    finally { $in.Dispose() }
}

function Copy-LauncherLF {
    # Copy a startup script to the package as $Name, normalizing to LF (no BOM).
    param([string] $Source, [string] $Destination, [string] $Name)
    $content = Get-Content -LiteralPath $Source -Raw
    [System.IO.File]::WriteAllText((Join-Path $Destination $Name), ($content -replace "`r`n", "`n"))
}

function Write-Launcher {
    # Copies the maintained startup scripts from scripts/startup into the package.
    # run-server.ps1 ships in EVERY build; Windows also gets run-server.cmd, other
    # platforms get run-server.sh. The self-contained variants (native apphost) are
    # used when -SelfContained is set; otherwise the framework-dependent ones.
    param([string] $Destination, [string] $Rid)

    $isWindows = $Rid.StartsWith('win-')
    $pwshSrc = if ($SelfContained) { $StartupPwshSC } else { $StartupPwsh }
    $bashSrc = if ($SelfContained) { $StartupBashSC } else { $StartupBash }

    Copy-LauncherLF -Source $pwshSrc -Destination $Destination -Name 'run-server.ps1'
    if ($isWindows) {
        # The .cmd apphost invocation (bin\SubspaceServer.exe) is identical for both modes.
        Copy-Item -LiteralPath $StartupCmd -Destination (Join-Path $Destination 'run-server.cmd') -Force
    }
    else {
        Copy-LauncherLF -Source $bashSrc -Destination $Destination -Name 'run-server.sh'
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

# Default is code-only; -Full assembles the complete package.
$CodeOnly = -not $Full

foreach ($rid in $Rids) {
    Write-Host ""
    $zoneDir = if ($InPlace) { $Output } else { Join-Path $Output $rid }
    $label = if ($CodeOnly) { "Updating code for" } else { "Packaging zone for" }
    $loc = if ($InPlace) { "$rid (in place: $zoneDir)" } else { $rid }
    Write-Host "==> $label $loc" -ForegroundColor Green

    if ($CodeOnly) {
        if (-not (Test-Path -LiteralPath (Join-Path $zoneDir 'conf'))) {
            Write-Warning "'$zoneDir' has no conf/ - not an existing zone. Use -Full to build a complete package. Skipping."
            continue
        }
    }
    else {
        if ($Clean -and (Test-Path -LiteralPath $zoneDir)) {
            Remove-Item -LiteralPath $zoneDir -Recurse -Force
        }
        # 1) Zone template (conf, arenas, maps, data, ...).
        Copy-ZoneTemplate -Destination $zoneDir
    }

    # 2) Publish the host into a fresh bin/ for this RID.
    $binDir = Join-Path $zoneDir 'bin'
    if (Test-Path -LiteralPath $binDir) { Remove-Item -LiteralPath $binDir -Recurse -Force }
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

    if ($CodeOnly) {
        # Prune only the code tree; never touch the deployment's conf/maps/etc.
        Remove-Excluded -Root $zoneDir -BinOnly
    }
    else {
        # 4) Launchers + LICENSE.
        Write-Launcher -Destination $zoneDir -Rid $rid
        if (Test-Path -LiteralPath $LicenseFile) {
            Copy-Item -LiteralPath $LicenseFile -Destination (Join-Path $zoneDir 'LICENSE') -Force
        }

        # 5) Prune the assembled package per package-exclude.txt.
        Remove-Excluded -Root $zoneDir
    }

    # 6) Optional archive (.tar.gz per RID).
    if ($Archive) {
        $version = '4.0.0'
        $tar = Join-Path $ArchiveDir "SubspaceServer-$version-$rid.tar.gz"
        if (Test-Path -LiteralPath $tar) { Remove-Item -LiteralPath $tar -Force }
        # Use Windows' own tar.exe (bsdtar, ships with Win10 1803+) rather than whatever
        # 'tar' is first on PATH - a GNU tar (e.g. from Git Bash) misreads the drive-letter
        # colon in C:\... as an rsh host. Falls back to PATH 'tar' on non-Windows / if absent.
        $tarExe = 'tar'
        if ($env:OS -eq 'Windows_NT') {
            $sysTar = Join-Path $env:SystemRoot 'System32\tar.exe'
            if (Test-Path -LiteralPath $sysTar) { $tarExe = $sysTar }
        }
        if ($rid.StartsWith('win-')) {
            & $tarExe -czf $tar -C $zoneDir '.'
            if ($LASTEXITCODE -ne 0) { throw "tar exited with code $LASTEXITCODE." }
        }
        else {
            # Unix package: create an uncompressed tar, stamp Unix exec bits on the
            # apphost / launchers / native libs (a Windows-created tar can't carry them),
            # then gzip. This makes the .tar.gz extract ready-to-run on Linux/macOS.
            $tmpTar = "$tar.tmp"
            if (Test-Path -LiteralPath $tmpTar) { Remove-Item -LiteralPath $tmpTar -Force }
            & $tarExe -cf $tmpTar -C $zoneDir '.'
            if ($LASTEXITCODE -ne 0) { throw "tar exited with code $LASTEXITCODE." }
            Set-TarExecBits -TarPath $tmpTar
            Compress-GZipFile -InPath $tmpTar -OutPath $tar
            Remove-Item -LiteralPath $tmpTar -Force
        }
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
        $srcOut = [System.IO.Path]::GetFullPath((Join-Path $ArchiveDir "SubspaceServer-$version.tar.gz"))
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
