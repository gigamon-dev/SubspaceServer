using System.Collections.ObjectModel;

namespace SS.Matchmaking.TeamVersus
{
    public interface ITeam
    {
        /// <summary>
        /// The match the team is for.
        /// </summary>
        IMatchData MatchData { get; }

        /// <summary>
        /// The index of the team in the match (see <see cref="IMatchData.Teams"/>).
        /// </summary>
        int TeamIdx { get; }

        /// <summary>
        /// The freq # of the team.
        /// </summary>
        short Freq { get; }

        /// <summary>
        /// The slots for players on the team.
        /// </summary>
        ReadOnlyCollection<IPlayerSlot> Slots { get; }

        /// <summary>
        /// The team's score.
        /// </summary>
        short Score { get; }
    }
}
