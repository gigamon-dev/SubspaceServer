using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for the login sequence (connection init) that responds such that no encryption is used.
    /// </summary>
    /// <remarks>
    /// This module cannot be used alongside other encryption modules. 
    /// If you intend to use an encryption module, make sure this one is not loaded.
    /// </remarks>
    [CoreModuleInfo]
    public class EncryptionNull : IModule
    {
        private INetworkEncryption _networkEncryption;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ConnectionInitResponsePacket
        {
            public readonly byte T1;
            public readonly byte T2;
            public readonly int Key;

            public ConnectionInitResponsePacket(int key)
            {
                T1 = 0x00;
                T2 = 0x02;
                Key = LittleEndianConverter.Convert(key);
            }
        }
        
        #region IModule Members

        public bool Load(ComponentBroker broker, INetworkEncryption networkEncryption)
        {
            _networkEncryption = networkEncryption ?? throw new ArgumentNullException(nameof(networkEncryption));

            ConnectionInitCallback.Register(broker, Callback_ConnectionInit);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            ConnectionInitCallback.Unregister(broker, Callback_ConnectionInit);

            return true;
        }

        #endregion

        private void Callback_ConnectionInit(IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld)
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
                Span<byte> disconnect = stackalloc byte[] { 0x00, 0x07};
                _networkEncryption.ReallyRawSend(remoteEndpoint, disconnect, ld);
                return;
            }

            int key = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buffer, 2, 4));

            // respond, sending back the key without change means no encryption, both to 1.34 and cont
            ConnectionInitResponsePacket response = new ConnectionInitResponsePacket(key);
            Span<byte> packetSpan = MemoryMarshal.Cast<ConnectionInitResponsePacket, byte>(MemoryMarshal.CreateSpan(ref response, 1));
            _networkEncryption.ReallyRawSend(remoteEndpoint, packetSpan, ld);
        }
    }
}
