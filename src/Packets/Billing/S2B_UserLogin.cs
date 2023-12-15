using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2B_UserLogin
    {
        #region Static members

        /// <summary>
        /// Maximum # of bytes in a packet, not including the <see cref="S2B_UserLogin_ClientExtraData"/> that Continuum sends.
        /// </summary>
        public static readonly int Length;

        static S2B_UserLogin()
        {
            Length = Marshal.SizeOf<S2B_UserLogin>();
        }

        #endregion

        public readonly byte Type;
        public byte MakeNew;
        private uint ipAddress;
        public NameInlineArray Name;
        public PasswordInlineArray Password;
        private int connectionId;
        private uint machineId;
        private int timeZone;
        private byte Unused0;
        private byte Sysop;
        private ushort clientVersion;
        // Followed by client extra data bytes (Continuum only)

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
            if (name.Length > NameInlineArray.Length)
                name = name[..NameInlineArray.Length];

            name.CopyTo(Name);
            Name[name.Length..].Clear();

            password = password.SliceNullTerminated();
            if (password.Length > PasswordInlineArray.Length)
                password = password[..PasswordInlineArray.Length];

            password.CopyTo(Password);
            Password[password.Length..].Clear();
        }

        #region Helpers

        public uint IPAddress
        {
            get => LittleEndianConverter.Convert(ipAddress);
            set => ipAddress = LittleEndianConverter.Convert(value);
        }

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

		#endregion

		#region Inline Array Types

		[InlineArray(Length)]
		public struct NameInlineArray
		{
			public const int Length = 32;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public void Clear()
			{
				((Span<byte>)this).Clear();
			}
		}

		[InlineArray(Length)]
		public struct PasswordInlineArray
		{
			public const int Length = 32;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public void Clear()
			{
				((Span<byte>)this).Clear();
			}
		}

		#endregion
	}

	[InlineArray(Length)]
	public struct S2B_UserLogin_ClientExtraData
    {
		public const int Length = 256;

		[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
		[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
		private byte _element0;
    }
}
