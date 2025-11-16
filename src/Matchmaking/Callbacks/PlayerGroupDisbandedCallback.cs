using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for when a <see cref="IPlayerGroup"/> disbands.
    /// </summary>
    /// <param name="group"></param>
    [ComponentCallback]
    public delegate void PlayerGroupDisbandedCallback(IPlayerGroup group);
}
