using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for when a match is about to start.
    /// </summary>
    /// <param name="match"></param>
    [ComponentCallback]
    public delegate void MatchStartingCallback(IMatch match);

    /// <summary>
    /// Callback delegate for when a match has started.
    /// </summary>
    /// <param name="match"></param>
    [ComponentCallback]
    public delegate void MatchStartedCallback(IMatch match);

    /// <summary>
    /// Callback delegate for when a match is about to end.
    /// </summary>
    /// <param name="match"></param>
    [ComponentCallback]
    public delegate void MatchEndingCallback(IMatch match);

    /// <summary>
    /// Callback delegate for when a match has ended.
    /// </summary>
    /// <param name="match"></param>
    [ComponentCallback]
    public delegate void MatchEndedCallback(IMatch match);

    /// <summary>
    /// Callback delegate for when player has been added to a match.
    /// </summary>
    /// <param name="match"></param>
    /// <param name="playerName"></param>
    /// <param name="player"></param>
    [ComponentCallback]
    public delegate void MatchAddPlayingCallback(IMatch match, string playerName, Player? player);

    /// <summary>
    /// Callback delegate for when a player has been removed from a match.
    /// </summary>
    /// <param name="match"></param>
    /// <param name="playerName"></param>
    /// <param name="player"></param>
    [ComponentCallback]
    public delegate void MatchRemovePlayingCallback(IMatch match, string playerName, Player? player);

    /// <summary>
    /// Callback delegate for when a player's match focus (which match the player is viewing) has changed.
    /// </summary>
    /// <param name="player"></param>
    /// <param name="oldMatch"></param>
    /// <param name="newMatch"></param>
    [ComponentCallback]
    public delegate void MatchFocusChangedCallback(Player player, IMatch? oldMatch, IMatch? newMatch);
}
