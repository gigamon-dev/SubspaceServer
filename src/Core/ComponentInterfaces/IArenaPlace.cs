using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that determines the arena that a player should be placed into
    /// when the player wants to enter an arena without indicating a specific one.
    /// </summary>
    /// <remarks>
    /// This can occur when:
    /// <list type="bullet">
    /// <item>The player first logs in.</item>
    /// <item>The player uses the ?go command without specifying an arena name.</item>
    /// </list>
    /// </remarks>
    public interface IArenaPlace : IComponentInterface
    {
        /// <summary>
        /// Place a player in an arena.
        /// This will be called when a player requests to join an arena
        /// without indicating any preference.
        /// </summary>
        /// <remarks>
        /// Implementors: To place the player, you
        /// should write an arena name into <paramref name="arenaName"/>
        /// and return true.  Optionally, put some spawn
        /// coordinates at the locations pointed to by spawnx and spawny.
        /// </remarks>
        /// <param name="arenaName">The span to which the arena name should be written into.</param>
        /// <param name="spawnX">to put spawn coordinates, if desired</param>
        /// <param name="spawnY">to put spawn coordinates, if desired</param>
        /// <param name="player">the player being placed</param>
        /// <param name="charsWritten">When this method returns, contains the number of characters written to <paramref name="arenaName"/>.</param>
        /// <returns><see langword="true"/> if an arena to place the player in was determined. Otherwise, <see langword="false"/>.</returns>
        bool TryPlace(Span<char> arenaName, ref int spawnX, ref int spawnY, Player player, out int charsWritten);
    }
}
