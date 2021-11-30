using SS.Core.ComponentInterfaces;
using SS.Packets;
using SS.Utilities;
using System;
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

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "ComponentBroker is a required parameter for the module to load even though it is not used.")]
        public bool Load(ComponentBroker broker, INetworkEncryption networkEncryption)
        {
            _networkEncryption = networkEncryption ?? throw new ArgumentNullException(nameof(networkEncryption));

            _networkEncryption.AppendConnectionInitHandler(Callback_ConnectionInit);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (!_networkEncryption.RemoveConnectionInitHandler(Callback_ConnectionInit))
                return false;

            return true;
        }

        #endregion

        private bool Callback_ConnectionInit(IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld)
        {
            if (len != ConnectionInitPacket.Length)
                return false;

            ref ConnectionInitPacket packet = ref MemoryMarshal.AsRef<ConnectionInitPacket>(buffer);

            // make sure the packet fits
            if (packet.T1 != 0x00 || packet.T2 != 0x01 || packet.Zero != 0x00)
                return false;

            ClientType type;
            
            // figure out client type
            switch (packet.ClientType)
            {
                case 0x01:
                    type = ClientType.VIE;
                    break;

                case 0x11:
                    type = ClientType.Continuum;
                    break;

                default:
                    return false; // unknown type
            }

            // get connection (null means no encryption)
            Player p = _networkEncryption.NewConnection(type, remoteEndpoint, null, ld);

            if (p == null)
            {
                // no slots left?
                Span<byte> disconnect = stackalloc byte[] { 0x00, 0x07};
                _networkEncryption.ReallyRawSend(remoteEndpoint, disconnect, ld);
                return true;
            }

            // respond, sending back the key without change means no encryption, both to 1.34 and cont
            ConnectionInitResponsePacket response = new(packet.Key);
            Span<byte> packetSpan = MemoryMarshal.Cast<ConnectionInitResponsePacket, byte>(MemoryMarshal.CreateSpan(ref response, 1));
            _networkEncryption.ReallyRawSend(remoteEndpoint, packetSpan, ld);

            return true;
        }
    }
}
