# Subspace Server .NET Quickstart from ASSS

### Contents

[Prerequisites](#prerequisites)<br>
[Installation](#installation)<br>
[Transition to Modules.config](#transition-to-modulesconfig)<br>
[Modules Attached to Arenas](#modules-attached-to-arenas)<br>
[Run the server](#run-the-server)<br>
[Take it further](#take-it-further)<br>

This article provides instructions on how to quickly get *Subspace Server .NET* up and running for those that already have a zone using *ASSS* and would like to try to hosting it using *Subspace Server .NET*.

If you don't already have a zone running *ASSS*, see [Quickstart](./quickstart.md) instead for instructions on how to set up from scratch.

## Prerequisites

The server requires that .NET be installed. If you don't already have it, you can get it from: https://dotnet.microsoft.com. (Currently .NET 7)

The server can technically be run on any system supported by .NET. However, pre-built binaries are only being provided for Linux x64 and Windows x64.

> You are free to download the source code and build it for other platforms/architectures, but be aware of these known limitations:
> - The System.Data.SQLite NuGet package doesn't include native binaries for ARM64. To work around this, either build your own ARM64 binary for SQLite or do not use the persist modules (`SS.Core.Modules.PersistSQLite` and `SS.Core.Modules.Persist`).
> - The Continuum Encryption native binaries (closed source) are currently only available for Linux x64 and Windows x64. In the future, macOS and ARM64 binaries may be considered.

## Installation

Download the latest [release](https://github.com/gigamon-dev/SubspaceServer/releases) and extract it to the location of your choice.

Copy files from your ASSS zone including:
- The `clients` folder (which should contain the Continuum.exe client)
- The `maps` folder
- The `arenas` folder
- `news.txt`
- `obscene.txt`
- `conf/passwd.conf`
- `conf/staff.conf`
- any additional .conf files under `conf/` that your arenas #include

## Transition to Modules.config

ASSS uses the `conf/modules.conf` file to determine the which modules to load and the order to load them in. Subspace Server .NET uses `conf/Modules.config`, which is similar, but is an XML file.

Module names are slightly different in Subspace Server .NET. It should be relatively obvious which modules you'll want to load compared to those being  loaded for your existing ASSS zone. In fact, the default load order is more or less the same. If in doubt, see [asss-equivalents](asss-equivalents.md) for a mapping of ASSS modules to Subspace Server .NET modules.

## Modules Attached to Arenas

In an arena.conf file, the `Modules:AttachModules` setting is used to attach modules to the arena. The value of this setting differs slightly, so you'll need to edit each of the arena.conf files that includes this setting.

In ASSS the setting looks like:
```INI
[ Modules ]
AttachModules = \
	points_kill \
	points_flag \
	points_goal \
	buy
```

The setting is similar in Subspace Server .NET, except that the module names are different. Here's the equivalent of the above for Subspace Server .NET:
```INI
[ Modules ]
AttachModules = \
	SS.Core.Modules.Scoring.KillPoints \
	SS.Core.Modules.Scoring.FlagGamePoints \
	SS.Core.Modules.Scoring.BallGamePoints \
	SS.Core.Modules.Buy
```

Notice the modules differ slightly in name, and they include the full namespace. See [asss-equivalents](asss-equivalents.md) for a mapping of ASSS modules to Subspace Server .NET modules.

## Run the server

To run the server, use the included startup script which is located in the zone's root folder.

#### Linux and macOS

The script is named: `run-server.sh`. From the shell, `cd` to the folder you installed the server to, and run it:

```
./run-server.sh
```

#### Windows

The script is named: `run-server.cmd`. Run it from File Explorer, or from the command line `cd` to the folder you installed the server to, and run it.

```
run-server.cmd
```

#### PowerShell

Alternatively, a PowerShell script, `run-server-ps1` is also included, in case that is your preference.

```
./run-server.ps1
```

## Take it further

For more information about the server, see the [User Manual](./user-manual.md). 

To learn how to create your own plugin modules, see the [Developer Guide](./developer-guide.md).
