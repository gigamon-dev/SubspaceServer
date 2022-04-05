using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using System;
using System.Collections.Generic;

namespace SS.Core.Modules.FlagGame
{
    public class DefaultCarryFlagBehavior : ICarryFlagBehavior
    {
        private readonly ICarryFlagGame _carryFlagGame;
        private readonly ILogManager _logManager;
        private readonly IMapData _mapData;
        private readonly IPrng _prng;

        private static readonly DefaultObjectPool<HashSet<MapCoordinate>> _mapCoordinateHashSetPool = new(new MapCoordinateHashSetPooledObjectPolicy());
        private static readonly DefaultObjectPool<List<MapCoordinate>> _mapCoordinateListPool = new(new MapCoordinateListPooledObjectPolicy());

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
            short flagId = -1;
            while (flagId < numFlags)
            {
                if (!_carryFlagGame.TryAddFlag(arena, out flagId))
                    break;

                SpawnFlag(arena, flagId, settings.SpawnCoordinate, settings.SpawnRadius, -1);
            }

            // TODO: maybe each flag needs a DateTime on when it should be spawned and have a setting to control the delay?
            // or maybe just a delay for neuted flags? since there's already an optional delay for starting the game
        }

        short ICarryFlagBehavior.PlayerKill(Arena arena, Player killed, Player killer, ReadOnlySpan<short> flagIds)
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
                    // Transfer the flag to the killer.
                    _carryFlagGame.TrySetFlagCarried(arena, flagId, killer, FlagPickupReason.Kill); // don't send a FlagPickup packet, the transferCount will take care of it
                    transferCount++;
                }
                else
                {
                    // killer is already holding the max # of flags
                    cantCarryFlagIds[cantCarryFlagCount++] = flagId;
                }
            }

            if (teamKillFlagCount > 0)
            {
                PlaceFlags(settings.TeamKillOwned, settings.TeamKillCenter, teamKillFlagIds[..teamKillFlagCount]);
            }

            if (cantCarryFlagCount > 0)
            {
                PlaceFlags(settings.DropOwned, settings.DropCenter, cantCarryFlagIds[..cantCarryFlagCount]);
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
                    MapCoordinate coordinate = new((short)(killed.Position.X >> 4), (short)(killed.Position.Y >> 4));
                    DropFlags(arena, flagIds, coordinate, freq);
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
                    MapCoordinate coordinate = new((short)(oldCarrier.Position.X >> 4), (short)(oldCarrier.Position.Y >> 4));
                    DropFlags(arena, flagIds, coordinate, freq);
                }
            }
        }

        #endregion

        protected void DropFlags(Arena arena, ReadOnlySpan<short> flagIds, MapCoordinate coordinate, short ownerFreq)
        {
            if (arena == null)
                return;

            if (flagIds.Length == 0)
                return;

            var settings = _carryFlagGame.GetSettings(arena);
            if (settings == null)
                return;

            List<MapCoordinate> available = _mapCoordinateListPool.Get();

            try
            {
                GetAvailableFlagDropCoordinates(arena, flagIds.Length, settings.DropRadius, coordinate, available);

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
                    if (!_carryFlagGame.TryGetFlagInfo(arena, flagId, out IFlagInfo flagInfo))
                        continue;

                    if (i < dropCount)
                    {
                        MapCoordinate dropCoordinate = available[i];

                        _carryFlagGame.TrySetFlagOnMap(arena, flagId, dropCoordinate, ownerFreq);

                        _logManager.LogA(LogLevel.Info, nameof(CarryFlags), arena, $"Set flag {flagId} location to ({dropCoordinate.X},{dropCoordinate.Y}).");
                    }
                    else
                    {
                        // Unable to get a location to drop the flag at.
                        _logManager.LogA(LogLevel.Warn, nameof(CarryFlags), arena, $"Unable to find a location to drop flag {flagId}.");

                        // Spawn it in the center instead.
                        SpawnFlag(arena, flagId, settings.SpawnCoordinate, settings.SpawnRadius, ownerFreq);
                    }
                }
            }
            finally
            {
                _mapCoordinateListPool.Return(available);
            }
        }

        private void GetAvailableFlagDropCoordinates(Arena arena, int needed, int desiredWalkDistance, MapCoordinate startCoordinate, List<MapCoordinate> available)
        {
            HashSet<MapCoordinate> all = _mapCoordinateHashSetPool.Get();
            HashSet<MapCoordinate> check = _mapCoordinateHashSetPool.Get();
            HashSet<MapCoordinate> next = _mapCoordinateHashSetPool.Get();

            try
            {
                all.Add(startCoordinate);

                if (IsAvailableToPlaceFlag(arena, startCoordinate))
                    available.Add(startCoordinate);

                next.Add(startCoordinate);

                while (needed > available.Count || desiredWalkDistance-- > 0)
                {
                    // Swap
                    (next, check) = (check, next);

                    WalkToNextAvailableCoordinate(arena, all, check, next);

                    if (next.Count == 0)
                        break; // there was no where left to walk

                    foreach (MapCoordinate candidate in next)
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
                _mapCoordinateHashSetPool.Return(all);
                _mapCoordinateHashSetPool.Return(check);
                _mapCoordinateHashSetPool.Return(next);
            }
        }

        private void WalkToNextAvailableCoordinate(
            Arena arena,
            HashSet<MapCoordinate> all,
            HashSet<MapCoordinate> check,
            HashSet<MapCoordinate> next)
        {
            if (all == null)
                throw new ArgumentNullException(nameof(all));

            if (check == null)
                throw new ArgumentNullException(nameof(check));

            if (next == null)
                throw new ArgumentNullException(nameof(next));

            if (check.Count == 0)
                throw new ArgumentException("At least one coordinate is required", nameof(check));

            if (next.Count != 0)
                next.Clear();

            foreach (MapCoordinate coordinate in check)
            {
                if (coordinate.X > 0)
                    CheckCoordinate(new MapCoordinate((short)(coordinate.X - 1), coordinate.Y));

                if (coordinate.Y > 0)
                    CheckCoordinate(new MapCoordinate(coordinate.X, (short)(coordinate.Y - 1)));

                if (coordinate.X < 1023)
                    CheckCoordinate(new MapCoordinate((short)(coordinate.X + 1), coordinate.Y));

                if (coordinate.Y < 1023)
                    CheckCoordinate(new MapCoordinate(coordinate.X, (short)(coordinate.Y + 1)));
            }

            void CheckCoordinate(MapCoordinate toCheck)
            {
                if (!all.Add(toCheck))
                    return;

                // Only places that a ship (normal size) will fit through.
                if (IsFlyThrough(arena, toCheck) && !IsSingleWide(arena, toCheck))
                    next.Add(toCheck);
            }
        }

        private bool IsSingleWide(Arena arena, MapCoordinate coordinate)
        {
            bool top = coordinate.Y == 0 || !IsFlyThrough(arena, coordinate with { Y = (short)(coordinate.Y - 1) });
            bool bottom = coordinate.Y == 1023 || !IsFlyThrough(arena, coordinate with { Y = (short)(coordinate.Y + 1) });
            if (top && bottom)
                return true;

            bool left = coordinate.X == 0 || !IsFlyThrough(arena, coordinate with { X = (short)(coordinate.X - 1) });
            bool right = coordinate.X == 1023 || !IsFlyThrough(arena, coordinate with { X = (short)(coordinate.X + 1) });
            if (left && right)
                return true;

            return false;
        }

        private bool IsFlyThrough(Arena arena, MapCoordinate coordinate)
        {
            MapTile? tile = _mapData.GetTile(arena, coordinate);
            return tile == null || IsFlyThrough(tile.Value);
        }

        private static bool IsFlyThrough(MapTile mapTile)
        {
            return mapTile.IsDoor || mapTile.IsSafe || mapTile.IsGoal || mapTile.IsFlyOver || mapTile.IsFlyUnder || mapTile.IsBrick || mapTile.IsTurfFlag;
        }

        private bool IsAvailableToPlaceFlag(Arena arena, MapCoordinate coordinate)
        {
            // is an empty tile
            MapTile? tile = _mapData.GetTile(arena, coordinate);
            if (tile != null)
                return false;

            // not occupied by another flag
            for (short flagId = 0; flagId < _carryFlagGame.GetFlagCount(arena); flagId++)
            {
                if (!_carryFlagGame.TryGetFlagInfo(arena, flagId, out IFlagInfo flagInfo))
                    continue;

                if (flagInfo.Location == coordinate)
                    return false;
            }

            // not in a "no flag drop" region
            foreach (MapRegion region in _mapData.RegionsAt(arena, coordinate))
                if (region.NoFlagDrops)
                    return false;

            return true;
        }

        protected void SpawnFlags(Arena arena, ReadOnlySpan<short> flagIds, short ownerFreq)
        {
            var settings = _carryFlagGame.GetSettings(arena);
            if (settings == null)
                return;

            foreach (short flagId in flagIds)
            {
                SpawnFlag(arena, flagId, settings.SpawnCoordinate, settings.SpawnRadius, ownerFreq);
            }
        }

        protected void SpawnFlag(Arena arena, short flagId, MapCoordinate coordinate, int radius, short ownerFreq)
        {
            MapCoordinate? location = null;
            int tries = 0;

            while (location == null && tries < 30)
            {
                // Pick a random point.
                (short x, short y) = CircularRandom(coordinate, radius + tries);

                if (_mapData.TryFindEmptyTileNear(arena, ref x, ref y)) // Move off any tiles.
                {
                    location = new MapCoordinate(x, y);
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
                _logManager.LogA(LogLevel.Warn, nameof(CarryFlags), arena, $"Unable to find a location to spawn flag {flagId} at ({coordinate.X},{coordinate.Y}) with radius {radius}.");
            }
        }

        private MapCoordinate CircularRandom(MapCoordinate coordinate, int radius)
        {
            double r = Math.Sqrt(_prng.Uniform()) * radius;

            double cx = coordinate.X + .5;
            double cy = coordinate.Y + .5;
            double theta = _prng.Uniform() * 2 * Math.PI;

            return new MapCoordinate(
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

        #region Helper types

        private class MapCoordinateHashSetPooledObjectPolicy : PooledObjectPolicy<HashSet<MapCoordinate>>
        {
            public override HashSet<MapCoordinate> Create()
            {
                return new HashSet<MapCoordinate>();
            }

            public override bool Return(HashSet<MapCoordinate> obj)
            {
                if (obj == null)
                    return false;

                obj.Clear();

                return true;
            }
        }

        private class MapCoordinateListPooledObjectPolicy : PooledObjectPolicy<List<MapCoordinate>>
        {
            public override List<MapCoordinate> Create()
            {
                return new List<MapCoordinate>();
            }

            public override bool Return(List<MapCoordinate> obj)
            {
                if (obj == null)
                    return false;

                obj.Clear();

                return true;
            }
        }

        #endregion
    }
}
