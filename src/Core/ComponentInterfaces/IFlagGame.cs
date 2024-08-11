using SS.Core.Map;
using System;
using System.Diagnostics.CodeAnalysis;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Flag:CarryFlags setting on whether flags can be carried, and if so, if there's a limit to how many can be carried at once.
    /// </summary>
    public enum ConfigCarryFlags
    {
        /// <summary>
        /// No
        /// </summary>
        None = 0,

        /// <summary>
        /// Flags can be carried. No limit.
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
        /// Gets the # of flags in an arena.
        /// </summary>
        /// <param name="arena">The arena to get the flag count for.</param>
        /// <returns>The # of flags.</returns>
        short GetFlagCount(Arena arena);

        /// <summary>
        /// Gets the # of flags owned by a team.
        /// </summary>
        /// <param name="arena">The arena to get the flag count for.</param>
        /// <param name="freq">The team to get the flag count for.</param>
        /// <returns>The # of flags.</returns>
        short GetFlagCount(Arena arena, short freq);
    }

    public interface IStaticFlagGame : IFlagGame
    {
        /// <summary>
        /// Gets the owner team of each flag.
        /// </summary>
        /// <param name="arena">The arena to get the flags owners for.</param>
        /// <param name="owners">The owners. This must be the same length as <see cref="IFlagGame.GetFlagCount(Arena)"/>.</param>
        /// <returns><see langword="true"/> if <paramref name="owners"/> was populated. <see langword="false"/> if there was an error.</returns>
        bool TryGetFlagOwners(Arena arena, Span<short> owners);

        /// <summary>
        /// Sets the owner team of each flag.
        /// </summary>
        /// <param name="arena">The arena to set the flag owners for.</param>
        /// <param name="flagOwners">The owner freq of each flag.</param>
        /// <returns><see langword="true"/> if the flag data was updated. <see langword="false"/> if there was an error.</returns>
        bool SetFlagOwners(Arena arena, ReadOnlySpan<short> flagOwners);

        /// <summary>
        /// Gets the owner team of a single flag in an arena.
        /// </summary>
        /// <param name="arena">The arena to get the flag owner in.</param>
        /// <param name="flagId">The flag to get data for.</param>
        /// <param name="owner">The freq that owns the flag.</param>
        /// <returns>True if the flag owner was retrieved. Otherwise, false.</returns>
        bool TryGetFlagOwner(Arena arena, short flagId, out short owner);

        /// <summary>
        /// Fakes a player touching (claiming) a flag.
        /// </summary>
        /// <param name="player">The player to fake touching the flag.</param>
        /// <param name="flagId">The flag to set data for.</param>
        /// <returns>True if the flag was updated. Otherwise, false.</returns>
        bool FakeTouchFlag(Player player, short flagId);
    }

    public enum FlagState
    {
        None,
        Carried,
        OnMap,
    }

    public enum FlagPickupReason
    {
        Pickup,
        Kill,
        Other,
    }

    public interface IFlagInfo
    {
        FlagState State { get; }
        Player? Carrier { get; }
        MapCoordinate? Location { get; }
        short Freq { get; }
    }

    public interface ICarryFlagSettings
    {
        bool AutoStart { get; }
        TimeSpan ResetDelay { get; }
        MapCoordinate SpawnCoordinate { get; }
        int SpawnRadius { get; }
        int DropRadius { get; }
        bool FriendlyTransfer { get; }
        ConfigCarryFlags CarryFlags { get; }
        bool DropOwned { get; }
        bool DropCenter { get; }
        bool NeutOwned { get; }
        bool NeutCenter { get; }
        bool TeamKillOwned { get; }
        bool TeamKillCenter { get; }
        bool SafeOwned { get; }
        bool SafeCenter { get; }
        TimeSpan WinDelay { get; }
        int MaxFlags { get; }
        int MinFlags { get; }
    }

    public interface ICarryFlagGame : IFlagGame
    {
        ICarryFlagSettings? GetSettings(Arena arena);

        /// <summary>
        /// Starts the flag game in an arena.
        /// </summary>
        /// <param name="arena">The arena to start the flag game in.</param>
        /// <returns><see langword="true"/> if the flag game was started. <see langword="false"/> if the flag game was already running.</returns>
        bool StartGame(Arena arena);

        /// <summary>
        /// Resets the flag game in an arena
        /// </summary>
        /// <remarks>
        /// Depending on settings, the flag game may automatically be restarted. See the return value.
        /// </remarks>
        /// <param name="arena">The arena to reset the flag game in.</param>
        /// <param name="winnerFreq">The team that won. -1 for no winner.</param>
        /// <param name="points">The # of points awarded. 0 for no points.</param>
        /// <returns><see langword="true"/> if the flag game was automatically restarted. <see langword="false"/> if the flag game needs to be manually started.</returns>
        bool ResetGame(Arena arena, short winnerFreq, int points, bool allowAutoStart);

        /// <summary>
        /// Gets the # of flags a player is carrying.
        /// </summary>
        /// <param name="player">The player to get the # of flags for.</param>
        /// <returns>The # of flags being carried.</returns>
        int GetFlagCount(Player player);

        /// <summary>
        /// Performs flag transfer logic when a player is killed.
        /// </summary>
        /// <param name="arena">The arena the kill occured in.</param>
        /// <param name="killed">The player that was killed.</param>
        /// <param name="killer">The player that got the kill.</param>
        /// <returns>
        /// The # of flags that were transferred from the <paramref name="killed"/> player to the <paramref name="killer"/>.
        /// This value will be sent in the <see cref="Packets.Game.S2C_Kill"/> packet.
        /// </returns>
        short TransferFlagsForPlayerKill(Arena arena, Player killed, Player killer); // TODO: maybe move this to a different interface, only meant for the Game module to call.

        /// <summary>
        /// Adds a flag to an arena.
        /// </summary>
        /// <param name="arena">The arena to add a flag to.</param>
        /// <param name="flagId">The flag added. 0 if no flag was added.</param>
        /// <returns>True if a flag was added. Otherwise, false.</returns>
        bool TryAddFlag(Arena arena, out short flagId);

        bool TryGetFlagInfo(Arena arena, short flagId, [MaybeNullWhen(false)] out IFlagInfo flagInfo);

        bool TrySetFlagNeuted(Arena arena, short flagId, MapCoordinate? location = null, short freq = -1);

        bool TrySetFlagOnMap(Arena arena, short flagId, MapCoordinate location, short freq);

        bool TrySetFlagCarried(Arena arena, short flagId, Player carrier, FlagPickupReason reason);
    }

    public enum AdjustFlagReason
    {
        /// <summary>
        /// Regular drop.
        /// </summary>
        Dropped,

        /// <summary>
        /// Dropped by a carrier in a safe zone.
        /// </summary>
        InSafe,

        /// <summary>
        /// Changed ship.
        /// </summary>
        ShipChange,

        /// <summary>
        /// Changed teams.
        /// </summary>
        FreqChange,

        /// <summary>
        /// Left the arena / exited the game.
        /// </summary>
        LeftArena,
    }

    public interface ICarryFlagBehavior : IComponentInterface
    {
        /// <summary>
        /// Called when a game is to be started.
        /// </summary>
        /// <remarks>
        /// An implementation should add flags by calling <see cref="ICarryFlagGame.TryAddFlag(Arena, out short)"/>.
        /// </remarks>
        /// <param name="arena">The arena the flag game is being started in.</param>
        void StartGame(Arena arena);

        /// <summary>
        /// Called when neuted flags should be spawned.
        /// </summary>
        /// <param name="arena">The arena that flags should be spawned in.</param>
        void SpawnFlags(Arena arena);

        /// <summary>
        /// Called when a player is killed.
        /// </summary>
        /// <remarks>
        /// An implementation should determine what should happen to any flags the <paramref name="killed"/> player was carrying.
        /// Note: this happens prior to the <see cref="ComponentCallbacks.KillCallback"/>.
        /// </remarks>
        /// <param name="arena">The arena.</param>
        /// <param name="killed">The player that was killed.</param>
        /// <param name="killer">The player that made the kill.</param>
        /// <returns>The # of flags transferred. This is the value to be sent in the <see cref="Packets.Game.S2C_Kill"/> packet.</returns>
        short PlayerKill(Arena arena, Player killed, Player killer, ReadOnlySpan<short> flagIds);

        /// <summary>
        /// Called when a player touches a flag.
        /// </summary>
        /// <remarks>
        /// An implementation should determine what happens to the flag. 
        /// The flag can be allowed to be picked up by calling <see cref="ICarryFlagGame.TrySetFlagCarried(Arena, short, Player)"/> 
        /// or the implementation can choose to do something else.
        /// </remarks>
        /// <param name="arena">The arena.</param>
        /// <param name="player">The player that touched the flag.</param>
        /// <param name="flagId">The flag that was touched.</param>
        void TouchFlag(Arena arena, Player player, short flagId);

        /// <summary>
        /// Called when flags that were being carried need to be adjusted.
        /// </summary>
        /// <remarks>
        /// This occurs:
        /// <list type="bullet">
        /// <item>For a normal flag drop, when a player's flag carry timer expires.</item>
        /// <item>For a flag drop due to being in a safe zone.</item>
        /// <item>When a player carrying flags changes ship.</item>
        /// <item>When a player carrying flags changes team.</item>
        /// <item>When a player carrying flags leaves the arena or logs off.</item>
        /// </list>
        /// </remarks>
        /// <param name="arena">The arena.</param>
        /// <param name="flagIds">The flag(s) to adjust.</param>
        /// <param name="reason">The reason for the adjustment.</param>
        /// <param name="oldCarrier">The player that was carrying the flag(s).</param>
        /// <param name="oldFreq">The freq that owned the flag(s).</param>
        void AdjustFlags(Arena arena, ReadOnlySpan<short> flagIds, AdjustFlagReason reason, Player oldCarrier, short oldFreq);
    }
}
