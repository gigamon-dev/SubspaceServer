using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        /// chunk type --> data of the chunk
        /// </summary>
        private readonly MultiDictionary<uint, ArraySegment<byte>> _rawChunks = new MultiDictionary<uint, ArraySegment<byte>>(); // using multi because many chunks will have the same type
        
        /// <summary>
        /// Extended LVL attributes:
        /// attribute name --> attribute value
        /// </summary>
        private readonly Dictionary<string, string> _attributeLookup = new Dictionary<string, string>();

        /// <summary>
        /// region name --> region
        /// </summary>
        private readonly Dictionary<string, MapRegion> _regionLookup = new Dictionary<string, MapRegion>();

        /// <summary>
        /// coordinate --> region set
        /// </summary>
        private readonly MapRegionSetCollection _regionSetCoordinateLookup = new MapRegionSetCollection(); // asss stores set index, instead we store a reference to the set

        /// <summary>
        /// list of all region sets
        /// </summary>
        private readonly List<ImmutableHashSet<MapRegion>> _regionSetList = new List<ImmutableHashSet<MapRegion>>();

        /// <summary>
        /// region --> region sets it belongs to
        /// </summary>
        private readonly Dictionary<MapRegion, HashSet<ImmutableHashSet<MapRegion>>> _regionMemberSetLookup = new Dictionary<MapRegion, HashSet<ImmutableHashSet<MapRegion>>>();

        public override void ClearLevel()
        {
            Lock();

            try
            {
                _rawChunks.Clear();
                _attributeLookup.Clear();
                _regionLookup.Clear();
                _regionSetCoordinateLookup.Clear();
                _regionSetList.Clear();
                _regionMemberSetLookup.Clear();

                base.ClearLevel();
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
                                    if (!ChunkHelper.ReadChunks(
                                        _rawChunks,
                                        new ArraySegment<byte>(bitmapData, (int)bh.Res1 + MetadataHeader.Length, (int)mh.TotalSize - MetadataHeader.Length)))
                                    {
                                        // some type of error reading chunks
                                    }

                                    // turn some of them into more useful data.
                                    ChunkHelper.ProcessChunks<object>(_rawChunks, (chunkKey, chunkData, clos) => ProcessMapChunk(chunkKey, chunkData), null);
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
                        if (!ChunkHelper.ReadChunks(
                            _rawChunks,
                            new ArraySegment<byte>(bitmapData, MetadataHeader.Length, (int)mh.TotalSize - MetadataHeader.Length)))
                        {
                            // some type of error reading chunks
                        }

                        // turn some of them into more useful data.
                        ChunkHelper.ProcessChunks<object>(_rawChunks, (chunkKey, chunkData, clos) => ProcessMapChunk(chunkKey, chunkData), null);
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
        /// # of regions
        /// </summary>
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

        public IImmutableSet<MapRegion> RegionsAtCoord(short x, short y)
        {
            if (!_regionSetCoordinateLookup.TryGetValue(x, y, out ImmutableHashSet<MapRegion> regionSet))
                return ImmutableHashSet<MapRegion>.Empty;

            return regionSet;
        }

        private bool AddRegion(MapRegion region)
        {
            if (region == null)
                return false;

            if (string.IsNullOrEmpty(region.Name))
                return false; // all regions must have a name

            _regionLookup[region.Name] = region;

            foreach (MapCoordinate coords in region.Coords)
            {
                ImmutableHashSet<MapRegion> oldRegionSet;
                ImmutableHashSet<MapRegion> newRegionSet = null;

                if (_regionSetCoordinateLookup.TryGetValue(coords, out oldRegionSet))
                {
                    // there is already a set at this coordinate

                    if (oldRegionSet.Contains(region))
                        continue; // the set at this coordinate already contains the region, skip it

                    // look for an existing set that contains the old set and the region
                    foreach (ImmutableHashSet<MapRegion> regionToCheck in _regionSetList)
                    {
                        if (regionToCheck.Count == (oldRegionSet.Count + 1) &&
                            regionToCheck.Contains(region) &&
                            oldRegionSet.IsSubsetOf(regionToCheck))
                        {
                            newRegionSet = regionToCheck;
                            break;
                        }
                    }

                    if (newRegionSet == null)
                    {
                        // set does not exist yet, create it
                        newRegionSet = oldRegionSet.Add(region);
                        _regionSetList.Add(newRegionSet);

                        foreach (MapRegion existingRegion in oldRegionSet)
                        {
                            _regionMemberSetLookup[existingRegion].Add(newRegionSet);
                        }
                    }
                }
                else
                {
                    // no set at this coordinate yet

                    // look for an existing set that conains only this region
                    foreach (ImmutableHashSet<MapRegion> regionToCheck in _regionSetList)
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
                        newRegionSet = ImmutableHashSet.Create(region);
                        _regionSetList.Add(newRegionSet);
                    }
                }

                _regionSetCoordinateLookup[coords] = newRegionSet;

                HashSet<ImmutableHashSet<MapRegion>> memberOfSet;
                if (!_regionMemberSetLookup.TryGetValue(region, out memberOfSet))
                {
                    memberOfSet = new HashSet<ImmutableHashSet<MapRegion>>();
                    _regionMemberSetLookup.Add(region, memberOfSet);
                }
                memberOfSet.Add(newRegionSet);
            }
            
            return true;
        }

        private bool ProcessMapChunk(uint key, ArraySegment<byte> chunkData)
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
                    MapRegion region = new MapRegion(ch.Data);
                    AddRegion(region);
                }
                catch
                {
                    // error reading region
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

        public bool TryGetAttribute(string key, out string value)
        {
            return _attributeLookup.TryGetValue(key, out value);
        }

        /// <summary>
        /// To get chunk data for the map.
        /// Note: only the payload of the chunk is included.  The chunk header is stripped out for you.
        /// <remarks>Similar to asss' Imapdata.MapChunk, except this will allow you enumerate over all matching chunks instead of just one.</remarks>
        /// </summary>
        /// <param name="chunkType">type of chunk to look for</param>
        /// <returns></returns>
        public IEnumerable<ArraySegment<byte>> ChunkData(uint chunkType)
        {
            IEnumerable<ArraySegment<byte>> matches;
            if (!_rawChunks.TryGetValues(chunkType, out matches))
                yield break;

            foreach (ArraySegment<byte> chunkWithHeader in matches)
            {
                yield return new ArraySegment<byte>(chunkWithHeader.Array, chunkWithHeader.Offset + ChunkHeader.Length, chunkWithHeader.Count - ChunkHeader.Length);
            }
        }
    }
}
