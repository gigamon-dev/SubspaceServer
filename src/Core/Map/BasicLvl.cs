using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Drawing;

namespace SS.Core.Map
{
    /// <summary>
    /// For reading the basic SubSpace map format which contains tile information and optionally a tileset (bitmap).
    /// </summary>
    public class BasicLvl
    {
        /// <summary>
        /// maximum # of flags a map is allowed to contain
        /// </summary>
        public const int MaxFlags = 255;

        private readonly object _mtx = new object();

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

        public void Lock()
        {
            Monitor.Enter(_mtx);
        }

        public void Unlock()
        {
            Monitor.Exit(_mtx);
        }

        public virtual void ClearLevel()
        {
            lock (_mtx)
            {
                _tileLookup.Clear();
                _flagCoordinateList.Clear();
                _errorCount = 0;
            }
        }

        public void SetAsEmergencyMap()
        {
            lock (_mtx)
            {
                ClearLevel();

                _tileLookup.Add(0, 0, new MapTile(1));
            }
        }

        public virtual bool LoadFromFile(string lvlname)
        {
            throw new Exception("not implemented, use ExtendedLvl for now");
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
                    MapTile { IsDoor : true} => Color.Blue,
                    MapTile { IsSafe: true } => Color.LightGreen,
                    MapTile { IsTurfFlag: true } => Color.Yellow,
                    MapTile { IsGoal : true } => Color.Red,
                    MapTile { IsWormhole : true } => Color.Purple,
                    MapTile { IsFlyOver: true } => Color.DarkGray,
                    MapTile { IsFlyUnder: true } => Color.DarkGray,
                    _ => Color.White
                };

                bitmap.SetPixel(kvp.Key.X, kvp.Key.Y, color);
            }

            return bitmap;
        }
    }
}
