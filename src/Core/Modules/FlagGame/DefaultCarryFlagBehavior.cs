using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Utilities.ObjectPool;
using System;
using System.Collections.Generic;

namespace SS.Core.Modules.FlagGame
{
    /// <summary>
    /// Default implementation of behaviors for carriable flags.
    /// </summary>
    public sealed class DefaultCarryFlagBehavior : ICarryFlagBehavior
    {
        private readonly ICarryFlagGame _carryFlagGame;
        private readonly ILogManager _logManager;
        private readonly IMapData _mapData;
        private readonly IPrng _prng;

        private static readonly DefaultObjectPool<HashSet<TileCoordinates>> _tileCoordinatesHashSetPool = new(new HashSetPooledObjectPolicy<TileCoordinates>() { InitialCapacity = 256 });
        private static readonly DefaultObjectPool<List<TileCoordinates>> _tileCoordinatesListPool = new(new ListPooledObjectPolicy<TileCoordinates>() { InitialCapacity = 256 });

        public DefaultCarryFlagBehavior(
            ICarryFlagGame carryFlagGame,
            ILogManager logManager,
            IMapData mapData,
            IPrng prng)
        {
            _carryFlagGame = carryFlagGame ?? throw new ArgumentNullException(nameof(carryFlagGame));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));
        }

        #region ICarryFlagBehavior

        void ICarryFlagBehavior.StartGame(Arena arena)
        {
            var settings = _carryFlagGame.GetSettings(arena);
            if (settings == null)
                return;

            int numFlags = _prng.Number(settings.MinFlags, settings.MaxFlags);

            // Add the flags.
            for (int x = 0; x < numFlags; x++)
            {
                if (!_carryFlagGame.TryAddFlag(arena, out short flagId))
                    break;

                SpawnFlag(arena, flagId, settings.SpawnCoordinates, settings.SpawnRadius, -1);
            }

            // TODO: maybe each flag needs a DateTime on when it should be spawned and have a setting to control the delay?
            // or maybe just a delay for neuted flags? since there's already an optional delay for starting the game
        }

        void ICarryFlagBehavior.SpawnFlags(Arena arena)
        {
            if (arena is null)
                return;

            var settings = _carryFlagGame.GetSettings(arena);
            if (settings == null)
                return;

            short flagCount = _carryFlagGame.GetFlagCount(arena);
            for (short flagId = 0; flagId < flagCount; flagId++)
            {
                if (!_carryFlagGame.TryGetFlagInfo(arena, flagId, out IFlagInfo? flagInfo))
                    continue;

                if (flagInfo.State == FlagState.None)
                {
                    SpawnFlag(arena, flagId, settings.SpawnCoordinates, settings.SpawnRadius, -1);
                }
            }
        }

        short ICarryFlagBehavior.GetPlayerKillTransferCount(Arena arena, Player killed, Player killer, ReadOnlySpan<short> flagIds)
        {
            return PlayerKill(arena, killed, killer, flagIds, false);
        }

        short ICarryFlagBehavior.PlayerKill(Arena arena, Player killed, Player killer, ReadOnlySpan<short> flagIds)
        {
            return PlayerKill(arena, killed, killer, flagIds, true);
        }

        private short PlayerKill(Arena arena, Player killed, Player killer, ReadOnlySpan<short> flagIds, bool modify)
        {
            if (arena == null
                || killed == null
                || killer == null
                || flagIds.Length <= 0)
            {
                return 0;
            }

            var settings = _carryFlagGame.GetSettings(arena);
            if (settings == null)
                return 0;

            // The transfer count (which will be sent in the S2C Kill packet) notifies clients of the transfer of flags.
            // Clients interpret a kill as meaning:
            // - The killed player lost all carried flags.
            // - The transfer count gets added to the killer's carry flag count.
            // No additional packet send is required for the transferred flags, but any flags that the killed player
            // was carrying that are not transferred require action (e.g. drop, neut, or re-spawn).

            short maxCanCarry = settings.CarryFlags == ConfigCarryFlags.Yes
                ? (short)CarryFlags.MaxFlags
                : (short)(settings.CarryFlags - 1);

            short transferCount = 0;

            bool isTeamKill = killed.Freq == killer.Freq;
            Span<short> teamKillFlagIds = stackalloc short[flagIds.Length];
            int teamKillFlagCount = 0;

            Span<short> cantCarryFlagIds = stackalloc short[flagIds.Length];
            int cantCarryFlagCount = 0;

            foreach (short flagId in flagIds)
            {
                // TODO: ASSS doesn't allow fake players to get flags. That makes sense for autoturrets, but wouldn't it break replays of a flag game (record module)? For now ignoring fake player logic.
                //if (killer.Type == ClientType.Fake)
                //{
                //}

                if (isTeamKill && !settings.FriendlyTransfer)
                {
                    // It was a teamkill and friendly transfer is not allowed.
                    teamKillFlagIds[teamKillFlagCount++] = flagId;
                }
                else if (killer.Packet.FlagsCarried < maxCanCarry)
                {
                    if (modify)
                    {
                        // Transfer the flag to the killer.
                        _carryFlagGame.TrySetFlagCarried(arena, flagId, killer, FlagPickupReason.Kill); // don't send a FlagPickup packet, the transferCount will take care of it
                    }

                    transferCount++;
                }
                else
                {
                    // killer is already holding the max # of flags
                    cantCarryFlagIds[cantCarryFlagCount++] = flagId;
                }
            }

            if (modify)
            {
                if (teamKillFlagCount > 0)
                {
                    PlaceFlags(settings.TeamKillOwned, settings.TeamKillCenter, teamKillFlagIds[..teamKillFlagCount]);
                }

                if (cantCarryFlagCount > 0)
                {
                    PlaceFlags(settings.DropOwned, settings.DropCenter, cantCarryFlagIds[..cantCarryFlagCount]);
                }
            }

            return transferCount;

            // local function for placing flags
            void PlaceFlags(bool owned, bool center, ReadOnlySpan<short> flagIds)
            {
                short freq = owned ? killed.Freq : (short)-1;

                if (center)
                {
                    SpawnFlags(arena, flagIds, freq);
                }
                else
                {
                    TileCoordinates coordinates = new((short)(killed.Position.X >> 4), (short)(killed.Position.Y >> 4));
                    DropFlags(arena, flagIds, coordinates, freq);
                }
            }
        }

        void ICarryFlagBehavior.TouchFlag(Arena arena, Player player, short flagId)
        {
            var settings = _carryFlagGame.GetSettings(arena);
            if (settings == null)
                return;

            short maxCanCarry = settings.CarryFlags == ConfigCarryFlags.Yes
                ? (short)CarryFlags.MaxFlags
                : (short)(settings.CarryFlags - 1);

            if (player.Packet.FlagsCarried < maxCanCarry)
            {
                _carryFlagGame.TrySetFlagCarried(arena, flagId, player, FlagPickupReason.Pickup);
            }
        }

        void ICarryFlagBehavior.AdjustFlags(Arena arena, ReadOnlySpan<short> flagIds, AdjustFlagReason reason, Player oldCarrier, short oldFreq)
        {
            var settings = _carryFlagGame.GetSettings(arena);
            if (settings == null)
                return;

            switch (reason)
            {
                case AdjustFlagReason.Dropped:
                    PlaceFlags(settings.DropOwned, settings.DropCenter, flagIds);
                    break;

                case AdjustFlagReason.InSafe:
                    PlaceFlags(settings.SafeOwned, settings.SafeCenter, flagIds);
                    break;

                case AdjustFlagReason.ShipChange:
                case AdjustFlagReason.FreqChange:
                case AdjustFlagReason.LeftArena:
                default:
                    PlaceFlags(settings.NeutOwned, settings.NeutCenter, flagIds);
                    break;
            }

            void PlaceFlags(bool owned, bool center, ReadOnlySpan<short> flagIds)
            {
                short freq = owned ? oldFreq : (short)-1;

                if (center)
                {
                    SpawnFlags(arena, flagIds, freq);
                }
                else
                {
                    TileCoordinates coordinates = new((short)(oldCarrier.Position.X >> 4), (short)(oldCarrier.Position.Y >> 4));
                    DropFlags(arena, flagIds, coordinates, freq);
                }
            }
        }

        #endregion

        private void DropFlags(Arena arena, ReadOnlySpan<short> flagIds, TileCoordinates coordinates, short ownerFreq)
        {
            if (arena == null)
                return;

            if (flagIds.Length == 0)
                return;

            var settings = _carryFlagGame.GetSettings(arena);
            if (settings == null)
                return;

            List<TileCoordinates> available = _tileCoordinatesListPool.Get();

            try
            {
                GetAvailableFlagDropCoordinates(arena, flagIds.Length, settings.DropRadius, coordinates, available);

                // Randomize the available coordinates (Fisher–Yates shuffle)
                for (int i = available.Count - 1; i > 0; i--)
                {
                    int randomIndex = _prng.Number(0, i);
                    if (randomIndex != i)
                    {
                        // swap
                        (available[randomIndex], available[i]) = (available[i], available[randomIndex]);
                    }
                }

                int dropCount = Math.Min(flagIds.Length, available.Count);

                for (int i = 0; i < flagIds.Length; i++)
                {
                    short flagId = flagIds[i];
                    if (!_carryFlagGame.TryGetFlagInfo(arena, flagId, out IFlagInfo? flagInfo))
                        continue;

                    if (i < dropCount)
                    {
                        TileCoordinates dropCoordinates = available[i];

                        _carryFlagGame.TrySetFlagOnMap(arena, flagId, dropCoordinates, ownerFreq);

                        _logManager.LogA(LogLevel.Info, nameof(CarryFlags), arena, $"Set flag {flagId} location to ({dropCoordinates.X},{dropCoordinates.Y}).");
                    }
                    else
                    {
                        // Unable to get a location to drop the flag at.
                        _logManager.LogA(LogLevel.Warn, nameof(CarryFlags), arena, $"Unable to find a location to drop flag {flagId}.");

                        // Spawn it in the center instead.
                        SpawnFlag(arena, flagId, settings.SpawnCoordinates, settings.SpawnRadius, ownerFreq);
                    }
                }
            }
            finally
            {
                _tileCoordinatesListPool.Return(available);
            }
        }

        private void GetAvailableFlagDropCoordinates(Arena arena, int needed, int desiredWalkDistance, TileCoordinates startCoordinates, List<TileCoordinates> available)
        {
            HashSet<TileCoordinates> all = _tileCoordinatesHashSetPool.Get();
            HashSet<TileCoordinates> check = _tileCoordinatesHashSetPool.Get();
            HashSet<TileCoordinates> next = _tileCoordinatesHashSetPool.Get();

            try
            {
                all.Add(startCoordinates);

                if (IsAvailableToPlaceFlag(arena, startCoordinates))
                    available.Add(startCoordinates);

                next.Add(startCoordinates);

                while (needed > available.Count || desiredWalkDistance-- > 0)
                {
                    // Swap
                    (next, check) = (check, next);

                    WalkToNextAvailableCoordinate(arena, all, check, next);

                    if (next.Count == 0)
                        break; // there was no where left to walk

                    foreach (TileCoordinates candidate in next)
                    {
                        if (IsAvailableToPlaceFlag(arena, candidate))
                        {
                            available.Add(candidate);
                        }
                    }
                }
            }
            finally
            {
                _tileCoordinatesHashSetPool.Return(all);
                _tileCoordinatesHashSetPool.Return(check);
                _tileCoordinatesHashSetPool.Return(next);
            }
        }

        private void WalkToNextAvailableCoordinate(
            Arena arena,
            HashSet<TileCoordinates> all,
            HashSet<TileCoordinates> check,
            HashSet<TileCoordinates> next)
        {
            ArgumentNullException.ThrowIfNull(all);
            ArgumentNullException.ThrowIfNull(check);
            ArgumentNullException.ThrowIfNull(next);

            if (check.Count == 0)
                throw new ArgumentException("At least one coordinate is required", nameof(check));

            if (next.Count != 0)
                next.Clear();

            foreach (TileCoordinates coordinates in check)
            {
                if (coordinates.X > 0)
                    CheckCoordinate(new TileCoordinates((short)(coordinates.X - 1), coordinates.Y));

                if (coordinates.Y > 0)
                    CheckCoordinate(new TileCoordinates(coordinates.X, (short)(coordinates.Y - 1)));

                if (coordinates.X < 1023)
                    CheckCoordinate(new TileCoordinates((short)(coordinates.X + 1), coordinates.Y));

                if (coordinates.Y < 1023)
                    CheckCoordinate(new TileCoordinates(coordinates.X, (short)(coordinates.Y + 1)));
            }

            void CheckCoordinate(TileCoordinates toCheck)
            {
                if (!all.Add(toCheck))
                    return;

                // Only places that a ship (normal size) will fit through.
                if (IsFlyThrough(arena, toCheck) && !IsSingleWide(arena, toCheck))
                    next.Add(toCheck);
            }
        }

        private bool IsSingleWide(Arena arena, TileCoordinates coordinates)
        {
            bool top = coordinates.Y == 0 || !IsFlyThrough(arena, coordinates with { Y = (short)(coordinates.Y - 1) });
            bool bottom = coordinates.Y == 1023 || !IsFlyThrough(arena, coordinates with { Y = (short)(coordinates.Y + 1) });
            if (top && bottom)
                return true;

            bool left = coordinates.X == 0 || !IsFlyThrough(arena, coordinates with { X = (short)(coordinates.X - 1) });
            bool right = coordinates.X == 1023 || !IsFlyThrough(arena, coordinates with { X = (short)(coordinates.X + 1) });
            if (left && right)
                return true;

            return false;
        }

        private bool IsFlyThrough(Arena arena, TileCoordinates coordinates)
        {
            MapTile tile = _mapData.GetTile(arena, coordinates);
            return tile == MapTile.None || IsFlyThrough(tile);
        }

        private static bool IsFlyThrough(MapTile mapTile)
        {
            return mapTile.IsDoor || mapTile.IsSafe || mapTile.IsGoal || mapTile.IsFlyOver || mapTile.IsFlyUnder || mapTile.IsBrick || mapTile.IsFlag;
        }

        private bool IsAvailableToPlaceFlag(Arena arena, TileCoordinates coordinates)
        {
            // is an empty tile
            MapTile tile = _mapData.GetTile(arena, coordinates);
            if (tile != MapTile.None)
                return false;

            // not occupied by another flag
            for (short flagId = 0; flagId < _carryFlagGame.GetFlagCount(arena); flagId++)
            {
                if (!_carryFlagGame.TryGetFlagInfo(arena, flagId, out IFlagInfo? flagInfo))
                    continue;

                if (flagInfo.Location == coordinates)
                    return false;
            }

            // not in a "no flag drop" region
            foreach (MapRegion region in _mapData.RegionsAt(arena, coordinates))
                if (region.NoFlagDrops)
                    return false;

            return true;
        }

        private void SpawnFlags(Arena arena, ReadOnlySpan<short> flagIds, short ownerFreq)
        {
            var settings = _carryFlagGame.GetSettings(arena);
            if (settings == null)
                return;

            foreach (short flagId in flagIds)
            {
                SpawnFlag(arena, flagId, settings.SpawnCoordinates, settings.SpawnRadius, ownerFreq);
            }
        }

        private void SpawnFlag(Arena arena, short flagId, TileCoordinates coordinates, int radius, short ownerFreq)
        {
            TileCoordinates? location = null;
            int tries = 0;

            while (location == null && tries < 30)
            {
                // Pick a random point.
                (short x, short y) = CircularRandom(coordinates, radius + tries);

                if (_mapData.TryFindEmptyTileNear(arena, ref x, ref y)) // Move off any tiles.
                {
                    location = new TileCoordinates(x, y);
                    if (!IsAvailableToPlaceFlag(arena, location.Value)) // Check if a flag can be dropped there.
                        location = null;
                }

                tries++;
            }

            if (location != null)
            {
                _carryFlagGame.TrySetFlagOnMap(arena, flagId, location.Value, ownerFreq);
            }
            else
            {
                _logManager.LogA(LogLevel.Warn, nameof(CarryFlags), arena, $"Unable to find a location to spawn flag {flagId} at {coordinates} with radius {radius}.");
            }
        }

        private TileCoordinates CircularRandom(TileCoordinates coordinates, int radius)
        {
            double r = Math.Sqrt(_prng.Uniform()) * radius;

            double cx = coordinates.X + .5;
            double cy = coordinates.Y + .5;
            double theta = _prng.Uniform() * 2 * Math.PI;

            return new TileCoordinates(
                Wrap((short)(cx + r * Math.Cos(theta))),
                Wrap((short)(cy + r * Math.Sin(theta))));
        }

        private static short Wrap(short value, short bottom = 0, short top = 1023)
        {
            while (true)
            {
                if (value < bottom)
                    value = (short)(2 * bottom - value);
                else if (value > top)
                    value = (short)(2 * top - value);
                else
                    return value;
            }
        }
    }
}
