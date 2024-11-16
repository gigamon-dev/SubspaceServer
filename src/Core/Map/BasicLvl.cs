using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.MemoryMappedFiles;

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

        private readonly Dictionary<TileCoordinates, MapTile> _tiles = new(65536);
        private readonly List<TileCoordinates> _flagCoordinatesList = new(MaxFlags);
        private readonly List<string> _errorList = [];
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
            get { return _tiles.Count; }
        }

        /// <summary>
        /// Gets the # of turf style flags on the map.
        /// </summary>
        public int FlagCount
        {
            get { return _flagCoordinatesList.Count; }
        }

        /// <summary>
        /// Gets the coordinate of a flag by the <paramref name="flagId"/>.
        /// </summary>
        /// <param name="flagId">Id of the flag.</param>
        /// <param name="coordinates">When this method returns, contains the coordinate of the specified flag, if found; otherwise, the default value.</param>
        /// <returns><see langword="true"/> if the flag was found. Otherwise, <see langword="false"/>.</returns>
        public bool TryGetFlagCoordinate(short flagId, out TileCoordinates coordinates)
        {
            if (flagId < _flagCoordinatesList.Count)
            {
                coordinates = _flagCoordinatesList[flagId];
                return true;
            }

            coordinates = default;
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
            _tiles.Clear();
            _flagCoordinatesList.Clear();
            _errorList.Clear();
            IsTileDataLoaded = false;
        }

        protected virtual void TrimExcess()
        {
            _tiles.TrimExcess();
            _flagCoordinatesList.TrimExcess();
            _errorList.TrimExcess();
        }

        /// <summary>
        /// Initializes the map for a fallback, in the scenario map loading failed.
        /// </summary>
        protected void SetAsEmergencyMap()
        {
            ClearLevel();

            _tiles[new TileCoordinates(0, 0)] = new MapTile(1);
            IsTileDataLoaded = true;
            TrimExcess();
        }

        public bool TryGetTile(TileCoordinates coordinates, out MapTile tile)
        {
            return _tiles.TryGetValue(coordinates, out tile);
        }

        /// <summary>
        /// Reads tile data from a memory mapped file.
        /// </summary>
        /// <param name="accessor">The accessor to the memory mapped file.</param>
        /// <param name="position">The position to start reading from.</param>
        /// <param name="length">The maximum number of bytes to read.</param>
        protected void ReadPlainTileData(MemoryMappedViewAccessor accessor, long position, long length)
        {
            ArgumentNullException.ThrowIfNull(accessor);

            while (length >= MapTileData.Length)
            {
                accessor.Read(position, out MapTileData td);

                if (td.X < 1024 && td.Y < 1024 && td.Type != MapTile.None)
                {
                    TileCoordinates coordinates = new(td.X, td.Y);
                    MapTile tile = new(td.Type);

                    if (tile.IsFlag)
                    {
                        _flagCoordinatesList.Add(coordinates);
                    }

                    int tileSize = tile.TileSize;
                    if (tileSize == 1)
                    {
                        _tiles.Add(coordinates, tile);
                    }
                    else if (tileSize > 1)
                    {
                        for (short x = 0; x < tileSize; x++)
                            for (short y = 0; y < tileSize; y++)
                                _tiles[new TileCoordinates((short)(coordinates.X + x), (short)(coordinates.Y + y))] = tile;
                    }
                    else
                    {
                        AddError($"Bad tile size ({tileSize}) for coordinate {coordinates} of type {td.Type}.");
                    }
                }
                else
                {
                    AddError($"Bad tile coordinate ({td.X},{td.Y}) of type {td.Type}.");
                }

                position += MapTileData.Length;
                length -= MapTileData.Length;
            }

            // order the flags, allowing them to be accessed by index (flag id)
            _flagCoordinatesList.Sort();

            if (_flagCoordinatesList.Count > MaxFlags)
            {
                AddError($"Too many flags ({_flagCoordinatesList.Count}/{MaxFlags}).");
            }

            IsTileDataLoaded = true;
        }

        // Note: It seems SkiaSharp only supports encoding to jpg, png, and webp even though it has many other image formats defined.
        private static readonly Dictionary<string, SKEncodedImageFormat> _extensionToImageFormat = new(StringComparer.OrdinalIgnoreCase)
        {
			//{ ".bmp", SKEncodedImageFormat.Bmp },
			//{ "bmp", SKEncodedImageFormat.Bmp },
			//{ ".gif", SKEncodedImageFormat.Gif },
			//{ "gif", SKEncodedImageFormat.Gif },
			//{ ".ico", SKEncodedImageFormat.Ico},
			//{ "ico", SKEncodedImageFormat.Ico},
			{ ".jpg", SKEncodedImageFormat.Jpeg },
            { "jpg", SKEncodedImageFormat.Jpeg },
            { ".jpeg", SKEncodedImageFormat.Jpeg },
            { "jpeg", SKEncodedImageFormat.Jpeg },
            { ".png", SKEncodedImageFormat.Png },
            { "png", SKEncodedImageFormat.Png },
            { ".webp", SKEncodedImageFormat.Webp },
            { "webp", SKEncodedImageFormat.Webp },
			//{ ".heif", SKEncodedImageFormat.Heif },
			//{ "heif", SKEncodedImageFormat.Heif },
		};

        private static readonly Dictionary<string, SKEncodedImageFormat>.AlternateLookup<ReadOnlySpan<char>> _extensionToImageFormatLookup = _extensionToImageFormat.GetAlternateLookup<ReadOnlySpan<char>>();

        /// <summary>
        /// Creates an image of the map, saving it to a specified <paramref name="path"/>.
        /// </summary>
        /// <param name="path">The path to save the file to. The image format is automatically determined based on the filename extension.</param>
        /// <exception cref="ArgumentException">The <paramref name="path"/> is null or white-space.</exception>
        /// <exception cref="ArgumentException">The <paramref name="path"/> file extension specifies an unsupported image format.</exception>
        /// <exception cref="Exception">Error encoding image.</exception>
        public void SaveImage(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string extension = Path.GetExtension(path);
            if (!_extensionToImageFormat.TryGetValue(extension, out SKEncodedImageFormat format))
                throw new ArgumentException("Unsupported image format.", nameof(path));

            using SKBitmap bitmap = CreateBitmap();

            bool success = false;

            using (FileStream fs = new(path, FileMode.CreateNew))
            {
                success = bitmap.Encode(fs, format, 100);
            }

            if (!success)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }

                throw new Exception($"Error encoding as {format}.");
            }
        }

        /// <summary>
        /// Creates an image of the map, saving it to a specified <paramref name="path"/>.
        /// </summary>
        /// <param name="imageFormat">The format to save the image as.</param>
        /// <exception cref="ArgumentException">The <paramref name="imageFormat"/> is white-space.</exception>
        /// <exception cref="ArgumentException">Unsupported image format for the provided <paramref name="imageFormat"/>.</exception>
        /// <exception cref="Exception">Error encoding image.</exception>
        public void SaveImage(Stream stream, ReadOnlySpan<char> imageFormat)
        {
            ArgumentNullException.ThrowIfNull(stream);

            if (imageFormat.IsWhiteSpace())
                throw new ArgumentException("Cannot be whitespace.", nameof(imageFormat));

            if (!_extensionToImageFormatLookup.TryGetValue(imageFormat, out SKEncodedImageFormat format))
                throw new ArgumentException("Unsupported image format.", nameof(imageFormat));

            using SKBitmap bitmap = CreateBitmap();

            if (!bitmap.Encode(stream, format, 100))
                throw new Exception($"Error encoding as {format}.");
        }

        private SKBitmap CreateBitmap()
        {
            SKImageInfo info = new(1024, 1024);
            SKBitmap bitmap = new(info);

            using SKCanvas canvas = new(bitmap);
            canvas.Clear(SKColors.Black);

            foreach ((TileCoordinates coordinates, MapTile tile) in _tiles)
            {
                SKColor color = tile switch
                {
                    { IsDoor: true } => SKColors.Blue,
                    { IsSafe: true } => SKColors.LightGreen,
                    { IsFlag: true } => SKColors.Yellow,
                    { IsGoal: true } => SKColors.Red,
                    { IsWormhole: true } => SKColors.Purple,
                    { IsFlyOver: true } => SKColors.DarkGray,
                    { IsFlyUnder: true } => SKColors.DarkGray,
                    _ => SKColors.White
                };

                canvas.DrawPoint(coordinates.X, coordinates.Y, color);
            }

            return bitmap;
        }
    }
}
