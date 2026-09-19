namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class BeforeSendInitialClientSettingsCallback
    {
        /// <summary>
        /// Delegate for a pub-sub callback that executes just before a player entering an arena is sent the client settings packet.
        /// </summary>
        /// <remarks>
        /// This can be used to override a player's client settings before the client settings packet is sent.
        /// </remarks>
        /// <param name="player">The player that is entering the arena and is about to be sent a client settings packet.</param>
        public delegate void BeforeSendInitialClientSettingsDelegate(Player player);
    }
}
