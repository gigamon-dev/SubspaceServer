namespace SS.Matchmaking.OpenSkill
{
    /// <summary>
    /// The arguments to use when calculating the ordinal to display an OpenSkill rating.
    /// </summary>
    public readonly record struct OrdinalArgs(double Z, double Alpha, double Target);
}
