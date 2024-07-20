using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2B_ServerConnect(uint serverId, uint groupId, uint scoreId, ReadOnlySpan<char> serverName, ushort port, ReadOnlySpan<char> password)
	{
        #region Static members

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        public static readonly int Length = Marshal.SizeOf<S2B_ServerConnect>();

        #endregion

        public readonly byte Type = (byte)S2BPacketType.ServerConnect;
        private readonly uint serverId = LittleEndianConverter.Convert(serverId);
        private readonly uint groupId = LittleEndianConverter.Convert(groupId);
        private readonly uint scoreId = LittleEndianConverter.Convert(scoreId);
        public readonly ServerNameInlineArray ServerName = new(serverName);
        private readonly ushort port = LittleEndianConverter.Convert(port);
        public readonly PasswordInlineArray Password = new(password);

		#region Helper Properties

		public uint ServerId => LittleEndianConverter.Convert(serverId);

		public uint GroupId => LittleEndianConverter.Convert(groupId);

		public uint ScoreId => LittleEndianConverter.Convert(scoreId);

		public ushort Port => LittleEndianConverter.Convert(port);

		#endregion

		#region Inline Array Types

		[InlineArray(Length)]
		public struct ServerNameInlineArray
		{
			public const int Length = 126;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public ServerNameInlineArray(ReadOnlySpan<char> value)
			{
				StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
			}
		}

		[InlineArray(Length)]
		public struct PasswordInlineArray
		{
			public const int Length = 32;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public PasswordInlineArray(ReadOnlySpan<char> value)
			{
				StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
			}
		}

		#endregion
	}
}
