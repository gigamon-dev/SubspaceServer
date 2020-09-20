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
        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
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

        public bool Load(ComponentBroker broker, IArenaManager arenaManager, IGame game, IMapData mapData)
        {
            _broker = broker;
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));

            MapRegionCallback.Register(broker, mapRegionHandler);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            MapRegionCallback.Unregister(broker, mapRegionHandler);
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
