using SS.Utilities.Collections;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;

namespace SS.Core.Map
{
    /// <summary>
    /// For reading the Extended lvl format.
    /// Extended means it may contain extra metadata such as regions and attributes.
    /// </summary>
    public class ExtendedLvl : BasicLvl
    {
        /// <summary>
        /// Maximum size in bytes that a metadata chunk can be.
        /// </summary>
        /// <remarks>ASSS limits chunk size, even though a limit is not specified in the extended lvl format specification?</remarks>
        private const int MaxChunkSize = 128 * 1024;

        /// <summary>
        /// Delimiter used for ATTR metadata containing key/value pairs.
        /// </summary>
        private const byte AttributeDelimiter = (byte)'=';

        /// <summary>
        /// chunk type --> data of the chunk
        /// </summary>
        private readonly MultiDictionary<uint, ReadOnlyMemory<byte>> _chunks = new MultiDictionary<uint, ReadOnlyMemory<byte>>(); // using multi because many chunks will have the same type

        /// <summary>
        /// Extended LVL attributes:
        /// attribute name --> attribute value
        /// </summary>
        private readonly Dictionary<string, string> _attributeLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// region name --> region
        /// </summary>
        private readonly Dictionary<string, MapRegion> _regionLookup = new Dictionary<string, MapRegion>(StringComparer.OrdinalIgnoreCase);

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

        // Cached delegates that will get allocated only if there is a map region
        private ProcessChunkCallback<MapRegion>? _processRegionChunk;
        private Action<string>? _addError;

        protected override void ClearLevel()
        {
            _chunks.Clear();
            _attributeLookup.Clear();
            _regionLookup.Clear();
            _regionSetCoordinateLookup.Clear();
            _regionSetList.Clear();
            _regionMemberSetLookup.Clear();

            base.ClearLevel();
        }

        protected override void TrimExcess()
        {
            _chunks.TrimExcess();
            _attributeLookup.TrimExcess();
            _regionLookup.TrimExcess();
            _regionSetCoordinateLookup.TrimExcess();
            _regionSetList.TrimExcess();
            _regionMemberSetLookup.TrimExcess();

            foreach (MapRegion region in _regionMemberSetLookup.Keys)
            {
                region.TrimExcess();
            }

            base.TrimExcess();
        }

        static ExtendedLvl()
        {
            var lvl = new ExtendedLvl();
            lvl.SetAsEmergencyMap();
            EmergencyMap = lvl;
        }

        /// <summary>
        /// An emergency map to use if loading from a file fails.
        /// </summary>
        public static ExtendedLvl EmergencyMap
        {
            get;
        }

        /// <summary>
        /// Initializes a new empty instance.
        /// </summary>
        private ExtendedLvl()
        {
        }

        /// <summary>
        /// Initializes a new instance from a file.
        /// </summary>
        /// <param name="path"></param>
        public ExtendedLvl(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Cannot be null or white-space.", nameof(path));

            using MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using MemoryMappedViewAccessor va = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            long length = new FileInfo(path).Length;
            long position = 0;

            if (length >= BitmapHeader.Length)
            {
                va.Read(position, out BitmapHeader bitmapHeader);

                if (bitmapHeader.BM == 0x4D42) // BM in ASCII
                {
                    // has a bitmap tileset
                    if (bitmapHeader.Reserved != 0)
                    {
                        // possibly has metadata
                        if (length >= bitmapHeader.Reserved + MetadataHeader.Length)
                        {
                            va.Read(bitmapHeader.Reserved, out MetadataHeader metadataHeader);
                            if (metadataHeader.Magic == MetadataHeader.MetadataMagic
                                && length >= bitmapHeader.Reserved + metadataHeader.TotalSize)
                            {
                                // has metadata
                                ReadChunks(
                                    va,
                                    bitmapHeader.Reserved + MetadataHeader.Length,
                                    metadataHeader.TotalSize - MetadataHeader.Length,
                                    ProcessMapChunk,
                                    true);
                            }
                        }
                    }

                    // get in position for tile data
                    position += bitmapHeader.FileSize;
                }
            }

            if (position == 0)
            {
                // was not a bitmap
                // possibly starts with metadata (non-backwards compatible extended lvl) which could contain TSET and TILE chunks
                if (length >= MetadataHeader.Length)
                {
                    va.Read(position, out MetadataHeader metadataHeader);
                    if (metadataHeader.Magic == MetadataHeader.MetadataMagic
                        && length >= metadataHeader.TotalSize)
                    {
                        // has metadata
                        ReadChunks(
                            va,
                            MetadataHeader.Length,
                            metadataHeader.TotalSize - MetadataHeader.Length,
                            ProcessMapChunk,
                            false);

                        // in a non-backwards compatible extended lvl, the tile data should be included in the metadata
                        if (!IsTileDataLoaded)
                            throw new Exception("Error loading non-backwards compatible extended lvl. No tile data.");

                        TrimExcess();
                        return;
                    }
                }
            }

            // At this point, we should be able to read tile data.
            // The position is either:
            // still at the very beginning of the file, meaning the map only contains tile data
            // OR
            // is after the bitmap (tileset)
            ReadPlainTileData(va, position, length - position);

            TrimExcess();
        }

        /// <summary>
        /// # of regions
        /// </summary>
        public int RegionCount
        {
            get { return _regionMemberSetLookup.Count; }
        }

        public MapRegion? FindRegionByName(string name)
        {
            _regionLookup.TryGetValue(name, out MapRegion? region);
            return region;
        }

        public ImmutableHashSet<MapRegion> RegionsAtCoord(short x, short y)
        {
            if (!_regionSetCoordinateLookup.TryGetValue(x, y, out ImmutableHashSet<MapRegion>? regionSet))
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
                ImmutableHashSet<MapRegion>? newRegionSet = null;

                if (_regionSetCoordinateLookup.TryGetValue(coords, out ImmutableHashSet<MapRegion>? oldRegionSet))
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

                if (!_regionMemberSetLookup.TryGetValue(region, out HashSet<ImmutableHashSet<MapRegion>>? memberOfSet))
                {
                    memberOfSet = new HashSet<ImmutableHashSet<MapRegion>>();
                    _regionMemberSetLookup.Add(region, memberOfSet);
                }
                memberOfSet.Add(newRegionSet);
            }

            return true;
        }

        private delegate void ProcessChunkCallback<T>(MemoryMappedViewAccessor accessor, uint chunkType, long position, int length, T state);

        private void ReadChunks<T>(
            MemoryMappedViewAccessor accessor,
            long position,
            long length,
            ProcessChunkCallback<T> processChunkCallback, //Action<MemoryMappedViewAccessor, uint, long, int, T> processChunkCallback,
            T state)
        {
            ArgumentNullException.ThrowIfNull(accessor);
            ArgumentNullException.ThrowIfNull(processChunkCallback);

            while (length >= ChunkHeader.Length)
            {
                // first check the chunk header
                accessor.Read(position, out ChunkHeader chunkHeader);

                long chunkSize = ChunkHeader.Length + chunkHeader.Size;

                if (chunkHeader.Size > MaxChunkSize || chunkSize > length)
                {
                    AddError($"Bad chunk at position {position} of type {chunkHeader.Type}, size {chunkHeader.Size}, remaining length {length}.");
                    break;
                }

                processChunkCallback(accessor, chunkHeader.Type, position + ChunkHeader.Length, (int)chunkHeader.Size, state);

                position += chunkSize;
                length -= chunkSize;

                if ((chunkHeader.Size & 3) != 0)
                {
                    int padding = 4 - ((int)chunkHeader.Size & 3);
                    position += padding;
                    length -= padding;
                }
            }
        }

        private void ProcessMapChunk(
            MemoryMappedViewAccessor accessor,
            uint chunkType,
            long position,
            int length,
            bool backwardsCompatible)
        {
            ArgumentNullException.ThrowIfNull(accessor);

            if (chunkType == MapMetadataChunkType.ATTR)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(length);

                try
                {
                    if (length >= 3)
                    {
                        accessor.ReadArray(position, buffer, 0, length);

                        int delimiterIndex = Array.IndexOf(buffer, AttributeDelimiter, 1, length);
                        if (delimiterIndex != -1)
                        {
                            string key = Encoding.ASCII.GetString(buffer, 0, delimiterIndex);
                            string value = Encoding.ASCII.GetString(buffer, delimiterIndex + 1, length - delimiterIndex - 1);
                            _attributeLookup[key] = value; // if the same attribute is specified more than once, overwrite the existing one
                        }
                        else
                        {
                            AddError($"Map metadata 'ATTR' chunk at position {position} of length {length} was missing the '=' delimiter.");
                        }
                    }
                    else
                    {
                        AddError($"Map metadata 'ATTR' chunk at position {position} of length {length} was too short.");
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else if (chunkType == MapMetadataChunkType.REGN)
            {
                MapRegion region = new();
                ReadChunks(accessor, position, length, _processRegionChunk ??= ProcessRegionChunk, region);

                if (region.Name is null)
                {
                    AddError($"Processed a 'REGN' chunk, but the region was missing a name.");
                }

                AddRegion(region);
            }
            else if (chunkType == MapMetadataChunkType.TSET)
            {
                if (backwardsCompatible)
                {
                    AddError($"Found map metadata 'TSET' chunk at position {position} of length {length} but it is only valid in a non-backwards compatible lvl.");
                }
                else
                {
                    // tileset, not needed by the server, skip it
                }
            }
            else if (chunkType == MapMetadataChunkType.TILE)
            {
                if (backwardsCompatible)
                {
                    AddError($"Found map metadata 'TILE' chunk at position {position} of length {length} but it is only valid in a non-backwards compatible lvl.");
                }
                else
                {
                    ReadPlainTileData(accessor, position, length);
                }
            }
            else
            {
                // unknown chunk type, hold onto a copy of it
                byte[] buffer = new byte[length];
                accessor.ReadArray(position, buffer, 0, length);
                _chunks.AddLast(chunkType, buffer);
            }
        }

        private void ProcessRegionChunk(
            MemoryMappedViewAccessor accessor,
            uint chunkType,
            long position,
            int length,
            MapRegion region)
        {
            region.ProcessRegionChunk(accessor, chunkType, position, length, _addError ??= AddError);
        }

        public bool TryGetAttribute(string key, [MaybeNullWhen(false)] out string value)
        {
            return _attributeLookup.TryGetValue(key, out value);
        }

        /// <summary>
        /// To get chunk data that was not processed.
        /// </summary>
        /// <remarks>Similar to asss' Imapdata.MapChunk, except this will allow you enumerate over all matching chunks instead of just one.</remarks>
        /// <param name="chunkType">The type of chunk to get.</param>
        /// <returns>Enumeration containing chunk payloads (header not included).</returns>
        public IEnumerable<ReadOnlyMemory<byte>> ChunkData(uint chunkType)
        {
            if (_chunks.TryGetValues(chunkType, out IEnumerable<ReadOnlyMemory<byte>>? matches))
                return matches;
            else
                return Enumerable.Empty<ReadOnlyMemory<byte>>();
        }
    }
}
