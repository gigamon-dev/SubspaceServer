# Subspace Server .NET Quickstart

### Contents

[Prerequisites](#prerequisites)<br>
[Installation](#installation)<br>
[Run the server](#run-the-server)<br>
[Connect to your zone](#connect-to-your-zone)<br>
[Take it further](#take-it-further)<br>

This article provides instructions on how to quickly get *Subspace Server .NET* up and running from scratch.

If you already have a zone running *ASSS*, see [Quickstart from ASSS](quickstart-from-asss.md) instead for instructions on how to use your existing *ASSS* files.

## Prerequisites

The server requires that .NET be installed. If you don't already have it, you can get it from: https://dotnet.microsoft.com. (Currently .NET 7)

The server can technically be run on any system supported by .NET. However, pre-built binaries are only being provided for Linux x64 and Windows x64.

> You are free to download the source code and build it for other platforms/architectures, but be aware of these known limitations:
> - The System.Data.SQLite NuGet package doesn't include native binaries for ARM64. To work around this, either build your own ARM64 binary for SQLite or do not use the persist modules (`SS.Core.Modules.PersistSQLite` and `SS.Core.Modules.Persist`).
> - The Continuum Encryption native binaries (closed source) are currently only available for Linux x64 and Windows x64. In the future, macOS and ARM64 binaries may be considered.

## Installation

1. Download the latest [release](../releases) and extract it to the location of your choice.

2. In the folder you extracted to, find the folder named `clients`. Copy `Continuum.exe` from your Continuum client's folder into the `clients` folder.

At this point, the server can technically be run and you'd be able to connect to it on port 5000. However, you'll likely want to change that.

## Configure the endpoint to Listen on

Open the `conf/global.conf` file and find the `[Listen]` section. It should look something like:

```ini
[ Listen ]
Port = 5000
BindAddress = 
```

The `Port` setting tells the server which UDP port to listen on for game requests. It also determines which port to listen on for ping requests, which is always implicitly the `Port` number + 1. Therefore, in this example, it listens for game requests on port 5000 and for ping requests on port 5001.

The `BindAddress` setting tells the server which IP address to listen on. By default, this setting is empty, meaning listen on all available network interfaces.

## Run the server

To run the server, use the included startup script which is located in the zone's root folder.

#### Linux and macOS

The script is named: `run-server.sh`. From the shell, `cd` to the folder you installed the server to, and run it:

```
./run-server.sh
```

#### Windows

The script is named: `run-server.cmd`. Run it from File Explorer, or from command line `cd` to the folder you installed the server to, and run it.

```
run-server.cmd
```

#### PowerShell

Alternatively, a PowerShell script, `run-server-ps1` is also included, in case that is your preference.

```
./run-server.ps1
```

> To shut the server down gracefully, use: `Ctrl + C`

## Connect to your zone

Start the Continuum client. On the main screen, click the **Zones** button to open the **Add/Update Zones** window. Alternatively, it can be opened by navigating on the menu bar to: **View** --> **Zones...**

On the **Add/Update Zones** window, click the "**Add Custom...**" button and fill out the fields. Fill in the **Zone Name** with a name of your choice. Fill in the **IP Address** of the server. If you're running it locally, you can use 127.0.0.1 to specify localhost. The **Port** should be the value you configured earlier, or 5000 if you skipped that step.

Confirm you choices and your zone should appear on list and you should be able to connect to it.

## Take it further

Now that you have your server running, what you do with it is up to you. 

For more information about the server, see the [User Manual](./user-manual.md). 

Here are a few topics that might be of interest:
- [How to initially set yourself up as sysop](./user-manual.md#how-to-initially-set-yourself-up-as-sysop)
- [List your zone on directory servers](./user-manual.md#list-your-zone-on-directory-servers)
- [Connect your zone to a billing server](./user-manual.md#connect-your-zone-to-a-billing-server)

If you would like to learn how to create your own plugins, see the [Developer Guide](./developer-guide.md).
