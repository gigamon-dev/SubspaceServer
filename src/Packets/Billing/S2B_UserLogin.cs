using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_UserLogin
    {
        /// <summary>
        /// Maximum # of bytes in a packet.
        /// </summary>
        public static readonly int Length;

        /// <summary>
        /// # of bytes without <see cref="ClientExtraDataBytes"/>
        /// </summary>
        public static readonly int LengthWithoutClientExtraData;

        static S2B_UserLogin()
        {
            Length = Marshal.SizeOf<S2B_UserLogin>();
            LengthWithoutClientExtraData = Length - ClientExtraDataBytesLength;
        }

        public readonly byte Type;
        public readonly byte MakeNew;
        private uint ipAddress;

        private const int nameBytesLength = 32;
        private fixed byte nameBytes[nameBytesLength];
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], nameBytesLength);

        private const int passwordBytesLength = 32;
        private fixed byte passwordBytes[passwordBytesLength];
        public Span<byte> PasswordBytes => MemoryMarshal.CreateSpan(ref passwordBytes[0], passwordBytesLength);

        private int connectionId;
        private uint machineId;
        private int timeZone;
        private byte Unused0;
        private byte Sysop;
        private ushort clientVersion;

        public const int ClientExtraDataBytesLength = 256;
        private fixed byte clientExtraDataBytes[ClientExtraDataBytesLength];
        public Span<byte> ClientExtraDataBytes => MemoryMarshal.CreateSpan(ref clientExtraDataBytes[0], ClientExtraDataBytesLength);

        public S2B_UserLogin(
            byte makeNew, 
            uint ipAddress, 
            ReadOnlySpan<byte> name, 
            ReadOnlySpan<byte> password, 
            int connectionId,
            uint machineId,
            int timeZone,
            ushort clientVersion)
        {
            Type = (byte)S2BPacketType.UserLogin;
            MakeNew = makeNew;
            this.ipAddress = LittleEndianConverter.Convert(ipAddress);
            this.connectionId = LittleEndianConverter.Convert(connectionId);
            this.machineId = LittleEndianConverter.Convert(machineId);
            this.timeZone = LittleEndianConverter.Convert(timeZone);
            Unused0 = 0;
            Sysop = 0;
            this.clientVersion = LittleEndianConverter.Convert(clientVersion);

            name.CopyTo(NameBytes);
            password.CopyTo(PasswordBytes);
        }
    }
}
