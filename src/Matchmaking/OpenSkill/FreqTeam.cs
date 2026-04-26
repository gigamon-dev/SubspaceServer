using OpenSkillSharp.Rating;

namespace SS.Matchmaking.OpenSkill
{
    /// <summary>
    /// An OpenSkill team for a freq.
    /// </summary>
    public class FreqTeam : Team
    {
        public required short Freq { get; set; }

        public override ITeam Clone()
        {
            // OpenSkillSharp expects a deep copy.
            return new FreqTeam
            {
                Freq = Freq,
                Players = Players.Select(p => p.Clone()).ToList(),
            };
        }
    }
}
