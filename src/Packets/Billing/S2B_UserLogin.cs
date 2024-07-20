using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2B_UserLogin(
		byte makeNew,
		uint ipAddress,
		ReadOnlySpan<byte> name,
		ReadOnlySpan<byte> password,
		int connectionId,
		uint machineId,
		int timeZone,
		ushort clientVersion)
	{
        #region Static Members

        /// <summary>
        /// Maximum # of bytes in a packet, not including the <see cref="S2B_UserLogin_ClientExtraData"/> that Continuum sends.
        /// </summary>
        public static readonly int Length = Marshal.SizeOf<S2B_UserLogin>();

        #endregion

        public readonly byte Type = (byte)S2BPacketType.UserLogin;
        public readonly byte MakeNew = makeNew;
        private readonly uint ipAddress = LittleEndianConverter.Convert(ipAddress);
        public readonly NameInlineArray Name = new(name);
        public readonly PasswordInlineArray Password = new(password);
        private readonly int connectionId = LittleEndianConverter.Convert(connectionId);
        private readonly uint machineId = LittleEndianConverter.Convert(machineId);
        private readonly int timeZone = LittleEndianConverter.Convert(timeZone);
        private readonly byte Unused0 = 0;
        private readonly byte Sysop = 0;
        private readonly ushort clientVersion = LittleEndianConverter.Convert(clientVersion);
		// Followed by client extra data bytes (Continuum only)

		#region Helper Properties

		public uint IPAddress => LittleEndianConverter.Convert(ipAddress);

		public int ConnectionId => LittleEndianConverter.Convert(connectionId);

		public uint MachineId => LittleEndianConverter.Convert(machineId);

		public int TimeZone => LittleEndianConverter.Convert(timeZone);

		public ushort ClientVersion => LittleEndianConverter.Convert(clientVersion);

		#endregion

		#region Inline Array Types

		[InlineArray(Length)]
		public struct NameInlineArray
		{
			public const int Length = 32;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

            public NameInlineArray(ReadOnlySpan<byte> value)
            {
				value = value.SliceNullTerminated();
				if (value.Length > Length)
					value = value[..Length];

				value.CopyTo(this);
				this[value.Length..].Clear();
			}

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

			public PasswordInlineArray(ReadOnlySpan<byte> value)
			{
				value = value.SliceNullTerminated();
				if (value.Length > Length)
					value = value[..Length];

				value.CopyTo(this);
				this[value.Length..].Clear();
			}

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
