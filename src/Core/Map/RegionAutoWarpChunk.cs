using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.Map
{
    public readonly ref struct RegionAutoWarpChunk
    {
        static RegionAutoWarpChunk()
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
        public static readonly int LengthWithoutArena;
        private static readonly DataLocation arenaName;
        public static readonly int LengthWithArena;

        private readonly ReadOnlySpan<byte> _data;

        public RegionAutoWarpChunk(ReadOnlySpan<byte> data)
        {
            _data = data;
        }

        public short X => x.GetValue(_data);

        public short Y => y.GetValue(_data);

        public string ArenaName => _data.Length == LengthWithArena ? arenaName.Slice(_data).ReadNullTerminatedASCII() : null;
    }
}
