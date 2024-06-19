# Subspace Server .NET
A zone server for the multiplayer game Subspace based on *A Small Subspace Server*.

## About
This is a cross-platform server written in C#.  It was developed based on the *A Small Subspace Server* (*ASSS*) open-source project and can be considered a derivative.  As such, the modular design closely resembles that of *ASSS* and is meant to be extended in very much the same ways. 

This project provides everything needed to host a zone. It has all of the essential functionality from *ASSS* and also many of the extras. 

- See [asss-equivalents](./doc/asss-equivalents.md) for a detailed listing and comparison of included parts.
- Also, see [additional-features](./doc/additional-features.md) for information about features that this server adds, which are not in *ASSS*.

## Get Started

[Download](https://github.com/gigamon-dev/SubspaceServer/releases) the latest release.

- Follow the [Quickstart](./doc/quickstart.md) for instructions on how to get up and running from scratch.
   - If you already have a zone running using *ASSS*, see [Quickstart from ASSS](./doc/quickstart-from-asss.md) instead. It has instructions on how to use your existing files.
- Read the [User Manual](./doc/user-manual.md) for more information.

## Build and Extend

The built-in functionality of the server is more than enough to host a zone. It's highly configurable and supports all the regular gameplay modes. However, the true power of this project is in how it can be extended by writing plugins.

For guidance on how to develop your own plugins to extend the server, see the [Developer Guide](./doc/developer-guide.md).

To build the server,
- Get the code:

   ```
   git clone https://github.com/gigamon-dev/SubspaceServer.git
   ````

- Open the solution: `"src/SubspaceServer.sln"` to build and run it.
- Remember to place a copy of **Continuum.exe** (currently 0.40) into the zone's **'clients'** folder: `"src/SubspaceServer/Zone/clients"` before running.

## Dependencies
- [CommunityToolkit.HighPerformance](https://www.nuget.org/packages/CommunityToolkit.HighPerformance) - for reducing string allocations by using StringPool
- [Google.Protobuf](https://www.nuget.org/packages/Google.Protobuf) - for serializing data
- [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) - for an embedded database that persists player and arena data
- [Microsoft.Extensions.ObjectPool](https://www.nuget.org/packages/Microsoft.Extensions.ObjectPool) - object pooling to reduce allocations and the need to garbage collect
- [Microsoft.IO.RecyclableMemoryStream](https://www.nuget.org/packages/Microsoft.IO.RecyclableMemoryStream) - for an improved MemoryStream with regards to performance and garbage collection
- [Npgsql](https://www.nuget.org/packages/Npgsql) - for connecting to a PostgreSQL database (optional matchmaking functionality)
- [SkiaSharp](https://www.nuget.org/packages/SkiaSharp) - for creating images of maps
- [System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing) - for a CRC-32 implementation compatible with zlib's

## License
GNU GPLv2, since that's what *A Small Subspace Server* uses and much of this can be considered as being a derivative of it.

## Thanks
This project exists by standing on the shoulders of giants. Thank you to **Grelminar** for creating *A Small Subspace Server*. This would not have been possible without his work and the inspiration it provided. Thank you to everyone that contributed to the *A Small Subspace Server* project. All of your efforts can be considered as being part of this project as well. Thank you to **POiD** for all the knowledge and help he has provided. Thank you to the creators of Subspace for making this great game, to everyone that has ever contributed in keeping the game going over the years, and to everyone that helps keep the game alive today.

## Related
- Repository: [A Small Subspace Server](https://bitbucket.org/grelminar/asss)
