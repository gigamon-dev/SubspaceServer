using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
    public enum TargetType
    {
        /// <summary>refers to no players</summary>
        None,

        /// <summary>refers to one single player (u.p must be filled in)</summary>
        Player,

        /// <summary>refers to a whole arena (u.arena must be filled in)</summary>
        Arena,

        /// <summary>refers to one freq (u.freq must be filled in)</summary>
        Freq,

        ///<summary>refers to the whole zone</summary>
        Zone,

        /// <summary>refers to an arbitrary set of players (u.list)</summary>
        List
    }

    public class Target
    {
        public TargetType Type;

        // ASSS puts this in a union, maybe i should do that too?
        // or maybe inheritance?  but then there'd be lots of casting...
        public Player Player;
        public Arena Arena;
        public int Freq;
        public IEnumerable<Player> List;

        public Target()
        {
        }
    }
}
