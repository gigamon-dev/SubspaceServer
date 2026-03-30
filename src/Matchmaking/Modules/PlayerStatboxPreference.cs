using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.Persist;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that manages the per-player statbox display preference.
    /// Provides the ?statbox command and persists the preference globally.
    /// </summary>
    [ModuleInfo("Manages per-player statbox display preference.")]
    public sealed class PlayerStatboxPreference : IAsyncModule, IPlayerStatboxPreference
    {
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IPlayerData _playerData;

        private IPersist? _persist;

        private PlayerDataKey<PlayerPreferenceData> _pdKey;
        private DelegatePersistentData<Player>? _persistRegistration;
        private InterfaceRegistrationToken<IPlayerStatboxPreference>? _iToken;

        private IComponentBroker? _broker;

        private const string CommandName = "statbox";

        public PlayerStatboxPreference(
            IChat chat,
            ICommandManager commandManager,
            IPlayerData playerData)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _broker = broker;
            _persist = broker.GetInterface<IPersist>();

            _pdKey = _playerData.AllocatePlayerData<PlayerPreferenceData>();

            if (_persist is not null)
            {
                _persistRegistration = new DelegatePersistentData<Player>(
                    (int)Persist.PersistKey.PlayerStatboxPreference,
                    PersistInterval.Forever,
                    PersistScope.Global,
                    Persist_GetData,
                    Persist_SetData,
                    Persist_ClearData);

                await _persist.RegisterPersistentDataAsync(_persistRegistration);
            }

            _commandManager.AddCommand(CommandName, Command_Statbox);

            _iToken = broker.RegisterInterface<IPlayerStatboxPreference>(this);
            return true;
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (_iToken is not null)
                broker.UnregisterInterface(ref _iToken);

            _commandManager.RemoveCommand(CommandName, Command_Statbox);

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

        StatboxPreference IPlayerStatboxPreference.GetPreference(Player player)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerPreferenceData? data))
                return StatboxPreference.Detailed;

            return data.Preference;
        }

        #region Persist

        private void Persist_GetData(Player? player, Stream outStream)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerPreferenceData? data))
                return;

            // Only write if non-default.
            if (data.Preference == StatboxPreference.Detailed)
                return;

            outStream.WriteByte((byte)data.Preference);
        }

        private void Persist_SetData(Player? player, Stream inStream)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerPreferenceData? data))
                return;

            int value = inStream.ReadByte();
            if (value < 0)
                return;

            StatboxPreference pref = (StatboxPreference)value;
            if (!Enum.IsDefined(pref))
                pref = StatboxPreference.Detailed;

            data.Preference = pref;
        }

        private void Persist_ClearData(Player? player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerPreferenceData? data))
                return;

            data.Preference = StatboxPreference.Detailed;
        }

        #endregion

        #region Command

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[off | simple | detailed]",
            Description = """
                Controls which statbox display style you see during matches.
                - detailed: Shows names, lives remaining, repels, and rockets (default).
                - simple: Shows names with kills and deaths columns.
                - off: Hides the statbox entirely.
                Use with no argument to see your current setting.
                """)]
        private void Command_Statbox(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerPreferenceData? data))
                return;

            if (parameters.IsEmpty)
            {
                _chat.SendMessage(player, $"Your statbox preference is currently: {data.Preference}");
                return;
            }

            StatboxPreference newPref;
            if (parameters.Equals("off", StringComparison.OrdinalIgnoreCase))
                newPref = StatboxPreference.Off;
            else if (parameters.Equals("simple", StringComparison.OrdinalIgnoreCase))
                newPref = StatboxPreference.Simple;
            else if (parameters.Equals("detailed", StringComparison.OrdinalIgnoreCase))
                newPref = StatboxPreference.Detailed;
            else
            {
                _chat.SendMessage(player, $"Unknown option '{parameters}'. Use: off, simple, or detailed.");
                return;
            }

            if (newPref == data.Preference)
            {
                _chat.SendMessage(player, $"Your statbox preference is already set to: {newPref}");
                return;
            }

            data.Preference = newPref;
            _chat.SendMessage(player, $"Statbox preference set to: {newPref}");

            if (_broker is not null)
                StatboxPreferenceChangedCallback.Fire(_broker, player, newPref);
        }

        #endregion

        private sealed class PlayerPreferenceData : IResettable
        {
            public StatboxPreference Preference = StatboxPreference.Detailed;

            bool IResettable.TryReset()
            {
                Preference = StatboxPreference.Detailed;
                return true;
            }
        }
    }
}
