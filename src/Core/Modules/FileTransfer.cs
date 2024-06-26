﻿using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Diagnostics;
using System.IO;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality to transfer files to and from game clients.
    /// </summary>
    [CoreModuleInfo]
    public class FileTransfer : IModule, IFileTransfer
    {
        private ComponentBroker _broker;
        private INetwork _network;
        private ILogManager _logManager;
        private ICapabilityManager _capabilityManager;
        private IPlayerData _playerData;
        private InterfaceRegistrationToken<IFileTransfer> _iFileTransferToken;

        private static readonly DefaultObjectPool<DownloadDataContext> s_downloadDataContextPool = new(new DefaultPooledObjectPolicy<DownloadDataContext>(), Constants.TargetPlayerCount);

        /// <summary>
        /// Per Player Data key to <see cref="UploadDataContext"/>.
        /// </summary>
        private PlayerDataKey<UploadDataContext> _udKey;

        #region Module Members

        public bool Load(
            ComponentBroker broker,
            INetwork net,
            ILogManager logManager,
            ICapabilityManager capabilityManager,
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _network = net ?? throw new ArgumentNullException(nameof(net));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _udKey = _playerData.AllocatePlayerData<UploadDataContext>();
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            _network.AddPacket(C2SPacketType.UploadFile, Packet_UploadFile);
            _network.AddSizedPacket(C2SPacketType.UploadFile, SizedPacket_UploadFile);

            _iFileTransferToken = _broker.RegisterInterface<IFileTransfer>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iFileTransferToken) != 0)
                return false;

            _network.RemovePacket(C2SPacketType.UploadFile, Packet_UploadFile);
            _network.RemoveSizedPacket(C2SPacketType.UploadFile, SizedPacket_UploadFile);

            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);
            _playerData.FreePlayerData(ref _udKey);

            return true;
        }

        #endregion

        #region IFileTransfer Members

        bool IFileTransfer.SendFile(Player player, string path, ReadOnlySpan<char> filename, bool deleteAfter)
        {
            if (player is null)
                return false;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            int fileLength;
            FileStream fileStream;

            try
            {
                FileInfo fileInfo = new(path);
                if (!fileInfo.Exists)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send '{path}' ({filename}). File does not exist.");
                    return false;
                }

                if (fileInfo.Length > (int.MaxValue - 17))
                {
                    _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send '{path}' ({filename}). File is too large.");
                    return false;
                }

                fileLength = (int)fileInfo.Length;
                fileStream = fileInfo.OpenRead();
            }
            catch (Exception ex)
            {
                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send '{path}' ({filename}). Error opening file. {ex.Message}");
                return false;
            }

            DownloadDataContext context = s_downloadDataContextPool.Get();

            try
            {
                context.Set(player, fileStream, filename, deleteAfter ? path : null);
            }
            catch (Exception ex)
            {
                s_downloadDataContextPool.Return(context);
                fileStream.Dispose();

                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send '{path}' ({filename}). Error initializing sized send. {ex.Message}");
                return false;
            }

            if (!_network.SendSized(player, fileLength + 17, GetSizedSendData, context))
            {
                s_downloadDataContextPool.Return(context);
                fileStream.Dispose();

                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send '{path}' ({filename}). Error queuing up a sized send.");
                return false;
            }

            return true;
        }

		bool IFileTransfer.SendFile(Player player, Stream stream, ReadOnlySpan<char> filename)
        {
            if (player is null)
            {
                stream?.Dispose();
                return false;
            }

			if (stream is null)
				return false;

			int fileLength;

			try
			{
                long length = stream.Length - stream.Position;

				if (length > (int.MaxValue - 17))
				{
                    stream.Dispose();
					_logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send file ({filename}). File is too large.");
					return false;
				}

				fileLength = (int)length;
			}
			catch (Exception ex)
			{
				stream.Dispose();
				_logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send file ({filename}). Error accessing stream. {ex.Message}");
				return false;
			}

			DownloadDataContext context = s_downloadDataContextPool.Get();

			try
			{
				context.Set(player, stream, filename, null);
			}
			catch (Exception ex)
			{
				s_downloadDataContextPool.Return(context);
				stream.Dispose();
				_logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send file ({filename}). Error initializing sized send. {ex.Message}");
				return false;
			}

			if (!_network.SendSized(player, fileLength + 17, GetSizedSendData, context))
			{
				s_downloadDataContextPool.Return(context);
				stream.Dispose();
				_logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send file ({filename}). Error queuing up a sized send.");
				return false;
			}

			return true;
		}

		bool IFileTransfer.RequestFile<T>(Player player, ReadOnlySpan<char> clientPath, FileUploadedDelegate<T> uploaded, T arg)
        {
            if (player is null)
                return false;

            if (Path.GetFileName(clientPath).IsEmpty)
                return false;

            if (StringUtils.DefaultEncoding.GetByteCount(clientPath) > S2C_RequestFile.PathInlineArray.Length)
                return false;

            if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return false;

            if (ud.Stream is not null || !string.IsNullOrWhiteSpace(ud.FileName) || !player.IsStandard)
                return false;

            ud.UploadedInvoker = new FileUploadedDelegateInvoker<T>(uploaded, arg); // TODO: consider object pooling

            S2C_RequestFile packet = new(clientPath, "unused-field");
            _network.SendToOne(player, ref packet, NetSendFlags.Reliable);

            _logManager.LogP(LogLevel.Info, nameof(FileTransfer), player, $"Requesting file '{clientPath}'.");

            if (clientPath.Contains("..", StringComparison.Ordinal))
                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, "Sent file request with '..' in the path.");

            return true;
        }

        void IFileTransfer.SetWorkingDirectory(Player player, string path)
        {
			ArgumentNullException.ThrowIfNull(player);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return;

            ud.WorkingDirectory = path;
        }

        string IFileTransfer.GetWorkingDirectory(Player player)
        {
			ArgumentNullException.ThrowIfNull(player);

			if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return null;

            return ud.WorkingDirectory;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return;

            if (action == PlayerAction.Connect)
            {
                ud.WorkingDirectory = ".";
            }
            else if (action == PlayerAction.Disconnect)
            {
                ud.Cleanup(false);
            }
        }

        private void Packet_UploadFile(Player player, Span<byte> data, int length, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return;

            if (!_capabilityManager.HasCapability(player, Constants.Capabilities.UploadFile))
            {
                _logManager.LogP(LogLevel.Info, nameof(FileTransfer), player, "Denied file upload");
                return;
            }

            bool success = false;

            try
            {
                ud.Stream = File.Create($"tmp/FileTransfer-{Guid.NewGuid():N}");
                ud.FileName = ud.Stream.Name;
				ud.Stream.Write(data[17..length]);
                success = true;
            }
            catch (Exception ex)
            {
                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Can't create temp file for upload. {ex.Message}");
            }

            ud.Cleanup(success);
        }

        private void SizedPacket_UploadFile(Player player, ReadOnlySpan<byte> data, int offset, int totalLength)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return;

            if (offset == -1)
            {
                // cancelled
                ud.Cleanup(false);
                return;
            }

            if (offset == 0 && data.Length > 17 && ud.Stream is null)
            {
                if (!_capabilityManager.HasCapability(player, Constants.Capabilities.UploadFile))
                {
                    _logManager.LogP(LogLevel.Info, nameof(FileTransfer), player, "Denied file upload");
                    return;
                }

                try
                {
                    ud.Stream = File.Create($"tmp/FileTransfer-{Guid.NewGuid():N}");
                }
                catch (Exception ex)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Can't create temp file for upload. {ex.Message}");
                    return;
                }

                ud.FileName = ud.Stream.Name;
                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Accepted file for upload (to '{ud.FileName}').");

                ud.Stream.Write(data[17..]);
            }
            else if (offset > 0 && ud.Stream is not null)
            {
                if (offset < totalLength)
                {
                    ud.Stream.Write(data);
                }
                else
                {
                    _logManager.LogP(LogLevel.Info, nameof(FileTransfer), player, "Completed upload.");
                    ud.Cleanup(true);
                }
            }
            else
            {
                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, "UploadFile with unexpected parameters.");
            }
        }

        private void GetSizedSendData(DownloadDataContext context, int offset, Span<byte> dataSpan)
        {
			ArgumentNullException.ThrowIfNull(context);
            ArgumentOutOfRangeException.ThrowIfLessThan(offset, 0);

            if (dataSpan.IsEmpty)
            {
                Span<char> filename = stackalloc char[16];
                int numChars = context.GetFileName(filename);
                filename = filename[..numChars];

                _logManager.LogP(LogLevel.Info, nameof(FileTransfer), context.Player, $"Completed send of '{filename}'.");
                context.Stream.Dispose();

                if (!string.IsNullOrWhiteSpace(context.DeletePath))
                {
                    try
                    {
                        File.Delete(context.DeletePath);
                        _logManager.LogP(LogLevel.Info, nameof(FileTransfer), context.Player, $"Deleted '{context.DeletePath}' ({filename}) after completed send.");
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), context.Player, $"Failed to delete '{context.DeletePath}' ({filename}) after completed send. {ex.Message}");
                    }
                }

                s_downloadDataContextPool.Return(context);
                return;
            }

            if (offset <= 16)
            {
                // needs all or part of the header
                ReadOnlySpan<byte> headerSpan = context.Header;

                if (offset != 0)
                {
                    // move to the data we need
                    headerSpan = headerSpan[offset..];
                }

                if (dataSpan.Length < headerSpan.Length)
                {
                    headerSpan[..dataSpan.Length].CopyTo(dataSpan);
                    return;
                }
                else
                {
                    headerSpan.CopyTo(dataSpan);

                    if (dataSpan.Length == headerSpan.Length)
                        return;

                    // needs data from the file too, move to the spot for the data
                    dataSpan = dataSpan[headerSpan.Length..];
                }
            }

            // the stream's position should already be where we want to start reading from
            do
            {
                int bytesRead = context.Stream.Read(dataSpan);

                if (bytesRead == 0)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(FileTransfer), $"Needed to retrieve sized data, but was {dataSpan.Length} bytes short.");
                    dataSpan.Clear();
                    return;
                }

                dataSpan = dataSpan[bytesRead..];
            }
            while (dataSpan.Length > 0);
        }

        private class DownloadDataContext : IResettable
        {
            // 0x10 followed by the filename (16 bytes, null terminator required)
            private readonly byte[] _header = new byte[17] { (byte)S2CPacketType.IncomingFile, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

            public void Set(Player player, Stream stream, ReadOnlySpan<char> filename, string deletePath)
            {
                Player = player;

                // In the header, the filename field is 16 bytes, and the last byte must be a null-terminator.
                // Therefore, the filename must be able to be encoded into 1 to 15 bytes.
                int byteCount = StringUtils.DefaultEncoding.GetByteCount(filename);
                if (byteCount < 1 || byteCount > 15)
                    throw new ArgumentException("The value cannot be encoded into 1 to 15 bytes.", nameof(filename));

                StringUtils.WriteNullPaddedString(_header.AsSpan(1), filename, true);
                Stream = stream ?? throw new ArgumentNullException(nameof(stream));
                DeletePath = deletePath; // can be null
            }

            public Player Player { get; private set; }

            public Stream Stream { get; private set; }

            public ReadOnlySpan<byte> Header => _header;

            public string DeletePath { get; private set; }

            public int GetFileName(Span<char> filename)
            {
                Span<byte> filenameBytes = StringUtils.SliceNullTerminated(_header.AsSpan(1));
                int charCount = StringUtils.DefaultEncoding.GetCharCount(filenameBytes);
                int numBytes = StringUtils.DefaultEncoding.GetChars(filenameBytes, filename);
                Debug.Assert(numBytes == filenameBytes.Length);
                return charCount;
            }

			bool IResettable.TryReset()
			{
				Player = null;

				if (Stream is not null)
				{
					Stream.Dispose();
					Stream = null;
				}

				_header.AsSpan(1).Clear();

				DeletePath = null;
				return true;
			}
		}

        private class UploadDataContext : IResettable, IDisposable
        {
            public FileStream Stream;

            public string FileName
            {
                get;
                set;
            }

            public IFileUploadedInvoker UploadedInvoker
            {
                get;
                set;
            }

            public string WorkingDirectory
            {
                get;
                set;
            }

            public void Cleanup(bool success)
            {
                if (Stream is not null)
                {
                    Stream.Dispose();
                    Stream = null;
                }

                if (success)
                {
                    if (UploadedInvoker is not null)
                    {
                        // Invoke the callback, it will handle cleaning up the file
                        UploadedInvoker.Invoke(FileName);
                    }
                    else if (!string.IsNullOrWhiteSpace(FileName))
                    {
                        // Nothing to invoke, get rid of the file as there's no use for it.
                        try
                        {
                            File.Delete(FileName);
                        }
                        catch
                        {
                        }
                    }
                }
                else
                {
                    UploadedInvoker?.Invoke(null);

                    if (!string.IsNullOrWhiteSpace(FileName))
                    {
                        try
                        {
                            File.Delete(FileName);
                        }
                        catch
                        {
                        }
                    }
                }

                FileName = null;
                UploadedInvoker = null;
            }

			public bool TryReset()
			{
                if (UploadedInvoker is not null)
                {
                    // We do not want to invoke callbacks when we call Cleanup(...).
                    UploadedInvoker = null;
                }

                Cleanup(false);

                return true;
            }

            public void Dispose()
            {
                TryReset();
            }
        }

        private interface IFileUploadedInvoker
        {
            void Invoke(string filename);
        }

        private class FileUploadedDelegateInvoker<T> : IFileUploadedInvoker
        {
            private readonly FileUploadedDelegate<T> callback;
            private readonly T state;

            public FileUploadedDelegateInvoker(FileUploadedDelegate<T> callback, T state)
            {
                this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
                this.state = state;
            }

            public void Invoke(string filename)
            {
                callback(filename, state);
            }
        }
    }
}
