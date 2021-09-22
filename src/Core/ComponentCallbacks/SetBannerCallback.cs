using SS.Core.Packets;

namespace SS.Core.ComponentCallbacks
{
    public static class SetBannerCallback
    {
        public delegate void SetBannerDelegate(Player player, in Banner banner, bool isFromPlayer);

        public static void Register(ComponentBroker broker, SetBannerDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, SetBannerDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player player, in Banner banner, bool isFromPlayer)
        {
            broker?.GetCallback<SetBannerDelegate>()?.Invoke(player, banner, isFromPlayer);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, banner, isFromPlayer);
        }
    }
}
