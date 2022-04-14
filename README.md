# Subspace Server .NET
A zone server for the multiplayer game Subspace based on *A Small Subspace Server*.

## About
This is a cross-platform server written in C#.  It was developed based on the *A Small Subspace Server* (*ASSS*) open-source project and can be considered a derivative.  As such, the modular design closely resembles that of *ASSS* and is meant to be extended in very much the same ways. 

This project aims to match the core functionality of *ASSS*. Contrary to its name, *ASSS* is not "small". Porting over everything in *ASSS* is not a realistic goal, therefore a choice had to be made on what is considered essential. Nearly everything needed to run a zone is available. See [asss-equivalents](./doc/asss-equivalents.md) for a more detailed listing. Also, see [additional-features](./doc/additional-features.md) for information about features that this server adds, which are not in *ASSS*.

For guidance on how to extend the server, see the [Developer Guide](./doc/developer-guide.md).

## Setup / Use
1. Open and build the solution: `"src/SubspaceServer.sln"`.
2. Setup the Zone
   1. Copy **Continuum.exe** (currently 0.40) into the zone's **'clients'** folder: `"src/SubspaceServer/Zone/clients"`.
   2. Configure it much like you would for *ASSS*.
      - arenas
      - conf
      - maps
      - news.txt
3. Run it.  
*Note: The working directory needs to be the **'Zone'** folder.*

## Dependencies
- [Google.Protobuf](https://www.nuget.org/packages/Google.Protobuf)
- [Iconic.Zlib.Netstandard](https://www.nuget.org/packages/Iconic.Zlib.Netstandard)
- [Microsoft.Extensions.ObjectPool](https://www.nuget.org/packages/Microsoft.Extensions.ObjectPool)
- [SixLabors.ImageSharp](https://www.nuget.org/packages/SixLabors.ImageSharp)
- [System.Data.SQLite.Core](https://www.nuget.org/packages/System.Data.SQLite.Core)

## License
GNU GPLv2, since that's what *A Small Subspace Server* uses and much of this can be considered as being a derivative of it.

## Thanks
This project exists by standing on the shoulders of giants. Thank you to **Grelminar** for creating *A Small Subspace Server*. This would not have been possible without his work and the inspiration it provided. Thank you to everyone that contributed to the *A Small Subspace Server* project. All of your efforts can be considered as being part of this project as well. Thank you to **POiD** for all the knowledge and help he has provided. Thank you to the creators of Subspace for making this great game, to everyone that has ever contributed in keeping the game going over the years, and to everyone that helps keep the game alive today.

## Related
- Repository: [A Small Subspace Server](https://bitbucket.org/grelminar/asss)
