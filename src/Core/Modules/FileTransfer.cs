using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using System;
using System.IO;
using System.Text;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class FileTransfer : IModule, IFileTransfer
    {
        private ComponentBroker _broker;
        private INetwork _network;
        private ILogManager _logManager;
        private ICapabilityManager _capabilityManager;
        private IPlayerData _playerData;
        private InterfaceRegistrationToken _iFileTransferToken;

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

        private int _udKey;

        private class UploadDataContext
        {

        }

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

            _iFileTransferToken = _broker.RegisterInterface<IFileTransfer>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<IFileTransfer>(ref _iFileTransferToken) != 0)
                return false;

            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);
            _playerData.FreePlayerData(_udKey);

            return true;
        }

        #endregion

        #region IFileTransfer Members

        bool IFileTransfer.SendFile(Player p, string path, string filename, bool deleteAfter)
        {
            if (p == null)
                return false;

            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                FileInfo fileInfo = new FileInfo(path);
                if (!fileInfo.Exists)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(FileTransfer), "file '{0}' does not exist", path);
                    return false;
                }

                FileStream fileStream = fileInfo.OpenRead();
                DownloadDataContext dd = new DownloadDataContext(fileStream, filename, deleteAfter ? path : null);
                _network.SendSized(p, (int)fileInfo.Length + 17, GetSizedSendData, dd);
                return true;
            }
            catch(Exception ex)
            {
                _logManager.LogM(LogLevel.Warn, nameof(FileTransfer), "error opening file '{0}' - {1}", path, ex.Message);
                return false;
            }
        }

        #endregion

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            // TODO:
            //if (!(p[_udKey] is UploadDataContext ud))
                //return;

            if (action == PlayerAction.Connect)
            {
                
            }
            else if(action == PlayerAction.Disconnect)
            {
                
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
                _logManager.LogM(LogLevel.Info, nameof(FileTransfer), "completed send of {0}", dd.Filename);
                dd.Stream.Dispose();
                if (!string.IsNullOrEmpty(dd.DeletePath))
                {
                    try
                    {
                        File.Delete(dd.DeletePath);
                        _logManager.LogM(LogLevel.Info, nameof(FileTransfer), "deleted {0} after completed send", dd.Filename);
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(FileTransfer), "failed to delete {0} after completed send.  {1}", dd.Filename, ex.Message);
                    }
                }

                return;
            }

            if (offset <= 16)
            {
                // needs all or part of the header, create the header
                Span<byte> headerSpan = stackalloc byte[17];
                headerSpan[0] = (byte)S2CPacketType.IncomingFile;
                Encoding.ASCII.GetBytes(dd.Filename, headerSpan.Slice(1));

                if (offset != 0)
                {
                    // move to the data we need
                    headerSpan = headerSpan.Slice(offset);
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
                    dataSpan = dataSpan.Slice(headerSpan.Length);
                }
            }

            // the stream's position should already be where we want to start reading from
            int bytesRead = dd.Stream.Read(dataSpan);
            if (bytesRead != dataSpan.Length)
            {
                _logManager.LogM(LogLevel.Warn, nameof(FileTransfer), $"Needed to retrieve sized data of {dataSpan.Length} bytes, but was only able to read {bytesRead} bytes.");
            }
        }
    }
}
