using OpenSkillSharp.Rating;

namespace SS.Matchmaking.OpenSkill
{
    /// <summary>
    /// An OpenSkill rating for a player.
    /// </summary>
    public class PlayerRating : Rating, IRating
    {
        /// <summary>
        /// The name of the player the rating is for.
        /// </summary>
        public required string PlayerName { get; set; }

        /// <summary>
        /// Timestamp that the rating was last updated in the database.
        /// This can be used to calcuate a rating decay for players that have not played in a long time.
        /// </summary>
        /// <remarks>
        /// Another use of this is to pass it to the database when saving.
        /// When <see langword="null"/>, the database will know it's for a new rating that is to be INSERTED only.
        /// When not <see langword="null"/>, the database will know it's for an existing rating that is to be UPDATED, and only if the timestamp matches.
        /// This protects against a brand new rating (initial values or rated from initial values) from overwriting an existing one.
        /// Also, it protects against updating if it's old game data being back-loaded in.
        /// </remarks>
        public DateTime? LastUpdated { get; set; }

        IRating IRating.Clone()
        {
            return new PlayerRating()
            {
                PlayerName = PlayerName,
                LastUpdated = LastUpdated,
                Mu = Mu,
                Sigma = Sigma,
            };
        }
    }
}
