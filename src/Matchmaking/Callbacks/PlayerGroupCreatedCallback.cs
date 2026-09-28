using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a player group is created.
    /// This occurs when a player invites another player.
    /// </summary>
    /// <param name="group">The group that was created.</param>
    [ComponentCallback]
    public delegate void PlayerGroupCreatedCallback(IPlayerGroup group);
}
