using System;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    /// <summary>
    /// A single banner.
    /// </summary>
    /// <remarks>
    /// 12 x 8 BMP (96 pixels), without the BMP header.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct Banner
    {
        private readonly BannerData _data;

        public Banner(ReadOnlySpan<byte> data)
        {
            _data = new(data);
        }

        /// <summary>
        /// Whether the banner is set.
        /// </summary>
        /// <remarks>
        /// <see langword="false"/> means the data consists of all zeros.
        /// </remarks>
        public readonly bool IsSet => _data.IsSet;

        /// <summary>
        /// This nested struct is to allow <see cref="Banner"/> to be readonly.
        /// <see cref="Banner"/> couldn't be readonly with the fixed size buffer directly inside of it.
        /// Having <see cref="Banner"/> as a readonly struct helps prevent defensive copies when passed as an 'in' parameter.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private unsafe struct BannerData
        {
            private const int DataLength = 96;
            private fixed byte dataBytes[DataLength];

            public BannerData(ReadOnlySpan<byte> data)
            {
                if (data.Length != DataLength)
                    throw new ArgumentOutOfRangeException(nameof(data), $"Length was not {DataLength}.");

                data.CopyTo(MemoryMarshal.CreateSpan(ref dataBytes[0], DataLength));
            }

            public readonly bool IsSet
            {
                get
                {
                    for (int i = 0; i < DataLength; i++)
                        if (dataBytes[i] != 0)
                            return true;

                    return false;
                }
            }
        }
    }
}
