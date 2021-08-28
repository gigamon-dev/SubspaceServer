using SS.Core.Packets;

namespace SS.Core.ComponentInterfaces
{
    //public struct ClientSettingOverrideKey
    //{
    //    private uint _key;
    //}

    /// <summary>
    /// The interface for managing the client-side settings.
    /// </summary>
    public interface IClientSettings : IComponentInterface
    {
        /// <summary>
        /// Sends client-side settings to a player.
        /// </summary>
        /// <remarks>This is used by the <see cref="SS.Core.Modules.ArenaManager"/> module as part of the arena response procedure.</remarks>
        /// <param name="p">The player to send settings to.</param>
        void SendClientSettings(Player p);

        /// <summary>
        /// Gets the checksum of a player's client-side settings.
        /// </summary>
        /// <remarks>This is used by the <see cref="SS.Core.Modules.Security"/> module to validate checksums.</remarks>
        /// <param name="p">The player to get the client-side settings checksum for.</param>
        /// <param name="key">The key to use when generating the checksum.</param>
        /// <returns>The checksum.</returns>
        uint GetChecksum(Player p, uint key);

        /// <summary>
        /// Generates a random prize for an arena based on client-side settings.
        /// </summary>
        /// <param name="arena">The arena to get a random prize for.</param>
        /// <returns>A random prize.</returns>
        Prize GetRandomPrize(Arena arena);

        // TODO: add override functionality

        /// <summary>
        /// Gets a key used to identify a client setting (section + key pair).
        /// </summary>
        /// <param name="section">The section to get a key for.</param>
        /// <param name="key">The setting key.</param>
        /// <returns></returns>
        //ClientSettingOverrideKey GetOverrideKey(string section, string key);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="arena"></param>
        /// <param name="key"></param>
        /// <param name="val"></param>
        //void ArenaOverride(Arena arena, ClientSettingOverrideKey key, int val);

        //void ArenaUnoverride(Arena arena, ClientSettingOverrideKey key);

        //bool GetArenaOverride(Arena arena, ClientSettingOverrideKey key, out int value);

        //int GetArenaValue(Arena arena, ClientSettingOverrideKey key);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="p"></param>
        /// <param name="key"></param>
        //void PlayerOverride(Player p, ClientSettingOverrideKey key);

        //void PlayerUnoverride(Player p, ClientSettingOverrideKey key);

        //bool GetPlayerOverride(Player p, ClientSettingOverrideKey key, out int value);

        //int GetPlayerValue(Player p, ClientSettingOverrideKey key);
    }
}
