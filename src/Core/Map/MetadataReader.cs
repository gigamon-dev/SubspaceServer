using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.Map
{
    /*
    public static class MetadataReader
    {
        /// <summary>
        /// To split a byte[] into metadata chunks.
        /// </summary>
        /// <param name="chunkLookup">place to store chunks that are read</param>
        /// <param name="arraySegment">byte array to read chunks from</param>
        /// <returns>true if the entire chunk data was read, otherwise false</returns>
        public static bool readChunks(MultiDictionary<uint, byte[]> chunkLookup, ArraySegment<byte> arraySegment)
        {
            if (chunkLookup == null)
                throw new ArgumentNullException("chunkLookup");

            int offset = arraySegment.Offset;
            int endOffset = arraySegment.Offset + arraySegment.Count;

            while (offset + ChunkHeader.Length <= endOffset)
            {
                // first check chunk header
                ChunkHeader ch = new ChunkHeader(arraySegment.Array, offset);
                int chunkSize = (int)ch.Size;
                int chunkSizeWithHeader = chunkSize + ChunkHeader.Length;
                if (chunkSize > MaxChunkSize || (offset + chunkSizeWithHeader) > endOffset)
                    break;

                // allocate space for the chunk and copy it in
                byte[] c = new byte[chunkSizeWithHeader];
                Array.Copy(arraySegment.Array, offset, c, 0, chunkSizeWithHeader);

                chunkLookup.AddLast(ch.Type, c);
                //chunkLookup[ch.Type] = c;

                offset += chunkSizeWithHeader;
                if ((chunkSize & 3) != 0)
                {
                    // account for the 4 byte boundary padding
                    int padding = 4 - (chunkSize & 3);
                    offset += padding;
                }
            }

            return offset == endOffset;
        }
    }
    */
}
