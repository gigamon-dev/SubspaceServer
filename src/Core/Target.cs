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

        /// <summary>Refers to one freq in an arena, <see cref="ITeamTarget"/>.</summary>
        Freq,

        ///<summary>Refers to the whole zone.</summary>
        Zone,

        /// <summary>Refers to an arbitrary set of players, <see cref="ISetTarget"/>.</summary>
        Set,
    }

    public interface ITarget
    {
        TargetType Type { get; }
    }

    public interface IPlayerTarget : ITarget
    {
        Player Player { get; }
    }

    public interface IArenaTarget : ITarget
    {
        Arena Arena { get; }
    }

    public interface ITeamTarget : IArenaTarget
    {
        int Freq { get; }
    }

    public interface ISetTarget : ITarget
    {
        HashSet<Player> Players { get; }
    }

    public class TeamTarget : ITeamTarget
    {
        private readonly Arena _arena;
        private readonly int _freq;

        public TeamTarget(Arena arena, int freq)
        {
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
            _freq = freq;
        }

        #region ITeamTarget Members

        public int Freq => _freq;

        #endregion

        #region IArenaTarget Members

        public Arena Arena => _arena;

        #endregion

        #region ITarget Members

        public TargetType Type => TargetType.Freq;

        #endregion
    }

    public class SetTarget : ISetTarget
    {
        private readonly HashSet<Player> _players;

        public SetTarget(HashSet<Player> players)
        {
            _players = players ?? throw new ArgumentNullException(nameof(players));
        }

        #region ISetTarget Members

        public HashSet<Player> Players => _players;

        #endregion

        #region ITarget Members

        public TargetType Type => TargetType.Set;

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

            public TargetType Type => _type;

            #endregion
        }

        /// <summary>
        /// No players
        /// </summary>
        public static ITarget NoTarget { get; } = new BasicTarget(TargetType.None);

        /// <summary>
        /// Target the entire zone.
        /// </summary>
        public static ITarget ZoneTarget { get; } = new BasicTarget(TargetType.Zone);

        /// <summary>
        /// Gets an <see cref="ITarget"/> that targets a specific team in an arena.
        /// </summary>
        /// <param name="arena"></param>
        /// <param name="freq"></param>
        public static ITeamTarget TeamTarget(Arena arena, int freq)
        {
            return arena.GetTeamTarget(freq);
        }

        /// <summary>
        /// Gets an <see cref="ITarget"/> that targets an arbitrary set of players
        /// </summary>
        /// <param name="players"></param>
        public static ISetTarget ListTarget(HashSet<Player> players)
        {
            return new SetTarget(players); // TODO: pooling of objects?
        }
    }
}
