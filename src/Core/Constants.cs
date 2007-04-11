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
    }
}
