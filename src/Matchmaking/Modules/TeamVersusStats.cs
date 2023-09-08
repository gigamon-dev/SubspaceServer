using Microsoft.Extensions.ObjectPool;
using Microsoft.IO;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.Json;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that tracks stats for team versus matches.
    /// </summary>
    [ModuleInfo($"""
        Tracks stats for team versus matches.
        For use with the {nameof(TeamVersusMatch)} module.
        """)]
    public class TeamVersusStats : IModule, IArenaAttachableModule, ITeamVersusStatsBehavior
    {
        #region Static members

        private static readonly RecyclableMemoryStreamManager s_recyclableMemoryStreamManager = new();
        private static readonly ObjectPool<LinkedListNode<DamageInfo>> s_damageInfoLinkedListNodePool;
        private static readonly ObjectPool<TickRangeCalculator> s_tickRangeCalculatorPool;
        private static readonly ObjectPool<Dictionary<PlayerTeamSlot, int>> s_damageDictionaryPool;
        private static readonly ObjectPool<List<(string PlayerName, int Damage)>> s_damageListPool;
        //private readonly ObjectPool<MatchStats> _matchStatsObjectPool = new NonTransientObjectPool<MatchStats>(new MatchStatsPooledObjectPolicy());

        static TeamVersusStats()
        {
            var provider = new DefaultObjectPoolProvider();
            s_damageInfoLinkedListNodePool = provider.Create(new DamageInfoLinkedListNodePooledObjectPolicy());
            s_tickRangeCalculatorPool = provider.Create(new TickRangeCalcualtorPooledObjectPolicy());
            s_damageDictionaryPool = provider.Create(new DamageDictionaryPooledObjectPolicy());
            s_damageListPool = provider.Create(new DamageListPooledObjectPolicy());
        }

        #endregion

        private IArenaManager _arenaManager;
        private IChat _chat;
        private IClientSettings _clientSettings;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMapData _mapData;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IWatchDamage _watchDamage;

        // optional
        private IGameStatsRepository _gameStatsRepository;

        private InterfaceRegistrationToken<ITeamVersusStatsBehavior> _iTeamVersusStatsBehaviorToken;

        private PlayerDataKey<PlayerData> _pdKey;

        private string _zoneServerName;

        #region Client Setting Identifiers

        private ClientSettingIdentifier _bombDamageLevelClientSettingId;
        private ClientSettingIdentifier _eBombShutdownTimeClientSettingId;
        private ClientSettingIdentifier _eBombDamagePercentClientSettingId;
        private ShipClientSettingIdentifiers[] _shipClientSettingIds = new ShipClientSettingIdentifiers[8];

        #endregion

        /// <summary>
        /// Map (.lvl file) info by base arena name.
        /// </summary>
        /// <remarks>
        /// Key: base arena name
        /// </remarks>
        private readonly Trie<(string LvlFilename, uint Checksum)> _arenaLvlTrie = new(false);

        /// <summary>
        /// Arena settings by base arena name.
        /// </summary>
        /// <remarks>
        /// Key: base arena name
        /// </remarks>
        private readonly Trie<ArenaSettings> _arenaSettingsTrie = new(false);

        /// <summary>
        /// Dictionary of <see cref="MatchStats"/> by <see cref="MatchIdentifier"/> for matches that are currently in progress.
        /// </summary>
        private readonly Dictionary<MatchIdentifier, MatchStats> _matchStatsDictionary = new();

        /// <summary>
        /// Dictionary that links a player (by player name) to their <see cref="MemberStats"/> of the match currently in progress that they are assigned a slot in.
        /// When a player is subbed out, they will no longer have a record in this collection.
        /// </summary>
        /// <remarks>
        /// Key: player name
        /// </remarks>
        private readonly Dictionary<string, MemberStats> _playerMemberDictionary = new(StringComparer.OrdinalIgnoreCase);

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            IClientSettings clientSettings,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMapData mapData,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IWatchDamage watchDamage)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _watchDamage = watchDamage ?? throw new ArgumentNullException(nameof(watchDamage));

            // Get client setting identifiers.
            if (!_clientSettings.TryGetSettingsIdentifier("Bomb", "BombDamageLevel", out _bombDamageLevelClientSettingId))
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), $"Unable to get client setting identifier for Bomb:BombDamageLevel.");
                return false;
            }

            if (!_clientSettings.TryGetSettingsIdentifier("Bomb", "EBombShutdownTime", out _eBombShutdownTimeClientSettingId))
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), $"Unable to get client setting identifier for Bomb:EBombShutdownTime.");
                return false;
            }

            if (!_clientSettings.TryGetSettingsIdentifier("Bomb", "EBombDamagePercent", out _eBombDamagePercentClientSettingId))
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), $"Unable to get client setting identifier for Bomb:EBombDamagePercent.");
                return false;
            }

            string[] shipNames = Enum.GetNames<ShipType>();
            for (int shipIndex = 0; shipIndex < 8; shipIndex++)
            {
                if (!_clientSettings.TryGetSettingsIdentifier(shipNames[shipIndex], "MaximumRecharge", out _shipClientSettingIds[shipIndex].MaximumRechargeId))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), $"Unable to get client setting identifier for {shipNames[shipIndex]}:MaximumRecharge.");
                    return false;
                }

                if (!_clientSettings.TryGetSettingsIdentifier(shipNames[shipIndex], "MaximumEnergy", out _shipClientSettingIds[shipIndex].MaximumEnergyId))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), $"Unable to get client setting identifier for {shipNames[shipIndex]}:MaximumEnergy.");
                    return false;
                }

                if (!_clientSettings.TryGetSettingsIdentifier(shipNames[shipIndex], "EmpBomb", out _shipClientSettingIds[shipIndex].EmpBombId))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), $"Unable to get client setting identifier for {shipNames[shipIndex]}:EmpBomb.");
                    return false;
                }
            }

            // Try to get the optional service for saving stats to a database.
            _gameStatsRepository = broker.GetInterface<IGameStatsRepository>();

            if (_gameStatsRepository is not null)
            {
                // We got the optional service. To use it, we'll need the server name.
                _zoneServerName = _configManager.GetStr(_configManager.Global, "Billing", "ServerName");

                if (string.IsNullOrWhiteSpace(_zoneServerName))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), "Missing setting, global.conf: Billing.ServerName");

                    broker.ReleaseInterface(ref _gameStatsRepository);
                    return false;
                }
            }

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            // Registered globally, instead of on attached arenas only, since matches can end after an arena is destroyed.
            _iTeamVersusStatsBehaviorToken = broker.RegisterInterface<ITeamVersusStatsBehavior>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface(ref _iTeamVersusStatsBehaviorToken);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);

            _playerData.FreePlayerData(ref _pdKey);

            if (_gameStatsRepository is not null)
                broker.ReleaseInterface(ref _gameStatsRepository);

            return true;
        }

        public bool AttachModule(Arena arena)
        {
            TeamVersusMatchStartedCallback.Register(arena, Callback_TeamVersusMatchStarted);
            TeamVersusMatchPlayerSubbedCallback.Register(arena, Callback_TeamVersusMatchPlayerSubbed);
            BricksPlacedCallback.Register(arena, Callback_BricksPlaced);
            KillCallback.Register(arena, Callback_Kill);
            PlayerDamageCallback.Register(arena, Callback_PlayerDamage);
            PlayerPositionPacketCallback.Register(arena, Callback_PlayerPositionPacket);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            SpawnCallback.Register(arena, Callback_Spawn);

            _commandManager.AddCommand("chart", Command_chart, arena);

            return true;
        }

        public bool DetachModule(Arena arena)
        {
            _commandManager.RemoveCommand("chart", Command_chart, arena);

            TeamVersusMatchStartedCallback.Unregister(arena, Callback_TeamVersusMatchStarted);
            TeamVersusMatchPlayerSubbedCallback.Unregister(arena, Callback_TeamVersusMatchPlayerSubbed);
            BricksPlacedCallback.Register(arena, Callback_BricksPlaced);
            KillCallback.Unregister(arena, Callback_Kill);
            PlayerDamageCallback.Unregister(arena, Callback_PlayerDamage);
            PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPositionPacket);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            SpawnCallback.Unregister(arena, Callback_Spawn);

            return true;
        }

        #endregion

        #region ITeamVersusStatsBehavior

        async ValueTask<bool> ITeamVersusStatsBehavior.PlayerKilledAsync(
            ServerTick ticks,
            DateTime timestamp,
            IMatchData matchData,
            Player killed,
            IPlayerSlot killedSlot,
            Player killer,
            IPlayerSlot killerSlot,
            bool isKnockout)
        {
            //
            // Gather some info in local variables prior to the delay.
            //

            if (!_matchStatsDictionary.TryGetValue(matchData.MatchIdentifier, out MatchStats matchStats))
            {
                _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats),
                    $"ITeamVersusStatsBehavior.PlayerKilledAsync called for an unknown match (MatchType:{matchData.MatchIdentifier.MatchType}, ArenaNumber:{matchData.MatchIdentifier.ArenaNumber}, BoxId:{matchData.MatchIdentifier.BoxIdx})");
                return false;
            }

            if (!killed.TryGetExtraData(_pdKey, out PlayerData killedPlayerData))
                return false;

            MemberStats killedMemberStats = killedPlayerData.MemberStats;
            if (killedMemberStats is null)
            {
                if (matchStats.Teams.TryGetValue(killedSlot.Team.Freq, out TeamStats killedTeamStats))
                {
                    killedMemberStats = killedTeamStats.Slots[killedSlot.SlotIdx].Members.Find(
                        mStat => string.Equals(mStat.PlayerName, killed.Name, StringComparison.OrdinalIgnoreCase));
                }

                if (killedMemberStats is null)
                    return false;
            }

            // player names
            string killedPlayerName = killed.Name;
            string killerPlayerName = killer.Name;

            // ships
            ShipType killedShip = killed.Ship;
            ShipType killerShip = killer.Ship;

            // kill coordinates
            short xCoord = killed.Position.X;
            short yCoord = killed.Position.Y;

            //
            // Delay processing the kill to allow time for final the C2S damage packet to make it to the server.
            // This gives a chance for C2S Damage packets to make it to the server and therefore more accurate damage stats.
            //

            await Task.Delay(200);

            // The Player objects might be invalid after the delay (e.g. if a player disconnected during the delay).
            // Compare by player name and consider the Player object invalid if it doesn't match.

            if (!string.Equals(killed.Name, killedPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                killed = null;
                killedPlayerData = null;
            }

            if (!string.Equals(killer.Name, killerPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                killer = null;
            }

            //
            // Calculate kill damage (how much damage to attribute to the kill) for each attacker based on recent damage.
            //

            Dictionary<PlayerTeamSlot, int> damageDictionary = s_damageDictionaryPool.Get();
            List<(string PlayerName, int Damage)> damageList = s_damageListPool.Get();

            try
            {
                CalculateDamageDealt(ticks, killedMemberStats, killedShip, damageDictionary);
                killedMemberStats.ClearRecentDamage();

                // Update attacker stats.
                foreach ((PlayerTeamSlot attacker, int damage) in damageDictionary)
                {
                    if (!matchStats.Teams.TryGetValue(attacker.Freq, out TeamStats attackerTeamStats))
                        continue;

                    SlotStats attackerSlotStats = attackerTeamStats.Slots[attacker.SlotIdx];
                    MemberStats attackerMemberStats = attackerSlotStats.Members.Find(mStat => string.Equals(mStat.PlayerName, attacker.PlayerName, StringComparison.OrdinalIgnoreCase));
                    if (killedSlot.Team.Freq == attacker.Freq)
                    {
                        attackerMemberStats.TeamKillDamage += damage;
                    }
                    else
                    {
                        attackerMemberStats.KillDamage += damage;

                        if (string.Equals(attackerMemberStats.PlayerName, killerPlayerName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (damageDictionary.Count == 1)
                            {
                                attackerMemberStats.SoloKills++;
                            }
                        }
                        else
                        {
                            attackerMemberStats.Assists++; // TODO: do we want more criteria (a minimum amount of damage)?
                        }
                    }
                }

                // Create a list of players that dealt damage, with their damage sum.
                foreach ((PlayerTeamSlot attacker, int damage) in damageDictionary)
                {
                    int index = damageList.FindIndex(tuple => string.Equals(tuple.PlayerName, attacker.PlayerName, StringComparison.OrdinalIgnoreCase));
                    if (index != -1)
                    {
                        damageList[index] = (attacker.PlayerName, damageList[index].Damage + damage);
                    }
                    else
                    {
                        damageList.Add((attacker.PlayerName, damage));
                    }
                }

                // Sort the list by: damage desc, name asc
                damageList.Sort(PlayerDamageTupleComparison);

                //
                // Send notifications
                //

                HashSet<Player> notifySet = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    GetPlayersToNotify(matchStats, notifySet);

                    StringBuilder gameTimeBuilder = _objectPoolManager.StringBuilderPool.Get();
                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        TimeSpan gameTime = matchData.Started is not null ? timestamp - matchData.Started.Value : TimeSpan.Zero;
                        gameTimeBuilder.AppendFriendlyTimeSpan(gameTime);

                        // Kill notification
                        int assistCount = 0;
                        foreach (var tuple in damageList)
                        {
                            if (string.Equals(killerPlayerName, tuple.PlayerName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append($"{tuple.PlayerName} ({tuple.Damage})");
                            assistCount++;
                        }

                        if (assistCount > 0)
                            _chat.SendSetMessage(notifySet, $"{killedPlayerName} kb {killerPlayerName} -- {(assistCount == 1 ? "Assist" : "Assists")}: {sb}");
                        else
                            _chat.SendSetMessage(notifySet, $"{killedPlayerName} kb {killerPlayerName}");

                        sb.Clear();

                        // Remaining lives notification
                        // TODO: A way to not duplicate this logic which is also in TeamVersusMatch
                        if (isKnockout)
                        {
                            _chat.SendSetMessage(notifySet, CultureInfo.InvariantCulture, $"{killedPlayerName} is OUT! [{gameTimeBuilder}]");
                        }
                        else
                        {
                            _chat.SendSetMessage(notifySet, CultureInfo.InvariantCulture, $"{killedPlayerName} has {killedMemberStats.SlotStats.Slot.Lives} {(killedMemberStats.SlotStats.Slot.Lives > 1 ? "lives" : "life")} remaining [{gameTimeBuilder}]");
                        }

                        // Score notification
                        // TODO: A way to not duplicate this logic which is also in TeamVersusMatch
                        StringBuilder remainingBuilder = _objectPoolManager.StringBuilderPool.Get();

                        try
                        {
                            short highScore = -1;
                            short highScoreFreq = -1;
                            int highScoreCount = 0;

                            foreach (var team in matchData.Teams)
                            {
                                if (sb.Length > 0)
                                {
                                    sb.Append('-');
                                    remainingBuilder.Append('v');
                                }

                                int remainingSlots = 0;
                                foreach (var slot in team.Slots)
                                {
                                    if (slot.Lives > 0)
                                    {
                                        remainingSlots++;
                                    }
                                }

                                sb.Append(team.Score);
                                remainingBuilder.Append(remainingSlots);

                                if (team.Score > highScore)
                                {
                                    highScore = team.Score;
                                    highScoreFreq = team.Freq;
                                    highScoreCount = 1;
                                }
                                else if (team.Score == highScore)
                                {
                                    highScoreCount++;
                                    highScoreFreq = -1;
                                }
                            }

                            if (highScoreCount == 1)
                            {
                                sb.Append($" Freq {highScoreFreq}");
                            }
                            else
                            {
                                sb.Append(" TIE");
                            }

                            _chat.SendSetMessage(notifySet, $"Score: {sb} -- {remainingBuilder} -- [{gameTimeBuilder}]");
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(remainingBuilder);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                        _objectPoolManager.StringBuilderPool.Return(gameTimeBuilder);
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(notifySet);
                }

                matchStats.AddKillEvent(
                    timestamp,
                    killedPlayerName,
                    killedShip,
                    killerPlayerName,
                    killerShip,
                    isKnockout,
                    xCoord,
                    yCoord,
                    damageList);
            }
            finally
            {
                s_damageDictionaryPool.Return(damageDictionary);
                s_damageListPool.Return(damageList);
            }

            return true;


            static int PlayerDamageTupleComparison((string PlayerName, int Damage) x, (string PlayerName, int Damage) y)
            {
                int ret = x.Damage.CompareTo(y.Damage);
                if (ret != 0)
                    return -ret; // damage desc

                return string.Compare(x.PlayerName, y.PlayerName); // player name asc
            }
        }

        async ValueTask<bool> ITeamVersusStatsBehavior.MatchEndedAsync(IMatchData matchData, MatchEndReason reason, ITeam winnerTeam)
        {
            if (!_matchStatsDictionary.Remove(matchData.MatchIdentifier, out MatchStats matchStats))
                return false;

            matchStats.EndTimestamp = DateTime.UtcNow;

            // Refresh play times and ship usage data.
            foreach (TeamStats teamStats in matchStats.Teams.Values)
            {
                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    foreach (MemberStats memberStats in slotStats.Members)
                    {
                        if (memberStats.StartTime is not null)
                        {
                            memberStats.PlayTime += matchStats.EndTimestamp.Value - memberStats.StartTime.Value;
                            memberStats.StartTime = null;
                        }

                        if (memberStats.CurrentShip is not null && memberStats.CurrentShipStartTime is not null)
                        {
                            memberStats.ShipUsage[(int)memberStats.CurrentShip] += (matchStats.EndTimestamp.Value - memberStats.CurrentShipStartTime.Value);
                            memberStats.CurrentShip = null;
                            memberStats.CurrentShipStartTime = null;
                        }
                    }
                }
            }

            foreach (TeamStats teamStats in matchStats.Teams.Values)
            {
                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    if (_playerMemberDictionary.Remove(slotStats.Slot.PlayerName))
                    {
                        SetStoppedPlaying(slotStats.Slot.Player);
                    }
                }
            }

            await SaveGameToDatabase(matchData, winnerTeam, matchStats);

            // Send game stat as chat notifications.
            HashSet<Player> notifySet = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetPlayersToNotify(matchStats, notifySet);
                PrintMatchStats(notifySet, matchStats, reason, winnerTeam);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(notifySet);
            }

            ResetMatchStats(matchStats);
            // TODO: return matchStats to a pool

            return true;


            async Task SaveGameToDatabase(IMatchData matchData, ITeam winnerTeam, MatchStats matchStats)
            {
                if (matchData is null || matchStats is null)
                    return;

                if (_gameStatsRepository is null)
                    return;

                // TODO: Maybe pool the MemoryStream and Utf8JsonWriter?
                // The RecyclableMemoryStream reuses underlying buffers, but maybe just having a pool of regular MemoryStream
                // The Utf8JsonWriter has a Reset method.

                using MemoryStream gameJsonStream = s_recyclableMemoryStreamManager.GetStream();
                using Utf8JsonWriter writer = new(gameJsonStream, default);

                writer.WriteStartObject(); // game object
                writer.WriteNumber("game_type_id"u8, 4); // TODO: translate matchData.MatchIdentifier.MatchType to the proper ID
                writer.WriteString("zone_server_name"u8, _zoneServerName);
                writer.WriteString("arena"u8, matchData.ArenaName);
                writer.WriteNumber("box_number"u8, matchData.MatchIdentifier.BoxIdx);
                WriteLvlInfo(writer, matchData);
                writer.WriteString("start_timestamp"u8, matchStats.StartTimestamp);
                writer.WriteString("end_timestamp"u8, matchStats.EndTimestamp.Value);
                writer.WriteString("replay_path"u8, (string)null); // TODO: add the ability automatically record games
                writer.WriteStartArray("team_stats"u8); // team_stats array

                foreach (TeamStats teamStats in matchStats.Teams.Values)
                {
                    writer.WriteStartObject(); // team object
                    writer.WriteNumber("freq"u8, teamStats.Team.Freq);
                    writer.WriteBoolean("is_premade"u8, teamStats.Team.IsPremade);
                    writer.WriteBoolean("is_winner"u8, teamStats.Team == winnerTeam);
                    writer.WriteNumber("score"u8, teamStats.Team.Score);
                    writer.WriteStartArray("player_slots"u8); // player_slots array

                    foreach (SlotStats slotStats in teamStats.Slots)
                    {
                        writer.WriteStartObject(); // slot object
                        writer.WriteStartArray("player_stats"u8);

                        foreach (MemberStats memberStats in slotStats.Members)
                        {
                            writer.WriteStartObject(); // team member object
                            writer.WriteString("player"u8, memberStats.PlayerName);
                            writer.TryWriteTimeSpanAsISO8601("play_duration"u8, memberStats.PlayTime);
                            writer.WriteNumber("lag_outs"u8, memberStats.LagOuts);
                            writer.WriteNumber("kills"u8, memberStats.Kills);
                            writer.WriteNumber("deaths"u8, memberStats.Deaths);
                            writer.WriteNumber("team_kills"u8, memberStats.TeamKills);
                            writer.WriteNumber("forced_reps"u8, memberStats.ForcedReps);
                            writer.WriteNumber("gun_damage_dealt"u8, memberStats.DamageDealtBullets);
                            writer.WriteNumber("bomb_damage_dealt"u8, memberStats.DamageDealtBombs);
                            writer.WriteNumber("team_damage_dealt"u8, memberStats.DamageDealtTeam);
                            writer.WriteNumber("gun_damage_taken"u8, memberStats.DamageTakenBullets);
                            writer.WriteNumber("bomb_damage_taken"u8, memberStats.DamageTakenBombs);
                            writer.WriteNumber("team_damage_taken"u8, memberStats.DamageTakenTeam);
                            writer.WriteNumber("self_damage"u8, memberStats.DamageSelf);
                            writer.WriteNumber("kill_damage"u8, memberStats.KillDamage);
                            writer.WriteNumber("team_kill_damage"u8, memberStats.TeamKillDamage);
                            writer.WriteNumber("bullet_fire_count"u8, memberStats.GunFireCount);
                            writer.WriteNumber("bomb_fire_count"u8, memberStats.BombFireCount);
                            writer.WriteNumber("mine_fire_count"u8, memberStats.MineFireCount);
                            writer.WriteNumber("bullet_hit_count"u8, memberStats.GunHitCount);
                            writer.WriteNumber("bomb_hit_count"u8, memberStats.BombHitCount);
                            writer.WriteNumber("mine_hit_count"u8, memberStats.MineHitCount);

                            if (memberStats.WastedRepels > 0)
                                writer.WriteNumber("wasted_repel"u8, memberStats.WastedRepels);

                            if (memberStats.WastedRockets > 0)
                                writer.WriteNumber("wasted_rocket"u8, memberStats.WastedRockets);

                            if (memberStats.WastedThors > 0)
                                writer.WriteNumber("wasted_thor"u8, memberStats.WastedThors);

                            if (memberStats.WastedBursts > 0)
                                writer.WriteNumber("wasted_burst"u8, memberStats.WastedBursts);

                            if (memberStats.WastedDecoys > 0)
                                writer.WriteNumber("wasted_decoy"u8, memberStats.WastedDecoys);

                            if (memberStats.WastedPortals > 0)
                                writer.WriteNumber("wasted_portal"u8, memberStats.WastedPortals);

                            if (memberStats.WastedBricks > 0)
                                writer.WriteNumber("wasted_brick"u8, memberStats.WastedBricks);

                            writer.WriteStartObject("ship_usage"u8);
                            for (int shipIndex = 0; shipIndex < memberStats.ShipUsage.Length; shipIndex++)
                            {
                                TimeSpan usage = memberStats.ShipUsage[shipIndex];
                                if (usage > TimeSpan.Zero)
                                {
                                    switch (shipIndex)
                                    {
                                        case 0: writer.TryWriteTimeSpanAsISO8601("warbird"u8, usage); break;
                                        case 1: writer.TryWriteTimeSpanAsISO8601("javelin"u8, usage); break;
                                        case 2: writer.TryWriteTimeSpanAsISO8601("spider"u8, usage); break;
                                        case 3: writer.TryWriteTimeSpanAsISO8601("leviathan"u8, usage); break;
                                        case 4: writer.TryWriteTimeSpanAsISO8601("terrier"u8, usage); break;
                                        case 5: writer.TryWriteTimeSpanAsISO8601("weasel"u8, usage); break;
                                        case 6: writer.TryWriteTimeSpanAsISO8601("lancaster"u8, usage); break;
                                        case 7: writer.TryWriteTimeSpanAsISO8601("shark"u8, usage); break;
                                        default:
                                            continue;
                                    }
                                }
                            }
                            writer.WriteEndObject(); // ship_usage

                            writer.WriteNumber("rating_change"u8, 0); // TODO: add rating logic
                            writer.WriteEndObject(); // team member object
                        }

                        writer.WriteEndArray(); // player_stats
                        writer.WriteEndObject(); // slot object
                    }

                    writer.WriteEndArray(); // player_slots array
                    writer.WriteEndObject(); // team object
                }

                writer.WriteEndArray(); // team_stats array

                writer.WritePropertyName("events"u8);
                matchStats.WriteEventsArray(writer);

                writer.WriteEndObject(); // game object

                writer.Flush();
                gameJsonStream.Position = 0;

                // DEBUG - REMOVE ME ***************************************************
                StreamReader reader = new(gameJsonStream, Encoding.UTF8);
                string data = reader.ReadToEnd();
                Console.WriteLine(data);
                gameJsonStream.Position = 0;
                // DEBUG - REMOVE ME ***************************************************

                matchStats.GameId = await _gameStatsRepository.SaveGame(gameJsonStream);


                void WriteLvlInfo(Utf8JsonWriter writer, IMatchData matchData)
                {
                    ReadOnlySpan<char> arenaBaseName;
                    if (matchData.Arena is not null)
                        arenaBaseName = matchData.Arena.BaseName;
                    else if (!Arena.TryParseArenaName(matchData.ArenaName, out arenaBaseName, out _))
                        arenaBaseName = "";

                    if (_arenaLvlTrie.TryGetValue(arenaBaseName, out var lvlTuple))
                    {
                        writer.WriteString("lvl_file_name"u8, lvlTuple.LvlFilename);
                        writer.WriteNumber("lvl_checksum"u8, (int)lvlTuple.Checksum);
                    }
                    else
                    {
                        writer.WriteString("lvl_file_name"u8, "");
                        writer.WriteNumber("lvl_checksum"u8, 0);
                    }
                }
            }
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                if (!_arenaSettingsTrie.TryGetValue(arena.BaseName, out ArenaSettings arenaSettings))
                {
                    arenaSettings = new ArenaSettings();
                    _arenaSettingsTrie.Add(arena.BaseName, arenaSettings);
                }

                arenaSettings.BombDamageLevel = _clientSettings.GetSetting(arena, _bombDamageLevelClientSettingId) / 1000;
                arenaSettings.EmpBombShutdownTime = (uint)_clientSettings.GetSetting(arena, _eBombShutdownTimeClientSettingId);
                arenaSettings.EmpBombDamageRatio = _clientSettings.GetSetting(arena, _eBombDamagePercentClientSettingId) / 1000f;

                for (int shipIndex = 0; shipIndex < arenaSettings.ShipSettings.Length; shipIndex++)
                {
                    arenaSettings.ShipSettings[shipIndex] = new ShipSettings()
                    {
                        MaximumRecharge = (short)_clientSettings.GetSetting(arena, _shipClientSettingIds[shipIndex].MaximumRechargeId),
                        MaximumEnergy = (short)_clientSettings.GetSetting(arena, _shipClientSettingIds[shipIndex].MaximumEnergyId),
                        HasEmpBomb = _clientSettings.GetSetting(arena, _shipClientSettingIds[shipIndex].EmpBombId) != 0,
                    };
                }

                string mapPath = _mapData.GetMapFilename(arena, null);
                if (mapPath is not null)
                    mapPath = Path.GetFileName(mapPath);

                _arenaLvlTrie[arena.BaseName] = (mapPath, _mapData.GetChecksum(arena, 0));
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            if (action == PlayerAction.Connect)
            {
                if (_playerMemberDictionary.TryGetValue(player.Name, out MemberStats memberStats))
                {
                    // The player is currently in a match and has reconnected.
                    playerData.MemberStats = memberStats;
                }
            }
            else if (action == PlayerAction.EnterArena)
            {
                if (playerData.MemberStats is not null
                    && arena == playerData.MemberStats.MatchStats.MatchData.Arena)
                {
                    AddDamageWatch(player, playerData);
                }
            }
            else if (action == PlayerAction.LeaveArena)
            {
                RemoveDamageWatch(player, playerData);

                if (playerData.MemberStats is not null
                    && arena == playerData.MemberStats.MatchStats?.MatchData?.Arena
                    && player.Ship != ShipType.Spec)
                {
                    // The player is in a match and left the match's arena while in a ship.
                    SetLagOut(playerData.MemberStats);
                }
            }
        }
        
        private void Callback_TeamVersusMatchStarted(IMatchData matchData)
        {
            if (_matchStatsDictionary.TryGetValue(matchData.MatchIdentifier, out MatchStats matchStats))
            {
                ResetMatchStats(matchStats);
            }
            else
            {
                matchStats = new(); // TODO: get from a pool
                matchStats.Initialize(matchData);
                _matchStatsDictionary.Add(matchData.MatchIdentifier, matchStats);
            }

            matchStats.StartTimestamp = matchData.Started.Value;

            for (int teamIdx = 0; teamIdx < matchData.Teams.Count; teamIdx++)
            {
                ITeam team = matchData.Teams[teamIdx];
                TeamStats teamStats = new(); // TODO: get from a pool
                teamStats.Initialize(matchStats, team);

                for (int slotIdx = 0; slotIdx < team.Slots.Count; slotIdx++)
                {
                    IPlayerSlot slot = team.Slots[slotIdx];
                    SlotStats slotStats = new(); // TODO: get from a pool
                    slotStats.Initialize(teamStats, slot);

                    MemberStats memberStats = new(); // TODO: get from a pool
                    memberStats.Initialize(slotStats, slot.PlayerName);
                    memberStats.StartTime = DateTime.UtcNow;

                    slotStats.Members.Add(memberStats);
                    slotStats.Current = memberStats;

                    _playerMemberDictionary[memberStats.PlayerName] = memberStats;
                    Player player = slot.Player ?? _playerData.FindPlayer(memberStats.PlayerName);
                    if (player is not null)
                    {
                        SetStartedPlaying(player, memberStats);
                    }

                    teamStats.Slots.Add(slotStats);

                    matchStats.AddAssignSlotEvent(matchStats.StartTimestamp, slot.Team.Freq, slot.SlotIdx, slot.PlayerName);
                }

                matchStats.Teams.Add(team.Freq, teamStats);
            }
        }

        private void Callback_TeamVersusMatchPlayerSubbed(IPlayerSlot playerSlot, string subOutPlayerName)
        {
            if (!_matchStatsDictionary.TryGetValue(playerSlot.MatchData.MatchIdentifier, out MatchStats matchStats)
                || !matchStats.Teams.TryGetValue(playerSlot.Team.Freq, out TeamStats teamStats))
            {
                return;
            }

            SlotStats slotStats = teamStats.Slots[playerSlot.SlotIdx];

            Debug.Assert(slotStats.Slot == playerSlot);
            Debug.Assert(string.Equals(slotStats.Current.PlayerName, subOutPlayerName, StringComparison.OrdinalIgnoreCase));
            Debug.Assert(!string.Equals(slotStats.Current.PlayerName, playerSlot.PlayerName, StringComparison.OrdinalIgnoreCase));

            _playerMemberDictionary.Remove(slotStats.Current.PlayerName);
            Player subOutPlayer = _playerData.FindPlayer(subOutPlayerName);
            if (subOutPlayer is not null)
            {
                SetStoppedPlaying(subOutPlayer);
            }

            MemberStats memberStats = slotStats.Members.Find(mStat => string.Equals(playerSlot.PlayerName, mStat.PlayerName, StringComparison.OrdinalIgnoreCase));
            if (memberStats is null)
            {
                memberStats = new(); // TODO: get from a pool
                memberStats.Initialize(slotStats, playerSlot.PlayerName);
                slotStats.Members.Add(memberStats);
            }

            slotStats.Current = memberStats;

            _playerMemberDictionary[memberStats.PlayerName] = memberStats;
            Player subInPlayer = playerSlot.Player ?? _playerData.FindPlayer(playerSlot.PlayerName);
            if (subInPlayer is not null)
            {
                SetStartedPlaying(subInPlayer, memberStats);
            }

            matchStats.AddAssignSlotEvent(DateTime.UtcNow, playerSlot.Team.Freq, playerSlot.SlotIdx, playerSlot.PlayerName);
        }

        private void Callback_BricksPlaced(Arena arena, Player player, IReadOnlyList<BrickData> bricks)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            MemberStats memberStats = playerData.MemberStats;
            if (memberStats is null || !memberStats.IsCurrent)
                return;

            memberStats.MatchStats.AddUseItemEvent(DateTime.UtcNow, player.Name, ShipItem.Brick, null);
        }

        private void Callback_PlayerDamage(Player player, ServerTick timestamp, ReadOnlySpan<DamageData> damageDataSpan)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            Arena arena = player.Arena;
            if (arena is null || !_arenaSettingsTrie.TryGetValue(arena.BaseName, out ArenaSettings arenaSettings))
                return;

            MemberStats playerStats = playerData.MemberStats;
            if (playerStats is null || !playerStats.IsCurrent)
                return;

            MatchStats matchStats = playerStats.MatchStats;
            if (arena != matchStats.MatchData.Arena)
                return;

            for (int i = 0; i < damageDataSpan.Length; i++)
            {
                ref readonly DamageData damageData = ref damageDataSpan[i];

                // damageData.Damage can be > damageData.Energy if it's the killing blow or if the player caused self-inflicted damage.
                // So, clamp the damage to the amount of energy the player has. So that we record the actual amount taken.
                short damage = Math.Clamp(damageData.Damage, (short)0, damageData.Energy);

                if (player.Id == damageData.AttackerPlayerId)
                {
                    playerStats.DamageSelf += damage;
                }
                else
                {
                    Player attackerPlayer = _playerData.PidToPlayer(damageData.AttackerPlayerId);

                    // Note: It's possible that the attacker disconnected before the damage packet made it to us,
                    // in which case attackerPlayer will be null, and we just won't record stats for the attacker.

                    MemberStats attackerStats = null;
                    if (attackerPlayer is not null)
                    {
                        if(attackerPlayer.TryGetExtraData(_pdKey, out PlayerData attackerPlayerData))
                            attackerStats = attackerPlayerData.MemberStats;

                        // There is a chance that the attacker was subbed out before the damage packet made it to us and is getting processed here,
                        // in which case we just won't record stats for the attacker.

                        /* 
                        // Possible to search like this
                        if (attackerStats is null)
                        {
                            // There's a chance that the attacker is no longer on the freq he was on when the damage was dealt.
                            // However, the attacker's current freq is our best guess.
                            if (matchStats.Teams.TryGetValue(attackerPlayer.Freq, out TeamStats attackerTeamStats))
                            {
                                foreach (SlotStats slotStats in attackerTeamStats.Slots)
                                {
                                    attackerStats = slotStats.Members.Find(
                                        mStat => string.Equals(mStat.PlayerName, attackerPlayer.Name, StringComparison.OrdinalIgnoreCase));

                                    if (attackerStats is not null)
                                        break;
                                }
                            }

                            if (attackerStats is null)
                            {
                                foreach (TeamStats teamStats in matchStats.Teams.Values)
                                {
                                    foreach (SlotStats slotStats in teamStats.Slots)
                                    {
                                        attackerStats = slotStats.Members.Find(
                                            mStat => string.Equals(mStat.PlayerName, attackerPlayer.Name, StringComparison.OrdinalIgnoreCase));

                                        if (attackerStats is not null)
                                            break;
                                    }
                                }
                            }
                        }
                        */
                    }

                    // If we haven't found the attacker's MemberStats by now, then too bad. We just won't record stats for the attacker.

                    //
                    // Recent damage (used for calculating damage that contributed to a kill)
                    //

                    if (attackerStats is not null)
                    {
                        int shipIndex = int.Clamp((int)player.Ship, 0, 7);

                        ShipSettings shipSettings = arenaSettings.ShipSettings[shipIndex];

                        // recharge rate = amount of energy in 10 seconds = amount of energy in 1000 ticks
                        short maximumRecharge = 
                            _clientSettings.TryGetSettingOverride(arena, _shipClientSettingIds[shipIndex].MaximumRechargeId, out int maximumRechargeInt)
                                ? (short)maximumRechargeInt
                                : _clientSettings.TryGetSettingOverride(player, _shipClientSettingIds[shipIndex].MaximumRechargeId, out maximumRechargeInt) 
                                    ? (short)maximumRechargeInt 
                                    : shipSettings.MaximumRecharge;

                        short maximumEnergy =
                            _clientSettings.TryGetSettingOverride(arena, _shipClientSettingIds[shipIndex].MaximumEnergyId, out int maximumEnergyInt)
                                ? (short)maximumEnergyInt
                                : _clientSettings.TryGetSettingOverride(player, _shipClientSettingIds[shipIndex].MaximumEnergyId, out maximumEnergyInt)
                                    ? (short)maximumEnergyInt
                                    : shipSettings.MaximumEnergy;

                        playerStats.RemoveOldRecentDamage(maximumEnergy, maximumRecharge);

                        // Calculate emp shutdown time.
                        uint empShutdownTicks = 0;
                        if (damageData.WeaponData.Type == WeaponCodes.Bomb || damageData.WeaponData.Type == WeaponCodes.ProxBomb)
                        {
                            int attackerShipIndex = int.Clamp((int)attackerPlayer.Ship, 0, 7);

                            // Only checking for an arena override on emp bomb, since it doesn't make sense to override that setting on the player-level.
                            bool isEmp = _clientSettings.TryGetSettingOverride(arena, _shipClientSettingIds[attackerShipIndex].EmpBombId, out int isEmpInt)
                                ? isEmpInt != 0
                                : arenaSettings.ShipSettings[attackerShipIndex].HasEmpBomb;

                            if (isEmp)
                            {
                                // The formula for calculating how long an EMP bomb pauses recharge is:
                                // (uint)(actualDamage * empBombShutdownTime / maxBombDamage);
                                // where maxBombDamage = BombDamageLevel * EmpBombDamageRatio
                                //
                                // Also, notice this is looking at damageData.Damage which is not clamped.
                                // The damage could have been a self-inflicted emp bomb that took the player all the way down to 0.
                                // So, using damageData.Damage gives us the true shutdown time.
                                empShutdownTicks = (uint)(damageData.Damage * arenaSettings.EmpBombShutdownTime / (arenaSettings.BombDamageLevel * arenaSettings.EmpBombDamageRatio));
                            }
                        }

                        LinkedListNode<DamageInfo> node = s_damageInfoLinkedListNodePool.Get();
                        node.Value = new DamageInfo()
                        {
                            Timestamp = timestamp,
                            Damage = damage,
                            EmpBombShutdownTicks = empShutdownTicks,
                            Attacker = new PlayerTeamSlot(
                                attackerPlayer.Name, 
                                attackerPlayer.Freq, 
                                attackerStats.SlotStats.Slot.SlotIdx),
                        };
                        playerStats.RecentDamageTaken.AddLast(node);
                    }

                    //
                    // Damage stats
                    //

                    if (damageData.WeaponData.Type == WeaponCodes.Bullet || damageData.WeaponData.Type == WeaponCodes.BounceBullet)
                    {
                        // bullet damage
                        playerStats.DamageTakenBullets += damage;

                        if (attackerStats is not null)
                        {
                            attackerStats.DamageDealtBullets += damage;
                            attackerStats.GunHitCount++;
                        }
                    }
                    else if (damageData.WeaponData.Type == WeaponCodes.Bomb
                        || damageData.WeaponData.Type == WeaponCodes.ProxBomb
                        || damageData.WeaponData.Type == WeaponCodes.Thor)
                    {
                        // bomb damage
                        if (attackerPlayer?.Freq == player.Freq)
                        {
                            playerStats.DamageTakenTeam += damage;
                        }
                        else
                        {
                            playerStats.DamageTakenBombs += damage;
                        }

                        if (attackerStats is not null)
                        {
                            if (player.Freq == attackerPlayer.Freq)
                            {
                                // Damage to teammate
                                attackerStats.DamageDealtTeam += damage;
                            }
                            else
                            {
                                // Damage to opponent
                                attackerStats.DamageDealtBombs += damage;

                                if (damageData.WeaponData.Alternate)
                                    attackerStats.MineHitCount++;
                                else
                                    attackerStats.BombHitCount++;
                            }
                        }
                    }
                    else if (damageData.WeaponData.Type == WeaponCodes.Shrapnel)
                    {
                        // consider it bomb damage
                        playerStats.DamageTakenBombs += damage;

                        if (attackerStats is not null)
                        {
                            attackerStats.DamageDealtBombs += damage;
                        }
                    }
                    else if (damageData.WeaponData.Type == WeaponCodes.Burst)
                    {
                        // consider it bullet damage
                        playerStats.DamageTakenBullets += damage;

                        if (attackerStats is not null)
                        {
                            attackerStats.DamageDealtBullets += damage;
                            //attackerStats.BulletHitCount++; // TODO: decide if we want to count it towards bullet hits (if so, probably want to consider firing a burst as having fired All:BurstShrapnel bullets)
                        }
                    }
                }
            }
        }

        private void Callback_PlayerPositionPacket(Player player, in C2S_PositionPacket positionPacket, bool hasExtraPositionData)
        {
            if (player is null)
                return;

            if (positionPacket.Weapon.Type == WeaponCodes.Null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            MemberStats memberStats = playerData.MemberStats;
            if (memberStats is null || !memberStats.IsCurrent)
                return;

            MatchStats matchStats = memberStats.MatchStats;
            if (player.Arena != matchStats.MatchData.Arena)
                return;

            switch (positionPacket.Weapon.Type)
            {
                case WeaponCodes.Bullet:
                case WeaponCodes.BounceBullet:
                    memberStats.GunFireCount++;
                    break;

                case WeaponCodes.Thor:
                    matchStats.AddUseItemEvent(DateTime.UtcNow, player.Name, ShipItem.Thor, null);
                    goto case WeaponCodes.Bomb;

                case WeaponCodes.Bomb:
                case WeaponCodes.ProxBomb:
                    if (positionPacket.Weapon.Alternate)
                        memberStats.MineFireCount++;
                    else
                        memberStats.BombFireCount++;
                    break;

                case WeaponCodes.Burst:
                    //memberStats.BulletFireCount += All:BurstShrapnel // TODO: maybe? and if so, also remember to add BulletHitCount logic

                    matchStats.AddUseItemEvent(DateTime.UtcNow, player.Name, ShipItem.Burst, null);
                    break;

                case WeaponCodes.Repel:
                    Dictionary<PlayerTeamSlot, int> damageDictionary = s_damageDictionaryPool.Get();
                    List<(string PlayerName, int Damage)> damageList = s_damageListPool.Get();

                    try
                    {
                        CalculateDamageDealt(ServerTick.Now, memberStats, player.Ship, damageDictionary);

                        // Update attacker stats.
                        foreach ((PlayerTeamSlot attacker, int damage) in damageDictionary)
                        {
                            if (!matchStats.Teams.TryGetValue(attacker.Freq, out TeamStats attackerTeamStats))
                                continue;

                            SlotStats attackerSlotStats = attackerTeamStats.Slots[attacker.SlotIdx];
                            MemberStats attackerMemberStats = attackerSlotStats.Members.Find(mStat => string.Equals(mStat.PlayerName, attacker.PlayerName, StringComparison.OrdinalIgnoreCase));
                            if (memberStats.TeamStats.Team.Freq != attacker.Freq)
                            {
                                attackerMemberStats.ForcedRepDamage += damage;
                                attackerMemberStats.ForcedReps++; // TODO: do we want more criteria (a certain amount of damage or an even smaller damage window)?
                            }
                        }

                        // Create a list of players that dealt damage, with their damage sum.
                        foreach ((PlayerTeamSlot attacker, int damage) in damageDictionary)
                        {
                            int index = damageList.FindIndex(tuple => string.Equals(tuple.PlayerName, attacker.PlayerName, StringComparison.OrdinalIgnoreCase));
                            if (index != -1)
                            {
                                damageList[index] = (attacker.PlayerName, damageList[index].Damage + damage);
                            }
                            else
                            {
                                damageList.Add((attacker.PlayerName, damage));
                            }
                        }

                        matchStats.AddUseItemEvent(DateTime.UtcNow, player.Name, ShipItem.Repel, damageList);
                    }
                    finally
                    {
                        s_damageDictionaryPool.Return(damageDictionary);
                        s_damageListPool.Return(damageList);
                    }

                    break;

                case WeaponCodes.Decoy:
                    matchStats.AddUseItemEvent(DateTime.UtcNow, player.Name, ShipItem.Decoy, null);
                    break;

                default:
                    break;
            }
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Prize green)
        {
            if (!killer.TryGetExtraData(_pdKey, out PlayerData killerData))
                return;

            if (!killed.TryGetExtraData(_pdKey, out PlayerData killedData))
                return;

            MemberStats killerStats = killerData.MemberStats;
            if (killerStats is null)
                return;

            MemberStats killedStats = killedData.MemberStats;
            if (killedStats is null)
                return;

            MatchStats matchStats = killedStats.MatchStats;
            if (matchStats is null)
                return;

            // Check that the players are in the same match.
            if (matchStats != killerStats.MatchStats)
                return;

            // Check that the kill was made in the correct arena.
            if (arena != matchStats.MatchData.Arena)
                return;

            // Update killer stats.
            if (killer.Freq == killed.Freq)
            {
                killerStats.TeamKills++;
            }
            else
            {
                killerStats.Kills++;
            }

            // Update killed stats.
            killedStats.Deaths++;

            IPlayerSlot slot = killedStats.SlotStats?.Slot;
            if (slot is not null)
            {
                killedStats.WastedRepels += slot.Repels;
                killedStats.WastedRockets += slot.Rockets;
                killedStats.WastedThors += slot.Thors;
                killedStats.WastedBursts += slot.Bursts;
                killedStats.WastedDecoys += slot.Decoys;
                killedStats.WastedPortals += slot.Portals;
                killedStats.WastedBricks += slot.Bricks;
            }

            // Unfortunately, we can't record damage stats here as the data is incomplete.
            // Continuum sends the last damage packet (for damage that that caused the kill) after the kill packet.
            // Continuum doesn't even group the packets. They're sent separately.
            // Therefore, instead we track damage stats in the TeamVersusMatchPlayerKilledCallback which is fired on a delay.
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            MemberStats memberStats = playerData.MemberStats;
            if (memberStats is null || !memberStats.IsCurrent)
                return;

            MatchStats matchStats = memberStats.MatchStats;
            if (player.Arena != matchStats.MatchData.Arena)
                return;

            // The player is in a match and is in the correct arena for that match.

            if (oldShip != ShipType.Spec && newShip == ShipType.Spec)
            {
                // The player changed to spec.
                SetLagOut(memberStats);
            }
            else if (oldShip == ShipType.Spec && newShip != ShipType.Spec)
            {
                // The player came out of spec and into a ship.
                memberStats.StartTime = DateTime.UtcNow;
            }

            DateTime now = DateTime.UtcNow;
            if (memberStats.CurrentShip is not null && memberStats.CurrentShipStartTime is not null)
            {
                memberStats.ShipUsage[(int)memberStats.CurrentShip] += (now - memberStats.CurrentShipStartTime.Value);
            }

            memberStats.CurrentShip = newShip == ShipType.Spec ? null : newShip;
            memberStats.CurrentShipStartTime = newShip == ShipType.Spec ? null : now;

            if (newShip != ShipType.Spec)
            {
                matchStats.AddShipChangeEvent(now, player.Name, newShip);
            }
        }

        private void Callback_Spawn(Player player, SpawnCallback.SpawnReason reason)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            MemberStats memberStats = playerData.MemberStats;
            if (memberStats is null)
                return;

            memberStats.ClearRecentDamage();
        }

        #endregion

        #region Commands
        private void Command_chart(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            MatchStats matchStats = playerData.MemberStats?.MatchStats;
            if (matchStats is null)
            {
                // TODO: check if the player is spectating a player in a match
                return;
            }
            
            HashSet<Player> notifySet = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                notifySet.Add(player);
                PrintMatchStats(notifySet, matchStats, null, null);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(notifySet);
            }            
        }

        #endregion

        private void SetStartedPlaying(Player player, MemberStats memberStats)
        {
            if (player is null
                || memberStats is null
                || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
            {
                return;
            }

            playerData.MemberStats = memberStats;
            AddDamageWatch(player, playerData);
        }

        private void SetStoppedPlaying(Player player)
        {
            if (player is null
                || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
            {
                return;
            }

            playerData.MemberStats = null;
            RemoveDamageWatch(player, playerData);
        }

        private void AddDamageWatch(Player player, PlayerData playerData)
        {
            if (player is null || playerData is null)
            {
                return;
            }

            if (!playerData.IsWatchingDamage)
            {
                _watchDamage.AddCallbackWatch(player);
                playerData.IsWatchingDamage = true;
            }
        }

        private void RemoveDamageWatch(Player player, PlayerData playerData)
        {
            if (player is null || playerData is null)
            {
                return;
            }

            if (playerData.IsWatchingDamage)
            {
                _watchDamage.RemoveCallbackWatch(player);
                playerData.IsWatchingDamage = false;
            }
        }

        private void CalculateDamageDealt(ServerTick ticks, MemberStats memberStats, ShipType ship, Dictionary<PlayerTeamSlot, int> damageDictionary)
        {
            if (memberStats is null || damageDictionary is null)
                return;

            IMatchData matchData = memberStats.MatchStats.MatchData;
            ReadOnlySpan<char> arenaBaseName;
            if (matchData.Arena is not null)
                arenaBaseName = matchData.Arena.BaseName;
            else if (!Arena.TryParseArenaName(matchData.ArenaName, out arenaBaseName, out _))
                return;

            if (!_arenaSettingsTrie.TryGetValue(arenaBaseName, out ArenaSettings arenaSettings))
                return;

            ShipSettings killedShipSettings = arenaSettings.ShipSettings[(int)ship];
            short maximumEnergy = killedShipSettings.MaximumEnergy;
            short rechargeRate = killedShipSettings.MaximumRecharge;

            memberStats.RemoveOldRecentDamage(maximumEnergy, rechargeRate);

            // How many ticks it takes for the player's ship to reach full energy from empty (maximum energy and recharge rate assumed).
            uint fullEnergyTicks = (uint)float.Ceiling(maximumEnergy * 1000f / rechargeRate);
            ServerTick cutoff = ticks - fullEnergyTicks;

            // TODO: Maybe add a parameter for an additional logic when calculating damage for a kill, to add a check if the last damage record was for the killing blow.
            // If not and there is at least one damage record, use the latest record to figure out how much damage the killer would have needed to inflict,
            // accounting for how much the player would have recharged. Assume the killer did half the damage?

            // This calculator will assist with calculating how much damage to award due to EMP recharge shutdown.
            // It will help us to determine how much shutdown time to allow when one EMP overlaps with another.
            // Here we start with the most recent damage first. So, as we iterate, we're adding in older damage after more recent damage.
            // For example, if we have damage from two EMPs and their shutdown time ranges overlap, the shutdown time awarded
            // for the EMP that came earlier will not include the time that intersects with the more recent EMP.
            // Note, this works for any # of overlapping EMPs too. Any range of overlap that is already counted will not be counted twice.
            TickRangeCalculator empShutdownCalculator = s_tickRangeCalculatorPool.Get();

            try
            {
                LinkedListNode<DamageInfo> node = memberStats.RecentDamageTaken.Last;
                while (node is not null)
                {
                    LinkedListNode<DamageInfo> previous = node.Previous;

                    ref DamageInfo damageInfo = ref node.ValueRef;

                    uint ticksAgo = (uint)(ticks - damageInfo.Timestamp);
                    int damage = 0;

                    if (damageInfo.Timestamp < cutoff)
                    {
                        // The damage happened outside of the recharge window.
                        if (damageInfo.EmpBombShutdownTicks > 0)
                        {
                            // It was an emp, so we might be able to count emp recharge loss that crossed into the window.
                            ServerTick empShutdownEndTick = damageInfo.Timestamp + damageInfo.EmpBombShutdownTicks;
                            if (empShutdownEndTick > cutoff)
                            {
                                int empShutdownTicks = empShutdownCalculator.Add(cutoff, empShutdownEndTick);
                                damage = (int)((rechargeRate / 1000f) * empShutdownTicks);
                            }
                            else
                            {
                                // The damage did not extend into the recharge window.
                                // However, there can still be earlier emp damage with a shutdown time that is long enough.
                                // So, we don't stop processing here, we want to keep reading previous nodes.
                            }
                        }
                    }
                    else
                    {
                        // The damage happened inside of the recharge window.
                        // We can count the full amount, minus any overlapping emp recharge damage.
                        int empShutdownDamage = 0;
                        if (damageInfo.EmpBombShutdownTicks > 0)
                        {
                            ServerTick empStartTimestamp = damageInfo.Timestamp;
                            ServerTick empEndTimestamp = empStartTimestamp + damageInfo.EmpBombShutdownTicks;

                            // Calculate how much emp shutdown time is allowed.
                            int empShutdownTicks = empShutdownCalculator.Add(empStartTimestamp, empEndTimestamp);
                            empShutdownDamage = (int)((rechargeRate / 1000f) * empShutdownTicks);
                        }

                        damage = damageInfo.Damage + empShutdownDamage;
                    }

                    if (damage > 0)
                    {
                        if (damageDictionary.TryGetValue(damageInfo.Attacker, out int totalDamage))
                        {
                            damageDictionary[damageInfo.Attacker] = totalDamage + damage;
                        }
                        else
                        {
                            damageDictionary.Add(damageInfo.Attacker, damage);
                        }
                    }

                    node = previous;
                }
            }
            finally
            {
                s_tickRangeCalculatorPool.Return(empShutdownCalculator);
            }
        }

        private void SetLagOut(MemberStats playerStats)
        {
            if (playerStats is null)
                return;

            if (playerStats.StartTime is not null)
            {
                playerStats.PlayTime += DateTime.UtcNow - playerStats.StartTime.Value;
                playerStats.StartTime = null;
            }

            playerStats.LagOuts++;
        }

        private void GetPlayersToNotify(MatchStats matchStats, HashSet<Player> players)
        {
            if (matchStats is null || players is null)
                return;

            IMatchData matchData = matchStats.MatchData;
            if (matchData is null)
                return;

            // Players in the match.
            foreach (ITeam team in matchData.Teams)
            {
                foreach (IPlayerSlot slot in team.Slots)
                {
                    if (slot.Player is not null)
                    {
                        players.Add(slot.Player);
                    }
                }
            }

            // Players in the arena and on the spec freq get notifications for all matches in the arena.
            // Players on a team freq get messages for the associated match (this includes a players that got subbed out).
            Arena arena = _arenaManager.FindArena(matchData.ArenaName);
            if (arena is not null)
            {
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena == arena // in the arena
                            && (player.Freq == arena.SpecFreq // on the spec freq
                                || matchStats.Teams.ContainsKey(player.Freq) // or on a team freq
                            ))
                        {
                            players.Add(player);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
        }

        private void PrintMatchStats(HashSet<Player> notifySet, MatchStats matchStats, MatchEndReason? reason, ITeam winnerTeam)
        {
            if (notifySet is null || matchStats is null)
                return;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                foreach (TeamStats teamStats in matchStats.Teams.Values)
                {
                    if (sb.Length > 0)
                        sb.Append('-');

                    sb.Append(teamStats.Team.Score);
                }

                if (reason is not null)
                {
                    switch (reason.Value)
                    {
                        case MatchEndReason.Decided:
                            if (winnerTeam is not null)
                            {
                                sb.Append($" Freq {winnerTeam.Freq}");
                            }
                            break;

                        case MatchEndReason.Draw:
                            sb.Append(" DRAW");
                            break;

                        case MatchEndReason.Aborted:
                            sb.Append(" CANCELLED");
                            break;
                    }
                }

                TimeSpan gameDuration = (matchStats.EndTimestamp ?? DateTime.UtcNow) - matchStats.StartTimestamp;
                sb.Append($" -- Game Time: ");
                sb.AppendFriendlyTimeSpan(gameDuration);

                if (matchStats.GameId is not null)
                {
                    sb.Append($" -- Game ID: {matchStats.GameId.Value}");
                }

                _chat.SendSetMessage(notifySet, $"{(reason is not null ? "Final" : "Current")} {matchStats.MatchData.MatchIdentifier.MatchType} Score: {sb}");
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            Span<char> playerName = stackalloc char[Constants.MaxPlayerNameLength];

            foreach (TeamStats teamStats in matchStats.Teams.Values)
            {
                SendHorizonalRule(notifySet);
                _chat.SendSetMessage(notifySet, $"| Freq {teamStats.Team.Freq,-4}            Ki/De TK SK AS FR WR WRk Mi LO PTime | DDealt/DTaken DmgE KiDmg FRDmg TmDmg TKDmg | AcB AcG |");
                SendHorizonalRule(notifySet);

                int totalKills = 0;
                int totalDeaths = 0;
                int totalTeamKills = 0;
                int totalSoloKills = 0;
                int totalAssists = 0;
                int totalForcedReps = 0;
                int totalWastedRepels = 0;
                int totalWastedRockets = 0;
                int totalLagOuts = 0;
                int totalDamageDealt = 0;
                int totalDamageTaken = 0;
                uint totalBombFireCount = 0;
                uint totalBombHitCount = 0;
                uint totalGunFireCount = 0;
                uint totalGunHitCount = 0;
                uint totalMineFireCount = 0;
                uint totalMineHitCount = 0;

                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    for (int memberIndex = 0; memberIndex < slotStats.Members.Count; memberIndex++)
                    {
                        MemberStats memberStats = slotStats.Members[memberIndex];

                        // Format the player name (add a space in front for subs, add trailing spaces).
                        ReadOnlySpan<char> name = memberStats.PlayerName;

                        if (memberIndex == 0)
                        {
                            // initial slot holder (no identation)
                            if (name.Length >= 20)
                                name = name[..20];

                            playerName.TryWrite($"{name,-20}", out _);
                        }
                        else
                        {
                            // sub, indent by 1 space
                            if (name.Length >= 19)
                                name = name[..19]; // truncate

                            playerName.TryWrite($" {name,-19}", out _);
                        }

                        // Calculations
                        int damageDealt = memberStats.DamageDealtBombs + memberStats.DamageDealtBullets;
                        int damageTaken = memberStats.DamageTakenBombs + memberStats.DamageTakenBullets + memberStats.DamageTakenTeam + memberStats.DamageSelf;
                        int totalDamage = damageDealt + damageTaken;
                        float? damageEfficiency = totalDamage > 0 ? (float)damageDealt / totalDamage : null;
                        uint bombMineFireCount = memberStats.BombFireCount + memberStats.MineFireCount;
                        uint bombMineHitCount = memberStats.BombHitCount + memberStats.MineHitCount;
                        float? bombAccuracy = bombMineFireCount > 0 ? (float)bombMineHitCount / bombMineFireCount * 100 : null;
                        float? gunAccuracy = memberStats.GunFireCount > 0 ? (float)memberStats.GunHitCount / memberStats.GunFireCount * 100 : null;

                        totalKills += memberStats.Kills;
                        totalDeaths += memberStats.Deaths;
                        totalTeamKills += memberStats.TeamKills;
                        totalSoloKills += memberStats.SoloKills;
                        totalAssists += memberStats.Assists;
                        totalForcedReps += memberStats.ForcedReps;
                        totalWastedRepels += memberStats.WastedRepels;
                        totalWastedRockets += memberStats.WastedRockets;
                        totalLagOuts += memberStats.LagOuts;
                        totalDamageDealt += damageDealt;
                        totalDamageTaken += damageTaken;
                        totalBombFireCount += memberStats.BombFireCount;
                        totalBombHitCount += memberStats.BombHitCount;
                        totalGunFireCount += memberStats.GunFireCount;
                        totalGunHitCount += memberStats.GunHitCount;
                        totalMineFireCount += memberStats.MineFireCount;
                        totalMineHitCount += memberStats.MineHitCount;

                        _chat.SendSetMessage(
                            notifySet,
                            $"| {playerName}" +
                            $" {memberStats.Kills,2}/{memberStats.Deaths,2}" +
                            $" {memberStats.TeamKills,2}" +
                            $" {memberStats.SoloKills,2}" +
                            $" {memberStats.Assists,2}" +
                            $" {memberStats.ForcedReps,2}" +
                            $" {memberStats.WastedRepels,2}" +
                            $" {memberStats.WastedRockets,3}" +
                            $" {memberStats.MineFireCount,2}" +
                            $" {memberStats.LagOuts,2}" +
                            $"{(int)memberStats.PlayTime.TotalMinutes,3}:{memberStats.PlayTime:ss}" +
                            $" | {damageDealt,6}/{damageTaken,6} {damageEfficiency,4:0%} {memberStats.KillDamage,5} {memberStats.ForcedRepDamage,5} {memberStats.DamageDealtTeam,5} {memberStats.TeamKillDamage,5}" +
                            $" | {bombAccuracy,3:N0} {gunAccuracy,3:N0} |");
                    }
                }

                int teamTotalDamage = totalDamageDealt + totalDamageTaken;
                float? totalDamageEfficiency = teamTotalDamage > 0 ? (float)totalDamageDealt / teamTotalDamage : null;
                uint totalBombMineFireCount = totalBombFireCount + totalMineFireCount;
                uint totalBombMineHitCount = totalBombHitCount + totalMineHitCount;
                float? totalBombAccuracy = totalBombMineFireCount > 0 ? (float)totalBombMineHitCount / totalBombMineFireCount * 100 : null;
                float? totalGunAccuracy = totalGunFireCount > 0 ? (float)totalGunHitCount / totalGunFireCount * 100 : null;

                _chat.SendSetMessage(
                    notifySet,
                    $"| TOTAL:              " +
                    $" {totalKills,2}/{totalDeaths,2}" +
                    $" {totalTeamKills,2}" +
                    $" {totalSoloKills,2}" +
                    $" {totalAssists,2}" +
                    $" {totalForcedReps,2}" +
                    $" {totalWastedRepels,2}" +
                    $" {totalWastedRockets,3}" +
                    $" {totalMineFireCount,2}" +
                    $" {totalLagOuts,2}" +
                    $"      " +
                    $" | {totalDamageDealt,6}/{totalDamageTaken,6} {totalDamageEfficiency,4:0%}                        " +
                    $" | {totalBombAccuracy,3:N0} {totalGunAccuracy,3:N0} |");
            }

            SendHorizonalRule(notifySet);


            void SendHorizonalRule(HashSet<Player> notifySet)
            {
                _chat.SendSetMessage(notifySet, $"+-----------------------------------------------------------+--------------------------------------------+---------+");
            }
        }

        private void ResetMatchStats(MatchStats matchStats)
        {
            // Clear the existing object.
            foreach (TeamStats teamStats in matchStats.Teams.Values)
            {
                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    foreach (MemberStats memberStats in slotStats.Members)
                    {
                        LinkedListNode<DamageInfo> node;
                        while ((node = memberStats.RecentDamageTaken.First) is not null)
                        {
                            memberStats.RecentDamageTaken.Remove(node);
                            s_damageInfoLinkedListNodePool.Return(node);
                        }

                        memberStats.Reset();

                        // TODO: return memberStats to a pool
                    }

                    slotStats.Reset();

                    // TODO: return slotStats to a pool
                }

                teamStats.Reset();

                // TODO: return teamStats to a pool
            }

            matchStats.Reset();
        }

        #region Helper types

        private class MatchStats
        {
            public IMatchData MatchData { get; private set; }

            /// <summary>
            /// Key = freq
            /// </summary>
            public readonly SortedList<short, TeamStats> Teams = new();

            public DateTime StartTimestamp;
            public DateTime? EndTimestamp;
            public long? GameId;

            private MemoryStream _eventsJsonStream;
            private Utf8JsonWriter _eventsJsonWriter;

            public void Initialize(IMatchData matchData)
            {
                Reset();

                MatchData = matchData ?? throw new ArgumentNullException(nameof(matchData));

                _eventsJsonStream = s_recyclableMemoryStreamManager.GetStream();
                _eventsJsonWriter = new Utf8JsonWriter(_eventsJsonStream);
                _eventsJsonWriter.WriteStartArray();
            }

            public void Reset()
            {
                MatchData = null;
                Teams.Clear();
                StartTimestamp = DateTime.MinValue;
                EndTimestamp = null;
                GameId = null;

                if (_eventsJsonStream is not null)
                {
                    _eventsJsonStream.Dispose();
                    _eventsJsonStream = null;
                }

                if (_eventsJsonWriter is not null)
                {
                    _eventsJsonWriter.Dispose();
                    _eventsJsonWriter = null;
                }
            }

            public void AddAssignSlotEvent(DateTime timestamp, short freq, int slotIdx, string playerName)
            {
                if (_eventsJsonWriter is null)
                    return;

                _eventsJsonWriter.WriteStartObject();
                _eventsJsonWriter.WriteNumber("event_type_id"u8, (int)GameEventType.TeamVersus_AssignSlot);
                _eventsJsonWriter.WriteString("timestamp"u8, timestamp);
                _eventsJsonWriter.WriteNumber("freq"u8, freq);
                _eventsJsonWriter.WriteNumber("slot_idx"u8, slotIdx);
                _eventsJsonWriter.WriteString("player"u8, playerName);
                _eventsJsonWriter.WriteEndObject();
            }

            public void AddShipChangeEvent(DateTime timestamp, string playerName, ShipType ship)
            {
                if (_eventsJsonWriter is null)
                    return;

                _eventsJsonWriter.WriteStartObject();
                _eventsJsonWriter.WriteNumber("event_type_id"u8, (int)GameEventType.TeamVersus_PlayerShipChange);
                _eventsJsonWriter.WriteString("timestamp"u8, timestamp);
                _eventsJsonWriter.WriteString("player"u8, playerName);
                _eventsJsonWriter.WriteNumber("ship"u8, (int)ship);
                _eventsJsonWriter.WriteEndObject();
            }

            public void AddUseItemEvent(
                DateTime timestamp, 
                string playerName, 
                ShipItem item, 
                List<(string PlayerName, int Damage)> damageList)
            {
                if (_eventsJsonWriter is null)
                    return;

                _eventsJsonWriter.WriteStartObject();
                _eventsJsonWriter.WriteNumber("event_type_id"u8, (int)GameEventType.TeamVersus_PlayerUseItem);
                _eventsJsonWriter.WriteString("timestamp"u8, timestamp);
                _eventsJsonWriter.WriteString("player"u8, playerName);
                _eventsJsonWriter.WriteNumber("ship_item_id"u8, (int)item);

                if (damageList is not null)
                {
                    _eventsJsonWriter.WriteStartArray("damage_stats");
                    foreach ((string attacker, int damage) in damageList)
                    {
                        _eventsJsonWriter.WriteStartObject();
                        _eventsJsonWriter.WriteString("player"u8, attacker);
                        _eventsJsonWriter.WriteNumber("damage"u8, damage);
                        _eventsJsonWriter.WriteEndObject();
                    }
                    _eventsJsonWriter.WriteEndArray();
                }

                _eventsJsonWriter.WriteEndObject();
            }

            public void AddKillEvent(
                DateTime timestamp, 
                string killedName, 
                ShipType killedShip,
                string killerName, 
                ShipType killerShip,
                bool isKnockout,
                short xCoord,
                short yCoord,
                List<(string PlayerName, int Damage)> damageList)
            {
                if (_eventsJsonWriter is null)
                    return;

                _eventsJsonWriter.WriteStartObject();
                _eventsJsonWriter.WriteNumber("event_type_id"u8, (int)GameEventType.TeamVersus_PlayerKill);
                _eventsJsonWriter.WriteString("timestamp"u8, timestamp);
                _eventsJsonWriter.WriteString("killed_player"u8, killedName);
                _eventsJsonWriter.WriteString("killer_player"u8, killerName);
                _eventsJsonWriter.WriteBoolean("is_knockout"u8, isKnockout);
                _eventsJsonWriter.WriteNumber("x_coord"u8, xCoord);
                _eventsJsonWriter.WriteNumber("y_coord"u8, yCoord);
                _eventsJsonWriter.WriteNumber("killed_ship"u8, (int)killedShip);
                _eventsJsonWriter.WriteNumber("killer_ship"u8, (int)killerShip);

                _eventsJsonWriter.WriteStartArray("score");
                foreach (var team in Teams.Values)
                {
                    _eventsJsonWriter.WriteNumberValue(team.Team.Score);
                }
                _eventsJsonWriter.WriteEndArray();

                if (damageList is not null)
                {
                    _eventsJsonWriter.WriteStartArray("damage_stats");
                    foreach ((string playerName, int damage) in damageList)
                    {
                        _eventsJsonWriter.WriteStartObject();
                        _eventsJsonWriter.WriteString("player"u8, playerName);
                        _eventsJsonWriter.WriteNumber("damage"u8, damage);
                        _eventsJsonWriter.WriteEndObject();
                    }
                    _eventsJsonWriter.WriteEndArray();
                }

                // TODO:
                //_eventsJsonWriter.WriteStartArray("rating_changes");
                //_eventsJsonWriter.WriteEndArray();

                _eventsJsonWriter.WriteEndObject();
            }

            public void WriteEventsArray(Utf8JsonWriter writer)
            {
                _eventsJsonWriter.WriteEndArray();
                _eventsJsonWriter.Flush();

                _eventsJsonStream.Position = 0;

                if (_eventsJsonStream is RecyclableMemoryStream rms)
                {
                    foreach (var memory in rms.GetReadOnlySequence())
                    {
                        writer.WriteRawValue(memory.Span, true);
                    }
                }
                else
                {
                    writer.WriteNullValue();
                }
            }
        }

        private class TeamStats
        {
            public MatchStats MatchStats { get; private set; }
            public ITeam Team { get; private set; }

            /// <summary>
            /// Player slots
            /// e.g. in a 4v4 match, there would be 4 slots. 
            /// </summary>
            public readonly List<SlotStats> Slots = new();

            public void Initialize(MatchStats matchStats, ITeam team)
            {
                MatchStats = matchStats ?? throw new ArgumentNullException(nameof(matchStats));
                Team = team ?? throw new ArgumentNullException(nameof(team));
            }

            public void Reset()
            {
                MatchStats = null;
                Team = null;
                Slots.Clear();
            }
        }

        private class SlotStats
        {
            public MatchStats MatchStats => TeamStats?.MatchStats;
            public TeamStats TeamStats { get; private set; }
            public IPlayerSlot Slot { get; private set; }

            /// <summary>
            /// Stats for each player that occupied the slot.
            /// </summary>
            public readonly List<MemberStats> Members = new();

            /// <summary>
            /// Stats of the player that currently holds the slot.
            /// </summary>
            public MemberStats Current;

            public void Initialize(TeamStats teamStats, IPlayerSlot slot)
            {
                TeamStats = teamStats ?? throw new ArgumentNullException(nameof(teamStats));
                Slot = slot ?? throw new ArgumentNullException(nameof(slot));
            }

            public void Reset()
            {
                TeamStats = null;
                Slot = null;
                Members.Clear();
                Current = null;
            }
        }

        private class MemberStats
        {
            public MatchStats MatchStats => TeamStats?.MatchStats;
            public TeamStats TeamStats => SlotStats?.TeamStats;
            public SlotStats SlotStats { get; private set; }

            public bool IsCurrent => SlotStats?.Current == this;

            public string PlayerName { get; private set; }

            //public Player Player; // TODO: keep track of player so that we can send notifications to even those that are no longer the current slot holder?

            #region Ship Usage

            public readonly TimeSpan[] ShipUsage = new TimeSpan[8];

            public ShipType? CurrentShip = null;
            public DateTime? CurrentShipStartTime = null;

            #endregion

            #region Damage

            /// <summary>
            /// Amount of damage taken from enemy bullets, including bursts.
            /// </summary>
            public int DamageTakenBullets;

            /// <summary>
            /// Amount of damage taken from enemy bombs, mines, shrapnel, or thors.
            /// </summary>
            /// <remarks>
            /// This does not include damage from teammates (see <see cref="DamageTakenTeam"/>) or self damage (see <see cref="DamageSelf"/>).
            /// </remarks>
            public int DamageTakenBombs;

            /// <summary>
            /// Amount of damage taken from teammates.
            /// </summary>
            public int DamageTakenTeam;

            /// <summary>
            /// Amount of damage dealt to enemies with bullets, including bursts.
            /// </summary>
            public int DamageDealtBullets;

            /// <summary>
            /// Amount of damage dealt to enemies with bombs, mines, shrapnel, or thors.
            /// </summary>
            /// <remarks>
            /// This does not include damage to teammates (see <see cref="DamageDealtTeam"/>) or self damage (see <see cref="DamageSelf"/>).
            /// </remarks>
            public int DamageDealtBombs;

            /// <summary>
            /// Amount of damage dealt to teammates.
            /// </summary>
            public int DamageDealtTeam;

            /// <summary>
            /// Amount of self damage.
            /// </summary>
            public int DamageSelf;

            /// <summary>
            /// Amount of damage attributed to an enemy being killed.
            /// Damage dealt to an enemy decays based on their recharge.
            /// This may give a better picture than kills and assists combined?
            /// </summary>
            public int KillDamage;

            /// <summary>
            /// Amount of damage done to teammates that were killed.
            /// </summary>
            public int TeamKillDamage;

            /// <summary>
            /// Amount of damage done to an enemy prior to the enemy using a repel.
            /// </summary>
            public int ForcedRepDamage;

            /// <summary>
            /// Recent damage taken in order from oldest to newest.
            /// Used upon death to calculate how much <see cref="KillDamage"/> or <see cref="TeamKillDamage"/> to give to the attacker(s).
            /// </summary>
            public readonly LinkedList<DamageInfo> RecentDamageTaken = new();

            #endregion

            #region Accuracy

            /// <summary>
            /// The # of guns fired.
            /// </summary>
            public uint GunFireCount;

            /// <summary>
            /// The # of bombs fired.
            /// </summary>
            public uint BombFireCount;

            /// <summary>
            /// The # of mines fired.
            /// </summary>
            public uint MineFireCount;

            /// <summary>
            /// The # of hits made on enemies with bullets.
            /// </summary>
            public uint GunHitCount;

            /// <summary>
            /// The # of hits made on enemies with bombs.
            /// </summary>
            public uint BombHitCount;

            /// <summary>
            /// The # of hits made on enemies with mines.
            /// </summary>
            public uint MineHitCount;

            #endregion

            #region Wasted items (died without using)

            public short WastedRepels;
            public short WastedRockets;
            public short WastedThors;
            public short WastedBursts;
            public short WastedDecoys;
            public short WastedPortals;
            public short WastedBricks;

            #endregion

            public short Kills;
            public short SoloKills;
            public short TeamKills;
            public short Deaths;
            public short Assists;
            public short ForcedReps;

            public TimeSpan PlayTime;

            public short LagOuts;

            /// <summary>
            /// Timestamp the player last started playing for the team.
            /// </summary>
            /// <remarks>
            /// This is set to the current time when a player initially ship changes into a ship.
            /// When the player stops playing (changes to spec, leaves the arena, or disconnects), 
            /// this is used to calculate <see cref="PlayTime"/>, and then cleared (set to null).
            /// </remarks>
            public DateTime? StartTime;

            public void Initialize(SlotStats slotStats, string playerName)
            {
                ArgumentException.ThrowIfNullOrEmpty(playerName);

                SlotStats = slotStats ?? throw new ArgumentNullException(nameof(slotStats));
                PlayerName = playerName;

                for (int shipIndex = 0; shipIndex < ShipUsage.Length; shipIndex++)
                {
                    ShipUsage[shipIndex] = TimeSpan.Zero;
                }
            }

            public void RemoveOldRecentDamage(short maximumEnergy, short rechargeRate)
            {
                // How many ticks it takes for the player's ship to reach full energy from empty (maximum energy and recharge rate assumed).
                uint fullEnergyTicks = (uint)float.Ceiling(maximumEnergy * 1000f / rechargeRate);

                // Remove nodes that are too old to be relevant.
                ServerTick cutoff = ServerTick.Now - fullEnergyTicks;
                LinkedListNode<DamageInfo> node = RecentDamageTaken.First;
                while (node is not null)
                {
                    if (node.ValueRef.Timestamp + node.ValueRef.EmpBombShutdownTicks < cutoff)
                    {
                        // The node represents damage taken from too long ago.
                        // Discard the node and continue with the next node.
                        LinkedListNode<DamageInfo> next = node.Next;
                        RecentDamageTaken.Remove(node);
                        s_damageInfoLinkedListNodePool.Return(node);
                        node = next;
                    }
                    else
                    {
                        // The node is in the valid time span.
                        break;
                    }
                }
            }

            public void ClearRecentDamage()
            {
                LinkedListNode<DamageInfo> node;
                while ((node = RecentDamageTaken.First) is not null)
                {
                    RecentDamageTaken.Remove(node);
                    s_damageInfoLinkedListNodePool.Return(node);
                }
            }

            public void Reset()
            {
                SlotStats = null;
                PlayerName = null;

                // ship usage
                CurrentShip = null;
                CurrentShipStartTime = null;

                // damage fields
                DamageTakenBullets = 0;
                DamageTakenBombs = 0;
                DamageDealtBullets = 0;
                DamageDealtBombs = 0;
                DamageDealtTeam = 0;
                DamageSelf = 0;
                KillDamage = 0;
                TeamKillDamage = 0;
                ClearRecentDamage();

                // accuracy fields
                GunFireCount = 0;
                BombFireCount = 0;
                MineFireCount = 0;
                GunHitCount = 0;
                BombHitCount = 0;
                MineHitCount = 0;

                // items
                WastedRepels = 0;
                WastedRockets = 0;
                WastedThors = 0;
                WastedBursts = 0;
                WastedDecoys = 0;
                WastedPortals = 0;
                WastedBricks = 0;

                // other
                Kills = 0;
                SoloKills = 0;
                TeamKills = 0;
                Deaths = 0;
                Assists = 0;
                ForcedReps = 0;
                PlayTime = TimeSpan.Zero;
                LagOuts = 0;
                StartTime = null;
            }
        }

        private class PlayerData : IPooledExtraData
        {
            /// <summary>
            /// The player's stats of the match in progress that the player is a current slot holder in.
            /// <see langword="null"/> is not currently the holder of a slot in a match.
            /// </summary>
            public MemberStats MemberStats;

            /// <summary>
            /// Whether damage is being watched for the player via IWatchDamage.
            /// </summary>
            public bool IsWatchingDamage = false;

            public void Reset()
            {
                MemberStats = null;
                IsWatchingDamage = false;
            }
        }

        private readonly struct PlayerTeamSlot : IEquatable<PlayerTeamSlot>
        {
            public readonly string PlayerName { get; }
            public readonly short Freq { get; }
            public readonly int SlotIdx { get; }

            public PlayerTeamSlot(string playerName, short freq, int slotIdx)
            {
                if (string.IsNullOrEmpty(playerName))
                    ArgumentException.ThrowIfNullOrEmpty(playerName);

                PlayerName = playerName;
                Freq = freq;
                SlotIdx = slotIdx;
            }

            public override bool Equals([NotNullWhen(true)] object obj)
            {
                return base.Equals(obj);
            }

            public bool Equals(PlayerTeamSlot other)
            {
                return string.Equals(PlayerName, other.PlayerName, StringComparison.OrdinalIgnoreCase)
                    && Freq == other.Freq
                    && SlotIdx == other.SlotIdx;
            }

            public override int GetHashCode()
            {
                return PlayerName.GetHashCode();
            }
        }

        private struct DamageInfo
        {
            /// <summary>
            /// When the damage was inflicted.
            /// </summary>
            public required ServerTick Timestamp;

            /// <summary>
            /// The amount of direct damage dealt.
            /// </summary>
            public required short Damage;

            /// <summary>
            /// The amount of time recharge is stopped due to an EMP bomb/mine. 0 for everything else.
            /// </summary>
            public required uint EmpBombShutdownTicks;

            /// <summary>
            /// Identifies who (player, team, and slot) inflicted the damage.
            /// This can be damage taken from an enemy, from an teammate, or self damage.
            /// </summary>
            public required PlayerTeamSlot Attacker;
        }

        private struct ShipClientSettingIdentifiers
        {
            /// <summary>
            /// All:MaximumRecharge - Amount of energy recharge in 10 seconds.
            /// </summary>
            public required ClientSettingIdentifier MaximumRechargeId;

            /// <summary>
            /// All:MaximumEnergy - Maximum amount of energy that a ship can store.
            /// </summary>
            public required ClientSettingIdentifier MaximumEnergyId;

            /// <summary>
            /// All:EmpBomb
            /// </summary>
            public required ClientSettingIdentifier EmpBombId;
        }

        private class ArenaSettings
        {
            /// <summary>
            /// Settings for each ship.
            /// </summary>
            public readonly ShipSettings[] ShipSettings = new ShipSettings[8];

            /// <summary>
            /// Bomb:BombDamageLevel - Amount of damage a bomb causes at its center point (for all bomb levels) 
            /// </summary>
            public int BombDamageLevel;

            /// <summary>
            /// Bomb:EBombShutdownTime - Maximum time recharge is stopped on players hit with an EMP bomb
            /// </summary>
            public uint EmpBombShutdownTime;

            /// <summary>
            /// Bomb:EBombDamagePercent - Percentage of normal damage applied to an EMP bomb (in 0.1%)
            /// 0=0% 1000=100% 2000=200%
            /// This value is the ratio, so EBombDamagePercent / 1000.
            /// </summary>
            public float EmpBombDamageRatio;
        }

        /// <summary>
        /// Ship settings that are used to calculate stats.
        /// </summary>
        private struct ShipSettings
        {
            /// <summary>
            /// All:MaximumRecharge - Amount of energy recharge in 10 seconds.
            /// </summary>
            public required short MaximumRecharge;

            /// <summary>
            /// All:MaximumEnergy - Maximum amount of energy that a ship can store.
            /// </summary>
            public required short MaximumEnergy;

            /// <summary>
            /// All:EmpBomb
            /// </summary>
            public required bool HasEmpBomb;
        }

        #endregion

        #region Pooled object policies

        private class DamageInfoLinkedListNodePooledObjectPolicy : IPooledObjectPolicy<LinkedListNode<DamageInfo>>
        {
            public LinkedListNode<DamageInfo> Create()
            {
                return new LinkedListNode<DamageInfo>(default);
            }

            public bool Return(LinkedListNode<DamageInfo> obj)
            {
                if (obj is null)
                    return false;

                if (obj.List is not null)
                    return false;

                obj.Value = default;
                return true;
            }
        }

        private class TickRangeCalcualtorPooledObjectPolicy : IPooledObjectPolicy<TickRangeCalculator>
        {
            public TickRangeCalculator Create()
            {
                return new TickRangeCalculator();
            }

            public bool Return(TickRangeCalculator obj)
            {
                if (obj is null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        private class DamageDictionaryPooledObjectPolicy : IPooledObjectPolicy<Dictionary<PlayerTeamSlot, int>>
        {
            public Dictionary<PlayerTeamSlot, int> Create()
            {
                return new Dictionary<PlayerTeamSlot, int>();
            }

            public bool Return(Dictionary<PlayerTeamSlot, int> obj)
            {
                if (obj is null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        private class DamageListPooledObjectPolicy : IPooledObjectPolicy<List<(string PlayerName, int Damage)>>
        {
            public List<(string PlayerName, int Damage)> Create()
            {
                return new List<(string PlayerName, int Damage)>();
            }

            public bool Return(List<(string PlayerName, int Damage)> obj)
            {
                if (obj is null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        #endregion
    }
}

