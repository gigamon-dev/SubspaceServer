using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Packets.Peer;
using SS.Utilities;
using SS.Utilities.Collections;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Hashing;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static SS.Core.ComponentInterfaces.IPeer;
using PeerSettings = SS.Core.ConfigHelp.Constants.Global.Peer0;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality to communicate with other "peer" zones to send and/or receive:
    /// <list type="bullet">
    /// <item>Player lists (list of arenas and players in each)</item>
    /// <item>Player counts (overall for the zone)</item>
    /// <item>Zone messages and alert (moderator) messages</item>
    /// <item>The ability to redirect a player to a peer's arena.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The original implementation for ASSS was written by Sharvil Nanavati (Snrrrub) in C++.
    /// JoWie rewrote it into a C module, and this is based on JoWie's C module.
    /// </remarks>
    [CoreModuleInfo]
    public class Peer : IModule, IPeer, IStringBuilderPoolProvider
    {
        private static readonly TimeSpan StaleArenaTimeout = TimeSpan.FromSeconds(30);

        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly INetwork _network;
        private readonly IRawNetwork _rawNetwork;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IRedirect _redirect;

        private ArenaDataKey<ArenaData> _arenaDataKey;
        private InterfaceRegistrationToken<IPeer>? _iPeerToken;
        private uint _nextArenaId = 1;
        private readonly ReaderWriterLockSlim _rwLock = new();

        // ASSS uses linked lists which could have been done here too.
        // However, to expose it as a read only collection of IPeerZone (which is immutable), I've decided to use a List.
        // This is so that it can be indexed on with IReadOnlyList<IPeerZone>, whereas enumerating would require allocations to box the enumerator.
        // I am taking this approach for all exposed lists, and I think that should be ok performance-wise since:
        // - there will not be many peer zones (and peer zones are not removed anyway, the list is based on what's configured)
        // - each zone won't have too many arenas (though they do get removed)
        private readonly List<PeerZone> _peers = [];
        private readonly ReadOnlyCollection<PeerZone> _readOnlyPeers;

        private readonly Dictionary<SocketAddress, PeerZone> _peerDictionary = [];

        private readonly DefaultObjectPool<PeerArena> _peerArenaPool = new(new DefaultPooledObjectPolicy<PeerArena>(), Constants.TargetArenaCount);
        private readonly DefaultObjectPool<PeerArenaName> _peerArenaNamePool = new(new DefaultPooledObjectPolicy<PeerArenaName>(), Constants.TargetArenaCount);

        public Peer(
            IArenaManager arenaManager,
            IChat chat,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            INetwork network,
            IRawNetwork rawNetwork,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IRedirect redirect)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _rawNetwork = rawNetwork ?? throw new ArgumentNullException(nameof(rawNetwork));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _redirect = redirect ?? throw new ArgumentNullException(nameof(redirect));

            _readOnlyPeers = _peers.AsReadOnly();
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _arenaDataKey = _arenaManager.AllocateArenaData<ArenaData>();
            _rawNetwork.RegisterPeerPacketHandler(ProcessPeerPacket);
            _iPeerToken = broker.RegisterInterface<IPeer>(this);

            ReadConfig();

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            broker.UnregisterInterface(ref _iPeerToken);
            _rawNetwork.UnregisterPeerPacketHandler(ProcessPeerPacket);
            _arenaManager.FreeArenaData(ref _arenaDataKey);
            return true;
        }

        #endregion

        #region IPeer

        int IPeer.GetPopulationSummary()
        {
            _rwLock.EnterReadLock();
            try
            {
                int total = 0;

                foreach (PeerZone peerZone in _peers)
                {
                    if (!peerZone.Config.IncludeInPopulation)
                    {
                        continue;
                    }

                    if (peerZone.PlayerCount >= 0) // player count packet
                    {
                        total += peerZone.PlayerCount;
                    }
                    else // player list packet
                    {
                        foreach (PeerArena peerArena in peerZone.Arenas)
                        {
                            total += peerArena.Players.Count;
                        }
                    }
                }

                return total;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        bool IPeer.FindPlayer(ReadOnlySpan<char> findName, ref int score, StringBuilder name, StringBuilder arena)
        {
            if (findName.IsEmpty)
                return false;

            if (name is null)
                throw new ArgumentNullException(nameof(name));

            if (arena is null)
                throw new ArgumentNullException(nameof(arena));

            bool hasMatch = false;

            StringBuilder bestPlayerName = _objectPoolManager.StringBuilderPool.Get();
            _rwLock.EnterReadLock();
            try
            {
                PeerArena? bestPeerArena = null;

                foreach (PeerZone peerZone in _peers)
                {
                    foreach (PeerArena peerArena in peerZone.Arenas)
                    {
                        if (!peerArena.IsConfigured)
                        {
                            continue;
                        }

                        foreach (ReadOnlyMemory<char> playerName in peerArena.Players)
                        {
                            ReadOnlySpan<char> playerNameSpan = playerName.Span;
                            if (playerNameSpan.Equals(findName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Exact match.
                                name.Clear();
                                name.Append(playerNameSpan);

                                arena.Clear();
                                arena.Append(peerArena.Name!.LocalName);

                                score = -1;
                                return true;
                            }

                            int index = playerNameSpan.IndexOf(findName, StringComparison.OrdinalIgnoreCase);
                            if (index != -1 && index < score)
                            {
                                bestPlayerName.Clear();
                                bestPlayerName.Append(playerName);
                                bestPeerArena = peerArena;
                                score = index;
                                hasMatch = true;
                            }
                        }
                    }
                }

                if (bestPlayerName.Length > 0)
                {
                    name.Clear();
                    name.Append(bestPlayerName);
                }

                if (bestPeerArena is not null)
                {
                    arena.Clear();
                    arena.Append(bestPeerArena.Name!.LocalName);
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
                _objectPoolManager.StringBuilderPool.Return(bestPlayerName);
            }

            return hasMatch;
        }

        bool IPeer.ArenaRequest(Player player, short arenaType, ReadOnlySpan<char> arenaName)
        {
            _rwLock.EnterReadLock();
            try
            {
                foreach (PeerZone peerZone in _peers)
                {
                    if (HasArenaConfigured(peerZone, arenaName))
                    {
                        PeerArenaName? name = FindPeerArenaRename(peerZone, arenaName, false);
                        ReadOnlySpan<char> targetArena = name is not null ? name.RemoteName : arenaName;
                        if (targetArena.Equals("0", StringComparison.OrdinalIgnoreCase))
                        {
                            // Workaround for subgame.
                            targetArena = "";
                        }
                        _logManager.LogP(LogLevel.Info, nameof(Peer), player, $"Redirecting to {peerZone.Config.IPEndPoint} : {targetArena}");
                        return _redirect.RawRedirect(player, peerZone.Config.IPEndPoint, arenaType, targetArena);
                    }
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            return false;
        }

        void IPeer.SendZoneMessage([InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IPeer)this).SendZoneMessage(handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IPeer.SendZoneMessage(StringBuilder message)
        {
            Span<char> messageSpan = stackalloc char[Math.Min(250, message.Length)];
            message.CopyTo(0, messageSpan, messageSpan.Length);
            ((IPeer)this).SendZoneMessage(messageSpan);
        }

        void IPeer.SendZoneMessage(ReadOnlySpan<char> message)
        {
            SendMessageToPeer(PeerPacketType.Chat, 0x00, message);
        }

        void IPeer.SendAlertMessage(ReadOnlySpan<char> alertName, ReadOnlySpan<char> playerName, ReadOnlySpan<char> arenaName, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IPeer)this).SendAlertMessage(alertName, playerName, arenaName, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IPeer.SendAlertMessage(ReadOnlySpan<char> alertName, ReadOnlySpan<char> playerName, ReadOnlySpan<char> arenaName, StringBuilder message)
        {
            Span<char> messageSpan = stackalloc char[Math.Min(250, message.Length)];
            message.CopyTo(0, messageSpan, messageSpan.Length);
            ((IPeer)this).SendAlertMessage(alertName, playerName, arenaName, messageSpan);
        }

        void IPeer.SendAlertMessage(ReadOnlySpan<char> alertName, ReadOnlySpan<char> playerName, ReadOnlySpan<char> arenaName, ReadOnlySpan<char> message)
        {
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
            try
            {
                sb.Append($"{alertName}: ({playerName}) ({(int.TryParse(arenaName, out _) ? "Public " : "")}: {message}");

                Span<char> messageSpan = stackalloc char[Math.Min(250, message.Length)];
                sb.CopyTo(0, messageSpan, messageSpan.Length);
                SendMessageToPeer(PeerPacketType.Op, 0x00, message);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        void IPeer.Lock()
        {
            _rwLock.EnterReadLock();
        }

        void IPeer.Unlock()
        {
            _rwLock.ExitReadLock();
        }

        IPeerZone? IPeer.FindZone(IPEndPoint endpoint)
        {
            if (!_rwLock.IsReadLockHeld)
                throw new InvalidOperationException($"{nameof(IPeer)}.{nameof(IPeer.Lock)} was not called.");

            SocketAddress address = endpoint.Serialize();
            return FindZone(address);
        }

        IPeerZone? IPeer.FindZone(SocketAddress address)
        {
            if (!_rwLock.IsReadLockHeld)
                throw new InvalidOperationException($"{nameof(IPeer)}.{nameof(IPeer.Lock)} was not called.");

            return FindZone(address);
        }

        IPeerArena? IPeer.FindArena(ReadOnlySpan<char> arenaName, bool remote)
        {
            if (!_rwLock.IsReadLockHeld)
                throw new InvalidOperationException($"{nameof(IPeer)}.{nameof(IPeer.Lock)} was not called.");

            foreach (PeerZone peerZone in _peers)
            {
                foreach (PeerArena peerArena in peerZone.Arenas)
                {
                    if (arenaName.Equals(remote ? peerArena.Name!.RemoteName : peerArena.Name!.LocalName, StringComparison.OrdinalIgnoreCase))
                    {
                        return peerArena;
                    }
                }
            }

            return null;
        }

        IReadOnlyList<IPeerZone> IPeer.Peers
        {
            get
            {
                if (!_rwLock.IsReadLockHeld)
                    throw new InvalidOperationException($"{nameof(IPeer)}.{nameof(IPeer.Lock)} was not called.");

                return _readOnlyPeers;
            }
        }

        #endregion

        #region IStringBuilderPoolProvider

        // This is to support the IPeer.SendMessageInterpolatedStringHandler.
        ObjectPool<StringBuilder> IStringBuilderPoolProvider.StringBuilderPool => _objectPoolManager.StringBuilderPool;

        #endregion IStringBuilderPoolProvider

        #region Timers

        private bool MainloopTimer_PeriodicUpdate()
        {
            ListenData? listenData = null;
            if (_network.Listening.Count > 0)
            {
                listenData = _network.Listening[0];
            }

            if (listenData is null)
            {
                // Try again later.
                return true;
            }

            _rwLock.EnterReadLock();
            try
            {
                foreach (PeerZone peerZone in _peers)
                {
                    if (peerZone.Config.SendPlayerList)
                    {
                        SendPlayerList(peerZone, listenData);
                    }
                    else
                    {
                        SendPlayerCount(peerZone, listenData);
                    }
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            return true;

            void SendPlayerList(PeerZone peerZone, ListenData listenData)
            {
                if (peerZone is null || listenData is null)
                    return;

                peerZone.PlayerListBuffer ??= new byte[512]; // initial size

                int pos = PeerPacketHeader.Length;

                _arenaManager.Lock();

                try
                {
                    foreach (Arena arena in _arenaManager.Arenas)
                    {
                        if (!arena.TryGetExtraData(_arenaDataKey, out ArenaData? arenaData))
                            continue;

                        if (arenaData.Id == 0)
                        {
                            arenaData.Id = _nextArenaId++;
                        }

                        // Arena id
                        AppendArenaIdToBuffer(ref peerZone.PlayerListBuffer, ref pos, arenaData.Id);

                        // Arena name
                        AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, arena.Name);
                        AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, "\0");

                        // Players
                        if (HasArenaConfiguredAsDummy(peerZone, arena.Name))
                        {
                            // Dummy player
                            AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, ":"); // : is not allowed in real player names
                            AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, arena.Name); // use the arena name as the player name to ensure it is unique
                            AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, "\0");
                        }
                        else
                        {
                            _playerData.Lock();
                            try
                            {
                                foreach (Player player in _playerData.Players)
                                {
                                    if (player.Arena != arena)
                                        continue;

                                    AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, player.Name);
                                    AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, "\0");
                                }
                            }
                            finally
                            {
                                _playerData.Unlock();
                            }
                        }

                        // End arena indicator
                        AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, "\0");
                    }
                }
                finally
                {
                    _arenaManager.Unlock();
                }

                foreach (PeerZone otherPeerZone in _peers)
                {
                    if (otherPeerZone == peerZone)
                    {
                        continue;
                    }

                    foreach (PeerArena otherPeerArena in otherPeerZone.Arenas)
                    {
                        if (!otherPeerArena.IsRelay)
                        {
                            continue;
                        }

                        // Arena id
                        AppendArenaIdToBuffer(ref peerZone.PlayerListBuffer, ref pos, otherPeerArena.LocalId);

                        // Arena name
                        AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, otherPeerArena.Name!.LocalName);
                        AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, "\0");

                        // Players
                        if (HasArenaConfiguredAsDummy(peerZone, otherPeerArena.Name.LocalName))
                        {
                            // Dummy player
                            AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, ":");
                            AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, otherPeerArena.Name!.LocalName);
                            AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, "\0");
                        }
                        else
                        {
                            foreach (ReadOnlyMemory<char> playerName in otherPeerArena.Players)
                            {
                                AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, playerName.Span);
                                AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, "\0");
                            }
                        }

                        // End arena indicator
                        AppendToBuffer(ref peerZone.PlayerListBuffer, ref pos, "\0");
                    }
                }

                ref PeerPacketHeader header = ref MemoryMarshal.AsRef<PeerPacketHeader>(peerZone.PlayerListBuffer);
                header = new(peerZone.Config.PasswordHash, PeerPacketType.PlayerList, ServerTick.Now);

                _rawNetwork.ReallyRawSend(peerZone.Config.SocketAddress, peerZone.PlayerListBuffer.AsSpan(0, pos), listenData);

                static void AppendArenaIdToBuffer(ref byte[] buffer, ref int pos, uint arenaId)
                {
                    while (buffer.Length < pos + 4)
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), arenaId);
                    pos += 4;
                }

                static void AppendToBuffer(ref byte[] buffer, ref int pos, ReadOnlySpan<char> text)
                {
                    int byteCount = StringUtils.DefaultEncoding.GetByteCount(text);

                    while (buffer.Length < pos + byteCount)
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }

                    pos += StringUtils.DefaultEncoding.GetBytes(text, buffer.AsSpan(pos));
                }
            }

            void SendPlayerCount(PeerZone peerZone, ListenData listenData)
            {
                if (peerZone is null || listenData is null)
                    return;

                Span<byte> packet = stackalloc byte[PeerPacketHeader.Length + 2];
                ref PeerPacketHeader header = ref MemoryMarshal.AsRef<PeerPacketHeader>(packet);
                header = new(peerZone.Config.PasswordHash, PeerPacketType.PlayerCount, ServerTick.Now);

                if (peerZone.Config.SendZeroPlayerCount)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(packet[PeerPacketHeader.Length..], 0);
                }
                else
                {
                    _arenaManager.GetPopulationSummary(out int total, out _);
                    BinaryPrimitives.WriteUInt16LittleEndian(packet[PeerPacketHeader.Length..], (ushort)total);
                }

                _rawNetwork.ReallyRawSend(peerZone.Config.SocketAddress, packet, listenData);
            }
        }

        private bool MainloopTimer_RemoveStaleArenas()
        {
            DateTime now = DateTime.UtcNow;

            _rwLock.EnterUpgradeableReadLock();
            try
            {
                foreach (PeerZone peerZone in _peers)
                {
                    for (int i = peerZone.Arenas.Count - 1; i >= 0; i--)
                    {
                        PeerArena peerArena = peerZone.Arenas[i];
                        if ((now - peerArena.LastUpdate) > StaleArenaTimeout)
                        {
                            _rwLock.EnterWriteLock();
                            try
                            {
                                peerZone.Arenas.RemoveAt(i);
                                peerZone.ArenaLookup.Remove(peerArena.Name!.RemoteName, out _);
                            }
                            finally
                            {
                                _rwLock.ExitWriteLock();
                            }

                            CleanupPeerArena(peerArena);
                        }
                    }
                }
            }
            finally
            {
                _rwLock.ExitUpgradeableReadLock();
            }

            return true;
        }

        #endregion

        bool ProcessPeerPacket(SocketAddress remoteAddress, ReadOnlySpan<byte> data)
        {
            if (data.Length < PeerPacketHeader.Length || data[0] != 0x00 || data[1] != 0x01 || data[6] != 0x0FF)
            {
                // Not the peer protocol.
                return false;
            }

            ref readonly PeerPacketHeader packetHeader = ref MemoryMarshal.AsRef<PeerPacketHeader>(data);

            _rwLock.EnterWriteLock();

            try
            {
                PeerZone? peerZone = FindZone(remoteAddress);
                if (peerZone is null)
                {
                    _logManager.LogM(LogLevel.Drivel, nameof(Peer), $"Received something that looks like peer data from {remoteAddress}, but this address has not been configured.");
                    return false;
                }

                if (peerZone.Config.PasswordHash != packetHeader.Password)
                {
                    _logManager.LogM(LogLevel.Drivel, nameof(Peer), $"Received something that looks like peer data from {remoteAddress}, but the password is incorrect.");
                    return false;
                }

                if (peerZone.Config.SendOnly)
                {
                    _logManager.LogM(LogLevel.Drivel, nameof(Peer), $"Received something that looks like peer data from {remoteAddress}, but this peer is configured as SendOnly.");
                    return false;
                }

                // TODO: Review this timestamp logic. Why not just check if it's newer?
                if (peerZone.Timestamps[packetHeader.Timestamp & 0xFF] == packetHeader.Timestamp)
                {
                    // Already received this packet.
                    return true;
                }

                peerZone.Timestamps[packetHeader.Timestamp & 0xFF] = packetHeader.Timestamp;

                ReadOnlySpan<byte> payload = data[PeerPacketHeader.Length..];

                switch (packetHeader.Type)
                {
                    case PeerPacketType.PlayerList:
                        HandlePlayerList(peerZone, payload);
                        break;

                    case PeerPacketType.Chat:
                    case PeerPacketType.Op:
                        HandleMessage(peerZone, packetHeader.Type, payload);
                        break;

                    case PeerPacketType.PlayerCount:
                        HandlePlayerCount(peerZone, payload);
                        break;
                }

                return true;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            void HandlePlayerList(PeerZone peerZone, ReadOnlySpan<byte> payload)
            {
                /* Format:
                 * [
                 *     <arena id (4 bytes)>
                 *     <arena name (null terminated)>
                 *     [
                 *         <player name (null terminated)>
                 *     ] (0 or more)
                 *     <0x00 (which marks the end of an arena)>
                 * ] (1 or more)
                 */

                DateTime now = DateTime.UtcNow;

                peerZone.PlayerCount = -1;

                Span<char> arenaNameBuffer = stackalloc char[Constants.MaxArenaNameLength];
                Span<char> playerNameBuffer = stackalloc char[Constants.MaxPlayerNameLength];

                while (payload.Length > 5)
                {
                    //
                    // Arena ID
                    //

                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    payload = payload[4..];

                    //
                    // Arena Name
                    //

                    ReadOnlySpan<byte> remoteNameBytes = payload.SliceNullTerminated();
                    if (remoteNameBytes.IsEmpty)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(Peer), $"Zone peer {peerZone.Config.IPEndPoint} sent us a player list with an empty arena name.");
                        break;
                    }
                    else if (remoteNameBytes.Length + 1 > payload.Length || payload[remoteNameBytes.Length] != 0x00)
                    {
                        // not null terminated
                        _logManager.LogM(LogLevel.Warn, nameof(Peer), $"Zone peer {peerZone.Config.IPEndPoint} sent us a player list with an unterminated arena name.");
                        break;
                    }

                    payload = payload[(remoteNameBytes.Length + 1)..];

                    int charCount = StringUtils.DefaultEncoding.GetCharCount(remoteNameBytes);
                    if (charCount > Constants.MaxArenaNameLength)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(Peer), $"Zone peer {peerZone.Config.IPEndPoint} sent us a player list with an arena name longer than {Constants.MaxArenaNameLength} characters.");
                        break;
                    }

                    int decodedCharCount = StringUtils.DefaultEncoding.GetChars(remoteNameBytes, arenaNameBuffer);
                    Debug.Assert(charCount == decodedCharCount);
                    Span<char> remoteName = arenaNameBuffer[..charCount];

                    // Make sure the remote name is all lower case.
                    // This ensures proper sorting in Continuum.
                    // Note: renamed local arena names are kept in the character casing specified in the config (PeerX:RenameArenas).
                    for (int i = 0; i < remoteName.Length; i++)
                    {
                        if (char.IsUpper(remoteName[i]))
                            remoteName[i] = char.ToLowerInvariant(remoteName[i]);
                    }

                    // If this arena as a rename target (the bar in foo=bar), ignore it.
                    // Except if the rename is just a difference in the character case (foo=FOO).
                    PeerArenaName? rename = FindPeerArenaRename(peerZone, remoteName, false);
                    if (rename is not null && !rename.IsCaseChange)
                    {
                        while (payload.Length > 0)
                        {
                            if (payload[0] == 0x00)
                            {
                                payload = payload[1..];
                                break; // end of player list
                            }

                            ReadOnlySpan<byte> playerNameBytes = payload.SliceNullTerminated();
                            if (playerNameBytes.Length + 1 > payload.Length || payload[playerNameBytes.Length] != 0x00)
                            {
                                // not null terminated
                                _logManager.LogM(LogLevel.Warn, nameof(Peer), $"Zone peer {peerZone.Config.IPEndPoint} sent us a player list with an unterminated arena name.");
                                break;
                            }

                            payload = payload[(playerNameBytes.Length + 1)..];
                        }

                        // Handle the next arena.
                        continue;
                    }

                    if (!peerZone.ArenaLookup.TryGetValue(remoteName, out PeerArena? peerArena))
                    {
                        peerArena = _peerArenaPool.Get();
                        peerArena.LocalId = _nextArenaId++;
                        peerArena.Name = _peerArenaNamePool.Get();

                        PeerArenaName? name = FindPeerArenaRename(peerZone, remoteName, true);
                        if (name is not null)
                        {
                            peerArena.Name.SetNames(name.RemoteName, name.LocalName);
                        }
                        else
                        {
                            peerArena.Name.SetNames(remoteName, remoteName);
                        }

                        peerZone.Arenas.Add(peerArena);
                        peerZone.ArenaLookup.TryAdd(remoteName, peerArena);
                    }

                    peerArena.Id = id;
                    peerArena.IsConfigured = HasArenaConfigured(peerZone, peerArena.Name!.LocalName);
                    peerArena.IsRelay = HasArenaConfiguredAsRelay(peerZone, peerArena.Name!.LocalName);
                    peerArena.LastUpdate = now;
                    peerArena.Players.Clear();

                    //
                    // Player list
                    //

                    while (payload.Length > 0)
                    {
                        if (payload[0] == 0x00)
                        {
                            payload = payload[1..];
                            break; // end of player list
                        }

                        ReadOnlySpan<byte> playerNameBytes = payload.SliceNullTerminated();
                        if (playerNameBytes.Length + 1 > payload.Length || payload[playerNameBytes.Length] != 0x00)
                        {
                            // not null terminated
                            _logManager.LogM(LogLevel.Warn, nameof(Peer), $"Zone peer {peerZone.Config.IPEndPoint} sent us a player list with an unterminated arena name.");
                            break;
                        }

                        payload = payload[(playerNameBytes.Length + 1)..];

                        charCount = StringUtils.DefaultEncoding.GetCharCount(playerNameBytes);
                        if (charCount > Constants.MaxPlayerNameLength)
                        {
                            _logManager.LogM(LogLevel.Warn, nameof(Peer), $"Zone peer {peerZone.Config.IPEndPoint} sent us a player list with a player name longer than {Constants.MaxPlayerNameLength} characters.");
                            break;
                        }

                        decodedCharCount = StringUtils.DefaultEncoding.GetChars(playerNameBytes, playerNameBuffer);
                        Debug.Assert(charCount == decodedCharCount);
                        Span<char> playerNameChars = playerNameBuffer[..charCount];
                        peerArena.Players.Add(playerNameChars);
                    }
                }
            }

            void HandleMessage(PeerZone peerZone, PeerPacketType packetType, ReadOnlySpan<byte> payload)
            {
                /* Format:
                 * <peer packet header (12 bytes)>
                 * <message type (1 byte)>
                 * <message (up to max chat message length, must be null terminated)>
                 */

                if (payload.Length < 2 || !peerZone.Config.ReceiveMessages)
                {
                    return;
                }

                byte messageType = payload[0];
                payload = payload[1..].SliceNullTerminated();
                if (payload.IsEmpty)
                    return;

                if (payload.Length > ChatPacket.MaxMessageBytes - 1) // -1 for the null terminator
                    payload = payload[..(ChatPacket.MaxMessageBytes - 1)];

                Span<char> text = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(payload)];
                if (text.IsEmpty)
                    return;

                if (StringUtils.DefaultEncoding.GetChars(payload, text) != text.Length)
                    return;

                switch (packetType)
                {
                    case PeerPacketType.Chat: // zone
                        _logManager.LogM(LogLevel.Info, nameof(Peer), $"Zone peer {peerZone.Config.IPEndPoint} sent us a zone message (type={messageType}): {text}");
                        _chat.SendArenaMessage(null, text);
                        break;

                    case PeerPacketType.Op: // alert
                        _logManager.LogM(LogLevel.Info, nameof(Peer), $"Zone peer {peerZone.Config.IPEndPoint} sent us an alert message (type={messageType}): {text}");
                        _chat.SendModMessage(text);
                        break;
                }
            }

            void HandlePlayerCount(PeerZone peerZone, ReadOnlySpan<byte> payload)
            {
                if (payload.Length < 2)
                {
                    return;
                }

                foreach (PeerArena peerArena in peerZone.Arenas)
                {
                    CleanupPeerArena(peerArena);
                }
                peerZone.Arenas.Clear();
                peerZone.ArenaLookup.Clear();

                peerZone.PlayerCount = BinaryPrimitives.ReadInt16LittleEndian(payload);
            }
        }

        [ConfigHelp("Peer0", "Address", ConfigScope.Global,
            Description = "Send and receive peer packets to/from this IP address.")]
        [ConfigHelp<int>("Peer0", "Port", ConfigScope.Global, Default = 0,
            Description = "Send and receive peer packets to/from this UDP port.")]
        [ConfigHelp("Peer0", "Password", ConfigScope.Global,
            Description = "Peers must agree upon a common password.")]
        [ConfigHelp<bool>("Peer0", "SendOnly", ConfigScope.Global, Default = false,
            Description = "If set, we send data to our peer but we reject any that we might receive.")]
        [ConfigHelp<bool>("Peer0", "SendPlayerList", ConfigScope.Global, Default = true,
            Description = "If set, send a full arena and player list to the peer. Otherwise only send a summary of our population.")]
        [ConfigHelp<bool>("Peer0", "SendZeroPlayerCount", ConfigScope.Global, Default = false,
            Description = "If set and SendPlayerList is not set, always send a population count of 0.")]
        [ConfigHelp<bool>("Peer0", "SendMessages", ConfigScope.Global, Default = true,
            Description = "If set, forward alert and zone (?z) messages to the peer.")]
        [ConfigHelp<bool>("Peer0", "ReceiveMessages", ConfigScope.Global, Default = true,
            Description = "If set, display the zone (*zone) and alert messages from this peer.")]
        [ConfigHelp<bool>("Peer0", "IncludeInPopulation", ConfigScope.Global, Default = true,
            Description = "If set, include the population count of this peer in the ping protocol.")]
        [ConfigHelp<bool>("Peer0", "ProvidesDefaultArenas", ConfigScope.Global, Default = false,
            Description = "If set, any arena that would normally end up as (default) will be redirected to this peer zone.")]
        [ConfigHelp("Peer0", "Arenas", ConfigScope.Global,
            Description = """
                A list of arena's that belong to the peer. This server will redirect players that try to ?go to
                this arena. These arena's will also be used for ?find and will be shown in ?arena. If you are also
                using Peer0:RenameArenas, you should put the local arena name here; this is the one you would see
                in the ?arena list if you are in this zone.
                """)]
        [ConfigHelp("Peer0", "SendDummyArenas", ConfigScope.Global,
            Description = """
                A list of arena's that we send to the peer with a single dummy player. Instead of the full
                player list.This will keep the arena in the arena list of the peer with a fixed count of 1.
                """)]
        [ConfigHelp("Peer0", "RelayArenas", ConfigScope.Global,
            Description = "A list of arena's of this peer that will be relayed to other peers.")]
        [ConfigHelp("Peer0", "RenameArenas", ConfigScope.Global,
            Description = """
                A list of arena's that belong to the peer which should be renamed to a different name locally.
                For example `foo = bar, 0 = twpublic` will display the remote `foo` arena as `bar` instead.
                """)]
        private void ReadConfig()
        {
            _mainloopTimer.ClearTimer(MainloopTimer_PeriodicUpdate, null);
            _mainloopTimer.ClearTimer(MainloopTimer_RemoveStaleArenas, null);

            _rwLock.EnterWriteLock();

            try
            {
                foreach (PeerZone peerZone in _peers)
                    CleanupPeerZone(peerZone);

                _peers.Clear();

                Span<char> sectionSpan = stackalloc char[7];
                for (int i = 0; i < 255; i++)
                {
                    if (!sectionSpan.TryWrite($"Peer{i}", out int charsWritten))
                        continue;

                    ReadOnlySpan<char> peerSection = sectionSpan[..charsWritten];

                    string? addressStr = _configManager.GetStr(_configManager.Global, peerSection, "Address");
                    if (string.IsNullOrWhiteSpace(addressStr))
                        break;

                    if (!IPAddress.TryParse(addressStr, out IPAddress? address))
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(Peer), $"Invalid valid for {peerSection}:Address.");
                        continue;
                    }

                    ushort port = (ushort)_configManager.GetInt(_configManager.Global, peerSection, "Port", PeerSettings.Port.Default);
                    if (port == 0)
                        break;

                    uint hash = 0xDEADBEEF;
                    string? password = _configManager.GetStr(_configManager.Global, peerSection, "Password");
                    if (!string.IsNullOrWhiteSpace(password))
                    {
                        hash = GetHash(password);
                    }

                    PeerZone peerZone = new(
                        new PeerZoneConfig(new IPEndPoint(address, port))
                        {
                            Id = i,
                            PasswordHash = hash,
                            SendOnly = _configManager.GetBool(_configManager.Global, peerSection, "SendOnly", PeerSettings.SendOnly.Default),
                            SendPlayerList = _configManager.GetBool(_configManager.Global, peerSection, "SendPlayerList", PeerSettings.SendPlayerList.Default),
                            SendZeroPlayerCount = _configManager.GetBool(_configManager.Global, peerSection, "SendZeroPlayerCount", PeerSettings.SendZeroPlayerCount.Default),
                            SendMessages = _configManager.GetBool(_configManager.Global, peerSection, "SendMessages", PeerSettings.SendMessages.Default),
                            ReceiveMessages = _configManager.GetBool(_configManager.Global, peerSection, "ReceiveMessages", PeerSettings.ReceiveMessages.Default),
                            IncludeInPopulation = _configManager.GetBool(_configManager.Global, peerSection, "IncludeInPopulation", PeerSettings.IncludeInPopulation.Default),
                            ProvideDefaultArenas = _configManager.GetBool(_configManager.Global, peerSection, "ProvidesDefaultArenas", PeerSettings.ProvidesDefaultArenas.Default),
                        });

                    string? arenas = _configManager.GetStr(_configManager.Global, peerSection, "Arenas");
                    if (!string.IsNullOrWhiteSpace(arenas))
                    {
                        ReadOnlySpan<char> remaining = arenas;
                        ReadOnlySpan<char> token;
                        while (!(token = remaining.GetToken(" \t:;,", out remaining)).IsEmpty)
                        {
                            peerZone.Config.Arenas.Add(token.ToString());
                        }
                    }

                    string? sendDummyArenas = _configManager.GetStr(_configManager.Global, peerSection, "SendDummyArenas");
                    if (!string.IsNullOrWhiteSpace(sendDummyArenas))
                    {
                        foreach (Range range in sendDummyArenas.AsSpan().SplitAny(' ', '\t', ':', ';', ','))
                        {
                            ReadOnlySpan<char> arenaName = sendDummyArenas[range].Trim();
                            if (arenaName.IsEmpty)
                                continue;

							peerZone.Config.SendDummyArenasLookup.Add(arenaName);
						}
                    }

                    string? relayArenas = _configManager.GetStr(_configManager.Global, peerSection, "RelayArenas");
                    if (!string.IsNullOrWhiteSpace(relayArenas))
                    {
						foreach (Range range in relayArenas.AsSpan().SplitAny(' ', '\t', ':', ';', ','))
						{
							ReadOnlySpan<char> arenaName = relayArenas[range].Trim();
							if (arenaName.IsEmpty)
								continue;

							peerZone.Config.RelayArenasLookup.Add(arenaName);
						}
                    }

                    string? renameArenas = _configManager.GetStr(_configManager.Global, peerSection, "RenameArenas");
                    if (!string.IsNullOrWhiteSpace(renameArenas))
                    {
                        ReadOnlySpan<char> remaining = renameArenas;
                        ReadOnlySpan<char> token;
                        while (!(token = remaining.GetToken(" \t:;,", out remaining)).IsEmpty)
                        {
                            ReadOnlySpan<char> remote;
                            if (!(remote = token.GetToken('=', out ReadOnlySpan<char> local)).IsEmpty)
                            {
                                local = local.TrimStart('=');

                                PeerArenaName peerArenaName = _peerArenaNamePool.Get();
                                peerArenaName.SetNames(remote, local);
                                peerZone.Config.RenamedArenas.Add(peerArenaName);
                            }
                        }
                    }

                    _peers.Add(peerZone);

                    if (!_peerDictionary.TryAdd(peerZone.Config.SocketAddress, peerZone))
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(Peer), $"Zone peer {i} at {peerZone.Config.IPEndPoint} is a duplicate. Check the config.");
                    }

                    _logManager.LogM(LogLevel.Info, nameof(Peer), $"Zone peer {i} at {peerZone.Config.IPEndPoint} (player {(peerZone.Config.SendPlayerList ? "list" : "count")})");
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            _mainloopTimer.SetTimer(MainloopTimer_PeriodicUpdate, 1000, 1000, null);
            _mainloopTimer.SetTimer(MainloopTimer_RemoveStaleArenas, 10000, 10000, null);


            static uint GetHash(string password)
            {
                int numBytes = StringUtils.DefaultEncoding.GetByteCount(password);
                byte[]? byteArray = null;
                Span<byte> byteSpan = numBytes <= 1024
                    ? stackalloc byte[numBytes]
                    : (byteArray = ArrayPool<byte>.Shared.Rent(StringUtils.DefaultEncoding.GetByteCount(password)));

                try
                {
                    numBytes = StringUtils.DefaultEncoding.GetBytes(password, byteSpan);
                    return ~Crc32.HashToUInt32(byteSpan[..numBytes]);
                }
                finally
                {
                    if (byteArray is not null)
                    {
                        ArrayPool<byte>.Shared.Return(byteArray);
                    }
                }
            }
        }

        private void CleanupPeerZone(PeerZone peerZone)
        {
            if (peerZone == null)
                return;

            peerZone.Config.Arenas.Clear();

            foreach (PeerArenaName peerArenaName in peerZone.Config.RenamedArenas)
            {
                _peerArenaNamePool.Return(peerArenaName);
            }
            peerZone.Config.RenamedArenas.Clear();

            peerZone.Config.SendDummyArenas.Clear();

            peerZone.Config.RelayArenas.Clear();

            foreach (PeerArena peerArena in peerZone.Arenas)
            {
                CleanupPeerArena(peerArena);
            }
            peerZone.Arenas.Clear();
            peerZone.ArenaLookup.Clear();

            peerZone.PlayerListBuffer = null;
        }

        private void CleanupPeerArena(PeerArena peerArena)
        {
            if (peerArena == null)
                return;

            if (peerArena.Name is not null)
            {
                _peerArenaNamePool.Return(peerArena.Name);
                peerArena.Name = null;
            }

            peerArena.Players.Clear();

            _peerArenaPool.Return(peerArena);
        }

        private PeerArenaName? FindPeerArenaRename(PeerZone peerZone, ReadOnlySpan<char> remoteName, bool remote)
        {
            foreach (PeerArenaName name in peerZone.Config.RenamedArenas)
            {
                if (remoteName.Equals(remote ? name.RemoteName : name.LocalName, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            return null;
        }

        private bool HasArenaConfigured(PeerZone peerZone, ReadOnlySpan<char> localName)
        {
            if (peerZone is null)
                return false;

            foreach (string configuredArenaName in peerZone.Config.Arenas)
            {
                if (localName.Equals(configuredArenaName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (peerZone.Config.ProvideDefaultArenas)
            {
                // Get the base arena name.
                while (localName.Length > 0 && char.IsAsciiDigit(localName[^1]))
                    localName = localName[..^1];

                if (localName.IsEmpty)
                {
                    localName = "(public)";
                }

                _arenaManager.Lock();

                try
                {
                    return !_arenaManager.KnownArenaNames.Contains(localName);
                }
                finally
                {
                    _arenaManager.Unlock();
                }
            }

            return false;
        }

        private bool HasArenaConfiguredAsDummy(PeerZone peerZone, ReadOnlySpan<char> localName)
        {
            if (peerZone is null)
                return false;

            return peerZone.Config.SendDummyArenasLookup.Contains(localName);
        }

        private bool HasArenaConfiguredAsRelay(PeerZone peerZone, ReadOnlySpan<char> localName)
        {
            if (peerZone is null)
                return false;

            return peerZone.Config.RelayArenasLookup.Contains(localName);
        }

        private PeerZone? FindZone(SocketAddress address)
        {
            if (address is null)
                return null;

            if (_peerDictionary.TryGetValue(address, out PeerZone? peerZone))
                return peerZone;
            else
                return null;
        }

        private void SendMessageToPeer(PeerPacketType packetType, byte messageType, ReadOnlySpan<char> message)
        {
            Span<byte> packet = stackalloc byte[PeerPacketHeader.Length + 1 + Math.Min(ChatPacket.MaxMessageBytes, message.Length + 1)];

            ref PeerPacketHeader header = ref MemoryMarshal.AsRef<PeerPacketHeader>(packet);

            // Fields of the header that are the same for all peer zones.
            header.T1 = 0x00;
            header.T2 = 0x01;
            header.T3 = 0xFF;
            header.Type = packetType;
            header.Timestamp = ServerTick.Now;

            // Message type
            Span<byte> payload = packet[PeerPacketHeader.Length..];
            payload[0] = messageType;
            payload = payload[1..];

            // The message
            int length = payload.WriteNullTerminatedString(message.TruncateForEncodedByteLimit(payload.Length - 1));

            Debug.Assert(PeerPacketHeader.Length + 1 + length == packet.Length);

            // Primary listen data.
            ListenData? listenData = _network.Listening.Count > 0 ? _network.Listening[0] : null;
            if (listenData is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(Peer), "Unable to send message. There are no listening sockets.");
                return;
            }

            _rwLock.EnterReadLock();

            try
            {
                // Send to each peer zone.
                foreach (PeerZone peerZone in _peers)
                {
                    if (!peerZone.Config.SendMessages)
                    {
                        continue;
                    }

                    // Field that is specific to a peer zone.
                    header.Password = peerZone.Config.PasswordHash;

                    _rawNetwork.ReallyRawSend(peerZone.Config.SocketAddress, packet, listenData);
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        #region Helper types

        private class PeerZone : IPeerZone
        {
            public PeerZoneConfig Config { get; }

            public int Id => Config.Id;

            public ReadOnlySpan<byte> IPAddress => Config.IPAddress;

            public ushort Port => Config.Port;

            public int PlayerCount { get; set; }

            public IReadOnlyList<string> ConfiguredArenas { get; }

            public readonly List<PeerArena> Arenas = new();
            private readonly ReadOnlyCollection<PeerArena> _readOnlyArenas;
            IReadOnlyList<IPeerArena> IPeerZone.Arenas => _readOnlyArenas;

            public readonly Trie<PeerArena> ArenaLookup = new(false);

            public readonly uint[] Timestamps = new uint[256];
            public byte[]? PlayerListBuffer;

            public PeerZone(PeerZoneConfig config)
            {
                Config = config ?? throw new ArgumentNullException(nameof(config));
                ConfiguredArenas = Config.Arenas.AsReadOnly();
                _readOnlyArenas = Arenas.AsReadOnly();
            }
        }

        private class PeerZoneConfig
        {
            /// <summary>
            /// Id in the config. E.g. [Peer3] is 3.
            /// </summary>
            public int Id { get; init; }

            /// <summary>
            /// Each peer zone is uniquely identified by its IP + port combination.
            /// </summary>
            public readonly IPEndPoint IPEndPoint;

            public readonly SocketAddress SocketAddress;

            private readonly byte[] _ipAddressBytes;
            public ReadOnlySpan<byte> IPAddress => _ipAddressBytes;

            public ushort Port { get; init; }

            /// <summary>
            /// Peers need to be configured with a matching password.
            /// This is the CRC32 of the password.
            /// </summary>
            public uint PasswordHash { get; set; }

            #region Config values

            public bool SendOnly { get; set; }
            public bool SendPlayerList { get; set; }
            public bool SendZeroPlayerCount { get; set; }
            public bool SendMessages { get; set; }
            public bool ReceiveMessages { get; set; }
            public bool IncludeInPopulation { get; set; }
            public bool ProvideDefaultArenas { get; set; }

            #endregion

            public readonly List<string> Arenas = new();
            public readonly List<PeerArenaName> RenamedArenas = new();
            public readonly HashSet<string> SendDummyArenas = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> SendDummyArenasLookup;
            public readonly HashSet<string> RelayArenas = new(StringComparer.OrdinalIgnoreCase);
			public readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> RelayArenasLookup;

			public PeerZoneConfig(IPEndPoint endPoint)
            {
                IPEndPoint = endPoint;
                SocketAddress = IPEndPoint.Serialize();
                _ipAddressBytes = IPEndPoint.Address.GetAddressBytes();
                Port = (ushort)IPEndPoint.Port;

                SendDummyArenasLookup = SendDummyArenas.GetAlternateLookup<ReadOnlySpan<char>>();
                RelayArenasLookup = RelayArenas.GetAlternateLookup<ReadOnlySpan<char>>();
            }
        }

        private class PeerArenaName : IPeerArenaName, IResettable
        {
            #region Remote

            private readonly char[] _remoteNameArray = new char[Constants.MaxArenaNameLength];
            private int _remoteLength = 0;
            public ReadOnlySpan<char> RemoteName => _remoteNameArray.AsSpan(0, _remoteLength);

            #endregion

            #region Local

            private readonly char[] _localNameArray = new char[Constants.MaxArenaNameLength];
            private int _localLength = 0;
            public ReadOnlySpan<char> LocalName => _localNameArray.AsSpan(0, _localLength);

            #endregion

            public bool IsCaseChange { get; private set; }

            public void SetNames(ReadOnlySpan<char> remoteName, ReadOnlySpan<char> localName)
            {
                if (remoteName.Length > Constants.MaxArenaNameLength)
                    throw new ArgumentOutOfRangeException(nameof(remoteName), $"Arena names can have a maximum of {Constants.MaxArenaNameLength} characters.");

                if (localName.Length > Constants.MaxArenaNameLength)
                    throw new ArgumentOutOfRangeException(nameof(localName), $"Arena names can have a maximum of {Constants.MaxArenaNameLength} characters.");

                remoteName.CopyTo(_remoteNameArray);
                _remoteLength = remoteName.Length;

                localName.CopyTo(_localNameArray);
                _localLength = localName.Length;

                IsCaseChange = LocalName.Equals(RemoteName, StringComparison.OrdinalIgnoreCase) && !LocalName.Equals(RemoteName, StringComparison.Ordinal);
            }

            bool IResettable.TryReset()
            {
                SetNames("", "");
                return true;
            }
        }

        private class PeerArena : IPeerArena, IResettable
        {
            public uint Id { get; set; }

            public uint LocalId { get; set; }

            public PeerArenaName? Name { get; set; }
            IPeerArenaName? IPeerArena.Name => Name;

            public bool IsConfigured { get; set; }

            public bool IsRelay { get; set; }

            public DateTime LastUpdate { get; set; }

            public readonly Trie Players = new(false);

            int IPeerArena.PlayerCount => Players.Count;

            bool IResettable.TryReset()
            {
                Id = 0;
                LocalId = 0;
                Name = null; // this should have already been cleared and object returned to the pool
                IsConfigured = false;
                IsRelay = false;
                LastUpdate = default;
                Players.Clear();

                return true;
            }
        }

        private class ArenaData : IResettable
        {
            public uint Id { get; set; } = 0;

            bool IResettable.TryReset()
            {
                Id = 0;
                return true;
            }
        }

        #endregion
    }
}
