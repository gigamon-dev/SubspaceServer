using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a player group is created.
    /// This occurs when a player invites another player.
    /// </summary>
    [CallbackHelper]
    public static partial class PlayerGroupCreatedCallback
    {
        public delegate void PlayerGroupCreatedDelegate(IPlayerGroup group);
    }
}
