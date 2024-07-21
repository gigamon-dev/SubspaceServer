namespace SS.Core.ComponentInterfaces
{
    public interface IMapNewsDownload : IComponentInterface
    {
        void SendMapFilename(Player player);
        uint GetNewsChecksum();
    }
}
