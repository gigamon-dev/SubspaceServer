using System;
using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    public delegate void MessageDelegate(Player player, ReadOnlySpan<char> message);

    public interface IChatNet : IComponentInterface
    {
        void AddHandler(string type, MessageDelegate handler);

        void RemoveHandler(string type, MessageDelegate handler);

        void SendToOne(Player player, ReadOnlySpan<char> message);

        void SendToArena(Arena arena, Player except, ReadOnlySpan<char> message);

        void SendToSet(IEnumerable<Player> set, ReadOnlySpan<char> message);

        //void GetClientStats(Player player, )
    }
}
