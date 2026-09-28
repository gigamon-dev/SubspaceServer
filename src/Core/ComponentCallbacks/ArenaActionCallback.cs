namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when an <see cref="Arena"/>'s life-cycle state has changed.
    /// </summary>
    /// <param name="arena">The arena whose state has changed.</param>
    /// <param name="action">The new state.</param>
    [ComponentCallback]
    public delegate void ArenaActionCallback(Arena arena, ArenaAction action);
}
