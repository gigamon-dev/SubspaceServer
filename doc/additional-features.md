# Notable Additional Features (not in ASSS)

## Network module - Reliable packet grouping
### Overview
In ASSS, when reliable data is queued up to be sent, a reliable header is prepended to it it and assigned a sequence number. When the packet is to be sent, it may get combined **into** a Grouped (0x00 0x0E) packet. That is, the reliable packet can be placed inside of a grouped packet as one of the grouped packet's items. Grouped packets are a nice feature of the Subspace protocol. They substantially reduce the # of UDP datagrams needed to be sent. However, there is room for improvement in the algorithm ASSS uses.

Consider the case when there are multiple pieces of data that need to be send reliably. ASSS will buffer up each piece of data, each with an additional reliable header (with unique sequence number), into the outgoing queue. A payload containing this would look like:

    [ grouped packet header (2 bytes) ]
    [ grouped packet item header 1 (1 byte) ]
    [ reliable header 1 (6 bytes, including 4 byte sequence #) ]
    [ reliable data 1 ]
    [ grouped packet item header 2 (1 byte) ]
    [ reliable header 2 (6 bytes, including 4 byte sequence #) ]
    [ reliable data 2 ]
    ...
    [ grouped packet item header N (1 byte) ]
    [ reliable header N (6 bytes, including 4 byte sequence #) ]
    [ reliable data N ]

The overhead (bytes) of the grouped packet containing reliable packets is:<br>2 + ((1 + 6) * # of reliable packets)

What if we could reverse it? That is, use one reliable packet, containing one grouped packet, containing the data. The payload would look like:

    [ reliable header (6 bytes, including 4 byte sequence #) ]
    [ grouped packet header (2 bytes)]
    [ grouped packet item header 1 (1 byte) ]
    [ reliable data 1 ]
    [ grouped packet item header 2 (1 byte) ]
    [ reliable data 2 ]
    ...
    [ grouped packet item header N (1 byte) ]
    [ reliable data N ]

The overhead (bytes) of reliable packet containing grouped packet is:<br>6 + 2 + (1 * # of reliable packets)

Here's how much of a difference there is in overhead:
| # Data Items | Grouped containing reliable | Reliable containing grouped |
| --- | --- | --- |
|2|16|10|
|3|23|11|
|4|30|12|
|5|37|13|
|6|44|14|
|7|51|15|
|8|58|16|
|9|65|17|
|10|72|18|

On top of that, since it's using just a single reliable packet, there is only 1 sequence # that needs to be ACK'd. So, there is some savings from less incoming data too.

### When are there multiple pieces of reliable data being sent?
This occurs more often than you'd expect. Here are some examples:
- When a player enters an arena, there is a lot of data sent reliably.
- When the server sends multiple lines of chat messages. E.g. player runs a command or game stats are listed.
- When the server sends multiple lvz object changes.
- etc.. 

### How it's implemented
When reliable data is queued, instead of appending the reliable header, the data itself is added to an "unsent" reliable outgoing queue. This is a separate queue
from the regular outgoing reliable queue.  When it's time to send reliable data, 
it reads from the "unsent" queue and moves those items into the regular 
outgoing queue. When it does this move, it tries to group up the packets. The reliable header is added (with the single the sequence number), and the result is placed in the regular reliable outgoing queue.

Additionally, there is a setting to limit the grouping of reliable packets such that, the resulting reliable packet containing a grouped packet, can itself can fit into another grouped packet. This is off by default, since I think it is still more efficient to group as many as possible to reduce the amount of sequence numbers used.

---

## Network module - Enhanced sending of sized data
Sending of sized data is implemented completely differently than in ASSS. This was to resolve many issues including:
- A race condition where sized data packets (0x0A) could be sent after a sized cancellation ACK (0x00 0x0C).
- A race condition where memory is used after it is freed.
- File I/O being performed on the mainloop thread.

Additionally, ASSS uses a timer to queue sized data to be sent. The timer limits the maximum possible transfer rate.
This doesn't have that limitation. When receiving an ACK for a sized data packet, it can immediately queue up more data
and optionally, attempt to send that data.

---

## Network module - Maximum packet length
The Subspace protocol allows for packets to have a maximum length of 520 bytes.
ASSS only sends up to 512 bytes, with larger packets getting fragmented using big data packets (0x00 0x08 and 0x00 0x09).
Subspace Server .NET supports the full 520 byte length, including handling when the data is sent reliably. This allows for the following to be sent in a single packet:
- A full static flag packet (0x22) containing 256 flags. The max length being: 1 + (256 * 2) = 513. Plus the 6 byte reliable header = 519 bytes.
- A full brick packet (0x21) containing 32 bricks. The max length being: 1 + (32 * 16) = 513. Plus the 6 byte reliable header = 519 bytes.
- A full periodic reward packet (0x23) containing 128 teams. The max length being 1 + (128 * 4) = 513. Plus the 6 byte reliable header = 519 bytes.

---

## Network module - Incoming sequence number overflow
For incoming reliable data, ASSS doesn't handle when sequence numbers overflow and wrap back around. This issue is addressed in Subspace Server .NET. Note, getting to the overflow limit is highly unlikely, especially for player connections. However, for a client connection to a billing server it is possible if connected long enough.

---

## Network module - Big data receive limit
When big data (0x00 0x08 and 0x00 0x09) is received, it is buffered into memory until ending the 0x00 0x09 packet is received. There is a limit on the amount of data allowed to be buffered. When ASSS hits that limit, it completely discards the big data in such a way that it forgets that it was receiving a big data transfer. This means any additional big data packets will be seen as the start of a new transfer. In Subspace Server .NET, this scenario is handled in such a way that the big data transfer is not forgotten. It just continues to ignore additional data, until the 0x00 0x09 packet is received.

---

## Network module - Connection stats for client connections

Added the ability to get conneciton stats for client connections. Updated the BillingUdp module to output the stats in the `?userdbadm` command. This should give some insight into the quality of the billing server connection. Note, it's not full lag stats like for players.

---

## Network module - Configurable incoming reliable window size

Added the ability to configure the size of the incoming reliable data buffer.
- `Net:PlayerReliableReceiveWindowSize` for player connections.
- `Net:ClientConnectionReliableReceiveWindowSize` for client connecitons.

This might be useful for client connections (to a billing server), where most data is transferred reliably and there is a lot of it (chat messages). Though, even for that it has limited usefulness since it depends on the other end's send window size.

---

## ConfigManager - Enhanced automatic config file reload on modification
ASSS does detect when a conf file is modified and will reload it. However, it only does this for "root" conf files. Any additional conf files pulled in from #include statements are not watched.

Subspace Server .NET watches all used conf files, including those from nested #include statements. When a conf file is modified, it will trigger the reload of all the root conf files that use it (including indirectly, through nested #include statements).

> The terminology Subspace Server .NET uses for 'root' conf files is "ConfDocument".  See the [SS.Core.Configuration.ConfDocument](../src/Core/Configuration/ConfDocument.cs) class.

---
## ConfigManager - Changes saved to proper location
When ASSS modifies a conf file, it just adds an override setting to the end of a 'root' conf file. Instead, Subspace Server .NET has the ability to locate the proper location to make the change. In other words, it can determine which conf file to change (even within #include files), find the proper section to write to, and even overwrite/update an existing setting.

---
## ArenaManager - opening/reading of arena.conf on a worker thread
In ASSS, the arena.conf is opened on the mainloop thread timer (in `arenaman.c` see the `ProcessArenaStates` function which calls `cfg->OpenConfigFile()`). This is file I/O, which is slower and can block. Therefore, in Subspace Server .NET this is done on a worker thread instead.

---
## ArenaManager - Player Entering packet to include multiple players
When a player enters an arena, the server sends a list of all players currently in the arena. This is done with the 0x03 (Player Entering) game packet which is sent reliably. ASSS sends (queues up) one packet for each player in the arena. E.g., if there were 150 players in the arena, it queues up 150 separate packets. In turn, the network module will send these reliable packets in grouped packets (albeit with the limitations described in [Network module - reliable packet grouping](#network-module---reliable-packet-grouping)).

The 0x03 (Player Entering) game packet allows for sending multiple in 1 packet. That is, the entire 0x03 is can be repeated for each player, sent in 1 jumbo packet. 
Subspace Server .NET does this, and depending on the resulting size, the Network module may split the jumbo packet
using 0x00 0x08 / 0x00 0x09 'big' packets. This results in fitting slightly more
data per packet than using reliably sent grouped packets. Also, it reduces the # of buffers required for queuing data too.

---
## Bricks module - send multiple bricks in one brick packet
The 0x21 (Brick) packet allows sending information for multiple bricks at one time by repeating the data portion (header not repeated). ASSS only queues up 1 brick per brick packet. Subspace Server .NET utilizes the ability to send multiple bricks per brick packet.

When a player enters an arena, the server sends all of the currently active bricks to that player. In the worst case scenario (there being the maximum # of active bricks, 256), ASSS would queue up 256 separate brick packets, using 256 buffers. Whereas Subspace Server .NET can send it using 9 buffers.

It is also possible to have a brick mode that drops multiple bricks at once. The bricks
will be combined in this scenario too.

---
## Stats module - global (zone-wide) player stats
The ASSS stats module only tracks per-arena stats. In Subspace Server .NET, the Stats module also tracks global (zone-wide) stats. This way it is possible to tell how long a player has been connected to the zone, not just a particular arena or arena group.

---
## Stats module - additional data types available
The ASSS `stats` module stores a stat as an Int32. This is also true for its "timer" stats which track # of seconds elapsed.

In Subspace Server .NET the Stats module currently supports the following data types: Int32, Int64, UInt32, UInt64, DateTime, and TimeSpan (for "timer" stats). Since the "timer" stats use a TimeSpan, the granularity is no longer limited to seconds.

Also, the stats module is using Google's [Protocol Buffers (protobuf)](https://developers.google.com/protocol-buffers) to serialize data. So it's possible that there is some size savings from the use of variable length encodings.

---
## Replay module - numerous enhancements
In addition to the functionality of the ASSS `record` module, the `SS.Replay.ReplayModule` contains functionality for recording and playing back events for:
- balls (based on the PowerBall Zone fork of the `record` module)
- bricks (based on the PowerBall Zone fork of the `record` module)
- flags (both static flags and carryable flags)
- crowns
- door & green seeds (door timings will be in sync, greens will gradually become in sync)
- additional chat message types: public macros and team chat

Also provided are the following arena configuration settings:<br>
```ini
[ Replay ]
; Notification settings
NotifyPlayback = Arena
NotifyPlaybackError = Player
NotifyRecording = Player
NotifyRecordingError = Player

; Playback settings
PlaybackMapCheckEnabled = 1
PlaybackSpecFreqCheckEnabled = 1
PlaybackLockTeams = 0

; Chat settings (recording)
RecordPublicChat = 1
RecordPublicMacroChat = 1
RecordSpecChat = 1
RecordTeamChat = 1
RecordArenaChat = 1

; Chat settings (playback)
PlaybackPublicChat = 1
PlaybackPublicMacroChat = 1
PlaybackSpecChat = 1
PlaybackTeamChat = 1
PlaybackArenaChat = 1
```
Use the ?man command in-game for more details on these settings.
> `?man Replay:`<br>
> `?man Replay:NotifyPlayback`

Underneath the scenes, the module does threading in a safer manner too.
- All file operations (including opening of streams) are done on a worker thread. 
- The playback thread queues up mainloop workitems.

Use the ?replay command to control recording and playback. The basic commands are:
> `?replay record <file>`<br>
> `?replay play <file>`<br>
> `?replay stop`

Use the ?man command in-game for more information about the command:
> `?man replay`

---
## KOTH (King of the Hill) and Crowns modules
The ASSS `koth` module has crown logic baked into it. In Subspace Server .NET, crown functionality has its own module, `SS.Core.Modules.Crowns`. This allows crowns to potentially be used for other purposes than KOTH. Note, the crown functionality currently supports assigning crowns to players such that it is visible to all players in the arena. This could potentially be upgraded to allow crown assignments that are visible to a subset of players, rather than the entire arena.

The ASSS `koth` module uses a timer, which runs every 5 seconds, to check for a win condition. It can only tell which players lost their crown in that 5 second window. As such, it considers those players to all have lost their crowns simutaneously. In Subspace Server .NET, KOTH is implemented differently. It keeps track of the time when each player's crown expires and uses that to determine a winner. This means it should be more accurate on who the winner is.

---
## SpeedGame module
ASSS does not have Speed Zone functionality. In Subspace Server .NET, speed functionality is provided by the `SS.Core.Modules.Scoring.SpeedGame` module. This includes use of the S2C 0x24 (Speed packet). Also, it persists a player's personal best score (?best command).

---
## Flag games
### Available modes
ASSS only provides a Warzone style flag game (`fg_wz` module) and a Turf Zone style flag game (`fg_turf` module). 

Subspace Server .NET includes additional logic to support Jackpot Zone, Running Zone, and Rabbit Zone style games as well. Also, for Warzone style games, it supports playing victory music (Misc:VictoryMusic).

### Design difference - extensibility
Flag games in Subspace Server .NET are implemented differently than in ASSS. ASSS has a single `flagcore` module. Instead, Subspace Server .NET has separate modules: a `CarryFlags` module for game modes where flags can be carried (Warzone, Jackpot, Running, Rabbit) and a `StaticFlags` module for flag games where flags are static, on the map (Turf). This allows each module to specialize in what they're for.

The `CarryFlags` module includes an extensibility point via the `ICarryFlagsBehavior` interface. A default implementation is provided which includes the standard flag behavior that is expected for Warzone, Jackpot Zone, Running Zone, and Rabbit Zone. It is possible to create a custom `ICarryFlagsBehavior` implementation where flags behave differently. For example, it could be possible to build implement a behavior where flags act like those in the ThreeWave CTF mod for Quake (or Team Fortress's CTF where the flag is an intelligence briefcase), or a game mode like Quake 3 Team Arena's 'Harvester' mode.

---
## ?laghist command
ASSS tracks many statistics for lag data. This includes the distribution of ping times for C2S and reliable data. ASSS planned to include a `?laghist` command to output this data. However, it was never implemented. In Subspace Server .NET the `?laghist` command is implemented in a way that it's presumed to have been intended. By default, it prints C2S ping stats. With the `-r` argument it prints reliable ping stats.
