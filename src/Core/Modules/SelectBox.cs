using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.ObjectPool;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that gives the ability to control the select box user interface on players using a game client that support the <see cref="S2CPacketType.SelectBox"/> packet.
    /// </summary>
    /// <remarks>
    /// See <see cref="ISelectBox"/> for how select boxes are tracked.
    /// </remarks>
    public sealed class SelectBox(
        ICommandManager commandManager,
        IConfigManager configManager,
        IMainloop mainloop,
        IMainloopTimer mainloopTimer,
        INetwork network,
        IObjectPoolManager objectPoolManager,
        IPlayerData playerData) : IModule, ISelectBox
    {
        private readonly ICommandManager _commandManager  = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly IMainloop _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        private readonly IMainloopTimer _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        private static readonly DefaultObjectPool<List<(Session Session, int Version)>> s_sessionListPool = new(new ListPooledObjectPolicy<(Session Session, int Version)>());

        private InterfaceRegistrationToken<ISelectBox>? _selectBoxRegistrationToken;
        private PlayerDataKey<PlayerData> _pdKey;

        /// <summary>
        /// The players that a select box is displayed to, for checking timeouts and switching to queued select boxes.
        /// </summary>
        private readonly HashSet<Player> _playersWithSelectBox = [];

        /// <summary>
        /// How long (in ticks) a select box without a timeout is displayed before a queued one is displayed instead. 0 = never.
        /// </summary>
        private int _queueSwitchTicks;

        private const string CommandName = "select";

        /// <summary>
        /// How often to check timeouts and whether to switch to a queued select box.
        /// </summary>
        private const int CheckIntervalMilliseconds = 500;

        /// <summary>
        /// The maximum # of bytes Continnum accepts for the <see cref="S2CPacketType.SelectBox"/> packet.
        /// </summary>
        public const int MaxSelectPacketLength = 8192;

        /// <summary>
        /// The maximum # of bytes for the title of a select box.
        /// </summary>
        /// <remarks>
        /// The field must include a null-terminator.
        /// </remarks>
        public const int MaxTitleLength = 64;

        /// <summary>
        /// The maximum # of bytes for the item text of a select box.
        /// </summary>
        /// <remarks>
        /// The field must include a null-terminator.
        /// </remarks>
        public const int MaxItemTextLength = 128;

        /// <summary>
        /// The text of the only item of the select box that replaces one that timed out or was cancelled.
        /// Selecting it is ignored.
        /// </summary>
        public const string ExpiredItemText = "Selection has expired.";

        #region Module members

        [ConfigHelp<int>("SelectBox", "QueueSwitchSeconds", ConfigScope.Global, Default = 30, Min = 0,
            Description = """
                How many seconds a select box that was opened without a timeout is displayed to a player,
                while another select box is queued for the player, before the queued one is displayed instead.
                The one that was displayed goes to the back of the player's queue.
                This is because the player may have closed it (ESC), which the server can't tell.
                A select box that was opened with a timeout is displayed until it times out.
                0 = never (the queued one is displayed once the player selects an item).
                """)]
        bool IModule.Load(IComponentBroker broker)
        {
            int queueSwitchSeconds = _configManager.GetInt(_configManager.Global, "SelectBox", "QueueSwitchSeconds", ConfigHelp.Constants.Global.SelectBox.QueueSwitchSeconds.Default);
            _queueSwitchTicks = queueSwitchSeconds > 0 ? Math.Min(queueSwitchSeconds, 1_000_000) * 100 : 0;

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            _commandManager.AddCommand(CommandName, Command_select);
            _mainloopTimer.SetTimer(MainloopTimer_Check, CheckIntervalMilliseconds, CheckIntervalMilliseconds, null);
            _selectBoxRegistrationToken = broker.RegisterInterface<ISelectBox>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _selectBoxRegistrationToken) != 0)
                return false;

            _mainloopTimer.ClearTimer(MainloopTimer_Check, null);
            _commandManager.RemoveCommand(CommandName, Command_select);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _playersWithSelectBox.Clear();
            _playerData.FreePlayerData(ref _pdKey);
            return true;
        }

        #endregion

        #region ISelectBox

        SelectBoxHandle ISelectBox.Open<TState>(
            ITarget target,
            ReadOnlySpan<char> title,
            IReadOnlyList<SelectBoxItem> items,
            TState state,
            SelectBoxItemSelectedDelegate<TState> selected,
            SelectBoxCompletedDelegate<TState>? completed,
            TimeSpan? timeout)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(selected);

            if (timeout is not null && timeout.Value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Must be positive.");

            if (!_mainloop.IsMainloop)
                throw new InvalidOperationException("Must be called on the mainloop thread.");

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> displayNow = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                // Only players in an arena are included, since leaving the arena disassociates a player from the select box.
                _playerData.TargetToSet(target, players, static p => p.Status == PlayerState.Playing && (p.ClientFeatures & ClientFeatures.SelectBox) != 0);

                // Never send a select box without items.
                // Continuum would display an empty select box, and selecting from it reads an item that doesn't exist.
                if (players.Count == 0 || items.Count == 0)
                {
                    completed?.Invoke(state);
                    return default;
                }

                // In ticks (1/100ths of a second). Limited, so that it can be compared with the difference between two ServerTicks.
                int timeoutTicks = timeout is null ? 0 : (int)Math.Clamp(Math.Ceiling(timeout.Value.TotalMilliseconds / 10), 1, 100_000_000);

                Session<TState> session = Session<TState>.Pool.Get();
                session.Initialize(state, selected, completed, timeoutTicks);
                session.BuildPacket(title, items);

                ServerTick now = ServerTick.Now;

                foreach (Player player in players)
                {
                    if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                        continue;

                    session.Players.Add(player);

                    if (pd.SelectBoxSession is null)
                    {
                        // No select box is displayed to the player, so display this one now.
                        SetDisplayed(player, pd, session, now);
                        displayNow.Add(player);
                    }
                    else
                    {
                        // The client only has one select box, and another one is displayed to the player.
                        // Queue this one, to be displayed once the ones before it are done.
                        pd.SelectBoxQueue.Add(session);
                    }
                }

                if (session.Players.Count == 0)
                {
                    session.Complete();
                    return default;
                }

                if (displayNow.Count > 0)
                    _network.SendToSet(displayNow, session.Packet, NetSendFlags.Reliable);

                return new SelectBoxHandle(session, session.Version);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(displayNow);
                _objectPoolManager.PlayerSetPool.Return(players);
            }
        }

        bool ISelectBox.Cancel(SelectBoxHandle handle)
        {
            if (!_mainloop.IsMainloop)
                throw new InvalidOperationException("Must be called on the mainloop thread.");

            if (handle.Session is not Session session || !session.IsActive || session.Version != handle.Version)
                return false;

            foreach (Player player in session.Players)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    continue;

                if (pd.SelectBoxSession == session)
                {
                    // It's displayed to the player. Replace it with the next queued select box, or if there isn't one, the expired select box.
                    ClearDisplayed(player, pd);

                    if (!TryDisplayNext(player, pd))
                        SendExpired(player, session);
                }
                else
                {
                    // It's queued for the player.
                    pd.SelectBoxQueue.Remove(session);
                }
            }

            session.Complete();
            return true;
        }

        #endregion

        private bool MainloopTimer_Check()
        {
            if (_playersWithSelectBox.Count == 0)
                return true;

            ServerTick now = ServerTick.Now;
            HashSet<Player> due = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                foreach (Player player in _playersWithSelectBox)
                {
                    if (player.TryGetExtraData(_pdKey, out PlayerData? pd)
                        && pd.SelectBoxSession is Session session
                        && IsDue(pd, session, now))
                    {
                        due.Add(player);
                    }
                }

                // This is done separately, since completing a select box calls its completed delegate, which can open or cancel select boxes.
                foreach (Player player in due)
                {
                    // Check again, in case a completed delegate changed it.
                    if (!player.TryGetExtraData(_pdKey, out PlayerData? pd)
                        || pd.SelectBoxSession is not Session session
                        || !IsDue(pd, session, now))
                    {
                        continue;
                    }

                    ClearDisplayed(player, pd);

                    if (session.TimeoutTicks > 0)
                    {
                        // It timed out for the player.
                        // Replace it with the next queued select box, or if there isn't one, the expired select box.
                        if (!TryDisplayNext(player, pd))
                            SendExpired(player, session);

                        session.Players.Remove(player);

                        if (session.Players.Count == 0)
                            session.Complete();
                    }
                    else
                    {
                        // The player may have closed it (ESC), which the server can't tell.
                        // Display the next queued select box instead, and put this one at the back of the queue.
                        pd.SelectBoxQueue.Add(session);
                        TryDisplayNext(player, pd);
                    }
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(due);
            }

            return true;
        }

        /// <summary>
        /// Gets whether the select box displayed to a player timed out, or should be switched to the next queued select box.
        /// </summary>
        private bool IsDue(PlayerData pd, Session session, ServerTick now)
        {
            int displayedTicks = now - pd.SelectBoxDisplayedTick;

            if (session.TimeoutTicks > 0)
                return displayedTicks >= session.TimeoutTicks; // Displayed until it times out, even if others are queued.

            return _queueSwitchTicks > 0 && pd.SelectBoxQueue.Count > 0 && displayedTicks >= _queueSwitchTicks;
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action != PlayerAction.LeaveArena && action != PlayerAction.Disconnect)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd)
                || (pd.SelectBoxSession is null && pd.SelectBoxQueue.Count == 0))
            {
                return;
            }

            // Nothing is sent to the player. Continuum keeps displaying the select box in the new arena, and selecting from it is ignored.
            // The queued select boxes are never displayed to the player.
            List<(Session Session, int Version)> sessions = s_sessionListPool.Get();

            try
            {
                if (pd.SelectBoxSession is Session displayed)
                {
                    ClearDisplayed(player, pd);
                    sessions.Add((displayed, displayed.Version));
                }

                foreach (Session queued in pd.SelectBoxQueue)
                    sessions.Add((queued, queued.Version));

                pd.SelectBoxQueue.Clear();

                foreach ((Session session, _) in sessions)
                    session.Players.Remove(player);

                // Complete the select boxes that are no longer associated with any player.
                // A completed delegate can cancel one of the others, so check that each is still the same select box.
                foreach ((Session session, int version) in sessions)
                {
                    if (session.IsActive && session.Version == version && session.Players.Count == 0)
                        session.Complete();
                }
            }
            finally
            {
                s_sessionListPool.Return(sessions);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<item value> <item text>]",
            Description = """
                Selects an item from the select box that is displayed.
                Continuum sends this command when an item is chosen from a select box.
                Without arguments, displays the select box again (e.g., if it was closed with ESC).
                """)]
        private void Command_select(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd) || pd.SelectBoxSession is not Session session)
                return;

            if (parameters.IsWhiteSpace())
            {
                // Display it again.
                _network.SendToOne(player, session.Packet, NetSendFlags.Reliable);

                // The player wants to see it, so it isn't switched to a queued select box for a while.
                // A select box with a timeout keeps its timeout.
                if (session.TimeoutTicks == 0)
                    pd.SelectBoxDisplayedTick = ServerTick.Now;

                return;
            }

            Span<Range> tokens = stackalloc Range[2];
            int tokenCount = parameters.Split(tokens, ' ', StringSplitOptions.TrimEntries);
            if (tokenCount < 1)
                return;

            if (!short.TryParse(parameters[tokens[0]], out short itemValue))
                return;

            ReadOnlySpan<char> itemText = tokenCount == 2 ? parameters[tokens[1]] : [];

            // It must match an item of the player's select box.
            // Otherwise, it's for a select box that the player is no longer associated with (or it was typed manually).
            if (!session.IsMatch(itemValue, itemText))
                return;

            // Choosing an item closes the select box.
            // Disassociate the player before calling the delegate, so that it can open another select box for the player, which is displayed immediately.
            ClearDisplayed(player, pd);
            session.Players.Remove(player);

            int version = session.Version;

            try
            {
                session.InvokeSelected(player, itemValue, itemText);
            }
            finally
            {
                // Display the next queued select box, unless the delegate displayed another one.
                if (pd.SelectBoxSession is null)
                    TryDisplayNext(player, pd);

                // The delegate may have caused the select box to be completed already (e.g., cancelled it).
                if (session.IsActive && session.Version == version && session.Players.Count == 0)
                    session.Complete();
            }
        }

        private void SetDisplayed(Player player, PlayerData pd, Session session, ServerTick now)
        {
            pd.SelectBoxSession = session;
            pd.SelectBoxDisplayedTick = now;
            _playersWithSelectBox.Add(player);
        }

        private void ClearDisplayed(Player player, PlayerData pd)
        {
            pd.SelectBoxSession = null;
            _playersWithSelectBox.Remove(player);
        }

        /// <summary>
        /// Displays the next select box in a player's queue, if there is one.
        /// </summary>
        /// <returns><see langword="true"/> if a select box was displayed.</returns>
        private bool TryDisplayNext(Player player, PlayerData pd)
        {
            if (pd.SelectBoxQueue.Count == 0)
                return false;

            Session next = pd.SelectBoxQueue[0];
            pd.SelectBoxQueue.RemoveAt(0);

            SetDisplayed(player, pd, next, ServerTick.Now);
            _network.SendToOne(player, next.Packet, NetSendFlags.Reliable);
            return true;
        }

        /// <summary>
        /// Sends a player a select box to replace one that timed out or was cancelled:
        /// the same title, with a single item that is ignored if selected.
        /// </summary>
        private void SendExpired(Player player, Session session)
        {
            ReadOnlySpan<byte> titleWithTerminator = session.TitleWithTerminator;
            Span<byte> buffer = stackalloc byte[1 + MaxTitleLength + 2 + MaxItemTextLength];
            buffer[0] = (byte)S2CPacketType.SelectBox;
            titleWithTerminator.CopyTo(buffer[1..]);
            int length = 1 + titleWithTerminator.Length;
            BinaryPrimitives.WriteInt16LittleEndian(buffer[length..], 0);
            length += 2;
            length += StringUtils.DefaultEncoding.GetBytes(ExpiredItemText, buffer[length..]);
            buffer[length++] = 0; // null-terminator

            _network.SendToOne(player, buffer[..length], NetSendFlags.Reliable);
        }

        #region Helper types

        private class PlayerData : IResettable
        {
            /// <summary>
            /// The select box that is displayed to the player (as far as the server can tell), if the player is still associated with it.
            /// </summary>
            public Session? SelectBoxSession;

            /// <summary>
            /// When <see cref="SelectBoxSession"/> was displayed to the player.
            /// </summary>
            public ServerTick SelectBoxDisplayedTick;

            /// <summary>
            /// The select boxes waiting to be displayed to the player, in order.
            /// </summary>
            public readonly List<Session> SelectBoxQueue = [];

            public bool TryReset()
            {
                SelectBoxSession = null;
                SelectBoxDisplayedTick = default;
                SelectBoxQueue.Clear();
                return true;
            }
        }

        /// <summary>
        /// A select box that was opened, and the players that it's associated with.
        /// </summary>
        private abstract class Session
        {
            /// <summary>
            /// The players that the select box is associated with (displayed or queued).
            /// </summary>
            public readonly HashSet<Player> Players = [];

            /// <summary>
            /// Incremented each time the session is completed, so that a <see cref="SelectBoxHandle"/> only refers to the select box it was returned for.
            /// </summary>
            public int Version;

            /// <summary>
            /// Whether the select box is open (not completed).
            /// </summary>
            public bool IsActive;

            /// <summary>
            /// How long (in ticks) the select box is displayed to a player before it times out for the player. 0 = no timeout.
            /// </summary>
            public int TimeoutTicks;

            private byte[]? _packet;
            private int _packetLength;
            private int _itemsOffset;

            /// <summary>
            /// The <see cref="S2CPacketType.SelectBox"/> packet.
            /// </summary>
            public ReadOnlySpan<byte> Packet => _packet.AsSpan(0, _packetLength);

            /// <summary>
            /// The title, including its null-terminator, as it was sent.
            /// </summary>
            public ReadOnlySpan<byte> TitleWithTerminator => _packet.AsSpan(1, _itemsOffset - 1);

            /// <summary>
            /// Builds the <see cref="S2CPacketType.SelectBox"/> packet.
            /// </summary>
            public void BuildPacket(ReadOnlySpan<char> title, IReadOnlyList<SelectBoxItem> items)
            {
                title = StringUtils.TruncateForEncodedByteLimit(title, MaxTitleLength - 1); // -1 for the null-terminator

                // Determine the length of the packet.
                int length = 1 + StringUtils.DefaultEncoding.GetByteCount(title) + 1; // type + title + null-terminator
                for (int i = 0; i < items.Count; i++) // purposely use the collection's indexer to avoid boxing an enumerator
                {
                    (short itemValue, ReadOnlyMemory<char> itemText) = items[i];

                    int itemTextByteCount = StringUtils.DefaultEncoding.GetByteCount(itemText.Span);
                    if (itemTextByteCount >= MaxItemTextLength)
                        itemTextByteCount = MaxItemTextLength - 1; // -1 to allow for the null-terminator

                    int additional = 2 + itemTextByteCount + 1; // itemValue + itemText + null-terminator
                    if (length + additional > MaxSelectPacketLength)
                        break;

                    length += additional;
                }

                _packet = ArrayPool<byte>.Shared.Rent(length);
                Span<byte> remaining = _packet.AsSpan(0, length);

                // Type
                remaining[0] = (byte)S2CPacketType.SelectBox;
                remaining = remaining[1..];
                length = 1;

                // Title
                int bytesWritten = StringUtils.DefaultEncoding.GetBytes(title, remaining);
                remaining = remaining[bytesWritten..];
                remaining[0] = 0; // null-terminator
                remaining = remaining[1..];
                length += bytesWritten + 1;
                _itemsOffset = length;

                for (int i = 0; i < items.Count; i++) // purposely use the collection's indexer to avoid boxing an enumerator
                {
                    (short itemValue, ReadOnlyMemory<char> itemText) = items[i];

                    // Item value
                    if (remaining.Length < 2)
                        break;

                    BinaryPrimitives.WriteInt16LittleEndian(remaining, itemValue);

                    // Item text
                    ReadOnlySpan<char> itemTextSpan = StringUtils.TruncateForEncodedByteLimit(itemText.Span, MaxItemTextLength - 1); // -1 for the null-terminator
                    if (!StringUtils.DefaultEncoding.TryGetBytes(itemTextSpan, remaining[2..], out bytesWritten)
                        || remaining.Length < 2 + bytesWritten + 1)
                    {
                        break;
                    }

                    remaining = remaining[(2 + bytesWritten)..];
                    remaining[0] = 0; // null-terminator
                    remaining = remaining[1..];
                    length += 2 + bytesWritten + 1;
                }

                _packetLength = length;
            }

            /// <summary>
            /// Gets whether an item (value and text) is in the select box, as Continuum sends it back with the ?select command.
            /// </summary>
            public bool IsMatch(short itemValue, ReadOnlySpan<char> itemText)
            {
                ReadOnlySpan<byte> remaining = _packet.AsSpan(_itemsOffset, _packetLength - _itemsOffset);
                Span<char> textBuffer = stackalloc char[MaxItemTextLength];

                while (remaining.Length >= 3) // value + null-terminator
                {
                    short value = BinaryPrimitives.ReadInt16LittleEndian(remaining);
                    remaining = remaining[2..];

                    int terminatorIndex = remaining.IndexOf((byte)0);
                    if (terminatorIndex == -1)
                        return false;

                    ReadOnlySpan<byte> textBytes = remaining[..terminatorIndex];
                    remaining = remaining[(terminatorIndex + 1)..];

                    if (value != itemValue)
                        continue;

                    // Compare the text as the client sends it back: the encoded bytes that were sent, decoded and trimmed (the command parameters are trimmed).
                    int charCount = StringUtils.DefaultEncoding.GetChars(textBytes, textBuffer);
                    if (MemoryExtensions.SequenceEqual(textBuffer[..charCount].Trim(), itemText))
                        return true;
                }

                return false;
            }

            public abstract void InvokeSelected(Player player, short itemValue, ReadOnlySpan<char> itemText);

            /// <summary>
            /// Resets the session, returns it to its pool, and then calls its completed delegate.
            /// </summary>
            public abstract void Complete();

            protected void ResetSession()
            {
                Players.Clear();
                Version++;
                IsActive = false;
                TimeoutTicks = 0;

                if (_packet is not null)
                {
                    ArrayPool<byte>.Shared.Return(_packet);
                    _packet = null;
                }

                _packetLength = 0;
                _itemsOffset = 0;
            }
        }

        private sealed class Session<TState> : Session
        {
            public static readonly DefaultObjectPool<Session<TState>> Pool = new(new DefaultPooledObjectPolicy<Session<TState>>());

            private TState _state = default!;
            private SelectBoxItemSelectedDelegate<TState>? _selected;
            private SelectBoxCompletedDelegate<TState>? _completed;

            public void Initialize(TState state, SelectBoxItemSelectedDelegate<TState> selected, SelectBoxCompletedDelegate<TState>? completed, int timeoutTicks)
            {
                _state = state;
                _selected = selected;
                _completed = completed;
                TimeoutTicks = timeoutTicks;
                IsActive = true;
            }

            public override void InvokeSelected(Player player, short itemValue, ReadOnlySpan<char> itemText)
            {
                _selected?.Invoke(player, itemValue, itemText, _state);
            }

            public override void Complete()
            {
                TState state = _state;
                SelectBoxCompletedDelegate<TState>? completed = _completed;

                _state = default!;
                _selected = null;
                _completed = null;
                ResetSession();
                Pool.Return(this);

                // Called last, after the session can no longer be referenced by a handle.
                completed?.Invoke(state);
            }
        }

        #endregion
    }
}
