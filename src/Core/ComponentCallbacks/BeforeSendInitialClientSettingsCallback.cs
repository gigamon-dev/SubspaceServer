using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class BeforeSendInitialClientSettingsCallback
    {
        /// <summary>
        /// Delegate for a pub-sub callback that executes just before a player entering an arena is sent the client settings packet.
        /// </summary>
        /// <remarks>
        /// This can be used to override a player's client settings before the client settings packet is initially sent.
        /// <para>
        /// The client setting packet is sent immediately after this callback is executed,
        /// so there is no need to call <see cref="IClientSettings.SendClientSettings(Player)"/>.
        /// Doing so would double send the packet which would be undesirable.
        /// </para>
        /// </remarks>
        /// <param name="player">The player that is entering the arena and is about to be sent a client settings packet.</param>
        public delegate void BeforeSendInitialClientSettingsDelegate(Player player);
    }
}
