namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a King of the Hill game has ended.
    /// </summary>
    /// <param name="arena">The arena the game was ended in.</param>
    [ComponentCallback]
    public delegate void KothEndedCallback(Arena arena);
}
