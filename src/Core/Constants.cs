using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
    /// <summary>
    /// equivalent of param.h
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// the search path for config files
        /// </summary>
        public const string CFG_CONFIG_SEARCH_PATH = "arenas/%b/%n:conf/%n:%n:arenas/(default)/%n";

        /// <summary>
        /// how many incoming rel packets to buffer for a client
        /// </summary>
        public const int CFG_INCOMING_BUFFER = 32;

        public const int MaxPacket = 512;

        /// <summary>
        /// callbacks / events
        /// </summary>
        public static class Events
        {
            public const string ConnectionInit = "conninit";
        }
    }
}
