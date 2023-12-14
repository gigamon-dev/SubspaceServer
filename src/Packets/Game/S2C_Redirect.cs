using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet that tells the client to switch to another server.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_Redirect
    {
        public readonly byte Type;
        private uint ip;
        private ushort port;
        private short arenaType; // Same values as in the ?go packet
        public ArenaNameInlineArray ArenaName;
        private uint loginId;
        
        public S2C_Redirect(uint ip, ushort port, short arenaType, ReadOnlySpan<char> arenaName, uint loginId)
        {
            Type = (byte)S2CPacketType.Redirect;
            this.ip = LittleEndianConverter.Convert(ip);
            this.port = LittleEndianConverter.Convert(port);
            this.arenaType = LittleEndianConverter.Convert(arenaType);
            ArenaName.Set(arenaName);
            this.loginId = LittleEndianConverter.Convert(loginId);
        }

		#region Inline Array Types

		[InlineArray(Length)]
		public struct ArenaNameInlineArray
		{
			public const int Length = 16;

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
