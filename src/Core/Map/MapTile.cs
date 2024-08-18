namespace SS.Core.Map
{
    /// <summary>
    /// Represents a single tile on a map (lvl file).
    /// </summary>
    public readonly struct MapTile(byte tile)
    {
        private readonly byte _tile = tile;

        public static implicit operator byte(MapTile tile)
        {
            return tile._tile;
        }

        public static readonly MapTile None = new(0);
        public static readonly MapTile TileStart = new(1);
        public static readonly MapTile TileEnd = new(160);
        public static readonly MapTile Flag = new(170);
        public static readonly MapTile Brick = new(250);

        public bool IsBorder => _tile == 20;

        public bool IsVerticalDoor => _tile >= 162 && _tile <= 165;

        public bool IsHorizontalDoor => _tile >= 166 && _tile <= 169;

        public bool IsDoor => IsVerticalDoor || IsHorizontalDoor;

        public bool IsFlag => _tile == 170;

        public bool IsSafe => _tile == 171;

        public bool IsGoal => _tile == 172;

        public bool IsFlyOver => _tile >= 173 && _tile <= 175;

        public bool IsFlyUnder => _tile >= 176 && _tile <= 190;

        public bool IsTinyAsteroid => _tile == 216;

        public bool IsBigAsteroid => _tile == 217;

        public bool IsTinyAsteroid2 => _tile == 218;

        public bool IsStation => _tile == 219;

        public bool IsWormhole => _tile == 220;

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
        public bool IsBrick => _tile == 250;
    }
}
