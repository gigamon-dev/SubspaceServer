﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.ComponentInterfaces;
using System.IO;
using SS.Core.Packets;
using SS.Core.ComponentCallbacks;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class FileTransferModule : IModule, IFileTransfer
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
                if (stream == null)
                    throw new ArgumentNullException("stream");

                if (filename == null) // can be empty
                    throw new ArgumentNullException("filename");

                Stream = stream;
                Filename = filename;
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
            PlayerActionCallback.Register(_broker, playerAction);
            _iFileTransferToken = _broker.RegisterInterface<IFileTransfer>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<IFileTransfer>(ref _iFileTransferToken) != 0)
                return false;

            PlayerActionCallback.Unregister(_broker, playerAction);
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
                    _logManager.LogM(LogLevel.Warn, nameof(FileTransferModule), "file '{0}' does not exist", path);
                    return false;
                }

                FileStream fileStream = fileInfo.OpenRead();
                DownloadDataContext dd = new DownloadDataContext(fileStream, filename, deleteAfter ? path : null);
                _network.SendSized<DownloadDataContext>(p, dd, (int)fileInfo.Length + 17, getSizedSendData);
                return true;
            }
            catch(Exception ex)
            {
                _logManager.LogM(LogLevel.Warn, nameof(FileTransferModule), "error opening file '{0}' - {1}", path, ex.Message);
                return false;
            }
        }

        #endregion

        private void playerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            UploadDataContext ud = p[_udKey] as UploadDataContext;
            if (ud == null)
                return;

            if (action == PlayerAction.Connect)
            {
                
            }
            else if(action == PlayerAction.Disconnect)
            {
                
            }
        }

        private void getSizedSendData(DownloadDataContext dd, int offset, byte[] buf, int bufStartIndex, int bytesNeeded)
        {
            if (dd == null)
                return;

            if (bytesNeeded == 0)
            {
                _logManager.LogM(LogLevel.Info, nameof(FileTransferModule), "completed send of {0}", dd.Filename);
                dd.Stream.Dispose();
                if (!string.IsNullOrEmpty(dd.DeletePath))
                {
                    try
                    {
                        File.Delete(dd.DeletePath);
                        _logManager.LogM(LogLevel.Info, nameof(FileTransferModule), "deleted {0} after completed send", dd.Filename);
                    }
                    catch(Exception ex)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(FileTransferModule), "failed to delete {0} after completed send.  {1}", dd.Filename, ex.Message);
                    }
                }
            }
            else if (offset == 0 && bytesNeeded >= 17)
            {
                buf[bufStartIndex++] = (byte)S2CPacketType.IncomingFile;
                Encoding.ASCII.GetBytes(dd.Filename, 0, 16, buf, bufStartIndex);
                bufStartIndex += 16;
                dd.Stream.Read(buf, bufStartIndex, bytesNeeded - 17);
            }
            else if(offset > 0)
            {
                dd.Stream.Read(buf, bufStartIndex, bytesNeeded);
            }
        }
    }
}