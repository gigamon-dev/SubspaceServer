using Microsoft.Extensions.ObjectPool;
using OpenSkillSharp;
using OpenSkillSharp.Models;
using SS.Core;
using System.Text;
using System.Text.Json;
using SS.Core.ComponentAdvisors;
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
    public sealed class CaptainsMatch : IModule, IArenaAttachableModule, IMatchFocusAdvisor, IFreqManagerEnforcerAdvisor, IPlayerGroupAdvisor
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

        // Ignore a second death packet within this window (50 ticks = 0.5 s) to mitigate a
        // client-side bug where a player receives two death packets for a single death.
        private const int DoubleDeathIgnoreTicks = 50;

        private IComponentBroker? _broker;
        private PlayerDataKey<PlayerData> _pdKey;
        private AdvisorRegistrationToken<IMatchFocusAdvisor>? _iMatchFocusAdvisorToken;
        private AdvisorRegistrationToken<IPlayerGroupAdvisor>? _iPlayerGroupAdvisorToken;

        // optional
        private ITeamVersusStatsBehavior? _tvStatsBehavior;
        private IMatchmakingQueues? _matchmakingQueues;
        private IPlayerGroups? _playerGroups;

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
            _matchmakingQueues = broker.GetInterface<IMatchmakingQueues>();
            _playerGroups = broker.GetInterface<IPlayerGroups>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            _iMatchFocusAdvisorToken = broker.RegisterAdvisor<IMatchFocusAdvisor>(this);
            _iPlayerGroupAdvisorToken = broker.RegisterAdvisor<IPlayerGroupAdvisor>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (!broker.UnregisterAdvisor(ref _iMatchFocusAdvisorToken))
                return false;

            if (!broker.UnregisterAdvisor(ref _iPlayerGroupAdvisorToken))
                return false;

            _mainloop.WaitForMainWorkItemDrain();

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _playerData.FreePlayerData(ref _pdKey);

            if (_tvStatsBehavior is not null)
                broker.ReleaseInterface(ref _tvStatsBehavior);

            if (_matchmakingQueues is not null)
                broker.ReleaseInterface(ref _matchmakingQueues);

            if (_playerGroups is not null)
                broker.ReleaseInterface(ref _playerGroups);

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
        [ConfigHelp("CaptainsMatch", "AllowShipChangeAfterDeathDuration", ConfigScope.Arena,
            Description = "Duration (TimeSpan, e.g. 00:00:05) after respawning from a death that a player may voluntarily change ships. Prevents item recycling by changing ships while alive. Empty or 0 = ship changes are always blocked during a match (queue via ?sc for next spawn).")]
        [ConfigHelp<int>("CaptainsMatch", "SubAvailableDelaySeconds", ConfigScope.Arena, Default = 30,
            Description = "Seconds after a player specs out before their slot becomes available for ?sub. Set to 0 for immediate. Captain can bypass with ?requestsub.")]
        [ConfigHelp<int>("CaptainsMatch", "MaxSubsPerTeam", ConfigScope.Arena, Default = 3,
            Description = "Maximum number of substitutions allowed per team per match. Set to 0 to disable the sub system.")]
        [ConfigHelp<int>("CaptainsMatch", "AbandonTimeoutSeconds", ConfigScope.Arena, Default = 10,
            Description = "Seconds a team with no active (playing) players has before the match is forfeited in their name. Set to 0 to disable.")]
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

            string[] shipNames = Enum.GetNames<ShipType>();
            var shipSettings = new ShipSettings[8];
            for (int i = 0; i < 8; i++)
            {
                shipSettings[i] = new ShipSettings
                {
                    InitialBurst = (byte)_configManager.GetInt(ch, shipNames[i], "InitialBurst", 0),
                    InitialRepel = (byte)_configManager.GetInt(ch, shipNames[i], "InitialRepel", 0),
                    InitialThor = (byte)_configManager.GetInt(ch, shipNames[i], "InitialThor", 0),
                    InitialBrick = (byte)_configManager.GetInt(ch, shipNames[i], "InitialBrick", 0),
                    InitialDecoy = (byte)_configManager.GetInt(ch, shipNames[i], "InitialDecoy", 0),
                    InitialRocket = (byte)_configManager.GetInt(ch, shipNames[i], "InitialRocket", 0),
                    InitialPortal = (byte)_configManager.GetInt(ch, shipNames[i], "InitialPortal", 0),
                };
            }

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
                AllowShipChangeAfterDeathDuration = TimeSpan.TryParse(
                    _configManager.GetStr(ch, "CaptainsMatch", "AllowShipChangeAfterDeathDuration"), out TimeSpan ascd)
                    ? ascd : TimeSpan.Zero,
                AbandonTimeoutMs = Math.Max(0, _configManager.GetInt(ch, "CaptainsMatch", "AbandonTimeoutSeconds", 10)) * 1000,
                SubAvailableDelay = TimeSpan.FromSeconds(Math.Max(0, _configManager.GetInt(ch, "CaptainsMatch", "SubAvailableDelaySeconds", 30))),
                MaxSubsPerTeam = _configManager.GetInt(ch, "CaptainsMatch", "MaxSubsPerTeam", 3),
                ShipSettings = shipSettings,
                OpenSkillModel = openSkillModel,
                OpenSkillSigmaDecayPerDay = sigmaDecayPerDay,
                OpenSkillUseScoresWhenPossible = _configManager.GetBool(ch, "CaptainsMatch", "OpenSkillUseScoresWhenPossible", false),
                StartLocations = startLocations,
            };

            _arenaDataDictionary[arena] = arenaData;

            arenaData.FreqManagerEnforcerAdvisorToken = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);

            KillCallback.Register(arena, Callback_Kill);
            SpawnCallback.Register(arena, Callback_Spawn);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            PlayerPositionPacketCallback.Register(arena, Callback_PlayerPositionPacket);

            _commandManager.AddCommand("captain", Command_Captain, arena);
            _commandManager.AddCommand("cap", Command_Captain, arena);
            _commandManager.AddCommand("join", Command_Join, arena);
            _commandManager.AddCommand("challenge", Command_Challenge, arena);
            _commandManager.AddCommand("chal", Command_Challenge, arena);
            _commandManager.AddCommand("accept", Command_Accept, arena);
            _commandManager.AddCommand("ready", Command_Ready, arena);
            _commandManager.AddCommand("rdy", Command_Ready, arena);
            _commandManager.AddCommand("cancel", Command_Cancel, arena);
            _commandManager.AddCommand("remove", Command_Remove, arena);
            _commandManager.AddCommand("ditch", Command_Leave, arena);
            _commandManager.AddCommand("end", Command_End, arena);
            _commandManager.AddCommand("sc", Command_Sc, arena);
            _commandManager.AddCommand("return", Command_Return, arena);
            _commandManager.AddCommand("items", Command_Items, arena);
            _commandManager.AddCommand("capinfo", Command_FreqInfo, arena);
            _commandManager.AddCommand("refuse", Command_Refuse, arena);
            _commandManager.AddCommand("disband", Command_Disband, arena);
            _commandManager.AddCommand("sub", Command_Sub, arena);
            _commandManager.AddCommand("requestsub", Command_RequestSub, arena);
            _commandManager.AddCommand("rsub", Command_RequestSub, arena);
            _commandManager.AddCommand("caphelp", Command_CapHelp, arena);
            // ?chart is provided by TeamVersusStats when it is attached to the arena.

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            _arenaDataDictionary.Remove(arena);

            if (!arena.UnregisterAdvisor(ref arenaData.FreqManagerEnforcerAdvisorToken))
                return false;

            KillCallback.Unregister(arena, Callback_Kill);
            SpawnCallback.Unregister(arena, Callback_Spawn);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPositionPacket);

            _commandManager.RemoveCommand("captain", Command_Captain, arena);
            _commandManager.RemoveCommand("cap", Command_Captain, arena);
            _commandManager.RemoveCommand("join", Command_Join, arena);
            _commandManager.RemoveCommand("challenge", Command_Challenge, arena);
            _commandManager.RemoveCommand("chal", Command_Challenge, arena);
            _commandManager.RemoveCommand("accept", Command_Accept, arena);
            _commandManager.RemoveCommand("ready", Command_Ready, arena);
            _commandManager.RemoveCommand("rdy", Command_Ready, arena);
            _commandManager.RemoveCommand("cancel", Command_Cancel, arena);
            _commandManager.RemoveCommand("remove", Command_Remove, arena);
            _commandManager.RemoveCommand("ditch", Command_Leave, arena);
            _commandManager.RemoveCommand("end", Command_End, arena);
            _commandManager.RemoveCommand("sc", Command_Sc, arena);
            _commandManager.RemoveCommand("return", Command_Return, arena);
            _commandManager.RemoveCommand("items", Command_Items, arena);
            _commandManager.RemoveCommand("capinfo", Command_FreqInfo, arena);
            _commandManager.RemoveCommand("refuse", Command_Refuse, arena);
            _commandManager.RemoveCommand("disband", Command_Disband, arena);
            _commandManager.RemoveCommand("sub", Command_Sub, arena);
            _commandManager.RemoveCommand("requestsub", Command_RequestSub, arena);
            _commandManager.RemoveCommand("rsub", Command_RequestSub, arena);
            _commandManager.RemoveCommand("caphelp", Command_CapHelp, arena);
            // ?chart is managed by TeamVersusStats.

            foreach (MatchCountdown c in arenaData.PendingCountdowns)
            {
                _mainloopTimer.ClearTimer<MatchCountdown>(Timer_PreCountdown, c);
                _mainloopTimer.ClearTimer<MatchCountdown>(Timer_Countdown, c);
            }

            foreach (ActiveMatch m in arenaData.ActiveMatches)
            {
                _mainloopTimer.ClearTimer<ActiveMatch>(Timer_MatchTimeExpired, m);
                _mainloopTimer.ClearTimer<ActiveMatch>(Timer_WinConditionCheck, m);
                _mainloopTimer.ClearTimer<AbandonState>(Timer_AbandonCheck, m.Freq1AbandonState);
                _mainloopTimer.ClearTimer<AbandonState>(Timer_AbandonCheck, m.Freq2AbandonState);

                foreach (CaptainsPlayerSlot slot in m.SpecOutSlots.Values)
                    CancelSubAvailableTimer(slot);
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

        #region IFreqManagerEnforcerAdvisor

        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder? errorMessage)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return ShipMask.None;

            if (arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match))
            {
                if (match.ActiveSlots.TryGetValue(player, out CaptainsPlayerSlot? activeSlot))
                {
                    if (CanShipChangeNow(arenaData, match, activeSlot))
                        return ShipMask.All;

                    // Outside the post-death window — lock to current ship; use ?sc to queue for next spawn.
                    errorMessage?.Append("You are in a match. Use ?sc <1-8> to change your ship for the next spawn.");
                    return player.Ship.GetShipMask();
                }

                if (match.SpecOutSlots.TryGetValue(player, out CaptainsPlayerSlot? specSlot))
                {
                    // Before GO (countdown phase), allow free re-entry — ?return is not yet available.
                    if (!arenaData.ActiveMatches.Contains(match))
                        return ShipMask.All;

                    if (specSlot.Lives <= 0)
                    {
                        errorMessage?.Append("You have been eliminated - no lives remaining.");
                        return ShipMask.None;
                    }

                    if (specSlot.LagOuts > arenaData.Config.MaxLagOuts)
                    {
                        errorMessage?.Append($"You have exceeded the maximum lagouts ({arenaData.Config.MaxLagOuts}) and cannot re-enter.");
                        return ShipMask.None;
                    }

                    errorMessage?.Append("Use ?return to re-enter the match.");
                    return ShipMask.None;
                }
            }

            // Not in a match — allow ship selection only if assigned to a formation freq.
            if (player.TryGetExtraData(_pdKey, out PlayerData? pd) && pd.ManagedArena == arena)
                return ShipMask.All;

            errorMessage?.Append("You are not on a team. Use ?captain to become a captain or ?join <name> to join one.");
            return ShipMask.None;
        }

        bool IFreqManagerEnforcerAdvisor.CanChangeToFreq(Player player, short newFreq, StringBuilder? errorMessage)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            bool isMatchFreq = arenaData.Config.FreqPairs.Any(p => p.F1 == newFreq || p.F2 == newFreq);
            if (!isMatchFreq)
                return true; // Non-match freqs are freely allowed.

            // Allow spectators to join match freqs for team comms.
            if (player.Ship == ShipType.Spec)
                return true;

            // It is a match freq — player must be assigned to it.
            if (arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match))
            {
                CaptainsPlayerSlot? slot = null;
                match.ActiveSlots.TryGetValue(player, out slot);
                if (slot is null) match.SpecOutSlots.TryGetValue(player, out slot);

                if (slot?.Team.Freq == newFreq)
                    return true;

                errorMessage?.Append("You cannot change to that freq while in a match.");
                return false;
            }

            Formation? formation = GetPlayerFormation(arenaData, player);
            if (formation?.AssignedFreq == newFreq)
                return true;

            errorMessage?.Append("You are not assigned to that freq.");
            return false;
        }

        bool IFreqManagerEnforcerAdvisor.CanEnterGame(Player player, StringBuilder? errorMessage)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            if (arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match))
            {
                if (match.SpecOutSlots.TryGetValue(player, out CaptainsPlayerSlot? specSlot))
                {
                    // Before GO (countdown phase), allow free re-entry — ?return is not yet available.
                    if (!arenaData.ActiveMatches.Contains(match))
                        return true;

                    if (specSlot.Lives <= 0)
                    {
                        errorMessage?.Append("You have been eliminated — no lives remaining.");
                        return false;
                    }

                    if (specSlot.LagOuts > arenaData.Config.MaxLagOuts)
                    {
                        errorMessage?.Append($"You have exceeded the maximum lagouts ({arenaData.Config.MaxLagOuts}) and cannot re-enter.");
                        return false;
                    }

                    // Eligible to return — but must use ?return explicitly, not by pressing a ship key.
                    errorMessage?.Append("Use ?return to re-enter the match.");
                    return false;
                }

                return false;
            }

            // Allow entering the game if assigned to a formation freq.
            if (player.TryGetExtraData(_pdKey, out PlayerData? pd) && pd.ManagedArena == arena)
                return true;

            errorMessage?.Append("You are not on a team. Use ?captain to become a captain or ?join <name> to join one.");
            return false;
        }

        bool IFreqManagerEnforcerAdvisor.IsUnlocked(Player player, StringBuilder? errorMessage) => true;

        #endregion

        #region IPlayerGroupAdvisor

        bool IPlayerGroupAdvisor.CanPlayerCreateGroup(Player player, StringBuilder message)
        {
            if (IsPlayerInCaptains(player))
            {
                message?.Append("Cannot create a group while in a captains team or match. Use ?ditch or ?disband first.");
                return false;
            }
            return true;
        }

        bool IPlayerGroupAdvisor.CanPlayerBeInvited(Player player, StringBuilder message)
        {
            if (IsPlayerInCaptains(player))
            {
                message?.Append($"Cannot invite {player.Name} — they are in a captains team or match.");
                return false;
            }
            return true;
        }

        bool IPlayerGroupAdvisor.CanPlayerAcceptInvite(Player player, StringBuilder message)
        {
            if (IsPlayerInCaptains(player))
            {
                message?.Append("Cannot accept a group invite while in a captains team or match. Use ?ditch or ?disband first.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true if the player is in any captains formation or active/pending match.
        /// </summary>
        private bool IsPlayerInCaptains(Player player)
        {
            foreach (ArenaData arenaData in _arenaDataDictionary.Values)
            {
                if (GetPlayerFormation(arenaData, player) is not null)
                    return true;

                if (IsPlayerInMatch(arenaData, player))
                    return true;
            }
            return false;
        }

        #endregion

        #region Callbacks

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                if (arena is not null && _arenaDataDictionary.TryGetValue(arena, out ArenaData? enterArenaData))
                    HandlePlayerEnter(arena, enterArenaData, player);
                return;
            }

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

            // Ignore kills before GO! — the match isn't officially active yet.
            if (!arenaData.ActiveMatches.Contains(killedMatch))
                return;

            if (!killedMatch.ActiveSlots.TryGetValue(killed, out CaptainsPlayerSlot? killedSlot))
                return;

            // Ignore a second death packet that arrives within the double-death window.
            // This mitigates a client bug where the player only truly dies once but the
            // client sends two death packets in rapid succession.
            ServerTick now = ServerTick.Now;
            if (killedSlot.LastKilledTick is { } lastTick && (now - lastTick) < DoubleDeathIgnoreTicks)
                return;

            killedSlot.LastKilledTick = now;

            CaptainsPlayerSlot? killerSlot = null;
            if (killer is not null
                && arenaData.PlayerToMatch.TryGetValue(killer, out ActiveMatch? killerMatch)
                && killerMatch == killedMatch)
            {
                // Also check SpecOutSlots: in a simultaneous double-KO the killer may already
                // have been removed from ActiveSlots by the time this callback fires.
                if (!killerMatch.ActiveSlots.TryGetValue(killer, out killerSlot))
                    killerMatch.SpecOutSlots.TryGetValue(killer, out killerSlot);
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

            // Always fire the kill callbacks so MatchLvz can update lives/deaths in the statbox,
            // even when there's no valid killer (e.g. mine kill). Use killedSlot as a fallback
            // for the killer parameters — MatchLvz only reads killedSlot from these callbacks.
            TeamVersusMatchPlayerKilledCallback.Fire(arena, killedSlot, killerSlot ?? killedSlot, isKnockout);
            TeamVersusStatsPlayerKilledCallback.Fire(arena, killedSlot, killedSlot, killerSlot ?? killedSlot, killerSlot ?? killedSlot, isKnockout);

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

            // Track in SpecOutSlots (lives=0) so EndMatch can clean up PlayerToMatch correctly.
            killedMatch.SpecOutSlots[killed] = killedSlot;

            if (killerSlot is not null && _tvStatsBehavior is not null && matchIsActive)
                _ = _tvStatsBehavior.PlayerKilledAsync(ServerTick.Now, DateTime.UtcNow, killedMatch.MatchData, killed, killedSlot, killer!, killerSlot, isKnockout);

            CheckAbandonCondition(arenaData, killedMatch, killedSlot.Team.Freq);

            // Delay the elimination check to handle simultaneous double-KO scenarios.
            ScheduleWinConditionCheck(arenaData, killedMatch);
        }

        private void Callback_Spawn(Player player, SpawnCallback.SpawnReason reason)
        {
            bool isAfterDeath = (reason & SpawnCallback.SpawnReason.AfterDeath) != 0;
            bool isShipChange = (reason & SpawnCallback.SpawnReason.ShipChange) != 0;

            if (!isAfterDeath)
                return;

            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match)
                || !arenaData.ActiveMatches.Contains(match))
                return;

            if (!match.ActiveSlots.TryGetValue(player, out CaptainsPlayerSlot? slot))
                return;

            // Open the ship-change window on each death respawn.
            TimeSpan duration = arenaData.Config.AllowShipChangeAfterDeathDuration;
            slot.AllowShipChangeExpiration = duration > TimeSpan.Zero
                ? DateTime.UtcNow + duration
                : null;

            // Apply queued ship if this spawn wasn't itself triggered by a ship change.
            if (!isShipChange
                && player.TryGetExtraData(_pdKey, out PlayerData? pd)
                && pd.RequestedShip is { } requestedShip
                && player.Ship != requestedShip)
            {
                pd.RequestedShip = null;
                _game.SetShip(player, requestedShip); // fires Callback_Spawn again with ShipChange flag
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> if <paramref name="slot"/>'s player is currently allowed to voluntarily
        /// change ships: either the match hasn't gone live yet (countdown) or they are within their post-death window.
        /// </summary>
        private static bool CanShipChangeNow(ArenaData arenaData, ActiveMatch match, CaptainsPlayerSlot slot)
        {
            // Before GO — free to pick any ship.
            if (!arenaData.ActiveMatches.Contains(match))
                return true;

            // In progress — only within the post-death window.
            return slot.AllowShipChangeExpiration is { } exp && exp > DateTime.UtcNow;
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match))
            {
                // Not in an active match/countdown — check if in a formation phase.
                if (newShip == ShipType.Spec && oldShip != ShipType.Spec)
                {
                    Formation? formation = GetPlayerFormation(arenaData, player);
                    if (formation is not null)
                    {
                        _logManager.LogP(LogLevel.Drivel, nameof(CaptainsMatch), player, "Specced during formation phase.");
                        if (formation.IsReady)
                        {
                            formation.IsReady = false;
                            SendToFormationPair(formation, formation.PairedWith, $"{player.Name} specced — {formation.Captain.Name}'s team is no longer ready.");
                        }
                    }
                }
                else if (newShip != ShipType.Spec && oldShip == ShipType.Spec)
                {
                    // Formation player re-entering — ensure they land on their assigned freq.
                    Formation? formation = GetPlayerFormation(arenaData, player);
                    if (formation?.AssignedFreq is { } assignedFreq && newFreq != assignedFreq)
                    {
                        _game.SetShipAndFreq(player, newShip, assignedFreq);
                    }
                }
                return;
            }

            if (newShip == ShipType.Spec && oldShip != ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(CaptainsMatch), player, "Specced during match.");

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
                    slot.SpecOutTimestamp = DateTime.UtcNow;

                    // Schedule a sub-available announcement after the delay.
                    if (arenaData.ActiveMatches.Contains(match)
                        && arenaData.Config.MaxSubsPerTeam > 0
                        && arenaData.Config.SubAvailableDelay > TimeSpan.Zero)
                    {
                        int delayMs = (int)arenaData.Config.SubAvailableDelay.TotalMilliseconds;
                        SubAvailableState subState = new()
                        {
                            Arena = arena,
                            ArenaData = arenaData,
                            Match = match,
                            Slot = slot,
                        };
                        slot.SubAvailableTimerKey = subState;
                        _mainloopTimer.SetTimer<SubAvailableState>(Timer_SubAvailable, delayMs, Timeout.Infinite, subState, subState);
                    }

                    if (arenaData.ActiveMatches.Contains(match))
                    {
                        TimeSpan elapsed = match.MatchData.Started is { } s ? DateTime.UtcNow - s : TimeSpan.Zero;
                        string elapsedStr = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
                        SendToMatchParticipants(match, $"{player.Name} has specced. (Count: {slot.LagOuts}) [{elapsedStr}]");
                    }
                    else
                    {
                        // Specced during countdown (before GO) — notify all participants.
                        SendToMatchParticipants(match, $"{player.Name} specced during the countdown. (Count: {slot.LagOuts})");
                    }

                    // Check win condition (all lives gone) and abandon condition (all specced out).
                    bool allOut = !match.ActiveSlots.Values.Any(s => s.Team.Freq == oldFreq);
                    if (allOut && arenaData.ActiveMatches.Contains(match))
                    {
                        ScheduleWinConditionCheck(arenaData, match);
                        CheckAbandonCondition(arenaData, match, slot.Team.Freq);
                    }
                    else if (allOut && !arenaData.ActiveMatches.Contains(match))
                    {
                        // All players on this team specced out during the countdown (before GO).
                        // Abort the countdown rather than letting it fire with an empty team.
                        MatchCountdown? countdown = arenaData.PendingCountdowns.Find(
                            c => c.ActiveMatch == match);
                        if (countdown is not null)
                            AbortCountdown(arena, arenaData, countdown, $"{player.Name} specced - countdown aborted.");
                    }
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
                    if (slot.Lives <= 0)
                    {
                        // Knocked out — cannot re-enter.
                        _game.SetShipAndFreq(player, ShipType.Spec, newFreq);
                    }
                    else if (newFreq == slot.Team.Freq)
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
                        slot.SpecOutTimestamp = null;
                        slot.SubRequested = false;
                        CancelSubAvailableTimer(slot);
                        CancelAbandonTimer(match, slot.Team.Freq);

                        // If the match has already started (post-GO), register the player with
                        // MatchFocus so position packets are routed correctly to teammates.
                        if (arenaData.ActiveMatches.Contains(match))
                            MatchAddPlayingCallback.Fire(_broker!, match.MatchData, player.Name!, player);

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
                    else
                    {
                        // Trying to switch teams mid-match — reject.
                        _game.SetShipAndFreq(player, ShipType.Spec, newFreq);
                        _chat.SendMessage(player, $"You cannot switch teams mid-match. You are assigned to Freq {slot.Team.Freq}.");
                    }
                }
            }
            else if (newShip != ShipType.Spec && oldShip != ShipType.Spec)
            {
                // Ship changed while alive (during the post-death window).
                if (match.ActiveSlots.TryGetValue(player, out CaptainsPlayerSlot? activeSlot))
                {
                    activeSlot.Ship = newShip;
                    activeSlot.AllowShipChangeExpiration = null; // one change per window
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

            // Clear the ship-change window when the player engages (fires a weapon).
            if (slot.AllowShipChangeExpiration is not null
                && positionPacket.Weapon.Type != WeaponCodes.Null)
            {
                slot.AllowShipChangeExpiration = null;
            }
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None,
            Description = "Become a captain and create a team. Other players can then ?join your team.")]
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

            if (_playerGroups?.GetGroup(player) is not null)
            {
                _chat.SendMessage(player, "You are in a group. Use ?group leave or ?group disband first.");
                return;
            }

            if (GetPlayerFormation(arenaData, player) is not null)
            {
                _chat.SendMessage(player, "You are already in a team formation. Use ?ditch or ?disband to leave first.");
                return;
            }
            if (IsPlayerInMatch(arenaData, player))
            {
                _chat.SendMessage(player, "You are already in an active match.");
                return;
            }

            var formation = new Formation { Captain = player, CreatedAt = DateTime.UtcNow };
            formation.Members.Add(player);
            arenaData.Formations[player] = formation;

            if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                pd.ManagedArena = arena;

            SendToAvailablePlayers(arena, arenaData, $"{player.Name} is now a captain! Type ?join {player.Name} or PM them with ?join to join their team. Team: {FormatTeamRoster(formation, arenaData.Config.PlayersPerTeam)}");
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "[<captain name>]",
            Description = """
                Join a captain's team. The captain must have used ?captain first.
                You can also PM the captain directly with ?join.
                If no captain name is specified, you will be auto-assigned to the team closest to being full.
                """)]
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

            if (_playerGroups?.GetGroup(player) is not null)
            {
                _chat.SendMessage(player, "You are in a group. Use ?group leave or ?group disband first.");
                return;
            }

            if (GetPlayerFormation(arenaData, player) is not null)
            {
                _chat.SendMessage(player, "You are already in a team formation. Use ?ditch or ?disband to leave first.");
                return;
            }
            if (IsPlayerInMatch(arenaData, player))
            {
                _chat.SendMessage(player, "You are already in an active match.");
                return;
            }

            // Allow PMing the captain directly: /?join sent as a private message to the captain.
            ReadOnlySpan<char> captainName = target.TryGetPlayerTarget(out Player? targetPlayer)
                ? targetPlayer.Name.AsSpan()
                : parameters.Trim();

            Formation? targetFormation;

            if (captainName.IsEmpty)
            {
                // Auto-assign: find the best available team.
                targetFormation = FindBestFormationForAutoJoin(arenaData);
                if (targetFormation is null)
                {
                    _chat.SendMessage(player, "No teams are currently looking for players. Type ?captain to start your own team.");
                    return;
                }
            }
            else
            {
                targetFormation = FindFormationByCaptainName(arenaData, captainName);
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
            }

            // Check kick cooldown for the target captain.
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

            targetFormation.Members.Add(player);
            targetFormation.IsReady = false; // team composition changed

            if (player.TryGetExtraData(_pdKey, out PlayerData? jpd))
                jpd.ManagedArena = arena;

            SendToAvailablePlayers(arena, arenaData, $"{player.Name} joined {targetFormation.Captain.Name}'s team. Team: {FormatTeamRoster(targetFormation, arenaData.Config.PlayersPerTeam)}");
        }

        /// <summary>
        /// Finds the best formation for auto-join: the non-full, non-paired formation with the most
        /// members. Ties broken by oldest creation time. Returns null if no eligible formation exists.
        /// </summary>
        private static Formation? FindBestFormationForAutoJoin(ArenaData arenaData)
        {
            Formation? best = null;

            foreach (Formation f in arenaData.Formations.Values)
            {
                // Skip full teams.
                if (f.Members.Count >= arenaData.Config.PlayersPerTeam)
                    continue;

                // Skip teams already paired for a match.
                if (f.PairedWith is not null)
                    continue;

                // Compare: most members first, then oldest creation time.
                if (best is null
                    || f.Members.Count > best.Members.Count
                    || (f.Members.Count == best.Members.Count && f.CreatedAt < best.CreatedAt))
                {
                    best = f;
                }
            }

            return best;
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<captain name>",
            Description = "Challenge another captain's formed team to a match. Both teams must be full before challenging.")]
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

                SendToAvailablePlayers(arena, arenaData,
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
            SendToAvailablePlayers(arena, arenaData, $"{player.Name}'s team has challenged {targetFormation.Captain.Name}'s team to a match!");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Description = "Accept a pending challenge from another captain.")]
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

            if (challengerFormation.Members.Count < arenaData.Config.PlayersPerTeam)
            {
                _chat.SendMessage(player, $"{challengerFormation.Captain.Name}'s team is not full ({challengerFormation.Members.Count}/{arenaData.Config.PlayersPerTeam}). Cannot accept.");
                _chat.SendMessage(challengerFormation.Captain, $"{player.Name} tried to accept your challenge, but your team is not full ({challengerFormation.Members.Count}/{arenaData.Config.PlayersPerTeam}). Fill your roster first.");
                return;
            }

            if (myFormation.Members.Count < arenaData.Config.PlayersPerTeam)
            {
                _chat.SendMessage(player, $"Your team is not full ({myFormation.Members.Count}/{arenaData.Config.PlayersPerTeam}). Fill your roster before accepting.");
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

            SendToAvailablePlayers(arena, arenaData,
                $"Challenge accepted! {challengerFormation.Captain.Name}'s team (Freq {challengerFormation.AssignedFreq}) vs "
                + $"{myFormation.Captain.Name}'s team (Freq {myFormation.AssignedFreq}). "
                + "Both captains type ?ready to start the match!");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Description = "Mark your team as ready to start. Both captains must ?ready after a challenge is accepted. Also aliased as ?rdy.")]
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

            if (myFormation.Members.Count < arenaData.Config.PlayersPerTeam)
            {
                int needed = arenaData.Config.PlayersPerTeam - myFormation.Members.Count;
                _chat.SendMessage(player, $"Cannot ready up — your team needs {needed} more player(s). ({myFormation.Members.Count}/{arenaData.Config.PlayersPerTeam})");

                // Unpair both formations so the short team can recruit and the opponent can re-challenge.
                Formation opponent = myFormation.PairedWith!;
                myFormation.PairedWith = null;
                myFormation.AssignedFreq = null;
                myFormation.IsReady = false;
                opponent.PairedWith = null;
                opponent.AssignedFreq = null;
                opponent.IsReady = false;

                SendToAvailablePlayers(arena, arenaData, $"{player.Name}'s team lost a player — match pairing cancelled. Team is recruiting.");
                return;
            }

            if (myFormation.IsReady)
            {
                _chat.SendMessage(player, "Your team is already marked as ready.");
                return;
            }

            // Sanity check: all members must be in a ship on the assigned freq.
            if (myFormation.AssignedFreq is { } assignedFreq)
            {
                List<string>? notReady = null;
                foreach (Player member in myFormation.Members)
                {
                    if (member.Ship == ShipType.Spec || member.Freq != assignedFreq)
                    {
                        notReady ??= [];
                        notReady.Add(member.Name!);
                    }
                }

                if (notReady is not null)
                {
                    _chat.SendMessage(player, $"Cannot ready up - the following player(s) are not in a ship on Freq {assignedFreq}: {string.Join(", ", notReady)}. (If you cannot field a full team, use ?disband to abandon matchmaking.)");
                    return;
                }
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

        [CommandHelp(
            Targets = CommandTarget.None,
            Description = "Unready your team if you previously typed ?ready, or abort a running countdown.")]
        private void Command_Cancel(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.Formations.TryGetValue(player, out Formation? myFormation))
            {
                _chat.SendMessage(player, "You must be a captain to cancel ready.");
                return;
            }

            if (myFormation.PairedWith is null)
            {
                _chat.SendMessage(player, "Your team has no pending match to cancel.");
                return;
            }

            if (!myFormation.IsReady)
            {
                _chat.SendMessage(player, "Your team has not readied up yet.");
                return;
            }

            // If a countdown is already running, abort it.
            MatchCountdown? countdown = arenaData.PendingCountdowns.Find(
                c => c.Formation1 == myFormation || c.Formation2 == myFormation);
            if (countdown is not null)
            {
                AbortCountdown(arena, arenaData, countdown,
                    $"Countdown aborted: {player.Name} cancelled.");
                return;
            }

            // Only this team was ready — just un-ready.
            myFormation.IsReady = false;
            SendToFormationPair(myFormation, myFormation.PairedWith, $"{player.Name} cancelled ready.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<player name>",
            Description = "Captains only: remove a player from your team and return them to spec.")]
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
            _chat.SendMessage(player, $"Team: {FormatTeamRoster(myFormation, arenaData.Config.PlayersPerTeam)}");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Description = "Leave your current team and return to spec. For captains, use ?disband instead.")]
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
                bool wasReady = memberFormation.IsReady;

                // If a countdown is in progress, abort it — ditching is a forfeit of participation.
                bool countdownAborted = false;
                if (arenaData.PlayerToMatch.ContainsKey(player))
                {
                    MatchCountdown? countdown = arenaData.PendingCountdowns.Find(
                        c => c.Formation1 == memberFormation || c.Formation2 == memberFormation);
                    if (countdown is not null)
                    {
                        AbortCountdown(arena, arenaData, countdown, $"{player.Name} left {memberFormation.Captain.Name}'s team — countdown aborted.");
                        countdownAborted = true;
                    }
                }

                memberFormation.Members.Remove(player);
                memberFormation.IsReady = false;

                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;

                if (player.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(player, ShipType.Spec, player.Freq);

                // If the team was ready and no countdown abort already covered it, notify the partner.
                if (wasReady && !countdownAborted && memberFormation.PairedWith is not null)
                    SendToFormationPair(memberFormation, memberFormation.PairedWith,
                        $"{player.Name} left {memberFormation.Captain.Name}'s team — they are no longer ready.");

                _chat.SendMessage(player, $"You have left {memberFormation.Captain.Name}'s team.");
                _chat.SendMessage(memberFormation.Captain, $"{player.Name} left your team. Team: {FormatTeamRoster(memberFormation, arenaData.Config.PlayersPerTeam)}");
                return;
            }

            _chat.SendMessage(player, "You are not in any team.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Description = "Captains only: forfeit the current match for your team.")]
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

            if (!(playerSlot.Team is CaptainsTeam ct && ct.OriginalCaptain == player))
            {
                _chat.SendMessage(player, "Only the team captain can forfeit.");
                return;
            }

            short losingFreq = playerSlot.Team.Freq;
            _chat.SendArenaMessage(arena, $"{player.Name}'s team (Freq {losingFreq}) has forfeited!");
            EndMatch(arena, arenaData, match, losingFreq);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[captain name]",
            Description = "Refuse a pending challenge. If multiple challenges are pending, specify the challenger's name.")]
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

        [CommandHelp(
            Targets = CommandTarget.None,
            Description = "Captains only: disband your entire team and return all members to spec.")]
        private void Command_Disband(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.Formations.TryGetValue(player, out Formation? myFormation))
            {
                if (IsPlayerInMatch(arenaData, player))
                    _chat.SendMessage(player, "You are in an active match. Use ?end to forfeit.");
                else
                    _chat.SendMessage(player, "You are not a captain.");
                return;
            }

            bool inCountdown = arenaData.PendingCountdowns.Any(c => c.Formation1 == myFormation || c.Formation2 == myFormation);
            if (inCountdown)
            {
                _chat.SendMessage(player, "A match is about to start. Use ?cancel to abort the countdown.");
                return;
            }

            DisbandFormation(arena, arenaData, myFormation, $"{player.Name}'s team has been disbanded.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<1-8>",
            Description = "Queue a ship change for your next spawn during an active match.")]
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
                // Only allow an immediate ship change if within the post-death window (or before GO).
                // Outside that window the player must wait for their next spawn to prevent item recycling.
                bool canChangeNow = match.ActiveSlots.TryGetValue(player, out CaptainsPlayerSlot? slot)
                    && CanShipChangeNow(arenaData, match, slot);

                if (canChangeNow)
                {
                    if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                        pd.RequestedShip = null;
                    _game.SetShipAndFreq(player, ship, player.Freq);
                    _chat.SendMessage(player, $"Ship changed to {ship}.");
                }
                else
                {
                    if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                        pd.RequestedShip = ship;
                    _chat.SendMessage(player, $"Ship queued: {ship} will apply on your next spawn.");
                }
            }
            else
            {
                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.RequestedShip = ship;
                _chat.SendMessage(player, $"Ship set to {ship}. It will apply when you re-enter.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Description = "Re-enter a match after lagging out, if you still have lives remaining.")]
        private void Command_Return(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match)
                || !arenaData.ActiveMatches.Contains(match))
            {
                _chat.SendMessage(player, "?return is only available during an active match.");
                return;
            }

            if (match.ActiveSlots.ContainsKey(player))
            {
                _chat.SendMessage(player, "You are already in play.");
                return;
            }

            if (!match.SpecOutSlots.TryGetValue(player, out CaptainsPlayerSlot? slot))
            {
                _chat.SendMessage(player, "You are not in this match.");
                return;
            }

            if (slot.Lives <= 0)
            {
                _chat.SendMessage(player, "You have been eliminated and cannot return.");
                return;
            }

            if (slot.LagOuts > arenaData.Config.MaxLagOuts)
            {
                _chat.SendMessage(player, $"You cannot return: exceeded maximum lagouts ({arenaData.Config.MaxLagOuts}).");
                return;
            }

            // Return the player to their assigned freq and ship. Callback_ShipFreqChange handles slot tracking.
            _game.SetShipAndFreq(player, arenaData.Config.DefaultShip, slot.Team.Freq);
            // Burn initial items so the player spawns clean. Use player.Ship in case the callback corrected the ship.
            RemoveAllItems(player, player.Ship, arenaData.Config.ShipSettings);
            _chat.SendArenaMessage(arena, $"{player.Name} has returned (burned).");
        }

        private void Command_Sub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (player.Ship != ShipType.Spec)
            {
                _chat.SendMessage(player, "You must be in spec to sub into a match.");
                return;
            }

            if (arenaData.PlayerToMatch.ContainsKey(player))
            {
                _chat.SendMessage(player, "You are already in a match.");
                return;
            }

            if (GetPlayerFormation(arenaData, player) is not null)
            {
                _chat.SendMessage(player, "You are on a team. Use ?ditch or ?disband first.");
                return;
            }

            if (_playerGroups?.GetGroup(player) is not null)
            {
                _chat.SendMessage(player, "You are in a group. Use ?group leave or ?group disband first.");
                return;
            }

            if (arenaData.Config.MaxSubsPerTeam <= 0)
            {
                _chat.SendMessage(player, "Substitutions are disabled in this arena.");
                return;
            }

            // Parse optional arguments: ?sub [freq] [ship]
            // A number 1-8 that matches a configured freq pair is treated as a freq.
            // A number 1-8 that does NOT match a freq pair is treated as a ship.
            // Two numbers: first is freq, second is ship.
            short? requestedFreq = null;
            ShipType? requestedShip = null;

            if (!parameters.IsEmpty)
            {
                ReadOnlySpan<char> remaining = parameters.Trim();
                ReadOnlySpan<char> first = remaining.GetToken(' ', out remaining);
                remaining = remaining.TrimStart();

                if (short.TryParse(first, out short firstVal))
                {
                    bool isFreqPair = arenaData.Config.FreqPairs.Any(p => p.F1 == firstVal || p.F2 == firstVal);

                    if (!remaining.IsEmpty && short.TryParse(remaining, out short secondVal))
                    {
                        // Two args: freq ship
                        requestedFreq = firstVal;
                        if (secondVal >= 1 && secondVal <= 8)
                            requestedShip = (ShipType)(secondVal - 1);
                    }
                    else if (isFreqPair)
                    {
                        // Single arg matches a freq pair — treat as freq.
                        requestedFreq = firstVal;
                    }
                    else if (firstVal >= 1 && firstVal <= 8)
                    {
                        // Single arg 1-8 that isn't a freq pair — treat as ship.
                        requestedShip = (ShipType)(firstVal - 1);
                    }
                    else
                    {
                        _chat.SendMessage(player, $"Invalid argument: {firstVal}. Usage: ?sub [freq] [ship 1-8]");
                        return;
                    }
                }
            }

            // Find an eligible slot.
            CaptainsPlayerSlot? targetSlot = null;
            ActiveMatch? targetMatch = null;
            Player? oldPlayer = null;
            int eligibleCount = 0;

            foreach (ActiveMatch match in arenaData.ActiveMatches)
            {
                foreach ((Player p, CaptainsPlayerSlot slot) in match.SpecOutSlots)
                {
                    if (!IsSlotSubEligible(slot, arenaData.Config))
                        continue;

                    if (requestedFreq.HasValue && slot.Team.Freq != requestedFreq.Value)
                        continue;

                    // Check MaxSubsPerTeam.
                    int subsUsed = slot.Team.Freq == match.Freq1 ? match.Freq1SubsUsed : match.Freq2SubsUsed;
                    if (subsUsed >= arenaData.Config.MaxSubsPerTeam)
                        continue;

                    eligibleCount++;
                    if (targetSlot is null)
                    {
                        targetSlot = slot;
                        targetMatch = match;
                        oldPlayer = p;
                    }
                }
            }

            if (targetSlot is null)
            {
                if (eligibleCount == 0)
                    _chat.SendMessage(player, "No slots are available for substitution.");
                else
                    _chat.SendMessage(player, "No slots matched the specified freq.");
                return;
            }

            if (!requestedFreq.HasValue && eligibleCount > 1)
            {
                // Collect the distinct freqs that need subs.
                HashSet<short> eligibleFreqs = [];
                foreach (ActiveMatch match in arenaData.ActiveMatches)
                    foreach ((Player p, CaptainsPlayerSlot slot) in match.SpecOutSlots)
                        if (IsSlotSubEligible(slot, arenaData.Config))
                        {
                            int subsUsed = slot.Team.Freq == match.Freq1 ? match.Freq1SubsUsed : match.Freq2SubsUsed;
                            if (subsUsed < arenaData.Config.MaxSubsPerTeam)
                                eligibleFreqs.Add(slot.Team.Freq);
                        }

                _chat.SendMessage(player, $"Multiple slots need a sub. Specify a freq: {string.Join(", ", eligibleFreqs.Select(f => $"?sub {f}"))}");
                return;
            }

            // Execute the sub with the requested ship (or DefaultShip if not specified).
            ShipType subShip = requestedShip ?? arenaData.Config.DefaultShip;
            ExecuteSub(arena, arenaData, targetMatch!, targetSlot, oldPlayer!, player, subShip);
        }

        private void Command_RequestSub(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return;

            if (!arenaData.PlayerToMatch.TryGetValue(player, out ActiveMatch? match)
                || !arenaData.ActiveMatches.Contains(match))
            {
                _chat.SendMessage(player, "You must be in an active match to request a sub.");
                return;
            }

            if (arenaData.Config.MaxSubsPerTeam <= 0)
            {
                _chat.SendMessage(player, "Substitutions are disabled in this arena.");
                return;
            }

            // Determine the caller's team freq.
            short callerFreq;
            if (match.ActiveSlots.TryGetValue(player, out CaptainsPlayerSlot? callerSlot))
                callerFreq = callerSlot.Team.Freq;
            else if (match.SpecOutSlots.TryGetValue(player, out callerSlot))
                callerFreq = callerSlot.Team.Freq;
            else
            {
                _chat.SendMessage(player, "You are not a participant in this match.");
                return;
            }

            // Check MaxSubsPerTeam.
            int subsUsed = callerFreq == match.Freq1 ? match.Freq1SubsUsed : match.Freq2SubsUsed;
            if (subsUsed >= arenaData.Config.MaxSubsPerTeam)
            {
                _chat.SendMessage(player, $"Your team has used all {arenaData.Config.MaxSubsPerTeam} substitutions.");
                return;
            }

            // Find the target slot.
            CaptainsPlayerSlot? targetSlot = null;
            ReadOnlySpan<char> targetName = parameters.Trim();

            foreach ((Player p, CaptainsPlayerSlot slot) in match.SpecOutSlots)
            {
                if (slot.Team.Freq != callerFreq)
                    continue;

                if (slot.Lives <= 0 || slot.WasSubbedIn)
                    continue;

                if (!targetName.IsEmpty)
                {
                    if (slot.PlayerName is not null && targetName.Equals(slot.PlayerName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetSlot = slot;
                        break;
                    }
                }
                else
                {
                    if (targetSlot is null || (slot.SpecOutTimestamp.HasValue && targetSlot.SpecOutTimestamp.HasValue && slot.SpecOutTimestamp < targetSlot.SpecOutTimestamp))
                        targetSlot = slot;
                }
            }

            if (targetSlot is null)
            {
                if (!targetName.IsEmpty)
                    _chat.SendMessage(player, $"No eligible specced-out slot found for '{targetName.ToString()}'.");
                else
                    _chat.SendMessage(player, "No teammates are eligible for substitution.");
                return;
            }

            if (targetSlot.SubRequested)
            {
                _chat.SendMessage(player, $"{targetSlot.PlayerName}'s slot is already flagged for substitution.");
                return;
            }

            targetSlot.SubRequested = true;

            // Announce to match participants (so teammates + opponents see the request)
            // and free spectators (so they can respond with ?sub).
            // Players in OTHER concurrent matches are excluded.
            string message = $"Freq {callerFreq} ({player.Name}'s team) needs a sub for {targetSlot.PlayerName}! Type ?sub {callerFreq} to join.";
            SendToMatchParticipants(match, message);
            SendToAvailablePlayers(arena, arenaData, message);
        }

        private static bool IsSlotSubEligible(CaptainsPlayerSlot slot, ArenaConfig config)
        {
            if (slot.Lives <= 0)
                return false;

            if (slot.WasSubbedIn)
                return false;

            if (slot.SubRequested)
                return true;

            if (slot.LagOuts > config.MaxLagOuts)
                return true;

            if (slot.SpecOutTimestamp.HasValue && DateTime.UtcNow - slot.SpecOutTimestamp.Value >= config.SubAvailableDelay)
                return true;

            return false;
        }

        private void ExecuteSub(Arena arena, ArenaData arenaData, ActiveMatch match, CaptainsPlayerSlot slot, Player oldPlayer, Player sub, ShipType subShip)
        {
            string oldName = slot.PlayerName ?? "unknown";
            short freq = slot.Team.Freq;

            // Cancel any pending sub-available announcement timer.
            CancelSubAvailableTimer(slot);

            // Remove old player from match tracking.
            match.SpecOutSlots.Remove(oldPlayer);
            arenaData.PlayerToMatch.Remove(oldPlayer);
            if (oldPlayer.TryGetExtraData(_pdKey, out PlayerData? oldPd))
                oldPd.ManagedArena = null;

            // If old player is still online and was set as Playing, unset them.
            if (_matchmakingQueues is not null)
            {
                if (slot.LeftArena)
                    _matchmakingQueues.UnsetPlayingByName(oldName, false);
                else
                    _matchmakingQueues.UnsetPlaying(oldPlayer, false);
            }

            // Update the slot for the sub.
            slot.OriginalPlayerName = oldName;
            slot.PlayerName = sub.Name;
            slot.Player = sub;
            slot.LagOuts = 0;
            slot.SpecOutTimestamp = null;
            slot.SubRequested = false;
            slot.WasSubbedIn = true;
            // Reset per-slot kill/death counters so the sub starts fresh. Prevents the sub from
            // inheriting the outgoing player's kill/death totals on the slot, which would
            // otherwise bleed into any IMemberStats reader that consults the live slot.
            // MatchLvz separately refreshes its own statbox state via TeamVersusMatchPlayerSubbedCallback.
            slot.Kills = 0;
            slot.Deaths = 0;

            // Add sub to match tracking.
            match.ActiveSlots[sub] = slot;
            arenaData.PlayerToMatch[sub] = match;

            // Increment the team's sub counter.
            if (freq == match.Freq1)
                match.Freq1SubsUsed++;
            else
                match.Freq2SubsUsed++;

            // Set sub as playing in the matchmaking system.
            _matchmakingQueues?.SetPlayingAsSub(sub);

            // Fire the sub callback BEFORE the ship change so MatchLvz can refresh the statbox
            // with the new player's name (the slot's PlayerName is already updated above).
            TeamVersusMatchPlayerSubbedCallback.Fire(arena, slot, oldName);

            // Put the sub in the game with the requested ship.
            slot.Ship = subShip;
            _game.SetShipAndFreq(sub, subShip, freq);
            RemoveAllItems(sub, sub.Ship, arenaData.Config.ShipSettings);
            EnsureExtraPositionDataWatch(sub);

            if (sub.TryGetExtraData(_pdKey, out PlayerData? subPd))
                subPd.ManagedArena = arena;

            // Fire callback so MatchFocus routes position packets correctly.
            MatchAddPlayingCallback.Fire(_broker!, match.MatchData, sub.Name!, sub);

            // Cancel abandon timer since the team now has a player.
            CancelAbandonTimer(match, freq);

            _chat.SendArenaMessage(arena, $"{sub.Name} has subbed in for {oldName} on Freq {freq}. ({slot.Lives} {(slot.Lives == 1 ? "life" : "lives")} remaining)");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[player name]",
            Description = "Display the current item counts for all players.")]
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
                    _chat.SendMessage(player, first ? $"Freq {team.Freq}: (all eliminated)" : sb.ToString());
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Description = "List all currently forming or formed teams in the arena, including their members.")]
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
                            string status = slot.Lives > 0 ? $"{slot.Lives} {(slot.Lives != 1 ? "lives" : "life")}" : "eliminated";
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
                    _chat.SendMessage(player, $"{f.Captain.Name}'s team{freqStr} - {state}: {FormatTeamRoster(f, arenaData.Config.PlayersPerTeam)}");
                }
            }

            if (!anyOutput)
                _chat.SendMessage(player, "No active teams or matches. Type ?captain to form a team!");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Description = "Display a summary of all available commands for this arena.")]
        private void Command_CapHelp(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            // Two-column ASCII box: left = 24-char command, right = 48-char description.
            static string Row(string cmd, string desc) => $"| {cmd,-24} | {desc,-48} |";
            static string Section(string name)
            {
                string left = $"-- {name} ".PadRight(26, '-');
                return $"+{left}+{new string('-', 50)}+";
            }
            const string Sep = "+--------------------------+--------------------------------------------------+";

            _chat.SendMessage(player, Section("Team Formation"));
            _chat.SendMessage(player, Row("?captain (?cap)", "Become a captain; others can join your team."));
            _chat.SendMessage(player, Row("?join [<captain>]", "Join a captain's team. Or PM captain ?join."));
            _chat.SendMessage(player, Row("", "Or ?join for auto-fill."));
            _chat.SendMessage(player, Row("?remove <player>", "[Cap] Remove a player from your team."));
            _chat.SendMessage(player, Row("?disband", "[Cap] Disband your entire team."));
            _chat.SendMessage(player, Row("?ditch", "Leave your current team."));
            _chat.SendMessage(player, Section("Challenge"));
            _chat.SendMessage(player, Row("?challenge (?chal) <cap>", "Challenge another full team to a match."));
            _chat.SendMessage(player, Row("?accept", "Accept a pending challenge."));
            _chat.SendMessage(player, Row("?refuse [captain]", "Refuse a pending challenge."));
            _chat.SendMessage(player, Section("Pre-Game"));
            _chat.SendMessage(player, Row("?ready (?rdy)", "[Cap] Mark ready. Both captains must ?ready."));
            _chat.SendMessage(player, Row("?cancel", "[Cap] Unready or abort a running countdown."));
            _chat.SendMessage(player, Section("In-Game"));
            _chat.SendMessage(player, Row("?return", "Re-enter the match if you have lives left."));
            _chat.SendMessage(player, Row("?sub [freq] [ship 1-8]", "Sub into a slot. Freq required if multiple"));
            _chat.SendMessage(player, Row("?requestsub (?rsub) [<player>]", "Flag a teammate's slot for immediate sub."));
            _chat.SendMessage(player, Row("?end", "[Cap] Forfeit the match for your team."));
            _chat.SendMessage(player, Row("?chart", "Show current match stats."));
            _chat.SendMessage(player, Row("?sc <1-8>", "Queue a ship change for your next spawn."));
            _chat.SendMessage(player, Row("?items [player]", "Show item counts for yourself or another."));
            _chat.SendMessage(player, Section("General Use"));
            _chat.SendMessage(player, Row("?capinfo", "See all forming or formed teams in the arena."));
            _chat.SendMessage(player, Row("?statbox", "Set statbox preference (detailed/simple/off)."));
            _chat.SendMessage(player, Row("?caphelp", "Show this command list."));
            _chat.SendMessage(player, Sep);
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
                SendToMatchAudience(arena, countdown.ArenaData, countdown.ActiveMatch, $"-{countdown.Seconds}-");
                return true;
            }

            SendToMatchAudience(arena, countdown.ArenaData, countdown.ActiveMatch, "GO!", ChatSound.Ding);
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

        private bool Timer_AbandonCheck(AbandonState state)
        {
            Arena? arena = state.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            if (!arenaData.ActiveMatches.Contains(state.Match))
                return false;

            // Verify the team still has no active players before forfeiting.
            if (state.Match.ActiveSlots.Values.Any(s => s.Team.Freq == state.Freq))
                return false;

            _chat.SendArenaMessage(arena, $"Freq {state.Freq} has no active players — forfeited!");
            EndMatch(arena, arenaData, state.Match, state.Freq);
            return false;
        }

        private bool Timer_SubAvailable(SubAvailableState state)
        {
            if (!_arenaDataDictionary.TryGetValue(state.Arena, out ArenaData? arenaData))
                return false;

            if (!arenaData.ActiveMatches.Contains(state.Match))
                return false;

            if (!state.Match.SpecOutSlots.Values.Contains(state.Slot))
                return false;

            if (state.Slot.Lives <= 0 || state.Slot.WasSubbedIn || state.Slot.SubRequested)
                return false;

            state.Slot.SubAvailableTimerKey = null;
            SendToAvailablePlayers(state.Arena, arenaData, $"Freq {state.Slot.Team.Freq} needs a sub for {state.Slot.PlayerName}! Type ?sub {state.Slot.Team.Freq} to join.");
            return false;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Removes the Playing state from all players in a match (both active and specced out)
        /// so they can queue for matchmaking again.
        /// </summary>
        private void UnsetPlayingForMatch(ActiveMatch match)
        {
            if (_matchmakingQueues is null)
                return;

            foreach (var (p, slot) in match.ActiveSlots)
                _matchmakingQueues.UnsetPlaying(p, false);

            foreach (var (p, slot) in match.SpecOutSlots)
            {
                if (slot.LeftArena)
                    _matchmakingQueues.UnsetPlayingByName(slot.PlayerName!, false);
                else
                    _matchmakingQueues.UnsetPlaying(p, false);
            }
        }

        /// <summary>
        /// Starts the abandon timer for <paramref name="freq"/> if it has no active players.
        /// Only fires when the match is active and <see cref="ArenaConfig.AbandonTimeoutMs"/> is non-zero.
        /// Safe to call unconditionally after any <see cref="ActiveMatch.ActiveSlots"/> removal.
        /// </summary>
        private void CheckAbandonCondition(ArenaData arenaData, ActiveMatch match, short freq)
        {
            if (arenaData.Config.AbandonTimeoutMs <= 0) return;
            if (!arenaData.ActiveMatches.Contains(match)) return;
            if (match.ActiveSlots.Values.Any(s => s.Team.Freq == freq)) return;

            AbandonState? state = match.GetAbandonState(freq);
            if (state is null) return;

            // Reset the timer (in case it was already running from a prior spec-out).
            _mainloopTimer.ClearTimer<AbandonState>(Timer_AbandonCheck, state);
            _mainloopTimer.SetTimer<AbandonState>(Timer_AbandonCheck, arenaData.Config.AbandonTimeoutMs, Timeout.Infinite, state, state);

            short otherFreq = freq == match.Freq1 ? match.Freq2 : match.Freq1;
            bool otherAlsoVacated = !match.ActiveSlots.Values.Any(s => s.Team.Freq == otherFreq);
            int vacatedCount = otherAlsoVacated ? 2 : 1;
            int abandonSeconds = arenaData.Config.AbandonTimeoutMs / 1000;
            string teamsStr = vacatedCount == 1 ? "1 team requires" : "2 teams require";
            SendToMatchParticipants(match, $"{teamsStr} active players. Game will auto-end in {abandonSeconds} seconds...");
        }

        private void RemoveAllItems(Player player, ShipType ship, ShipSettings[] shipSettings)
        {
            if ((int)ship >= shipSettings.Length)
                return;

            ref ShipSettings s = ref shipSettings[(int)ship];
            AdjustItem(player, Prize.Burst, s.InitialBurst);
            AdjustItem(player, Prize.Repel, s.InitialRepel);
            AdjustItem(player, Prize.Thor, s.InitialThor);
            AdjustItem(player, Prize.Brick, s.InitialBrick);
            AdjustItem(player, Prize.Decoy, s.InitialDecoy);
            AdjustItem(player, Prize.Rocket, s.InitialRocket);
            AdjustItem(player, Prize.Portal, s.InitialPortal);

            void AdjustItem(Player player, Prize prize, byte initial)
            {
                short adjustAmount = (short)(0 - initial);
                if (adjustAmount >= 0)
                    return;
                _game.GivePrize(player, (Prize)(-(short)prize), adjustAmount);
            }
        }

        private void CancelAbandonTimer(ActiveMatch match, short freq)
        {
            AbandonState? state = match.GetAbandonState(freq);
            if (state is not null)
                _mainloopTimer.ClearTimer<AbandonState>(Timer_AbandonCheck, state);
        }

        private void CancelSubAvailableTimer(CaptainsPlayerSlot slot)
        {
            if (slot.SubAvailableTimerKey is not null)
            {
                _mainloopTimer.ClearTimer<SubAvailableState>(Timer_SubAvailable, slot.SubAvailableTimerKey);
                slot.SubAvailableTimerKey = null;
            }
        }

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
                bool wasReady = memberFormation.IsReady;
                memberFormation.Members.Remove(player);
                memberFormation.IsReady = false;

                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;

                // If a countdown was running, abort it — all players must be present at GO.
                bool countdownAborted = false;
                if (memberFormation.PairedWith is not null)
                {
                    MatchCountdown? countdown = arenaData.PendingCountdowns.Find(
                        c => c.Formation1 == memberFormation || c.Formation2 == memberFormation);
                    if (countdown is not null)
                    {
                        AbortCountdown(arena, arenaData, countdown,
                            $"Countdown aborted: {player.Name} left the arena.");
                        countdownAborted = true;
                    }
                }

                // If the team was ready and no countdown abort already covered it, notify the partner.
                if (wasReady && !countdownAborted && memberFormation.PairedWith is not null)
                    SendToFormationPair(memberFormation, memberFormation.PairedWith,
                        $"{player.Name} left the arena — {memberFormation.Captain.Name}'s team is no longer ready.");

                if (memberFormation.Captain is not null)
                    _chat.SendMessage(memberFormation.Captain, $"{player.Name} left the arena. Team: {FormatTeamRoster(memberFormation, arenaData.Config.PlayersPerTeam)}");
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
                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                {
                    pd.ManagedArena = null;
                    RemoveExtraPositionDataWatch(player, pd);
                }

                if (arenaData.ActiveMatches.Contains(match))
                {
                    // Treat leave/disconnect like a spec-out so the player can return if they
                    // reconnect (e.g. after a temporary internet issue). The slot is preserved in
                    // SpecOutSlots; the Player key becomes stale but HandlePlayerEnter re-keys it.
                    bool wasActive = match.ActiveSlots.Remove(player);
                    if (wasActive)
                    {
                        match.SpecOutSlots[player] = playerSlot;
                        playerSlot.Player = null;
                        playerSlot.LagOuts++;
                        playerSlot.LeftArena = true;

                        TimeSpan elapsed = match.MatchData.Started is { } s ? DateTime.UtcNow - s : TimeSpan.Zero;
                        string elapsedStr = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
                        SendToMatchParticipants(match, $"{player.Name} has left the arena. (Count: {playerSlot.LagOuts}) [{elapsedStr}]");
                    }
                    else
                    {
                        // Already in SpecOutSlots (specced out before leaving); just mark as absent.
                        playerSlot.LeftArena = true;
                    }

                    bool allOut = !match.ActiveSlots.Values.Any(s => s.Team.Freq == playerSlot.Team.Freq);
                    if (allOut)
                    {
                        ScheduleWinConditionCheck(arenaData, match);
                        CheckAbandonCondition(arenaData, match, playerSlot.Team.Freq);
                    }
                }
                else
                {
                    // Countdown (not yet active): remove fully so the countdown can be aborted or
                    // the match can start cleanly without a missing player.
                    match.ActiveSlots.Remove(player);
                    match.SpecOutSlots.Remove(player);
                    arenaData.PlayerToMatch.Remove(player);
                    playerSlot.Player = null;
                    playerSlot.Lives = 0;
                }
            }
        }

        /// <summary>
        /// Called when a player enters the arena. Re-associates the player with any slot they
        /// held in an active match before disconnecting, so they can rejoin seamlessly.
        /// </summary>
        private void HandlePlayerEnter(Arena arena, ArenaData arenaData, Player player)
        {
            string playerName = player.Name!;

            foreach (ActiveMatch match in arenaData.ActiveMatches)
            {
                // Search for a stale SpecOutSlots entry left by a prior disconnect/leave.
                Player? staleKey = null;
                CaptainsPlayerSlot? slot = null;

                foreach ((Player p, CaptainsPlayerSlot s) in match.SpecOutSlots)
                {
                    if (p != player && string.Equals(p.Name, playerName, StringComparison.OrdinalIgnoreCase))
                    {
                        staleKey = p;
                        slot = s;
                        break;
                    }
                }

                if (staleKey is null)
                    continue;

                // Re-key the dictionaries with the new Player object.
                match.SpecOutSlots.Remove(staleKey);
                match.SpecOutSlots[player] = slot!;
                arenaData.PlayerToMatch.Remove(staleKey);
                arenaData.PlayerToMatch[player] = match;

                slot!.LeftArena = false;

                if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = arena;

                break;
            }
        }

        /// <summary>
        /// Sends a message to all players in the arena who are currently in spectator mode.
        /// Used for formation-phase announcements that are irrelevant to players in active matches.
        /// </summary>
        /// <summary>
        /// Sends a message to all players in the arena who are not already committed to a match
        /// (i.e., not in <see cref="ArenaData.PlayerToMatch"/>, which covers both active matches and pending countdowns).
        /// Formation members who haven't both readied up yet are included.
        /// </summary>
        private void SendToAvailablePlayers(Arena arena, ArenaData arenaData, string message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                _playerData.Lock();
                try
                {
                    foreach (Player p in _playerData.Players)
                        if (p.Arena == arena && p.Status == PlayerState.Playing && !arenaData.PlayerToMatch.ContainsKey(p))
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
        /// Sends a message to all players currently tracked in an active match (both active and specced-out slots).
        /// </summary>
        private void SendToMatchParticipants(ActiveMatch match, string message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                foreach (Player p in match.ActiveSlots.Keys)
                    set.Add(p);
                foreach (Player p in match.SpecOutSlots.Keys)
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
        /// Sends a message to all players in a match's audience: the match participants (active + specced-out)
        /// plus any arena spectators not currently participating in a different match.
        /// </summary>
        private void SendToMatchAudience(Arena arena, ArenaData arenaData, ActiveMatch match, string message, ChatSound sound = ChatSound.None)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                foreach (Player p in match.ActiveSlots.Keys)
                    set.Add(p);
                foreach (Player p in match.SpecOutSlots.Keys)
                    set.Add(p);

                // Include spectators: players in the arena not assigned to any match.
                _playerData.Lock();
                try
                {
                    foreach (Player p in _playerData.Players)
                        if (p.Arena == arena && p.Status == PlayerState.Playing && !arenaData.PlayerToMatch.ContainsKey(p))
                            set.Add(p);
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (set.Count > 0)
                {
                    if (sound != ChatSound.None)
                        _chat.SendSetMessage(set, sound, message);
                    else
                        _chat.SendSetMessage(set, message);
                }
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
            // Warp all players to their start positions immediately when both teams are ready.
            if (arenaData.Config.StartLocations.Count > 0)
            {
                HashSet<short>? warnedFreqs = null;
                foreach (var (p, slot) in countdown.ActiveMatch.ActiveSlots)
                {
                    if (arenaData.Config.StartLocations.TryGetValue(slot.Team.Freq, out (short X, short Y) loc))
                        _game.WarpTo(p, loc.X, loc.Y);
                    else if (warnedFreqs is null || warnedFreqs.Add(slot.Team.Freq))
                        _logManager.LogA(LogLevel.Warn, nameof(CaptainsMatch), arena, $"No start location configured for Freq {slot.Team.Freq}; players will not be warped.");
                }
            }

            string timeMsg = arenaData.Config.TimeLimit is { } tl
                ? $" Time limit: {(int)tl.TotalMinutes}m."
                : string.Empty;
            int lives = arenaData.Config.LivesPerPlayer;
            SendToMatchAudience(arena, arenaData, countdown.ActiveMatch,
                $"Match starting in 15 seconds! Freq {countdown.ActiveMatch.Freq1} vs Freq {countdown.ActiveMatch.Freq2}. " +
                $"Each player has {lives} {(lives != 1 ? "lives" : "life")}.{timeMsg}");
            // 12 seconds before the 3-second countdown begins = 15 seconds total to GO!
            _mainloopTimer.SetTimer(Timer_PreCountdown, 12000, Timeout.Infinite, countdown, countdown);
        }

        private bool Timer_PreCountdown(MatchCountdown countdown)
        {
            Arena? arena = countdown.Arena;
            if (arena is null)
                return false;

            countdown.Seconds = 3;
            SendToMatchAudience(arena, countdown.ArenaData, countdown.ActiveMatch, "-3-");
            _mainloopTimer.SetTimer(Timer_Countdown, 1000, 1000, countdown, countdown);
            return false;
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
            int pairIdx = arenaData.Config.FreqPairs.FindIndex(p => p.F1 == freq1 || p.F2 == freq1);
            int generation = arenaData.MatchGeneration++;
            var matchData = new CaptainsMatchData(arena, config, (short)(pairIdx >= 0 ? pairIdx : 0), generation);

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

            // Mark all participants as Playing to block ?next queueing during countdown and match.
            if (_matchmakingQueues is not null)
            {
                foreach (Player p in activeMatch.ActiveSlots.Keys)
                    _matchmakingQueues.SetPlaying(p);
            }

            activeMatch.Freq1AbandonState = new AbandonState { Arena = arena, ArenaData = arenaData, Match = activeMatch, Freq = freq1 };
            activeMatch.Freq2AbandonState = new AbandonState { Arena = arena, ArenaData = arenaData, Match = activeMatch, Freq = freq2 };

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

            // Re-warp all players at GO! in case the pre-countdown warp was missed (e.g. timing/client issue).
            if (arenaData.Config.StartLocations.Count > 0)
            {
                HashSet<short>? warnedFreqs = null;
                foreach (var (p, slot) in match.ActiveSlots)
                {
                    if (arenaData.Config.StartLocations.TryGetValue(slot.Team.Freq, out (short X, short Y) loc))
                        _game.WarpTo(p, loc.X, loc.Y);
                    else if (warnedFreqs is null || warnedFreqs.Add(slot.Team.Freq))
                        _logManager.LogA(LogLevel.Warn, nameof(CaptainsMatch), arena, $"No start location configured for Freq {slot.Team.Freq}; players will not be warped.");
                }
            }

            if (arenaData.Config.TimeLimit is { } tl)
                _mainloopTimer.SetTimer<ActiveMatch>(Timer_MatchTimeExpired, (int)tl.TotalMilliseconds, Timeout.Infinite, match, match);
        }

        private void EndMatch(Arena arena, ArenaData arenaData, ActiveMatch match, short losingFreq)
        {
            short winningFreq = match.Freq1 == losingFreq ? match.Freq2 : match.Freq1;

            _chat.SendArenaMessage(arena, $"Freq {winningFreq} wins! Freq {losingFreq} has been eliminated.");

            ITeam? winnerTeam = match.MatchData.Teams.FirstOrDefault(t => t.Freq == winningFreq);
            TeamVersusMatchEndedCallback.Fire(arena, match.MatchData, MatchEndReason.Decided, winnerTeam);

            if (_tvStatsBehavior is not null)
                _ = _tvStatsBehavior.MatchEndedAsync(match.MatchData, MatchEndReason.Decided, winnerTeam);

            MatchEndedCallback.Fire(_broker!, match.MatchData);

            // Remove Playing state so players can queue again after the match.
            UnsetPlayingForMatch(match);

            // Cancel any pending sub-available announcement timers before match teardown.
            foreach (CaptainsPlayerSlot slot in match.SpecOutSlots.Values)
                CancelSubAvailableTimer(slot);

            // Remove from ActiveMatches before speccing players so that Callback_ShipFreqChange
            // does not fire spurious "has specced" announcements or re-schedule win-condition checks.
            arenaData.ActiveMatches.Remove(match);

            // Spec out all players from both teams and clear match tracking.
            foreach (var (p, _) in match.ActiveSlots)
            {
                if (p.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
                else if (p.Freq != arena.SpecFreq)
                    _game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
                if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                {
                    pd.ManagedArena = null;
                    RemoveExtraPositionDataWatch(p, pd);
                }
                arenaData.PlayerToMatch.Remove(p);
            }
            foreach (var (p, _) in match.SpecOutSlots)
            {
                if (p.Freq != arena.SpecFreq)
                    _game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
                if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;
                arenaData.PlayerToMatch.Remove(p);
            }

            // Rebuild the winning team as a Formation so they can receive a new challenge.
            if (winnerTeam is CaptainsTeam winnerCaptainsTeam)
            {
                Player? winnerCaptain = winnerCaptainsTeam.OriginalCaptain;
                var winners = new HashSet<Player>();

                foreach (var (p, slot) in match.ActiveSlots)
                    if (slot.Team.Freq == winningFreq)
                        winners.Add(p);

                // Include players eliminated during the match or currently specced out — they're
                // still winners. Exclude players who left the arena and never reconnected.
                foreach (var (p, slot) in match.SpecOutSlots)
                    if (slot.Team.Freq == winningFreq && !slot.LeftArena)
                        winners.Add(p);

                if (winners.Count < arenaData.Config.PlayersPerTeam)
                {
                    // A player left mid-match and did not return — bail on reformation.
                    _chat.SendArenaMessage(arena, $"Freq {winningFreq}'s team cannot reform (a player left the game).");
                }
                else
                {
                    if (winnerCaptain is null || !winners.Contains(winnerCaptain))
                        winnerCaptain = winners.FirstOrDefault();

                    if (winnerCaptain is not null)
                    {
                        var winnerFormation = new Formation
                        {
                            Captain = winnerCaptain,
                            AssignedFreq = winningFreq,
                            CreatedAt = match.MatchData.Started ?? DateTime.UtcNow,
                        };
                        foreach (Player p in winners)
                        {
                            winnerFormation.Members.Add(p);
                            if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                                pd.ManagedArena = arena;
                        }

                        arenaData.Formations[winnerCaptain] = winnerFormation;
                        _chat.SendArenaMessage(arena, $"Freq {winningFreq} ({winnerCaptain.Name}'s team) wins! Challenge them: ?challenge {winnerCaptain.Name}");
                    }
                }
            }

            // Rebuild the losing team as a Formation so they can re-challenge.
            ITeam? loserTeam = match.MatchData.Teams.FirstOrDefault(t => t.Freq == losingFreq);
            if (loserTeam is CaptainsTeam loserCaptainsTeam)
            {
                Player? loserCaptain = loserCaptainsTeam.OriginalCaptain;
                var loserPlayers = new HashSet<Player>();

                foreach (var (p, slot) in match.ActiveSlots)
                    if (slot.Team.Freq == losingFreq)
                        loserPlayers.Add(p);
                foreach (var (p, slot) in match.SpecOutSlots)
                    if (slot.Team.Freq == losingFreq && !slot.LeftArena)
                        loserPlayers.Add(p);

                if (loserPlayers.Count < arenaData.Config.PlayersPerTeam)
                {
                    // A player left mid-match and did not return — bail on reformation.
                    _chat.SendArenaMessage(arena, $"Freq {losingFreq}'s team cannot reform (a player left the game).");
                }
                else
                {
                    if (loserCaptain is null || !loserPlayers.Contains(loserCaptain))
                        loserCaptain = loserPlayers.FirstOrDefault();

                    if (loserCaptain is not null)
                    {
                        var loserFormation = new Formation
                        {
                            Captain = loserCaptain,
                            CreatedAt = match.MatchData.Started ?? DateTime.UtcNow,
                        };
                        foreach (Player p in loserPlayers)
                        {
                            loserFormation.Members.Add(p);
                            if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                                pd.ManagedArena = arena;
                        }

                        arenaData.Formations[loserCaptain] = loserFormation;
                        _chat.SendArenaMessage(arena, $"Freq {losingFreq} ({loserCaptain.Name}'s team) is regrouping. Challenge them: ?challenge {loserCaptain.Name}");
                    }
                }
            }

            arenaData.KickedPlayers.Clear();
            _mainloopTimer.ClearTimer<ActiveMatch>(Timer_MatchTimeExpired, match);
            _mainloopTimer.ClearTimer<ActiveMatch>(Timer_WinConditionCheck, match);
            CancelAbandonTimer(match, match.Freq1);
            CancelAbandonTimer(match, match.Freq2);
        }

        /// <summary>
        /// Ends the match as a draw (time expired with equal alive counts). Specs all players; neither team reforms.
        /// </summary>
        private void EndMatchDraw(Arena arena, ArenaData arenaData, ActiveMatch match)
        {

            TeamVersusMatchEndedCallback.Fire(arena, match.MatchData, MatchEndReason.Draw, null);

            if (_tvStatsBehavior is not null)
                _ = _tvStatsBehavior.MatchEndedAsync(match.MatchData, MatchEndReason.Draw, null);

            MatchEndedCallback.Fire(_broker!, match.MatchData);

            // Remove Playing state so players can queue again after the match.
            UnsetPlayingForMatch(match);

            // Cancel any pending sub-available announcement timers before match teardown.
            foreach (CaptainsPlayerSlot slot in match.SpecOutSlots.Values)
                CancelSubAvailableTimer(slot);

            // Remove from ActiveMatches before speccing players so that Callback_ShipFreqChange
            // does not fire spurious "has specced" announcements or re-schedule win-condition checks.
            arenaData.ActiveMatches.Remove(match);

            foreach (var (p, _) in match.ActiveSlots)
            {
                if (p.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
                else if (p.Freq != arena.SpecFreq)
                    _game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
                RemoveExtraPositionDataWatch(p);
                if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;
                arenaData.PlayerToMatch.Remove(p);
            }
            foreach (var (p, _) in match.SpecOutSlots)
            {
                if (p.Freq != arena.SpecFreq)
                    _game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
                if (p.TryGetExtraData(_pdKey, out PlayerData? pd))
                    pd.ManagedArena = null;
                arenaData.PlayerToMatch.Remove(p);
            }
            arenaData.KickedPlayers.Clear();
            _mainloopTimer.ClearTimer<ActiveMatch>(Timer_MatchTimeExpired, match);
            _mainloopTimer.ClearTimer<ActiveMatch>(Timer_WinConditionCheck, match);
            CancelAbandonTimer(match, match.Freq1);
            CancelAbandonTimer(match, match.Freq2);
        }

        /// <summary>
        /// Aborts a pending countdown: cancels timers, removes players from PlayerToMatch,
        /// and resets both formations to un-ready (but keeps them paired and on their freqs).
        /// </summary>
        private void AbortCountdown(Arena arena, ArenaData arenaData, MatchCountdown countdown, string reason)
        {
            _mainloopTimer.ClearTimer<MatchCountdown>(Timer_PreCountdown, countdown);
            _mainloopTimer.ClearTimer<MatchCountdown>(Timer_Countdown, countdown);
            arenaData.PendingCountdowns.Remove(countdown);

            foreach (CaptainsPlayerSlot slot in countdown.ActiveMatch.SpecOutSlots.Values)
                CancelSubAvailableTimer(slot);

            foreach (Player p in countdown.ActiveMatch.ActiveSlots.Keys)
                arenaData.PlayerToMatch.Remove(p);

            foreach (Player p in countdown.ActiveMatch.SpecOutSlots.Keys)
                arenaData.PlayerToMatch.Remove(p);

            // Remove Playing state so players can queue again.
            UnsetPlayingForMatch(countdown.ActiveMatch);

            countdown.Formation1.IsReady = false;
            countdown.Formation1.PairedWith = null;
            countdown.Formation1.AssignedFreq = null;
            countdown.Formation2.IsReady = false;
            countdown.Formation2.PairedWith = null;
            countdown.Formation2.AssignedFreq = null;

            // Spec out all members of both formations to the spectator freq — the pairing is
            // broken and freq reservations are released, so players should not remain on match freqs.
            foreach (Player p in countdown.Formation1.Members)
                if (p.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);

            foreach (Player p in countdown.Formation2.Members)
                if (p.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);

            SendToMatchAudience(arena, arenaData, countdown.ActiveMatch, reason);
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
                    _mainloopTimer.ClearTimer<MatchCountdown>(Timer_PreCountdown, countdown);
                    _mainloopTimer.ClearTimer<MatchCountdown>(Timer_Countdown, countdown);
                    arenaData.PendingCountdowns.Remove(countdown);

                    foreach (Player p in countdown.ActiveMatch.ActiveSlots.Keys)
                        arenaData.PlayerToMatch.Remove(p);
                }
            }

            // Spec out all members to the spectator freq so they don't remain on match freqs.
            foreach (Player p in formation.Members)
            {
                if (p.Ship != ShipType.Spec)
                    _game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
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
                        _game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
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
        /// Returns the freq pair to use for a new challenge/accept. If one or both formations already
        /// hold an assigned freq (winning team on field), returns the pair containing that freq.
        /// When both formations have freqs from different pairs, the acceptor's pair takes priority
        /// (the challenger comes to the acceptor). Otherwise returns the first pair not currently
        /// in use by any formation, countdown, or active match.
        /// </summary>
        private static (short F1, short F2)? GetPairForChallenge(ArenaData arenaData, Formation formationA, Formation formationB)
        {
            // If both formations have assigned freqs, prefer the acceptor's (formationB) pair.
            // The challenger gives up their old freq and moves to the acceptor's pair.
            // If only one has an assigned freq, use that formation's pair.
            short? existingFreq = formationB.AssignedFreq ?? formationA.AssignedFreq;
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
                          || arenaData.PendingCountdowns.Any(c => c.ActiveMatch.Freq1 == pair.F1 || c.ActiveMatch.Freq1 == pair.F2 || c.ActiveMatch.Freq2 == pair.F1 || c.ActiveMatch.Freq2 == pair.F2);
                if (!inUse)
                    return pair;
            }

            return null;
        }

        private static void AssignFreqs(ArenaData arenaData, Formation challenger, Formation acceptor, (short F1, short F2) pair)
        {
            bool IsInPair(short freq) => freq == pair.F1 || freq == pair.F2;
            short OtherFreq(short freq) => freq == pair.F1 ? pair.F2 : pair.F1;

            if (acceptor.AssignedFreq.HasValue && IsInPair(acceptor.AssignedFreq.Value))
            {
                // Acceptor stays on their freq within the pair; challenger gets the other.
                challenger.AssignedFreq = OtherFreq(acceptor.AssignedFreq.Value);
            }
            else if (challenger.AssignedFreq.HasValue && IsInPair(challenger.AssignedFreq.Value))
            {
                // Challenger stays on their freq within the pair; acceptor gets the other.
                acceptor.AssignedFreq = OtherFreq(challenger.AssignedFreq.Value);
            }
            else
            {
                // Neither has a freq in this pair (or neither has an assigned freq at all).
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

        /// <summary>
        /// Returns a unified team roster string: captain first, then other members, then count.
        /// E.g., "Alice, Bob (2/4)"
        /// </summary>
        private static string FormatTeamRoster(Formation formation, int playersPerTeam)
        {
            var names = new List<string>(formation.Members.Count);
            names.Add(formation.Captain.Name!);
            foreach (Player m in formation.Members)
                if (m != formation.Captain)
                    names.Add(m.Name!);
            return $"{string.Join(", ", names)} ({formation.Members.Count}/{playersPerTeam})";
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

        #endregion

        #region Data

        private readonly struct ShipSettings
        {
            public byte InitialBurst { get; init; }
            public byte InitialRepel { get; init; }
            public byte InitialThor { get; init; }
            public byte InitialBrick { get; init; }
            public byte InitialDecoy { get; init; }
            public byte InitialRocket { get; init; }
            public byte InitialPortal { get; init; }
        }

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
            public required TimeSpan AllowShipChangeAfterDeathDuration;
            public required int AbandonTimeoutMs;
            public required TimeSpan SubAvailableDelay;
            public required int MaxSubsPerTeam;
            /// <summary>Initial item counts per ship, indexed by <see cref="ShipType"/> cast to int. Used to burn items on spawn.</summary>
            public required ShipSettings[] ShipSettings;
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

            /// <summary>When this formation was created. Used to break ties in auto-join.</summary>
            public DateTime CreatedAt;

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

            /// <summary>Number of substitutions used per team. Checked against <see cref="ArenaConfig.MaxSubsPerTeam"/>.</summary>
            public int Freq1SubsUsed;
            public int Freq2SubsUsed;

            /// <summary>Timer state/key for each team's abandon timeout. Initialized in <see cref="BuildMatchCountdown"/>.</summary>
            public AbandonState Freq1AbandonState = null!;
            public AbandonState Freq2AbandonState = null!;

            public AbandonState? GetAbandonState(short freq) =>
                freq == Freq1 ? Freq1AbandonState : freq == Freq2 ? Freq2AbandonState : null;
        }

        /// <summary>
        /// Timer state/key for the per-team abandon timeout.
        /// One instance is allocated per team per match and reused for the life of the match.
        /// </summary>
        private sealed class AbandonState
        {
            public Arena Arena = null!;
            public ArenaData ArenaData = null!;
            public ActiveMatch Match = null!;
            public short Freq;
        }

        private sealed class SubAvailableState
        {
            public Arena Arena = null!;
            public ArenaData ArenaData = null!;
            public ActiveMatch Match = null!;
            public CaptainsPlayerSlot Slot = null!;
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

            public AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor>? FreqManagerEnforcerAdvisorToken;

            /// <summary>
            /// Monotonically increasing counter used to generate unique <see cref="MatchIdentifier"/>
            /// values across consecutive matches on the same freq pair. Prevents the LVZ statbox
            /// from displaying stale data due to client-side image caching when the identifier is reused.
            /// </summary>
            public int MatchGeneration;

            bool IResettable.TryReset()
            {
                Arena = null!;
                Config = null!;
                MatchGeneration = 0;
                Formations.Clear();
                ActiveMatches.Clear();
                PendingCountdowns.Clear();
                PlayerToMatch.Clear();
                KickedPlayers.Clear();
                FreqManagerEnforcerAdvisorToken = null;
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

        /// <summary>No play-area mechanics for captains matches; all regions are null.</summary>
        private sealed class CaptainsMatchBoxConfiguration : IMatchBoxConfiguration
        {
            public string? PlayAreaMapRegion => null;
        }

        private sealed class CaptainsMatchConfiguration : IMatchConfiguration
        {
            private readonly ArenaConfig _config;
            private readonly IMatchBoxConfiguration[] _boxes;

            public CaptainsMatchConfiguration(ArenaConfig config)
            {
                _config = config;
                // One box per freq pair so that BoxIdx (pair index) is always a valid index.
                _boxes = new IMatchBoxConfiguration[Math.Max(1, config.FreqPairs.Count)];
                for (int i = 0; i < _boxes.Length; i++)
                    _boxes[i] = new CaptainsMatchBoxConfiguration();
            }

            public long? GameTypeId => _config.GameTypeId;
            public int NumTeams => 2;
            public int PlayersPerTeam => _config.PlayersPerTeam;
            public int LivesPerPlayer => _config.LivesPerPlayer;
            public TimeSpan? TimeLimit => _config.TimeLimit;
            public TimeSpan? OverTimeLimit => _config.OverTimeLimit;
            public TimeSpan WinConditionDelay => _config.WinConditionDelay;
            public int TimeLimitWinBy => _config.TimeLimitWinBy;
            public int MaxLagOuts => _config.MaxLagOuts;
            public ReadOnlySpan<IMatchBoxConfiguration> Boxes => _boxes;
            public IOpenSkillModel OpenSkillModel => _config.OpenSkillModel;
            public double OpenSkillSigmaDecayPerDay => _config.OpenSkillSigmaDecayPerDay;
            public bool OpenSkillUseScoresWhenPossible => _config.OpenSkillUseScoresWhenPossible;
        }

        private sealed class CaptainsMatchData : IMatchData
        {
            private ReadOnlyCollection<ITeam>? _teams;

            public CaptainsMatchData(Arena arena, IMatchConfiguration configuration, short matchSlotId, int generation)
            {
                MatchIdentifier = new MatchIdentifier($"CaptainsMatch#{generation}", arena.Number, matchSlotId);
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
            public string? PlayerName { get; set; }
            public Player? Player { get; set; }
            public int? PremadeGroupId => null;
            public int LagOuts { get; set; }

            /// <summary>
            /// Tick at which this slot last received a kill. Used to discard duplicate death
            /// packets that arrive within <see cref="DoubleDeathIgnoreTicks"/>.
            /// </summary>
            public ServerTick? LastKilledTick { get; set; }

            /// <summary>
            /// Set when the player left the arena mid-match (disconnect or leave) and the slot
            /// is being held open for a reconnect. Cleared when the player re-enters the arena.
            /// Used to exclude absent players from post-match team reformation.
            /// </summary>
            public bool LeftArena { get; set; }

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

            /// <summary>
            /// When non-null, the player is within the post-death window in which they may voluntarily change ships.
            /// Cleared when the window expires, when the player fires a weapon, or at match end.
            /// </summary>
            public DateTime? AllowShipChangeExpiration;

            public DateTime? SpecOutTimestamp;
            public bool SubRequested;
            public bool WasSubbedIn;
            public string? OriginalPlayerName;
            public object? SubAvailableTimerKey;
        }

        #endregion
    }
}
