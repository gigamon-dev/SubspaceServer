using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
            if (data.Length != BannerData.Length)
                throw new ArgumentOutOfRangeException(nameof(data), $"Must be {BannerData.Length} in length.");

            data.CopyTo(_data);
        }

		/// <summary>
		/// Whether the banner is set.
		/// </summary>
		/// <remarks>
		/// <see langword="false"/> means the data consists of all zeros.
		/// </remarks>
		public readonly bool IsSet => MemoryExtensions.ContainsAnyExcept((ReadOnlySpan<byte>)_data, (byte)0);

		/// <summary>
		/// This nested struct is to allow <see cref="Banner"/> to be readonly.
		/// <see cref="Banner"/> couldn't be readonly with the fixed size buffer directly inside of it.
		/// Having <see cref="Banner"/> as a readonly struct helps prevent defensive copies when passed as an 'in' parameter.
		/// </summary>
		[InlineArray(Length)]
		private struct BannerData
        {
            public const int Length = 96;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;
        }
    }
}
