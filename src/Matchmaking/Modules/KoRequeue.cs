using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that allows KO'd players to re-enter the matchmaking queue after a configurable cooldown,
    /// potentially joining a new match before their old one finishes.
    /// <para>
    /// Early requeue is only granted if the player has no pending reckless play penalty for the match.
    /// The player's auto-requeue preference is respected: if they have auto-requeue enabled they are
    /// placed back in the queue automatically; otherwise they must type <c>?next</c> again.
    /// </para>
    /// <para>For use with the <see cref="TeamVersusMatch"/> module.</para>
    /// </summary>
    [ModuleInfo($"""
        Allows KO'd players to re-enter the matchmaking queue after a configurable cooldown.
        For use with the {nameof(TeamVersusMatch)} module.
        """)]
    public sealed class KoRequeue : IModule, IArenaAttachableModule
    {
        private readonly IChat _chat;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IPlayerData _playerData;

        private IMatchmakingQueues? _matchmakingQueues;
        private IKoEarlyRequeue? _koEarlyRequeue;
        private IRecklessPlayPenalty? _recklessPlayPenalty; // optional

        private readonly Dictionary<Arena, ArenaData> _arenaDataDictionary = new(Constants.TargetArenaCount);

        public KoRequeue(
            IChat chat,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            IPlayerData playerData)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _matchmakingQueues = broker.GetInterface<IMatchmakingQueues>();
            if (_matchmakingQueues is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(KoRequeue), $"Unable to get {nameof(IMatchmakingQueues)}.");
                return false;
            }

            _koEarlyRequeue = broker.GetInterface<IKoEarlyRequeue>();
            if (_koEarlyRequeue is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(KoRequeue), $"Unable to get {nameof(IKoEarlyRequeue)}.");
                broker.ReleaseInterface(ref _matchmakingQueues);
                return false;
            }

            // Optional: if RecklessPlayPenalty is not loaded, all KO'd players are eligible for early requeue.
            _recklessPlayPenalty = broker.GetInterface<IRecklessPlayPenalty>();

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (_recklessPlayPenalty is not null)
                broker.ReleaseInterface(ref _recklessPlayPenalty);

            broker.ReleaseInterface(ref _koEarlyRequeue);
            broker.ReleaseInterface(ref _matchmakingQueues);
            return true;
        }

        #endregion

        #region IArenaAttachableModule members

        [ConfigHelp<bool>("SS.Matchmaking.KoRequeue", "Enabled", ConfigScope.Arena, Default = false,
            Description = "Set to 1 to enable early requeue for KO'd players.")]
        [ConfigHelp<int>("SS.Matchmaking.KoRequeue", "CooldownSeconds", ConfigScope.Arena, Default = 30,
            Description = "Seconds after KO before the player may queue for a new match.")]
        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            ArenaData arenaData = new();
            arenaData.Enabled  = _configManager.GetBool(arena.Cfg!, "SS.Matchmaking.KoRequeue", "Enabled", false);
            arenaData.Cooldown = TimeSpan.FromSeconds(_configManager.GetInt(arena.Cfg!, "SS.Matchmaking.KoRequeue", "CooldownSeconds", 30));

            _arenaDataDictionary.Add(arena, arenaData);

            TeamVersusMatchPlayerKilledCallback.Register(arena, Callback_TeamVersusMatchPlayerKilled);
            TeamVersusMatchEndedCallback.Register(arena, Callback_TeamVersusMatchEnded);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            TeamVersusMatchPlayerKilledCallback.Unregister(arena, Callback_TeamVersusMatchPlayerKilled);
            TeamVersusMatchEndedCallback.Unregister(arena, Callback_TeamVersusMatchEnded);

            _arenaDataDictionary.Remove(arena);
            return true;
        }

        #endregion

        #region Callbacks

        private void Callback_TeamVersusMatchPlayerKilled(IPlayerSlot killedSlot, IPlayerSlot killerSlot, bool isKnockout)
        {
            if (!isKnockout)
                return;

            IMatchData matchData = killedSlot.MatchData;
            Arena? arena = matchData.Arena;
            if (arena is null)
                return;

            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData) || !arenaData.Enabled)
                return;

            string? playerName = killedSlot.PlayerName;
            if (string.IsNullOrEmpty(playerName))
                return;

            // If the player has a pending reckless play penalty, they forfeit early requeue.
            if (_recklessPlayPenalty?.HasPendingPenalty(matchData, playerName) == true)
                return;

            Player? player = killedSlot.Player;
            if (player is not null)
            {
                _chat.SendMessage(player,
                    $"You have been knocked out. You may queue for a new match in {FormatDuration(arenaData.Cooldown)}.");
            }

            _mainloopTimer.SetTimer(MainloopTimer_KoCooldown, (int)arenaData.Cooldown.TotalMilliseconds, Timeout.Infinite,
                new KoTimerContext(matchData, playerName), matchData);

            _logManager.LogM(LogLevel.Info, nameof(KoRequeue),
                $"[{arena.Name}] [{playerName}] KO'd; early requeue cooldown started ({arenaData.Cooldown.TotalSeconds:F0}s).");
        }

        private void Callback_TeamVersusMatchEnded(IMatchData matchData, MatchEndReason reason, ITeam? winnerTeam)
        {
            // Cancel any pending cooldown timers for this match.
            _mainloopTimer.ClearTimer<KoTimerContext>(MainloopTimer_KoCooldown, matchData);
        }

        #endregion

        #region Timer

        private bool MainloopTimer_KoCooldown(KoTimerContext context)
        {
            IMatchData matchData = context.MatchData;
            string playerName = context.PlayerName;

            // Defensive check: verify the player's slot is still in KnockedOut status.
            // (A sub could theoretically fill the slot before the timer fires.)
            bool stillKnockedOut = false;
            foreach (ITeam team in matchData.Teams)
            {
                foreach (IPlayerSlot slot in team.Slots)
                {
                    if (string.Equals(slot.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
                    {
                        stillKnockedOut = slot.Status == PlayerSlotStatus.KnockedOut;
                        goto foundSlot;
                    }
                }
            }
            foundSlot:

            if (!stillKnockedOut)
                return false;

            // Free the player from the Playing state so they can queue again.
            // allowRequeue: true respects the player's auto-requeue preference:
            //   - if auto-requeue is on  → they are placed back in queue automatically
            //   - if auto-requeue is off → PreviousQueued is cleared; they must type ?next
            _matchmakingQueues!.UnsetPlayingByName(playerName, allowRequeue: true);

            // Mark this in the old match's participation record so EndMatch skips them.
            _koEarlyRequeue!.MarkPlayerKoEarlyRequeued(matchData, playerName);

            Player? player = _playerData.FindPlayer(playerName);
            if (player is not null)
            {
                _chat.SendMessage(player, "You are now free to queue for a new match. Use ?next to join.");
            }

            _logManager.LogM(LogLevel.Info, nameof(KoRequeue),
                $"[{matchData.ArenaName}] [{playerName}] Early requeue cooldown elapsed; player freed from Playing state.");

            return false; // one-shot timer
        }

        #endregion

        private static string FormatDuration(TimeSpan duration)
        {
            int totalSeconds = Math.Max(0, (int)duration.TotalSeconds);
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
        }

        private sealed class ArenaData
        {
            public bool Enabled;
            public TimeSpan Cooldown;
        }

        private sealed class KoTimerContext(IMatchData matchData, string playerName)
        {
            public IMatchData MatchData { get; } = matchData;
            public string PlayerName { get; } = playerName;
        }
    }
}
