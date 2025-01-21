using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Packets;
using SS.Utilities.Binary;
using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides VIE encryption.
    /// </summary>
    [CoreModuleInfo]
    public sealed class EncryptionVIE(
        IRawNetwork rawNetwork,
        IPlayerData playerData) : IModule, IEncrypt, IClientEncrypt
    {
        public const string InterfaceIdentifier = "enc-vie";

        private readonly IRawNetwork _rawNetwork = rawNetwork ?? throw new ArgumentNullException(nameof(rawNetwork));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        private InterfaceRegistrationToken<IEncrypt>? _iEncryptToken;
        private InterfaceRegistrationToken<IClientEncrypt>? _iClientEncryptToken;

        private PlayerDataKey<EncData> _pdKey;

        private readonly DefaultObjectPool<EncData> _encDataPool = new(new DefaultPooledObjectPolicy<EncData>(), Constants.TargetPlayerCount + Constants.TargetClientConnectionCount);

        #region Module methods

        bool IModule.Load(IComponentBroker broker)
        {
            _pdKey = playerData.AllocatePlayerData(_encDataPool);
            _rawNetwork.AppendConnectionInitHandler(ProcessConnectionInit);
            _iEncryptToken = broker.RegisterInterface<IEncrypt>(this, InterfaceIdentifier);
            _iClientEncryptToken = broker.RegisterInterface<IClientEncrypt>(this, InterfaceIdentifier);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iEncryptToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iClientEncryptToken) != 0)
                return false;

            if (!_rawNetwork.RemoveConnectionInitHandler(ProcessConnectionInit))
                return false;

            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        #endregion

        #region IEncrypt members

        int IEncrypt.Encrypt(Player player, Span<byte> data, int len)
        {
            if (!player.TryGetExtraData(_pdKey, out EncData? pd))
                return len;

            return pd.Encrypt(data, len);
        }

        int IEncrypt.Decrypt(Player player, Span<byte> data, int len)
        {
            if (!player.TryGetExtraData(_pdKey, out EncData? pd))
                return len;

            return pd.Decrypt(data, len);
        }

        void IEncrypt.Void(Player player)
        {
            if (!player.TryGetExtraData(_pdKey, out EncData? pd))
                return;

            pd.Reset();
        }

        #endregion

        #region IClientEncrypt members

        void IClientEncrypt.Initialize(IClientConnection connection)
        {
            if (connection is null)
                return;

            EncData? encData = _encDataPool.Get();
            if (!connection.TryAddExtraData(encData))
            {
                _encDataPool.Return(encData);

                if (connection.TryGetExtraData<EncData>(out encData))
                    encData.Reset();
            }
        }

        int IClientEncrypt.Encrypt(IClientConnection connection, Span<byte> data, int len)
        {
            if (connection is null || !connection.TryGetExtraData<EncData>(out EncData? ed) || ed is null)
                return len;

            if (data[0] == 0x00 && data[1] == 0x01)
            {
                // sending key init, keep track of the key we're sending
                ref ConnectionInitPacket packet = ref MemoryMarshal.AsRef<ConnectionInitPacket>(data);
                ed.SetPendingKeyResponse(packet.Key);
                return len;
            }
            else
            {
                return ed.Encrypt(data, len);
            }
        }

        int IClientEncrypt.Decrypt(IClientConnection connection, Span<byte> data, int len)
        {
            if (connection is null || !connection.TryGetExtraData<EncData>(out EncData? ed) || ed is null)
                return len;

            if (data[0] == 0x00 && data[1] == 0x02)
            {
                // got key response
                ref ConnectionInitResponsePacket packet = ref MemoryMarshal.AsRef<ConnectionInitResponsePacket>(data);
                ed.Init(packet.Key);
                return len;
            }
            else
            {
                return ed.Decrypt(data, len);
            }
        }

        void IClientEncrypt.Void(IClientConnection connection)
        {
            if (connection is null)
                return;

            if (connection.TryRemoveExtraData<EncData>(out EncData? encData))
            {
                _encDataPool.Return(encData);
            }
        }

        #endregion

        private bool ProcessConnectionInit(SocketAddress remoteAddress, ReadOnlySpan<byte> data, ListenData ld)
        {
            if (data.Length != ConnectionInitPacket.Length)
                return false;

            ref readonly ConnectionInitPacket packet = ref MemoryMarshal.AsRef<ConnectionInitPacket>(data);

            if (packet.T1 != 0x00 || packet.T2 != 0x01 || packet.Zero != 0x00)
                return false;

            ClientType clientType;
            switch (packet.ClientType)
            {
                case 0x01:
                    clientType = ClientType.VIE;
                    break;

                case 0x11:
                    clientType = ClientType.Continuum;
                    break;

                default:
                    return false; // unknown type
            }

            IPEndPoint remoteEndpoint = (IPEndPoint)ld.GameSocket.LocalEndPoint!.Create(remoteAddress);
            Player? player = _rawNetwork.NewConnection(clientType, remoteEndpoint, InterfaceIdentifier, ld);

            if (player is null)
            {
                // no slots left?
                Span<byte> disconnect = [0x00, 0x07];
                _rawNetwork.ReallyRawSend(remoteAddress, disconnect, ld);
                return true;
            }

            if (!player.TryGetExtraData(_pdKey, out EncData? pd))
                return false; // should not happen, sanity

            int key = -packet.Key;

            // initialize encryption state for the player
            pd.Init(key);

            // send the response
            ConnectionInitResponsePacket response = new(key);
            _rawNetwork.ReallyRawSend(remoteAddress, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref response, 1)), ld);

            return true;
        }

        private sealed class EncData : IResettable, IDisposable
        {
            private enum EncDataStatus
            {
                Uninitialized = 0,
                PendingKeyResponse, // only for client connections
                Ready,
            }

            private EncDataStatus _status = EncDataStatus.Uninitialized;
            private int? _key;
            private readonly byte[] _table = new byte[520];
            private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);

            public void SetPendingKeyResponse(int requestedKey)
            {
                _rwLock.EnterWriteLock();

                try
                {
                    _status = EncDataStatus.PendingKeyResponse;
                    _key = requestedKey;
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }

            public void Init(int key)
            {
                _rwLock.EnterWriteLock();

                try
                {
                    if (_status == EncDataStatus.PendingKeyResponse)
                    {
                        // encryption for a client connection
                        if (key == _key)
                        {
                            // we've been told to not use encryption
                            key = 0;
                        }
                    }

                    _status = EncDataStatus.Ready;
                    _key = key;

                    if (key == 0)
                        return; // no encryption

                    Span<Int16LittleEndian> myTable = MemoryMarshal.Cast<byte, Int16LittleEndian>(_table);

                    for (int loop = 0; loop < 0x104; loop++)
                    {
                        int t = (int)((key * 0x834E0B5F) >> 48);
                        t += t >> 31;
                        key = ((key % 127773) * 16807) - (t * 2836) + 123;
                        if (key == 0 || (key & 0x80000000) != 0) key += 0x7FFFFFFF;
                        myTable[loop] = (short)key;
                    }
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }

            public int Encrypt(Span<byte> data, int len)
            {
                _rwLock.EnterReadLock();

                try
                {
                    if (_status != EncDataStatus.Ready)
                        return len;

                    int work = _key!.Value;
                    if (work == 0)
                        return len; // no encryption

                    int until;

                    if (data[0] == 0)
                    {
                        data = data[2..];
                        until = (len - 2) / 4 + 1;
                    }
                    else
                    {
                        data = data[1..];
                        until = (len - 1) / 4 + 1;
                    }

                    ReadOnlySpan<Int32LittleEndian> myTable = MemoryMarshal.Cast<byte, Int32LittleEndian>(_table);
                    Span<Int32LittleEndian> myData = MemoryMarshal.Cast<byte, Int32LittleEndian>(data);

                    for (int loop = 0; loop < until; loop++)
                    {
                        work = myData[loop] ^ myTable[loop] ^ work;
                        myData[loop] = work;
                    }

                    return len;
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }
            }

            public int Decrypt(Span<byte> data, int len)
            {
                _rwLock.EnterReadLock();

                try
                {
                    if (_status != EncDataStatus.Ready)
                        return len;

                    int work = _key!.Value;
                    if (work == 0)
                        return len; // no encryption

                    int until;

                    if (data[0] == 0)
                    {
                        data = data[2..];
                        until = (len - 2) / 4 + 1;
                    }
                    else
                    {
                        data = data[1..];
                        until = (len - 1) / 4 + 1;
                    }

                    ReadOnlySpan<Int32LittleEndian> myTable = MemoryMarshal.Cast<byte, Int32LittleEndian>(_table);
                    Span<Int32LittleEndian> myData = MemoryMarshal.Cast<byte, Int32LittleEndian>(data);

                    for (int loop = 0; loop < until; loop++)
                    {
                        int tmp = myData[loop];
                        myData[loop] = myTable[loop] ^ work ^ tmp;
                        work = tmp;
                    }

                    return len;
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }
            }

            public void Reset()
            {
                _rwLock.EnterWriteLock();

                try
                {
                    _status = EncDataStatus.Uninitialized;
                    _key = null;
                    Array.Clear(_table, 0, _table.Length);
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }

            bool IResettable.TryReset()
            {
                Reset();
                return true;
            }

            public void Dispose()
            {
                _rwLock.Dispose();
            }
        }
    }
}
