using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that penalizes players who get KO'd too quickly after a match starts.
    /// <para>
    /// If a player loses all their lives within a configured time window after match start,
    /// they receive a queue hold that prevents them from playing in another match for a
    /// duration that scales with how quickly they were eliminated.
    /// </para>
    /// <para>For use with the <see cref="TeamVersusMatch"/> module.</para>
    /// </summary>
    [ModuleInfo($"""
        Penalizes players that get KO'd too quickly after a match starts.
        For use with the {nameof(TeamVersusMatch)} module.
        """)]
    public sealed class RecklessPlayPenalty : IModule, IArenaAttachableModule
    {
        private readonly IChat _chat;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IPlayerData _playerData;

        private IMatchmakingQueues? _matchmakingQueues;

        private readonly Dictionary<Arena, ArenaData> _arenaDataDictionary = new(Constants.TargetArenaCount);

        public RecklessPlayPenalty(
            IChat chat,
            IConfigManager configManager,
            ILogManager logManager,
            IPlayerData playerData)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _matchmakingQueues = broker.GetInterface<IMatchmakingQueues>();
            if (_matchmakingQueues is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(RecklessPlayPenalty), $"Unable to get {nameof(IMatchmakingQueues)}.");
                return false;
            }

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            broker.ReleaseInterface(ref _matchmakingQueues);
            return true;
        }

        #endregion

        #region IArenaAttachableModule members

        [ConfigHelp<bool>("SS.Matchmaking.RecklessPlayPenalty", "Enabled", ConfigScope.Arena, Default = false,
            Description = "Set to 1 to enable the reckless play penalty feature.")]
        [ConfigHelp<int>("SS.Matchmaking.RecklessPlayPenalty", "ThresholdSeconds", ConfigScope.Arena, Default = 180,
            Description = "KO within this many seconds of match start counts as reckless.")]
        [ConfigHelp<int>("SS.Matchmaking.RecklessPlayPenalty", "PenaltyMinimumSeconds", ConfigScope.Arena, Default = 120,
            Description = "Hold duration (seconds) when KO'd right at the threshold boundary.")]
        [ConfigHelp<int>("SS.Matchmaking.RecklessPlayPenalty", "PenaltyMaximumSeconds", ConfigScope.Arena, Default = 600,
            Description = "Hold duration (seconds) when KO'd almost instantly after match start.")]
        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            ArenaData arenaData = new();

            arenaData.Enabled        = _configManager.GetBool(arena.Cfg!, "SS.Matchmaking.RecklessPlayPenalty", "Enabled", false);
            arenaData.Threshold      = TimeSpan.FromSeconds(_configManager.GetInt(arena.Cfg!, "SS.Matchmaking.RecklessPlayPenalty", "ThresholdSeconds",      180));
            arenaData.PenaltyMinimum = TimeSpan.FromSeconds(_configManager.GetInt(arena.Cfg!, "SS.Matchmaking.RecklessPlayPenalty", "PenaltyMinimumSeconds", 120));
            arenaData.PenaltyMaximum = TimeSpan.FromSeconds(_configManager.GetInt(arena.Cfg!, "SS.Matchmaking.RecklessPlayPenalty", "PenaltyMaximumSeconds", 600));

            if (arenaData.Enabled)
            {
                if (arenaData.Threshold <= TimeSpan.Zero)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(RecklessPlayPenalty),
                        $"[{arena.Name}] ThresholdSeconds must be positive. Reckless play penalty will never trigger.");
                }

                if (arenaData.PenaltyMinimum > arenaData.PenaltyMaximum)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(RecklessPlayPenalty),
                        $"[{arena.Name}] PenaltyMinimumSeconds ({arenaData.PenaltyMinimum.TotalSeconds}) is greater than PenaltyMaximumSeconds ({arenaData.PenaltyMaximum.TotalSeconds}). Penalty will increase with elapsed time instead of decrease.");
                }
            }

            _arenaDataDictionary.Add(arena, arenaData);

            TeamVersusMatchPlayerKilledCallback.Register(arena, Callback_TeamVersusMatchPlayerKilled);
            TeamVersusMatchEndedCallback.Register(arena, Callback_TeamVersusMatchEnded);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            TeamVersusMatchPlayerKilledCallback.Unregister(arena, Callback_TeamVersusMatchPlayerKilled);
            TeamVersusMatchEndedCallback.Unregister(arena, Callback_TeamVersusMatchEnded);

            if (!_arenaDataDictionary.Remove(arena, out ArenaData? arenaData))
                return false;

            foreach (Dictionary<string, (TimeSpan Penalty, TimeSpan ElapsedAtKo)> matchPenalties in arenaData.PendingPenalties.Values)
                matchPenalties.Clear();
            arenaData.PendingPenalties.Clear();

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

            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.Enabled)
                return;

            if (arenaData.Threshold <= TimeSpan.Zero)
                return;

            if (matchData.Started is not { } started)
                return;

            TimeSpan elapsed = DateTime.UtcNow - started;

            if (elapsed >= arenaData.Threshold)
                return;

            // Penalty scales linearly from PenaltyMaximum (instant KO) down to PenaltyMinimum (KO just at threshold).
            double t = elapsed.TotalSeconds / arenaData.Threshold.TotalSeconds;
            double penaltySeconds = arenaData.PenaltyMaximum.TotalSeconds
                + t * (arenaData.PenaltyMinimum.TotalSeconds - arenaData.PenaltyMaximum.TotalSeconds);
            TimeSpan penalty = TimeSpan.FromSeconds(penaltySeconds);

            // Clamp as a safety net against floating-point edge cases.
            if (penalty < arenaData.PenaltyMinimum) penalty = arenaData.PenaltyMinimum;
            if (penalty > arenaData.PenaltyMaximum) penalty = arenaData.PenaltyMaximum;

            string? playerName = killedSlot.PlayerName;
            if (string.IsNullOrEmpty(playerName))
                return;

            if (!arenaData.PendingPenalties.TryGetValue(matchData, out Dictionary<string, (TimeSpan Penalty, TimeSpan ElapsedAtKo)>? matchPenalties))
            {
                matchPenalties = new Dictionary<string, (TimeSpan, TimeSpan)>(StringComparer.OrdinalIgnoreCase);
                arenaData.PendingPenalties[matchData] = matchPenalties;
            }

            // A player can only be KO'd once per match, but guard defensively and keep the larger penalty.
            if (!matchPenalties.TryGetValue(playerName, out (TimeSpan Penalty, TimeSpan ElapsedAtKo) existing) || penalty > existing.Penalty)
                matchPenalties[playerName] = (penalty, elapsed);

            _logManager.LogM(LogLevel.Info, nameof(RecklessPlayPenalty),
                $"[{arena.Name}] [{playerName}] Reckless KO at {elapsed.TotalSeconds:F1}s into match (threshold: {arenaData.Threshold.TotalSeconds}s). Pending penalty: {penalty.TotalSeconds:F0}s.");
        }

        private void Callback_TeamVersusMatchEnded(IMatchData matchData, MatchEndReason reason, ITeam? winnerTeam)
        {
            Arena? arena = matchData.Arena;
            if (arena is null)
                return;

            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            // Always remove from the dictionary to prevent memory leaks, regardless of whether penalties are applied.
            if (!arenaData.PendingPenalties.Remove(matchData, out Dictionary<string, (TimeSpan Penalty, TimeSpan ElapsedAtKo)>? matchPenalties))
                return;

            if (!arenaData.Enabled)
                return;

            // A cancelled match never reached InProgress, so no reckless KOs could have occurred.
            // (This also protects against any hypothetical edge cases where data was recorded before cancellation.)
            if (reason == MatchEndReason.Cancelled)
                return;

            foreach ((string playerName, (TimeSpan penalty, TimeSpan elapsedAtKo)) in matchPenalties)
            {
                // Apply the hold. Safe even if the player was already removed from the Playing state
                // by another mechanism — UnsetPlayingWithHold early-returns when the name is not found.
                _matchmakingQueues!.UnsetPlayingWithHold(playerName, penalty);

                Player? player = _playerData.FindPlayer(playerName);
                if (player is not null)
                {
                    _chat.SendMessage(player,
                        $"You were KO'd too quickly ({FormatDuration(elapsedAtKo)} into the match). " +
                        $"You must wait {FormatDuration(penalty)} before queuing again.");
                }

                _logManager.LogM(LogLevel.Info, nameof(RecklessPlayPenalty),
                    $"[{arena.Name}] [{playerName}] Reckless play penalty applied: {penalty.TotalSeconds:F0}s.");
            }
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
            public TimeSpan Threshold;
            public TimeSpan PenaltyMinimum;
            public TimeSpan PenaltyMaximum;

            // Outer key: IMatchData (reference equality — match objects live for the match duration)
            // Inner key: player name (OrdinalIgnoreCase); value: penalty duration and elapsed time at the moment of KO
            public readonly Dictionary<IMatchData, Dictionary<string, (TimeSpan Penalty, TimeSpan ElapsedAtKo)>> PendingPenalties = [];
        }
    }
}
