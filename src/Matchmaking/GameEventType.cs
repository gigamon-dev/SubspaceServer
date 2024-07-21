namespace SS.Matchmaking
{
    /// <summary>
    /// Represents the type of game event.
    /// </summary>
    /// <remarks>
    /// The values should match those in the database lookup table: subspacestats.ss.event_type
    /// </remarks>
    public enum GameEventType
    {
        #region Team Versus

        /// <summary>
        /// When a player is assinged to a slot on a team.
        /// </summary>
        TeamVersus_AssignSlot = 1,

        /// <summary>
        /// When a player is killed.
        /// </summary>
        TeamVersus_PlayerKill,

        /// <summary>
        /// When a player changes ship, including changing to spectator mode.
        /// </summary>
        TeamVersus_PlayerShipChange,

        /// <summary>
        /// When a player uses an item (e.g. repel)
        /// </summary>
        TeamVersus_PlayerUseItem,

        #endregion

        #region PowerBall

        /// <summary>
        /// When a goal is scored with a ball.
        /// </summary>
        Ball_Goal = 100,

        /// <summary>
        /// When a save is made.
        /// </summary>
        Ball_Save,

        /// <summary>
        /// When a ball is stolen.
        /// </summary>
        Ball_Steal,

        #endregion
    }
}
