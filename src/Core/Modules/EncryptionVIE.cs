using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace SS.Core.Modules
{
    public class EncryptionVIE : IModule, IEncrypt
    {
        private INetworkEncryption _networkEncryption;
        private IPlayerData _playerData;
        private const string IEncryptID = "enc-vie";
        private InterfaceRegistrationToken _iEncryptToken;

        private int _pdKey;

        #region Module methods

        public bool Load(
            ComponentBroker broker, 
            INetworkEncryption networkEncryption,
            IPlayerData playerData)
        {
            _networkEncryption = networkEncryption ?? throw new ArgumentNullException(nameof(networkEncryption));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _pdKey = playerData.AllocatePlayerData<PlayerData>();
            _networkEncryption.AppendConnectionInitHandler(ProcessConnectionInit);
            _iEncryptToken = broker.RegisterInterface<IEncrypt>(this, IEncryptID);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<IEncrypt>(ref _iEncryptToken) != 0)
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
            if (p[_pdKey] is not PlayerData pd)
                return len;

            return pd.Encrypt(data, len);
        }

        int IEncrypt.Decrypt(Player p, Span<byte> data, int len)
        {
            if (p[_pdKey] is not PlayerData pd)
                return data.Length;

            return pd.Decrypt(data, len);
        }

        void IEncrypt.Void(Player p)
        {
            if (p[_pdKey] is not PlayerData pd)
                return;

            pd.Reset();
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

            Player p = _networkEncryption.NewConnection(clientType, remoteEndpoint, IEncryptID, ld);

            if (p == null)
            {
                // no slots left?
                Span<byte> disconnect = stackalloc byte[] { 0x00, 0x07 };
                _networkEncryption.ReallyRawSend(remoteEndpoint, disconnect, ld);
                return true;
            }

            if (p[_pdKey] is not PlayerData pd)
                return false; // should not happen, sanity

            int key = -packet.Key;

            // initialize encryption state for the player
            pd.Init(key);

            // send the response
            ConnectionInitResponsePacket response = new(key);
            _networkEncryption.ReallyRawSend(remoteEndpoint, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref response, 1)), ld);

            return true;
        }

        private class PlayerData
        {
            private int? _key;
            private readonly byte[] _table = new byte[520];
            private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);

            public void Init(int key)
            {
                _rwLock.EnterWriteLock();

                try
                {
                    _key = key;

                    if (key == 0)
                        return;

                    Span<short> myTable = MemoryMarshal.Cast<byte, short>(_table);

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
                    if (_key == null)
                        return len;

                    int work = _key.Value;
                    if (work == 0)
                        return len;

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

                    Span<int> myTable = MemoryMarshal.Cast<byte, int>(_table);
                    Span<int> myData = MemoryMarshal.Cast<byte, int>(data);

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
                    if (_key == null)
                        return len;

                    int work = _key.Value;
                    if (work == 0)
                        return len;

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

                    Span<int> myTable = MemoryMarshal.Cast<byte, int>(_table);
                    Span<int> myData = MemoryMarshal.Cast<byte, int>(data);

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
                    _key = null;
                    Array.Clear(_table, 0, _table.Length);
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }
        }
    }
}
