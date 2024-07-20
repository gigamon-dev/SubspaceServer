using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LoginPacket
    {
        #region Static members

        public static readonly int VIELength;
        public static readonly int ContinuumLength;

        static LoginPacket()
        {
            VIELength = Marshal.SizeOf<LoginPacket>();
            ContinuumLength = VIELength + 64;
        }

        #endregion

        public byte Type;
        public byte Flags;
        public NameInlineArray Name;
        public PasswordInlineArray Password;
        private uint macId;
        private byte blah;
        private ushort timeZoneBias;
        private ushort unk1;
        private ushort cVersion;
        private int field444;
        private int field555;
        private uint d2;
        private UnusedInlineArray blah2;
        // The continuum login packet (0x24) has 64 more bytes (continuum id field) that come next (not included in this struct).
        // The zone server doesn't know how to interpret the bytes. It just passes them to the billing server.

        #region Helper properties

        public uint MacId
        {
            readonly get => LittleEndianConverter.Convert(macId);
            set => macId = LittleEndianConverter.Convert(value);
        }

		public ushort TimeZoneBias
		{
			readonly get => LittleEndianConverter.Convert(timeZoneBias);
			set => timeZoneBias = LittleEndianConverter.Convert(value);
		}

		public ushort CVersion
        {
			readonly get => LittleEndianConverter.Convert(cVersion);
            set => cVersion = LittleEndianConverter.Convert(value);
        }

		public uint D2
		{
			readonly get => LittleEndianConverter.Convert(d2);
			set => d2 = LittleEndianConverter.Convert(value);
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

		[InlineArray(Length)]
		private struct UnusedInlineArray
        {
			public const int Length = 12;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;
		}

		#endregion
	}
}
