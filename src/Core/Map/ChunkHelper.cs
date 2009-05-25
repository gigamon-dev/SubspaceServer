using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Map
{
    public static class ChunkHelper
    {
        /// <summary>
        /// Maximum size in bytes that a metadata chunk can be.
        /// </summary>
        private const int MaxChunkSize = 128 * 1024;

        /// <summary>
        /// to read raw metadata chunks
        /// </summary>
        /// <param name="chunkLookup">place to store chunks that are read</param>
        /// <param name="arraySegment">byte array to read chunks from</param>
        /// <returns>true if the entire chunk data was read, otherwise false</returns>
        public static bool ReadChunks(MultiDictionary<uint, ArraySegment<byte>> chunkLookup, ArraySegment<byte> source)
        {
            if (chunkLookup == null)
                throw new ArgumentNullException("chunkLookup");

            int offset = source.Offset;
            int endOffset = source.Offset + source.Count;

            while (offset + ChunkHeader.Length <= endOffset)
            {
                // first check chunk header
                ChunkHeader ch = new ChunkHeader(source.Array, offset);
                int chunkSize = (int)ch.Size;
                int chunkSizeWithHeader = chunkSize + ChunkHeader.Length;
                if (chunkSize > MaxChunkSize || (offset + chunkSizeWithHeader) > endOffset)
                    break;

                chunkLookup.AddLast(ch.Type, ch.DataWithHeader);
                /*
                // allocate space for the chunk and copy it in
                // TODO: (enhancement) probably dont need to allocate a new array like asss does, maybe use ArraySegment<byte> instead?
                byte[] c = new byte[chunkSizeWithHeader];
                Array.Copy(arraySegment.Array, offset, c, 0, chunkSizeWithHeader);
                
                chunkLookup.AddLast(ch.Type, c);
                //chunkLookup[ch.Type] = c;
                */
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

        /// <summary>
        /// calls the callback for each chunk.
        /// if the callback returns true, the chunk will be removed (meaning it's been sucessfully processed)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="chunkLookup"></param>
        /// <param name="chunkProcessingCallback"></param>
        /// <param name="clos">argument to use when calling the callback</param>
        public static void ProcessChunks<T>(
            MultiDictionary<uint, ArraySegment<byte>> chunkLookup,
            Func<uint, ArraySegment<byte>, T, bool> chunkProcessingCallback,
            T clos)
        {
            if (chunkLookup == null)
                throw new ArgumentNullException("chunkLookup");

            if (chunkProcessingCallback == null)
                throw new ArgumentNullException("chunkProcessingCallback");

            LinkedList<KeyValuePair<uint, ArraySegment<byte>>> chunksToRemove = null;

            try
            {
                foreach (KeyValuePair<uint, ArraySegment<byte>> kvp in chunkLookup)
                {
                    if (chunkProcessingCallback(kvp.Key, kvp.Value, clos))
                    {
                        if (chunksToRemove == null)
                            chunksToRemove = new LinkedList<KeyValuePair<uint, ArraySegment<byte>>>();

                        chunksToRemove.AddLast(kvp);
                    }
                }
            }
            finally
            {
                if (chunksToRemove != null)
                {
                    foreach (KeyValuePair<uint, ArraySegment<byte>> kvp in chunksToRemove)
                        chunkLookup.Remove(kvp.Key, kvp.Value);
                }
            }
        }
    }
}
