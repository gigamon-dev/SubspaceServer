# Subspace Server .NET
A zone server for the multiplayer game Subspace Continuum based on *A Small Subspace Server*.

## About
This is a cross-platform server written in C#.  It was originally made for the .NET Framework, but now targets .NET Core.  Though primarly developed on Windows, it has been briefly tested on macOS and Linux.

This project was developed based on the *A Small Subspace Server* open-source project.  Many parts can almost be considered a direct port from C to C#.  While other parts use completely different logic to achieve similar goals.

This server only contains the basic functionality needed for a player to connect, download news.txt, download maps, and play. *A Small Subspace Server* has much more functionality which is far ouside the scope of this project.

## Purpose
This was created due to curiosity, for knowledge, and for fun. It is not intended to be used to host an actual zone. In fact, it was just a personal fun project that was never meant for the eyes of others.

The question that spurred the creation of this was: *Can it be done?*  
The answer: *Yes*

## License
GNU GPLv2, since that's what *A Small Subspace Server* uses and much of this can be considered as being a derivative of that.

## Dependencies
- NuGet package: [Iconic.Zlib.Netstandard](https://www.nuget.org/packages/Iconic.Zlib.Netstandard)

## Setup / Use
1. Open and build the solution: `"src/SubspaceServer.sln"`.
2. Setup the Zone
   1. Copy **Continuum.exe** (currently 0.40) into the zone's **'clients'** folder: `"src/SubspaceServer/Zone/clients"`.
   2. Configure it much like you would for *A Small Subspace Server*.
      - arenas
      - conf
      - maps
      - news.txt
3. Run it.  
*Note: The working directory needs to be the **'Zone'** folder.*

## Related
- Repository: [A Small Subspace Server](https://bitbucket.org/grelminar/asss)
