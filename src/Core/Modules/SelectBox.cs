using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that gives the ability to control the select box user interface on players using a game client that support the <see cref="S2CPacketType.SelectBox"/> packet.
    /// </summary>
    public sealed class SelectBox(
        ICommandManager commandManager,
        INetwork network,
        IObjectPoolManager objectPoolManager,
        IPlayerData playerData) : IModule, ISelectBox
    {
        private readonly ICommandManager _commandManager  = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        private InterfaceRegistrationToken<ISelectBox>? _selectBoxRegistrationToken;

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

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _commandManager.AddCommand("select", Command_select);
            _selectBoxRegistrationToken = broker.RegisterInterface<ISelectBox>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _selectBoxRegistrationToken) != 0)
                return false;

            _commandManager.RemoveCommand("select", Command_select);
            return true;
        }

        #endregion

        #region ISelectBox

        void ISelectBox.Open(ITarget target, ReadOnlySpan<char> title, IReadOnlyList<SelectBoxItem> items)
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

            byte[]? packetArray = null;
            try
            {
                //
                // Build the packet
                //

                Span<byte> buffer = length <= 1024 ? stackalloc byte[length] : (packetArray = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);

                // Type
                buffer[0] = (byte)S2CPacketType.SelectBox;
                Span<byte> remaining = buffer[1..];
                length = 1;

                // Title
                if (!StringUtils.DefaultEncoding.TryGetBytes(title, remaining, out int bytesWritten))
                    return;

                remaining = remaining[bytesWritten..];
                remaining[0] = 0; // null-terminator
                remaining = remaining[1..];
                length += bytesWritten + 1;

                for (int i = 0; i < items.Count; i++) // purposely use the collection's indexer to avoid boxing an enumerator
                {
                    (short itemValue, ReadOnlyMemory<char> itemText) = items[i];

                    // Item value
                    if (remaining.Length < 2)
                        break;

                    BinaryPrimitives.WriteInt16LittleEndian(remaining, itemValue);
                    remaining = remaining[2..];

                    // Item text
                    ReadOnlySpan<char> itemTextSpan = StringUtils.TruncateForEncodedByteLimit(itemText.Span, MaxItemTextLength - 1); // -1 for the null-terminator
                    if (!StringUtils.DefaultEncoding.TryGetBytes(itemTextSpan, remaining, out bytesWritten))
                        break;

                    remaining = remaining[bytesWritten..];
                    remaining[0] = 0; // null-terminator
                    remaining = remaining[1..];
                    length += 2 + bytesWritten + 1;
                }

                //
                // Send the packet
                //

                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    _playerData.TargetToSet(target, players, static p => (p.ClientFeatures & ClientFeatures.SelectBox) != 0);
                    _network.SendToSet(players, buffer[..length], NetSendFlags.Reliable);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }
            }
            finally
            {
                if (packetArray is not null)
                    ArrayPool<byte>.Shared.Return(packetArray);
            }
        }

        #endregion

        private void Command_select(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            Span<Range> tokens = stackalloc Range[2];
            int tokenCount = parameters.Split(tokens, ' ', StringSplitOptions.TrimEntries);
            if (tokenCount < 1)
                return;

            if (!short.TryParse(parameters[tokens[0]], out short itemValue))
                return;

            ReadOnlySpan<char> itemText = tokenCount == 2 ? parameters[tokens[1]] : [];

            SelectBoxItemSelectedCallback.Fire(arena, player, itemValue, itemText);
        }
    }
}
