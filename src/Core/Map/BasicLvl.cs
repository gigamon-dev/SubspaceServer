using SkiaSharp;
using SS.Utilities.Collections;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
        /// Gets the coordinate of a flag by the <paramref name="flagId"/>.
        /// </summary>
        /// <param name="flagId">Id of the flag.</param>
        /// <param name="coordinate">When this method returns, contains the coordinate of the specified flag, if found; otherwise, the default value.</param>
        /// <returns><see langword="true"/> if the flag was found. Otherwise, <see langword="false"/>.</returns>
        public bool TryGetFlagCoordinate(short flagId, out MapCoordinate coordinate)
        {
            if (flagId < _flagCoordinateList.Count)
            {
                coordinate = _flagCoordinateList[flagId];
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

            // order the flags, allowing them to be accessed by index (flag id)
            _flagCoordinateList.Sort();

            if (_flagCoordinateList.Count > MaxFlags)
            {
                AddError($"Too many flags ({_flagCoordinateList.Count}/{MaxFlags}).");
            }

            IsTileDataLoaded = true;
        }

        // Note: It seems SkiaSharp only supports encoding to jpg, png, and webp even though it has many other image formats defined.
        private static readonly Trie<SKEncodedImageFormat> _extensionToImageFormatTrie = new(false)
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
            if (!_extensionToImageFormatTrie.TryGetValue(extension, out SKEncodedImageFormat format))
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

			if (!_extensionToImageFormatTrie.TryGetValue(imageFormat, out SKEncodedImageFormat format))
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

			foreach (KeyValuePair<MapCoordinate, MapTile> kvp in _tileLookup)
			{
				SKColor color = kvp.Value switch
				{
					{ IsDoor: true } => SKColors.Blue,
					{ IsSafe: true } => SKColors.LightGreen,
					{ IsTurfFlag: true } => SKColors.Yellow,
					{ IsGoal: true } => SKColors.Red,
					{ IsWormhole: true } => SKColors.Purple,
					{ IsFlyOver: true } => SKColors.DarkGray,
					{ IsFlyUnder: true } => SKColors.DarkGray,
					_ => SKColors.White
				};

				canvas.DrawPoint(kvp.Key.X, kvp.Key.Y, color);
			}

            return bitmap;
		}
	}
}
