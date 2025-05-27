using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a <see cref="IPlayerGroup"/> disbands.
    /// </summary>
    [CallbackHelper]
    public static partial class PlayerGroupDisbandedCallback
    {
        public delegate void PlayerGroupDisbandedDelegate(IPlayerGroup group);
    }
}
