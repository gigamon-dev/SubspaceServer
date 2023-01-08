namespace SS.Matchmaking.TeamVersus
{
    /// <summary>
    /// Unique identifier for a match's type and place.
    /// </summary>
    public readonly record struct MatchIdentifier(string MatchType, int ArenaNumber, int BoxIdx) : IEquatable<MatchIdentifier>
    {
        public bool Equals(MatchIdentifier other)
        {
            return string.Equals(MatchType, other.MatchType, StringComparison.OrdinalIgnoreCase)
                && ArenaNumber == other.ArenaNumber
                && BoxIdx == other.BoxIdx;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(MatchType), ArenaNumber.GetHashCode(), BoxIdx.GetHashCode());
        }
    }
}
