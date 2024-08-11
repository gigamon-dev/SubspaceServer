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
    [CoreModuleInfo]
    public class EncryptionNull(IRawNetwork rawNetwork) : IModule
    {
        private readonly IRawNetwork _rawNetwork = rawNetwork ?? throw new ArgumentNullException(nameof(rawNetwork));

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public readonly struct ConnectionInitResponsePacket(int key)
        {
            public readonly byte T1 = 0x00;
            public readonly byte T2 = 0x02;
            public readonly int Key = LittleEndianConverter.Convert(key);
        }

        #region IModule Members

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "ComponentBroker is a required parameter for the module to load even though it is not used.")]
        bool IModule.Load(IComponentBroker broker)
        {
            _rawNetwork.AppendConnectionInitHandler(Callback_ConnectionInit);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (!_rawNetwork.RemoveConnectionInitHandler(Callback_ConnectionInit))
                return false;

            return true;
        }

        #endregion

        private bool Callback_ConnectionInit(SocketAddress remoteAddress, ReadOnlySpan<byte> data, ListenData ld)
        {
            if (data.Length != ConnectionInitPacket.Length)
                return false;

            ref readonly ConnectionInitPacket packet = ref MemoryMarshal.AsRef<ConnectionInitPacket>(data);

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
            IPEndPoint remoteEndpoint = (IPEndPoint)ld.GameSocket.LocalEndPoint!.Create(remoteAddress);
            Player? player = _rawNetwork.NewConnection(type, remoteEndpoint, null, ld);

            if (player is null)
            {
                // no slots left?
                Span<byte> disconnect = stackalloc byte[] { 0x00, 0x07 };
                _rawNetwork.ReallyRawSend(remoteAddress, disconnect, ld);
                return true;
            }

            // respond, sending back the key without change means no encryption, both to 1.34 and cont
            ConnectionInitResponsePacket response = new(packet.Key);
            _rawNetwork.ReallyRawSend(remoteAddress, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref response, 1)), ld);

            return true;
        }
    }
}
