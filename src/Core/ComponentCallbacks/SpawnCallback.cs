using System;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="SpawnDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class SpawnCallback
    {
        /// <summary>
        /// The reason a player spawned.
        /// </summary>
        [Flags]
        public enum SpawnReason
        {
            /// <summary>
            /// The player died and is respawning.
            /// </summary>
            AfterDeath = 1,

            /// <summary>
            /// A ship reset was applied to the player.
            /// </summary>
            ShipReset = 2,

            /// <summary>
            /// Spawn was triggered by a flag game victory.
            /// </summary>
            FlagVictory = 4,

            /// <summary>
            /// Changing ships triggered the spawn.
            /// </summary>
            ShipChange = 8,

            /// <summary>
            /// This is the first spawn since leaving spectator mode, or entering the arena.
            /// </summary>
            Initial = 16,
        }

        /// <summary>
        /// Delegate for a callback that is called when a player spawns.
        /// </summary>
        /// <param name="player">The player that spawned.</param>
        /// <param name="reason">
        /// The reason(s) for the spawn.
        /// There can be multiple reasons at the same time, use the bitwise & operator to determine which flags are set.
        /// </param>
        public delegate void SpawnDelegate(Player player, SpawnReason reason);
    }
}
