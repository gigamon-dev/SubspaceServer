using System.Collections.ObjectModel;

namespace SS.Matchmaking.TeamVersus
{
    public interface IMatchData
    {
        /// <summary>
        /// Uniquely identifies a match's type and place.
        /// </summary>
        MatchIdentifier MatchIdentifier { get; }

        /// <summary>
        /// Configuration settings for the match.
        /// </summary>
        IMatchConfiguration Configuration { get; }

        /// <summary>
        /// The name of the arena the match is in.
        /// </summary>
        string ArenaName { get; }

        //Arena Arena { get; } // TODO: set it when the arena is created, clear it (null) when destroyed.

        /// <summary>
        /// The teams in the match.
        /// </summary>
        ReadOnlyCollection<ITeam> Teams { get; }

        /// <summary>
        /// When the match started.
        /// </summary>
        DateTime? Started { get; }
    }
}
