using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Interfaces;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that manages the per-player matchmaking mode preference (Casual / Strict).
    /// Provides the ?matchmaking command.
    /// </summary>
    [ModuleInfo("Manages per-player matchmaking mode preference (casual/strict).")]
    public sealed class MatchmakingPreference : IModule, IMatchmakingPreferences
    {
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IPlayerData _playerData;

        private PlayerDataKey<PreferenceData> _pdKey;
        private InterfaceRegistrationToken<IMatchmakingPreferences>? _iToken;

        private const string CommandName = "matchmaking";

        public MatchmakingPreference(
            IChat chat,
            ICommandManager commandManager,
            IPlayerData playerData)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        bool IModule.Load(IComponentBroker broker)
        {
            _pdKey = _playerData.AllocatePlayerData<PreferenceData>();
            _commandManager.AddCommand(CommandName, Command_Matchmaking);
            _iToken = broker.RegisterInterface<IMatchmakingPreferences>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iToken) != 0)
                return false;

            _commandManager.RemoveCommand(CommandName, Command_Matchmaking);
            _playerData.FreePlayerData(ref _pdKey);
            return true;
        }

        MatchmakingMode IMatchmakingPreferences.GetMatchmakingMode(string playerName)
        {
            Player? player = _playerData.FindPlayer(playerName);
            if (player is null)
                return MatchmakingMode.Casual;

            return player.TryGetExtraData(_pdKey, out PreferenceData? d) ? d.Mode : MatchmakingMode.Casual;
        }

        MatchmakingMode IMatchmakingPreferences.SetMatchmakingMode(Player player, MatchmakingMode mode)
        {
            if (!player.TryGetExtraData(_pdKey, out PreferenceData? d))
                return MatchmakingMode.Casual;

            d.Mode = mode;
            return d.Mode;
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[strict | casual]",
            Description = """
                Controls your matchmaking preference.
                - casual: Default. No restrictions on skill disparity.
                - strict: Prefer not to be placed in matches with large skill gaps.
                Use with no argument to see your current setting.
                """)]
        private void Command_Matchmaking(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PreferenceData? data))
                return;

            if (parameters.IsEmpty)
            {
                _chat.SendMessage(player, $"Matchmaking preference: {data.Mode}");
                return;
            }

            MatchmakingMode newMode;
            if (parameters.Equals("strict", StringComparison.OrdinalIgnoreCase))
                newMode = MatchmakingMode.Strict;
            else if (parameters.Equals("casual", StringComparison.OrdinalIgnoreCase))
                newMode = MatchmakingMode.Casual;
            else
            {
                _chat.SendMessage(player, $"Unknown option '{parameters}'. Use: strict or casual.");
                return;
            }

            if (newMode == data.Mode)
            {
                _chat.SendMessage(player, $"Matchmaking preference is already set to: {newMode}");
                return;
            }

            data.Mode = newMode;
            _chat.SendMessage(player, $"Matchmaking preference: {newMode}");
        }

        private sealed class PreferenceData : IResettable
        {
            public MatchmakingMode Mode = MatchmakingMode.Casual;

            bool IResettable.TryReset()
            {
                Mode = MatchmakingMode.Casual;
                return true;
            }
        }
    }
}
