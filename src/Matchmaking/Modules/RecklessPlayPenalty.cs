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
    public sealed class RecklessPlayPenalty(
        IChat chat,
        IConfigManager configManager,
        ILogManager logManager,
        IPlayerData playerData,
        IPlayManager playManager) : IModule, IArenaAttachableModule
    {
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        private readonly IPlayManager _playManager = playManager ?? throw new ArgumentNullException(nameof(playManager));

        private readonly Dictionary<Arena, ArenaData> _arenaDataDictionary = new(Constants.TargetArenaCount);

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
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
        [ConfigHelp<bool>("SS.Matchmaking.RecklessPlayPenalty", "NotifyPlayer", ConfigScope.Arena, Default = true,
            Description = "Set to 1 to privately notify a player when a reckless play queue hold is applied.")]
        [ConfigHelp<bool>("SS.Matchmaking.RecklessPlayPenalty", "NotifyArena", ConfigScope.Arena, Default = false,
            Description = "Set to 1 to notify the arena when a reckless play queue hold is applied.")]
        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            ArenaData arenaData = new();

            arenaData.Enabled        = _configManager.GetBool(arena.Cfg!, "SS.Matchmaking.RecklessPlayPenalty", "Enabled", false);
            arenaData.Threshold      = TimeSpan.FromSeconds(_configManager.GetInt(arena.Cfg!, "SS.Matchmaking.RecklessPlayPenalty", "ThresholdSeconds",      180));
            arenaData.PenaltyMinimum = TimeSpan.FromSeconds(_configManager.GetInt(arena.Cfg!, "SS.Matchmaking.RecklessPlayPenalty", "PenaltyMinimumSeconds", 120));
            arenaData.PenaltyMaximum = TimeSpan.FromSeconds(_configManager.GetInt(arena.Cfg!, "SS.Matchmaking.RecklessPlayPenalty", "PenaltyMaximumSeconds", 600));
            arenaData.NotifyPlayer   = _configManager.GetBool(arena.Cfg!, "SS.Matchmaking.RecklessPlayPenalty", "NotifyPlayer", true);
            arenaData.NotifyArena    = _configManager.GetBool(arena.Cfg!, "SS.Matchmaking.RecklessPlayPenalty", "NotifyArena", false);

            if (arenaData.Enabled)
            {
                if (arenaData.Threshold <= TimeSpan.Zero)
                {
                    _logManager.LogA(LogLevel.Warn, nameof(RecklessPlayPenalty), arena,
                        $"ThresholdSeconds must be positive. Reckless play penalty will never trigger.");
                }

                if (arenaData.PenaltyMinimum > arenaData.PenaltyMaximum)
                {
                    _logManager.LogA(LogLevel.Warn, nameof(RecklessPlayPenalty), arena,
                        $"PenaltyMinimumSeconds ({arenaData.PenaltyMinimum.TotalSeconds}) is greater than PenaltyMaximumSeconds ({arenaData.PenaltyMaximum.TotalSeconds}). Penalty will increase with elapsed time instead of decrease.");
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

            foreach (Dictionary<string, PendingPenalty> matchPenalties in arenaData.PendingPenalties.Values)
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

            Player? player = killedSlot.Player;
            if (player is null)
                return;

            int knockoutOrder = GetKnockoutOrder(matchData);
            bool isFirstOut = knockoutOrder == 1;
            string reason = isFirstOut ? "first out before the prac integrity threshold" : "early death-out";

            if (!arenaData.PendingPenalties.TryGetValue(matchData, out Dictionary<string, PendingPenalty>? matchPenalties))
            {
                matchPenalties = new Dictionary<string, PendingPenalty>(StringComparer.OrdinalIgnoreCase);
                arenaData.PendingPenalties[matchData] = matchPenalties;
            }

            // A player can only be KO'd once per match, but guard defensively and keep the larger penalty.
            if (!matchPenalties.TryGetValue(player.Name!, out PendingPenalty? existing) || penalty > existing.Penalty)
            {
                matchPenalties[player.Name!] = new PendingPenalty(
                    player.Name!,
                    penalty,
                    elapsed,
                    knockoutOrder,
                    isFirstOut,
                    reason);
            }

            _logManager.LogP(LogLevel.Info, nameof(RecklessPlayPenalty), player,
                $"Reckless KO at {elapsed.TotalSeconds:F1}s into match (threshold: {arenaData.Threshold.TotalSeconds}s, order: {knockoutOrder}, reason: {reason}). Pending penalty: {penalty.TotalSeconds:F0}s.");
        }

        private void Callback_TeamVersusMatchEnded(IMatchData matchData, MatchEndReason reason, ITeam? winnerTeam)
        {
            Arena? arena = matchData.Arena;
            if (arena is null)
                return;

            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            // Always remove from the dictionary to prevent memory leaks, regardless of whether penalties are applied.
            if (!arenaData.PendingPenalties.Remove(matchData, out Dictionary<string, PendingPenalty>? matchPenalties))
                return;

            if (!arenaData.Enabled)
                return;

            // A cancelled match never reached InProgress, so no reckless KOs could have occurred.
            // (This also protects against any hypothetical edge cases where data was recorded before cancellation.)
            if (reason == MatchEndReason.Cancelled)
                return;

            foreach (PendingPenalty pendingPenalty in matchPenalties.Values)
            {
                // Apply the hold. Safe even if the player was already removed from the Playing state
                // by another mechanism — UnsetPlayingWithHold early-returns when the name is not found.
                _playManager.AddPlayHold(pendingPenalty.PlayerName, pendingPenalty.Penalty);

                if (arenaData.NotifyPlayer)
                {
                    Player? player = _playerData.FindPlayer(pendingPenalty.PlayerName);
                    if (player is not null)
                    {
                        _chat.SendMessage(player,
                            $"You received a {FormatNoticeDuration(pendingPenalty.Penalty)} queue timeout for {GetPlayerNoticeReason(pendingPenalty)}.");
                    }
                }

                if (arenaData.NotifyArena)
                {
                    _chat.SendArenaMessage(arena,
                        $"NOTICE: {pendingPenalty.PlayerName} received a {FormatNoticeDuration(pendingPenalty.Penalty)} queue timeout for {pendingPenalty.Reason}.");
                }

                _logManager.LogM(LogLevel.Info, nameof(RecklessPlayPenalty),
                    $"[{arena.Name}] [{pendingPenalty.PlayerName}] Reckless play penalty applied: {pendingPenalty.Penalty.TotalSeconds:F0}s. " +
                    $"Elapsed: {pendingPenalty.KnockoutElapsedTime.TotalSeconds:F1}s. Order: {pendingPenalty.KnockoutOrder}. FirstOut: {pendingPenalty.IsFirstOut}. Reason: {pendingPenalty.Reason}.");
            }
        }

        #endregion

        private static int GetKnockoutOrder(IMatchData matchData)
        {
            int knockedOutCount = 0;

            foreach (ITeam team in matchData.Teams)
            {
                foreach (IPlayerSlot slot in team.Slots)
                {
                    if (slot.Status == PlayerSlotStatus.KnockedOut)
                        knockedOutCount++;
                }
            }

            return Math.Max(1, knockedOutCount);
        }

        private static string GetPlayerNoticeReason(PendingPenalty pendingPenalty)
        {
            return pendingPenalty.IsFirstOut
                ? pendingPenalty.Reason
                : $"{pendingPenalty.Reason} before the prac integrity threshold";
        }

        private static string FormatNoticeDuration(TimeSpan duration)
        {
            int totalSeconds = Math.Max(0, (int)duration.TotalSeconds);
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;

            if (minutes > 0 && seconds > 0)
                return $"{minutes} {Pluralize(minutes, "minute")} {seconds} {Pluralize(seconds, "second")}";

            if (minutes > 0)
                return $"{minutes} {Pluralize(minutes, "minute")}";

            return $"{seconds} {Pluralize(seconds, "second")}";
        }

        private static string Pluralize(int value, string singular)
        {
            return value == 1 ? singular : $"{singular}s";
        }

        private sealed record PendingPenalty(
            string PlayerName,
            TimeSpan Penalty,
            TimeSpan KnockoutElapsedTime,
            int KnockoutOrder,
            bool IsFirstOut,
            string Reason);

        private sealed class ArenaData
        {
            public bool Enabled;
            public bool NotifyPlayer;
            public bool NotifyArena;
            public TimeSpan Threshold;
            public TimeSpan PenaltyMinimum;
            public TimeSpan PenaltyMaximum;

            // Outer key: IMatchData (reference equality — match objects live for the match duration)
            // Inner key: player name (OrdinalIgnoreCase); value: pending penalty details captured at the moment of KO
            public readonly Dictionary<IMatchData, Dictionary<string, PendingPenalty>> PendingPenalties = [];
        }
    }
}
