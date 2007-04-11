using System;
using System.Collections.Generic;
using System.Text;

using SS.Utilities;
using System.IO;

namespace SS.Core
{
    public class SubspaceBuffer : DataBuffer<SubspaceBuffer>
    {
        public const int MAX_BUFFER_SIZE = 512;

        public readonly byte[] Bytes = new byte[MAX_BUFFER_SIZE];
        public int NumBytes;

        private MemoryStream memoryStream;
        public readonly BinaryReader Reader;
        public readonly BinaryWriter Writer;

        public SubspaceBuffer()
        {
            memoryStream = new MemoryStream(Bytes);
            Reader = new BinaryReader(memoryStream, System.Text.Encoding.ASCII);
            Writer = new BinaryWriter(memoryStream, System.Text.Encoding.ASCII);
        }
    }
}
