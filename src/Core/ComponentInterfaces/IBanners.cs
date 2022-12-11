using SS.Packets;

namespace SS.Core.ComponentInterfaces
{
    public interface IBanners : IComponentInterface
    {
        void SetBanner(Player player, in Banner banner, bool isFromPlayer);
    }
}
