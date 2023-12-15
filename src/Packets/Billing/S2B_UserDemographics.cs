using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2B_UserDemographics
    {
        #region Static members

        public static readonly int Length;
		public static readonly int LengthWithoutData;

        static S2B_UserDemographics()
        {
            Length = Marshal.SizeOf<S2B_UserDemographics>();
			LengthWithoutData = Length - DataInlineArray.Length;
        }

        #endregion

        public byte Type;
        private int connectionId;
        private DataInlineArray data;

        public S2B_UserDemographics(int connectionId)
        {
            Type = (byte)S2BPacketType.UserDemographics;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
        }

        public int SetData(ReadOnlySpan<byte> data)
        {
            data.CopyTo(this.data);
            return LengthWithoutData + data.Length;
		}

		#region Inline Array Types

		[InlineArray(Length)]
		public struct DataInlineArray
		{
			public const int Length = 765;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;
		}

		#endregion
	}
}
