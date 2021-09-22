using SS.Core.Packets;

namespace SS.Core.ComponentInterfaces
{
    public interface IBanners : IComponentInterface
    {
        void SetBanner(Player p, in Banner banner, bool isFromPlayer);
    }
}
