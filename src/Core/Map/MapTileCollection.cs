using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.Map
{
    /// <summary>
    /// collection for storing map tiles
    /// this differs from asss use of a trie for the sparse array (could implement it, but don't feel like wasting time on it just to save some memory)
    /// instead of a 1024x1024 array of tiles which are mostly empty, i have opted for putting it into a dictionary which will probably use more memory than a trie, but less than a full array implmentation without losing speed
    /// </summary>
    public class MapTileCollection : Dictionary<MapCoordinate, MapTile>
    {
        public void Add(short x, short y, MapTile tile)
        {
            this[new MapCoordinate(x, y)] = tile;
        }

        public bool TryGetValue(short x, short y, out MapTile tile)
        {
            return this.TryGetValue(new MapCoordinate(x, y), out tile);
        }
    }
}
