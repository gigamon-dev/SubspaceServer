using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SS.Matchmaking.Persist
{
    /// <summary>
    /// Keys for use with the <see cref="Core.ComponentInterfaces.IPersist"/>.
    /// </summary>
    /// <remarks>
    /// Keys need to be globally unique. 
    /// This includes being unique those in <see cref="Core.ComponentInterfaces.PersistKey"/>.
    /// </remarks>
    public enum PersistKey
    {
        MatchmakingQueuesPlayerData = 10000,
    }
}
