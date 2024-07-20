using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map
{
    /// <summary>
    /// header to a chunk of metadata in an extended lvl file
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChunkHeader
    {
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<ChunkHeader>();

        #endregion

        private uint type;
        private uint size;

		#region Helper Properties

		/// <summary>
		/// describes what this chunk represents
		/// </summary>
		public uint Type
		{
			readonly get => LittleEndianConverter.Convert(type);
			set => type = LittleEndianConverter.Convert(value);
		}

		/// <summary>
		/// the number of bytes in the data portion of this chunk, _not_ including the header.
		/// </summary>
		public uint Size
		{
			readonly get => LittleEndianConverter.Convert(size);
			set => size = LittleEndianConverter.Convert(value);
		}

		#endregion
	}
}
