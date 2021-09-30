using System;
using System.Collections.Generic;

namespace SS.Core
{
    public enum TargetType
    {
        /// <summary>Refers to no players.</summary>
        None,

        /// <summary>Refers to one single player, <see cref="IPlayerTarget"/>.</summary>
        Player,

        /// <summary>Refers to a whole arena, <see cref="IArenaTarget"/>.</summary>
        Arena,

        /// <summary>Refers to one freq, <see cref="ITeamTarget"/>.</summary>
        Freq,

        ///<summary>Refers to the whole zone.</summary>
        Zone,

        /// <summary>Refers to an arbitrary set of players, <see cref="IListTarget"/>.</summary>
        List
    }

    public interface ITarget
    {
        TargetType Type
        {
            get;
        }
    }

    public interface IPlayerTarget : ITarget
    {
        Player Player
        {
            get;
        }
    }

    public interface IArenaTarget : ITarget
    {
        Arena Arena
        {
            get;
        }
    }

    public interface ITeamTarget : IArenaTarget
    {
        int Freq
        {
            get;
        }
    }

    public interface IListTarget : ITarget
    {
        IEnumerable<Player> List
        {
            get;
        }
    }

    public class TeamTarget : ITeamTarget
    {
        private Arena _arena;
        private int _freq;

        public TeamTarget(Arena arena, int freq)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            _arena = arena;
            _freq = freq;
        }

        #region ITeamTarget Members

        public int Freq
        {
            get { return _freq; }
        }

        #endregion

        #region IArenaTarget Members

        public Arena Arena
        {
            get { return _arena; }
        }

        #endregion

        #region ITarget Members

        public TargetType Type
        {
            get { return TargetType.Freq; }
        }

        #endregion
    }

    public static class Target
    {
        private class BasicTarget : ITarget
        {
            private TargetType _type;

            public BasicTarget(TargetType type)
            {
                _type = type;
            }

            #region ITarget Members

            public TargetType Type
            {
                get { return _type; }
            }

            #endregion
        }

        private static readonly ITarget _noTarget = new BasicTarget(TargetType.None);
        private static readonly ITarget _zoneTarget = new BasicTarget(TargetType.Zone);

        // ASSS puts this in a union, maybe i should do that too?
        // or maybe inheritance?  but then there'd be lots of casting...
        //public readonly Player Player;
        //public readonly Arena Arena;
        //public readonly int Freq;
        //public readonly IEnumerable<Player> List;

        /// <summary>
        /// No players
        /// </summary>
        public static ITarget NoTarget
        {
            get { return _noTarget; }
        }

        /// <summary>
        /// Target the entire zone.
        /// </summary>
        public static ITarget ZoneTarget
        {
            get { return _zoneTarget; }
        }

        /// <summary>
        /// Target a single player
        /// </summary>
        /// <param name="p"></param>
        public static IPlayerTarget PlayerTarget(Player p)
        {
            return p;
        }

        /// <summary>
        /// Target an entire arena
        /// </summary>
        /// <param name="arena"></param>
        public static IArenaTarget ArenaTarget(Arena arena)
        {
            return arena;
        }

        /// <summary>
        /// Target a specific team in an arena
        /// </summary>
        /// <param name="arena"></param>
        /// <param name="freq"></param>
        public static ITeamTarget TeamTarget(Arena arena, int freq)
        {
            return new TeamTarget(arena, freq);
        }

        /// <summary>
        /// Target a list of players
        /// </summary>
        /// <param name="players"></param>
        public static IListTarget ListTarget(IEnumerable<Player> players)
        {
            return null;
            //Type = TargetType.List;
            //List = players;
        }
    }
}
