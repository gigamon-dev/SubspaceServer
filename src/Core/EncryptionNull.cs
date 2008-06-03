using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using SS.Utilities;
using SS.Core.Packets;

namespace SS.Core
{
    public class EncryptionNull : IModule
    {
        private INetworkEncryption _net;

        private struct ConnectionInitResponsePacket
        {
            // static constructor to initialize packet's info
            static ConnectionInitResponsePacket()
            {
                DataLocationBuilder locationBuilder = new DataLocationBuilder();
                t1 = locationBuilder.CreateDataLocation(8);
                t2 = locationBuilder.CreateDataLocation(8);
                key = locationBuilder.CreateDataLocation(32);
            }

            // static data members that tell the location of each field in the byte array of a packet
            private static readonly DataLocation t1;
            private static readonly DataLocation t2;
            private static readonly DataLocation key;

            // data members
            private readonly byte[] data;

            public ConnectionInitResponsePacket(byte[] data)
            {
                this.data = data;
            }

            public byte T1
            {
                //get { return ExtendedBitConverter.ToByte(data, t1.ByteOffset, t1.BitOffset); }
                set { ExtendedBitConverter.WriteByteBits(value, data, t1.ByteOffset, t1.BitOffset, t1.NumBits); }
            }

            public byte T2
            {
                //get { return ExtendedBitConverter.ToByte(data, t2.ByteOffset, t2.BitOffset); }
                set { ExtendedBitConverter.WriteByteBits(value, data, t2.ByteOffset, t2.BitOffset, t2.NumBits); }
            }

            public int Key
            {
                set { ExtendedBitConverter.WriteInt32Bits(value, data, key.ByteOffset, key.BitOffset, key.NumBits); }
            }
        }

        #region IModule Members

        public Type[] InterfaceDependencies
        {
            get
            {
                return new Type[] {
                    typeof(INetworkEncryption)
                };
            }
        }

        public bool Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _net = interfaceDependencies[typeof(INetworkEncryption)] as INetworkEncryption;
            mm.RegisterCallback<ConnectionInitDelegate>(Constants.Events.ConnectionInit, new ConnectionInitDelegate(connectionInit));

            return true;
        }

        public bool Unload(ModuleManager mm)
        {
            mm.UnregisterCallback(Constants.Events.ConnectionInit, new ConnectionInitDelegate(connectionInit));

            return true;
        }

        #endregion

        private void connectionInit(IPEndPoint remoteEndpoint, byte[] buffer, object v)
        {
            ClientType type;
            Player p;

            // make sure the packet fits
            if(buffer.Length != 8 ||
                buffer[0] != 0x00 ||
                buffer[1] != 0x01 ||
                buffer[7] != 0x00)
                return;

            // figure out client type
            switch (buffer[6])
            {
                case 0x01:
                    type = ClientType.Vie;
                    break;

                case 0x11:
                    type = ClientType.Continuum;
                    break;

                default:
                    return; // unknown type
            }

            // get connection (null means no encryption)
            p = _net.NewConnection(type, remoteEndpoint, null, v);

            if (p == null)
            {
                // no slots left?
                byte[] pkt = {0x00, 0x07};
                _net.ReallyRawSend(remoteEndpoint, pkt, pkt.Length, v);
                return;
            }

            //int key = BitConverter.ToInt32(buffer, 2);
            int key = ExtendedBitConverter.ToInt32(buffer, 2, 0);

            // respond, sending back the key without change means no encryption, both to 1.34 and cont
            // note: reusing the buffer (asss creates a new buffer on the stack)
            ConnectionInitResponsePacket rp = new ConnectionInitResponsePacket(buffer);
            rp.T1 = 0x00;
            rp.T2 = 0x02;
            rp.Key = key;
            _net.ReallyRawSend(remoteEndpoint, buffer, 6, v);
        }
    }
}
