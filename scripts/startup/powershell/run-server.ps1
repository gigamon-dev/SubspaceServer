# This is a startup script for running Subspace Server .NET.
# It checks the server's exit code and will automatically restart the server when necessary.
# For example, if the server is to recycle with ?recyclezone or ?shutdown -r.
#
# $zoneRoot - the root path of the server installation. By default, the location of this script.

param (
    [string]$zoneRoot = $PSScriptRoot
)

if (!(Test-Path -Path $zoneRoot)) {
    Write-Output "Directory '$zoneRoot' not found."
    exit 1
}

if (!(test-path -path "$zoneroot/bin/subspaceserver.dll")) {
    write-output "Directory '$zoneroot' is not a valid zone directory ('bin/subspaceserver.dll' not found)."
    exit 1
}

if (!(test-path -path "$zoneroot/conf")) {
    write-output "Directory '$zoneroot' is not a valid zone directory ('conf' folder not found)."
    exit 1
}

cd $zoneRoot

$continue = $true

do {
    Write-Output "$(Get-Date -Format 'u'): Starting Subspace Server .NET"

    $process = Start-Process dotnet ./bin/SubspaceServer.dll -PassThru -Wait

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
