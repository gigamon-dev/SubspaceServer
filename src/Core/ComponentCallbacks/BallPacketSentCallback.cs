using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace SS.Core.ComponentCallbacks
{
    public static class BallPacketSentCallback
    {
        /// <summary>
        /// Delegate for when a <see cref="S2CPacketType.Ball"/> packet is sent.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="ballPacket">The packet.</param>
        public delegate void BallPacketSentDelegate(Arena arena, ref readonly BallPacket ballPacket);

        public static void Register(IComponentBroker broker, BallPacketSentDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, BallPacketSentDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, ref readonly BallPacket ballPacket)
        {
            broker?.GetCallback<BallPacketSentDelegate>()?.Invoke(arena, in ballPacket);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, in ballPacket);
        }
    }
}
