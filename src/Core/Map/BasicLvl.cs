using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

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
        private readonly List<string> _errorList = new List<string>();
        private readonly ReadOnlyCollection<string> _readOnlyErrors;

        protected BasicLvl()
        {
            _readOnlyErrors = new ReadOnlyCollection<string>(_errorList);
        }

        protected bool IsTileDataLoaded
        {
            get;
            private set;
        } = false;

        /// <summary>
        /// Gets the total # of tiles on the map.
        /// </summary>
        public int TileCount
        {
            get { return _tileLookup.Count; }
        }

        /// <summary>
        /// Gets the # of turf style flags on the map.
        /// </summary>
        public int FlagCount
        {
            get { return _flagCoordinateList.Count; }
        }

        /// <summary>
        /// To get the coordinate of a flag by the FlagId.
        /// </summary>
        /// <param name="index">Id of the flag.</param>
        /// <param name="coordinate">When this method returns, contains the coordinate of the specified flag, if found; otherwise, the default value.</param>
        /// <returns><see langword="true"/> if the flag was found; otherwise, <see langword="false"/>.</returns>
        public bool TryGetFlagCoordinate(int index, out MapCoordinate coordinate)
        {
            if (index < _flagCoordinateList.Count)
            {
                coordinate = _flagCoordinateList[index];
                return true;
            }

            coordinate = default;
            return false;
        }

        /// <summary>
        /// Gets descriptive information about any errors detected when the map was loaded.
        /// </summary>
        public IReadOnlyList<string> Errors => _readOnlyErrors;

        /// <summary>
        /// Adds an error. For use when loading a map.
        /// </summary>
        /// <param name="message">A description of what was wrong.</param>
        protected void AddError(string message)
        {
            _errorList.Add(message);
        }

        /// <summary>
        /// Resets the map back to the original un-loaded state.
        /// </summary>
        protected virtual void ClearLevel()
        {
            _tileLookup.Clear();
            _flagCoordinateList.Clear();
            _errorList.Clear();
            IsTileDataLoaded = false;
        }

        protected virtual void TrimExcess()
        {
            _tileLookup.TrimExcess();
            _flagCoordinateList.TrimExcess();
            _errorList.TrimExcess();
        }

        /// <summary>
        /// Initializes the map for a fallback, in the scenario map loading failed.
        /// </summary>
        protected void SetAsEmergencyMap()
        {
            ClearLevel();

            _tileLookup.Add(0, 0, new MapTile(1));
            IsTileDataLoaded = true;
            TrimExcess();
        }

        public bool TryGetTile(MapCoordinate coord, out MapTile tile)
        {
            return _tileLookup.TryGetValue(coord, out tile);
        }

        /// <summary>
        /// Reads tile data from a memory mapped file.
        /// </summary>
        /// <param name="accessor">The accessor to the memory mapped file.</param>
        /// <param name="position">The position to start reading from.</param>
        /// <param name="length">The maximum number of bytes to read.</param>
        protected void ReadPlainTileData(MemoryMappedViewAccessor accessor, long position, long length)
        {
            if (accessor == null)
                throw new ArgumentNullException(nameof(accessor));

            int mapTileDataLength = Marshal.SizeOf<MapTileData>();

            while (length >= mapTileDataLength)
            {
                accessor.Read(position, out MapTileData td);

                if (td.X < 1024 && td.Y < 1024 && td.Type != MapTile.None)
                {
                    MapCoordinate coord = new MapCoordinate(td.X, td.Y);
                    MapTile tile = new MapTile(td.Type);

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
                        for (short x = 0; x < tileSize; x++)
                            for (short y = 0; y < tileSize; y++)
                                _tileLookup.Add((short)(coord.X + x), (short)(coord.Y + y), tile);
                    }
                    else
                    {
                        AddError($"Bad tile size ({tileSize}) for coordinate ({td.X},{td.Y}) of type {td.Type}.");
                    }
                }
                else
                {
                    AddError($"Bad tile coordinate ({td.X},{td.Y}) of type {td.Type}.");
                }

                position += mapTileDataLength;
                length -= mapTileDataLength;
            }

            // order the flags, allowing them to be accessed by index
            // TODO: verify the comparison, can't test yet since the flagcore module is not done
            _flagCoordinateList.Sort();

            if (_flagCoordinateList.Count > MaxFlags)
            {
                AddError($"Too many flags ({_flagCoordinateList.Count}/{MaxFlags}).");
            }

            IsTileDataLoaded = true;
        }

        /// <summary>
        /// Gets a bitmap representation of the map tiles.
        /// Note: Remember to Dispose() the bitmap.
        /// </summary>
        /// <returns>A bitmap object.</returns>
        public Bitmap ToBitmap()
        {
            Bitmap bitmap = new Bitmap(1024, 1024);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Black);
            }

            foreach (KeyValuePair<MapCoordinate, MapTile> kvp in _tileLookup)
            {
                Color color = kvp.Value switch
                {
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
