using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.IO;
using System.Text;

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

        /// <summary>
        /// Per Player Data key to <see cref="UploadDataContext"/>.
        /// </summary>
        private PlayerDataKey<UploadDataContext> _udKey;

        #region IModule Members

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

        bool IFileTransfer.SendFile(Player player, string path, string filename, bool deleteAfter)
        {
            if (player == null)
                return false;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                FileInfo fileInfo = new(path);
                if (!fileInfo.Exists)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(FileTransfer), $"File '{path}' does not exist.");
                    return false;
                }

                FileStream fileStream = fileInfo.OpenRead();
                DownloadDataContext dd = new(fileStream, filename, deleteAfter ? path : null);
                _network.SendSized(player, (int)fileInfo.Length + 17, GetSizedSendData, dd);
                return true;
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Warn, nameof(FileTransfer), $"Error opening file '{path}'. {ex.Message}");
                return false;
            }
        }

        bool IFileTransfer.RequestFile<T>(Player player, string path, FileUploadedDelegate<T> uploaded, T arg)
        {
            if (player == null)
                return false;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return false;

            if (path.Length > 256)
                return false;

            if (ud.Stream != null || !string.IsNullOrWhiteSpace(ud.FileName) || !player.IsStandard)
                return false;

            ud.UploadedInvoker = new FileUploadedDelegateInvoker<T>(uploaded, arg);

            S2C_RequestFile packet = new(path, "unused-field");
            _network.SendToOne(player, ref packet, NetSendFlags.Reliable);

            _logManager.LogP(LogLevel.Info, nameof(FileTransfer), player, $"Requesting file '{path}'.");

            if (path.Contains(".."))
                _logManager.LogP(LogLevel.Warn, nameof(FileTransfer), player, "Sent file request with '..' in the path.");

            return true;
        }

        void IFileTransfer.SetWorkingDirectory(Player player, string path)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Cannot be null or white-space.", nameof(path));

            if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return;

            ud.WorkingDirectory = path;
        }

        string IFileTransfer.GetWorkingDirectory(Player player)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return null;

            return ud.WorkingDirectory;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (player == null)
                return;

            if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return;

            if (action == PlayerAction.Connect)
            {
                ud.WorkingDirectory = ".";
            }
            else if(action == PlayerAction.Disconnect)
            {
                ud.Cleanup(false);
            }
        }

        private void Packet_UploadFile(Player player, byte[] data, int length)
        {
            if (player == null)
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
                ud.Stream.Write(new Span<byte>(data, 17, length - 17));
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
            if (player == null)
                return;

            if (!player.TryGetExtraData(_udKey, out UploadDataContext ud))
                return;

            if (offset == -1)
            {
                // cancelled
                ud.Cleanup(false);
                return;
            }

            if (offset == 0 && data.Length > 17 && ud.Stream == null)
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
            else if (offset > 0 && ud.Stream != null)
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

        private void GetSizedSendData(DownloadDataContext dd, int offset, Span<byte> dataSpan)
        {
            if (dd == null)
                throw new ArgumentNullException(nameof(dd));

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Cannot be less than zero.");

            if (dataSpan.IsEmpty)
            {
                _logManager.LogM(LogLevel.Info, nameof(FileTransfer), $"Completed send of '{dd.Filename}'.");
                dd.Stream.Dispose();

                if (!string.IsNullOrWhiteSpace(dd.DeletePath))
                {
                    try
                    {
                        File.Delete(dd.DeletePath);
                        _logManager.LogM(LogLevel.Info, nameof(FileTransfer), $"Deleted '{dd.DeletePath}' ({dd.Filename}) after completed send.");
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(FileTransfer), $"Failed to delete '{dd.DeletePath}' ({dd.Filename}) after completed send. {ex.Message}");
                    }
                }

                return;
            }

            if (offset <= 16)
            {
                // needs all or part of the header, create the header
                Span<byte> headerSpan = stackalloc byte[17];
                headerSpan[0] = (byte)S2CPacketType.IncomingFile;
                Encoding.ASCII.GetBytes(dd.Filename, headerSpan[1..]);

                if (offset != 0)
                {
                    // move to the data we need
                    headerSpan = headerSpan[offset..];
                }

                if (dataSpan.Length < headerSpan.Length)
                {
                    headerSpan.Slice(0, dataSpan.Length).CopyTo(dataSpan);
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
                int bytesRead = dd.Stream.Read(dataSpan);

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

        private class DownloadDataContext
        {
            public readonly FileStream Stream;
            public readonly string Filename;
            public readonly string DeletePath;

            public DownloadDataContext(FileStream stream, string filename, string deletePath)
            {
                Stream = stream ?? throw new ArgumentNullException(nameof(stream));
                Filename = filename ?? throw new ArgumentNullException(nameof(filename)); // can be empty
                DeletePath = deletePath;
            }
        }

        private class UploadDataContext : IPooledExtraData, IDisposable
        {
            public FileStream Stream;

            public string FileName
            {
                get;
                set;
            }

            public FileUploadedDelegateInvoker UploadedInvoker
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
                if (Stream != null)
                {
                    Stream.Dispose();
                    Stream = null;
                }

                if (success)
                {
                    if (UploadedInvoker != null)

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

            public void Reset()
            {
                if (UploadedInvoker is not null)
                {
                    // We do not want to invoke callbacks when we call Cleanup(...).
                    UploadedInvoker = null;
                }

                Cleanup(false);
            }

            public void Dispose()
            {
                Reset();
            }
        }

        private abstract class FileUploadedDelegateInvoker
        {
            public abstract void Invoke(string filename);
        }

        private class FileUploadedDelegateInvoker<T> : FileUploadedDelegateInvoker
        {
            private readonly FileUploadedDelegate<T> callback;
            private readonly T state;

            public FileUploadedDelegateInvoker(FileUploadedDelegate<T> callback, T state)
            {
                this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
                this.state = state;
            }

            public override void Invoke(string filename)
            {
                callback(filename, state);
            }
        }
    }
}
