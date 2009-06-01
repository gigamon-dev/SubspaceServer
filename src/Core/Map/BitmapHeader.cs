using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SS.Utilities;

namespace SS.Core.Map
{
    /// <summary>
    /// to read the header of a bitmap file
    /// </summary>
    public struct BitmapHeader
    {
        static BitmapHeader()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            bm = locationBuilder.CreateUInt16DataLocation();
            fsize = locationBuilder.CreateUInt32DataLocation();
            res1 = locationBuilder.CreateUInt32DataLocation();
            offbits = locationBuilder.CreateUInt32DataLocation();
            Length = locationBuilder.NumBytes;
        }

        private static readonly UInt16DataLocation bm;
        private static readonly UInt32DataLocation fsize;
        private static readonly UInt32DataLocation res1;
        private static readonly UInt32DataLocation offbits;
        public static readonly int Length;

        private readonly byte[] data;

        public BitmapHeader(byte[] data)
        {
            this.data = data;
        }

        /// <summary>
        /// should be 0x42 0x4D (ASCII code points for B and M)
        /// </summary>
        public ushort BM
        {
            get { return bm.GetValue(data); }
        }

        /// <summary>
        /// the size of the BMP file in bytes
        /// </summary>
        public uint FileSize
        {
            get { return fsize.GetValue(data); }
        }

        /// <summary>
        /// both 2 byte reserved spots as one single 4 byte spot.  This is used to figure out where the metadata starts.
        /// <remarks>both reserved spots are used together as one to support 32 bit tilesets</remarks>
        /// </summary>
        public uint Res1
        {
            get { return res1.GetValue(data); }
        }

        /// <summary>
        /// the offset of where the bitmap data begins
        /// </summary>
        public uint BitmapOffset
        {
            get { return offbits.GetValue(data); }
        }
    }
}
