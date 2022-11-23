using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_UserLogin
    {
        #region Static members

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

        #endregion

        public readonly byte Type;
        public byte MakeNew;
        private uint ipAddress;
        private fixed byte nameBytes[NameBytesLength];
        private fixed byte passwordBytes[PasswordBytesLength];
        private int connectionId;
        private uint machineId;
        private int timeZone;
        private byte Unused0;
        private byte Sysop;
        private ushort clientVersion;
        private fixed byte clientExtraDataBytes[ClientExtraDataBytesLength];

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

            name = name.SliceNullTerminated();
            if (name.Length > NameBytes.Length)
                name = name[..NameBytes.Length];

            name.CopyTo(NameBytes);
            NameBytes[name.Length..].Clear();

            password = password.SliceNullTerminated();
            if (password.Length > PasswordBytes.Length)
                password = password[..PasswordBytes.Length];

            password.CopyTo(PasswordBytes);
            PasswordBytes[password.Length..].Clear();
        }

        #region Helpers

        public uint IPAddress
        {
            get => LittleEndianConverter.Convert(ipAddress);
            set => ipAddress = LittleEndianConverter.Convert(value);
        }

        private const int NameBytesLength = 32;
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], NameBytesLength);

        private const int PasswordBytesLength = 32;
        public Span<byte> PasswordBytes => MemoryMarshal.CreateSpan(ref passwordBytes[0], PasswordBytesLength);

        public int ConnectionId
        {
            get => LittleEndianConverter.Convert(connectionId);
            set => connectionId = LittleEndianConverter.Convert(value);
        }

        public uint MachineId
        {
            get => LittleEndianConverter.Convert(machineId);
            set => machineId = LittleEndianConverter.Convert(value);
        }

        public int TimeZone
        {
            get => LittleEndianConverter.Convert(timeZone);
            set => timeZone = LittleEndianConverter.Convert(value);
        }

        public ushort ClientVersion
        {
            get => LittleEndianConverter.Convert(clientVersion);
            set => clientVersion = LittleEndianConverter.Convert(value);
        }

        public const int ClientExtraDataBytesLength = 256;
        public Span<byte> ClientExtraDataBytes => MemoryMarshal.CreateSpan(ref clientExtraDataBytes[0], ClientExtraDataBytesLength);

        #endregion
    }
}
