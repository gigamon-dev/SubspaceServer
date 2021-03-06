﻿using System.Collections.Generic;
using System.Collections.Immutable;

namespace SS.Core.Map
{
    public class MapCoordinateCollection<TValue> : Dictionary<MapCoordinate, TValue>
    {
        public void Add(short x, short y, TValue value)
        {
            this[new MapCoordinate(x, y)] = value;
        }

        public bool TryGetValue(short x, short y, out TValue value)
        {
            return this.TryGetValue(new MapCoordinate(x, y), out value);
        }
    }

    /// <summary>
    /// collection for storing map tiles
    /// this differs from asss use of a trie for the sparse array (could implement it, but don't feel like wasting time on it just to save some memory)
    /// instead of a 1024x1024 array of tiles which are mostly empty, i have opted for putting it into a dictionary which will probably use more memory than a trie, but less than a full array implmentation without losing speed
    /// </summary>
    public class MapTileCollection : MapCoordinateCollection<MapTile>
    {
    }

    public class MapRegionSetCollection : MapCoordinateCollection<ImmutableHashSet<MapRegion>>
    {
    }
}
