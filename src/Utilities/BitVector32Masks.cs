using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.Specialized;

namespace SS.Utilities
{
    /// <summary>
    /// Helper to get boolean masks for use with a the <see cref="BitVector32"/>.
    /// For a BitVector32, you need the previous mask in order to get a subsequent mask.
    /// This makes it easier to get a mask by being able to specify the index of a bit, 
    /// where an index of 0 is the lowest order bit and 31 as the highest order.
    /// </summary>
    public static class BitVector32Masks
    {
        static BitVector32Masks()
        {
            _masks = new int[32];
            for (int x = 0; x < 32; x++)
            {
                if (x == 0)
                    _masks[x] = BitVector32.CreateMask();
                else
                    _masks[x] = BitVector32.CreateMask(_masks[x - 1]);
            }
        }

        private static int[] _masks;

        /// <summary>
        /// Gets a mask for reading a boolean bit at a particular location.
        /// </summary>
        /// <param name="bitIndex">
        /// The index of the bit to get a mask for.
        /// Such that,  0 is the lowest order bit and 31 as the highest order.
        /// </param>
        /// <returns>The mask.</returns>
        public static int GetMask(int bitIndex)
        {
            if (bitIndex < 0 || bitIndex > 31)
                throw new ArgumentOutOfRangeException("bitIndex");

            return _masks[bitIndex];
        }
    }
}
