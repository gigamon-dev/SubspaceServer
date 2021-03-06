﻿using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Buffers.Binary;
using System.Net;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class EncryptionNull : IModule
    {
        private INetworkEncryption _networkEncryption;

        private struct ConnectionInitResponsePacket
        {
            // static constructor to initialize packet's info
            static ConnectionInitResponsePacket()
            {
                DataLocationBuilder locationBuilder = new DataLocationBuilder();
                t1 = locationBuilder.CreateByteDataLocation();
                t2 = locationBuilder.CreateByteDataLocation();
                key = locationBuilder.CreateInt32DataLocation();
            }

            // static data members that tell the location of each field in the byte array of a packet
            private static readonly ByteDataLocation t1;
            private static readonly ByteDataLocation t2;
            private static readonly Int32DataLocation key;

            // data members
            private readonly byte[] data;

            public ConnectionInitResponsePacket(byte[] data)
            {
                this.data = data;
            }

            public byte T1
            {
                set { t1.SetValue(data, value); }
                //get { return ExtendedBitConverter.ToByte(data, t1.ByteOffset, t1.BitOffset); }
                //set { ExtendedBitConverter.WriteByteBits(value, data, t1.ByteOffset, t1.BitOffset, t1.NumBits); }
            }

            public byte T2
            {
                set { t2.SetValue(data, value); }
                //get { return ExtendedBitConverter.ToByte(data, t2.ByteOffset, t2.BitOffset); }
                //set { ExtendedBitConverter.WriteByteBits(value, data, t2.ByteOffset, t2.BitOffset, t2.NumBits); }
            }

            public int Key
            {
                set { key.SetValue(data, value); }
                //set { ExtendedBitConverter.WriteInt32Bits(value, data, key.ByteOffset, key.BitOffset, key.NumBits); }
            }
        }

        #region IModule Members

        public bool Load(ComponentBroker broker, INetworkEncryption networkEncryption)
        {
            _networkEncryption = networkEncryption ?? throw new ArgumentNullException(nameof(networkEncryption));

            ConnectionInitCallback.Register(broker, connectionInit);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            ConnectionInitCallback.Unregister(broker, connectionInit);

            return true;
        }

        #endregion

        private void connectionInit(IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld)
        {
            ClientType type;
            Player p;

            // make sure the packet fits
            if (len != 8 ||
                buffer[0] != 0x00 ||
                buffer[1] != 0x01 ||
                buffer[7] != 0x00)
                return;

            // figure out client type
            switch (buffer[6])
            {
                case 0x01:
                    type = ClientType.VIE;
                    break;

                case 0x11:
                    type = ClientType.Continuum;
                    break;

                default:
                    return; // unknown type
            }

            // get connection (null means no encryption)
            p = _networkEncryption.NewConnection(type, remoteEndpoint, null, ld);

            if (p == null)
            {
                // no slots left?
                byte[] pkt = {0x00, 0x07};
                _networkEncryption.ReallyRawSend(remoteEndpoint, pkt, pkt.Length, ld);
                return;
            }

            int key = BinaryPrimitives.ReadInt32LittleEndian(new Span<byte>(buffer, 2, 4));

            // respond, sending back the key without change means no encryption, both to 1.34 and cont
            // note: reusing the buffer (asss creates a new buffer on the stack)
            ConnectionInitResponsePacket rp = new ConnectionInitResponsePacket(buffer);
            rp.T1 = 0x00;
            rp.T2 = 0x02;
            rp.Key = key;
            _networkEncryption.ReallyRawSend(remoteEndpoint, buffer, 6, ld);
        }
    }
}
