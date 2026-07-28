# Startup script for a self-contained Subspace Server .NET build.
# It checks the server's exit code and will automatically restart the server when necessary.
# For example, if the server is to recycle with ?recyclezone or ?shutdown -r.
#
# This variant runs the native apphost directly and does NOT require the .NET runtime to
# be installed. For framework-dependent builds, use run-server.ps1.
#
# $zoneRoot - the root path of the server installation. By default, the location of this script.

param (
    [string]$zoneRoot = $PSScriptRoot
)

if (!(Test-Path -Path $zoneRoot)) {
    Write-Output "Directory '$zoneRoot' not found."
    exit 1
}

# The native apphost is 'SubspaceServer.exe' on Windows and 'SubspaceServer' elsewhere.
$appHost = Join-Path $zoneRoot 'bin/SubspaceServer.exe'
if (!(Test-Path -Path $appHost)) {
    $appHost = Join-Path $zoneRoot 'bin/SubspaceServer'
}

if (!(Test-Path -Path $appHost)) {
    Write-Output "Directory '$zoneRoot' is not a valid self-contained zone directory ('bin/SubspaceServer' not found)."
    exit 1
}

if (!(Test-Path -Path "$zoneRoot/conf")) {
    Write-Output "Directory '$zoneRoot' is not a valid zone directory ('conf' folder not found)."
    exit 1
}

cd $zoneRoot

$continue = $true

do {
    Write-Output "$(Get-Date -Format 'u'): Starting Subspace Server .NET"

    $process = Start-Process $appHost -PassThru -Wait

    switch ($process.ExitCode) {
        0 {
            $message = "shutdown"
            $continue = $false
            break;
        }

        1 {
            $message = "recycle, restarting"
            break;
        }

        2 {
            $message = "unknown general error"
            $continue = $false
            break;
        }

        3 {
            $message = "out of memory, restarting"
            break;
        }

        4 {}
        5 {
            $message = "error loading modules"
            $continue = $false
            break;
        }

        default {
            $message = "unknown exit code: $($process.ExitCode)"
            $continue = $false
            break;
        }
    }

    Write-Output "$(Get-Date -Format 'u'): Subspace Server .NET exited: $message"

} while($continue)
