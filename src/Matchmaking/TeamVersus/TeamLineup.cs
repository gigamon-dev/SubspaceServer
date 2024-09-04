using Microsoft.Extensions.ObjectPool;

namespace SS.Matchmaking.TeamVersus
{
    public class TeamLineup : IResettable
    {
        /// <summary>
        /// Whether the team is from a premade group of players.
        /// </summary>
        public bool IsPremade
        {
            get
            {
                foreach ((_, int? premadeGroupId) in Players)
                {
                    if (premadeGroupId is not null)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// The players on the team.
        /// </summary>
        /// <remarks>
        /// <list type="table">
        /// <item><term>Key</term><description>Player Name</description></item>
        /// <item><term>Value</term><description>Premade Group ID (null for solo players)</description></item>
        /// </list>
        /// </remarks>
        public readonly Dictionary<string, int?> Players = new(StringComparer.OrdinalIgnoreCase);

        bool IResettable.TryReset()
        {
            Players.Clear();
            return true;
        }
    }
}
