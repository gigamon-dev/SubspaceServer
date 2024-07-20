using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map.Lvz
{
    /// <summary>
    /// The header to the object section within a LVZ file.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct ObjectSectionHeader
    {
		#region Static Members

		public const uint CLV1 = 0x31564c43;
        public const uint CLV2 = 0x32564c43;

        public static readonly int Length = Marshal.SizeOf<ObjectSectionHeader>();

        #endregion

        private readonly uint type;
		private readonly uint objectCount;
		private readonly uint imageCount;

		#region Helper Properties

		public uint Type => LittleEndianConverter.Convert(type);

        public uint ObjectCount => LittleEndianConverter.Convert(objectCount);
        
        public uint ImageCount => LittleEndianConverter.Convert(imageCount);

        #endregion
    }
}
