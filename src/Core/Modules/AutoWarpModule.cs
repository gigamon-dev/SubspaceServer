using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.ComponentInterfaces;
using SS.Core.ComponentCallbacks;
using SS.Core.Map;
using SS.Utilities;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class AutoWarpModule : IModule
    {
        private ModuleManager _mm;
        private IArenaManagerCore _arenaManager;
        private IGame _game;
        private IMapData _mapData;

        private struct AutoWarpChunk
        {
            static AutoWarpChunk()
            {
                DataLocationBuilder locationBuilder = new DataLocationBuilder();
                x = locationBuilder.CreateInt16DataLocation();
                y = locationBuilder.CreateInt16DataLocation();
                LengthWithoutArena = locationBuilder.NumBytes;
                arenaName = locationBuilder.CreateDataLocation(16);
                LengthWithArena = locationBuilder.NumBytes;
            }

            private static readonly Int16DataLocation x;
            private static readonly Int16DataLocation y;
            private static readonly int LengthWithoutArena;
            private static readonly DataLocation arenaName;
            private static readonly int LengthWithArena;

            private ArraySegment<byte> _data;

            public AutoWarpChunk(ArraySegment<byte> data)
            {
                _data = data;
            }

            public short X
            {
                get { return x.GetValue(_data.Array, _data.Offset);  }
            }

            public short Y
            {
                get { return y.GetValue(_data.Array, _data.Offset); }
            }

            public string ArenaName
            {
                get
                {
                    // TODO: replace with better string reading routine
                    return Encoding.ASCII.GetString(_data.Array, arenaName.ByteOffset, arenaName.NumBytes);
                }
            }

            public bool HasArena
            {
                get
                {
                    return _data.Count == LengthWithArena && !string.IsNullOrEmpty(ArenaName);
                }
            }
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies { get; } = new Type[]
        {
            typeof(IArenaManagerCore), 
            typeof(IGame), 
            typeof(IMapData), 
        };

        bool IModule.Load(ModuleManager mm, IReadOnlyDictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;
            _arenaManager = interfaceDependencies[typeof(IArenaManagerCore)] as IArenaManagerCore;
            _game = interfaceDependencies[typeof(IGame)] as IGame;
            _mapData = interfaceDependencies[typeof(IMapData)] as IMapData;

            MapRegionCallback.Register(mm, mapRegionHandler);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            MapRegionCallback.Unregister(mm, mapRegionHandler);
            return true;
        }

        #endregion

        private void mapRegionHandler(Player p, MapRegion region, short x, short y, bool entering)
        {
            if (p == null)
                return;

            if (region == null)
                return;

            if (!entering)
                return;

            foreach (ArraySegment<byte> chunkData in region.ChunkData(MapMetadataChunkType.RegionChunkType.Autowarp))
            {
                AutoWarpChunk aw = new AutoWarpChunk(chunkData);
                if (aw.HasArena)
                {
                    _arenaManager.SendToArena(p, aw.ArenaName, aw.X, aw.Y);
                }
                else
                {
                    _game.WarpTo(p, aw.X, aw.Y);
                }

                break;
            }
        }
    }
}
