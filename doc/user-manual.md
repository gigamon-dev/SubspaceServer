# Subspace Server .NET User Manual

> This is a work in progress.

## Directory structure

```
+-- arenas
|    +-- (public)
|    | arena.conf
|    +-- (default)
+-- bin
|    +-- modules
+-- clients
+-- conf
|    +-- groupdef.dir
|    + global.conf
|    + groupdef.conf
|    + Modules.config
|    + passwd.conf
|    + staff.conf
+-- data
+-- log
+-- maps
+-- recordings
+-- tmp
+ news.txt
+ obscene.txt
+ scrty
+ scrty1
```

TODO: Add info about each folder and notable files.

## Running the Server

The server is a .NET application and can be run using the `.NET CLI`.

From the zone root directory run:

```
dotnet ./bin/SubspaceServer.dll
```

The server also accepts passing optional arguments:

```
dotnet ./bin/SubspaceServer.dll [<zone root>] [-i]`
```

Alternatively, a default host executable is provided:

#### Linux or macOS

`./bin/SubspaceServer [<zone root>] [-i]`

#### Windows

`./bin/SubspaceServer.exe [<zone root>] [-i]`

### Command line arguments

`<zone root>`<br>
The root path of the zone.

`-i`<br>
To use interactive mode.


> Unlike *ASSS*, this server does not support running chrooted on Linux.

### Use a Startup Script

Startup scripts are provided that start the server. They will restart the server if the server wants to reccyle (e.g. due to a `?recyclezone` or `?shutdown -r`).

#### Linux and macOS

```
./run-server.sh
```

#### Windows (cmd)

```
run-server.cmd
```

#### PowerShell

```
./run-server.ps1
```

## Modules

## Access Control

Getting started

TODO: describe how to initially set yourself up with a password and add yourself as a sysop

TODO: Groups and capabilities

`conf/staff.conf`
`conf/passwd.conf`

Groups:
- default : regular user
- mod : moderator
- smod : super moderator
- bot : non-human, bot application connecting as a privileged user
- sysop : highest access level available, can manage files directly with `?putfile`, `?getfile`, etc...

### Advanced Access Control

> Works exactly the same as in ASSS. See the ASSS [userguide.pdf](https://asss.minegoboom.com/files/userguide.html#%_sec_4)

TODO: describe `conf/groupdef.conf` and the `conf/groupdef.dir` directory

In `conf/groupdef.conf` sections are group names. Individual lines within a section are capabilities. There are 3 types of capabilities:

#### Command capabilties

Determine which commands a user can run.

- cmd_*&lt;command name&gt;*

- privcmd_*&lt;command name&gt;*

- rprivcmd_*&lt;command name&gt;*

#### Hierarchy capabilities

Are in format: 
> `higher_than_`*&lt;group name&gt;*

  For example: 
- higher_than_default
-  higher_than_mod
- higher_than_smod

#### Special bulit-in capabilities
- **seemodchat** - 
- **sendmodchat** - 
- **sendsoundmessages** - 
- **uploadfile** - 
- **seesysoplogall** - 
- **seesysoplogarena** - 
- **seeprivarena** - 
- **seeprivfreq** - 
- **suppresssecurity** - 
- **bypasssecurity** - 
- **broadcastbot** - 
- **broadcastany** - 
- **invisiblespectator** - 
- **unlimitedchat** - 
- **changesettings** - 
- **isstaff** - 
- **seeallstaff** - 
- **forceshipfreqchange** - 
- **excludepopulation** - 
- **bypasslock** - 
- **seenrg** - 
- **seeepd** - 
- **setbanner** - 

## Logging

TODO: describe the logging system

> Works exactly the same as in ASSS. See the ASSS [userguide.pdf](https://asss.minegoboom.com/files/userguide.html#%_sec_5)

## Configuration

The server is highly configurable. Here are some of the most important changes you'll probably want to make.

### Listen endpoint(s)

Open the `conf/global.conf` file and find the `[Listen]` section. It should look something like:
```ini
[ Listen ]
Port = 5000
BindAddress = 
AllowVIE = 1
AllowCont = 1
ConnectAs = 
```
The `Port` setting tells the server which UDP port to listen on for game requests. In this example, it listens on port 5000. Also, ping requests are listened to on the `Port` number + 1. So in this example, the server will listen for ping requests on port 5001.

The `BindAddress` setting tells the server which IP address to listen on. By default, this setting is empty, meaning listen on all network interfaces.

The `AllowVIE` setting controls whether to allow VIE protocol clients. Note, this includes the original Subspace 1.34 and bots.

The `AllowCont` setting controls whether to allow Continuum clients. You'll want to leave this set to 1.

The `ConnectAs` setting tells which arena to send players to, empty means use the first available public arena (e.g. as if you typed ?go when in game without specifying an arena name).

> Like ASSS, the server supports listening on multiple endpoints simultaneously by configuring multiple listen sections (e.g. `[Listen]`, `[Listen1]`, `[Listen2]`, and so on) but is is recommended to just use the single `[Listen]` (no number) endpoint since it is used when connecting to a Billing server.

### How to initially set yourself up as sysop

These instructions describe how to set up your zone after initially installing it, to allow yourself to login as a sysop.

Verify that the `SS.Core.Modules.AuthFile` module is being loaded by opening the `conf/Modules.config` file and making sure it's included. If this is a fresh install of the server, then it should already be there. This is what the line should look like:

```ini
<module type="SS.Core.Modules.AuthFile, SS.Core" />
```

Open the `conf/staff.conf` file. Find the `[(global)]` section and add a line to it that says:

```ini
 <your name> = sysop
 ```

 where `<your name>` is the username you want to be able to login as sysop with.

Open the `conf/passwd.conf` file. Find the `[General]` section and verify that the following settings are set to `yes`. 
```ini
AllowUnknown = yes
RequireAuthenticationToSetPassword = no
```
> Turning off `RequireAuthenticationToSetPassword` is dangerous and should only be used temporarily. At the end of these instuctions we will turn it back on. DO NOT SKIP THAT STEP!

Start the server up and login with the username you configured earlier. When in-game, set your password using the following command, filling in `<password>` with the password you want to use:

```
?local_password <password>
```

Log out and reconnect to the zone with the password you just set. You should now be logged in as sysop. To verify, run the following command in-game:

```
?getgroup
```

and the server should respond with:
```
You are in group sysop.
```

Open the `conf/passwd.conf` file. Find the `[General]` section and change the `RequireAuthenticationToSetPassword` setting to `yes`.

```ini
RequireAuthenticationToSetPassword = yes
```

### How to add a mod / smod

TODO:


### List your zone on Directory server(s)

To list your zone on Directory servers, so that others can find it, do the following:

Open the `conf/global.conf` file and fill in the `[Directory]` section. It should look like:
```INI
[ Directory ]
Name = test zone
Description = this is a test zone.
Password = 

Server1 = sscentral.sscuservers.net
Server2 = sscentral.trenchwars.org
```
These settings allow you to set a `Name` and `Description` for your zone and choose which directory servers you want to publish to. Do not change `Password` setting (leave it empty), unless you know what you're doing. Certain zone name prefixes such as "SSC" are reserved and require a password.

Open the `conf/Modules.config` file and uncomment the line for `SS.Core.Modules.DirectoryPublisher`. This tells the server to load the DirectoryPublisher module. Here's what the line should look like after uncommenting it:
```XML
   <!-- DirectoryPublisher: To add your zone to directory servers. Remember to configure [Directory] settings in global.conf. -->
	<module type="SS.Core.Modules.DirectoryPublisher, SS.Core" />
```

### Connect your zone to a Billing server

If you have access to a billing server, you can connect your zone to it using the `SS.Core.Modules.BillingUdp`. To set it up, first edit your `conf/Modules.config` to load the module.

```
<module type="SS.Core.Modules.BillingUdp"/>
```

Next, open the `conf/global.conf` file and edit the `[Billing]` section.

```INI
[ Billing ]
IP = 127.0.0.1
Port = 1850
ServerName = Test zone
Password = bill
ServerID = 0
GroupID = 1
ScoreID = 0
```

The administrator of the billing server should be able to provide you with the setting values to use.

- `IP` - the IP address of the billing server
- `Port` - the port of the billing server
- `ServerName` - the name to use for your zone on the billing server
- `Password` - the password to connect to the billing server with

### Arena Attachable modules

Certain modules need to be attached to an arena for their functionality to be made active. This allows for a more fine grained control over which functionality to use available in each arena, rather be active server-wide (every arena). The downside is that it requires a bit more configuration.

For example, one arena might host a ball game, and there you would likely want to use the `SS.Core.Modules.Scoring.BallGamePoints` module to handle scoring of the ball game. Whereas, another arena might host a capture the flag game, and it would likely want to use `SS.Core.Modules.Scoring.FlagGamePoints`.

In an `arena.conf`, attached modules are specified through the `Modules:AttachModules` setting. For example, for an arena hosting a capture the flag game, the setting might look like:

```ini
[ Modules ]
AttachModules = \
	SS.Core.Modules.Scoring.KillPoints \
	SS.Core.Modules.Scoring.FlagGamePoints
```

That is, functionality for awarding points for kills is enabled, and functionality for running the flag game (determining a winner and awarding points) is enabled.

> For a module to be attached to an arena, it needs to have been loaded. So, remember to include the module in the `conf/Modules.config` for it to be loaded on startup.

Here's a list of the included modules that support attaching to arenas:

| Module | Description |
| --- | --- |
| `SS.Core.Modules.Buy` | Ability to ?buy prizes. |
| `SS.Core.Modules.Scoring.BallGamePoints` | Scoring for ball games. |
| `SS.Core.Modules.Scoring.FlagGamePoints` | Scoring for flag games. |
| `SS.Core.Modules.Scoring.KillPoints` | Scoring for kills. |
| `SS.Core.Modules.Scoring.Koth` | "King of the Hill" game mode / scoring. |
| `SS.Core.Modules.Scoring.SpeedGame` | "Speed" game mode / scoring. |
| `SS.Core.Modules.Enforcers.LegalShip` | Enforces legal ships by freq. |
| `SS.Core.Modules.Enforcers.LockSpec` | Enforces that players can only spectate. |
| `SS.Core.Modules.Enforcers.ShipChange` | Enforces rules for changing ships. |
| `SS.Matchmaking.Modules.OneVersusOneStats` | Stats for 1v1 matches. For use in conjunction with `SS.Matchmaking.Modules.OneVersusOneMatch`. |
| `SS.Matchmaking.Modules.TeamVersusStats` | Stats for team matches. For use in conjunction with `SS.Matchmaking.Modules.TeamVersusMatch` |
