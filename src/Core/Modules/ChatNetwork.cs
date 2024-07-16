using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using SS.Utilities.Collections;
using SS.Utilities.ObjectPool;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides the server functionality for the 'simple chat protocol'.
    /// </summary>
    [CoreModuleInfo]
    public class ChatNetwork : IModule, IChatNetwork, IStringBuilderPoolProvider, IDisposable
    {
        /// <summary>
        /// The maximum # of bytes to allow a message to be in the "simple chat protocol".
        /// </summary>
        private const int MaxMessageSize = 2035;

        private ComponentBroker _broker;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private InterfaceRegistrationToken<IChatNetwork> _iChatNetworkToken;

        private TimeSpan _messageDelay;
        private readonly TimeSpan _keepAliveTimeSpan = TimeSpan.FromMinutes(3);
        private PlayerDataKey<ClientData> _clientDataKey;
        private Socket _listenSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private Thread _chatThread;
        private readonly Trie<ChatMessageHandler> _handlerTrie = new(false);

        private static readonly DefaultObjectPool<LinkedListNode<OutBuffer>> s_outBufferLinkedListNodePool = new(new LinkedListNodePooledObjectPolicy<OutBuffer>(), Constants.TargetPlayerCount * 64);

        #region Module members

        [ConfigHelp("Net", "ChatMessageDelay", ConfigScope.Global, typeof(int), DefaultValue = "20",
            Description = """
            The delay between sending messages to clients using the
            text-based chat protocol. (To limit bandwidth used by
            non-playing cilents.)
            """)]
        public bool Load(
            ComponentBroker broker,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloop mainloop,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _messageDelay = TimeSpan.FromMilliseconds(_configManager.GetInt(_configManager.Global, "Net", "ChatMessageDelay", 20) * 10);

            _clientDataKey = _playerData.AllocatePlayerData<ClientData>();

            // Initialize the listening socket.
            if (!Initialize())
            {
                return false;
            }

            // Start the worker thread. It will be the only thread to use the listening socket, until it ends.
            _cancellationTokenSource = new();
            _chatThread = new(ChatThread);
            _chatThread.Name = nameof(ChatNetwork);
            _chatThread.Start(_cancellationTokenSource.Token);

            _iChatNetworkToken = broker.RegisterInterface<IChatNetwork>(this);

            return true;


            [ConfigHelp("Net", "ChatListen", ConfigScope.Global, typeof(string),
                Description = """
                The TCP endpoint to listen on for the 'simple chat protocol' (SS.Core.Modules.ChatNetwork module).
                The setting can be either a port or IP:port.
                When only a port is specified, it will listen on all network interfaces (any IP address available).
                When the setting is omitted, the settings in the [Listen] section are used.
                IPv6 addresses are also supported. When specifying a port with an IPv6 address, enclose the IP in square brackets [].
                For example, to listen on any available IPv6 address:
                ChatListen = [::]:5000
                """)]
            bool Initialize()
            {
                string endpointStr = _configManager.GetStr(_configManager.Global, "Net", "ChatListen");
                IPEndPoint endPoint;
                if (string.IsNullOrWhiteSpace(endpointStr))
                {
                    // Not configured, try to use the Net:Listen
                    INetwork network = _broker.GetInterface<INetwork>();
                    if (network is null)
                        return false;

                    try
                    {
                        if (!network.TryGetListenData(0, out endPoint, out _))
                            return false;
                    }
                    finally
                    {
                        broker.ReleaseInterface(ref network);
                    }
                }
                else
                {
                    if (int.TryParse(endpointStr, out int port))
                    {
                        // Port only
                        endPoint = new(IPAddress.Any, port);
                    }
                    else
                    {
                        // IP and port
                        if (!IPEndPoint.TryParse(endpointStr, out endPoint))
                            return false;
                    }
                }

                if (!InitializeSocket(endPoint))
                {
                    if (_listenSocket is not null)
                    {
                        _listenSocket.Dispose();
                        _listenSocket = null;
                    }

                    return false;
                }

                return true;


                bool InitializeSocket(IPEndPoint endPoint)
                {
                    try
                    {
                        _listenSocket = new(endPoint.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    }
                    catch (SocketException ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(ChatNetwork), $"Error creating socket ({endPoint}). {ex.Message}");
                        return false;
                    }

                    try
                    {
                        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                    }
                    catch (SocketException ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(ChatNetwork), $"Error setting socket option ({endPoint}). {ex.Message}");
                        return false;
                    }

                    try
                    {
                        _listenSocket.Bind(endPoint);
                    }
                    catch (SocketException ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(ChatNetwork), $"Error binding socket ({endPoint}). {ex.Message}");
                        return false;
                    }

                    try
                    {
                        _listenSocket.Blocking = false;
                    }
                    catch (SocketException ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(ChatNetwork), $"Error setting socket as non-blocking ({endPoint}). {ex.Message}");
                        return false;
                    }

                    try
                    {
                        _listenSocket.Listen(5);
                    }
                    catch (SocketException ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(ChatNetwork), $"Error listening on socket ({endPoint}). {ex.Message}");
                        return false;
                    }

                    _logManager.LogM(LogLevel.Info, nameof(ChatNetwork), $"Listening on {_listenSocket.LocalEndPoint}");
                    return true;
                }
            }
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface(ref _iChatNetworkToken);
            Cleanup();
            _playerData.FreePlayerData(ref _clientDataKey);
            return true;
        }

        #endregion

        #region IChatNetwork

        void IChatNetwork.AddHandler(ReadOnlySpan<char> type, ChatMessageHandler handler)
        {
            if (type.IsEmpty)
                throw new ArgumentException(paramName: nameof(type), message: "The value cannot be empty.");

            if (handler is null)
                throw new ArgumentNullException(nameof(handler));

            if (_handlerTrie.Remove(type, out ChatMessageHandler handlers))
            {
                handlers += handler;
                _handlerTrie.Add(type, handlers);
            }
            else
            {
                _handlerTrie.Add(type, handler);
            }
        }

        void IChatNetwork.RemoveHandler(ReadOnlySpan<char> type, ChatMessageHandler handler)
        {
            if (type.IsEmpty)
                throw new ArgumentException(paramName: nameof(type), message: "The value cannot be empty.");

            if (handler is null)
                throw new ArgumentNullException(nameof(handler));

            if (_handlerTrie.Remove(type, out ChatMessageHandler handlers))
            {
                handlers -= handler;

                if (handlers is not null)
                {
                    _handlerTrie.Add(type, handlers);
                }
            }
        }

        void IChatNetwork.SendToOne(Player player, ReadOnlySpan<char> message)
        {
            if (player is null
                || !player.IsChat
                || !player.TryGetExtraData(_clientDataKey, out ClientData clientData))
            {
                return;
            }

            // Encode the text.
            int byteCount = StringUtils.DefaultEncoding.GetByteCount(message) + 1;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            byteCount = StringUtils.DefaultEncoding.GetBytes(message, buffer);
            buffer[byteCount++] = (byte)'\n';

            // Get and populate a node.
            LinkedListNode<OutBuffer> node = s_outBufferLinkedListNodePool.Get();
            node.Value = new OutBuffer(buffer, byteCount);

            // Add the node to the outgoing list.
            lock (clientData.OutLock)
            {
                clientData.OutList.AddLast(node);
            }
        }

        void IChatNetwork.SendToOne(Player player, StringBuilder message)
        {
            Span<char> text = stackalloc char[Math.Min(MaxMessageSize, message.Length)];
            message.CopyTo(0, text, text.Length);
            ((IChatNetwork)this).SendToOne(player, text);
        }

        void IChatNetwork.SendToOne(Player player, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChatNetwork)this).SendToOne(player, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChatNetwork.SendToOne(Player player, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChatNetwork)this).SendToOne(player, ref handler);
        }

        void IChatNetwork.SendToArena(Arena arena, Player except, ReadOnlySpan<char> message)
        {
            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Status == PlayerState.Playing
                        && player.Arena == arena
                        && player != except
                        && player.IsChat)
                    {
                        ((IChatNetwork)this).SendToOne(player, message);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        void IChatNetwork.SendToArena(Arena arena, Player except, StringBuilder message)
        {
            Span<char> text = stackalloc char[Math.Min(MaxMessageSize, message.Length)];
            message.CopyTo(0, text, text.Length);
            ((IChatNetwork)this).SendToArena(arena, except, text);
        }

        void IChatNetwork.SendToArena(Arena arena, Player except, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChatNetwork)this).SendToArena(arena, except, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChatNetwork.SendToArena(Arena arena, Player except, IFormatProvider provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChatNetwork)this).SendToArena(arena, except, ref handler);
        }

        void IChatNetwork.SendToSet(HashSet<Player> set, ReadOnlySpan<char> message)
        {
            if (set is null)
                return;

            foreach (Player player in set)
            {
                ((IChatNetwork)this).SendToOne(player, message);
            }
        }

        void IChatNetwork.SendToSet(HashSet<Player> set, StringBuilder message)
        {
            Span<char> text = stackalloc char[Math.Min(MaxMessageSize, message.Length)];
            message.CopyTo(0, text, text.Length);
            ((IChatNetwork)this).SendToSet(set, text);
        }

        void IChatNetwork.SendToSet(HashSet<Player> set, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChatNetwork)this).SendToSet(set, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChatNetwork.SendToSet(HashSet<Player> set, IFormatProvider provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChatNetwork)this).SendToSet(set, ref handler);
        }

        bool IChatNetwork.TryGetClientStats(Player player, Span<char> ip, out int ipBytesWritten, out int port, out ChatClientStats stats)
        {
            if (player is null
                || !player.IsChat
                || !player.TryGetExtraData(_clientDataKey, out ClientData clientData)
                || clientData.Socket?.RemoteEndPoint is not IPEndPoint ipEndPoint
                || !ipEndPoint.Address.TryFormat(ip, out ipBytesWritten))
            {
                ipBytesWritten = 0;
                port = 0;
                stats = default;
                return false;
            }

            port = ipEndPoint.Port;
            stats = new ChatClientStats()
            {
                BytesSent = clientData.BytesSent,
                BytesReceived = clientData.BytesReceived,
            };
            return true;
        }

        #endregion

        #region IStringBuilderPoolProvider

        ObjectPool<StringBuilder> IStringBuilderPoolProvider.StringBuilderPool => _objectPoolManager.StringBuilderPool;

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Cleanup();
        }

        #endregion

        /// <summary>
        /// Thread that does all the socket operations.
        /// </summary>
        /// <param name="obj"></param>
        private void ChatThread(object obj)
        {
            if (obj is not CancellationToken cancellationToken)
                return;

            WaitHandle waitHandle = cancellationToken.WaitHandle;
            List<Socket> readList = new();
            List<Socket> writeList = new();
            Dictionary<Socket, (Player Player, ClientData ClientData)> playerSocketDictionary = new();
            HashSet<Player> playersToRemove = new();
            HashSet<Player> playersToProcess = new();

            while (!waitHandle.WaitOne(100))
            {
                readList.Clear();
                writeList.Clear();

                // listening socket
                readList.Add(_listenSocket);

                // client sockets
                playerSocketDictionary.Clear();
                _playerData.WriteLock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.IsChat
                            && player.Status >= PlayerState.Connected
                            && player.TryGetExtraData(_clientDataKey, out ClientData clientData)
                            && clientData.Socket is not null)
                        {
                            if (player.Status < PlayerState.TimeWait)
                            {
                                // Always check for incoming data
                                readList.Add(clientData.Socket);
                                playerSocketDictionary.Add(clientData.Socket, (player, clientData));

                                // and possibly outgoing too
                                if (clientData.OutList.Count > 0)
                                {
                                    writeList.Add(clientData.Socket);
                                }
                            }
                            else
                            {
                                _logManager.LogP(LogLevel.Info, nameof(ChatNetwork), player, "Disconnected.");

                                // Close the socket and cleanup buffers.
                                clientData.Reset();
                                playersToRemove.Add(player);
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.WriteUnlock();
                }

                foreach (Player player in playersToRemove)
                {
                    _playerData.FreePlayer(player);
                }

                playersToRemove.Clear();

                // Check which sockets are ready for accepting, reading, writing.
                Socket.Select(readList, writeList, null, 0);

                // Accept new connections and read incoming data.
                foreach (Socket socket in readList)
                {
                    if (socket == _listenSocket)
                    {
                        // There is at least one new connection that can be accepted.
                        do
                        {
                            _ = DoAccept();
                        }
                        while (_listenSocket.Poll(0, SelectMode.SelectRead));
                    }
                    else
                    {
                        // Read incoming data.
                        if (playerSocketDictionary.TryGetValue(socket, out var playerInfo))
                        {
                            ReadResult result = DoRead(playerInfo.Player, playerInfo.ClientData);
                            if (result == ReadResult.Disconnect || result == ReadResult.Error)
                            {
                                _playerData.KickPlayer(playerInfo.Player);
                            }
                        }
                    }
                }

                // Send outgoing data.
                foreach (Socket socket in writeList)
                {
                    if (playerSocketDictionary.TryGetValue(socket, out var playerInfo))
                    {
                        WriteResult result = DoWrite(playerInfo.Player, playerInfo.ClientData);
                        if (result == WriteResult.Error)
                        {
                            _playerData.KickPlayer(playerInfo.Player);
                        }
                    }
                }

                // Process incoming data that we read.
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.IsChat
                            && player.Status < PlayerState.TimeWait
                            && player.TryGetExtraData(_clientDataKey, out ClientData clientData)
                            && clientData.Socket is not null
                            && clientData.InIsDirty
                            && DateTime.UtcNow - clientData.LastProcessed > _messageDelay)
                        {
                            playersToProcess.Add(player);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                foreach (Player player in playersToProcess)
                {
                    Process(player);
                }

                playersToProcess.Clear();

                // Send a keep alive "NOOP" to idle connections.
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.IsChat
                            && player.Status < PlayerState.TimeWait
                            && player.TryGetExtraData(_clientDataKey, out ClientData clientData)
                            && clientData.Socket is not null
                            && DateTime.UtcNow - clientData.LastProcessed > _keepAliveTimeSpan
                            && DateTime.UtcNow - clientData.LastSend > _keepAliveTimeSpan)
                        {
                            ((IChatNetwork)this).SendToOne(player, "NOOP");
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }


            // Local function that accepts a connection from the listening socket.
            Player DoAccept()
            {
                Socket clientSocket;

                try
                {
                    clientSocket = _listenSocket.Accept();
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(ChatNetwork), $"Error accepting socket. {ex.Message}");
                    return null;
                }

                if (clientSocket.RemoteEndPoint is not IPEndPoint remoteEndPoint)
                {
                    _logManager.LogM(LogLevel.Error, nameof(ChatNetwork), $"Error getting IP of accepted socket.");
                    clientSocket.Dispose();
                    return null;
                }

                try
                {
                    clientSocket.Blocking = false;
                }
                catch (SocketException ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(ChatNetwork), $"Error setting accepted socket as non-blocking ({remoteEndPoint}). {ex.Message}");
                    clientSocket.Dispose();
                    return null;
                }

                Player player = _playerData.NewPlayer(ClientType.Chat);

                if (!player.TryGetExtraData(_clientDataKey, out ClientData clientData))
                {
                    clientSocket.Dispose();
                    _playerData.FreePlayer(player);
                    return null;
                }

                clientData.Socket = clientSocket;
                clientData.LastProcessed = DateTime.UtcNow;
                clientData.LastSend = DateTime.UtcNow;
                clientData.LastReceive = DateTime.UtcNow;

                _playerData.WriteLock();

                try
                {
                    player.IPAddress = remoteEndPoint.Address;
                    player.ClientName = "<unknown chat client>";
                    player.Status = PlayerState.Connected;
                }
                finally
                {
                    _playerData.WriteUnlock();
                }

                _logManager.LogP(LogLevel.Drivel, nameof(ChatNetwork), player, $"New connection from {remoteEndPoint}.");

                return player;
            }


            // Local function that receives data from a connected client.
            ReadResult DoRead(Player player, ClientData clientData)
            {
                if (player is null
                    || clientData is null
                    || clientData.Socket is null)
                {
                    return ReadResult.Error;
                }

                if (clientData.InData is null)
                {
                    clientData.InData = ArrayPool<byte>.Shared.Rent(MaxMessageSize);
                    clientData.InPosition = 0;
                }

                int remaining = MaxMessageSize - clientData.InPosition;
                if (remaining <= 0)
                    return ReadResult.NoData; // no more room in the buffer

                int bytesReceived;

                try
                {
                    bytesReceived = clientData.Socket.Receive(clientData.InData, clientData.InPosition, remaining, SocketFlags.None);
                }
                catch (Exception ex)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(ChatNetwork), player, $"Error receiving data. {ex.Message}");
                    return ReadResult.Error;
                }

                if (bytesReceived == 0)
                {
                    // The remote side shutdown the connection.
                    return ReadResult.Disconnect;
                }
                else if (bytesReceived > 0)
                {
                    clientData.InPosition += bytesReceived;
                    clientData.InIsDirty = true;
                    clientData.LastReceive = DateTime.UtcNow;
                    clientData.BytesReceived += (ulong)bytesReceived;
                    return ReadResult.Ok;
                }
                else
                {
                    return ReadResult.NoData;
                }
            }


            // Local function that sends data to a connected client.
            WriteResult DoWrite(Player player, ClientData clientData)
            {
                if (player is null
                    || clientData is null
                    || clientData.Socket is null)
                {
                    return WriteResult.Error;
                }

                LinkedListNode<OutBuffer> node;
                lock (clientData.OutLock)
                {
                    node = clientData.OutList.First;

                    if (node is not null)
                    {
                        clientData.OutList.Remove(node);
                    }
                }

                if (node is null)
                {
                    return WriteResult.NoData;
                }

                ref OutBuffer buffer = ref node.ValueRef;

                int remaining = buffer.Length - buffer.Position;
                if (remaining > 0)
                {
                    // Try to send as much of the data as possible.
                    int bytesSent;

                    try
                    {
                        bytesSent = clientData.Socket.Send(buffer.Data, buffer.Position, remaining, SocketFlags.None);
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogP(LogLevel.Warn, nameof(ChatNetwork), player, $"Error sending {remaining} bytes. {ex.Message}");
                        return WriteResult.Error;
                    }

                    if (bytesSent > 0)
                    {
                        buffer.Position += bytesSent;
                        clientData.BytesSent += (ulong)bytesSent;
                    }

                    remaining = buffer.Length - buffer.Position;

                    // Keep track of the attempt to send, even if no data was actually sent.
                    clientData.LastSend = DateTime.UtcNow;
                }

                if (remaining > 0)
                {
                    // There is more data to send for this buffer.
                    // Add the node back to the front of the list.
                    lock (clientData.OutLock)
                    {
                        clientData.OutList.AddFirst(node);
                    }
                }
                else
                {
                    // The buffer has been completely sent.
                    ArrayPool<byte>.Shared.Return(buffer.Data, true);
                    s_outBufferLinkedListNodePool.Return(node);
                }

                return WriteResult.Ok;
            }


            // Local function that tries to process 1 line from the data received from a client.
            void Process(Player player)
            {
                const byte CR = 0x0D;
                const byte LF = 0x0A;

                if (player is null || !player.TryGetExtraData(_clientDataKey, out ClientData clientData))
                    return;

                // Try to parse out a line.
                Span<byte> data = clientData.InData.AsSpan(0, clientData.InPosition);
                int lineIndex = data.IndexOfAny(CR, LF);
                if (lineIndex >= 0)
                {
                    // Got a line. Convert it to characters using the default encoding.
                    data = data[..lineIndex];
                    int charCount = StringUtils.DefaultEncoding.GetCharCount(data);
                    char[] line = ArrayPool<char>.Shared.Rent(charCount);
                    int decodedByteCount = StringUtils.DefaultEncoding.GetChars(data, line);
                    Debug.Assert(decodedByteCount == data.Length);

                    // The line shouldn't have any control characters. Replace any that exist.
                    StringUtils.ReplaceControlCharacters(new Span<char>(line, 0, charCount));

                    // Queue it up to be processed on the mainloop thread.
                    _mainloop.QueueMainWorkItem(MainloopWorkItem_ProcessLine, new CallHandlersDTO(player, line, charCount));

                    // Skip any additional CR or LF (end of line) delimiters.
                    int remainingIndex = lineIndex + 1;
                    while (remainingIndex < clientData.InPosition
                        && (clientData.InData[remainingIndex] == CR || clientData.InData[remainingIndex] == LF))
                    {
                        remainingIndex++;
                    }

                    int remainingLength = clientData.InPosition - remainingIndex;
                    if (remainingLength > 0)
                    {
                        // There is still more data. Move the remaining data to the beginning of the array.
                        Array.Copy(clientData.InData, remainingIndex, clientData.InData, 0, remainingLength);
                        clientData.InPosition = remainingLength;
                    }
                    else
                    {
                        // All of the data was processed.
                        ArrayPool<byte>.Shared.Return(clientData.InData, true);
                        clientData.InData = null;
                        clientData.InPosition = 0;
                        clientData.InIsDirty = false;
                    }
                }
                else
                {
                    // End of line not found.
                    int remaining = MaxMessageSize - clientData.InPosition;
                    if (remaining <= 0)
                    {
                        // No line found and the buffer's full. Discard.
                        ArrayPool<byte>.Shared.Return(clientData.InData, true);
                        clientData.InData = null;
                        clientData.InPosition = 0;
                    }

                    clientData.InIsDirty = false;
                }

                clientData.LastProcessed = DateTime.UtcNow;


                // Local function that is executed on the mainloop thread to call chat handlers.
                void MainloopWorkItem_ProcessLine(CallHandlersDTO dto)
                {
                    try
                    {
                        Player player = dto.Player;
                        if (player is null
                            || player.Status < PlayerState.Connected
                            || player.Status >= PlayerState.TimeWait)
                        {
                            return;
                        }

                        ReadOnlySpan<char> line = new(dto.Line, 0, dto.Length);
                        ReadOnlySpan<char> type;
                        int typeIndex = line.IndexOf(':');
                        if (typeIndex == -1)
                        {
                            type = line;
                            line = ReadOnlySpan<char>.Empty;
                        }
                        else
                        {
                            type = line[..typeIndex];
                            line = line[(typeIndex + 1)..];
                        }

                        if (_handlerTrie.TryGetValue(type, out ChatMessageHandler handlers))
                        {
                            handlers(player, line);
                        }
                    }
                    finally
                    {
                        if (dto.Line is not null)
                        {
                            ArrayPool<char>.Shared.Return(dto.Line, true);
                        }
                    }
                }
            }
        }

        private void Cleanup()
        {
            // Stop the worker thread and wait for it to terminate.
            _cancellationTokenSource?.Cancel();
            _chatThread?.Join();
            _chatThread = null;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            // Cleanup the listening socket.
            if (_listenSocket is not null)
            {
                _listenSocket.Close();
                _listenSocket.Dispose();
                _listenSocket = null;
            }

            // Cleanup any accepted sockets and buffers.
            if (_playerData is not null)
            {
                _playerData.Lock();
                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Type != ClientType.Chat
                            || !player.TryGetExtraData(_clientDataKey, out ClientData clientData))
                        {
                            continue;
                        }

                        clientData.Reset();
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }

            _handlerTrie.Clear();
        }

        #region Helper types

        /// <summary>
        /// Data transfer object to tell the mainloop thread to call chat message handlers.
        /// </summary>
        /// <param name="Player">The player the data was from.</param>
        /// <param name="Line">Buffer containing the data (rented from the ArrayPool).</param>
        /// <param name="Length">The # of bytes of <paramref name="Line"/> that contain data.</param>
        private readonly record struct CallHandlersDTO(Player Player, char[] Line, int Length);

        /// <summary>
        /// Per-player data for chat clients.
        /// </summary>
        /// <remarks>
        /// The <see cref="ChatThread(object)"/> is the only thread that accesses the data members.
        /// Except for <see cref="OutList"/>, which has <see cref="OutLock"/> to synchronize access.
        /// </remarks>
        private class ClientData : IResettable
        {
            /// <summary>
            /// The socket that we accepted a connection for.
            /// </summary>
            public Socket Socket;

            #region Timestamps

            /// <summary>
            /// Timestamp of the last attempt to send outgoing data.
            /// </summary>
            public DateTime LastSend;

            /// <summary>
            /// Timestamp of the last attempt to receive incoming data.
            /// </summary>
            public DateTime LastReceive;

            /// <summary>
            /// Timestamp of the last attempt to process incoming data.
            /// </summary>
            public DateTime LastProcessed;

            #endregion

            #region Incoming

            /// <summary>
            /// A buffer of incoming data. Rented from the <see cref="ArrayPool{T}"/>. <see langword="null"/> when there is no incoming data.
            /// </summary>
            public byte[] InData;

            /// <summary>
            /// The next position in <see cref="InData"/> that incoming data will be written to. This can also be used to tell the length of the data received so far.
            /// </summary>
            public int InPosition;

            /// <summary>
            /// Whether there is data available in <see cref="InData"/> that we have not yet attempted to process.
            /// </summary>
            public bool InIsDirty;

            #endregion

            #region Outgoing

            /// <summary>
            /// A list of outgoing buffers to send.
            /// </summary>
            public readonly LinkedList<OutBuffer> OutList = new();

            /// <summary>
            /// To synchronize access to <see cref="OutList"/>. It is the only data member accesssed by threads other than the ChatThread.
            /// </summary>
            public readonly object OutLock = new();

            #endregion

            #region Stats

            public ulong BytesSent;
            public ulong BytesReceived;

			#endregion

			public void Reset()
			{
                if (Socket is not null)
                {
                    Socket.Close();
                    Socket.Dispose();
                    Socket = null;
                }

                LastProcessed = DateTime.MinValue;
                LastSend = DateTime.MinValue;
                LastReceive = DateTime.MinValue;

                if (InData is not null)
                {
                    ArrayPool<byte>.Shared.Return(InData, true);
                    InData = null;
                }

                InPosition = 0;
                InIsDirty = false;

                lock (OutLock)
                {
                    LinkedListNode<OutBuffer> node;
                    while ((node = OutList.First) is not null)
                    {
                        OutList.Remove(node);
                        ArrayPool<byte>.Shared.Return(node.ValueRef.Data, true);
                        s_outBufferLinkedListNodePool.Return(node);
                    }
                }

                BytesSent = BytesReceived = 0;
            }

            bool IResettable.TryReset()
            {
                Reset();
				return true;
			}
        }

        private struct OutBuffer
        {
            /// <summary>
            /// Array containing the data to send.
            /// </summary>
            public readonly byte[] Data;

            /// <summary>
            /// The # of bytes of <see cref="Data"/> to send.
            /// </summary>
            public readonly int Length;

            public OutBuffer(byte[] data, int length)
            {
                Data = data ?? throw new ArgumentNullException(nameof(data));
                Length = length;
                Position = 0;
            }

            /// <summary>
            /// The current position in <see cref="Data"/> to send.
            /// </summary>
            public int Position { get; set; }
        }

        private enum ReadResult
        {
            /// <summary>
            /// No data was read from the socket. Either because there was no more room in the buffer to read into or there was no data to receive.
            /// </summary>
            NoData,

            /// <summary>
            /// Data was read from the socket into the 'In' buffer.
            /// </summary>
            Ok,

            /// <summary>
            /// The remote side closed the connection.
            /// </summary>
            Disconnect,

            /// <summary>
            /// An error occurred. Generally, this means we should close the socket.
            /// </summary>
            Error,
        }

        private enum WriteResult
        {
            /// <summary>
            /// There is no data waiting to be sent.
            /// </summary>
            NoData,

            /// <summary>
            /// There was data waiting to be sent and an attempt to send it was made.
            /// This does not mean any data was actually transferred, just that we tried. The TCP send buffer could have been full.
            /// </summary>
            Ok,

            /// <summary>
            /// An error occurred. Generally, this means we should close the socket.
            /// </summary>
            Error,
        }

        #endregion
    }
}
