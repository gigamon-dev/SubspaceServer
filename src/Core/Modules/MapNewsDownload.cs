﻿using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.ObjectPool;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Threading;

namespace SS.Core.Modules
{
	/// <summary>
	/// Module that provides functionality to download map files (lvl and lvz) and the news.txt file.
	/// </summary>
	[CoreModuleInfo]
    public sealed class MapNewsDownload : IModule, IMapNewsDownload, IDisposable
    {
        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private INetwork _net;
        private ILogManager _logManager;
        private IConfigManager _configManager;
        private IMainloop _mainloop;
        private IArenaManager _arenaManager;
        private IMapData _mapData;
        private IObjectPoolManager _objectPoolManager;
		private InterfaceRegistrationToken<IMapNewsDownload> _iMapNewsDownloadToken;

        private ArenaDataKey<List<MapDownloadData>> _dlKey;

        /// <summary>
        /// Map that's used if the configured one cannot be read.
        /// </summary>
        /// <remarks>
        /// This includes the header (0x2A, with filename "tinymap.lvl") and data (last 12 bytes).
        /// </remarks>
        private readonly byte[] _emergencyMap = [
			0x2a, 0x74, 0x69, 0x6e, 0x79, 0x6d, 0x61, 0x70,
            0x2e, 0x6c, 0x76, 0x6c, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x78, 0x9c, 0x63, 0x60, 0x60, 0x60, 0x04,
            0x00, 0x00, 0x05, 0x00, 0x02
        ];

        /// <summary>
        /// manages news.txt
        /// </summary>
        private NewsManager _newsManager;

        // Cached delegates
        private readonly Action<Arena> _threadPoolWork_InitializeArena;
        private readonly GetSizedSendDataDelegate<NewsDownloadContext> _getNewsData;
		private readonly GetSizedSendDataDelegate<MapDownloadContext> _getMapData;

		public MapNewsDownload()
        {
            _threadPoolWork_InitializeArena = ThreadPoolWork_InitializeArena;
            _getNewsData = GetNewsData;
            _getMapData = GetMapData;
        }

        #region Module Members

        [ConfigHelp("General", "NewsFile", ConfigScope.Global, typeof(string), DefaultValue = "news.txt",
            Description = "The filename of the news file.")]
        public bool Load(
            ComponentBroker broker,
            IPlayerData playerData,
            INetwork net,
            ILogManager logManager,
            IConfigManager configManager,
            IMainloop mainloop,
            IArenaManager arenaManager,
            IMapData mapData,
            IObjectPoolManager objectPoolManager)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _dlKey = _arenaManager.AllocateArenaData(new ListPooledObjectPolicy<MapDownloadData>() { InitialCapacity = S2C_MapFilename.MaxFiles });

            _net.AddPacket(C2SPacketType.UpdateRequest, Packet_UpdateRequest);
            _net.AddPacket(C2SPacketType.MapRequest, Packet_MapNewsRequest);
            _net.AddPacket(C2SPacketType.NewsRequest, Packet_MapNewsRequest);

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);

            string newsFilename = _configManager.GetStr(_configManager.Global, "General", "NewsFile");
            if (string.IsNullOrWhiteSpace(newsFilename))
                newsFilename = "news.txt";

            _newsManager = new NewsManager(this, newsFilename);

            _iMapNewsDownloadToken = broker.RegisterInterface<IMapNewsDownload>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iMapNewsDownloadToken) != 0)
                return false;

            _newsManager.Dispose();

            _net.RemovePacket(C2SPacketType.UpdateRequest, Packet_UpdateRequest);
            _net.RemovePacket(C2SPacketType.MapRequest, Packet_MapNewsRequest);
            _net.RemovePacket(C2SPacketType.NewsRequest, Packet_MapNewsRequest);

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);

            _arenaManager.FreeArenaData(ref _dlKey);

            return true;
        }

        #endregion

        #region IMapNewsDownload Members

        void IMapNewsDownload.SendMapFilename(Player player)
        {
            if (player is null)
                return;

            Arena arena = player.Arena;
            if (arena is null)
                return;

            if (!arena.TryGetExtraData(_dlKey, out List<MapDownloadData> downloadList))
                return;

            if (downloadList.Count == 0)
            {
                _logManager.LogA(LogLevel.Warn, nameof(MapNewsDownload), arena, "Missing map data.");
                return;
            }

            S2C_MapFilename packet = new();

            int len = 0;

            // allow vie clients that specifically ask for them to get all the
            // lvz data, to support bots.
            if (player.Type == ClientType.Continuum || player.Flags.WantAllLvz)
            {
                int idx = 0;

                foreach (MapDownloadData data in downloadList)
                {
                    if (!data.IsOptional || player.Flags.WantAllLvz)
                    {
                        len = packet.SetFileInfo(idx, data.FileName, data.Checksum, (uint)data.Data.Length);
                        idx++;
                    }
                }
            }
            else
            {
                MapDownloadData data = downloadList[0]; // index 0 always has the lvl
                len = packet.SetFileInfo(data.FileName, data.Checksum);
            }

            if (len > 0)
            {
				_net.SendToOne(
                    player,
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1))[..len],
                    NetSendFlags.Reliable);
            }
        }

        uint IMapNewsDownload.GetNewsChecksum() => _newsManager.TryGetNews(out _, out uint checksum) ? checksum : 0;

        #endregion

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena is null)
                return;

            if (action == ArenaAction.Create)
            {
                // note: asss does this in reverse order, but i think it is a race condition
                _arenaManager.HoldArena(arena);
                _mainloop.QueueThreadPoolWorkItem(_threadPoolWork_InitializeArena, arena);
            }
            else if (action == ArenaAction.Destroy)
            {
                if (!arena.TryGetExtraData(_dlKey, out List<MapDownloadData> downloadList))
                    return;

                downloadList.Clear();
            }
        }

		private void ThreadPoolWork_InitializeArena(Arena arena)
		{
			if (arena is null)
				return;

			try
			{
				if (!arena.TryGetExtraData(_dlKey, out List<MapDownloadData> downloadList))
					return;

				downloadList.Clear();

				MapDownloadData data = null;

                // First, add the map itself
				string filePath = _mapData.GetMapFilename(arena, null);
				if (!string.IsNullOrEmpty(filePath))
				{
					data = CompressMap(filePath, true, false);
				}

				if (data is null)
				{
					_logManager.LogA(LogLevel.Warn, nameof(MapNewsDownload), arena, "Can't load level file, falling back to 'tinymap.lvl'.");
                    data = new MapDownloadData("tinymap.lvl", false, 0x5643ef8a, _emergencyMap);
				}

				downloadList.Add(data);

				// Next, add lvz files
				foreach (LvzFileInfo lvzInfo in _mapData.LvzFilenames(arena))
				{
					data = CompressMap(lvzInfo.Filename, false, lvzInfo.IsOptional);

					if (data is not null)
					{
						downloadList.Add(data);
					}
				}
			}
			finally
			{
				_arenaManager.UnholdArena(arena);
			}


			MapDownloadData CompressMap(string filePath, bool compress, bool isOptional)
			{
				if (string.IsNullOrWhiteSpace(filePath))
					throw new ArgumentException("Cannot be null or white-space.", nameof(filePath));

				try
				{
					string fileName = Path.GetFileName(filePath);

					if (string.IsNullOrWhiteSpace(fileName))
					{
						_logManager.LogM(LogLevel.Error, nameof(MapNewsDownload), $"Missing file name '{filePath}'.");
						return null;
					}

					if (StringUtils.DefaultEncoding.GetByteCount(fileName) >= S2C_MapFilename.File.FileNameInlineArray.Length)
					{
						_logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"File name '{fileName}' is too long.");
					}

					uint checksum;
					byte[] mapData;

					using (FileStream inputStream = File.OpenRead(filePath))
					{
						// Calculate CRC
						Crc32 crc32 = _objectPoolManager.Crc32Pool.Get();

						try
						{
							crc32.Append(inputStream);
							checksum = crc32.GetCurrentHashAsUInt32();
						}
						finally
						{
							_objectPoolManager.Crc32Pool.Return(crc32);
						}

						inputStream.Position = 0;

						if (compress)
						{
							// Compress using zlib
							using MemoryStream compressedStream = new();
							using (ZLibStream zlibStream = new(compressedStream, CompressionLevel.Optimal, true))
							{
								inputStream.CopyTo(zlibStream);
							}

							compressedStream.Position = 0;

							// Create the map data packet and copy the compressed data into it.
							mapData = new byte[17 + compressedStream.Length];
                            Span<byte> dataSpan = mapData.AsSpan(17);
                            while (dataSpan.Length > 0)
                            {
                                int bytesRead = compressedStream.Read(dataSpan);
                                if (bytesRead == 0)
                                    return null; // end of stream when we expected more data

                                dataSpan = dataSpan[bytesRead..];
                            }
						}
						else
						{
							// Create the map data packet and copy the uncompressed data into it.
							mapData = new byte[17 + inputStream.Length];
							Span<byte> dataSpan = mapData.AsSpan(17);
							while (dataSpan.Length > 0)
							{
								int bytesRead = inputStream.Read(dataSpan);
								if (bytesRead == 0)
									return null; // end of stream when we expected more data

								dataSpan = dataSpan[bytesRead..];
							}
						}
					}

					// Fill in the packet header.
					mapData[0] = (byte)S2CPacketType.MapData;
					StringUtils.DefaultEncoding.GetBytes(fileName, 0, (fileName.Length <= 16) ? fileName.Length : 16, mapData, 1);

					if (mapData.Length > 256 * 1024)
						_logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"Compressed map/lvz is bigger than 256k: {filePath}.");

					return new MapDownloadData(fileName, isOptional, checksum, mapData);
				}
				catch
				{
					return null;
				}
			}
		}

        private void Packet_UpdateRequest(Player player, Span<byte> data, int len, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            if (len != 1)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), player, $"Bad update req packet len={len}.");
                return;
            }

            IFileTransfer fileTransfer = _broker.GetInterface<IFileTransfer>();
            if (fileTransfer is not null)
            {
                try
                {
                    if (!fileTransfer.SendFile(player, "clients/update.exe", string.Empty, false))
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), "Update request, but error setting up to be sent.");
                    }
                }
                finally
                {
                    _broker.ReleaseInterface(ref fileTransfer);
                }
            }
        }

        private void Packet_MapNewsRequest(Player player, Span<byte> data, int len, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            if (data[0] == (byte)C2SPacketType.MapRequest)
            {
                if (len != 1 && len != 3)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), player, $"Bad map/LVZ req packet len={len}.");
                    return;
                }

                Arena arena = player.Arena;
                if (arena is null)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), player, "Map request before entering arena.");
                    return;
                }

                ushort lvznum = (len == 3) ? (ushort)(data[1] | data[2] << 8) : (ushort)0;
                bool wantOpt = player.Flags.WantAllLvz;

                MapDownloadData mdd = GetMap(arena, lvznum, wantOpt);

                if (mdd is null)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(MapNewsDownload), player, $"Can't find lvl/lvz {lvznum}.");
                    return;
                }

                _net.SendSized(player, mdd.Data.Length, _getMapData, new MapDownloadContext(player, mdd));

                _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), player, $"Sending map/lvz {lvznum} ({mdd.Data.Length} bytes) (transfer '{mdd.FileName}').");

                // if we're getting these requests, it's too late to set their ship
                // and team directly, we need to go through the in-game procedures
                if (player.IsStandard && (player.Ship != ShipType.Spec || player.Freq != arena.SpecFreq))
                {
                    IGame game = _broker.GetInterface<IGame>();
                    if (game is not null)
                    {
                        try
                        {
                            game.SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);
                        }
                        finally
                        {
                            _broker.ReleaseInterface(ref game);
                        }
                    }
                }
            }
            else if (data[0] == (byte)C2SPacketType.NewsRequest)
            {
                if (len != 1)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), player, $"Bad news req packet len={len}.");
                    return;
                }

                if (_newsManager.TryGetNews(out ReadOnlyMemory<byte> compressedNewsData, out _))
                {
					_net.SendSized(player, compressedNewsData.Length, _getNewsData, new NewsDownloadContext(player, compressedNewsData));
                    _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), player, $"Sending news.txt ({compressedNewsData.Length} bytes).");
                }
                else
                {
                    _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), "News request, but compressed news doesn't exist.");
                }
            }


			MapDownloadData GetMap(Arena arena, int lvznum, bool wantOpt)
			{
				if (!arena.TryGetExtraData(_dlKey, out List<MapDownloadData> downloadList))
					return null;

				int idx = lvznum;
				foreach (MapDownloadData mdd in downloadList)
				{
					if (!mdd.IsOptional || wantOpt)
					{
						if (idx == 0)
							return mdd;

						idx--;
					}
				}

				return null;
			}
		}

        private void GetNewsData(NewsDownloadContext context, int offset, Span<byte> dataSpan)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Cannot be less than zero.");

            if (dataSpan.IsEmpty)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), context.Player, "Finished news download.");
                return;
            }

            ReadOnlySpan<byte> newsData = context.NewsData.Span;

            int bytesToCopy = dataSpan.Length;
            if (newsData.Length - offset < bytesToCopy)
            {
                bytesToCopy = newsData.Length - offset;

                if (bytesToCopy <= 0)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"Needed to retrieve news sized data of {dataSpan.Length} bytes, but there was no data remaining.");
                    return;
                }
                
                _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"Needed to retrieve news sized data of {dataSpan.Length} bytes, but there was only {bytesToCopy} bytes.");
            }

            newsData.Slice(offset, bytesToCopy).CopyTo(dataSpan);
        }

        private void GetMapData(MapDownloadContext context, int offset, Span<byte> dataSpan)
        {
			if (offset < 0)
				throw new ArgumentOutOfRangeException(nameof(offset), "Cannot be less than zero.");

			MapDownloadData mdd = context.MapDownloadData;

			if (dataSpan.IsEmpty)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), context.Player, $"Finished map/lvz download (transfer '{mdd.FileName}').");
                return;
            }

            int bytesToCopy = dataSpan.Length;
			if (mdd.Data.Length - offset < bytesToCopy)
            {
                bytesToCopy = mdd.Data.Length - offset;

                if (bytesToCopy <= 0)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"Needed to retrieve map sized data of {dataSpan.Length} bytes, but there was no data remaining.");
                    return;
                }
                
                _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"Needed to retrieve map sized data of {dataSpan.Length} bytes, but there was only {bytesToCopy} bytes.");
            }

            mdd.Data.Span.Slice(offset, bytesToCopy).CopyTo(dataSpan);
        }

        public void Dispose()
        {
            _newsManager?.Dispose();
        }

        #region Helper types

        private readonly struct NewsDownloadContext(Player player, ReadOnlyMemory<byte> newsData)
		{
			public Player Player { get; } = player ?? throw new ArgumentNullException(nameof(player));
			public ReadOnlyMemory<byte> NewsData { get; } = newsData;
		}

		private readonly struct MapDownloadContext(Player player, MapDownloadData mapDownloadData)
		{
			public Player Player { get; } = player ?? throw new ArgumentNullException(nameof(player));
			public MapDownloadData MapDownloadData { get; } = mapDownloadData ?? throw new ArgumentNullException(nameof(mapDownloadData));
		}

        private sealed class NewsManager : IDisposable
        {
            private readonly MapNewsDownload _parent;
            private readonly string _newsFilename;
            private byte[] _compressedNewsData; // includes packet header
            private uint? _newsChecksum;
            private FileSystemWatcher _fileWatcher;
            private readonly ReaderWriterLockSlim _rwLock = new();

            public NewsManager(MapNewsDownload parent, string filename)
            {
                _parent = parent ?? throw new ArgumentNullException(nameof(parent));

                if (string.IsNullOrWhiteSpace(filename))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(filename));

                _newsFilename = filename;

                _fileWatcher = new FileSystemWatcher(".", _newsFilename);
                _fileWatcher.NotifyFilter = NotifyFilters.LastWrite;
                _fileWatcher.Changed += FileWatcher_Changed;
                _fileWatcher.EnableRaisingEvents = true;

                ProcessNewsFile();
            }

            private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
            {
                ProcessNewsFile();
            }

            private void ProcessNewsFile()
            {
                _rwLock.EnterUpgradeableReadLock();

                try
                {
                    uint checksum;
                    byte[] fileData;

					try
                    {
                        FileStream newsStream = null;
                        int tries = 0;

                        do
                        {
                            try
                            {
                                newsStream = File.OpenRead(_newsFilename);
                            }
                            catch (IOException ex)
                            {
                                // Note: This retry logic is to workaround the "The process cannot access the file because it is being used by another process." race condition.
                                if (++tries >= 30)
                                {
                                    _parent._logManager.LogM(LogLevel.Error, nameof(MapNewsDownload), $"Error opening '{_newsFilename}' ({tries} tries). {ex.Message}");
                                    return;
                                }

                                _parent._logManager.LogM(LogLevel.Drivel, nameof(MapNewsDownload), $"Error opening '{_newsFilename}' ({tries} tries). {ex.Message}");

                                Thread.Sleep(100);
                            }
                            catch (Exception ex)
                            {
                                _parent._logManager.LogM(LogLevel.Error, nameof(MapNewsDownload), $"Error opening '{_newsFilename}'. {ex.Message}");
                                return;
                            }
                        }
                        while (newsStream is null);

                        try
                        {
                            // calculate the checksum
                            Crc32 crc32 = _parent._objectPoolManager.Crc32Pool.Get();

							try
							{
								crc32.Append(newsStream);
								checksum = crc32.GetCurrentHashAsUInt32();
							}
							finally
							{
								_parent._objectPoolManager.Crc32Pool.Return(crc32);
							}

							if (_newsChecksum is not null && _newsChecksum.Value == checksum)
                            {
                                _parent._logManager.LogM(LogLevel.Drivel, nameof(MapNewsDownload), $"Checked '{_newsFilename}', but there was no change (checksum {checksum:X}).");
                                return; // same checksum, no change
                            }

                            newsStream.Position = 0;

                            // compress using zlib
                            using MemoryStream compressedStream = new();
                            using (ZLibStream zlibStream = new(compressedStream, CompressionLevel.Optimal, true))
                            {
                                newsStream.CopyTo(zlibStream);
                            }

                            compressedStream.Position = 0;

							fileData = new byte[17 + compressedStream.Length]; // 17 is the size of the header
							fileData[0] = (byte)S2CPacketType.IncomingFile;
							// intentionally leaving 16 bytes of 0 for the name
							Span<byte> dataSpan = fileData.AsSpan(17);
                            while (dataSpan.Length > 0)
                            {
                                int bytesRead = compressedStream.Read(dataSpan);
                                if (bytesRead == 0)
                                {
                                    // end of stream when we expected more data
                                    _parent._logManager.LogM(LogLevel.Drivel, nameof(MapNewsDownload), $"Error loading '{_newsFilename}'. Unable to read all data.");
									return; 
								}

								dataSpan = dataSpan[bytesRead..];
							}
						}
                        finally
                        {
                            newsStream.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        _parent._logManager.LogM(LogLevel.Drivel, nameof(MapNewsDownload), $"Error loading '{_newsFilename}'. {ex.Message}");
                        return;
                    }

                    // update the data members
                    _rwLock.EnterWriteLock();

                    try
                    {
                        _compressedNewsData = fileData;
                        _newsChecksum = checksum;
                    }
                    finally
                    {
                        _rwLock.ExitWriteLock();
                    }

                    _parent._logManager.LogM(LogLevel.Info, nameof(MapNewsDownload), $"Loaded news.txt (checksum {checksum:X}).");
                }
                finally
                {
                    _rwLock.ExitUpgradeableReadLock();
                }
            }

            public bool TryGetNews(out ReadOnlyMemory<byte> data, out uint checksum)
            {
                _rwLock.EnterReadLock();

                try
                {
                    if (_compressedNewsData is not null && _newsChecksum is not null)
                    {
                        data = _compressedNewsData;
                        checksum = _newsChecksum.Value;
                        return true;
                    }
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }

                data = null;
                checksum = 0;
                return false;
            }

            #region IDisposable Members

            public void Dispose()
            {
                if (_fileWatcher is not null)
                {
                    _fileWatcher.EnableRaisingEvents = false;
                    _fileWatcher.Changed -= FileWatcher_Changed;
                    _fileWatcher.Dispose();
                    _fileWatcher = null;
                }

                _rwLock.Dispose();
            }

            #endregion
        }

		/// <summary>
		/// Data for a map file that can be downloaded.
		/// </summary>
		/// <param name="FileName">Name of the file.</param>
		/// <param name="IsOptional">Whether the file is optional (lvzs are optional).</param>
		/// <param name="Checksum">CRC32 of the file.</param>
		/// <param name="Data">Compressed bytes of the file, includes the packet header.</param>
		private record MapDownloadData(string FileName, bool IsOptional, uint Checksum, ReadOnlyMemory<byte> Data);

        #endregion
    }
}
