namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate that is invoked right before a player's ship or freq (team) is changed.
    /// </summary>
    /// <remarks>
    /// This is executed synchronously before a player's ship or freq is changed.
    /// In other words, before <see cref="Player.Ship"/> and <see cref="Player.Freq"/> are changed, 
    /// and before the <see cref="Packets.Game.S2CPacketType.ShipChange"/> packet is sent to anyone.
    /// Handlers must not perform another ship or freq change as that would be recursive and will lead to unexpected, undefined behavior.
    /// </remarks>
    /// <param name="player">The player whose ship is changing.</param>
    /// <param name="newShip">The type of ship that the player changing to.</param>
    /// <param name="oldShip">The type of ship that the player changing from.</param>
    /// <param name="newFreq">The player's new team.</param>
    /// <param name="oldFreq">The player's old team.</param>
    [ComponentCallback]
    public delegate void BeforeShipFreqChangeCallback(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq);

    /// <summary>
    /// Callback delegate that is invoked when a player's ship or freq (team) is changed.
    /// </summary>
    /// <remarks>
    /// This is executed synchronously after a player's ship or freq is changed.
    /// Handlers must not perform another ship or freq change as that would be recursive and will lead to unexpected, undefined behavior.
    /// </remarks>
    /// <param name="player">The player whose ship was changed.</param>
    /// <param name="newShip">The type of ship that the player changed to.</param>
    /// <param name="oldShip">The type of ship that the player changed from.</param>
    /// <param name="newFreq">The player's new team.</param>
    /// <param name="oldFreq">The player's old team.</param>
    [ComponentCallback]
    public delegate void PreShipFreqChangeCallback(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq);

    /// <summary>
    /// Callback delegate that is invoked when a player's ship or freq (team) is changed.
    /// </summary>
    /// <remarks>
    /// This is executed asynchronously, after a player's ship or freq is changed.
    /// </remarks>
    /// <param name="player">The player whose ship was changed.</param>
    /// <param name="newShip">The type of ship that the player changed to.</param>
    /// <param name="oldShip">The type of ship that the player changed from.</param>
    /// <param name="newFreq">The player's new team.</param>
    /// <param name="oldFreq">The player's old team.</param>
    [ComponentCallback]
    public delegate void ShipFreqChangeCallback(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq);
}
