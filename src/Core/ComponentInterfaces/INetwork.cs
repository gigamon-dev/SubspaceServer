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
        void AddPacket(int pktype, PacketDelegate func);
        void RemovePacket(int pktype, PacketDelegate func);
        void AddSizedPacket(int pktype, SizedPacketDelegate func);
        void RemoveSizedPacket(int pktype, SizedPacketDelegate func);
    }
}
