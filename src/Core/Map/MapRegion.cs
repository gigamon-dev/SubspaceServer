using SS.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;

namespace SS.Core.Map
{
    /// <summary>
    /// Represents a designated set of tiles, usually but not necessarily contiguous.
    /// </summary>
    /// <remarks>
    /// In asss, the region struct knows about the lvl that contains it and keeps track of what region sets it belongs to.
    /// In this implementation, it doesn't know anything about the lvl.  The ExtendedLvl class keeps track of region sets, etc...
    /// </remarks>
    public class MapRegion
    {
        /// <summary>
        /// Name of the region.
        /// </summary>
        public string Name
        {
            get;
            private set;
        }

        private readonly MultiDictionary<uint, ReadOnlyMemory<byte>> _chunks = new MultiDictionary<uint, ReadOnlyMemory<byte>>();

        /// <summary>
        /// To get chunk data that was not processed.
        /// </summary>
        /// <remarks>Similar to asss' Imapdata.RegionChunk, except this will allow you enumerate over all matching chunks instead of just one.</remarks>
        /// <param name="chunkType">The type of chunk to get.</param>
        /// <returns>Enumeration containing chunk payloads (header not included).</returns>
        public IEnumerable<ReadOnlyMemory<byte>> ChunkData(uint chunkType)
        {
            if (_chunks.TryGetValues(chunkType, out IEnumerable<ReadOnlyMemory<byte>> matches))
                return matches;
            else
                return Enumerable.Empty<ReadOnlyMemory<byte>>();
        }

        /// <summary>
        /// # of tiles this region contains.
        /// </summary>
        public int TileCount
        {
            get;
            private set;
        }

        /// <summary>
        /// Whether the region represents a base in a flag game
        /// </summary>
        public bool IsBase
        {
            get;
            private set;
        }

        /// <summary>
        /// Whether antiwarp should be disabled for ships in the region.
        /// </summary>
        public bool NoAntiwarp
        {
            get;
            private set;
        }

        /// <summary>
        /// Whether all weapons should be disabled for ships in the region.
        /// </summary>
        public bool NoWeapons
        {
            get;
            private set;
        }

        /// <summary>
        /// Whether flags should not be allowed to drop in the region.
        /// </summary>
        public bool NoFlagDrops
        {
            get;
            private set;
        }

        public class AutoWarpDestination
        {
            public AutoWarpDestination(short x, short y, string arenaName)
            {
                X = x;
                Y = y;
                ArenaName = arenaName;
            }

            public short X { get; }
            public short Y { get; }
            public string ArenaName { get; }
        }

        public AutoWarpDestination AutoWarp
        {
            get;
            private set;
        }

        /// <summary>
        /// A rectangle based off of data from the run length encoded region data
        /// </summary>
        private class RleEntry
        {
            public short X;
            public short Y;
            public short Width;
            public short Height;
        }

        // random point generation info
        private readonly LinkedList<RleEntry> _rleData = new LinkedList<RleEntry>();
        private readonly Random random = new Random();

        internal MapRegion()
        {
        }

        /// <summary>
        /// To enumerate on all the coordinates contained in the map region.
        /// </summary>
        public IEnumerable<MapCoordinate> Coords
        {
            get
            {
                LinkedListNode<RleEntry> node = _rleData.First;
                while (node != null)
                {
                    RleEntry entry = node.Value;

                    for (short x = entry.X; x < (entry.X + entry.Width); x++)
                        for (short y = entry.Y; y < (entry.Y + entry.Height); y++)
                            yield return new MapCoordinate(x, y);

                    node = node.Next;
                }
            }
        }

        /// <summary>
        /// To check if a coordinate is in the region.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public bool ContainsCoordinate(short x, short y)
        {
            LinkedListNode<RleEntry> node = _rleData.First;

            while (node != null)
            {
                RleEntry entry = node.Value;
                System.Drawing.Rectangle rectangle = new System.Drawing.Rectangle(entry.X, entry.Y, entry.Width, entry.Height);
                if (rectangle.Contains(x, y))
                    return true;

                node = node.Next;
            }

            return false;
        }

        /// <summary>
        /// To check if a coordinate is in the region.
        /// </summary>
        /// <param name="coordinate"></param>
        /// <returns></returns>
        public bool ContainsCoordinate(MapCoordinate coordinate)
        {
            return ContainsCoordinate(coordinate.X, coordinate.Y);
        }

        /// <summary>
        /// To find a random point in the region.
        /// If a point cannot be determined, x and y will be set to -1.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        public void FindRandomPoint(out short x, out short y)
        {
            if (TileCount <= 0)
            {
                x = y = -1;
                return;
            }

            int index = (short)random.Next(0, TileCount);

            LinkedListNode<RleEntry> node = _rleData.First;

            while (node != null)
            {
                RleEntry entry = node.Value;
                int n = entry.Width * entry.Height;
                if (index < n)
                {
                    x = (short)(entry.X + (index % entry.Width));
                    y = (short)(entry.Y + (index / entry.Width));
                    return;
                }
                else
                {
                    index -= n;
                }

                node = node.Next;
            }

            Debug.WriteLine(string.Format("Error with random point in region {0}", Name));
            x = y = -1;
        }

        internal void ProcessRegionChunk(
            MemoryMappedViewAccessor va,
            uint chunkType,
            long position,
            int length,
            Action<string> addError)
        {
            Console.WriteLine($"Process region chunk type:{chunkType} position:{position} length:{length}");
            if (chunkType == RegionMetadataChunkType.Name)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    va.ReadArray(position, buffer, 0, length);
                    Name = Encoding.ASCII.GetString(buffer, 0, length);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else if (chunkType == RegionMetadataChunkType.TileData)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    va.ReadArray(position, buffer, 0, length);
                    if (!ReadRunLengthEncodedTileData(buffer.AsSpan(0, length)))
                    {
                        addError($"Error reading run tile data for region " +
                            $"{(!string.IsNullOrWhiteSpace(Name) ? "'" + Name + "'" : string.Empty)} " +
                            $"at position {position} of length {length}.");
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else if (chunkType == RegionMetadataChunkType.IsBase)
            {
                IsBase = true;
            }
            else if (chunkType == RegionMetadataChunkType.NoAntiwarp)
            {
                NoAntiwarp = true;
            }
            else if (chunkType == RegionMetadataChunkType.NoWeapons)
            {
                NoWeapons = true;
            }
            else if (chunkType == RegionMetadataChunkType.NoFlagDrops)
            {
                NoFlagDrops = true;
            }
            else if (chunkType == RegionMetadataChunkType.Autowarp)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    va.ReadArray(position, buffer, 0, length);

                    RegionAutoWarpChunk autoWarpChunk = new RegionAutoWarpChunk(buffer.AsSpan(0, length));
                    AutoWarp = new AutoWarpDestination(
                        autoWarpChunk.X,
                        autoWarpChunk.Y,
                        autoWarpChunk.ArenaName?.Trim());
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else
            {
                //string typeStr = Encoding.ASCII.GetString(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref chunkType, 1)));
                byte[] buffer = new byte[length];
                va.ReadArray(position, buffer, 0, length);

                // store chunkType and buffer
            }
        }

        private bool ReadRunLengthEncodedTileData(ReadOnlySpan<byte> source)
        {
            int i = 0;
            byte b;
            byte op;
            short lastRow = -1,
                cx = 0,
                cy = 0,
                n;

            LinkedList<RleEntry> lastRowData = new LinkedList<RleEntry>();

            while (i < source.Length)
            {
                if (cx < 0 || cx > 1023 || cy < 0 || cy > 1023)
                    return false;

                b = source[i++];
                op = (byte)((b & 192) >> 6);

                if ((b & 32) == 0)
                {
                    // single byte sequence
                    n = (short)((b & 31) + 1);
                }
                else
                {
                    // double byte sequence
                    if ((b & 28) != 0)
                        Debug.WriteLine("warning, noticed invalid double byte sequence data, ignoring");

                    if (i >= source.Length)
                        return false; // ran out of data to read

                    n = (short)((((b & 3) << 8) | source[i++]) + 1);
                }

                switch (op)
                {
                    case 0: // n empty in a row
                        cx += n;
                        break;

                    case 1: // n present in a row
                        if (cy != lastRow)
                        {
                            lastRowData.Clear();
                            lastRow = cy;
                        }
                        {
                            RleEntry entry = new RleEntry
                            {
                                X = cx,
                                Y = cy,
                                Width = n,
                                Height = 1
                            };

                            lastRowData.AddLast(entry);
                            _rleData.AddLast(entry);
                            TileCount += n;

                            cx += n;
                        }
                        break;

                    case 2: // n rows of empty
                        if (cx != 0)
                            return false;

                        cy += n;
                        break;

                    case 3: // repeat last row n times
                        if (cx != 0 || cy == 0)
                            return false;

                        for (LinkedListNode<RleEntry> node = lastRowData.First;
                            node != null;
                            node = node.Next)
                        {
                            RleEntry entry = node.Value;
                            entry.Height += n;
                            TileCount += n * entry.Width;
                        }

                        cy += n;
                        lastRow = (short)(cy - 1);
                        break;
                }

                if (cx == 1024)
                {
                    cx = 0;
                    cy++;
                }
            }

            if (i != source.Length || cy != 1024)
                return false;

            return true;
        }

        public void TrimExcess()
        {
            _chunks.TrimExcess();
        }
    }
}
