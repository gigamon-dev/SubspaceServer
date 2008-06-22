using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.Specialized;

namespace SS.Utilities
{
    /// <summary>
    /// since the BitVector32 class in the .Net Framework forces you do use masks and i dont like it
    /// this one hides the mask from you, just specify what bit you want 0 - 31
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

        public static int GetMask(int bitIndex)
        {
            if (bitIndex < 0 || bitIndex > 31)
                throw new ArgumentOutOfRangeException("bitIndex");

            return _masks[bitIndex];
        }
    /*
        

        private BitVector32 _bitVector;
        private uint _data;

        public BitField32(uint value)
        {
            BitVector32.CreateMask(
            _data = value;
        }

        public uint Data
        {
            get { return _data; }
        }

        public bool this[int bit]
        {
            get
            {
                if (bit < 0 || bit > 31)
                    throw new ArgumentOutOfRangeException("value");


            }

            set
            {
            }
        }
     */
    }
}
