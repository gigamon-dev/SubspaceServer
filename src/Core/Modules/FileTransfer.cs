using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.Collections;
using SS.Utilities.ObjectPool;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality to transfer files to and from game clients.
    /// </summary>
    [CoreModuleInfo]
    public sealed class FileTransfer : IModule, IFileTransfer
    {
        /// <summary>
        /// The # of expected concurrent uploads.
        /// </summary>
        /// <remarks>
        /// Most likely there is a single sysop that's uploading, but we'll be generous and be ready to handle more.
        /// </remarks>
        private const int TargetConcurrentUploads = 8;

        private static readonly DefaultObjectPool<DownloadDataContext> s_downloadDataContextPool = new(new DefaultPooledObjectPolicy<DownloadDataContext>(), Constants.TargetPlayerCount);
        private static readonly DefaultObjectPool<LinkedListNode<UploadDataChunk>> s_uploadDataChunkNodePool = new(new LinkedListNodePooledObjectPolicy<UploadDataChunk>(), TargetConcurrentUploads + 64);
        private static readonly DefaultObjectPool<LinkedListNode<Player>> s_playerNodePool = new(new LinkedListNodePooledObjectPolicy<Player>(), TargetConcurrentUploads);

        private readonly IComponentBroker _broker;
        private readonly INetwork _network;
        private readonly ILogManager _logManager;
        private readonly ICapabilityManager _capabilityManager;
        private readonly IMainloop _mainloop;
        private readonly IPlayerData _playerData;
        private InterfaceRegistrationToken<IFileTransfer>? _iFileTransferToken;

        private readonly HybridEventQueue<Player> _uploadQueue = new(TargetConcurrentUploads, s_playerNodePool);
        private readonly CancellationTokenSource _stopCancellationTokenSource = new();
        private readonly CancellationToken _stopToken;
        private Thread? _thread;

        /// <summary>
        /// Per Player Data key to <see cref="UploadDataContext"/>.
        /// </summary>
        private PlayerDataKey<UploadDataContext> _udKey;

        public FileTransfer(
            IComponentBroker broker,
            INetwork network,
            ILogManager logManager,
            ICapabilityManager capabilityManager,
            IMainloop mainloop,
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _stopToken = _stopCancellationTokenSource.Token;
        }

        #region Module Members

        bool IModule.Load(IComponentBroker broker)
        {
            _udKey = _playerData.AllocatePlayerData<UploadDataContext>();
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            _network.AddPacket(C2SPacketType.UploadFile, Packet_UploadFile);
            _network.AddSizedPacket(C2SPacketType.UploadFile, SizedPacket_UploadFile);

            _thread = new(UploadThread) { Name = $"{nameof(FileTransfer)}-upload" };
            _thread.Start();

            _iFileTransferToken = _broker.RegisterInterface<IFileTransfer>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iFileTransferToken) != 0)
                return false;

            _stopCancellationTokenSource.Cancel();
            _thread?.Join();

            _network.RemovePacket(C2SPacketType.UploadFile, Packet_UploadFile);
            _network.RemoveSizedPacket(C2SPacketType.UploadFile, SizedPacket_UploadFile);

            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);
            _playerData.FreePlayerData(ref _udKey);

            return true;
        }

        #endregion

        #region IFileTransfer Members

        async Task<bool> IFileTransfer.SendFileAsync(Player player, string path, ReadOnlyMemory<char> filename, bool deleteAfter)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            if (MemoryExtensions.IsWhiteSpace(filename.Span))
                throw new ArgumentException("Cannot be white-space.", nameof(filename));

            if (!_mainloop.IsMainloop)
                return false;

            // Save the player name before any await, so that we can check the player object after the await.
            string? playerName = player.Name;
            if (playerName is null)
                return false;

            FileStream fileStream;

            try
            {
                fileStream = await Task.Factory.StartNew(
                    static (p) => new FileStream((string)p!, FileMode.Open, FileAccess.Read, FileShare.Read),
                    path).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
                if (player == _playerData.FindPlayer(playerName))
                {
                    _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send '{path}' ({filename}). Error opening file. {ex.Message}");
                }

                return false;
            }

            // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
            if (player != _playerData.FindPlayer(playerName))
            {
                await fileStream.DisposeAsync().ConfigureAwait(true);
                return false;
            }

            return await SendFileAsync(player, fileStream, filename, path, deleteAfter).ConfigureAwait(true);
        }

        Task<bool> IFileTransfer.SendFileAsync(Player player, Stream stream, ReadOnlyMemory<char> filename)
        {
            return SendFileAsync(player, stream, filename, null, false);
        }

        async Task<bool> SendFileAsync(Player player, Stream stream, ReadOnlyMemory<char> filename, string? path, bool deletePath)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(stream);

            if (MemoryExtensions.IsWhiteSpace(filename.Span))
                throw new ArgumentException("Cannot be white-space.", nameof(filename));

            if (!_mainloop.IsMainloop)
                return false;

            string? playerName = player.Name;
            if (playerName is null)
                return false;

            int fileLength;

            try
            {
                long length = await Task.Factory.StartNew(static (s) => ((Stream)s!).Length, stream).ConfigureAwait(true); // Resume execution on the mainloop thread.
                length -= stream.Position;

                if (length > (int.MaxValue - 17))
                {
                    await stream.DisposeAsync().ConfigureAwait(true);

                    // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
                    if (player == _playerData.FindPlayer(playerName))
                    {
                        if (!string.IsNullOrWhiteSpace(path))
                            _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send '{path}' ({filename}). The file is too large.");
                        else
                            _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send data stream as file ({filename}). The file is too large.");
                    }

                    return false;
                }

                fileLength = (int)length;
            }
            catch (Exception ex)
            {
                await stream.DisposeAsync().ConfigureAwait(true);

                // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
                if (player == _playerData.FindPlayer(playerName))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send '{path}' ({filename}). Error accessing file info. {ex.Message}");
                    else
                        _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send data stream as file ({filename}). Error accessing stream info. {ex.Message}");
                }

                return false;
            }

            DownloadDataContext context = s_downloadDataContextPool.Get();

            try
            {
                context.Set(player, stream, filename.Span, deletePath ? path : null);
            }
            catch (Exception ex)
            {
                s_downloadDataContextPool.Return(context);
                await stream.DisposeAsync().ConfigureAwait(true);

                // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
                if (player == _playerData.FindPlayer(playerName))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send '{path}' ({filename}). Error initializing sized send. {ex.Message}");
                    else
                        _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send data stream as file ({filename}). Error initializing sized send. {ex.Message}");
                }

                return false;
            }

            if (!_network.SendSized(player, fileLength + 17, GetSizedSendData, context))
            {
                s_downloadDataContextPool.Return(context);
                await stream.DisposeAsync().ConfigureAwait(true);

                // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
                if (player == _playerData.FindPlayer(playerName))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send '{path}' ({filename}). Error queuing up a sized send.");
                    else
                        _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Unable to send data stream as file ({filename}). Error queuing up a sized send.");
                }

                return false;
            }

            return true;
        }

        bool IFileTransfer.SendFile(Player player, MemoryStream stream, ReadOnlySpan<char> filename)
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

        Task<string?> IFileTransfer.RequestFileAsync(Player player, ReadOnlyMemory<char> clientPath)
        {
            if (player is null || !player.IsStandard)
                return Task.FromResult((string?)null);

            if (Path.GetFileName(clientPath.Span).IsEmpty)
                return Task.FromResult((string?)null);

            if (StringUtils.DefaultEncoding.GetByteCount(clientPath.Span) > S2C_RequestFile.PathInlineArray.Length)
                return Task.FromResult((string?)null);

            if (!player.TryGetExtraData(_udKey, out UploadDataContext? ud))
                return Task.FromResult((string?)null);

            Task<string?> task;

            // Try to enter the semaphore, but do not wait if it already is held.
            if (!ud.StreamSemaphore.Wait(0))
            {
                // Another thread is holding the semaphore already. This means there's already a transfer in progress.
                return Task.FromResult((string?)null);
            }

            try
            {
                if (ud.Stream is not null)
                {
                    // There is another transfer in progress.
                    return Task.FromResult((string?)null);
                }

                lock (ud.Lock)
                {
                    if (ud.UploadTaskCompletionSource is not null)
                    {
                        // There was already another request (but no data received yet). Complete the previous request as a failure.
                        // This handles the case where the previous request was for a file that did not exist.
                        ud.UploadTaskCompletionSource.SetResult(null);
                        ud.UploadTaskCompletionSource = null;
                    }

                    // Create a new task completion source and task.
                    ud.UploadTaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    task = ud.UploadTaskCompletionSource.Task;
                }
            }
            finally
            {
                ud.StreamSemaphore.Release();
            }

            S2C_RequestFile packet = new(clientPath.Span, "unused-field");
            _network.SendToOne(player, ref packet, NetSendFlags.Reliable);

            _logManager.LogP(LogLevel.Info, nameof(FileTransfer), player, $"Requesting file '{clientPath}'.");

            if (clientPath.Span.Contains("..", StringComparison.Ordinal))
                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, "Sent file request with '..' in the path.");

            return task;
        }

        void IFileTransfer.SetWorkingDirectory(Player player, string path)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            if (!player.TryGetExtraData(_udKey, out UploadDataContext? ud))
                return;

            ud.WorkingDirectory = path;
        }

        string? IFileTransfer.GetWorkingDirectory(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);

            if (!player.TryGetExtraData(_udKey, out UploadDataContext? ud))
                return null;

            return ud.WorkingDirectory;
        }

        #endregion

        private async void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_udKey, out UploadDataContext? ud))
                return;

            if (action == PlayerAction.Connect)
            {
                ud.WorkingDirectory = ".";
            }
            else if (action == PlayerAction.Disconnect)
            {
                _playerData.AddHold(player);

                _uploadQueue.Remove(player);
                await ud.CleanupAsync(false, false).ConfigureAwait(false);

                _playerData.RemoveHold(player);
            }
        }

        #region Packet handlers

        // The data either fit into a regular sized packet OR it was sent to us using big data packets (0x00 0x08 and 0x00 0x09).
        // Regular packet handlers such as this one are executed on the mainloop thread.
        private void Packet_UploadFile(Player player, Span<byte> data, NetReceiveFlags flags)
        {
            if (player is null || !player.TryGetExtraData(_udKey, out UploadDataContext? ud))
                return;

            if (!_capabilityManager.HasCapability(player, Constants.Capabilities.UploadFile))
            {
                _logManager.LogP(LogLevel.Info, nameof(FileTransfer), player, "Not authorized to upload file.");
                return;
            }

            byte[] dataArray = ArrayPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(dataArray);

            bool queued = false;

            lock (ud.Lock)
            {
                if (ud.UploadTaskCompletionSource is not null)
                {
                    // Add a node with the data.
                    LinkedListNode<UploadDataChunk> node = s_uploadDataChunkNodePool.Get();
                    node.ValueRef = new UploadDataChunk(dataArray, data.Length, 0, data.Length);
                    ud.ChunksToWrite.AddLast(node);

                    // Add a node indicating the data is complete.
                    node = s_uploadDataChunkNodePool.Get();
                    node.ValueRef = new UploadDataChunk(null, 0, data.Length, data.Length);
                    ud.ChunksToWrite.AddLast(node);

                    // Queue the player up to be processed.
                    _uploadQueue.TryEnqueue(player);

                    queued = true;
                }
            }

            if (!queued)
            {
                // There's no upload task. It may have been cancelled.
                ArrayPool<byte>.Shared.Return(dataArray);
            }
        }

        // This gets executed on the reliable thread since sized data packets must be sent reliably.
        private void SizedPacket_UploadFile(Player player, ReadOnlySpan<byte> data, int offset, int totalLength)
        {
            if (player is null || !player.TryGetExtraData(_udKey, out UploadDataContext? ud))
                return;

            if (!_capabilityManager.HasCapability(player, Constants.Capabilities.UploadFile))
            {
                _logManager.LogP(LogLevel.Info, nameof(FileTransfer), player, "Not authorized to upload file.");
                return;
            }

            byte[] dataArray = ArrayPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(dataArray);

            bool queued = false;

            lock (ud.Lock)
            {
                if (ud.UploadTaskCompletionSource is not null)
                {
                    LinkedListNode<UploadDataChunk> node = s_uploadDataChunkNodePool.Get();
                    node.ValueRef = new UploadDataChunk(dataArray, data.Length, offset, totalLength);
                    ud.ChunksToWrite.AddLast(node);

                    // Queue the player up to be processed.
                    _uploadQueue.TryEnqueue(player);

                    queued = true;
                }
            }

            if (!queued)
            {
                // There's no upload task. It may have been cancelled.
                ArrayPool<byte>.Shared.Return(dataArray);
            }
        }

        #endregion

        private void UploadThread()
        {
            WaitHandle[] waitHandles = [_uploadQueue.ReadyEvent, _stopToken.WaitHandle];

            while (!_stopToken.IsCancellationRequested)
            {
                switch (WaitHandle.WaitAny(waitHandles))
                {
                    case 0:
                        ProcessUploadQueue();
                        break;

                    case 1:
                        // We've been told to stop.
                        return;
                }
            }

            void ProcessUploadQueue()
            {
                while (!_stopToken.IsCancellationRequested)
                {
                    // Get the next player that has data to process.
                    if (!_uploadQueue.TryDequeue(out Player? player))
                    {
                        // No pending work.
                        break;
                    }

                    ProcessPlayerUploadData(player);
                }
            }

            void ProcessPlayerUploadData(Player player)
            {
                if (!player.TryGetExtraData(_udKey, out UploadDataContext? ud))
                    return;

                while (true)
                {
                    byte[]? dataArray;
                    int dataLength;
                    int offset;
                    int totalLength;

                    lock (ud.Lock)
                    {
                        if (ud.UploadTaskCompletionSource is null)
                            return;

                        LinkedListNode<UploadDataChunk>? node = ud.ChunksToWrite.First;
                        if (node is null)
                            break;

                        ref UploadDataChunk chunk = ref node.ValueRef;
                        dataArray = chunk.DataArray;
                        dataLength = chunk.DataLength;
                        offset = chunk.Offset;
                        totalLength = chunk.TotalLength;

                        ud.ChunksToWrite.Remove(node);
                        s_uploadDataChunkNodePool.Return(node);
                    }

                    try
                    {
                        ProcessUploadDataChunk(
                            player,
                            ud,
                            dataArray is not null ? dataArray.AsSpan(0, dataLength) : [],
                            offset,
                            totalLength);
                    }
                    finally
                    {
                        if (dataArray is not null)
                        {
                            ArrayPool<byte>.Shared.Return(dataArray);
                        }
                    }
                }
            }

            void ProcessUploadDataChunk(Player player, UploadDataContext ud, ReadOnlySpan<byte> data, int offset, int totalLength)
            {
                if (offset == -1)
                {
                    // cancelled
                    ud.CleanupAsync(false, false).Wait();
                    return;
                }

                ud.StreamSemaphore.Wait();

                try
                {
                    lock (ud.Lock)
                    {
                        if (ud.UploadTaskCompletionSource is null)
                        {
                            // The player disconnected.
                            return;
                        }
                    }

                    FileStream? fileStream = ud.Stream;

                    if (fileStream is null)
                    {
                        // The first 17 bytes are the file upload packet header (1 byte for type which is 0x16, followed by 16 bytes for file name).
                        // The file name is not needed. The command that requested the transfer already knows the name (and may have a name it wants to use that's not limited to 16 bytes).
                        // We only want the actual file data which comes after the header.

                        // Check that we have at least the very first byte of actual file data.
                        if (offset <= 17 && offset + data.Length > 17)
                        {
                            // Skip the file upload packet header.
                            data = data[(17 - offset)..];

                            string tempFilePath = $"tmp/FileTransfer-{Guid.NewGuid():N}";

                            try
                            {
                                fileStream = File.Create(tempFilePath);
                            }
                            catch (Exception ex)
                            {
                                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Error creating a temp file for upload. {ex.Message}");
                                return;
                            }

                            try
                            {
                                fileStream.Write(data);
                            }
                            catch (Exception ex)
                            {
                                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Error writing to file for upload. {ex.Message}");

                                // Close the stream.
                                fileStream.Dispose();

                                // Delete the temp file.
                                try
                                {
                                    File.Delete(tempFilePath);
                                }
                                catch (Exception deleteException)
                                {
                                    _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Error deleting temp file. {deleteException.Message}");
                                }

                                return;
                            }

                            // Set the stream in the player's UploadDataContext.
                            // However, keep in mind we were purposely not holding any locks while creating and writing to the file.
                            // It's possible that the UploadDataContext is no longer valid (player disconnected)

                            bool success = false;

                            lock (ud.Lock)
                            {
                                if (ud.UploadTaskCompletionSource is not null)
                                {
                                    ud.Stream = fileStream;
                                    ud.FilePath = tempFilePath;

                                    success = true;
                                }
                            }

                            if (success)
                            {
                                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Accepted file for upload (to '{tempFilePath}').");
                            }
                            else
                            {
                                // The UploadDataContext was no longer valid.

                                // Close the stream.
                                fileStream.Dispose();

                                // Delete the temp file.
                                try
                                {
                                    File.Delete(tempFilePath);
                                }
                                catch (Exception deleteException)
                                {
                                    _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Error deleting temp file. {deleteException.Message}");
                                }
                            }
                        }
                        else if (offset >= totalLength)
                        {
                            // There's no file stream, but this is the last piece of data for the file.
                            // This means there was an issue creating or writing to the file.
                            // Set it as having completed unsuccessfully.
                            ud.CleanupAsync(false, true).Wait();
                        }
                    }
                    else
                    {
                        if (offset < totalLength)
                        {
                            try
                            {
                                fileStream.Write(data);
                            }
                            catch (Exception ex)
                            {
                                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, $"Error writing to file for upload. {ex.Message}");
                                // TODO: figure out what to do, flag it as bad? get rid of the stream?
                                return;
                            }
                        }
                        else
                        {
                            _logManager.LogP(LogLevel.Info, nameof(FileTransfer), player, "Completed upload.");
                            ud.CleanupAsync(true, true).Wait();
                        }
                    }
                }
                finally
                {
                    ud.StreamSemaphore.Release();
                }
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

                _logManager.LogP(LogLevel.Info, nameof(FileTransfer), context.Player!, $"Completed send of '{filename}'.");
                context.Stream!.Dispose();

                if (!string.IsNullOrWhiteSpace(context.DeletePath))
                {
                    try
                    {
                        File.Delete(context.DeletePath);
                        _logManager.LogP(LogLevel.Info, nameof(FileTransfer), context.Player!, $"Deleted '{context.DeletePath}' ({filename}) after completed send.");
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), context.Player!, $"Failed to delete '{context.DeletePath}' ({filename}) after completed send. {ex.Message}");
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
                int bytesRead = context.Stream!.Read(dataSpan);

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
            private readonly byte[] _header = [(byte)S2CPacketType.IncomingFile, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

            public void Set(Player player, Stream stream, ReadOnlySpan<char> filename, string? deletePath)
            {
                Player = player;

                // In the header, the filename field is 16 bytes, and the last byte must be a null-terminator.
                // Therefore, the filename must be able to be encoded into 1 to 15 bytes.
                int byteCount = StringUtils.DefaultEncoding.GetByteCount(filename);
                if (byteCount < 1 || byteCount > 15)
                    throw new ArgumentException("The value cannot be encoded into 1 to 15 bytes.", nameof(filename));

                StringUtils.WriteNullPaddedString(_header.AsSpan(1), filename, true);
                Stream = stream ?? throw new ArgumentNullException(nameof(stream));
                DeletePath = deletePath;
            }

            public Player? Player { get; private set; }

            public Stream? Stream { get; private set; }

            public ReadOnlySpan<byte> Header => _header;

            public string? DeletePath { get; private set; }

            public int GetFileName(Span<char> filename)
            {
                Span<byte> filenameBytes = StringUtils.SliceNullTerminated(_header.AsSpan(1));
                return StringUtils.DefaultEncoding.GetChars(filenameBytes, filename);
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

        /// <summary>
        /// A chunk of data for a file being uploaded.
        /// </summary>
        /// <param name="DataArray">The array containing the data to write. This is rented from the <see cref="ArrayPool{T}"/>.</param>
        /// <param name="DataLength">The # of bytes in <paramref name="dataArray"/> to write.</param>
        /// <param name="Offset">The offset of the file transfer.</param>
        /// <param name="TotalLength">The total # of bytes of the transfer.</param>
        private readonly record struct UploadDataChunk(byte[]? DataArray, int DataLength, int Offset, int TotalLength);

        private class UploadDataContext : IResettable, IDisposable
        {
            /// <summary>
            /// For synchronizing access to everything except the <see cref="Stream"/>.
            /// </summary>
            public readonly object Lock = new();

            /// <summary>
            /// The current upload task producer. This provides the actual task and allows us to set its result.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="Lock"/>.
            /// </remarks>
            public TaskCompletionSource<string?>? UploadTaskCompletionSource;

            /// <summary>
            /// Queue of data to write to the <see cref="Stream"/>.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="Lock"/>.
            /// </remarks>
            public readonly LinkedList<UploadDataChunk> ChunksToWrite = new();

            /// <summary>
            /// Used to synchronize access to the <see cref="Stream"/>.
            /// </summary>
            public readonly SemaphoreSlim StreamSemaphore = new(1, 1);

            /// <summary>
            /// Stream to the temporary file that the data is being written to.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="StreamSemaphore"/>.
            /// </remarks>
            public FileStream? Stream;

            /// <summary>
            /// The file path of the temporary file.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="Lock"/>.
            /// </remarks>
            public string? FilePath;


            public string? WorkingDirectory;

            public async Task CleanupAsync(bool success, bool isSemaphoreHeld)
            {
                if (!isSemaphoreHeld)
                    await StreamSemaphore.WaitAsync();

                try
                {
                    // If there's a stream, close/dispose it.
                    if (Stream is not null)
                    {
                        await Stream.DisposeAsync();
                        Stream = null;
                    }

                    lock (Lock)
                    {
                        // Remove any remaining data in the queue.
                        LinkedListNode<UploadDataChunk>? node;
                        while ((node = ChunksToWrite.First) is not null)
                        {
                            ArrayPool<byte>.Shared.Return(node.ValueRef.DataArray!);
                            ChunksToWrite.Remove(node);
                            s_uploadDataChunkNodePool.Return(node);
                        }

                        bool deleteTempFile = true;

                        if (UploadTaskCompletionSource is not null)
                        {
                            UploadTaskCompletionSource.SetResult(success ? FilePath : null);
                            UploadTaskCompletionSource = null;

                            if (success)
                            {
                                // The awaiter of the task that we just set the result on is responsible for cleaning up the file.
                                deleteTempFile = false;
                            }
                        }

                        if (deleteTempFile && !string.IsNullOrWhiteSpace(FilePath))
                        {
                            try
                            {
                                File.Delete(FilePath);
                            }
                            catch
                            {
                            }
                        }

                        FilePath = null;
                    }
                }
                finally
                {
                    if (!isSemaphoreHeld)
                        StreamSemaphore.Release();
                }
            }

            public bool TryReset()
            {
                // When this is called. The player should already be past the disconnect stage.
                // This means there should be no stream, and this should actually execute synchronously.

                CleanupAsync(false, false).Wait();
                return true;
            }

            public void Dispose()
            {
                TryReset();
            }
        }
    }
}
