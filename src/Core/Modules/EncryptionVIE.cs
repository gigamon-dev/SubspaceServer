using SS.Core.ComponentInterfaces;
using SS.Packets;
using SS.Utilities.Binary;
using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace SS.Core.Modules
{
    public class EncryptionVIE : IModule, IEncrypt, IClientEncrypt
    {
        public const string InterfaceIdentifier = "enc-vie";

        private INetworkEncryption _networkEncryption;
        private IPlayerData _playerData;
        private InterfaceRegistrationToken<IEncrypt> _iEncryptToken;
        private InterfaceRegistrationToken<IClientEncrypt> _iClientEncryptToken;

        private PlayerDataKey<EncData> _pdKey;

        #region Module methods

        public bool Load(
            ComponentBroker broker, 
            INetworkEncryption networkEncryption,
            IPlayerData playerData)
        {
            _networkEncryption = networkEncryption ?? throw new ArgumentNullException(nameof(networkEncryption));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _pdKey = playerData.AllocatePlayerData<EncData>();
            _networkEncryption.AppendConnectionInitHandler(ProcessConnectionInit);
            _iEncryptToken = broker.RegisterInterface<IEncrypt>(this, InterfaceIdentifier);
            _iClientEncryptToken = broker.RegisterInterface<IClientEncrypt>(this, InterfaceIdentifier);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iEncryptToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iClientEncryptToken) != 0)
                return false;

            if (!_networkEncryption.RemoveConnectionInitHandler(ProcessConnectionInit))
                return false;

            _playerData.FreePlayerData(_pdKey);

            return true;
        }

        #endregion

        #region IEncrypt members

        int IEncrypt.Encrypt(Player p, Span<byte> data, int len)
        {
            if (!p.TryGetExtraData(_pdKey, out EncData pd))
                return len;

            return pd.Encrypt(data, len);
        }

        int IEncrypt.Decrypt(Player p, Span<byte> data, int len)
        {
            if (!p.TryGetExtraData(_pdKey, out EncData pd))
                return len;

            return pd.Decrypt(data, len);
        }

        void IEncrypt.Void(Player p)
        {
            if (!p.TryGetExtraData(_pdKey, out EncData pd))
                return;

            pd.Reset();
        }

        #endregion

        #region IClientEncrypt members

        void IClientEncrypt.Initialze(ClientConnection cc)
        {
            cc?.TryAddExtraData(new EncData());
        }

        int IClientEncrypt.Encrypt(ClientConnection cc, Span<byte> data, int len)
        {
            if (cc == null || !cc.TryGetExtraData(out EncData ed) || ed == null)
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

        int IClientEncrypt.Decrypt(ClientConnection cc, Span<byte> data, int len)
        {
            if (cc == null || !cc.TryGetExtraData(out EncData ed) || ed == null)
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

        void IClientEncrypt.Void(ClientConnection cc)
        {
            cc?.TryRemoveExtraData(out EncData _);
        }

        #endregion

        private bool ProcessConnectionInit(IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld)
        {
            if (len != ConnectionInitPacket.Length)
                return false;

            ref ConnectionInitPacket packet = ref MemoryMarshal.AsRef<ConnectionInitPacket>(buffer);

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

            Player p = _networkEncryption.NewConnection(clientType, remoteEndpoint, InterfaceIdentifier, ld);

            if (p == null)
            {
                // no slots left?
                Span<byte> disconnect = stackalloc byte[] { 0x00, 0x07 };
                _networkEncryption.ReallyRawSend(remoteEndpoint, disconnect, ld);
                return true;
            }

            if (!p.TryGetExtraData(_pdKey, out EncData pd))
                return false; // should not happen, sanity

            int key = -packet.Key;

            // initialize encryption state for the player
            pd.Init(key);

            // send the response
            ConnectionInitResponsePacket response = new(key);
            _networkEncryption.ReallyRawSend(remoteEndpoint, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref response, 1)), ld);

            return true;
        }

        private sealed class EncData : IPooledExtraData, IDisposable
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

                    int work = _key.Value;
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

                    int work = _key.Value;
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

            public void Dispose()
            {
                _rwLock.Dispose();
            }
        }
    }
}
