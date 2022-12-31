using System.Collections.ObjectModel;

namespace SS.Matchmaking.TeamVersus
{
    public interface ITeam
    {
        short Freq { get; }

        ReadOnlyCollection<IPlayerSlot> Slots { get; }

        short Score { get; }
    }
}
