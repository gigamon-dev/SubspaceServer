using System;
using System.Collections.Generic;
using System.Drawing;

namespace SS.Core.Map
{
    /// <summary>
    /// For reading the basic SubSpace map format which contains tile information and optionally a tileset (bitmap).
    /// </summary>
    public abstract class BasicLvl
    {
        /// <summary>
        /// maximum # of flags a map is allowed to contain
        /// </summary>
        public const int MaxFlags = 255;

        private readonly MapTileCollection _tileLookup = new MapTileCollection();
        private readonly List<MapCoordinate> _flagCoordinateList = new List<MapCoordinate>(MaxFlags);
        private int _errorCount;

        public int TileCount
        {
            get { return _tileLookup.Count; }
        }

        public int FlagCount
        {
            get { return _flagCoordinateList.Count; }
        }

        public int ErrorCount
        {
            get { return _errorCount; }
        }

        protected virtual void ClearLevel()
        {
            _tileLookup.Clear();
            _flagCoordinateList.Clear();
            _errorCount = 0;
        }

        protected void SetAsEmergencyMap()
        {
            ClearLevel();

            _tileLookup.Add(0, 0, new MapTile(1));
        }

        public bool TryGetTile(MapCoordinate coord, out MapTile tile)
        {
            return _tileLookup.TryGetValue(coord, out tile);
        }

        protected void ReadPlainTileData(ArraySegment<byte> arraySegment)
        {
            int offset = arraySegment.Offset;
            int endOffset = arraySegment.Offset + arraySegment.Count;

            while ((offset + MapTileData.Length) <= endOffset)
            {
                MapTileData tileData = new MapTileData(arraySegment.Array, offset);

                uint x = tileData.X;
                uint y = tileData.Y;
                uint type = tileData.Type;

                if (x < 1024 && y < 1024)
                {
                    MapCoordinate coord = new MapCoordinate((short)x, (short)y);
                    MapTile tile = new MapTile((byte)type);

                    if (tile.IsTurfFlag)
                    {
                        _flagCoordinateList.Add(coord);
                    }

                    int tileSize = tile.TileSize;
                    if (tileSize == 1)
                    {
                        _tileLookup.Add(coord, tile);
                    }
                    else if (tileSize > 1)
                    {
                        for (short xPos = 0; xPos < tileSize; xPos++)
                            for (short yPos = 0; yPos < tileSize; yPos++)
                                _tileLookup.Add((short)(coord.X + xPos), (short)(coord.Y + yPos), tile);
                    }
                    else
                        _errorCount++;
                }
                else
                    _errorCount++;

                offset += MapTileData.Length;
            }

            // order the flags, this is how they become indexed
            _flagCoordinateList.Sort();
        }

        /// <summary>
        /// Get a bitmap object representing the map tiles.
        /// Note: remember to Dispose() the bitmap.
        /// </summary>
        /// <returns></returns>
        public Bitmap ToBitmap()
        {
            Bitmap bitmap = new Bitmap(1024, 1024);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Black);
            }

            foreach (KeyValuePair<MapCoordinate, MapTile> kvp in _tileLookup)
            {
                Color color = kvp.Value switch {
                    { IsDoor : true} => Color.Blue,
                    { IsSafe: true } => Color.LightGreen,
                    { IsTurfFlag: true } => Color.Yellow,
                    { IsGoal : true } => Color.Red,
                    { IsWormhole : true } => Color.Purple,
                    { IsFlyOver: true } => Color.DarkGray,
                    { IsFlyUnder: true } => Color.DarkGray,
                    _ => Color.White
                };

                bitmap.SetPixel(kvp.Key.X, kvp.Key.Y, color);
            }

            return bitmap;
        }
    }
}
