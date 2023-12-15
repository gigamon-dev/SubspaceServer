using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2B_ServerConnect
    {
        #region Static members

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        public static readonly int Length;

        static S2B_ServerConnect()
        {
            Length = Marshal.SizeOf<S2B_ServerConnect>();
        }

        #endregion

        public readonly byte Type;
        private uint serverId;
        private uint groupId;
        private uint scoreId;
        public ServerNameInlineArray ServerName;
        private ushort port;
        public PasswordInlineArray Password;

        public S2B_ServerConnect(uint serverId, uint groupId, uint scoreId, ReadOnlySpan<char> serverName, ushort port, ReadOnlySpan<char> password)
        {
            Type = (byte)S2BPacketType.ServerConnect;
            this.serverId = LittleEndianConverter.Convert(serverId);
            this.groupId = LittleEndianConverter.Convert(groupId);
            this.scoreId = LittleEndianConverter.Convert(scoreId);
            this.port = LittleEndianConverter.Convert(port);
            ServerName.Set(serverName);
            Password.Set(password);
        }

		#region Inline Array Types

		[InlineArray(Length)]
		public struct ServerNameInlineArray
		{
			public const int Length = 126;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public void Set(ReadOnlySpan<char> value)
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

			public void Set(ReadOnlySpan<char> value)
			{
				StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
			}
		}

		#endregion
	}
}
