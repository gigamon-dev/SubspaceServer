using Microsoft.Extensions.ObjectPool;
using OpenSkillSharp;
using OpenSkillSharp.Models;
using SS.Core;
using System.Text.Json;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.League;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;
using SS.Utilities;
using System.Collections.ObjectModel;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module for captain-based team formation with a challenge system.
    /// Any player can become a captain, form a team, and challenge another captain to a match.
    /// The losing team is disbanded; the winning team stays on the field for the next challenger.
    /// Fires TeamVersus callbacks so <see cref="MatchLvz"/> can display the scoreboard.
    /// Supports multiple simultaneous matches when multiple freq pairs are configured.
    /// </summary>
    [ModuleInfo("Captain-based team formation with challenge system.")]
    public sealed class CaptainsMatch : IModule, IArenaAttachableModule, IMatchFocusAdvisor
    {
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly IGame _game;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;

        private IComponentBroker? _broker;
        private PlayerDataKey<PlayerData> _pdKey;
        private AdvisorRegistrationToken<IMatchFocusAdvisor>? _iMatchFocusAdvisorToken;

        // optional
        private ITeamVersusStatsBehavior? _tvStatsBehavior;

        private readonly Dictionary<Arena, ArenaData> _arenaDataDictionary = new(Constants.TargetArenaCount);
        private static readonly DefaultObjectPool<ArenaData> _arenaDataPool = new(new DefaultPooledObjectPolicy<ArenaData>(), Constants.TargetArenaCount);

        public CaptainsMatch(
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            IGame game,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _broker = broker;
            _tvStatsBehavior = broker.GetInterface<ITeamVersusStatsBehavior>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            _iMatchFocusAdvisorToken = broker.RegisterAdvisor<IMatchFocusAdvisor>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (!broker.UnregisterAdvisor(ref _iMatchFocusAdvisorToken))
                return false;

            _mainloop.WaitForMainWorkItemDrain();

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _playerData.FreePlayerData(ref _pdKey);

            if (_tvStatsBehavior is not null)
                broker.ReleaseInterface(ref _tvStatsBehavior);

            _broker = null;
            return true;
        }

        [ConfigHelp<long>("CaptainsMatch", "GameTypeId", ConfigScope.Arena,
            Description = "The GameTypeId to use when saving match stats to the database.")]
        [ConfigHelp<int>("CaptainsMatch", "PlayersPerTeam", ConfigScope.Arena, Default = 4,
            Description = "Number of players required per team (including the captain).")]
        [ConfigHelp<int>("CaptainsMatch", "LivesPerPlayer", ConfigScope.Arena, Default = 3,
            Description = "Number of lives each player starts with per match.")]
        [ConfigHelp<int>("CaptainsMatch", "DefaultShip", ConfigScope.Arena, Default = 1,
            Description = "Ship (1-8) assigned to players when they are moved to a freq (1=Warbird).")]
        [ConfigHelp<int>("CaptainsMatch", "Freq1", ConfigScope.Arena, Default = 100,
            Description = "Frequency for the first slot of match pair 1. Add Freq3/Freq4, Freq5/Freq6, etc. for additional simultaneous match slots.")]
        [ConfigHelp<int>("CaptainsMatch", "Freq2", ConfigScope.Arena, Default = 200,
            Description = "Frequency for the second slot of match pair 1.")]
        [ConfigHelp<int>("CaptainsMatch", "KickCooldownSeconds", ConfigScope.Arena, Default = 60,
            Description = "Seconds a kicked player must wait before they can ?join again. Resets when the current match ends.")]
        [ConfigHelp("CaptainsMatch", "TimeLimit", ConfigScope.Arena,
            Description = "Duration (TimeSpan, e.g. 00:30:00) of each match. Empty = untimed. When time expires the team whose score leads by TimeLimitWinBy wins; otherwise overtime or draw.")]
        [ConfigHelp("CaptainsMatch", "OverTimeLimit", ConfigScope.Arena,
            Description = "Duration (TimeSpan) of overtime when no team leads by TimeLimitWinBy at the end of regular time. Requires TimeLimit. Empty = no overtime (draw instead).")]
        [ConfigHelp("CaptainsMatch", "WinConditionDelay", ConfigScope.Arena, Default = "00:00:02",
            Description = "Delay (TimeSpan) after a knockout before checking for team elimination. Allows double-KO draws.")]
        [ConfigHelp<int>("CaptainsMatch", "TimeLimitWinBy", ConfigScope.Arena, Default = 2,
            Description = "Minimum score lead required for a time-limit win. If neither team leads by this amount, overtime begins (if configured) or the match draws.")]
        [ConfigHelp<int>("CaptainsMatch", "MaxLagOuts", ConfigScope.Arena, Default = 3,
            Description = "Maximum number of times a player may voluntarily spec out during a match before they cannot re-enter.")]
        [ConfigHelp("CaptainsMatch", "OpenSkillModel", ConfigScope.Arena, Default = "PlackettLuce",
            Description = "OpenSkill rating model. Options: PlackettLuce, BradleyTerryFull, BradleyTerryPart, ThurstoneMostellerFull, ThurstoneMostellerPart.")]
        [ConfigHelp("CaptainsMatch", "OpenSkillModelJson", ConfigScope.Arena,
            Description = "JSON parameters for the OpenSkill model (e.g. { \"Mu\":25.0, \"Sigma\":8.33 }). Empty uses model defaults.")]
        [ConfigHelp("CaptainsMatch", "OpenSkillSigmaDecayPerDay", ConfigScope.Arena, Default = "0",
            Description = "Amount added to a player's sigma per day of inactivity (rating uncertainty growth).")]
        [ConfigHelp<bool>("CaptainsMatch", "OpenSkillUseScoresWhenPossible", ConfigScope.Arena, Default = false,
            Description = "Whether to rate teams using kill scores rather than ranks when possible.")]
        [ConfigHelp("CaptainsMatch", "FreqNStartLocation", ConfigScope.Arena,
            Description = "Tile coordinates (x,y) to warp freq N's players to at match start. E.g., Freq100StartLocation = 354,354. Optional; omit to skip warping.")]
        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            ConfigHandle ch = arena.Cfg!;

            // GameTypeId
            long? gameTypeId;
            string? gameTypeIdStr = _configManager.GetStr(ch, "CaptainsMatch", "GameTypeId");
            if (string.IsNullOrWhiteSpace(gameTypeIdStr))
            {
                gameTypeId = null;
            }
            else if (long.TryParse(gameTypeIdStr, out long gameTypeIdLong))
            {
                gameTypeId = gameTypeIdLong;
            }
            else
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusMatch), "Invalid CaptainsMatch.GameTypeId; defaulting to none.");
                gameTypeId = null;
            }

            // Read freq pairs: Freq1/Freq2, Freq3/Freq4, Freq5/Freq6, ...
            var freqPairs = new List<(short F1, short F2)>();
            for (int i = 1; ; i += 2)
            {
                int f1 = _configManager.GetInt(ch, "CaptainsMatch", $"Freq{i}", i == 1 ? 100 : 0);
                int f2 = _configManager.GetInt(ch, "CaptainsMatch", $"Freq{i + 1}", i == 1 ? 200 : 0);
                if (f1 <= 0 || f2 <= 0)
                    break;
                freqPairs.Add(((short)f1, (short)f2));
            }

            if (freqPairs.Count == 0)
                freqPairs.Add((100, 200));

            // Read optional start locations: FreqNStartLocation = x,y for each freq in each pair.
            var startLocations = new Dictionary<short, (short X, short Y)>();
            foreach ((short f1, short f2) in freqPairs)
            {
                foreach (short freq in new[] { f1, f2 })
                {
                    string? raw = _configManager.GetStr(ch, "CaptainsMatch", $"Freq{freq}StartLocation");
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;
                    int comma = raw.IndexOf(',');
                    if (comma <= 0)
                        continue;
                    if (short.TryParse(raw.AsSpan(0, comma).Trim(), out short sx) &&
                        short.TryParse(raw.AsSpan(comma + 1).Trim(), out short sy))
                    {
                        startLocations[freq] = (sx, sy);
                    }
                }
            }

            ArenaData arenaData = _arenaDataPool.Get();
            arenaData.Arena = arena;
            // TimeLimit
            TimeSpan? timeLimit = null;
            string? timeLimitStr = _configManager.GetStr(ch, "CaptainsMatch", "TimeLimit");
            if (!string.IsNullOrWhiteSpace(timeLimitStr))
            {
                if (!TimeSpan.TryParse(timeLimitStr, out TimeSpan tl))
                    _logManager.LogA(LogLevel.Warn, nameof(CaptainsMatch), arena, "Invalid CaptainsMatch.TimeLimit; treated as untimed.");
                else
                    timeLimit = tl;
            }

            // OverTimeLimit (only meaningful when TimeLimit is set)
            TimeSpan? overTimeLimit = null;
            if (timeLimit is not null)
            {
                string? otlStr = _configManager.GetStr(ch, "CaptainsMatch", "OverTimeLimit");
                if (!string.IsNullOrWhiteSpace(otlStr))
                {
                    if (!TimeSpan.TryParse(otlStr, out TimeSpan otl))
                        _logManager.LogA(LogLevel.Warn, nameof(CaptainsMatch), arena, "Invalid CaptainsMatch.OverTimeLimit; no overtime.");
                    else
                        overTimeLimit = otl;
                }
            }

            // WinConditionDelay (default 2 seconds)
            TimeSpan winConditionDelay = TimeSpan.FromSeconds(2);
            string? wcdStr = _configManager.GetStr(ch, "CaptainsMatch", "WinConditionDelay");
            if (!string.IsNullOrWhiteSpace(wcdStr) && !TimeSpan.TryParse(wcdStr, out winConditionDelay))
            {
                _logManager.LogA(LogLevel.Warn, nameof(CaptainsMatch), arena, "Invalid CaptainsMatch.WinConditionDelay; defaulting to 2s.");
                winConditionDelay = TimeSpan.FromSeconds(2);
            }

            // OpenSkill model
            OpenSkill.ModelType modelType = _configManager.GetEnum(ch, "CaptainsMatch", "OpenSkillModel", OpenSkill.ModelType.PlackettLuce);
            string modelJson = _configManager.GetStr(ch, "CaptainsMatch", "OpenSkillModelJson") ?? "{}";
            if (string.IsNullOrWhiteSpace(modelJson))
                modelJson = "{}";

            IOpenSkillModel openSkillModel = modelType switch
            {
                OpenSkill.ModelType.PlackettLuce         => JsonSerializer.Deserialize<PlackettLuce>(modelJson) ?? new PlackettLuce(),
                OpenSkill.ModelType.BradleyTerryFull     => JsonSerializer.Deserialize<BradleyTerryFull>(modelJson) ?? new BradleyTerryFull(),
                OpenSkill.ModelType.BradleyTerryPart     => JsonSerializer.Deserialize<BradleyTerryPart>(modelJson) ?? new BradleyTerryPart(),
                OpenSkill.ModelType.ThurstoneMostellerFull => JsonSerializer.Deserialize<ThurstoneMostellerFull>(modelJson) ?? new ThurstoneMostellerFull(),
                OpenSkill.ModelType.ThurstoneMostellerPart => JsonSerializer.Deserialize<ThurstoneMostellerPart>(modelJson) ?? new ThurstoneMostellerPart(),
                _ => new PlackettLuce(),
            };

            string? sigmaDecayStr = _configManager.GetStr(ch, "CaptainsMatch", "OpenSkillSigmaDecayPerDay");
            double sigmaDecayPerDay = 0;
            if (!string.IsNullOrWhiteSpace(sigmaDecayStr) && !double.TryParse(sigmaDecayStr, out sigmaDecayPerDay))
            {
                _logManager.LogA(LogLevel.Warn, nameof(CaptainsMatch), arena, "Invalid CaptainsMatch.OpenSkillSigmaDecayPerDay; defaulting to 0.");
                sigmaDecayPerDay = 0;
            }
            sigmaDecayPerDay = double.Abs(sigmaDecayPerDay);

            arenaData.Config = new ArenaConfig
            {
                GameTypeId = gameTypeId,
                PlayersPerTeam = _configManager.GetInt(ch, "CaptainsMatch", "PlayersPerTeam", 4),
                LivesPerPlayer = _configManager.GetInt(ch, "CaptainsMatch", "LivesPerPlayer", 3),
                DefaultShip = (ShipType)Math.Clamp(_configManager.GetInt(ch, "CaptainsMatch", "DefaultShip", 1) - 1, 0, 7),
                FreqPairs = freqPairs,
                KickCooldown = TimeSpan.FromSeconds(_configManager.GetInt(ch, "CaptainsMatch", "KickCooldownSeconds", 60)),
                TimeLimit = timeLimit,
                OverTimeLimit = overTimeLimit,
                WinConditionDelay = winConditionDelay,
                TimeLimitWinBy = Math.Max(1, _configManager.GetInt(ch, "CaptainsMatch", "TimeLimitWinBy", 2)),
                MaxLagOuts = _configManager.GetInt(ch, "CaptainsMatch", "MaxLagOuts", 3),
                OpenSkillModel = openSkillModel,
                OpenSkillSigmaDecayPerDay = sigmaDecayPerDay,
                OpenSkillUseScoresWhenPossible = _configManager.GetBool(ch, "CaptainsMatch", "OpenSkillUseScoresWhenPossible", false),
                StartLocations = startLocations,
            };

            _arenaDataDictionary[arena] = arenaData;

            KillCallback.Register(arena, Callback_Kill);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            PlayerPositionPacketCallback.Register(arena, Callback_PlayerPositionPacket);

            _commandManager.AddCommand("captain", Command_Captain, arena);
            _commandManager.AddCommand("cap", Command_Captain, arena);
            _commandManager.AddCommand("join", Command_Join, arena);
            _commandManager.AddCommand("challenge", Command_Challenge, arena);
            _commandManager.AddCommand("accept", Command_Accept, arena);
            _commandManager.AddCommand("ready", Command_Ready, arena);
            _commandManager.AddCommand("rdy", Command_Ready, arena);
            _commandManager.AddCommand("remove", Command_Remove, arena);
            _commandManager.AddCommand("leave", Command_Leave, arena);
            _commandManager.AddCommand("end", Command_End, arena);
            _commandManager.AddCommand("sc", Command_Sc, arena);
            _commandManager.AddCommand("items", Command_Items, arena);
            _commandManager.AddCommand("freqinfo", Command_FreqInfo, arena);
            _commandManager.AddCommand("refuse", Command_Refuse, arena);
            _commandManager.AddCommand("disband", Command_Disband, arena);
            // ?chart is provided by TeamVersusStats when it is attached to the arena.

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            _arenaDataDictionary.Remove(arena);

            KillCallback.Unregister(arena, Callback_Kill);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPositionPacket);

            _commandManager.RemoveCommand("captain", Command_Captain, arena);
            _commandManager.RemoveCommand("cap", Command_Captain, arena);
            _commandManager.RemoveCommand("join", Command_Join, arena);
            _commandManager.RemoveCommand("challenge", Command_Challenge, arena);
            _commandManager.RemoveCommand("accept", Command_Accept, arena);
            _commandManager.RemoveCommand("ready", Command_Ready, arena);
            _commandManager.RemoveCommand("rdy", Command_Ready, arena);
            _commandManager.RemoveCommand("remove", Command_Remove, arena);
            _commandManager.RemoveCommand("leave", Command_Leave, arena);
            _commandManager.RemoveCommand("end", Command_End, arena);
            _commandManager.RemoveCommand("sc", Command_Sc, arena);
            _commandManager.RemoveCommand("items", Command_Items, arena);
            _commandManager.RemoveCommand("freqinfo", Command_FreqInfo, arena);
            _commandManager.RemoveCommand("refuse", Command_Refuse, arena);
            _commandManager.RemoveCommand("disband", Command_Disband, arena);
            // ?chart is managed by TeamVersusStats.

            foreach (MatchCountdown c in arenaData.PendingCountdowns)
                _mainloopTimer.ClearTimer<MatchCountdown>(Timer_Countdown, c);

            foreach (ActiveMatch m in arenaData.ActiveMatches)
            {
                _mainloopTimer.ClearTimer<ActiveMatch>(Timer_MatchTimeExpired, m);
                _mainloopTimer.ClearTimer<ActiveMatch>(Timer_WinConditionCheck, m);
            }

            ((IResettable)arenaData).TryReset();
            _arenaDataPool.Return(arenaData);

            return true;
        }

        #endregion

        #region IMatchFocusAdvisor

        bool IMatchFocusAdvisor.TryGetPlaying(IMatch match, HashSet<string> players)
        {
            foreach (ArenaData arenaData in _arenaDataDictionary.Values)
            {
                foreach (ActiveMatch m in arenaData.ActiveMatches)
                {
                    if (m.MatchData != match)
                        continue;

                    foreach (CaptainsPlayerSlot slot in m.ActiveSlots.Values)
                        if (slot.PlayerName is not null)
                            players.Add(slot.PlayerName);

                    return true;
                }
            }

            return false;
        }

        IMatch? IMatchFocusAdvisor.GetMatch(Player player)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd) || pd.ManagedArena is null)
                return null;
            if (!_arenaDataDictionary.TryGetValue(pd.ManagedArena, out ArenaData? arenaData))
                return null;
            if (!arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match))
                return null;
            if (!arenaData.ActiveMatches.Contains(match))
                return null; // still in countdown, not officially started
            return match.MatchData;
        }

        #endregion

        #region Callbacks

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action != PlayerAction.LeaveArena && action != PlayerAction.Disconnect)
                return;

            Arena? managedArena = null;
            if (action == PlayerAction.LeaveArena)
            {
                if (arena is not null && _arenaDataDictionary.ContainsKey(arena))
                    managedArena = arena;
            }
            else
            {
                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    managedArena = pd.ManagedArena;
            }

            if (managedArena is null || !_arenaDataDictionary.TryGetValue(managedArena, out ArenaData? arenaData))
                return;

            HandlePlayerLeave(managedArena, arenaData, player);
        }

        private void Callback_Kill(Arena arena, Player? killer, Player? killed, short bounty, short flagCount, short pts, Prize green)
        {
            if (killed is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.PlayerToMatch.TryGetValue(killed, out ActiveMatch? killedMatch))
                return;

            if (!killedMatch.ActiveSlots.TryGetValue(killed, out CaptainsPlayerSlot? killedSlot))
                return;

            CaptainsPlayerSlot? killerSlot = null;
            if (killer is not null
                && arenaData.PlayerToMatch.TryGetValue(killer, out ActiveMatch? killerMatch)
                && killerMatch == killedMatch)
            {
                killerMatch.ActiveSlots.TryGetValue(killer, out killerSlot);
            }

            if (killerSlot is not null)
            {
                killerSlot.Kills++;

                // Award a point to the killer's team (or the opposing team on a TK).
                if (killerSlot.Team != killedSlot.Team)
                    ((CaptainsTeam)killerSlot.Team).Score++;
                else
                {
                    var otherTeam = killedMatch.MatchData.Teams.FirstOrDefault(t => t != killedSlot.Team);
                    if (otherTeam is CaptainsTeam ct) ct.Score++;
                }
            }
            killedSlot.Deaths++;

            bool isKnockout = killedSlot.Lives <= 1;
            killedSlot.Lives--;

            bool matchIsActive = arenaData.ActiveMatches.Contains(killedMatch);

            if (killerSlot is not null)
            {
                TeamVersusMatchPlayerKilledCallback.Fire(arena, killedSlot, killerSlot, isKnockout);
                TeamVersusStatsPlayerKilledCallback.Fire(arena, killedSlot, killedSlot, killerSlot, killerSlot, isKnockout);
            }

            if (!isKnockout)
            {
                if (killerSlot is not null && _tvStatsBehavior is not null && matchIsActive)
                    _ = _tvStatsBehavior.PlayerKilledAsync(ServerTick.Now, DateTime.UtcNow, killedMatch.MatchData, killed, killedSlot, killer!, killerSlot, isKnockout);
                return;
            }

            // Knockout: remove from ActiveSlots BEFORE calling SetShipAndFreq to avoid false forfeit
            // triggers from ShipFreqChangeCallback.
            killedMatch.ActiveSlots.Remove(killed);
            killedSlot.Player = null;

            _game.SetShipAndFreq(killed, ShipType.Spec, killed.Freq);

            if (killerSlot is not null && _tvStatsBehavior is not null && matchIsActive)
                _ = _tvStatsBehavior.PlayerKilledAsync(ServerTick.Now, DateTime.UtcNow, killedMatch.MatchData, killed, killedSlot, killer!, killerSlot, isKnockout);

            // Delay the elimination check to handle simultaneous double-KO scenarios.
            ScheduleWinConditionCheck(arenaData, killedMatch);
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match))
                return;

            if (newShip == ShipType.Spec && oldShip != ShipType.Spec)
            {
                // Stop tracking items for this player.
                if (player.TryGetExtraData(_pdKey, out PlayerData? pd) && pd.IsWatchingExtraPositionData)
                {
                    _game.RemoveExtraPositionDataWatch(player);
                    pd.IsWatchingExtraPositionData = false;
                }

                // Player went to spec voluntarily during an active match.
                if (match.ActiveSlots.TryGetValue(player, out CaptainsPlayerSlot? slot))
                {
                    match.ActiveSlots.Remove(player);
                    match.SpecOutSlots[player] = slot;
                    slot.Player = null;
                    slot.LagOuts++;

                    // Forfeit if all of the team's players are now out of ActiveSlots.
                    bool allOut = !match.ActiveSlots.Values.Any(s => s.Team.Freq == oldFreq);
                    if (allOut && arenaData.ActiveMatches.Contains(match))
                        ScheduleWinConditionCheck(arenaData, match);
                }
            }
            else if (newShip != ShipType.Spec && oldShip == ShipType.Spec)
            {
                // Start tracking items for this player.
                if (player.TryGetExtraData(_pdKey, out PlayerData? pd) && !pd.IsWatchingExtraPositionData)
                {
                    _game.AddExtraPositionDataWatch(player);
                    pd.IsWatchingExtraPositionData = true;
                }

                // Player re-entering from spec — restore their slot if they specced out voluntarily.
                if (match.SpecOutSlots.TryGetValue(player, out CaptainsPlayerSlot? slot))
                {
                    if (newFreq == slot.Team.Freq && slot.Lives > 0)
                    {
                        // Enforce MaxLagOuts.
                        if (slot.LagOuts > arenaData.Config.MaxLagOuts)
                        {
                            _game.SetShipAndFreq(player, ShipType.Spec, newFreq);
                            _chat.SendMessage(player, $"You cannot return: exceeded maximum lagouts ({arenaData.Config.MaxLagOuts}).");
                            return;
                        }

                        match.SpecOutSlots.Remove(player);
                        match.ActiveSlots[player] = slot;
                        slot.Player = player;

                        // Apply requested ship if set, otherwise use DefaultShip.
                        ShipType targetShip = arenaData.Config.DefaultShip;
                        if (player.TryGetExtraData(_pdKey, out PlayerData? pd2) && pd2.RequestedShip is not null)
                        {
                            targetShip = pd2.RequestedShip.Value;
                            pd2.RequestedShip = null;
                        }

                        if (newShip != targetShip)
                            _game.SetShipAndFreq(player, targetShip, newFreq);
                    }
                    else if (newFreq != slot.Team.Freq)
                    {
                        // Trying to switch teams mid-match — reject.
                        _game.SetShipAndFreq(player, ShipType.Spec, newFreq);
                        _chat.SendMessage(player, $"You cannot switch teams mid-match. You are assigned to Freq {slot.Team.Freq}.");
                    }
                }
            }
        }

        private void Callback_PlayerPositionPacket(Player player, ref readonly C2S_PositionPacket positionPacket, ref readonly ExtraPositionData extra, bool hasExtraPositionData)
        {
            if (!hasExtraPositionData || player.Ship == ShipType.Spec)
                return;

            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match))
                return;

            if (!match.ActiveSlots.TryGetValue(player, out CaptainsPlayerSlot? slot))
                return;

            ItemChanges changes = ItemChanges.None;

            if (slot.Repels != extra.Repels) { slot.Repels = extra.Repels; changes |= ItemChanges.Repels; }
            if (slot.Rockets != extra.Rockets) { slot.Rockets = extra.Rockets; changes |= ItemChanges.Rockets; }
            if (slot.Bursts != extra.Bursts) { slot.Bursts = extra.Bursts; changes |= ItemChanges.Bursts; }
            if (slot.Thors != extra.Thors) { slot.Thors = extra.Thors; changes |= ItemChanges.Thors; }
            if (slot.Bricks != extra.Bricks) { slot.Bricks = extra.Bricks; changes |= ItemChanges.Bricks; }
            if (slot.Decoys != extra.Decoys) { slot.Decoys = extra.Decoys; changes |= ItemChanges.Decoys; }
            if (slot.Portals != extra.Portals) { slot.Portals = extra.Portals; changes |= ItemChanges.Portals; }

            if (changes != ItemChanges.None)
                TeamVersusMatchPlayerItemsChangedCallback.Fire(arena, slot, changes);
        }

        #endregion

        #region Commands

        private void Command_Captain(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (player.Ship != ShipType.Spec)
            {
                _chat.SendMessage(player, "You must be in spec to become a captain.");
                return;
            }

            if (GetPlayerFormation(arenaData, player) is not null || IsPlayerInMatch(arenaData, player))
            {
                _chat.SendMessage(player, "You are already in a team or active match.");
                return;
            }

            var formation = new Formation { Captain = player };
            formation.Members.Add(player);
            arenaData.Formations[player] = formation;

            if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                pd.ManagedArena = arena;

            SendToSpecPlayers(arena, $"{player.Name} is now a captain! Type ?join {player.Name} to join their team. ({formation.Members.Count}/{arenaData.Config.PlayersPerTeam})");
        }

        private void Command_Join(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (player.Ship != ShipType.Spec)
            {
                _chat.SendMessage(player, "You must be in spec to join a team.");
                return;
            }

            if (GetPlayerFormation(arenaData, player) is not null || IsPlayerInMatch(arenaData, player))
            {
                _chat.SendMessage(player, "You are already in a team or active match.");
                return;
            }

            if (arenaData.KickedPlayers.TryGetValue(player.Name!, out DateTime kickedAt))
            {
                TimeSpan remaining = arenaData.Config.KickCooldown - (DateTime.UtcNow - kickedAt);
                if (remaining > TimeSpan.Zero)
                {
                    _chat.SendMessage(player, $"You were recently kicked. Wait {(int)remaining.TotalSeconds + 1} more second(s) before joining.");
                    return;
                }
                arenaData.KickedPlayers.Remove(player.Name!);
            }

            ReadOnlySpan<char> captainName = parameters.Trim();
            if (captainName.IsEmpty)
            {
                _chat.SendMessage(player, "Usage: ?join <captain name>");
                return;
            }

            Formation? targetFormation = FindFormationByCaptainName(arenaData, captainName);
            if (targetFormation is null)
            {
                _chat.SendMessage(player, $"No captain named '{captainName}' found. They must type ?captain first.");
                return;
            }

            if (targetFormation.PairedWith is not null)
            {
                _chat.SendMessage(player, "That team is already paired for a match and is not accepting new members.");
                return;
            }

            if (targetFormation.Members.Count >= arenaData.Config.PlayersPerTeam)
            {
                _chat.SendMessage(player, $"{targetFormation.Captain.Name}'s team is already full ({arenaData.Config.PlayersPerTeam}/{arenaData.Config.PlayersPerTeam}).");
                return;
            }

            targetFormation.Members.Add(player);
            targetFormation.IsReady = false; // team composition changed

            if (player.TryGetExtraData(_pdKey, out PlayerData? jpd))
                jpd.ManagedArena = arena;

            SendToSpecPlayers(arena, $"{player.Name} joined {targetFormation.Captain.Name}'s team. ({targetFormation.Members.Count}/{arenaData.Config.PlayersPerTeam})");
        }

        private void Command_Challenge(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.Formations.TryGetValue(player, out Formation? myFormation))
            {
                _chat.SendMessage(player, "You must be a captain to issue a challenge. Type ?captain first.");
                return;
            }

            if (myFormation.PairedWith is not null)
            {
                _chat.SendMessage(player, "Your team is already paired for a match. Type ?ready to start.");
                return;
            }

            if (myFormation.Members.Count < arenaData.Config.PlayersPerTeam)
            {
                int needed = arenaData.Config.PlayersPerTeam - myFormation.Members.Count;
                _chat.SendMessage(player, $"Your team needs {needed} more player(s) before challenging. ({myFormation.Members.Count}/{arenaData.Config.PlayersPerTeam})");
                return;
            }

            ReadOnlySpan<char> targetName = parameters.Trim();
            if (targetName.IsEmpty)
            {
                _chat.SendMessage(player, "Usage: ?challenge <captain name>");
                return;
            }

            Formation? targetFormation = FindFormationByCaptainName(arenaData, targetName);
            if (targetFormation is null)
            {
                _chat.SendMessage(player, $"No captain named '{targetName}' found.");
                return;
            }

            if (targetFormation.Captain == player)
            {
                _chat.SendMessage(player, "You cannot challenge yourself.");
                return;
            }

            if (targetFormation.PairedWith is not null)
            {
                _chat.SendMessage(player, $"{targetFormation.Captain.Name}'s team is already paired with another team.");
                return;
            }

            if (targetFormation.Members.Count < arenaData.Config.PlayersPerTeam)
            {
                int needed = arenaData.Config.PlayersPerTeam - targetFormation.Members.Count;
                _chat.SendMessage(player, $"{targetFormation.Captain.Name}'s team still needs {needed} more player(s) ({targetFormation.Members.Count}/{arenaData.Config.PlayersPerTeam}).");
                return;
            }

            // Mutual challenge: if the target already challenged this captain, auto-pair immediately.
            if (targetFormation.SentChallengeTo == player)
            {
                var pair = GetPairForChallenge(arenaData, myFormation, targetFormation);
                if (pair is null)
                {
                    _chat.SendMessage(player, "No match slots available right now. Wait for a match to finish.");
                    return;
                }

                targetFormation.SentChallengeTo = null;
                ClearAllChallengesFor(arenaData, myFormation);
                ClearAllChallengesFor(arenaData, targetFormation);

                myFormation.PairedWith = targetFormation;
                targetFormation.PairedWith = myFormation;

                AssignFreqs(arenaData, myFormation, targetFormation, pair.Value);
                MoveFormationToFreq(arena, arenaData, myFormation);
                MoveFormationToFreq(arena, arenaData, targetFormation);

                SendToSpecPlayers(arena,
                    $"Mutual challenge! {player.Name}'s team (Freq {myFormation.AssignedFreq}) vs "
                    + $"{targetFormation.Captain.Name}'s team (Freq {targetFormation.AssignedFreq}). "
                    + "Both captains type ?ready to start!");
                return;
            }

            // Cancel any existing outgoing challenge from this captain.
            if (myFormation.SentChallengeTo is not null)
            {
                _chat.SendMessage(player, $"Your previous challenge to {myFormation.SentChallengeTo.Name} has been cancelled.");
                myFormation.SentChallengeTo = null;
            }

            myFormation.SentChallengeTo = targetFormation.Captain;
            _chat.SendMessage(player, $"Challenge sent to {targetFormation.Captain.Name}!");
            _chat.SendMessage(targetFormation.Captain, ChatSound.Ding,
                $">>> {player.Name}'s team ({myFormation.Members.Count} players) has challenged your team! Type ?accept {player.Name} to accept. <<<");
            SendToSpecPlayers(arena, $"{player.Name}'s team has challenged {targetFormation.Captain.Name}'s team to a match!");
        }

        private void Command_Accept(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.Formations.TryGetValue(player, out Formation? myFormation))
            {
                _chat.SendMessage(player, "You must be a captain to accept a challenge.");
                return;
            }

            if (myFormation.PairedWith is not null)
            {
                _chat.SendMessage(player, "Your team is already paired for a match.");
                return;
            }

            // Find the challenger — by name if specified, otherwise the only pending challenger.
            ReadOnlySpan<char> challengerName = parameters.Trim();
            Formation? challengerFormation = null;
            bool ambiguous = false;

            foreach (Formation f in arenaData.Formations.Values)
            {
                if (f.SentChallengeTo != player)
                    continue;

                if (!challengerName.IsEmpty)
                {
                    if (challengerName.Equals(f.Captain.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        challengerFormation = f;
                        break;
                    }
                }
                else
                {
                    if (challengerFormation is not null)
                    {
                        ambiguous = true;
                        break;
                    }
                    challengerFormation = f;
                }
            }

            if (ambiguous)
            {
                _chat.SendMessage(player, "Multiple challenges pending. Use ?accept <captain name> to specify.");
                return;
            }

            if (challengerFormation is null)
            {
                string msg = challengerName.IsEmpty
                    ? "No pending challenges found."
                    : $"No challenge from '{challengerName}' found.";
                _chat.SendMessage(player, msg);
                return;
            }

            var pair = GetPairForChallenge(arenaData, challengerFormation, myFormation);
            if (pair is null)
            {
                _chat.SendMessage(player, "No match slots available right now. Wait for a match to finish.");
                return;
            }

            // Pair the two formations.
            challengerFormation.SentChallengeTo = null;
            challengerFormation.PairedWith = myFormation;
            myFormation.PairedWith = challengerFormation;

            // Cancel all other pending challenges involving either formation.
            ClearAllChallengesFor(arenaData, challengerFormation);
            ClearAllChallengesFor(arenaData, myFormation);

            // Assign freqs, respecting any existing assignment (e.g., winning team staying on a freq).
            AssignFreqs(arenaData, challengerFormation, myFormation, pair.Value);

            // Move players to their assigned freqs.
            MoveFormationToFreq(arena, arenaData, challengerFormation);
            MoveFormationToFreq(arena, arenaData, myFormation);

            SendToSpecPlayers(arena,
                $"Challenge accepted! {challengerFormation.Captain.Name}'s team (Freq {challengerFormation.AssignedFreq}) vs "
                + $"{myFormation.Captain.Name}'s team (Freq {myFormation.AssignedFreq}). "
                + "Both captains type ?ready to start the match!");
        }

        private void Command_Ready(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.Formations.TryGetValue(player, out Formation? myFormation))
            {
                _chat.SendMessage(player, "You must be a captain to ready up.");
                return;
            }

            if (myFormation.PairedWith is null)
            {
                _chat.SendMessage(player, "Your team has not been paired yet. Use ?challenge to find an opponent first.");
                return;
            }

            if (myFormation.IsReady)
            {
                _chat.SendMessage(player, "Your team is already marked as ready.");
                return;
            }

            myFormation.IsReady = true;
            SendToFormationPair(myFormation, myFormation.PairedWith, $"{player.Name}'s team is ready!");

            if (myFormation.PairedWith.IsReady)
            {
                // Both teams ready: build match data so TeamVersusStats can initialize during countdown.
                MatchCountdown countdown = BuildMatchCountdown(arena, arenaData, myFormation, myFormation.PairedWith);
                arenaData.PendingCountdowns.Add(countdown);

                if (_tvStatsBehavior is not null)
                    _ = _tvStatsBehavior.InitializeAsync(countdown.PendingMatchData);

                BeginCountdown(arena, arenaData, countdown);
            }
        }

        private void Command_Remove(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.Formations.TryGetValue(player, out Formation? myFormation))
            {
                _chat.SendMessage(player, "You are not a captain.");
                return;
            }

            ReadOnlySpan<char> targetName = parameters.Trim();
            if (targetName.IsEmpty)
            {
                _chat.SendMessage(player, "Usage: ?remove <player name>");
                return;
            }

            Player? kickTarget = FindPlayerInArena(arena, targetName);
            if (kickTarget is null)
            {
                _chat.SendMessage(player, $"Player '{targetName}' not found.");
                return;
            }

            if (kickTarget == player)
            {
                _chat.SendMessage(player, "You cannot remove yourself. Use ?disband to disband your team.");
                return;
            }

            if (!myFormation.Members.Contains(kickTarget))
            {
                _chat.SendMessage(player, $"{kickTarget.Name} is not on your team.");
                return;
            }

            myFormation.Members.Remove(kickTarget);
            myFormation.IsReady = false;

            if (kickTarget.TryGetExtraData(_pdKey, out PlayerData? kpd))
                kpd.ManagedArena = null;

            if (kickTarget.Ship != ShipType.Spec)
                _game.SetShipAndFreq(kickTarget, ShipType.Spec, kickTarget.Freq);

            arenaData.KickedPlayers[kickTarget.Name!] = DateTime.UtcNow;
            int cooldownSec = (int)arenaData.Config.KickCooldown.TotalSeconds;
            _chat.SendMessage(player, $"{kickTarget.Name} was removed from your team.");
            _chat.SendMessage(kickTarget, $"You were kicked and may not rejoin for {cooldownSec} second(s).");
            _chat.SendMessage(player, $"Team: ({myFormation.Members.Count}/{arenaData.Config.PlayersPerTeam})");
        }

        private void Command_Leave(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            // Captain leaves → disband the formation.
            if (arenaData.Formations.TryGetValue(player, out Formation? myFormation))
            {
                DisbandFormation(arena, arenaData, myFormation, $"{player.Name}'s team has been disbanded.");
                return;
            }

            // Non-captain member of a formation.
            Formation? memberFormation = GetNonCaptainFormation(arenaData, player);
            if (memberFormation is not null)
            {
                memberFormation.Members.Remove(player);
                memberFormation.IsReady = false;

                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;

                if (player.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(player, ShipType.Spec, player.Freq);

                _chat.SendMessage(player, $"You have left {memberFormation.Captain.Name}'s team.");
                _chat.SendMessage(memberFormation.Captain, $"{player.Name} left your team. ({memberFormation.Members.Count}/{arenaData.Config.PlayersPerTeam})");
                return;
            }

            _chat.SendMessage(player, "You are not in any team.");
        }

        private void Command_End(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match) || !arenaData.ActiveMatches.Contains(match))
            {
                _chat.SendMessage(player, "You are not in an active match.");
                return;
            }

            CaptainsPlayerSlot? playerSlot = null;
            match.ActiveSlots.TryGetValue(player, out playerSlot);
            if (playerSlot is null)
                match.SpecOutSlots.TryGetValue(player, out playerSlot);

            if (playerSlot is null)
            {
                _chat.SendMessage(player, "You are not in the active match.");
                return;
            }

            short losingFreq = playerSlot.Team.Freq;
            _chat.SendArenaMessage(arena, $"{player.Name}'s team (Freq {losingFreq}) has forfeited!");
            EndMatch(arena, arenaData, match, losingFreq);
        }

        private void Command_Refuse(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.Formations.TryGetValue(player, out Formation? myFormation))
            {
                _chat.SendMessage(player, "You must be a captain to refuse challenges.");
                return;
            }

            ReadOnlySpan<char> challengerName = parameters.Trim();
            Formation? challengerFormation = null;
            bool ambiguous = false;

            foreach (Formation f in arenaData.Formations.Values)
            {
                if (f.SentChallengeTo != player)
                    continue;

                if (!challengerName.IsEmpty)
                {
                    if (challengerName.Equals(f.Captain.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        challengerFormation = f;
                        break;
                    }
                }
                else
                {
                    if (challengerFormation is not null)
                    {
                        ambiguous = true;
                        break;
                    }
                    challengerFormation = f;
                }
            }

            if (ambiguous)
            {
                _chat.SendMessage(player, "Multiple challenges pending. Use ?refuse <captain name> to specify.");
                return;
            }

            if (challengerFormation is null)
            {
                string msg = challengerName.IsEmpty
                    ? "No pending challenges to refuse."
                    : $"No challenge from '{challengerName}' found.";
                _chat.SendMessage(player, msg);
                return;
            }

            challengerFormation.SentChallengeTo = null;
            _chat.SendMessage(player, $"You refused {challengerFormation.Captain.Name}'s challenge.");
            _chat.SendMessage(challengerFormation.Captain, $"{player.Name} refused your challenge.");
        }

        private void Command_Disband(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.Formations.TryGetValue(player, out Formation? myFormation))
            {
                _chat.SendMessage(player, "You are not a captain.");
                return;
            }

            DisbandFormation(arena, arenaData, myFormation, $"{player.Name}'s team has been disbanded.");
        }

        private void Command_Sc(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match))
            {
                _chat.SendMessage(player, "?sc is only available during an active match.");
                return;
            }

            bool inMatch = match.ActiveSlots.ContainsKey(player) || match.SpecOutSlots.ContainsKey(player);
            if (!inMatch)
            {
                _chat.SendMessage(player, "?sc is only available during an active match.");
                return;
            }

            if (!int.TryParse(parameters.Trim(), out int shipNum) || shipNum < 1 || shipNum > 8)
            {
                _chat.SendMessage(player, "Usage: ?sc <1-8>  (1=Warbird, 2=Javelin, 3=Spider, 4=Leviathan, 5=Terrier, 6=Weasel, 7=Lancaster, 8=Shark)");
                return;
            }

            var ship = (ShipType)(shipNum - 1);

            if (player.Ship != ShipType.Spec)
            {
                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.RequestedShip = null;
                _game.SetShipAndFreq(player, ship, player.Freq);
            }
            else
            {
                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.RequestedShip = ship;
                _chat.SendMessage(player, $"Ship set to {ship}. It will apply when you re-enter.");
            }
        }

        private void Command_Items(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (arenaData.ActiveMatches.Count == 0)
            {
                _chat.SendMessage(player, "No active match.");
                return;
            }

            foreach (ActiveMatch match in arenaData.ActiveMatches)
            {
                foreach (ITeam team in match.MatchData.Teams)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"Freq {team.Freq}: ");
                    bool first = true;
                    foreach (IPlayerSlot iSlot in team.Slots)
                    {
                        var slot = (CaptainsPlayerSlot)iSlot;
                        if (slot.Lives <= 0)
                            continue;
                        if (!first) sb.Append(", ");
                        sb.Append($"{slot.PlayerName} {slot.Repels}/{slot.Rockets}");
                        first = false;
                    }
                    if (!first)
                        _chat.SendMessage(player, sb.ToString());
                }
            }
        }

        private void Command_FreqInfo(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            bool anyOutput = false;

            if (arenaData.ActiveMatches.Count > 0)
            {
                anyOutput = true;
                foreach (ActiveMatch match in arenaData.ActiveMatches)
                {
                    _chat.SendMessage(player, $"=== Active Match (Freq {match.Freq1} vs {match.Freq2}) ===");
                    foreach (ITeam team in match.MatchData.Teams)
                    {
                        _chat.SendMessage(player, $"Freq {team.Freq}:");
                        foreach (IPlayerSlot iSlot in team.Slots)
                        {
                            var slot = (CaptainsPlayerSlot)iSlot;
                            string status = slot.Lives > 0 ? $"{slot.Lives} live{(slot.Lives != 1 ? "s" : "")}" : "eliminated";
                            _chat.SendMessage(player, $"  {slot.PlayerName}: {status}");
                        }
                    }
                }
            }

            if (arenaData.Formations.Count > 0)
            {
                anyOutput = true;
                _chat.SendMessage(player, "=== Forming Teams ===");
                foreach (Formation f in arenaData.Formations.Values)
                {
                    string state;
                    if (f.PairedWith is not null)
                        state = f.IsReady ? "Ready" : "Paired (waiting for ?ready)";
                    else if (f.SentChallengeTo is not null)
                        state = $"Challenged {f.SentChallengeTo.Name}";
                    else
                        state = "Forming";

                    string freqStr = f.AssignedFreq.HasValue ? $" [Freq {f.AssignedFreq}]" : "";
                    _chat.SendMessage(player, $"{f.Captain.Name}'s team{freqStr} — {state} ({f.Members.Count}/{arenaData.Config.PlayersPerTeam}): {string.Join(", ", f.Members.Select(m => m.Name))}");
                }
            }

            if (!anyOutput)
                _chat.SendMessage(player, "No active teams or matches. Type ?captain to form a team!");
        }

        private void Command_Chart(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (arenaData.ActiveMatches.Count == 0)
            {
                _chat.SendMessage(player, "No active match.");
                return;
            }

            // Show the player's own match if they are in one; otherwise show the first active match.
            ActiveMatch targetMatch;
            if (arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? playerMatch) && arenaData.ActiveMatches.Contains(playerMatch))
                targetMatch = playerMatch;
            else
                targetMatch = arenaData.ActiveMatches[0];

            PrintMatchChart(player, arena, targetMatch.MatchData, -1);
        }

        #endregion

        #region Timers

        private void ScheduleWinConditionCheck(ArenaData arenaData, ActiveMatch match)
        {
            int delayMs = (int)arenaData.Config.WinConditionDelay.TotalMilliseconds;
            _mainloopTimer.ClearTimer<ActiveMatch>(Timer_WinConditionCheck, match);
            _mainloopTimer.SetTimer<ActiveMatch>(Timer_WinConditionCheck, delayMs, Timeout.Infinite, match, match);
        }

        private bool Timer_Countdown(MatchCountdown countdown)
        {
            Arena? arena = countdown.Arena;
            if (arena is null)
                return false;

            countdown.Seconds--;

            if (countdown.Seconds > 0)
            {
                _chat.SendArenaMessage(arena, $"-{countdown.Seconds}-");
                return true;
            }

            _chat.SendArenaMessage(arena, ChatSound.Ding, "GO!");
            countdown.ArenaData.PendingCountdowns.Remove(countdown);
            StartMatch(arena, countdown.ArenaData, countdown);
            return false;
        }

        private bool Timer_MatchTimeExpired(ActiveMatch match)
        {
            Arena? arena = match.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            if (!arenaData.ActiveMatches.Contains(match))
                return false;

            // Determine if a team leads by enough to win.
            short team1Score = match.MatchData.Teams.FirstOrDefault(t => t.Freq == match.Freq1)?.Score ?? 0;
            short team2Score = match.MatchData.Teams.FirstOrDefault(t => t.Freq == match.Freq2)?.Score ?? 0;
            int winBy = arenaData.Config.TimeLimitWinBy;

            if (team1Score >= team2Score + winBy)
            {
                _chat.SendArenaMessage(arena, $"Time's up! Freq {match.Freq1} wins {team1Score}-{team2Score}!");
                EndMatch(arena, arenaData, match, match.Freq2);
                return false;
            }
            else if (team2Score >= team1Score + winBy)
            {
                _chat.SendArenaMessage(arena, $"Time's up! Freq {match.Freq2} wins {team2Score}-{team1Score}!");
                EndMatch(arena, arenaData, match, match.Freq1);
                return false;
            }

            // No winner by required margin.
            if (!match.IsOvertime && arenaData.Config.OverTimeLimit is { } otl)
            {
                match.IsOvertime = true;
                int otlMs = (int)otl.TotalMilliseconds;
                _mainloopTimer.SetTimer<ActiveMatch>(Timer_MatchTimeExpired, otlMs, Timeout.Infinite, match, match);
                _chat.SendArenaMessage(arena, $"Overtime! Score is {match.Freq1}:{team1Score} - {match.Freq2}:{team2Score}. {(int)otl.TotalMinutes}m remaining.");
            }
            else
            {
                _chat.SendArenaMessage(arena, $"Time's up! It's a draw! ({match.Freq1}:{team1Score} - {match.Freq2}:{team2Score})");
                EndMatchDraw(arena, arenaData, match);
            }

            return false;
        }

        private bool Timer_WinConditionCheck(ActiveMatch match)
        {
            Arena? arena = match.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            if (!arenaData.ActiveMatches.Contains(match))
                return false;

            bool team1Eliminated = !match.ActiveSlots.Values.Any(s => s.Team.Freq == match.Freq1)
                                && !match.SpecOutSlots.Values.Any(s => s.Team.Freq == match.Freq1 && s.Lives > 0);
            bool team2Eliminated = !match.ActiveSlots.Values.Any(s => s.Team.Freq == match.Freq2)
                                && !match.SpecOutSlots.Values.Any(s => s.Team.Freq == match.Freq2 && s.Lives > 0);

            if (team1Eliminated && team2Eliminated)
            {
                _chat.SendArenaMessage(arena, "Double knockout! It's a draw!");
                EndMatchDraw(arena, arenaData, match);
            }
            else if (team1Eliminated)
                EndMatch(arena, arenaData, match, match.Freq1);
            else if (team2Eliminated)
                EndMatch(arena, arenaData, match, match.Freq2);

            return false;
        }

        #endregion

        #region Helpers

        private void HandlePlayerLeave(Arena arena, ArenaData arenaData, Player player)
        {
            // Captain leaving → disband their formation.
            if (arenaData.Formations.TryGetValue(player, out Formation? myFormation))
            {
                DisbandFormation(arena, arenaData, myFormation, null);
                return;
            }

            // Non-captain member of a formation.
            Formation? memberFormation = GetNonCaptainFormation(arenaData, player);
            if (memberFormation is not null)
            {
                memberFormation.Members.Remove(player);
                memberFormation.IsReady = false;

                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;

                if (memberFormation.Captain is not null)
                    _chat.SendMessage(memberFormation.Captain, $"{player.Name} left the arena. ({memberFormation.Members.Count}/{arenaData.Config.PlayersPerTeam})");
                return;
            }

            // Player in an active or countdown match.
            if (!arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match))
                return;

            CaptainsPlayerSlot? playerSlot = null;
            match.ActiveSlots.TryGetValue(player, out playerSlot);
            if (playerSlot is null)
                match.SpecOutSlots.TryGetValue(player, out playerSlot);

            if (playerSlot is not null)
            {
                match.ActiveSlots.Remove(player);
                match.SpecOutSlots.Remove(player);
                arenaData.PlayerToMatch.Remove(player);
                playerSlot.Player = null;
                playerSlot.Lives = 0;

                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                {
                    pd.ManagedArena = null;
                    RemoveExtraPositionDataWatch(player, pd);
                }

                if (arenaData.ActiveMatches.Contains(match))
                    ScheduleWinConditionCheck(arenaData, match);
            }
        }

        /// <summary>
        /// Sends a message to all players in the arena who are currently in spectator mode.
        /// Used for formation-phase announcements that are irrelevant to players in active matches.
        /// </summary>
        private void SendToSpecPlayers(Arena arena, string message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                _playerData.Lock();
                try
                {
                    foreach (Player p in _playerData.Players)
                        if (p.Arena == arena && p.Ship == ShipType.Spec && p.Status == PlayerState.Playing)
                            set.Add(p);
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (set.Count > 0)
                    _chat.SendSetMessage(set, message);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        /// <summary>
        /// Sends a message to all members of two paired formations.
        /// Used for ready-state notifications that are only relevant to the participants.
        /// </summary>
        private void SendToFormationPair(Formation formation1, Formation? formation2, string message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                foreach (Player p in formation1.Members)
                    set.Add(p);

                if (formation2 is not null)
                    foreach (Player p in formation2.Members)
                        set.Add(p);

                if (set.Count > 0)
                    _chat.SendSetMessage(set, message);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        /// <summary>
        /// Starts watching extra position data for a player if they are already in a ship and not yet watched.
        /// Called when a player is added to <see cref="ArenaData.PlayerToMatch"/> while potentially already
        /// in a ship (moved to freq before the countdown began).
        /// </summary>
        private void EnsureExtraPositionDataWatch(Player player)
        {
            if (player.Ship == ShipType.Spec)
                return;
            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd) || pd.IsWatchingExtraPositionData)
                return;
            _game.AddExtraPositionDataWatch(player);
            pd.IsWatchingExtraPositionData = true;
        }

        private void RemoveExtraPositionDataWatch(Player player, PlayerData? pd = null)
        {
            if (pd is null)
                player.TryGetExtraData(_pdKey, out pd);
            if (pd is not null && pd.IsWatchingExtraPositionData)
            {
                _game.RemoveExtraPositionDataWatch(player);
                pd.IsWatchingExtraPositionData = false;
            }
        }

        private void BeginCountdown(Arena arena, ArenaData arenaData, MatchCountdown countdown)
        {
            countdown.Seconds = 3;
            _chat.SendArenaMessage(arena, "-3-");
            _mainloopTimer.SetTimer(Timer_Countdown, 1000, 1000, countdown, countdown);
        }

        /// <summary>
        /// Builds the per-match countdown object (match data + player slots) before the countdown begins,
        /// so that <see cref="ITeamVersusStatsBehavior.InitializeAsync"/> can run during the countdown.
        /// Players are added to <see cref="ArenaData.PlayerToMatch"/> now, so callbacks work during countdown.
        /// </summary>
        private MatchCountdown BuildMatchCountdown(Arena arena, ArenaData arenaData, Formation formationA, Formation formationB)
        {
            // Ensure formation1 is on the lower-numbered freq of the pair.
            Formation formation1, formation2;
            if (formationA.AssignedFreq <= formationB.AssignedFreq)
            {
                formation1 = formationA;
                formation2 = formationB;
            }
            else
            {
                formation1 = formationB;
                formation2 = formationA;
            }

            short freq1 = formation1.AssignedFreq!.Value;
            short freq2 = formation2.AssignedFreq!.Value;

            var config = new CaptainsMatchConfiguration(arenaData.Config);
            var matchData = new CaptainsMatchData(arena, config, freq1);

            var activeMatch = new ActiveMatch
            {
                Arena = arena,
                ArenaData = arenaData,
                MatchData = matchData,
                Freq1 = freq1,
                Freq2 = freq2,
            };

            var team1SlotList = new List<CaptainsPlayerSlot>(arenaData.Config.PlayersPerTeam);
            var team1 = new CaptainsTeam(matchData, 0, freq1, team1SlotList, formation1.Captain);
            int slotIdx = 0;
            foreach (Player p in formation1.Members)
            {
                var ps = new CaptainsPlayerSlot(matchData, team1, slotIdx++, p.Name!, p, arenaData.Config.LivesPerPlayer, arenaData.Config.DefaultShip);
                team1SlotList.Add(ps);
                activeMatch.ActiveSlots[p] = ps;
                arenaData.PlayerToMatch[p] = activeMatch;
                EnsureExtraPositionDataWatch(p);
            }

            var team2SlotList = new List<CaptainsPlayerSlot>(arenaData.Config.PlayersPerTeam);
            var team2 = new CaptainsTeam(matchData, 1, freq2, team2SlotList, formation2.Captain);
            slotIdx = 0;
            foreach (Player p in formation2.Members)
            {
                var ps = new CaptainsPlayerSlot(matchData, team2, slotIdx++, p.Name!, p, arenaData.Config.LivesPerPlayer, arenaData.Config.DefaultShip);
                team2SlotList.Add(ps);
                activeMatch.ActiveSlots[p] = ps;
                arenaData.PlayerToMatch[p] = activeMatch;
                EnsureExtraPositionDataWatch(p);
            }

            matchData.SetTeams(team1, team2);

            return new MatchCountdown
            {
                Arena = arena,
                ArenaData = arenaData,
                Formation1 = formation1,
                Formation2 = formation2,
                PendingMatchData = matchData,
                ActiveMatch = activeMatch,
                Seconds = 3,
            };
        }

        private void StartMatch(Arena arena, ArenaData arenaData, MatchCountdown countdown)
        {
            var matchData = countdown.PendingMatchData;
            var match = countdown.ActiveMatch;

            matchData.Started = DateTime.UtcNow;
            arenaData.ActiveMatches.Add(match);

            Formation formation1 = countdown.Formation1;
            Formation formation2 = countdown.Formation2;

            // Remove the paired formations — players are now tracked via PlayerToMatch/ActiveSlots.
            arenaData.Formations.Remove(formation1.Captain);
            arenaData.Formations.Remove(formation2.Captain);

            MatchStartingCallback.Fire(_broker!, matchData);
            foreach (var (p, _) in match.ActiveSlots)
                MatchAddPlayingCallback.Fire(_broker!, matchData, p.Name!, p);
            MatchStartedCallback.Fire(_broker!, matchData);
            TeamVersusMatchStartedCallback.Fire(arena, matchData);

            if (_tvStatsBehavior is not null)
                _ = _tvStatsBehavior.MatchStartedAsync(matchData);

            string timeMsg = arenaData.Config.TimeLimit is { } tlMsg
                ? $" Time limit: {(int)tlMsg.TotalMinutes}m."
                : string.Empty;
            _chat.SendArenaMessage(arena, $"Match started! Freq {match.Freq1} vs Freq {match.Freq2}. Each player has {arenaData.Config.LivesPerPlayer} lives.{timeMsg}");

            // Warp each player to their freq's configured start location.
            if (arenaData.Config.StartLocations.Count > 0)
            {
                foreach (var (p, slot) in match.ActiveSlots)
                {
                    if (arenaData.Config.StartLocations.TryGetValue(slot.Team.Freq, out (short X, short Y) loc))
                        _game.WarpTo(p, loc.X, loc.Y);
                }
            }

            if (arenaData.Config.TimeLimit is { } tl)
                _mainloopTimer.SetTimer<ActiveMatch>(Timer_MatchTimeExpired, (int)tl.TotalMilliseconds, Timeout.Infinite, match, match);
        }

        private void EndMatch(Arena arena, ArenaData arenaData, ActiveMatch match, short losingFreq)
        {
            short winningFreq = match.Freq1 == losingFreq ? match.Freq2 : match.Freq1;

            _chat.SendArenaMessage(arena, $"Freq {winningFreq} wins! Freq {losingFreq} has been eliminated.");
            PrintMatchChart(null, arena, match.MatchData, winningFreq);

            ITeam? winnerTeam = match.MatchData.Teams.FirstOrDefault(t => t.Freq == winningFreq);
            TeamVersusMatchEndedCallback.Fire(arena, match.MatchData, MatchEndReason.Decided, winnerTeam);

            if (_tvStatsBehavior is not null)
                _ = _tvStatsBehavior.MatchEndedAsync(match.MatchData, MatchEndReason.Decided, winnerTeam);

            MatchEndedCallback.Fire(_broker!, match.MatchData);

            // Spec out the losing team and clear their ManagedArena / PlayerToMatch entries.
            foreach (var (p, slot) in match.ActiveSlots)
            {
                if (slot.Team.Freq != losingFreq)
                    continue;
                if (p.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(p, ShipType.Spec, p.Freq);
                if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                {
                    pd.ManagedArena = null;
                    RemoveExtraPositionDataWatch(p, pd);
                }
                arenaData.PlayerToMatch.Remove(p);
            }
            foreach (var (p, slot) in match.SpecOutSlots)
            {
                if (slot.Team.Freq != losingFreq)
                    continue;
                if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;
                arenaData.PlayerToMatch.Remove(p);
            }

            // Rebuild the winning team as a Formation so they can receive a new challenge.
            if (winnerTeam is CaptainsTeam winnerCaptainsTeam)
            {
                Player? winnerCaptain = winnerCaptainsTeam.OriginalCaptain;
                var survivors = new HashSet<Player>();

                foreach (var (p, slot) in match.ActiveSlots)
                    if (slot.Team.Freq == winningFreq)
                        survivors.Add(p);

                foreach (var (p, slot) in match.SpecOutSlots)
                    if (slot.Team.Freq == winningFreq && slot.Lives > 0)
                        survivors.Add(p);

                if (winnerCaptain is null || !survivors.Contains(winnerCaptain))
                    winnerCaptain = survivors.FirstOrDefault();

                if (winnerCaptain is not null && survivors.Count > 0)
                {
                    var winnerFormation = new Formation
                    {
                        Captain = winnerCaptain,
                        AssignedFreq = winningFreq,
                    };
                    foreach (Player p in survivors)
                        winnerFormation.Members.Add(p);

                    arenaData.Formations[winnerCaptain] = winnerFormation;
                    _chat.SendArenaMessage(arena, $"Freq {winningFreq} ({winnerCaptain.Name}'s team) stays on the field! Challenge them: ?challenge {winnerCaptain.Name}");
                }
            }

            // Remove extra position data watches for winning team players and remove from PlayerToMatch.
            foreach (var (p, _) in match.ActiveSlots)
                RemoveExtraPositionDataWatch(p);

            foreach (Player p in match.ActiveSlots.Keys.ToList())
                arenaData.PlayerToMatch.Remove(p);
            foreach (Player p in match.SpecOutSlots.Keys.ToList())
                arenaData.PlayerToMatch.Remove(p);

            arenaData.ActiveMatches.Remove(match);
            arenaData.KickedPlayers.Clear();
            _mainloopTimer.ClearTimer<ActiveMatch>(Timer_MatchTimeExpired, match);
            _mainloopTimer.ClearTimer<ActiveMatch>(Timer_WinConditionCheck, match);
        }

        /// <summary>
        /// Ends the match as a draw (time expired with equal alive counts). Specs all players; neither team reforms.
        /// </summary>
        private void EndMatchDraw(Arena arena, ArenaData arenaData, ActiveMatch match)
        {
            PrintMatchChart(null, arena, match.MatchData, -1);

            TeamVersusMatchEndedCallback.Fire(arena, match.MatchData, MatchEndReason.Draw, null);

            if (_tvStatsBehavior is not null)
                _ = _tvStatsBehavior.MatchEndedAsync(match.MatchData, MatchEndReason.Draw, null);

            MatchEndedCallback.Fire(_broker!, match.MatchData);

            foreach (var (p, _) in match.ActiveSlots)
            {
                if (p.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(p, ShipType.Spec, p.Freq);
                RemoveExtraPositionDataWatch(p);
                if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;
                arenaData.PlayerToMatch.Remove(p);
            }
            foreach (var (p, _) in match.SpecOutSlots)
            {
                if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;
                arenaData.PlayerToMatch.Remove(p);
            }

            arenaData.ActiveMatches.Remove(match);
            arenaData.KickedPlayers.Clear();
            _mainloopTimer.ClearTimer<ActiveMatch>(Timer_MatchTimeExpired, match);
            _mainloopTimer.ClearTimer<ActiveMatch>(Timer_WinConditionCheck, match);
        }

        private void DisbandFormation(Arena arena, ArenaData arenaData, Formation formation, string? message)
        {
            // If this formation was in a countdown, cancel it and remove players from PlayerToMatch.
            if (formation.PairedWith is not null)
            {
                MatchCountdown? countdown = arenaData.PendingCountdowns.Find(
                    c => c.Formation1 == formation || c.Formation2 == formation);
                if (countdown is not null)
                {
                    _mainloopTimer.ClearTimer<MatchCountdown>(Timer_Countdown, countdown);
                    arenaData.PendingCountdowns.Remove(countdown);

                    foreach (Player p in countdown.ActiveMatch.ActiveSlots.Keys)
                        arenaData.PlayerToMatch.Remove(p);
                }
            }

            // Spec out all members.
            foreach (Player p in formation.Members)
            {
                if (p.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(p, ShipType.Spec, p.Freq);
                if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;
            }

            // Notify and unpair the partner, if any.
            if (formation.PairedWith is not null)
            {
                Formation partner = formation.PairedWith;
                partner.PairedWith = null;
                partner.IsReady = false;
                partner.AssignedFreq = null;

                foreach (Player p in partner.Members)
                {
                    if (p.Ship != ShipType.Spec)
                        _game.SetShipAndFreq(p, ShipType.Spec, p.Freq);
                    if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                        pd.ManagedArena = null;
                }

                if (partner.Captain is not null)
                    _chat.SendMessage(partner.Captain, "The opposing captain disbanded their team. Your pairing has been cancelled.");
            }

            // Clear outgoing challenge.
            formation.SentChallengeTo = null;

            // Cancel any incoming challenges targeting this formation.
            foreach (Formation f in arenaData.Formations.Values)
                if (f.SentChallengeTo == formation.Captain)
                    f.SentChallengeTo = null;

            arenaData.Formations.Remove(formation.Captain);

            if (!string.IsNullOrEmpty(message))
                _chat.SendArenaMessage(arena, message);
        }

        private void MoveFormationToFreq(Arena arena, ArenaData arenaData, Formation formation)
        {
            if (!formation.AssignedFreq.HasValue)
                return;

            short freq = formation.AssignedFreq.Value;
            foreach (Player p in formation.Members)
            {
                ShipType targetShip = arenaData.Config.DefaultShip;
                if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                {
                    pd.ManagedArena = arena;
                    if (pd.RequestedShip is not null)
                    {
                        targetShip = pd.RequestedShip.Value;
                        pd.RequestedShip = null;
                    }
                }

                if (p.Ship == ShipType.Spec || p.Freq != freq)
                    _game.SetShipAndFreq(p, targetShip, freq);
            }
        }

        /// <summary>
        /// Returns the freq pair to use for a new challenge/accept. If one formation already holds an
        /// assigned freq (winning team on field), returns that pair. Otherwise returns the first
        /// pair not currently in use by any formation, countdown, or active match.
        /// </summary>
        private static (short F1, short F2)? GetPairForChallenge(ArenaData arenaData, Formation formationA, Formation formationB)
        {
            short? existingFreq = formationA.AssignedFreq ?? formationB.AssignedFreq;
            if (existingFreq.HasValue)
            {
                foreach (var pair in arenaData.Config.FreqPairs)
                    if (pair.F1 == existingFreq || pair.F2 == existingFreq)
                        return pair;
                return null;
            }

            foreach (var pair in arenaData.Config.FreqPairs)
            {
                bool inUse = arenaData.Formations.Values.Any(f => f.AssignedFreq == pair.F1 || f.AssignedFreq == pair.F2)
                          || arenaData.ActiveMatches.Any(m => m.Freq1 == pair.F1 || m.Freq1 == pair.F2 || m.Freq2 == pair.F1 || m.Freq2 == pair.F2)
                          || arenaData.PendingCountdowns.Any(c => c.ActiveMatch.Freq1 == pair.F1 || c.ActiveMatch.Freq2 == pair.F2);
                if (!inUse)
                    return pair;
            }

            return null;
        }

        private static void AssignFreqs(ArenaData arenaData, Formation challenger, Formation acceptor, (short F1, short F2) pair)
        {
            if (acceptor.AssignedFreq.HasValue)
            {
                challenger.AssignedFreq = acceptor.AssignedFreq.Value == pair.F1 ? pair.F2 : pair.F1;
            }
            else if (challenger.AssignedFreq.HasValue)
            {
                acceptor.AssignedFreq = challenger.AssignedFreq.Value == pair.F1 ? pair.F2 : pair.F1;
            }
            else
            {
                challenger.AssignedFreq = pair.F1;
                acceptor.AssignedFreq = pair.F2;
            }
        }

        private static void ClearAllChallengesFor(ArenaData arenaData, Formation formation)
        {
            formation.SentChallengeTo = null;
            foreach (Formation f in arenaData.Formations.Values)
                if (f.SentChallengeTo == formation.Captain)
                    f.SentChallengeTo = null;
        }

        private Formation? GetPlayerFormation(ArenaData arenaData, Player player)
        {
            if (arenaData.Formations.TryGetValue(player, out Formation? f))
                return f;
            return GetNonCaptainFormation(arenaData, player);
        }

        private static Formation? GetNonCaptainFormation(ArenaData arenaData, Player player)
        {
            foreach (Formation f in arenaData.Formations.Values)
                if (f.Captain != player && f.Members.Contains(player))
                    return f;
            return null;
        }

        private static bool IsPlayerInMatch(ArenaData arenaData, Player player)
            => arenaData.PlayerToMatch.ContainsKey(player);

        private static Formation? FindFormationByCaptainName(ArenaData arenaData, ReadOnlySpan<char> name)
        {
            foreach (Formation f in arenaData.Formations.Values)
                if (name.Equals(f.Captain.Name, StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }

        private Player? FindPlayerInArena(Arena arena, ReadOnlySpan<char> name)
        {
            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.Players)
                    if (p.Arena == arena && name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                        return p;
                return null;
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        /// <summary>
        /// Prints the match stats chart. Pass <paramref name="winnerFreq"/> = -1 when match is still in progress.
        /// When <paramref name="recipient"/> is null the chart is sent to the whole arena.
        /// </summary>
        private void PrintMatchChart(Player? recipient, Arena arena, CaptainsMatchData matchData, short winnerFreq)
        {
            void Send(string line)
            {
                if (recipient is not null)
                    _chat.SendMessage(recipient, line);
                else
                    _chat.SendArenaMessage(arena, line);
            }

            Send("+--- Match Results ------------------------- K --- D ---+");

            int mvpKills = -1, lvpDeaths = -1;
            string? mvpName = null, lvpName = null;

            foreach (ITeam team in matchData.Teams)
            {
                bool isWinner = winnerFreq > 0 && team.Freq == winnerFreq;
                Send($"| Freq {team.Freq}{(isWinner ? " (W)" : "    ")}                                 K     D |");

                int teamKills = 0, teamDeaths = 0;
                foreach (IPlayerSlot iSlot in team.Slots)
                {
                    var slot = (CaptainsPlayerSlot)iSlot;
                    string name = (slot.PlayerName ?? "?").Length > 26 ? (slot.PlayerName ?? "?")[..26] : (slot.PlayerName ?? "?");
                    Send($"|   {name,-26}               {slot.Kills,3}   {slot.Deaths,3} |");
                    teamKills += slot.Kills;
                    teamDeaths += slot.Deaths;

                    if (slot.Kills > mvpKills) { mvpKills = slot.Kills; mvpName = slot.PlayerName; }
                    if (slot.Deaths > lvpDeaths) { lvpDeaths = slot.Deaths; lvpName = slot.PlayerName; }
                }

                Send($"|   {"Total",-26}               {teamKills,3}   {teamDeaths,3} |");
                Send("|                                                        |");
            }

            Send("+--------------------------------------------------------+");

            if (mvpKills > 0 && mvpName is not null)
                Send($"MVP: {mvpName} ({mvpKills} kill{(mvpKills != 1 ? "s" : "")})");
            if (lvpDeaths > 0 && lvpName is not null)
                Send($"LVP: {lvpName} ({lvpDeaths} death{(lvpDeaths != 1 ? "s" : "")})");
        }

        #endregion

        #region Data

        private sealed class ArenaConfig
        {
            public required long? GameTypeId;
            public required int PlayersPerTeam;
            public required int LivesPerPlayer;
            public required ShipType DefaultShip;
            public required List<(short F1, short F2)> FreqPairs;
            public required TimeSpan KickCooldown;
            public required TimeSpan? TimeLimit;
            public required TimeSpan? OverTimeLimit;
            public required TimeSpan WinConditionDelay;
            public required int TimeLimitWinBy;
            public required int MaxLagOuts;
            public required IOpenSkillModel OpenSkillModel;
            public required double OpenSkillSigmaDecayPerDay;
            public required bool OpenSkillUseScoresWhenPossible;
            /// <summary>Key: freq number. Value: tile coordinates to warp players to at match start.</summary>
            public required Dictionary<short, (short X, short Y)> StartLocations;
        }

        /// <summary>
        /// Represents a captain's team in the formation/challenge phase (before a match starts).
        /// </summary>
        private sealed class Formation
        {
            public Player Captain = null!;
            public readonly HashSet<Player> Members = [];

            /// <summary>The captain this formation has sent a challenge to, or null.</summary>
            public Player? SentChallengeTo;

            /// <summary>The opposing formation this team is paired with (challenge accepted), or null.</summary>
            public Formation? PairedWith;

            /// <summary>Whether the captain has typed ?ready after being paired.</summary>
            public bool IsReady;

            /// <summary>The freq assigned to this formation (set on challenge acceptance or preserved from previous match win).</summary>
            public short? AssignedFreq;
        }

        /// <summary>
        /// Represents one active or countdown match between two teams.
        /// Added to <see cref="ArenaData.ActiveMatches"/> when the match officially starts.
        /// </summary>
        private sealed class ActiveMatch
        {
            public Arena Arena = null!;
            public ArenaData ArenaData = null!;
            public CaptainsMatchData MatchData = null!;
            public short Freq1;
            public short Freq2;

            /// <summary>Players currently alive in this match.</summary>
            public readonly Dictionary<Player, CaptainsPlayerSlot> ActiveSlots = [];

            /// <summary>Players who specced out voluntarily mid-match (may still have lives).</summary>
            public readonly Dictionary<Player, CaptainsPlayerSlot> SpecOutSlots = [];

            /// <summary>Whether the match is currently in overtime.</summary>
            public bool IsOvertime;
        }

        /// <summary>
        /// Tracks countdown state for a pending match.
        /// Used as the timer key so multiple independent countdowns can run simultaneously.
        /// </summary>
        private sealed class MatchCountdown
        {
            public Arena Arena = null!;
            public ArenaData ArenaData = null!;
            public Formation Formation1 = null!; // on Freq1
            public Formation Formation2 = null!; // on Freq2
            public CaptainsMatchData PendingMatchData = null!;
            public ActiveMatch ActiveMatch = null!;
            public int Seconds;
        }

        private sealed class ArenaData : IResettable
        {
            public Arena Arena = null!;
            public ArenaConfig Config = null!;

            /// <summary>All active formations, keyed by captain player.</summary>
            public readonly Dictionary<Player, Formation> Formations = [];

            /// <summary>All currently active matches (started, not yet ended).</summary>
            public readonly List<ActiveMatch> ActiveMatches = [];

            /// <summary>All pending countdowns (both teams ready, countdown in progress).</summary>
            public readonly List<MatchCountdown> PendingCountdowns = [];

            /// <summary>
            /// Global lookup: player → their <see cref="ActiveMatch"/> (covers both countdown and active matches).
            /// Populated in <see cref="BuildMatchCountdown"/>; entries removed in <see cref="EndMatch"/> /
            /// <see cref="EndMatchDraw"/> / <see cref="DisbandFormation"/> / <see cref="HandlePlayerLeave"/>.
            /// </summary>
            public readonly Dictionary<Player, ActiveMatch> PlayerToMatch = [];

            /// <summary>Players kicked by a captain, mapped to the kick timestamp. Cleared when the match ends.</summary>
            public readonly Dictionary<string, DateTime> KickedPlayers = new(StringComparer.OrdinalIgnoreCase);

            bool IResettable.TryReset()
            {
                Arena = null!;
                Config = null!;
                Formations.Clear();
                ActiveMatches.Clear();
                PendingCountdowns.Clear();
                PlayerToMatch.Clear();
                KickedPlayers.Clear();
                return true;
            }
        }

        private sealed class PlayerData : IResettable
        {
            public Arena? ManagedArena;

            /// <summary>Ship requested via ?sc, applied on next respawn/rejoin.</summary>
            public ShipType? RequestedShip;

            /// <summary>Whether extra position data is being watched for item tracking.</summary>
            public bool IsWatchingExtraPositionData;

            bool IResettable.TryReset()
            {
                ManagedArena = null;
                RequestedShip = null;
                IsWatchingExtraPositionData = false;
                return true;
            }
        }

        // TeamVersus-compatible match data model used by MatchLvz and MatchFocus.

        private sealed class CaptainsMatchConfiguration : IMatchConfiguration
        {
            private readonly ArenaConfig _config;
            public CaptainsMatchConfiguration(ArenaConfig config) => _config = config;
            public long? GameTypeId => _config.GameTypeId;
            public int NumTeams => 2;
            public int PlayersPerTeam => _config.PlayersPerTeam;
            public int LivesPerPlayer => _config.LivesPerPlayer;
            public TimeSpan? TimeLimit => _config.TimeLimit;
            public TimeSpan? OverTimeLimit => _config.OverTimeLimit;
            public TimeSpan WinConditionDelay => _config.WinConditionDelay;
            public int TimeLimitWinBy => _config.TimeLimitWinBy;
            public int MaxLagOuts => _config.MaxLagOuts;
            public ReadOnlySpan<IMatchBoxConfiguration> Boxes => default;
            public IOpenSkillModel OpenSkillModel => _config.OpenSkillModel;
            public double OpenSkillSigmaDecayPerDay => _config.OpenSkillSigmaDecayPerDay;
            public bool OpenSkillUseScoresWhenPossible => _config.OpenSkillUseScoresWhenPossible;
        }

        private sealed class CaptainsMatchData : IMatchData
        {
            private ReadOnlyCollection<ITeam>? _teams;

            public CaptainsMatchData(Arena arena, IMatchConfiguration configuration, short matchSlotId)
            {
                MatchIdentifier = new MatchIdentifier("CaptainsMatch", arena.Number, matchSlotId);
                Configuration = configuration;
                ArenaName = arena.Name;
                Arena = arena;
            }

            public MatchIdentifier MatchIdentifier { get; }
            public IMatchConfiguration Configuration { get; }
            public string ArenaName { get; }
            public Arena? Arena { get; }
            public ReadOnlyCollection<ITeam> Teams => _teams!;
            public DateTime? Started { get; set; }
            public LeagueGameInfo? LeagueGame => null;

            public void SetTeams(CaptainsTeam team1, CaptainsTeam team2)
                => _teams = new ReadOnlyCollection<ITeam>([team1, team2]);
        }

        private sealed class CaptainsTeam : ITeam
        {
            private readonly List<CaptainsPlayerSlot> _rawSlots;
            private ReadOnlyCollection<IPlayerSlot>? _slotsReadOnly;

            public CaptainsTeam(IMatchData matchData, int teamIdx, short freq, List<CaptainsPlayerSlot> slots, Player? originalCaptain)
            {
                MatchData = matchData;
                TeamIdx = teamIdx;
                Freq = freq;
                _rawSlots = slots;
                OriginalCaptain = originalCaptain;
            }

            public IMatchData MatchData { get; }
            public int TeamIdx { get; }
            public short Freq { get; }
            public ReadOnlyCollection<IPlayerSlot> Slots => _slotsReadOnly ??= _rawSlots.ConvertAll<IPlayerSlot>(s => s).AsReadOnly();
            public short Score { get; set; }

            /// <summary>The captain at the time the match started (may have been knocked out since).</summary>
            public Player? OriginalCaptain { get; }
        }

        private sealed class CaptainsPlayerSlot : IPlayerSlot, IMemberStats
        {
            public CaptainsPlayerSlot(IMatchData matchData, ITeam team, int slotIdx, string playerName, Player? player, int lives, ShipType ship)
            {
                MatchData = matchData;
                Team = team;
                SlotIdx = slotIdx;
                PlayerName = playerName;
                Player = player;
                Lives = lives;
                Ship = ship;
            }

            public IMatchData MatchData { get; }
            public ITeam Team { get; }
            public int SlotIdx { get; }
            public string? PlayerName { get; }
            public Player? Player { get; set; }
            public int? PremadeGroupId => null;
            public int LagOuts { get; set; }
            public PlayerSlotStatus Status => Lives <= 0 ? PlayerSlotStatus.KnockedOut : Player is not null ? PlayerSlotStatus.Playing : PlayerSlotStatus.Waiting;
            public int Lives { get; set; }
            public ShipType Ship { get; set; }
            public byte Bursts { get; set; }
            public byte Repels { get; set; }
            public byte Thors { get; set; }
            public byte Bricks { get; set; }
            public byte Decoys { get; set; }
            public byte Rockets { get; set; }
            public byte Portals { get; set; }

            // IMemberStats
            public short Kills { get; set; }
            public short Deaths { get; set; }
        }

        #endregion
    }
}
