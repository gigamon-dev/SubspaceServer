using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Flag:CarryFlags setting on whether flags can be picked up.
    /// </summary>
    public enum ConfigCarryFlags
    {
        /// <summary>
        /// No
        /// </summary>
        None = 0,

        /// <summary>
        /// Flags can be carried.
        /// </summary>
        Yes,

        /// <summary>
        /// One flag can be carried at a time.
        /// </summary>
        One,
    }

    public interface IFlagGame : IComponentInterface
    {
        /// <summary>
        /// Resets the flag game.
        /// </summary>
        /// <param name="arena">The arena to reset the flag game for.</param>
        void ResetGame(Arena arena);

        /// <summary>
        /// Gets the # of flags.
        /// </summary>
        /// <param name="arena">The arena to get the flag count for.</param>
        /// <returns></returns>
        int GetFlagCount(Arena arena);

        /// <summary>
        /// Gets the # of flags owned by a team.
        /// </summary>
        /// <param name="arena">The arena to get the flag count for.</param>
        /// <param name="freq">The team to get the flag count for.</param>
        /// <returns></returns>
        int GetFlagCount(Arena arena, int freq);
    }

    public interface IStaticFlagGame : IFlagGame
    {
        /// <summary>
        /// Gets the owner team of each flag.
        /// </summary>
        /// <param name="arena">The arena to get the flags owners for.</param>
        /// <returns>The owners.</returns>
        ReadOnlySpan<short> GetFlagOwners(Arena arena);

        /// <summary>
        /// Sets the owner team of each flag.
        /// </summary>
        /// <param name="arena">The arena to set the flag owners for.</param>
        /// <param name="flagOwners">The owner freq of each flag.</param>
        /// <returns><see langword="true"/> if the flag data was updated. <see langword="false"/> if there was an error.</returns>
        bool SetFlagOwners(Arena arena, ReadOnlySpan<short> flagOwners);
    }

    public interface ICarryFlagGame : IFlagGame
    {
        /// <summary>
        /// Gets the # of flags a player is carrying.
        /// </summary>
        /// <param name="player">The player to get the # of flags for.</param>
        /// <returns>The # of flags being carried.</returns>
        int GetFlagCount(Player player);

        //void GetFlagInfo()

        //void NeutFlag(Arena arena, )

        //void MoveFlag(Arena arena, )
    }
}
