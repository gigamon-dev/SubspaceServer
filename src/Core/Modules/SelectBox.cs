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
        IMainloop mainloop,
        IMainloopTimer mainloopTimer,
        INetwork network,
        IObjectPoolManager objectPoolManager,
        IPlayerData playerData) : IModule, ISelectBox
    {
        private readonly ICommandManager _commandManager  = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IMainloop _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        private readonly IMainloopTimer _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        private static readonly DefaultObjectPool<List<(Session Session, int Version)>> s_sessionListPool = new(new ListPooledObjectPolicy<(Session Session, int Version)>());

        private InterfaceRegistrationToken<ISelectBox>? _selectBoxRegistrationToken;
        private PlayerDataKey<PlayerData> _pdKey;

        private const string CommandName = "select";

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

        bool IModule.Load(IComponentBroker broker)
        {
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            _commandManager.AddCommand(CommandName, Command_select);
            _selectBoxRegistrationToken = broker.RegisterInterface<ISelectBox>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _selectBoxRegistrationToken) != 0)
                return false;

            _commandManager.RemoveCommand(CommandName, Command_select);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _mainloopTimer.ClearTimer<Session>(MainloopTimer_Expire, null);
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
            List<(Session Session, int Version)> replaced = s_sessionListPool.Get();
            SelectBoxHandle handle = default;

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

                Session<TState> session = Session<TState>.Pool.Get();
                session.Initialize(state, selected, completed);
                session.BuildPacket(title, items);

                foreach (Player player in players)
                {
                    if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                        continue;

                    // The client only has one select box, so this one replaces the one that was open.
                    if (pd.SelectBoxSession is not null)
                    {
                        Session previous = pd.SelectBoxSession;
                        previous.Players.Remove(player);

                        if (previous.Players.Count == 0)
                            replaced.Add((previous, previous.Version));
                    }

                    pd.SelectBoxSession = session;
                    session.Players.Add(player);
                }

                if (session.Players.Count == 0)
                {
                    CompleteSession(session);
                }
                else
                {
                    _network.SendToSet(session.Players, session.Packet, NetSendFlags.Reliable);

                    if (timeout is not null)
                    {
                        int timeoutMilliseconds = (int)Math.Clamp(Math.Ceiling(timeout.Value.TotalMilliseconds), 1, int.MaxValue);
                        session.IsTimerSet = true;
                        _mainloopTimer.SetTimer<Session>(MainloopTimer_Expire, timeoutMilliseconds, timeoutMilliseconds, session, session);
                    }

                    handle = new SelectBoxHandle(session, session.Version);
                }

                // Complete the select boxes that were replaced for all of their players.
                // This is done last, since it calls their completed delegates.
                // A completed delegate can cancel one of the others, so check that each is still the same select box.
                foreach ((Session previous, int version) in replaced)
                {
                    if (previous.IsActive && previous.Version == version)
                        CompleteSession(previous);
                }

                return handle;
            }
            finally
            {
                s_sessionListPool.Return(replaced);
                _objectPoolManager.PlayerSetPool.Return(players);
            }
        }

        bool ISelectBox.Cancel(SelectBoxHandle handle)
        {
            if (!_mainloop.IsMainloop)
                throw new InvalidOperationException("Must be called on the mainloop thread.");

            if (handle.Session is not Session session || !session.IsActive || session.Version != handle.Version)
                return false;

            Expire(session);
            return true;
        }

        #endregion

        private bool MainloopTimer_Expire(Session session)
        {
            // Returning false stops the timer.
            session.IsTimerSet = false;

            if (session.IsActive)
                Expire(session);

            return false;
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action != PlayerAction.LeaveArena && action != PlayerAction.Disconnect)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd) || pd.SelectBoxSession is not Session session)
                return;

            // Nothing is sent to the player. Continuum keeps displaying the select box in the new arena, and selecting from it is ignored.
            pd.SelectBoxSession = null;
            session.Players.Remove(player);

            if (session.Players.Count == 0)
                CompleteSession(session);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<item value> <item text>",
            Description = """
                Selects an item from the select box that was last displayed.
                Continuum sends this command when an item is chosen from a select box.
                """)]
        private void Command_select(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd) || pd.SelectBoxSession is not Session session)
                return;

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
            // Disassociate the player before calling the delegate, so that it can open another select box for the player.
            pd.SelectBoxSession = null;
            session.Players.Remove(player);

            int version = session.Version;

            try
            {
                session.InvokeSelected(player, itemValue, itemText);
            }
            finally
            {
                // The delegate may have caused the select box to be completed already (e.g., cancelled it).
                if (session.IsActive && session.Version == version && session.Players.Count == 0)
                    CompleteSession(session);
            }
        }

        /// <summary>
        /// Sends a replacement select box to the players still associated with a select box, and completes it.
        /// </summary>
        private void Expire(Session session)
        {
            if (session.Players.Count > 0)
            {
                foreach (Player player in session.Players)
                {
                    if (player.TryGetExtraData(_pdKey, out PlayerData? pd) && pd.SelectBoxSession == session)
                        pd.SelectBoxSession = null;
                }

                // Same title, with a single item that is ignored if selected.
                ReadOnlySpan<byte> titleWithTerminator = session.TitleWithTerminator;
                Span<byte> buffer = stackalloc byte[1 + MaxTitleLength + 2 + MaxItemTextLength];
                buffer[0] = (byte)S2CPacketType.SelectBox;
                titleWithTerminator.CopyTo(buffer[1..]);
                int length = 1 + titleWithTerminator.Length;
                BinaryPrimitives.WriteInt16LittleEndian(buffer[length..], 0);
                length += 2;
                length += StringUtils.DefaultEncoding.GetBytes(ExpiredItemText, buffer[length..]);
                buffer[length++] = 0; // null-terminator

                _network.SendToSet(session.Players, buffer[..length], NetSendFlags.Reliable);
            }

            CompleteSession(session);
        }

        private void CompleteSession(Session session)
        {
            if (session.IsTimerSet)
            {
                _mainloopTimer.ClearTimer<Session>(MainloopTimer_Expire, session);
                session.IsTimerSet = false;
            }

            session.Complete();
        }

        #region Helper types

        private class PlayerData : IResettable
        {
            /// <summary>
            /// The select box that was last displayed to the player, if the player is still associated with it.
            /// </summary>
            public Session? SelectBoxSession;

            public bool TryReset()
            {
                SelectBoxSession = null;
                return true;
            }
        }

        /// <summary>
        /// A select box that was opened, and the players that it's associated with.
        /// </summary>
        private abstract class Session
        {
            /// <summary>
            /// The players that the select box is associated with.
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
            /// Whether a timer is set for the select box's timeout.
            /// </summary>
            public bool IsTimerSet;

            private byte[]? _packet;
            private int _packetLength;
            private int _itemsOffset;

            /// <summary>
            /// The <see cref="S2CPacketType.SelectBox"/> packet that was sent.
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
                IsTimerSet = false;

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

            public void Initialize(TState state, SelectBoxItemSelectedDelegate<TState> selected, SelectBoxCompletedDelegate<TState>? completed)
            {
                _state = state;
                _selected = selected;
                _completed = completed;
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
