namespace SS.Core.Map
{
    /// <summary>
    /// Represents a single tile on a map (lvl file).
    /// </summary>
    public readonly struct MapTile
    {
        private readonly byte _tile;

        public MapTile(byte tile)
        {
            _tile = tile;
        }

        public static implicit operator byte(MapTile tile)
        {
            return tile._tile;
        }

        public static readonly MapTile None = new MapTile(0);
        public static readonly MapTile TileStart = new MapTile(1);
        public static readonly MapTile TileEnd = new MapTile(160);

        public bool IsBorder
        {
            get { return _tile == 20; }
        }

        public bool IsVerticalDoor
        {
            get { return _tile >= 162 && _tile <= 165; }
        }

        public bool IsHorizontalDoor
        {
            get { return _tile >= 166 && _tile <= 169; }
        }

        public bool IsDoor
        {
            get { return IsVerticalDoor || IsHorizontalDoor; }
        }

        public bool IsTurfFlag
        {
            get { return _tile == 170; }
        }

        public bool IsSafe
        {
            get { return _tile == 171; }
        }

        public bool IsGoal
        {
            get { return _tile == 172; }
        }

        public bool IsFlyOver
        {
            get { return _tile >= 173 && _tile <= 175; }
        }

        public bool IsFlyUnder
        {
            get { return _tile >= 176 && _tile <= 190; }
        }

        public bool IsTinyAsteroid
        {
            get { return _tile == 216; }
        }

        public bool IsBigAsteroid
        {
            get { return _tile == 217; }
        }

        public bool IsTinyAsteroid2
        {
            get { return _tile == 218; }
        }

        public bool IsStation
        {
            get { return _tile == 219; }
        }

        public bool IsWormhole
        {
            get { return _tile == 220; }
        }

        public int TileSize
        {
            get
            {
                if (IsBigAsteroid)
                    return 2;
                else if (IsStation)
                    return 6;
                else if (IsWormhole)
                    return 5;
                else
                    return 1;
            }
        }

        /// <summary>
        /// internal tile
        /// </summary>
        public bool IsBrick
        {
            get { return _tile == 250; }
        }
    }
}
