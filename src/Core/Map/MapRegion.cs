using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;
using System.Diagnostics;

namespace SS.Core.Map
{
    /*
    /// <summary>
    /// TODO: consider exposing map region as an interface used by the IMapData inferface.
    /// That way only readonly operations are exposed?  Currently, the MapRegion class is specialized for reading only.
    /// </summary>
    public interface IMapDataRegion
    {
        string Name
        {
            get;
        }

        IEnumerable<ArraySegment<byte>> ChunkData(uint chunkType);

        int TileCount
        {
            get;
        }

        bool NoAntiwarp
        {
            get;
        }

        bool NoWeapons
        {
            get;
        }

        IEnumerable<MapCoordinate> Coords
        {
            get;
        }

        bool ContainsCoordinate(short x, short y);
        bool ContainsCoordinate(MapCoordinate coordinate);
        void FindRandomPoint(out short x, out short y);
    }
    */

    /// <summary>
    /// In asss, the region struct knows about the lvl that contains it and keeps track of what region sets it belongs to.
    /// In this implementation, it doesn't know anything about the lvl.  The ExtendedLvl class keeps track of region sets, etc...
    /// </summary>
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

        public readonly MultiDictionary<uint, ArraySegment<byte>> Chunks = new MultiDictionary<uint, ArraySegment<byte>>();

        /// <summary>
        /// To get chunk data for the region.
        /// Note: only the payload of the chunk is included.  The chunk header is stripped out for you.
        /// <remarks>Similar to asss' Imapdata.RegionChunk, except this will allow you enumerate over all matching chunks instead of just one.</remarks>
        /// </summary>
        /// <param name="chunkType">type of chunk to look for</param>
        /// <returns></returns>
        public IEnumerable<ArraySegment<byte>> ChunkData(uint chunkType)
        {
            IEnumerable<ArraySegment<byte>> matches;
            if (!Chunks.TryGetValues(chunkType, out matches))
                yield break;

            foreach (ArraySegment<byte> chunkWithHeader in matches)
            {
                yield return new ArraySegment<byte>(chunkWithHeader.Array, chunkWithHeader.Offset + 8, chunkWithHeader.Count - 8);
            }
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
        private Random random = new Random();

        public MapRegion(ArraySegment<byte> regionChunkData)
        {
            if (!ChunkHelper.ReadChunks(Chunks, regionChunkData))
            {
                Debug.WriteLine("warning: did not read all chunk data");
            }

            ChunkHelper.ProcessChunks<MapRegion>(Chunks, processRegionChunk, this);
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

        private bool processRegionChunk(uint key, ArraySegment<byte> chunkData, MapRegion region)
        {
            if (region == null)
                throw new ArgumentNullException("region");

            ChunkHeader ch = new ChunkHeader(chunkData.Array, chunkData.Offset);
            uint chunkType = ch.Type;

            if (chunkType == MapMetadataChunkType.RegionChunkType.Name)
            {
                // a descriptive name for the region
                if (!string.IsNullOrEmpty(region.Name))
                    return true; // already have a name for this region

                ArraySegment<byte> chunkDataWithoutHeader = ch.Data;
                region.Name = Encoding.ASCII.GetString(chunkDataWithoutHeader.Array, chunkDataWithoutHeader.Offset, chunkDataWithoutHeader.Count);
                return true;
            }
            else if (chunkType == MapMetadataChunkType.RegionChunkType.TileData)
            {
                // tile data, the definition of the region
                if (!readRunLengthEncodedTileData(region, ch.Data))
                {
                    // error in lvl file while reading rle tile data
                    //Debug.WriteLine("<ExtendedLvl> error in lvl file while reading rle tile data");
                    throw new Exception("error in lvl file while reading rle tile data");
                }
                return true;
            }
            else if (chunkType == MapMetadataChunkType.RegionChunkType.NoAntiwarp)
            {
                NoAntiwarp = true;
                return true;
            }
            else if (chunkType == MapMetadataChunkType.RegionChunkType.NoWeapons)
            {
                NoWeapons = true;
                return true;
            }

            return false;
        }

        private bool readRunLengthEncodedTileData(MapRegion region, ArraySegment<byte> source)
        {
            if (region == null)
                throw new ArgumentNullException("region");

            int i = 0;
            byte b;
            byte op;
            short lastRow = -1,
                cx = 0,
                cy = 0,
                n;

            LinkedList<RleEntry> lastRowData = new LinkedList<RleEntry>();

            while (i < source.Count)
            {
                if (cx < 0 || cx > 1023 || cy < 0 || cy > 1023)
                    return false;

                b = source.Array[source.Offset + i++];
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

                    if (i >= source.Count)
                        return false; // ran out of data to read

                    n = (short)((((b & 3) << 8) | source.Array[source.Offset + i++]) + 1);
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
                            RleEntry entry = new RleEntry();
                            entry.X = cx;
                            entry.Y = cy;
                            entry.Width = n;
                            entry.Height = 1;

                            lastRowData.AddLast(entry);
                            region._rleData.AddLast(entry);
                            region.TileCount += n;

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
                            region.TileCount += n * entry.Width;
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

            if (i != source.Count || cy != 1024)
                return false;

            return true;
        }
    }
}
