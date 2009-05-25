using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using SS.Utilities;

namespace SS.Core.Map
{
    /// <summary>
    /// For reading the Extended lvl format.
    /// Extended means it may contain extra metadata such as regions and attributes.
    /// </summary>
    public class ExtendedLvl : BasicLvl
    {
        /// <summary>
        /// Delimiter used for ATTR metadata.
        /// </summary>
        private readonly char[] _attributeSeparator = "=".ToCharArray();

        /// <summary>
        /// Maximum size in bytes that a metadata chunk can be.
        /// </summary>
        private const int MaxChunkSize = 128 * 1024;
        
        // TODO: add extended features (this lvl class is not really 'extended' until it supports regions, attributes, etc)
        private readonly MultiDictionary<uint, ArraySegment<byte>> _rawChunks = new MultiDictionary<uint, ArraySegment<byte>>(); // using multi because many chunks will have the same type
        private readonly Dictionary<string, string> _attributeLookup = new Dictionary<string, string>();

        /// <summary>
        /// region name --> region
        /// </summary>
        private readonly Dictionary<string, MapRegion> _regionLookup = new Dictionary<string, MapRegion>();

        /// <summary>
        /// coordinate --> region set
        /// </summary>
        private readonly RegionSetCoordinateCollection _regionSetCoordinateLookup = new RegionSetCoordinateCollection(); // asss stores set index, instead we store a reference to the set

        /// <summary>
        /// list of all region sets
        /// </summary>
        private readonly List<HashSet<MapRegion>> _regionSetList = new List<HashSet<MapRegion>>();

        /// <summary>
        /// region --> region sets it belongs to
        /// </summary>
        private readonly Dictionary<MapRegion, HashSet<HashSet<MapRegion>>> _regionMemberSetLookup = new Dictionary<MapRegion, HashSet<HashSet<MapRegion>>>();

        public override void ClearLevel()
        {
            Lock();

            try
            {
                base.ClearLevel();

                // TODO: clear extended lvl data
            }
            finally
            {
                Unlock();
            }
        }

        public override bool LoadFromFile(string lvlname)
        {
            if (lvlname == null)
                throw new ArgumentNullException("lvlname");

            Lock();

            try
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
                        ReadPlainTileData(new ArraySegment<byte>(bitmapData, offset, bitmapData.Length - offset));
                    }
                }
                else
                {
                    // lvl file with tileset
                    ReadPlainTileData(new ArraySegment<byte>(bitmapData, offset, bitmapData.Length - offset));
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Unlock();
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
        private void processChunks<T>(
            MultiDictionary<uint, ArraySegment<byte>> chunkLookup, 
            Func<uint, ArraySegment<byte>, T, bool> chunkProcessingCallback, 
            T clos)
        {
            if (chunkLookup == null)
                throw new ArgumentNullException("chunkLookup");

            if(chunkProcessingCallback == null)
                throw new ArgumentNullException("chunkProcessingCallback");

            LinkedList<KeyValuePair<uint, ArraySegment<byte>>> chunksToRemove = null;

            try
            {
                foreach (KeyValuePair<uint, ArraySegment<byte>> kvp in chunkLookup)
                {
                    if (chunkProcessingCallback(kvp.Key, kvp.Value, clos))
                    {
                        if (chunksToRemove == null)
                            chunksToRemove = new LinkedList<KeyValuePair<uint, ArraySegment<byte>>>();

                        chunksToRemove.AddLast(kvp);
                    }
                }
            }
            finally
            {
                if (chunksToRemove != null)
                {
                    foreach (KeyValuePair<uint, ArraySegment<byte>> kvp in chunksToRemove)
                        chunkLookup.Remove(kvp.Key, kvp.Value);
                }
            }
        }
        /*
        private bool contains(MapRegion region, int x, int y)
        {
            
            //if(region.ExtendedLvl.
            return true;
        }
        
        // this might belong in the MapRegion class
        private void addRegionTile(MapRegion region, int x, int y)
        {
            if (region == null)
                throw new ArgumentNullException("region");

            // TODO
        }
        
        // this might belong in the MapRegion class
        private bool readRunLengthEncodedTileData(MapRegion region, ArraySegment<byte> source)
        {
            if (region == null)
                throw new ArgumentNullException("region");


            int lastRow = -1, 
                cx = 0,
                cy = 0,
                i = 0, 
                b, op, d1, n, x, y;

            LinkedList<RleEntry> lastRowData = new LinkedList<RleEntry>();

            while (i < source.Count)
            {
                if (cx < 0 || cx > 1023 || cy < 0 || cy > 1023)
                    return false;

                b = source.Array[source.Offset + i++];
                op = (b & 192) >> 6;
                d1 = b & 31;
                n = d1 + 1;

                if ((b & 32) != 0)
                    n = (d1 << 8) + source.Array[source.Offset + i++] + 1;

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
                            region.RleData.AddLast(entry);
                            region.Tiles += n;

                            for (x = cx; x < (cx + n); x++)
                                addRegionTile(region, x, cy);

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
                            region.Tiles += n * entry.Width;
                        }

                        for(y = cy ; y<(cy + n); y++)
                            for (x = 0; x < 1024; x++)
                                if(contains(region, x, cy-1))
                                    addRegionTile(region, x, y);

                        cy += n;
                        lastRow = cy - 1;
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
                    Debug.WriteLine("<ExtendedLvl> error in lvl file while reading rle tile data");
                }
                return true;
            }

            return false;
        }
        */

        public int RegionCount
        {
            get { return _regionMemberSetLookup.Count; }
        }

        public MapRegion FindRegionByName(string name)
        {
            MapRegion region;
            _regionLookup.TryGetValue(name, out region);
            return region;
        }

        public IEnumerable<MapRegion> RegionsAtCoord(short x, short y)
        {
            HashSet<MapRegion> regionSet;
            if (!_regionSetCoordinateLookup.TryGetValue(x, y, out regionSet))
                yield break;

            foreach(MapRegion region in regionSet)
                yield return region;
        }

        private bool addRegion(MapRegion region)
        {
            if (region == null)
                return false;

            if (string.IsNullOrEmpty(region.Name))
                return false; // all regions must have a name

            _regionLookup[region.Name] = region;

            foreach (MapCoordinate coords in region.Coords)
            {
                HashSet<MapRegion> oldRegionSet;
                HashSet<MapRegion> newRegionSet = null;

                if (_regionSetCoordinateLookup.TryGetValue(coords, out oldRegionSet))
                {
                    // there is already a set at this coordinate

                    if (oldRegionSet.Contains(region))
                        continue; // the set at this coordinate already contains the region, skip it

                    // look for an existing set that contains the old set and the region
                    foreach (HashSet<MapRegion> regionToCheck in _regionSetList)
                    {
                        if (regionToCheck.Count == (oldRegionSet.Count + 1) &&
                            regionToCheck.Contains(region) &&
                            oldRegionSet.IsSubsetOf(regionToCheck))
                        {
                            newRegionSet = regionToCheck;
                        }
                    }

                    if (newRegionSet == null)
                    {
                        // set does not exist yet, create it
                        newRegionSet = new HashSet<MapRegion>();
                        newRegionSet.UnionWith(oldRegionSet);
                        newRegionSet.Add(region);
                        _regionSetList.Add(newRegionSet);
                    }
                }
                else
                {
                    // no set at this coordinate yet

                    // look for an existing set that conains only this region
                    foreach (HashSet<MapRegion> regionToCheck in _regionSetList)
                    {
                        if (regionToCheck.Count == 1 && regionToCheck.Contains(region))
                        {
                            newRegionSet = regionToCheck;
                            break;
                        }
                    }

                    if (newRegionSet == null)
                    {
                        // set does not exist yet, create it
                        newRegionSet = new HashSet<MapRegion>();
                        newRegionSet.Add(region);
                        _regionSetList.Add(newRegionSet);
                    }
                }

                _regionSetCoordinateLookup.Add(coords, newRegionSet);

                HashSet<HashSet<MapRegion>> memberOfSet;
                if (!_regionMemberSetLookup.TryGetValue(region, out memberOfSet))
                {
                    memberOfSet = new HashSet<HashSet<MapRegion>>();
                    _regionMemberSetLookup.Add(region, memberOfSet);
                }
                memberOfSet.Add(newRegionSet);
            }
            
            return true;
        }

        private bool processMapChunk(uint key, ArraySegment<byte> chunkData)
        {
            ChunkHeader ch = new ChunkHeader(chunkData.Array, chunkData.Offset);
            uint chunkType = ch.Type;

            if (chunkType == MapMetadataChunkType.ATTR)
            {
                // attribute
                string attributeStr = Encoding.ASCII.GetString(chunkData.Array, ChunkHeader.Length, chunkData.Count - ChunkHeader.Length);
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
                    /*
                    //ArraySegment<byte> regionChunkData = new ArraySegment<byte>(chunkData, ChunkHeader.Length, chunkData.Length - ChunkHeader.Length);
                    MapRegion region = new MapRegion(this);
                    readChunks(region.Chunks, ch.Data);
                    processChunks<MapRegion>(region.Chunks, processRegionChunk, region);

                    if (string.IsNullOrEmpty(region.Name))
                        return true; // all regions must have a name

                    _regionLookup[region.Name] = region;
                    */

                    MapRegion region = new MapRegion(ch.Data);
                    addRegion(region);
                }
                catch
                {
                }

                return true;
            }
            else if (chunkType == MapMetadataChunkType.TSET)
            {
                // tileset, dont care about this so just mark as processed
                // TODO: load this into a Bitmap object
                //ArraySegment<byte> bitmapData = ch.Data;
                //MemoryStream ms = new MemoryStream(bitmapData.Array, bitmapData.Offset, bitmapData.Count);
                //Bitmap
                return true;
            }
            else if (chunkType == MapMetadataChunkType.TILE)
            {
                // tile data
                ReadPlainTileData(new ArraySegment<byte>(chunkData.Array, ChunkHeader.Length, chunkData.Count - ChunkHeader.Length));
                return true;
            }
            else
            {
                // unknown chunk type
                //string chunkTypeStr = Encoding.ASCII.GetString(chunkData, 0, 4);
                return false;
            }
        }

        /// <summary>
        /// to read raw metadata chunks
        /// </summary>
        /// <param name="chunkLookup">place to store chunks that are read</param>
        /// <param name="arraySegment">byte array to read chunks from</param>
        /// <returns>true if the entire chunk data was read, otherwise false</returns>
        private bool readChunks(MultiDictionary<uint, ArraySegment<byte>> chunkLookup, ArraySegment<byte> source)
        {
            if (chunkLookup == null)
                throw new ArgumentNullException("chunkLookup");

            int offset = source.Offset;
            int endOffset = source.Offset + source.Count;

            while (offset + ChunkHeader.Length <= endOffset)
            {
                // first check chunk header
                ChunkHeader ch = new ChunkHeader(source.Array, offset);
                int chunkSize = (int)ch.Size;
                int chunkSizeWithHeader = chunkSize + ChunkHeader.Length;
                if (chunkSize > MaxChunkSize || (offset + chunkSizeWithHeader) > endOffset)
                    break;

                chunkLookup.AddLast(ch.Type, ch.DataWithHeader);
                /*
                // allocate space for the chunk and copy it in
                // TODO: (enhancement) probably dont need to allocate a new array like asss does, maybe use ArraySegment<byte> instead?
                byte[] c = new byte[chunkSizeWithHeader];
                Array.Copy(arraySegment.Array, offset, c, 0, chunkSizeWithHeader);
                
                chunkLookup.AddLast(ch.Type, c);
                //chunkLookup[ch.Type] = c;
                */
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

        public bool TryGetAttribute(string key, out string value)
        {
            return _attributeLookup.TryGetValue(key, out value);
        }
    }
}
