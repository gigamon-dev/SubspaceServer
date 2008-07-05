using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentInterfaces
{
    public struct ClientSettingOverrideKey
    {
        private uint _key;
    }

    public interface IClientSettings : IComponentInterface
    {
        void SendClientSettings(Player p);
        uint GetChecksum(Player p, uint key);
        Prize GetRandomPrize(Arena arena);
        ClientSettingOverrideKey GetOverrideKey(string section, string key);
        void ArenaOverride(Arena arena, ClientSettingOverrideKey key, int val);
        void PlayerOverride(Player p, ClientSettingOverrideKey key);
    }
}
