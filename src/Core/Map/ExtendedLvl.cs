using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using SS.Utilities;

namespace SS.Core.Map
{
    public class ExtendedLvl
    {
        private readonly char[] _attributeSeparator = "=".ToCharArray();

        /// <summary>
        /// maximum # of flags a map is allowed to contain
        /// </summary>
        private const int MaxFlags = 255;

        private const int MaxChunkSize = 128 * 1024;

        private readonly object _mtx = new object();
        private readonly MapTileCollection _tileLookup = new MapTileCollection();
        private readonly List<MapCoordinate> _flagCoordinateList = new List<MapCoordinate>(MaxFlags);
        // TODO: add extended features (this lvl class is not really 'extended' until it supports regions, attributes, etc)
        private readonly MultiDictionary<uint, byte[]> _rawChunks = new MultiDictionary<uint, byte[]>(); // using multi because many chunks will have the same type
        //private readonly Dictionary<uint, byte[]> _rawChunks = new Dictionary<uint, byte[]>();
        private readonly Dictionary<string, string> _attributeLookup = new Dictionary<string, string>();
        private readonly Dictionary<string, MapRegion> _regionLookup = new Dictionary<string, MapRegion>();
        private int _errorCount;

        public int TileCount
        {
            get { return _tileLookup.Count; }
        }

        public int FlagCount
        {
            get { return _flagCoordinateList.Count; }
        }

        public int ErrorCount
        {
            get { return _errorCount; }
        }

        public void Lock()
        {
            Monitor.Enter(_mtx);
        }

        public void Unlock()
        {
            Monitor.Exit(_mtx);
        }

        public void ClearLevel()
        {
            lock (_mtx)
            {
                _tileLookup.Clear();
                _flagCoordinateList.Clear();
                _errorCount = 0;
            }
        }

        public bool LoadFromFile(string lvlname)
        {
            if (lvlname == null)
                throw new ArgumentNullException("lvlname");

            try
            {
                lock (_mtx)
                {
                    byte[] bitmapData = File.ReadAllBytes(lvlname);
                    int offset = 0;

                    if (bitmapData.Length >= BitmapHeader.Length)
                    {
                        BitmapHeader bh = new BitmapHeader(bitmapData);
                        if (bh.BM == 19778)
                        {
                            // has a bitmap tileset

                            if (bh.Res1 != 0)
                            {
                                // possible metadata, try to read it
                                if (bitmapData.Length >= bh.Res1 + MetadataHeader.Length)
                                {
                                    MetadataHeader mh = new MetadataHeader(bitmapData, (int)bh.Res1);
                                    if (mh.Magic == MetadataHeader.MetadataMagic &&
                                        bitmapData.Length >= bh.Res1 + mh.TotalSize)
                                    {
                                        // looks good, start reading chunks which start right after the metadata header
                                        if (!readChunks(
                                            _rawChunks,
                                            new ArraySegment<byte>(bitmapData, (int)bh.Res1 + MetadataHeader.Length, (int)mh.TotalSize - MetadataHeader.Length)))
                                        {
                                            // some type of error reading chunks
                                        }

                                        // turn some of them into more useful data.
                                        processChunks<object>(_rawChunks, (chunkKey, chunkData, clos) => processMapChunk(chunkKey, chunkData), null);
                                    }
                                }
                            }

                            // get in position for tile data
                            offset += (int)bh.FileSize;
                        }
                    }

                    if (offset == 0)
                    {
                        // was not a bitmap
                        // possible metadata, try to read it
                        MetadataHeader mh = new MetadataHeader(bitmapData, 0);
                        if (bitmapData.Length >= MetadataHeader.Length &&
                            mh.Magic == MetadataHeader.MetadataMagic &&
                            bitmapData.Length >= mh.TotalSize)
                        {
                            // this is a non-backwards compatible ELVL (tileset and tile data will be in the metadata)
                            // start reading chunks which start right after the metadata header
                            if (!readChunks(
                                _rawChunks,
                                new ArraySegment<byte>(bitmapData, MetadataHeader.Length, (int)mh.TotalSize - MetadataHeader.Length)))
                            {
                                // some type of error reading chunks
                            }

                            // turn some of them into more useful data.
                            processChunks<object>(_rawChunks, (chunkKey, chunkData, clos) => processMapChunk(chunkKey, chunkData), null);
                        }
                        else
                        {
                            // normal lvl file w/o tileset
                            readPlainTileData(new ArraySegment<byte>(bitmapData, offset, bitmapData.Length - offset));
                        }
                    }
                    else
                    {
                        // lvl file with tileset
                        readPlainTileData(new ArraySegment<byte>(bitmapData, offset, bitmapData.Length - offset));
                    }

                    /*
                    // useful check to see that we are in fact loading correctly
                    System.Drawing.Bitmap b = new System.Drawing.Bitmap(1024, 1024);
                    foreach (KeyValuePair<MapCoordinate,MapTile> kvp in Tiles)
                    {
                        b.SetPixel(kvp.Key.X, kvp.Key.Y, System.Drawing.Color.Black);
                    }
                    b.Save("C:\\mapimage.bmp");
                    */

                    _flagCoordinateList.Sort();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// calls the callback for each chunk.
        /// if the callback returns true, the chunk will be removed (meaning it's been sucessfully processed)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="chunkLookup"></param>
        /// <param name="chunkProcessingCallback"></param>
        /// <param name="clos">argument to use when calling the callback</param>
        private void processChunks<T>(MultiDictionary<uint, byte[]> chunkLookup, Func<uint, byte[], T, bool> chunkProcessingCallback, T clos)
        {
            if (chunkLookup == null)
                throw new ArgumentNullException("chunkLookup");

            if(chunkProcessingCallback == null)
                throw new ArgumentNullException("chunkProcessingCallback");

            LinkedList<KeyValuePair<uint, byte[]>> chunksToRemove = null;

            try
            {
                foreach (KeyValuePair<uint, byte[]> kvp in chunkLookup)
                {
                    if (chunkProcessingCallback(kvp.Key, kvp.Value, clos))
                    {
                        if (chunksToRemove == null)
                            chunksToRemove = new LinkedList<KeyValuePair<uint, byte[]>>();

                        chunksToRemove.AddLast(kvp);
                    }
                }
            }
            finally
            {
                if (chunksToRemove != null)
                {
                    foreach (KeyValuePair<uint, byte[]> kvp in chunksToRemove)
                        chunkLookup.Remove(kvp.Key, kvp.Value);
                }
            }
        }

        private bool processRegionChunk(uint key, byte[] chunkData, MapRegion region)
        {
            return false;
        }

        private bool processMapChunk(uint key, byte[] chunkData)
        {
            if (chunkData == null)
                throw new ArgumentNullException("chunkData");

            ChunkHeader ch = new ChunkHeader(chunkData);
            uint chunkType = ch.Type;
            if (chunkType == MapMetadataChunkType.ATTR)
            {
                // attribute
                string attributeStr = Encoding.ASCII.GetString(chunkData, ChunkHeader.Length, chunkData.Length - ChunkHeader.Length);
                string[] tokens = attributeStr.Split(_attributeSeparator, 2);
                if (tokens.Length == 2)
                {
                    _attributeLookup[tokens[0]] = tokens[1]; // if the same attribute is specified more than once, overwrite the existing one
                }
                return true;
            }
            else if (chunkType == MapMetadataChunkType.REGN)
            {
                // region
                try
                {
                    ArraySegment<byte> regionChunkData = new ArraySegment<byte>(chunkData, ChunkHeader.Length, chunkData.Length - ChunkHeader.Length);
                    MapRegion region = new MapRegion();

                    // TODO:
                    //_regionLookup[region.Name] = region;
                }
                catch
                {
                }

                return true;
            }
            else if (chunkType == MapMetadataChunkType.TSET)
            {
                // tileset, dont care about this so just mark as processed
                return true;
            }
            else if (chunkType == MapMetadataChunkType.TILE)
            {
                // tile data
                readPlainTileData(new ArraySegment<byte>(chunkData, ChunkHeader.Length, chunkData.Length - ChunkHeader.Length));
                return true;
            }
            else
                return false;
        }

        /// <summary>
        /// to read raw metadata chunks
        /// </summary>
        /// <param name="chunkLookup">place to store chunks that are read</param>
        /// <param name="arraySegment">byte array to read chunks from</param>
        /// <returns>true if the entire chunk data was read, otherwise false</returns>
        private bool readChunks(MultiDictionary<uint, byte[]> chunkLookup, ArraySegment<byte> arraySegment)
        {
            if (chunkLookup == null)
                throw new ArgumentNullException("chunkLookup");

            int offset = arraySegment.Offset;
            int endOffset = arraySegment.Offset + arraySegment.Count;

            while (offset + ChunkHeader.Length <= endOffset)
            {
                // first check chunk header
                ChunkHeader ch = new ChunkHeader(arraySegment.Array, offset);
                int chunkSize = (int)ch.Size;
                int chunkSizeWithHeader = chunkSize + ChunkHeader.Length;
                if (chunkSize > MaxChunkSize || (offset + chunkSizeWithHeader) > endOffset)
                    break;

                // allocate space for the chunk and copy it in
                byte[] c = new byte[chunkSizeWithHeader];
                Array.Copy(arraySegment.Array, offset, c, 0, chunkSizeWithHeader);
                
                chunkLookup.AddLast(ch.Type, c);
                //chunkLookup[ch.Type] = c;

                offset += chunkSizeWithHeader;
                if ((chunkSize & 3) != 0)
                {
                    // account for the 4 byte boundary padding
                    int padding = 4 - (chunkSize & 3);
                    offset += padding;
                }
            }

            return offset == endOffset;
        }

        private void readPlainTileData(ArraySegment<byte> arraySegment)
        {
            int offset = arraySegment.Offset;
            int endOffset = arraySegment.Offset + arraySegment.Count;

            while ((offset + MapTileData.Length) <= endOffset)
            {
                MapTileData tileData = new MapTileData(arraySegment.Array, offset);

                uint x = tileData.X;
                uint y = tileData.Y;
                uint type = tileData.Type;

                if (x < 1024 && y < 1024)
                {
                    MapCoordinate coord = new MapCoordinate((short)x, (short)y);
                    MapTile tile = new MapTile((byte)type);

                    if (tile.IsTurfFlag)
                    {
                        _flagCoordinateList.Add(coord);
                    }

                    int tileSize = tile.TileSize;
                    if (tileSize == 1)
                    {
                        _tileLookup.Add(coord, tile);
                    }
                    else if (tileSize > 1)
                    {
                        for (short xPos = 0; xPos < tileSize; xPos++)
                            for (short yPos = 0; yPos < tileSize; yPos++)
                                _tileLookup.Add((short)(coord.X + xPos), (short)(coord.Y + yPos), tile);
                    }
                    else
                        _errorCount++;
                }
                else
                    _errorCount++;

                offset += MapTileData.Length;
            }
        }

        public void SetAsEmergencyMap()
        {
            lock (_mtx)
            {
                ClearLevel();

                _tileLookup.Add(0, 0, new MapTile(1));
            }
        }

        public bool TryGetTile(MapCoordinate coord, out MapTile tile)
        {
            return _tileLookup.TryGetValue(coord, out tile);
        }

        public bool TryGetAttribute(string key, out string value)
        {
            return _attributeLookup.TryGetValue(key, out value);
        }
    }
}
