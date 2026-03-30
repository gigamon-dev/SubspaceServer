using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Persist;
using System.Text;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that manages a per-player preferred arena.
    /// Provides the ?arena command and persists the preference globally.
    /// On first arena entry each session, players with a preference set are automatically sent there.
    /// </summary>
    [ModuleInfo("Manages per-player arena preference, automatically routing players to their preferred arena on first entry.")]
    public sealed class PlayerArenaPreference : IAsyncModule
    {
        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly ILogManager _logManager;
        private readonly IPlayerData _playerData;

        private IPersist? _persist;

        private PlayerDataKey<PlayerPreferenceData> _pdKey;
        private DelegatePersistentData<Player>? _persistRegistration;

        private IComponentBroker? _broker;

        private const string CommandName = "defaultarena";

        public PlayerArenaPreference(
            IArenaManager arenaManager,
            IChat chat,
            ICommandManager commandManager,
            ILogManager logManager,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _broker = broker;
            _persist = broker.GetInterface<IPersist>();

            if (_persist is null)
                _logManager.LogM(LogLevel.Warn, nameof(PlayerArenaPreference), "IPersist not available — arena preference will not be saved across sessions.");

            _pdKey = _playerData.AllocatePlayerData<PlayerPreferenceData>();

            if (_persist is not null)
            {
                _persistRegistration = new DelegatePersistentData<Player>(
                    (int)Persist.PersistKey.PlayerArenaPreference,
                    PersistInterval.Forever,
                    PersistScope.Global,
                    Persist_GetData,
                    Persist_SetData,
                    Persist_ClearData);

                await _persist.RegisterPersistentDataAsync(_persistRegistration);
            }

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            _commandManager.AddCommand(CommandName, Command_Arena);
            return true;
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _commandManager.RemoveCommand(CommandName, Command_Arena);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);

            if (_persist is not null && _persistRegistration is not null)
            {
                await _persist.UnregisterPersistentDataAsync(_persistRegistration);
                _persistRegistration = null;
            }

            if (_persist is not null)
                broker.ReleaseInterface(ref _persist);

            _playerData.FreePlayerData(ref _pdKey);
            _broker = null;
            return true;
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action != PlayerAction.EnterArena)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerPreferenceData? data))
                return;

            if (data.HasBeenRedirected)
                return;

            // Mark as handled for this session regardless of whether a redirect is needed.
            data.HasBeenRedirected = true;

            if (string.IsNullOrEmpty(data.PreferredArena))
                return;

            // Don't redirect if the player is already in their preferred arena.
            if (arena is not null && arena.Name.Equals(data.PreferredArena, StringComparison.OrdinalIgnoreCase))
                return;

            _arenaManager.SendToArena(player, data.PreferredArena, 0, 0);
        }

        #region Persist

        private void Persist_GetData(Player? player, Stream outStream)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerPreferenceData? data))
                return;

            if (string.IsNullOrEmpty(data.PreferredArena))
                return;

            outStream.Write(Encoding.ASCII.GetBytes(data.PreferredArena));
        }

        private void Persist_SetData(Player? player, Stream inStream)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerPreferenceData? data))
                return;

            Span<byte> buffer = stackalloc byte[Constants.MaxArenaNameLength];
            int bytesRead = inStream.Read(buffer);
            if (bytesRead <= 0)
                return;

            string arenaName = Encoding.ASCII.GetString(buffer[..bytesRead]);
            if (arenaName.Length > Constants.MaxArenaNameLength)
            {
                _logManager.LogP(LogLevel.Warn, nameof(PlayerArenaPreference), player, $"Persist_SetData: stored arena name '{arenaName}' exceeds max length, ignoring.");
                return;
            }

            data.PreferredArena = arenaName;
        }

        private void Persist_ClearData(Player? player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerPreferenceData? data))
                return;

            data.PreferredArena = null;
        }

        #endregion

        #region Command

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[-clear|-c | <arena name>]",
            Description = """
                Controls which arena you are automatically sent to when you first enter the zone.
                - Use with no argument to see your current setting.
                - Use -clear (or -c) to remove the preference.
                - Use an arena name to set the preference.
                """)]
        private void Command_Arena(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerPreferenceData? data))
                return;

            if (parameters.IsEmpty)
            {
                if (string.IsNullOrEmpty(data.PreferredArena))
                    _chat.SendMessage(player, "Your arena preference is not set.");
                else
                    _chat.SendMessage(player, $"Your arena preference is: {data.PreferredArena}");
                return;
            }

            if (parameters.Equals("-clear", StringComparison.OrdinalIgnoreCase) || parameters.Equals("-c", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(data.PreferredArena))
                {
                    _chat.SendMessage(player, "Your arena preference is already not set.");
                    return;
                }

                data.PreferredArena = null;
                _chat.SendMessage(player, "Arena preference cleared.");
                return;
            }

            if (parameters.Length > Constants.MaxArenaNameLength)
            {
                _chat.SendMessage(player, $"Arena name too long (max {Constants.MaxArenaNameLength} characters).");
                return;
            }

            string newArena = parameters.ToString();

            if (newArena.Equals(data.PreferredArena, StringComparison.OrdinalIgnoreCase))
            {
                _chat.SendMessage(player, $"Your arena preference is already set to: {data.PreferredArena}");
                return;
            }

            data.PreferredArena = newArena;
            _chat.SendMessage(player, $"Arena preference set to: {newArena}");
        }

        #endregion

        private sealed class PlayerPreferenceData : IResettable
        {
            /// <summary>The player's preferred arena name, or <see langword="null"/> if not set.</summary>
            public string? PreferredArena;

            /// <summary>Session flag: whether the player has already been redirected (or checked) this session.</summary>
            public bool HasBeenRedirected;

            bool IResettable.TryReset()
            {
                PreferredArena = null;
                HasBeenRedirected = false;
                return true;
            }
        }
    }
}
