using System.Collections.Generic;

namespace SS.Core.Map
{
    /// <summary>
    /// Currently this is just derived from <see cref="Dictionary{TKey, TValue}"/>.
    /// The plan is to eventually switch to a different data structure, such as the trie/sparse array that ASSS' uses to save on memory usage.
    /// For now this will suffice, it probably will use more memory though.
    /// </summary>
    /// <typeparam name="TValue"></typeparam>
    public class MapCoordinateCollection<TValue> : Dictionary<MapCoordinate, TValue>
    {
        // TODO: Switch to different data structure that is less memory intensive?

        public MapCoordinateCollection() : base()
        {
        }

        public MapCoordinateCollection(int capacity) : base(capacity)
        {
        }
    }
}
