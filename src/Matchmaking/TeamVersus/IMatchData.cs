using SS.Core;
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

        /// <summary>
        /// The arena the match is in.
        /// </summary>
        /// <remarks><see langword="null"/> if the arena does not exist (e.g. no players in it).</remarks>
        Arena Arena { get; }

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
