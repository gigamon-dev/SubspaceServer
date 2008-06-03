using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core
{
    public class NetStats
    {
        public ulong pcountpings, pktsent, pktrecvd;
        public ulong bytesent, byterecvd;
        public ulong buffercount, buffersused;
        public ulong[] grouped_stats = new ulong[8];
        public ulong[] pri_stats = new ulong[5];
        //byte reserved[176];
    }
}
