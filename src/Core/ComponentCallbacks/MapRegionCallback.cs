using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate when a <see cref="Player"/> enters or exits a <see cref="MapRegion"/>.
    /// </summary>
    /// <param name="player">The player entering or exiting a region.</param>
    /// <param name="region">The region being entered or exited.</param>
    /// <param name="x">The x-coordinate of the player.</param>
    /// <param name="y">The y-coordinate of the player.</param>
    /// <param name="entering">True if the region is being entered.  False if being exited.</param>
    [ComponentCallback]
    public delegate void MapRegionCallback(Player player, MapRegion region, short x, short y, bool entering);
}
