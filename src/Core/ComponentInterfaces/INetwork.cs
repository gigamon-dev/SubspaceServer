using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    [Flags]
    public enum NetSendFlags
    {
        Unreliable = 0x00,
        Reliable = 0x01,
        Dropabble = 0x02,
        Urgent = 0x04,

        PriorityN1 = 0x10,
        PriorityDefault = 0x20,
        PriorityP1 = 0x30,
        PriorityP2 = 0x40,
        PriorityP3 = 0x50,
        PriorityP4 = 0x64, // includes urgent flag
        PriorityP5 = 0x74, // includes urgent flag

        /// <summary>
        /// this if for use in the Network module only, do not use it directly
        /// </summary>
        Ack = 0x0100,
    }

    public interface INetwork : IComponentInterface
    {
        /// <summary>
        /// To send data to a single player
        /// </summary>
        /// <param name="p">player to send to</param>
        /// <param name="data">data to send</param>
        /// <param name="len">length of data to send</param>
        /// <param name="flags">flags specifying options for the send</param>
        void SendToOne(Player p, byte[] data, int len, NetSendFlags flags);

        /// <summary>
        /// To send data to players in a specific arena or
        /// To send data to players in all arenas.
        /// A specified person can be excluded from the send.
        /// </summary>
        /// <param name="arena">arena to send data to, null for all arenas</param>
        /// <param name="except">player to exclude from the send</param>
        /// <param name="data">data to send</param>
        /// <param name="len">length of data to send</param>
        /// <param name="flags">flags specifying options for the send</param>
        void SendToArena(Arena arena, Player except, byte[] data, int len, NetSendFlags flags);

        /// <summary>
        /// To send data to a set of players.
        /// </summary>
        /// <param name="set">players to send to</param>
        /// <param name="data">data to send</param>
        /// <param name="len">length of data to send</param>
        /// <param name="flags">flags specifying options for the send</param>
        void SendToSet(IEnumerable<Player> set, byte[] data, int len, NetSendFlags flags);

        //void SendToTarget(Target target, byte[] data, int len, NetSendFlags flags);
        //void SendWithCallback(Player p, byte[] data, int len, ReliableDelegate callback, object obj);
        //void SendSized(Player p, object clos, int len, 

        void AddPacket(int pktype, PacketDelegate func);
        void RemovePacket(int pktype, PacketDelegate func);
        void AddSizedPacket(int pktype, SizedPacketDelegate func);
        void RemoveSizedPacket(int pktype, SizedPacketDelegate func);
    }
}
