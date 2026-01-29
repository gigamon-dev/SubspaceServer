using Microsoft.Extensions.ObjectPool;
using Microsoft.IO;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.Json;
using SS.Utilities.ObjectPool;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    public sealed class TeamVersusStats : IModule, IArenaAttachableModule, ITeamVersusStatsBehavior, ILeagueHelp
    {
        #region Static members

        private static readonly RecyclableMemoryStreamManager s_recyclableMemoryStreamManager = new();
        private static readonly ObjectPool<LinkedListNode<DamageInfo>> s_damageInfoLinkedListNodePool;
        private static readonly ObjectPool<LinkedListNode<WeaponUse>> s_weaponUseLinkedListNodePool;
        private static readonly ObjectPool<TickRangeCalculator> s_tickRangeCalculatorPool;
        private static readonly ObjectPool<Dictionary<PlayerTeamSlot, int>> s_damageDictionaryPool;
        private static readonly ObjectPool<List<(string PlayerName, int Damage)>> s_damageListPool;
        private static readonly ObjectPool<Dictionary<string, float>> s_ratingChangeDictionaryPool;
        //private readonly ObjectPool<MatchStats> _matchStatsObjectPool = new NonTransientObjectPool<MatchStats>(new MatchStatsPooledObjectPolicy());

        static TeamVersusStats()
        {
            var provider = new DefaultObjectPoolProvider();
            s_damageInfoLinkedListNodePool = new DefaultObjectPool<LinkedListNode<DamageInfo>>(new LinkedListNodePooledObjectPolicy<DamageInfo>(), Constants.TargetPlayerCount * 256);
            s_weaponUseLinkedListNodePool = new DefaultObjectPool<LinkedListNode<WeaponUse>>(new LinkedListNodePooledObjectPolicy<WeaponUse>(), Constants.TargetPlayerCount * 256);
            s_tickRangeCalculatorPool = provider.Create(new TickRangeCalculatorPooledObjectPolicy());
            s_damageDictionaryPool = provider.Create(new DictionaryPooledObjectPolicy<PlayerTeamSlot, int>() { InitialCapacity = Constants.TargetPlayerCount });
            s_damageListPool = provider.Create(new ListPooledObjectPolicy<(string PlayerName, int Damage)>() { InitialCapacity = Constants.TargetPlayerCount });
            s_ratingChangeDictionaryPool = provider.Create(new DictionaryPooledObjectPolicy<string, float>() { InitialCapacity = Constants.TargetPlayerCount, EqualityComparer = StringComparer.OrdinalIgnoreCase });
        }

        #endregion

        private const int DefaultRating = 500;
        private const int MinimumRating = 100;

        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly IClientSettings _clientSettings;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IMapData _mapData;
        private readonly IMatchFocus _matchFocus;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IWatchDamage _watchDamage;

        // optional
        private IGameStatsRepository? _gameStatsRepository;
        private ILeagueRepository? _leagueRepository;

        private InterfaceRegistrationToken<ITeamVersusStatsBehavior>? _iTeamVersusStatsBehaviorToken;

        private PlayerDataKey<PlayerData> _pdKey;

        private string? _zoneServerName;
        private string? _subspaceStatsWebUrl;

        #region Client Setting Identifiers

        private ClientSettingIdentifier _bombDamageLevelClientSettingId;
        private ClientSettingIdentifier _eBombShutdownTimeClientSettingId;
        private ClientSettingIdentifier _eBombDamagePercentClientSettingId;
        private readonly ShipClientSettingIdentifiers[] _shipClientSettingIds = new ShipClientSettingIdentifiers[8];

        #endregion

        /// <summary>
        /// Map (.lvl file) info by base arena name.
        /// </summary>
        /// <remarks>
        /// Key: base arena name
        /// </remarks>
        private readonly Dictionary<string, (string LvlFilename, uint Checksum)> _arenaMapInfo = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string LvlFilename, uint Checksum)>.AlternateLookup<ReadOnlySpan<char>> _arenaMapInfoLookup;

		/// <summary>
		/// Arena settings by base arena name.
		/// </summary>
		/// <remarks>
		/// Key: base arena name
		/// </remarks>
		private readonly Dictionary<string, ArenaSettings> _arenaSettings = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ArenaSettings>.AlternateLookup<ReadOnlySpan<char>> _arenaSettingsLookup;

		/// <summary>
		/// Dictionary of <see cref="MatchStats"/> by <see cref="MatchIdentifier"/> for matches that are currently in progress.
		/// </summary>
		private readonly Dictionary<MatchIdentifier, MatchStats> _matchStatsDictionary = [];

        /// <summary>
        /// Dictionary that links a player (by player name) to their <see cref="MemberStats"/> of the match currently in progress that they are assigned a slot in.
        /// When a player is subbed out, they will no longer have a record in this collection.
        /// </summary>
        /// <remarks>
        /// Key: player name
        /// </remarks>
        private readonly Dictionary<string, MemberStats> _playerMemberDictionary = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<Arena, ArenaData> _arenaDataDictionary = [];

        private readonly DefaultObjectPool<ArenaData> _arenaDataPool = new(new DefaultPooledObjectPolicy<ArenaData>(), Constants.TargetArenaCount);

        public TeamVersusStats(
            IArenaManager arenaManager,
            IChat chat,
            IClientSettings clientSettings,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            IMapData mapData,
            IMatchFocus matchFocus,
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
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _matchFocus = matchFocus ?? throw new ArgumentNullException(nameof(matchFocus));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _watchDamage = watchDamage ?? throw new ArgumentNullException(nameof(watchDamage));

            _arenaMapInfoLookup = _arenaMapInfo.GetAlternateLookup<ReadOnlySpan<char>>();
            _arenaSettingsLookup = _arenaSettings.GetAlternateLookup<ReadOnlySpan<char>>();
		}

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
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

                if (!_clientSettings.TryGetSettingsIdentifier(shipNames[shipIndex], "CloakEnergy", out _shipClientSettingIds[shipIndex].CloakEnergyId))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), $"Unable to get client setting identifier for {shipNames[shipIndex]}:CloakEnergy.");
                    return false;
                }

                if (!_clientSettings.TryGetSettingsIdentifier(shipNames[shipIndex], "StealthEnergy", out _shipClientSettingIds[shipIndex].StealthEnergyId))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), $"Unable to get client setting identifier for {shipNames[shipIndex]}:StealthEnergy.");
                    return false;
                }

                if (!_clientSettings.TryGetSettingsIdentifier(shipNames[shipIndex], "XRadarEnergy", out _shipClientSettingIds[shipIndex].XRadarEnergyId))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), $"Unable to get client setting identifier for {shipNames[shipIndex]}:XRadarEnergy.");
                    return false;
                }

                if (!_clientSettings.TryGetSettingsIdentifier(shipNames[shipIndex], "AntiWarpEnergy", out _shipClientSettingIds[shipIndex].AntiWarpEnergyId))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), $"Unable to get client setting identifier for {shipNames[shipIndex]}:AntiWarpEnergy.");
                    return false;
                }
            }

            // Try to get the optional service for saving stats to a database.
            _gameStatsRepository = broker.GetInterface<IGameStatsRepository>();
            _leagueRepository = broker.GetInterface<ILeagueRepository>();

            if (_gameStatsRepository is not null || _leagueRepository is not null)
            {
                // We got the optional service. To use it, we'll need the server name.
                _zoneServerName = _configManager.GetStr(_configManager.Global, "Billing", "ServerName");

                if (string.IsNullOrWhiteSpace(_zoneServerName))
                {
                    _logManager.LogM(LogLevel.Error, nameof(TeamVersusStats), "Missing setting, global.conf: Billing.ServerName");

                    broker.ReleaseInterface(ref _gameStatsRepository);
                    broker.ReleaseInterface(ref _leagueRepository);
                    return false;
                }

                // Optionally, a URL to the subspace-stats-web site can be configured.
                _subspaceStatsWebUrl = _configManager.GetStr(_configManager.Global, "SS.Matchmaking", "SubspaceStatsWebUrl")?.TrimEnd('/');
            }

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            // Registered globally, instead of on attached arenas only, since matches can end after an arena is destroyed.
            _iTeamVersusStatsBehaviorToken = broker.RegisterInterface<ITeamVersusStatsBehavior>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            broker.UnregisterInterface(ref _iTeamVersusStatsBehaviorToken);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);

            _playerData.FreePlayerData(ref _pdKey);

            if (_gameStatsRepository is not null)
                broker.ReleaseInterface(ref _gameStatsRepository);

            if (_leagueRepository is not null)
                broker.ReleaseInterface(ref _leagueRepository);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            ArenaData arenaData = _arenaDataPool.Get();
            _arenaDataDictionary.Add(arena, arenaData);

            TeamVersusMatchPlayerLagOutCallback.Register(arena, Callback_TeamVersusMatchPlayerLagOut);
            TeamVersusMatchPlayerShipChangedCallback.Register(arena, Callback_TeamVersusMatchPlayerShipChanged);
            TeamVersusMatchPlayerSubbedCallback.Register(arena, Callback_TeamVersusMatchPlayerSubbed);
            BricksPlacedCallback.Register(arena, Callback_BricksPlaced);
            PlayerDamageCallback.Register(arena, Callback_PlayerDamage);
            PlayerPositionPacketCallback.Register(arena, Callback_PlayerPositionPacket);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            SpawnCallback.Register(arena, Callback_Spawn);

            _commandManager.AddCommand("chart", Command_chart, arena);

            arenaData.ILeagueHelpToken = arena.RegisterInterface<ILeagueHelp>(this, nameof(TeamVersusStats));

            return true;
        }
        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!_arenaDataDictionary.Remove(arena, out ArenaData? arenaData))
                return false;

            try
            {
                if (arena.UnregisterInterface(ref arenaData.ILeagueHelpToken) != 0)
                    return false;
            }
            finally
            {
                _arenaDataPool.Return(arenaData);
            }

            _commandManager.RemoveCommand("chart", Command_chart, arena);

            TeamVersusMatchPlayerLagOutCallback.Unregister(arena, Callback_TeamVersusMatchPlayerLagOut);
            TeamVersusMatchPlayerShipChangedCallback.Unregister(arena, Callback_TeamVersusMatchPlayerShipChanged);
            TeamVersusMatchPlayerSubbedCallback.Unregister(arena, Callback_TeamVersusMatchPlayerSubbed);
            BricksPlacedCallback.Unregister(arena, Callback_BricksPlaced);
            PlayerDamageCallback.Unregister(arena, Callback_PlayerDamage);
            PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPositionPacket);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            SpawnCallback.Unregister(arena, Callback_Spawn);

            return true;
        }

        #endregion

        #region ITeamVersusStatsBehavior

        async Task<bool> ITeamVersusStatsBehavior.BalanceTeamsAsync(IMatchConfiguration matchConfiguration, IReadOnlyList<TeamLineup> teamList)
        {
            if (matchConfiguration is null)
                return false;

            if (matchConfiguration.GameTypeId is null)
                return false;

            if (teamList.Count < 2)
                return false;

            if (_gameStatsRepository is null)
                return false;

            // Get ratings of each player.
            Dictionary<string, int> playerRatingDictionary = new(StringComparer.OrdinalIgnoreCase); // TODO: pool

            foreach (TeamLineup teamLineup in teamList)
            {
                foreach ((string playerName, _) in teamLineup.Players)
                {
                    playerRatingDictionary[playerName] = DefaultRating;
                }
            }

            await _gameStatsRepository.GetPlayerRatingsAsync(matchConfiguration.GameTypeId.Value, playerRatingDictionary);

            // Create a list of player ratings sorted by rating.
            List<(string PlayerName, int Rating)> playerRatingList = []; // TODO: pool
            foreach ((string playerName, int rating) in playerRatingDictionary)
            {
                playerRatingList.Add((playerName, rating));
            }

            playerRatingList.Sort(PlayerRatingComparison);

            // Clear the existing teams so that we can reassign all the players.
            foreach (TeamLineup teamLineup in teamList)
            {
                teamLineup.Players.Clear();
            }

            // Assign players to teams snaking back and forth.
            // This should give a decent distribution of skill (by rating).
            bool ascending = true;
            int playerIndex = 0;
            int teamIndex = 0;
            while (playerIndex < playerRatingList.Count)
            {
                teamList[teamIndex].Players.Add(playerRatingList[playerIndex++].PlayerName, null);

                if (ascending)
                {
                    if (teamIndex == teamList.Count - 1)
                    {
                        ascending = false;
                    }
                    else
                    {
                        teamIndex++;
                    }
                }
                else
                {
                    if (teamIndex == 0)
                    {
                        ascending = true;
                    }
                    else
                    {
                        teamIndex--;
                    }
                }

            }

            return true;


            static int PlayerRatingComparison((string PlayerName, int Rating) x, (string PlayerName, int Rating) y)
            {
                return -x.Rating.CompareTo(y.Rating); // rating desc
            }
        }

        async Task ITeamVersusStatsBehavior.InitializeAsync(IMatchData matchData)
        {
            //
            // Initialize the stats object model for the match
            //

            // Match
            if (_matchStatsDictionary.TryGetValue(matchData.MatchIdentifier, out MatchStats? matchStats))
            {
                ResetMatchStats(matchStats);
            }
            else
            {
                matchStats = new(); // TODO: get from a pool
                _matchStatsDictionary.Add(matchData.MatchIdentifier, matchStats);
            }

            matchStats.Initialize(matchData);

            Dictionary<string, int> playerRatingDictionary = new(StringComparer.OrdinalIgnoreCase); // TOOD: pool

            for (int teamIdx = 0; teamIdx < matchData.Teams.Count; teamIdx++)
            {
                // Team
                ITeam team = matchData.Teams[teamIdx];
                TeamStats teamStats = new(); // TODO: get from a pool
                teamStats.Initialize(matchStats, team);
                matchStats.Teams.Add(team.Freq, teamStats);

                for (int slotIdx = 0; slotIdx < team.Slots.Count; slotIdx++)
                {
                    // Slot
                    IPlayerSlot slot = team.Slots[slotIdx];
                    SlotStats slotStats = new(); // TODO: get from a pool
                    slotStats.Initialize(teamStats, slot);
                    teamStats.Slots.Add(slotStats);

                    // Member (only if the slot is filled)
                    if (!string.IsNullOrWhiteSpace(slot.PlayerName))
                    {
                        MemberStats memberStats = new(); // TODO: get from a pool
                        memberStats.Initialize(slotStats, slot.PlayerName, true);
                        slotStats.Members.Add(memberStats);
                        slotStats.Current = memberStats;

                        _playerMemberDictionary[slot.PlayerName] = memberStats;
                        playerRatingDictionary.Add(slot.PlayerName, DefaultRating);
                    }
                }
            }

            //
            // Initialize the ratings for each team member
            //

            if (_gameStatsRepository is not null && matchData.Configuration.GameTypeId is not null)
            {
                await _gameStatsRepository.GetPlayerRatingsAsync(matchData.Configuration.GameTypeId.Value, playerRatingDictionary);
            }

            foreach (TeamStats teamStats in matchStats.Teams.Values)
            {
                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    foreach (MemberStats memberStats in slotStats.Members)
                    {
                        if (!playerRatingDictionary.TryGetValue(memberStats.PlayerName!, out int rating))
                        {
                            rating = DefaultRating;
                        }

                        memberStats.InitialRating = rating;
                    }
                }
            }
        }

        Task ITeamVersusStatsBehavior.MatchStartedAsync(IMatchData matchData)
        {
            if (!_matchStatsDictionary.TryGetValue(matchData.MatchIdentifier, out MatchStats? matchStats))
                return Task.CompletedTask;

            matchStats.StartTimestamp = matchData.Started!.Value;

            foreach (TeamStats teamStats in matchStats.Teams.Values)
            {
                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    foreach (MemberStats memberStats in slotStats.Members)
                    {
                        IPlayerSlot slot = slotStats.Slot!;
                        Player? player = slot.Player ?? _playerData.FindPlayer(memberStats.PlayerName);
                        if (player is not null)
                        {
                            if (player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                            {
                                playerData.MemberStats = memberStats;
                            }

                            if (player.Arena is not null
                                && player.Arena == matchData.Arena
                                && player.Freq == teamStats.Team?.Freq
                                && player.Ship != ShipType.Spec)
                            {
                                SetStartedPlaying(player, memberStats, matchStats.StartTimestamp);
                            }
                            else
                            {
                                // This is just in case we decide to allow starting a match where a player has left.
                                // E.g., in a team battle royale style game.
                                AddOrUpdatePlayerInfo(memberStats.MatchStats!, memberStats.PlayerName!, null);
                            }
                        }

                        matchStats.AddAssignSlotEvent(matchStats.StartTimestamp, slot.Team.Freq, slot.SlotIdx, memberStats.PlayerName!);
                    }
                }

                teamStats.RefreshRemainingSlotsAndAverageRating();
            }

            _mainloopTimer.SetTimer(MainloopTimer_ProcessPlayerDistances, 1000, 1000, matchStats, matchStats);

            return Task.CompletedTask;
        }

        private bool MainloopTimer_ProcessPlayerDistances(MatchStats matchStats)
        {
            if (matchStats.MatchData is null
                || matchStats.StartTimestamp == DateTime.MinValue
                || matchStats.EndTimestamp is not null
                || matchStats.Teams.Count == 0)
            {
                // The stats are not for an ongoing match, stop the timer.
                return false;
            }

            Arena? arena = matchStats.MatchData.Arena;
            if (arena is null)
            {
                // The arena does not exist, but the match can still be ongoing.
                // A short amount of time is given for players to return to the match after everyone leaves.
                return true;
            }

            string? playRegionName = matchStats.MatchData.Configuration.Boxes[matchStats.MatchData.MatchIdentifier.BoxIdx].PlayAreaMapRegion;
            MapRegion? playRegion = playRegionName is not null ? _mapData.FindRegionByName(arena, playRegionName) : null;

            foreach ((short freq, TeamStats teamStats) in matchStats.Teams)
            {
                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    MemberStats? memberStats = slotStats.Current;
                    if (memberStats is null)
                        continue;

                    Player? player = _playerData.FindPlayer(memberStats.PlayerName);
                    if (player is null 
                        || player.Flags.IsDead
                        || (playRegion is not null && !_mapData.RegionsAt(arena, (short)(player.Position.X / 16), (short)(player.Position.Y / 16)).Contains(playRegion)))
                    {
                        continue;
                    }

                    // Distance to team
                    int? distanceSquared = null;
                    foreach (SlotStats otherSlotStats in teamStats.Slots)
                    {
                        if (slotStats == otherSlotStats)
                            continue;

                        MemberStats? otherMemberStats = otherSlotStats.Current;
                        if (otherMemberStats is null)
                            continue;

                        Player? otherPlayer = _playerData.FindPlayer(otherMemberStats.PlayerName);
                        if (otherPlayer is null
                            || otherPlayer.Flags.IsDead
                            || (playRegion is not null && !_mapData.RegionsAt(arena, (short)(otherPlayer.Position.X / 16), (short)(otherPlayer.Position.Y / 16)).Contains(playRegion)))
                        {
                            continue;
                        }

                        int distanceX = player.Position.X - otherPlayer.Position.X;
                        int distanceY = player.Position.Y - otherPlayer.Position.Y;
                        int distance = (distanceX * distanceX) + (distanceY * distanceY);
                        if (distanceSquared is null || distance < distanceSquared)
                            distanceSquared = distance;
                    }

                    if (distanceSquared is not null)
                    {
                        ulong distance = (uint)Math.Ceiling(Math.Sqrt(distanceSquared.Value));

                        if (memberStats.DistanceToTeamSum is null || memberStats.DistanceToTeamSamples is null)
                        {
                            memberStats.DistanceToTeamSum = distance;
                            memberStats.DistanceToTeamSamples = 1;
                        }
                        else
                        {
                            memberStats.DistanceToTeamSum += distance;
                            memberStats.DistanceToTeamSamples++;
                        }
                    }

                    // Distance to enemy
                    distanceSquared = null;
                    foreach (TeamStats otherTeamStats in matchStats.Teams.Values)
                    {
                        if (teamStats == otherTeamStats)
                            continue;

                        foreach (SlotStats otherSlotStats in otherTeamStats.Slots)
                        {
                            MemberStats? otherMemberStats = otherSlotStats.Current;
                            if (otherMemberStats is null)
                                continue;

                            Player? otherPlayer = _playerData.FindPlayer(otherMemberStats.PlayerName);
                            if (otherPlayer is null
                                || otherPlayer.Flags.IsDead
                                || (playRegion is not null && !_mapData.RegionsAt(arena, (short)(otherPlayer.Position.X / 16), (short)(otherPlayer.Position.Y / 16)).Contains(playRegion)))
                            {
                                continue;
                            }

                            int distanceX = player.Position.X - otherPlayer.Position.X;
                            int distanceY = player.Position.Y - otherPlayer.Position.Y;
                            int distance = (distanceX * distanceX) + (distanceY * distanceY);
                            if (distanceSquared is null || distance < distanceSquared)
                                distanceSquared = distance;
                        }
                    }

                    if (distanceSquared is not null)
                    {
                        ulong distance = (uint)Math.Ceiling(Math.Sqrt(distanceSquared.Value));

                        if (memberStats.DistanceToEnemySum is null || memberStats.DistanceToEnemySamples is null)
                        {
                            memberStats.DistanceToEnemySum = distance;
                            memberStats.DistanceToEnemySamples = 1;
                        }
                        else
                        {
                            memberStats.DistanceToEnemySum += distance;
                            memberStats.DistanceToEnemySamples++;
                        }
                    }
                }
            }

            return true;
        }

        async Task<bool> ITeamVersusStatsBehavior.PlayerKilledAsync(
            ServerTick timestampTick,
            DateTime timestamp,
            IMatchData matchData,
            Player? killed,
            IPlayerSlot killedSlot,
            Player? killer,
            IPlayerSlot killerSlot,
            bool isKnockout)
        {
            if (killed is null || !killed.TryGetExtraData(_pdKey, out PlayerData? killedPlayerData))
                return false;

            if (killer is null || !killer.TryGetExtraData(_pdKey, out PlayerData? killerPlayerData))
                return false;

            MemberStats? killedMemberStats = killedPlayerData.MemberStats;
            if (killedMemberStats is null)
                return false;

            MemberStats? killerMemberStats = killerPlayerData.MemberStats;
            if (killerMemberStats is null)
                return false;

            MatchStats? matchStats = killedMemberStats.MatchStats;
            if (matchStats is null)
                return false;

            // Check that the players are in the same match.
            if (matchStats != killerMemberStats.MatchStats)
                return false;

            // Check that the kill was made in the correct arena.
            if (killed.Arena != matchStats.MatchData!.Arena || killer.Arena != matchStats.MatchData.Arena)
                return false;

            bool isTeamKill = killedSlot.Team == killerSlot.Team;

            //
            // Update stats that are not damage related.
            //

            // Update killer stats.
            if (isTeamKill)
            {
                killerMemberStats.TeamKills++;
            }
            else
            {
                killerMemberStats.Kills++;

                if (isKnockout)
                {
                    killerMemberStats.Knockouts++;
                }
            }

            // Update killed stats.
            killedMemberStats.Deaths++;
            killedMemberStats.WastedRepels += killedSlot.Repels;
            killedMemberStats.WastedRockets += killedSlot.Rockets;
            killedMemberStats.WastedThors += killedSlot.Thors;
            killedMemberStats.WastedBursts += killedSlot.Bursts;
            killedMemberStats.WastedDecoys += killedSlot.Decoys;
            killedMemberStats.WastedPortals += killedSlot.Portals;
            killedMemberStats.WastedBricks += killedSlot.Bricks;

            // Whether to consider it a "first out" when calculating the killer player's rating.
            bool isFirstOutKill = false;

            // Whether to consider it a "first out" when calculating the killed player's rating.
            bool isFirstOutDeath = false;

            if (isKnockout && !matchStats.FirstOutProcessed)
            {
                matchStats.FirstOutProcessed = true;
                isFirstOutKill = true;

                // We need to check if the member was responsible for all of the deaths for the slot
                // because there's a chance the player subbed into a slot that didn't have all lives remaining.
                // For example, a player that subbed into a slot on its last life should not be marked with a "first out".
                // However, a player that subbed in with all lives remaining should get marked as "first out" if they expend all the lives.
                if (killedMemberStats.Deaths == matchData.Configuration.LivesPerPlayer)
                {
                    isFirstOutDeath = true;
                    matchStats.FirstOut = killedMemberStats;
                    matchStats.FirstOutCritical =
                        ((DateTime.UtcNow - matchStats.StartTimestamp).TotalMinutes < 10d) // knocked out before the 10 minute mark
                        && (killedMemberStats.Kills < 2); // TODO: add a setting to control this? < 2 kills is the rule 4v4 uses, but this needs to support other modes (can't assume 3 lives per player, can't assume 2 teams only, etc..)
                }
            }

            // ships
            ShipType killedShip = killed.Ship;
            if (killedShip == ShipType.Spec)
            {
                // This case probably isn't possible, but just in case.
                if (killedMemberStats.CurrentShip is not null)
                {
                    killedShip = killedMemberStats.CurrentShip.Value;
                }
                else if (killedMemberStats.PreviousShip is not null)
                {
                    killedShip = killedMemberStats.PreviousShip.Value;
                }
                else
                {
                    _logManager.LogP(LogLevel.Warn, nameof(TeamVersusStats), killed, "Kill handler was unable to get the ship of killed player.");
                    return false;
                }
            }

            ShipType killerShip = killer.Ship;
            if (killerShip == ShipType.Spec)
            {
                // This is possible if the killer's ship was changed before the killed player's die packet was received/processed.
                // For example, both players were died, the killer was KO'd and ship changed to spec, and the killed player also dies (before being told the killer changed to spec).
                if (killerMemberStats.CurrentShip is not null)
                {
                    killerShip = killerMemberStats.CurrentShip.Value;
                }
                else if (killerMemberStats.PreviousShip is not null)
                {
                    killerShip = killerMemberStats.PreviousShip.Value;
                }
                else
                {
                    _logManager.LogP(LogLevel.Warn, nameof(TeamVersusStats), killed, "Kill handler was unable to get the ship of killer player.");
                    return false;
                }
            }

            // There's a chance that the player was at full energy prior to getting killed (e.g. portal onto a stack of mines/bombs).
            ProcessWastedEnergy(killed, killedMemberStats, killedShip, timestampTick);

            //
            // Gather some info in local variables prior to the delay.
            //

            // player names
            string killedPlayerName = killed.Name!;
            string killerPlayerName = killer.Name!;

            // kill coordinates
            short xCoord = killed.Position.X;
            short yCoord = killed.Position.Y;

            //
            // Delay processing the kill to allow time for the final C2S damage packet to make it to the server.
            // This gives a chance for C2S Damage packets to make it to the server and therefore more accurate damage stats.
            //

            await Task.Delay(200);

            // The Player objects (and therefore the PlayerData objects too) might be invalid after the await
            // (e.g. if a player disconnected during the delay).
            // We could verify a Player object by comparing Player.Name and checking that Player.Status = PlayerState.Playing.
            // However, we aren't going to use the Player or PlayerData objects after this point.
            // So, let's just clear our references to them.
            killed = null;
            killedPlayerData = null;
            killer = null;
            killerPlayerData = null;

            //
            // Damage related logic (stats, notifications, event logging).
            //

            Dictionary<PlayerTeamSlot, int> damageDictionary = s_damageDictionaryPool.Get();
            List<(string PlayerName, int Damage)> damageList = s_damageListPool.Get();
            Dictionary<string, float> ratingChangeDictionary = s_ratingChangeDictionaryPool.Get();

            try
            {
                // Calculate damage stats.
                CalculateDamageSources(timestampTick, killedMemberStats, killedShip, damageDictionary);
                killedMemberStats.ClearRecentDamage();

                // Update attacker stats.
                bool hasAssist = false;
                foreach ((PlayerTeamSlot attacker, int damage) in damageDictionary)
                {
                    if (!matchStats.Teams.TryGetValue(attacker.Freq, out TeamStats? attackerTeamStats))
                        continue;

                    SlotStats attackerSlotStats = attackerTeamStats.Slots[attacker.SlotIdx];
                    MemberStats? attackerMemberStats = attackerSlotStats.Members.Find(mStat => string.Equals(mStat.PlayerName, attacker.PlayerName, StringComparison.OrdinalIgnoreCase));
                    if (attackerMemberStats is null)
                        continue;

                    if (killedSlot.Team.Freq == attacker.Freq)
                    {
                        if (!string.Equals(attacker.PlayerName, killedPlayerName, StringComparison.OrdinalIgnoreCase))
                        {
                            // Damage from a teammate.
                            attackerMemberStats.TeamKillDamage += damage;
                        }
                    }
                    else
                    {
                        // Damage from an enemy.
                        attackerMemberStats.KillDamage += damage;

                        if (!string.Equals(attackerMemberStats.PlayerName, killerPlayerName, StringComparison.OrdinalIgnoreCase))
                        {
                            // Note: Purposely awarding an assist even if the attacker was not on the killer's team (possible if there are more than 2 teams).
                            attackerMemberStats.Assists++; // TODO: do we want more criteria (a minimum amount of damage)?
                            hasAssist = true;
                        }
                    }
                }

                if (!isTeamKill && !hasAssist)
                {
                    killerMemberStats.SoloKills++;
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
                // Rating
                //

                float killPoints =
                    isTeamKill
                        ? -2 // team kill
                        : isFirstOutKill
                            ? 10 // first out
                            : isKnockout && (killedMemberStats.TeamStats!.RemainingSlots >= killerMemberStats.TeamStats!.RemainingSlots)
                                ? 7 // knockout
                                : 5; // normal

                float assistPoints =
                    isKnockout
                        ? 1.5f // knockout
                        : 1.0f; // normal

                float deathPoints =
                    isFirstOutDeath
                        ? matchStats.FirstOutCritical
                            ? -30 // First Out & Critical
                            : -20 // First Out
                        : isKnockout && (killedMemberStats.TeamStats!.RemainingSlots >= killerMemberStats.TeamStats!.RemainingSlots)
                            ? -10 // KO
                            : -4; // normal

                // TODO: review this, differs than current 4v4, needs to work for any # of players and any # of teams
                float teammateEnemiesFactor = (float)Math.Sqrt(killedMemberStats.TeamStats!.RemainingSlots / Math.Max(1, killerMemberStats.TeamStats!.RemainingSlots));

                float killedKillerRatingFactor =
                    killerMemberStats.InitialRating == 0
                        ? 1f
                        : (float)Math.Sqrt((double)killedMemberStats.InitialRating / killerMemberStats.InitialRating);

                // Kill (killer)
                if (isTeamKill)
                {
                    // Doesn't use rating killed / rating killer.
                    killerMemberStats.RatingChange += ratingChangeDictionary[killerPlayerName] = teammateEnemiesFactor * killPoints;
                }
                else
                {
                    killerMemberStats.RatingChange += ratingChangeDictionary[killerPlayerName] = killedKillerRatingFactor * teammateEnemiesFactor * killPoints;
                }

                // Assists (attackers other than the killer, that are not on the killed player's team)
                foreach ((PlayerTeamSlot attacker, int damage) in damageDictionary)
                {
                    if (killedMemberStats.TeamStats.Team!.Freq != attacker.Freq
                        && !string.Equals(killerPlayerName, attacker.PlayerName, StringComparison.OrdinalIgnoreCase)
                        && matchStats.Teams.TryGetValue(attacker.Freq, out TeamStats? attackerTeamStats))
                    {
                        SlotStats attackerSlotStats = attackerTeamStats.Slots[attacker.SlotIdx];
                        MemberStats? attackerMemberStats = attackerSlotStats.Members.Find(mStat => string.Equals(mStat.PlayerName, attacker.PlayerName, StringComparison.OrdinalIgnoreCase));
                        if (attackerMemberStats is null)
                            continue;

                        attackerMemberStats.RatingChange += ratingChangeDictionary[attacker.PlayerName] =
                            assistPoints * (killedMemberStats.TeamStats.RemainingSlots / Math.Min(1, attackerTeamStats.RemainingSlots));
                    }
                }

                // Death & Wasted Reps (killed)
                float killedKillerTeamRatingRatio =
                    killerMemberStats.TeamStats.AverageRating == 0
                        ? 1f
                        : (killedMemberStats.InitialRating / killerMemberStats.TeamStats.AverageRating);

                killedMemberStats.RatingChange += ratingChangeDictionary[killedPlayerName] =
                    (killedKillerRatingFactor * teammateEnemiesFactor * deathPoints)
                    - (.5f * killedSlot.Repels * killedKillerTeamRatingRatio);

                //
                // Send notifications
                //

                HashSet<Player> notifySet = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    // Players that are playing the match or are spectating the match, and are in the match's arena.
                    _matchFocus.TryGetPlayers(matchData, notifySet, MatchFocusReasons.Playing | MatchFocusReasons.Spectating, matchData.Arena);

                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        TimeSpan gameTime = matchData.Started is not null ? timestamp - matchData.Started.Value : TimeSpan.Zero;

                        // Kill notification
                        int killerDamage = 0;
                        StringBuilder addBuilder = _objectPoolManager.StringBuilderPool.Get();
                        try
                        {
                            int assistCount = 0;
                            foreach ((string playerName, int damage) in damageList)
                            {
                                if (string.Equals(killerPlayerName, playerName, StringComparison.OrdinalIgnoreCase))
                                {
                                    killerDamage += damage;
                                    continue;
                                }

                                if (addBuilder.Length > 0)
                                    addBuilder.Append(", ");

                                addBuilder.Append($"{playerName} ({damage})");
                                assistCount++;
                            }

                            sb.Append($"{killedPlayerName} kb {killerPlayerName}");

                            if (killerDamage > 0)
                            {
                                sb.Append($" ({killerDamage})");
                            }

                            if (isTeamKill)
                            {
                                sb.Append(" [TEAMKILL]");
                            }

                            if (assistCount > 0)
                            {
                                sb.Append($" -- Add: ");
                                sb.Append(addBuilder);
                            }
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(addBuilder);
                        }

                        _chat.SendSetMessage(notifySet, sb);
                        sb.Clear();

                        // Remaining lives notification
                        // TODO: A way to not duplicate this logic which is also in TeamVersusMatch
                        if (isKnockout)
                        {
                            sb.Append($"{killedPlayerName} is OUT!");
                        }
                        else
                        {
                            sb.Append($"{killedPlayerName} has {killedMemberStats.SlotStats!.Slot!.Lives} {(killedMemberStats.SlotStats.Slot.Lives > 1 ? "lives" : "life")} remaining");
                        }

                        sb.Append(" [");
                        sb.AppendFriendlyTimeSpan(gameTime);
                        sb.Append(']');

                        _chat.SendSetMessage(notifySet, sb);
                        sb.Clear();

                        // Score notification
                        // TODO: A way to not duplicate this logic which is also in TeamVersusMatch
                        sb.Append("Score: ");

                        StringBuilder remainingBuilder = _objectPoolManager.StringBuilderPool.Get();

                        try
                        {
                            short highScore = -1;
                            short highScoreFreq = -1;
                            int highScoreCount = 0;

                            foreach (var team in matchData.Teams)
                            {
                                if (sb.Length > "Score: ".Length)
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

                            sb.Append(" -- ");
                            sb.Append(remainingBuilder);
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(remainingBuilder);
                        }

                        sb.Append(" -- [");
                        sb.AppendFriendlyTimeSpan(gameTime);
                        sb.Append(']');

                        _chat.SendSetMessage(notifySet, sb);
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(notifySet);
                }

                killedMemberStats.TeamStats.RefreshRemainingSlotsAndAverageRating();

                matchStats.AddKillEvent(
                    timestamp,
                    killedPlayerName,
                    killedShip,
                    killerPlayerName,
                    killerShip,
                    isKnockout,
                    isTeamKill,
                    xCoord,
                    yCoord,
                    damageList,
                    ratingChangeDictionary);
            }
            finally
            {
                s_damageDictionaryPool.Return(damageDictionary);
                s_damageListPool.Return(damageList);
                s_ratingChangeDictionaryPool.Return(ratingChangeDictionary);
            }

            return true;


            // local function for sorting
            static int PlayerDamageTupleComparison((string PlayerName, int Damage) x, (string PlayerName, int Damage) y)
            {
                int ret = x.Damage.CompareTo(y.Damage);
                if (ret != 0)
                    return -ret; // damage desc

                return string.Compare(x.PlayerName, y.PlayerName); // player name asc
            }
        }

        async Task<bool> ITeamVersusStatsBehavior.MatchEndedAsync(IMatchData matchData, MatchEndReason reason, ITeam? winnerTeam)
        {
            if (!_matchStatsDictionary.Remove(matchData.MatchIdentifier, out MatchStats? matchStats))
                return false;

            matchStats.EndTimestamp = DateTime.UtcNow;
            ServerTick endTick = ServerTick.Now;

            _mainloopTimer.ClearTimer<MatchStats>(MainloopTimer_ProcessPlayerDistances, matchStats);

            Arena? arena = matchData.Arena; // null if the arena doesn't exist

            if (reason != MatchEndReason.Cancelled)
            {
                // Refresh stats that are affected by the match ending: wasted energy, play time, and ship usage.
                foreach (TeamStats teamStats in matchStats.Teams.Values)
                {
                    foreach (SlotStats slotStats in teamStats.Slots)
                    {
                        foreach (MemberStats memberStats in slotStats.Members)
                        {
                            if (memberStats.IsCurrent)
                            {
                                // Play time
                                ProcessPlayTime(memberStats, matchStats.EndTimestamp.Value);

                                // Ship usage
                                ProcessShipUsage(memberStats, matchStats.EndTimestamp.Value, null);

                                // Wasted energy
                                if (arena is not null)
                                {
                                    Player? player = _playerData.FindPlayer(memberStats.PlayerName);
                                    if (player is not null
                                        && player.Ship != ShipType.Spec
                                        && player.Arena == arena)
                                    {
                                        ProcessWastedEnergy(player, memberStats, player.Ship, endTick);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            foreach (TeamStats teamStats in matchStats.Teams.Values)
            {
                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    foreach (MemberStats memberStats in slotStats.Members)
                    {
                        if (_playerMemberDictionary.Remove(memberStats.PlayerName!, out MemberStats? otherStats))
                        {
                            if (otherStats != memberStats)
                            {
                                // It's possible that the player is in another match.
                                _playerMemberDictionary.Add(memberStats.PlayerName!, otherStats);
                            }
                        }
                    }

                    Player? currentPlayer = slotStats.Slot?.Player;
                    if (currentPlayer is not null)
                    {
                        SetStoppedPlaying(currentPlayer, matchStats.EndTimestamp.Value);
                    }
                }
            }

            while (matchStats.PlayerDataSet.Count > 0)
            {
                foreach (PlayerData playerData in matchStats.PlayerDataSet)
                {
                    playerData.MemberStats = null;
                    // The playerData.MemberStats setter modified the set, so the enumerator is no longer usable.
                    break;
                }
            }

            if (reason != MatchEndReason.Cancelled)
            {
                await SaveGameToDatabase(matchData, winnerTeam, matchStats);

                // Send game stat as chat notifications.
                HashSet<Player> notifySet = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    // Notification to players that have any association to the match, regardless of the arena they are in.
                    _matchFocus.TryGetPlayers(matchStats.MatchData!, notifySet, MatchFocusReasons.Any, null);
                    PrintMatchStats(notifySet, matchStats, reason, winnerTeam);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(notifySet);
                }
            }

            ResetMatchStats(matchStats);
            // TODO: return matchStats to a pool

            return true;


            // local function that saves match stats to the database
            async Task SaveGameToDatabase(IMatchData matchData, ITeam? winnerTeam, MatchStats matchStats)
            {
                if (matchData is null || matchStats is null)
                    return;

                bool isLeagueMatch = matchData.LeagueSeasonGameId is not null;
                if ((!isLeagueMatch && _gameStatsRepository is null) || (isLeagueMatch && _leagueRepository is null))
                    return;

                if (matchData.Configuration.GameTypeId is null)
                    return;

                // TODO: Maybe pool the MemoryStream and Utf8JsonWriter?
                // The RecyclableMemoryStream reuses underlying buffers, but maybe just having a pool of regular MemoryStream
                // The Utf8JsonWriter has a Reset method.

                using MemoryStream gameJsonStream = s_recyclableMemoryStreamManager.GetStream();
                using Utf8JsonWriter writer = new(gameJsonStream, default);

                writer.WriteStartObject(); // game object
                writer.WriteNumber("game_type_id"u8, matchData.Configuration.GameTypeId.Value);
                writer.WriteString("zone_server_name"u8, _zoneServerName);
                writer.WriteString("arena"u8, matchData.ArenaName);
                writer.WriteNumber("box_number"u8, matchData.MatchIdentifier.BoxIdx);
                WriteLvlInfo(writer, matchData);
                writer.WriteString("start_timestamp"u8, matchStats.StartTimestamp);
                writer.WriteString("end_timestamp"u8, matchStats.EndTimestamp!.Value);
                writer.WriteString("replay_path"u8, (string?)null); // TODO: add the ability automatically record games

                writer.WriteStartObject("players");
                foreach ((string playerName, PlayerInfo playerInfo) in matchStats.PlayerInfoDictionary)
                {
                    writer.WriteStartObject(playerName); // player object
                    writer.WriteString("squad"u8, playerInfo.Squad); // write it even if there is no squad (so that the database knows to clear the player's current squad)

                    if (playerInfo.XRes is not null && playerInfo.YRes is not null)
                    {
                        writer.WriteNumber("x_res"u8, playerInfo.XRes.Value);
                        writer.WriteNumber("y_res"u8, playerInfo.YRes.Value);
                    }

                    writer.WriteEndObject(); // player object
                }
                writer.WriteEndObject(); // players object

                writer.WriteStartArray("team_stats"u8); // team_stats array

                foreach (TeamStats teamStats in matchStats.Teams.Values)
                {
                    writer.WriteStartObject(); // team object
                    writer.WriteNumber("freq"u8, teamStats.Team!.Freq);
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

                            if (memberStats.PremadeGroupId is not null)
                                writer.WriteNumber("premade_group", memberStats.PremadeGroupId.Value); // TODO: change the database to use this and track stats separately for solo vs grouped play

                            writer.TryWriteTimeSpanAsISO8601("play_duration"u8, memberStats.PlayTime);
                            writer.WriteNumber("lag_outs"u8, memberStats.LagOuts);
                            writer.WriteNumber("kills"u8, memberStats.Kills);
                            writer.WriteNumber("deaths"u8, memberStats.Deaths);
                            writer.WriteNumber("team_kills"u8, memberStats.TeamKills);
                            writer.WriteNumber("knockouts"u8, memberStats.Knockouts);
                            writer.WriteNumber("solo_kills"u8, memberStats.SoloKills);
                            writer.WriteNumber("assists"u8, memberStats.Assists);
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
                            writer.WriteNumber("forced_rep_damage"u8, memberStats.ForcedRepDamage);
                            writer.WriteNumber("bullet_fire_count"u8, memberStats.GunFireCount);
                            writer.WriteNumber("bomb_fire_count"u8, memberStats.BombFireCount);
                            writer.WriteNumber("mine_fire_count"u8, memberStats.MineFireCount);
                            writer.WriteNumber("bullet_hit_count"u8, memberStats.GunHitCount);
                            writer.WriteNumber("bomb_hit_count"u8, memberStats.BombHitCount);
                            writer.WriteNumber("mine_hit_count"u8, memberStats.MineHitCount);

                            if (matchStats.FirstOut == memberStats)
                            {
                                writer.WriteNumber("first_out"u8, matchStats.FirstOutCritical ? (short)FirstOut.YesCritical : (short)FirstOut.Yes);
                            }

                            writer.WriteNumber("wasted_energy"u8, memberStats.WastedEnergy);

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

                            if (memberStats.DistanceToEnemySum is not null && memberStats.DistanceToEnemySamples is not null && memberStats.DistanceToEnemySamples > 0)
                            {
                                writer.WriteNumber("enemy_distance_sum"u8, memberStats.DistanceToEnemySum.Value);
                                writer.WriteNumber("enemy_distance_samples"u8, memberStats.DistanceToEnemySamples.Value);
                            }

                            if (memberStats.DistanceToTeamSum is not null && memberStats.DistanceToTeamSamples is not null && memberStats.DistanceToTeamSamples > 0)
                            {
                                writer.WriteNumber("team_distance_sum"u8, memberStats.DistanceToTeamSum.Value);
                                writer.WriteNumber("team_distance_samples"u8, memberStats.DistanceToTeamSamples.Value);
                            }

                            writer.WriteNumber("rating_change"u8, (int)memberStats.RatingChange); // round towards zero (cut any fractional part off)

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
                //StreamReader reader = new(gameJsonStream, Encoding.UTF8);
                //string data = reader.ReadToEnd();
                //Console.WriteLine(data);
                //gameJsonStream.Position = 0;
                // DEBUG - REMOVE ME ***************************************************

                if (matchData.LeagueSeasonGameId is null && _gameStatsRepository is not null)
                    matchStats.GameId = await _gameStatsRepository.SaveGameAsync(gameJsonStream);
                else if (matchData.LeagueSeasonGameId is not null && _leagueRepository is not null)
                    matchStats.GameId = await _leagueRepository!.SaveGameAsync(matchData.LeagueSeasonGameId.Value, gameJsonStream);

                if (matchStats.GameId is not null)
                {
                    _logManager.LogM(LogLevel.Info, nameof(TeamVersusStats), $"Saved GameId {matchStats.GameId.Value} to the database.");
                }


                void WriteLvlInfo(Utf8JsonWriter writer, IMatchData matchData)
                {
                    ReadOnlySpan<char> arenaBaseName;
                    if (matchData.Arena is not null)
                        arenaBaseName = matchData.Arena.BaseName;
                    else if (!Arena.TryParseArenaName(matchData.ArenaName, out arenaBaseName, out _))
                        arenaBaseName = "";

                    if (_arenaMapInfoLookup.TryGetValue(arenaBaseName, out var lvlTuple))
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

        #region ILeagueHelp

        void ILeagueHelp.PrintHelp(Player player)
        {
            _chat.SendMessage(player, "--- Stats Information ---------------------------------------------------------");
            PrintCommand(player, "chart", "Prints the stats chart for the current match.");

            void PrintCommand(Player player, string command, string description)
            {
                _chat.SendMessage(player, $"?{command,-10}  {description}");
            }
        }

        #endregion

        #region Callbacks

        private async void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                if (!_arenaSettings.TryGetValue(arena.BaseName, out ArenaSettings? arenaSettings))
                {
                    arenaSettings = new ArenaSettings();
                    _arenaSettings.Add(arena.BaseName, arenaSettings);
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
                        CloakEnergy = (short)_clientSettings.GetSetting(arena, _shipClientSettingIds[shipIndex].CloakEnergyId),
                        StealthEnergy = (short)_clientSettings.GetSetting(arena, _shipClientSettingIds[shipIndex].StealthEnergyId),
                        XRadarEnergy = (short)_clientSettings.GetSetting(arena, _shipClientSettingIds[shipIndex].XRadarEnergyId),
                        AntiWarpEnergy = (short)_clientSettings.GetSetting(arena, _shipClientSettingIds[shipIndex].AntiWarpEnergyId),
                    };
                }

                string? mapPath = await _mapData.GetMapFilenameAsync(arena, null);
                if (mapPath is not null)
                    mapPath = Path.GetFileName(mapPath);

                _arenaMapInfo[arena.BaseName] = (mapPath!, _mapData.GetChecksum(arena, 0));
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (action == PlayerAction.Connect)
            {
                if (_playerMemberDictionary.TryGetValue(player.Name!, out MemberStats? memberStats))
                {
                    // The player is currently in a match and has reconnected.
                    playerData.MemberStats = memberStats;
                }
            }
            else if (action == PlayerAction.EnterGame)
            {
                MemberStats? memberStats = playerData.MemberStats;
                if (memberStats is not null
                    && memberStats.IsCurrent
                    && player.Arena == memberStats.MatchStats?.MatchData?.Arena
                    && player.Freq == memberStats.TeamStats?.Team?.Freq
                    && player.Ship != ShipType.Spec)
                {
                    SetStartedPlaying(player, memberStats, DateTime.UtcNow);
                }
            }
            else if (action == PlayerAction.LeaveArena)
            {
                SetStoppedPlaying(player, DateTime.UtcNow);

                MemberStats? memberStats = playerData.MemberStats;
                if (memberStats is not null)
                {
                    ProcessWastedEnergy(player, memberStats, player.Ship, ServerTick.Now);
                }
            }
            else if (action == PlayerAction.Disconnect)
            {
                if (playerData.MemberStats is not null)
                {
                    playerData.MemberStats = null;
                }
            }
        }

        private void Callback_TeamVersusMatchPlayerLagOut(IPlayerSlot playerSlot, SlotInactiveReason reason)
        {
            if (!_matchStatsDictionary.TryGetValue(playerSlot.MatchData.MatchIdentifier, out MatchStats? matchStats)
                || !matchStats.Teams.TryGetValue(playerSlot.Team.Freq, out TeamStats? teamStats))
            {
                return;
            }

            SlotStats slotStats = teamStats.Slots[playerSlot.SlotIdx];
            MemberStats? memberStats = slotStats.Current;
            if (memberStats is null)
                return;

            ProcessPlayTime(memberStats, DateTime.UtcNow); // TODO: review whether we need to do this or if it's handled elsewhere

            memberStats.LagOuts++;
        }

        private void Callback_TeamVersusMatchPlayerShipChanged(IPlayerSlot playerSlot, ShipType oldShip, ShipType newShip)
        {
            if (!_matchStatsDictionary.TryGetValue(playerSlot.MatchData.MatchIdentifier, out MatchStats? matchStats)
                || !matchStats.Teams.TryGetValue(playerSlot.Team.Freq, out TeamStats? teamStats))
            {
                return;
            }

            SlotStats slotStats = teamStats.Slots[playerSlot.SlotIdx];
            MemberStats? memberStats = slotStats.Current;
            if (memberStats is null)
                return;

            matchStats.AddShipChangeEvent(DateTime.UtcNow, memberStats.PlayerName!, newShip);
        }

        private async void Callback_TeamVersusMatchPlayerSubbed(IPlayerSlot playerSlot, string? subOutPlayerName)
        {
            if (!_matchStatsDictionary.TryGetValue(playerSlot.MatchData.MatchIdentifier, out MatchStats? matchStats)
                || !matchStats.Teams.TryGetValue(playerSlot.Team.Freq, out TeamStats? teamStats))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;

            SlotStats slotStats = teamStats.Slots[playerSlot.SlotIdx];

            Debug.Assert(slotStats.Slot == playerSlot);

            // Sub out
            MemberStats? subOutStats = slotStats.Current;
            if (subOutStats is not null)
            {
                Debug.Assert(string.Equals(subOutStats.PlayerName, subOutPlayerName, StringComparison.OrdinalIgnoreCase));
                Debug.Assert(!string.Equals(subOutStats.PlayerName, playerSlot.PlayerName, StringComparison.OrdinalIgnoreCase));

                _playerMemberDictionary.Remove(subOutStats.PlayerName!);

                Player? subOutPlayer = _playerData.FindPlayer(subOutStats.PlayerName);
                if (subOutPlayer is not null)
                {
                    SetStoppedPlaying(subOutPlayer, now);
                }
            }

            // Sub in
            string? subInPlayerName = playerSlot.PlayerName;
            if (!string.IsNullOrWhiteSpace(subInPlayerName))
            {
                MemberStats? memberStats = slotStats.Members.Find(mStat => string.Equals(subInPlayerName, mStat.PlayerName, StringComparison.OrdinalIgnoreCase));
                if (memberStats is null)
                {
                    memberStats = new(); // TODO: get from a pool
                    memberStats.Initialize(slotStats, subInPlayerName, false);
                    slotStats.Members.Add(memberStats);
                }

                slotStats.Current = memberStats;

                _playerMemberDictionary[subInPlayerName] = memberStats;
                Player? subInPlayer = playerSlot.Player ?? _playerData.FindPlayer(subInPlayerName);
                if (subInPlayer is not null)
                {
                    if (subInPlayer.TryGetExtraData(_pdKey, out PlayerData? subInPlayerData))
                    {
                        subInPlayerData.MemberStats = memberStats;
                    }

                    SetStartedPlaying(subInPlayer, memberStats, now);
                }

                matchStats.AddAssignSlotEvent(DateTime.UtcNow, playerSlot.Team.Freq, playerSlot.SlotIdx, subInPlayerName);

                // Get the rating of the player subbing in.
                Dictionary<string, int> playerRatingDictionary = new(StringComparer.OrdinalIgnoreCase) // TODO: pool
                {
                    [subInPlayerName] = DefaultRating
                };

                long? gameTypeId = matchStats.MatchData?.Configuration.GameTypeId;

                if (_gameStatsRepository is not null && gameTypeId is not null)
                {
                    await _gameStatsRepository.GetPlayerRatingsAsync(gameTypeId.Value, playerRatingDictionary);
                }

                if (playerRatingDictionary.TryGetValue(subInPlayerName, out int rating))
                    memberStats.InitialRating = rating;

                // Refresh the team's average rating.
                teamStats.RefreshRemainingSlotsAndAverageRating();
            }
        }

        private void Callback_BricksPlaced(Arena arena, Player? player, IReadOnlyList<BrickData> bricks)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            MemberStats? memberStats = playerData.MemberStats;
            if (memberStats is null || !memberStats.IsCurrent)
                return;

            MatchStats? matchStats = memberStats.MatchStats;
            if (matchStats is null)
                return;

            // Check that the player is in the match's arena and on the proper team.
            if (arena != matchStats.MatchData?.Arena || player.Freq != memberStats.TeamStats?.Team?.Freq)
                return;

            matchStats.AddUseItemEvent(DateTime.UtcNow, player.Name!, ShipItem.Brick, null, null);
        }

        private void Callback_PlayerDamage(Player player, ServerTick timestamp, ReadOnlySpan<DamageData> damageDataSpan)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            Arena? arena = player.Arena;
            if (arena is null || !_arenaSettings.TryGetValue(arena.BaseName, out ArenaSettings? arenaSettings))
                return;

            MemberStats? playerStats = playerData.MemberStats;
            if (playerStats is null || !playerStats.IsCurrent)
                return;

            MatchStats? matchStats = playerStats.MatchStats;
            if (matchStats is null)
                return;

            // Check that the player is in the match's arena and on the proper team.
            if (arena != matchStats.MatchData?.Arena || player.Freq != playerStats.TeamStats?.Team?.Freq)
                return;

            for (int i = 0; i < damageDataSpan.Length; i++)
            {
                ref readonly DamageData damageData = ref damageDataSpan[i];

                // damageData.Damage can be > damageData.Energy if it's the killing blow or if the player caused self-inflicted damage.
                // So, limit the damage to the amount of energy the player has. So that we record the actual amount taken.
                short damage = Math.Clamp(damageData.Damage, (short)0, damageData.Energy);

                Player? attackerPlayer;
                MemberStats? attackerStats = null;

                if (player.Id == damageData.AttackerPlayerId)
                {
                    // Self-inflicted damage (this includes going through a wormhole).
                    attackerPlayer = player;
                    attackerStats = playerStats;
                }
                else
                {
                    attackerPlayer = _playerData.PidToPlayer(damageData.AttackerPlayerId);

                    if (attackerPlayer is not null)
                    {
                        if (attackerPlayer.TryGetExtraData(_pdKey, out PlayerData? attackerPlayerData)
                            && attackerPlayerData.MemberStats is not null
                            && attackerPlayerData.MemberStats.MatchStats == matchStats)
                        {
                            attackerStats = attackerPlayerData.MemberStats;
                        }

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
                }

                // If we haven't found the attacker's MemberStats by now, then too bad. We just won't record stats for the attacker.
                // Note: It's possible that the attacker disconnected before the damage packet made it to us,
                // in which case attackerPlayer will be null, and we just won't record stats for the attacker.

                //
                // Recent damage (used for calculating damage that contributed to a kill)
                //

                if (attackerPlayer is not null
                    && attackerStats is not null
                    && player.Ship != ShipType.Spec) // there's a chance that the player was switched to spec and then the damage packet arrived
                {
                    int shipIndex = (int)player.Ship;
                    ShipSettings shipSettings = arenaSettings.ShipSettings[shipIndex];

                    // recharge rate = amount of energy in 10 seconds = amount of energy in 1000 ticks
                    short maximumRecharge = GetClientSetting(player, _shipClientSettingIds[shipIndex].MaximumRechargeId, shipSettings.MaximumRecharge);

                    short maximumEnergy = GetClientSetting(player, _shipClientSettingIds[shipIndex].MaximumEnergyId, shipSettings.MaximumEnergy);

                    playerStats.RemoveOldRecentDamage(maximumEnergy, maximumRecharge);

                    // Calculate emp shutdown time.
                    uint empShutdownTicks = 0;
                    if (damageData.WeaponData.Type == WeaponCodes.Bomb || damageData.WeaponData.Type == WeaponCodes.ProxBomb)
                    {
                        ShipType attackerShip = attackerPlayer.Ship;

                        // There's a chance the attacker was switched to spec before the damage packet arrived.
                        if (attackerShip == ShipType.Spec && attackerStats.PreviousShip is not null)
                            attackerShip = attackerStats.PreviousShip.Value;

                        if (attackerShip != ShipType.Spec)
                        {
                            int attackerShipIndex = (int)attackerShip;

                            // Only checking for an arena override on emp bomb, since it doesn't make sense to override the setting on the player-level.
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
                    }

                    LinkedListNode<DamageInfo> node = s_damageInfoLinkedListNodePool.Get();
                    node.Value = new DamageInfo()
                    {
                        Timestamp = timestamp,
                        Damage = damage,
                        EmpBombShutdownTicks = empShutdownTicks,
                        Attacker = new PlayerTeamSlot(
                            attackerPlayer!.Name!,
                            attackerPlayer.Freq,
                            attackerStats.SlotStats!.Slot!.SlotIdx),
                    };
                    playerStats.RecentDamageTaken.AddLast(node);
                }

                //
                // Damage stats
                //

                if (player.Id == damageData.AttackerPlayerId)
                {
                    // Self-inflicted damage.
                    playerStats.DamageSelf += damage;
                }
                else
                {
                    // Damage from an another player (enemy or teammate).
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
                            if (player.Freq == attackerPlayer!.Freq)
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

        private void Callback_PlayerPositionPacket(Player player, ref readonly C2S_PositionPacket positionPacket, ref readonly ExtraPositionData extra, bool hasExtraPositionData)
        {
            if (player is null)
                return;

            if (player.Ship == ShipType.Spec)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            MemberStats? memberStats = playerData.MemberStats;
            if (memberStats is null || !memberStats.IsCurrent)
                return;

            MatchStats? matchStats = memberStats.MatchStats;
            if (matchStats is null)
                return;

            // Check that the player is in the match's arena and on the proper team.
            if (arena != matchStats.MatchData?.Arena || player.Freq != memberStats.TeamStats?.Team?.Freq)
                return;

            //
            // Wasted energy
            //

            // For wasted energy tracking, ignore out of order packets.
            if (memberStats.LastPositionTime is null || positionPacket.Time > memberStats.LastPositionTime.Value)
            {
                memberStats.LastPositionTime = positionPacket.Time;

                if (_arenaSettings.TryGetValue(arena.BaseName, out ArenaSettings? arenaSettings))
                {
                    int shipIndex = (int)player.Ship;
                    ShipSettings arenaShipSettings = arenaSettings.ShipSettings[shipIndex];
                    ref ShipClientSettingIdentifiers shipClientSettingIds = ref _shipClientSettingIds[shipIndex];
                    short maximumEnergy = GetClientSetting(player, shipClientSettingIds.MaximumEnergyId, arenaShipSettings.MaximumEnergy);
                    PlayerPositionStatus utilityStatus = positionPacket.Status & (PlayerPositionStatus.Stealth | PlayerPositionStatus.Cloak | PlayerPositionStatus.XRadar | PlayerPositionStatus.Antiwarp);

                    // The player is at full energy if the player has the maximum energy or has maximum energy - 1 with at least one utility active.
                    bool isAtFullEnergy = positionPacket.Energy == maximumEnergy || (positionPacket.Energy == (maximumEnergy - 1) && utilityStatus != 0);

                    if (memberStats.FullEnergyStartTime is null)
                    {
                        // Was not at full energy.
                        if (isAtFullEnergy)
                        {
                            memberStats.FullEnergyStartTime = positionPacket.Time;
                            memberStats.FullEnergyUtilityStatus = utilityStatus;
                        }
                    }
                    else
                    {
                        // Was at full energy
                        if (!isAtFullEnergy || memberStats.FullEnergyUtilityStatus != utilityStatus)
                        {
                            // No longer at full energy, or there was a change in utility (stealth, cloak, xradar, antiwarp) use.
                            int duration = positionPacket.Time - memberStats.FullEnergyStartTime.Value;
                            if (duration > 0)
                            {
                                AddWastedEnergy(player, memberStats, arenaShipSettings, shipClientSettingIds, duration);
                            }

                            if (isAtFullEnergy)
                            {
                                // Still at maximum energy (meaning we're in here because the player had a change in utility use).
                                memberStats.FullEnergyStartTime = positionPacket.Time;
                                memberStats.FullEnergyUtilityStatus = utilityStatus;
                            }
                            else
                            {
                                // No longer at full energy.
                                memberStats.FullEnergyStartTime = null;
                                memberStats.FullEnergyUtilityStatus = 0;
                            }
                        }
                    }
                }
            }

            if (positionPacket.Weapon.Type != WeaponCodes.Null)
            {
                playerData.TrimWeaponUseLog();

                if (!playerData.AddWeaponUse(positionPacket.Weapon.Type, positionPacket.Time))
                    return; // This is a duplicate packet. Ignore it.
            }

            //
            // Weapon use
            //

            switch (positionPacket.Weapon.Type)
            {
                case WeaponCodes.Bullet:
                case WeaponCodes.BounceBullet:
                    memberStats.GunFireCount++;
                    break;

                case WeaponCodes.Thor:
                    matchStats.AddUseItemEvent(DateTime.UtcNow, player.Name!, ShipItem.Thor, null, null);
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

                    matchStats.AddUseItemEvent(DateTime.UtcNow, player.Name!, ShipItem.Burst, null, null);
                    break;

                case WeaponCodes.Repel:
                    Dictionary<PlayerTeamSlot, int> damageDictionary = s_damageDictionaryPool.Get();
                    List<(string PlayerName, int Damage)> damageList = s_damageListPool.Get();
                    Dictionary<string, float> ratingChangeDictionary = s_ratingChangeDictionaryPool.Get();

                    try
                    {
                        CalculateDamageSources(ServerTick.Now, memberStats, player.Ship, damageDictionary);

                        // Update attacker stats.
                        foreach ((PlayerTeamSlot attacker, int damage) in damageDictionary)
                        {
                            if (!matchStats.Teams.TryGetValue(attacker.Freq, out TeamStats? attackerTeamStats))
                                continue;

                            if (memberStats.TeamStats!.Team!.Freq != attacker.Freq)
                            {
                                SlotStats attackerSlotStats = attackerTeamStats.Slots[attacker.SlotIdx];
                                MemberStats? attackerMemberStats = attackerSlotStats.Members.Find(mStat => string.Equals(mStat.PlayerName, attacker.PlayerName, StringComparison.OrdinalIgnoreCase));
                                if (attackerMemberStats is null)
                                    continue;

                                attackerMemberStats.ForcedRepDamage += damage;
                                attackerMemberStats.ForcedReps++; // TODO: do we want more criteria (a certain amount of damage or an even smaller damage window)?

                                // Rating for forced rep to the attacker.
                                float ratingRatio =
                                    attackerMemberStats.InitialRating == 0
                                        ? 1f
                                        : (memberStats.TeamStats.AverageRating / attackerMemberStats.InitialRating);

                                float change = .5f * ratingRatio;
                                attackerMemberStats.RatingChange += change;

                                if (ratingChangeDictionary.TryGetValue(attacker.PlayerName, out float ratingChange))
                                {
                                    ratingChangeDictionary[attacker.PlayerName] = ratingChange + change;
                                }
                                else
                                {
                                    ratingChangeDictionary[attacker.PlayerName] = change;
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

                        matchStats.AddUseItemEvent(DateTime.UtcNow, player.Name!, ShipItem.Repel, damageList, ratingChangeDictionary);
                    }
                    finally
                    {
                        s_ratingChangeDictionaryPool.Return(ratingChangeDictionary);
                        s_damageDictionaryPool.Return(damageDictionary);
                        s_damageListPool.Return(damageList);
                    }

                    break;

                case WeaponCodes.Decoy:
                    matchStats.AddUseItemEvent(DateTime.UtcNow, player.Name!, ShipItem.Decoy, null, null);
                    break;

                default:
                    break;
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            MemberStats? memberStats = playerData.MemberStats;
            if (memberStats is null || !memberStats.IsCurrent)
                return;

            MatchStats? matchStats = memberStats.MatchStats;
            if (matchStats is null)
                return;

            IMatchData? matchData = matchStats.MatchData;
            if (matchData is null)
                return;

            if (arena != matchData.Arena)
                return;

            // The player is in a match and is in the correct arena for that match.

            short? teamFreq = memberStats.TeamStats?.Team?.Freq;
            if (teamFreq is null)
                return;

            DateTime now = DateTime.UtcNow;
            ServerTick nowTick = ServerTick.Now;

            ProcessWastedEnergy(player, memberStats, oldShip, nowTick);

            bool wasPlaying = (oldFreq == teamFreq.Value) && (oldShip != ShipType.Spec);
            bool isPlaying = (newFreq == teamFreq.Value) && (newShip != ShipType.Spec);

            if (wasPlaying && !isPlaying)
            {
                SetStoppedPlaying(player, now);
            }

            if (!wasPlaying && isPlaying)
            {
                SetStartedPlaying(player, memberStats, now);
            }
        }

        private void Callback_Spawn(Player player, SpawnCallback.SpawnReason reason)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            MemberStats? memberStats = playerData.MemberStats;
            if (memberStats is null)
                return;

            memberStats.ClearRecentDamage();
        }

        #endregion

        #region Commands
        private void Command_chart(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            IMatch? match = _matchFocus.GetFocusedMatch(player);
            if (match is null || match is not IMatchData matchData)
                return;

            if (!_matchStatsDictionary.TryGetValue(matchData.MatchIdentifier, out MatchStats? matchStats))
                return;

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

        private static void AddOrUpdatePlayerInfo(MatchStats matchStats, string playerName, Player? player)
        {
            if (matchStats is null || string.IsNullOrWhiteSpace(playerName))
                return;

            PlayerInfo playerInfo = new(player?.Squad, player?.Xres, player?.Yres);

            matchStats.PlayerInfoDictionary.Remove(playerName); // using remove in case the player name changed [upper|lower]case
            matchStats.PlayerInfoDictionary.Add(playerName, playerInfo);
        }

        private void SetStartedPlaying(Player player, MemberStats memberStats, DateTime asOf)
        {
            if (player is null
                || memberStats is null
                || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
            {
                return;
            }

            AddOrUpdatePlayerInfo(memberStats.MatchStats!, player.Name!, player);
            memberStats.StartTime = asOf; // for calculating play time
            ProcessShipUsage(memberStats, asOf, player.Ship);
            AddDamageWatch(player, playerData);
        }

        private void SetStoppedPlaying(Player player, DateTime asOf)
        {
            if (player is null
                || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
            {
                return;
            }

            RemoveDamageWatch(player, playerData);

            MemberStats? memberStats = playerData.MemberStats;
            if (memberStats is not null)
            {
                ProcessPlayTime(memberStats, asOf);
                ProcessShipUsage(memberStats, asOf, null);
            }
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

        private void CalculateDamageSources(ServerTick asOfTick, MemberStats memberStats, ShipType ship, Dictionary<PlayerTeamSlot, int> damageDictionary)
        {
            if (memberStats is null || damageDictionary is null)
                return;

            IMatchData matchData = memberStats.MatchStats!.MatchData!;
            ReadOnlySpan<char> arenaBaseName;
            if (matchData.Arena is not null)
                arenaBaseName = matchData.Arena.BaseName;
            else if (!Arena.TryParseArenaName(matchData.ArenaName, out arenaBaseName, out _))
                return;

            if (!_arenaSettingsLookup.TryGetValue(arenaBaseName, out ArenaSettings? arenaSettings))
                return;

            ShipSettings killedShipSettings = arenaSettings.ShipSettings[(int)ship];
            short maximumEnergy = killedShipSettings.MaximumEnergy;
            short rechargeRate = killedShipSettings.MaximumRecharge;

            memberStats.RemoveOldRecentDamage(maximumEnergy, rechargeRate);

            // How many ticks it takes for the player's ship to reach full energy from empty (maximum energy and recharge rate assumed).
            uint fullEnergyTicks = (uint)float.Ceiling(maximumEnergy * 1000f / rechargeRate);
            ServerTick cutoff = asOfTick - fullEnergyTicks;

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
                LinkedListNode<DamageInfo>? node = memberStats.RecentDamageTaken.Last;
                while (node is not null)
                {
                    LinkedListNode<DamageInfo>? previous = node.Previous;
                    ref DamageInfo damageInfo = ref node.ValueRef;
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

        private void PrintMatchStats(HashSet<Player> notifySet, MatchStats matchStats, MatchEndReason? reason, ITeam? winnerTeam)
        {
            if (notifySet is null || matchStats is null)
                return;

            DateTime now = DateTime.UtcNow;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append($"{(reason is not null ? "Final" : "Current")} {matchStats.MatchData!.MatchIdentifier.MatchType} Score: ");

                StringBuilder scoreBuilder = _objectPoolManager.StringBuilderPool.Get();
                try
                {
                    foreach (TeamStats teamStats in matchStats.Teams.Values)
                    {
                        if (scoreBuilder.Length > 0)
                            scoreBuilder.Append('-');

                        scoreBuilder.Append(teamStats.Team!.Score);
                    }

                    sb.Append(scoreBuilder);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(scoreBuilder);
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

                        case MatchEndReason.Cancelled:
                            sb.Append(" CANCELLED");
                            break;
                    }
                }

                TimeSpan gameDuration = (matchStats.EndTimestamp ?? now) - matchStats.StartTimestamp;
                sb.Append($" -- Game Time: ");
                sb.AppendFriendlyTimeSpan(gameDuration);

                if (matchStats.GameId is not null)
                {
                    sb.Append($" -- Game ID: {matchStats.GameId.Value}");

                    if (!string.IsNullOrWhiteSpace(_subspaceStatsWebUrl))
                    {
                        sb.Append($"  Web: {_subspaceStatsWebUrl}/Game/{matchStats.GameId.Value}");
                    }
                }

                _chat.SendSetMessage(notifySet, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            Span<char> playerName = stackalloc char[Constants.MaxPlayerNameLength];

            foreach (TeamStats teamStats in matchStats.Teams.Values)
            {
                SendHorizonalRule(notifySet);
                _chat.SendSetMessage(notifySet, $"| Freq {teamStats.Team!.Freq,-4}            Ki/De TK SK AS FR WR WRk WEPM   dE Mi LO PTime | DDealt/DTaken DmgE KiDmg FRDmg TmDmg | AcB AcG | Rat TRat |");
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
                int aveRatingChange = 0;
                int aveTotalRating = 0;

                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    for (int memberIndex = 0; memberIndex < slotStats.Members.Count; memberIndex++)
                    {
                        MemberStats memberStats = slotStats.Members[memberIndex];

                        // Format the player name (add a space in front for subs, add trailing spaces).
                        ReadOnlySpan<char> name = memberStats.PlayerName;

                        if (memberIndex == 0)
                        {
                            // initial slot holder (no indentation)
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
                        int? wastedEnergy = memberStats.PlayTime == TimeSpan.Zero ? null : (int)(memberStats.WastedEnergy / memberStats.PlayTime.TotalMinutes);
                        int? aveEnemyDistance = memberStats.DistanceToEnemySum is null || memberStats.DistanceToEnemySamples is null || memberStats.DistanceToEnemySamples < 0 ? null : (int)(memberStats.DistanceToEnemySum / memberStats.DistanceToEnemySamples / 16);
                        int damageDealt = memberStats.DamageDealtBombs + memberStats.DamageDealtBullets;
                        int damageTaken = memberStats.DamageTakenBombs + memberStats.DamageTakenBullets + memberStats.DamageTakenTeam + memberStats.DamageSelf;
                        int totalDamage = damageDealt + damageTaken;
                        float? damageEfficiency = totalDamage > 0 ? (float)damageDealt / totalDamage : null;
                        uint bombMineFireCount = memberStats.BombFireCount + memberStats.MineFireCount;
                        uint bombMineHitCount = memberStats.BombHitCount + memberStats.MineHitCount;
                        float? bombAccuracy = bombMineFireCount > 0 ? (float)bombMineHitCount / bombMineFireCount * 100 : null;
                        float? gunAccuracy = memberStats.GunFireCount > 0 ? (float)memberStats.GunHitCount / memberStats.GunFireCount * 100 : null;
                        int ratingChange = (int)memberStats.RatingChange;
                        int totalRating = Math.Max(memberStats.InitialRating + ratingChange, MinimumRating);

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
                        aveRatingChange += ratingChange;
                        aveTotalRating += totalRating;

                        TimeSpan playTime = memberStats.PlayTime;
                        if (memberStats.StartTime is not null)
                        {
                            TimeSpan duration = (matchStats.EndTimestamp ?? now) - memberStats.StartTime.Value;
                            if (duration > TimeSpan.Zero)
                            {
                                playTime += duration;
                            }
                        }

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
                            $" {wastedEnergy,4}" +
                            $" {aveEnemyDistance,4}" +
                            $" {memberStats.MineFireCount,2}" +
                            $" {memberStats.LagOuts,2}" +
                            $"{(int)playTime.TotalMinutes,3}:{playTime:ss}" +
                            $" | {damageDealt,6}/{damageTaken,6} {damageEfficiency,4:0%} {memberStats.KillDamage,5} {memberStats.ForcedRepDamage,5} {memberStats.DamageDealtTeam,5}" +
                            $" | {bombAccuracy,3:N0} {gunAccuracy,3:N0}" +
                            $" |{ratingChange,4:+#;-#;0} {totalRating,4} |");
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
                    $"     " +
                    $"     " +
                    $" {totalMineFireCount,2}" +
                    $" {totalLagOuts,2}" +
                    $"      " +
                    $" | {totalDamageDealt,6}/{totalDamageTaken,6} {totalDamageEfficiency,4:0%}                  " +
                    $" | {totalBombAccuracy,3:N0} {totalGunAccuracy,3:N0}" +
                    $" |{(aveRatingChange / teamStats.Slots.Count),4:+#;-#;0} {(aveTotalRating / teamStats.Slots.Count),4} |");
            }

            SendHorizonalRule(notifySet);


            void SendHorizonalRule(HashSet<Player> notifySet)
            {
                _chat.SendSetMessage(notifySet, $"+---------------------------------------------------------------------+--------------------------------------+---------+----------+");
            }
        }

        private static void ResetMatchStats(MatchStats matchStats)
        {
            // Clear the existing object.
            foreach (TeamStats teamStats in matchStats.Teams.Values)
            {
                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    foreach (MemberStats memberStats in slotStats.Members)
                    {
                        LinkedListNode<DamageInfo>? node;
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

        private short GetClientSetting(Player player, ClientSettingIdentifier clientSettingIdentifier, short defaultValue)
        {
            ArgumentNullException.ThrowIfNull(player);

            if (_clientSettings.TryGetSettingOverride(player, clientSettingIdentifier, out int maximumRechargeInt))
                return (short)maximumRechargeInt;

            Arena? arena = player.Arena;
            if (arena is not null && _clientSettings.TryGetSettingOverride(arena, clientSettingIdentifier, out maximumRechargeInt))
                return (short)maximumRechargeInt;

            return defaultValue;
        }

        /// <summary>
        /// Processes a player's "play time" stat for when a player stops playing. This clears the timestamp the player started playing.
        /// </summary>
        /// <param name="memberStats">The stats of the player to process.</param>
        /// <param name="asOf">The timestamp to calculate data as of.</param>
        private static void ProcessPlayTime(MemberStats memberStats, DateTime asOf)
        {
            if (memberStats.StartTime is not null)
            {
                TimeSpan duration = asOf - memberStats.StartTime.Value;
                if (duration > TimeSpan.Zero)
                {
                    memberStats.PlayTime += duration;
                }

                memberStats.StartTime = null;
            }
        }

        /// <summary>
        /// Processes a player's ship usage stats.
        /// </summary>
        /// <param name="memberStats">The stats of the player to process.</param>
        /// <param name="asOf">The timestamp to calculate data as of.</param>
        /// <param name="newShip">The player's new ship; <see langword="null"/> for no ship.</param>
        private static void ProcessShipUsage(MemberStats memberStats, DateTime asOf, ShipType? newShip)
        {
            if (memberStats is null)
                return;

            // Add additional usage.
            if (memberStats.CurrentShip is not null && memberStats.CurrentShipStartTime is not null)
            {
                TimeSpan duration = asOf - memberStats.CurrentShipStartTime.Value;
                if (duration > TimeSpan.Zero)
                {
                    memberStats.ShipUsage[(int)memberStats.CurrentShip] += duration;
                }
            }

            // Previous ship.
            if (memberStats.CurrentShip is not null)
            {
                memberStats.PreviousShip = memberStats.CurrentShip;
            }

            // [Re]set current ship variables.
            if (newShip is null || newShip == ShipType.Spec)
            {
                memberStats.CurrentShip = null;
                memberStats.CurrentShipStartTime = null;
            }
            else
            {
                memberStats.CurrentShip = newShip;
                memberStats.CurrentShipStartTime = asOf;
            }
        }

        /// <summary>
        /// Processes a player's "wasted energy" stat.
        /// If the player is in the "full energy" state, it will add to the player's wasted energy stat and reset the "full energy" state.
        /// </summary>
        /// <param name="player">The player to process wasted energy for.</param>
        /// <param name="memberStats">The player's stats.</param>
        /// <param name="ship">
        /// The ship that the player was using, used for calculating energy values.
        /// This is passed in since since <see cref="Player.Ship"/> is the player's current ship, 
        /// which is not going to be the correct ship when processing due to a ship change.
        /// </param>
        /// <param name="asOfTick">Timestamp (ticks) that the wasted energy should be calculated as of.</param>
        private void ProcessWastedEnergy(Player player, MemberStats memberStats, ShipType ship, ServerTick asOfTick)
        {
            if (player is null)
                return;

            if (memberStats is null)
                return;

            //ShipType ship = memberStats.CurrentShip ?? memberStats.PreviousShip ?? ShipType.Spec;

            if (memberStats.FullEnergyStartTime is not null)
            {
                // Add
                Arena? arena = player.Arena;
                if (ship != ShipType.Spec
                    && arena is not null
                    && _arenaSettings.TryGetValue(arena.BaseName, out ArenaSettings? arenaSettings))
                {
                    int shipIndex = (int)ship;
                    ShipSettings arenaShipSettings = arenaSettings.ShipSettings[shipIndex];
                    ref ShipClientSettingIdentifiers shipClientSettingIds = ref _shipClientSettingIds[shipIndex];

                    int duration = asOfTick - memberStats.FullEnergyStartTime.Value;
                    if (duration > 0)
                    {
                        AddWastedEnergy(player, memberStats, arenaShipSettings, shipClientSettingIds, duration);
                    }
                }

                // Reset
                memberStats.FullEnergyStartTime = null;
                memberStats.FullEnergyUtilityStatus = 0;
            }
        }

        private void AddWastedEnergy(Player player, MemberStats memberStats, ShipSettings arenaShipSettings, ShipClientSettingIdentifiers shipClientSettingIds, int duration)
        {
            if (player is null)
                return;

            if (memberStats is null)
                return;

            if (duration <= 0)
                return;

            short utilityDrainRate = 0;

            // stealth
            if ((memberStats.FullEnergyUtilityStatus & PlayerPositionStatus.Stealth) == PlayerPositionStatus.Stealth)
            {
                utilityDrainRate += GetClientSetting(player, shipClientSettingIds.StealthEnergyId, arenaShipSettings.StealthEnergy);
            }

            // cloak
            if ((memberStats.FullEnergyUtilityStatus & PlayerPositionStatus.Cloak) == PlayerPositionStatus.Cloak)
            {
                utilityDrainRate += GetClientSetting(player, shipClientSettingIds.CloakEnergyId, arenaShipSettings.CloakEnergy);
            }

            // x-radar
            if ((memberStats.FullEnergyUtilityStatus & PlayerPositionStatus.XRadar) == PlayerPositionStatus.XRadar)
            {
                utilityDrainRate += GetClientSetting(player, shipClientSettingIds.XRadarEnergyId, arenaShipSettings.XRadarEnergy);
            }

            // anti-warp
            if ((memberStats.FullEnergyUtilityStatus & PlayerPositionStatus.Antiwarp) == PlayerPositionStatus.Antiwarp)
            {
                utilityDrainRate += GetClientSetting(player, shipClientSettingIds.AntiWarpEnergyId, arenaShipSettings.AntiWarpEnergy);
            }

            short maximumRecharge = GetClientSetting(player, shipClientSettingIds.MaximumRechargeId, arenaShipSettings.MaximumRecharge);
            memberStats.WastedEnergy += (int)((maximumRecharge - utilityDrainRate) / 1000f * duration);
        }

        #region Helper types

        private readonly record struct PlayerInfo(string? Squad, short? XRes, short? YRes);

        private class MatchStats
        {
            public IMatchData? MatchData { get; private set; }

            /// <summary>
            /// Key = freq
            /// </summary>
            public readonly SortedList<short, TeamStats> Teams = [];

            /// <summary>
            /// <see cref="PlayerData"/> objects that have their <see cref="PlayerData.MemberStats"/> set for this match.
            /// </summary>
            public readonly HashSet<PlayerData> PlayerDataSet = new(32);

            public readonly Dictionary<string, PlayerInfo> PlayerInfoDictionary = new(StringComparer.OrdinalIgnoreCase);

            public DateTime StartTimestamp;
            public DateTime? EndTimestamp;
            public long? GameId;

            #region First Out

            /// <summary>
            /// Whether the first knockout has occurred.
            /// </summary>
            public bool FirstOutProcessed;

            /// <summary>
            /// The stats of the player that is marked as "First Out".
            /// <see langword="null"/> if no player is marked as "First Out".
            /// </summary>
            public MemberStats? FirstOut;

            /// <summary>
            /// Whether the <see cref="FirstOut"/> is considered "critical" based on certain criteria.
            /// </summary>
            public bool FirstOutCritical;

            #endregion

            private MemoryStream? _eventsJsonStream;
            private Utf8JsonWriter? _eventsJsonWriter;

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
                PlayerInfoDictionary.Clear();
                StartTimestamp = DateTime.MinValue;
                EndTimestamp = null;
                GameId = null;
                FirstOutProcessed = false;
                FirstOut = null;
                FirstOutCritical = false;

                if (_eventsJsonWriter is not null)
                {
                    _eventsJsonWriter.Dispose();
                    _eventsJsonWriter = null;
                }

                if (_eventsJsonStream is not null)
                {
                    _eventsJsonStream.Dispose();
                    _eventsJsonStream = null;
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
                // The ship change event only 
                if (ship == ShipType.Spec)
                    return;

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
                List<(string PlayerName, int Damage)>? damageList,
                Dictionary<string, float>? ratingChangeDictionary)
            {
                if (_eventsJsonWriter is null)
                    return;

                _eventsJsonWriter.WriteStartObject();
                _eventsJsonWriter.WriteNumber("event_type_id"u8, (int)GameEventType.TeamVersus_PlayerUseItem);
                _eventsJsonWriter.WriteString("timestamp"u8, timestamp);
                _eventsJsonWriter.WriteString("player"u8, playerName);
                _eventsJsonWriter.WriteNumber("ship_item_id"u8, (int)item);

                WriteDamageStats(damageList);
                WriteRatingChanges(ratingChangeDictionary);

                _eventsJsonWriter.WriteEndObject();
            }

            public void AddKillEvent(
                DateTime timestamp,
                string killedName,
                ShipType killedShip,
                string killerName,
                ShipType killerShip,
                bool isKnockout,
                bool isTeamKill,
                short xCoord,
                short yCoord,
                List<(string PlayerName, int Damage)> damageList,
                Dictionary<string, float> ratingChangeDictionary)
            {
                if (_eventsJsonWriter is null)
                    return;

                _eventsJsonWriter.WriteStartObject();
                _eventsJsonWriter.WriteNumber("event_type_id"u8, (int)GameEventType.TeamVersus_PlayerKill);
                _eventsJsonWriter.WriteString("timestamp"u8, timestamp);
                _eventsJsonWriter.WriteString("killed_player"u8, killedName);
                _eventsJsonWriter.WriteString("killer_player"u8, killerName);
                _eventsJsonWriter.WriteBoolean("is_knockout"u8, isKnockout);
                _eventsJsonWriter.WriteBoolean("is_team_kill"u8, isTeamKill);
                _eventsJsonWriter.WriteNumber("x_coord"u8, xCoord);
                _eventsJsonWriter.WriteNumber("y_coord"u8, yCoord);
                _eventsJsonWriter.WriteNumber("killed_ship"u8, (int)killedShip);
                _eventsJsonWriter.WriteNumber("killer_ship"u8, (int)killerShip);

                _eventsJsonWriter.WriteStartArray("score");
                foreach (var teamStats in Teams.Values)
                {
                    _eventsJsonWriter.WriteNumberValue(teamStats.Team!.Score);
                }
                _eventsJsonWriter.WriteEndArray();

                _eventsJsonWriter.WriteStartArray("remaining_slots");
                foreach (var teamStats in Teams.Values)
                {
                    int remainingSlots = 0;
                    foreach (var slotStats in teamStats.Slots)
                    {
                        if (slotStats.Slot!.Lives > 0)
                        {
                            remainingSlots++;
                        }
                    }

                    _eventsJsonWriter.WriteNumberValue(remainingSlots);
                }
                _eventsJsonWriter.WriteEndArray();

                WriteDamageStats(damageList);
                WriteRatingChanges(ratingChangeDictionary);

                _eventsJsonWriter.WriteEndObject();
            }

            private void WriteDamageStats(List<(string PlayerName, int Damage)>? damageList)
            {
                if (_eventsJsonWriter is null)
                    return;

                if (damageList is not null && damageList.Count > 0)
                {
                    _eventsJsonWriter.WriteStartObject("damage_stats");
                    foreach ((string playerName, int damage) in damageList)
                    {
                        _eventsJsonWriter.WriteNumber(playerName, damage);
                    }
                    _eventsJsonWriter.WriteEndObject(); // damage_stats object
                }
            }

            private void WriteRatingChanges(Dictionary<string, float>? ratingChangeDictionary)
            {
                if (_eventsJsonWriter is null)
                    return;

                if (ratingChangeDictionary is not null && ratingChangeDictionary.Count > 0)
                {
                    _eventsJsonWriter.WriteStartObject("rating_changes");
                    foreach ((string playerName, float rating) in ratingChangeDictionary)
                    {
                        _eventsJsonWriter.WriteNumber(playerName, rating);
                    }
                    _eventsJsonWriter.WriteEndObject(); // rating_changes object
                }
            }

            public void WriteEventsArray(Utf8JsonWriter writer)
            {
                if (_eventsJsonWriter is null)
                    return;

                _eventsJsonWriter.WriteEndArray();
                _eventsJsonWriter.Flush();

                _eventsJsonStream!.Position = 0;

                if (_eventsJsonStream is RecyclableMemoryStream rms)
                {
                    writer.WriteRawValue(rms.GetReadOnlySequence());
                }
                else
                {
                    writer.WriteNullValue();
                }
            }
        }

        private class TeamStats
        {
            public MatchStats? MatchStats { get; private set; }
            public ITeam? Team { get; private set; }

            /// <summary>
            /// Player slots
            /// e.g. in a 4v4 match, there would be 4 slots. 
            /// </summary>
            public readonly List<SlotStats> Slots = [];

            public int RemainingSlots;
            public int AverageRating;

            public void Initialize(MatchStats matchStats, ITeam team)
            {
                MatchStats = matchStats ?? throw new ArgumentNullException(nameof(matchStats));
                Team = team ?? throw new ArgumentNullException(nameof(team));

                AverageRating = DefaultRating;
            }

            public void RefreshRemainingSlotsAndAverageRating()
            {
                RemainingSlots = 0;
                int sum = 0;
                int count = 0;

                foreach (SlotStats slotStats in Slots)
                {
                    IPlayerSlot slot = slotStats.Slot!;
                    if (slot.Lives > 0)
                    {
                        RemainingSlots++;

                        MemberStats? currentMemberStats = slotStats.Current;
                        if (currentMemberStats is not null)
                        {
                            sum += currentMemberStats.InitialRating;
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    AverageRating = sum / count;
                }
            }

            public void Reset()
            {
                MatchStats = null;
                Team = null;
                Slots.Clear();
                RemainingSlots = 0;
                AverageRating = DefaultRating;
            }
        }

        private class SlotStats
        {
            public MatchStats? MatchStats => TeamStats?.MatchStats;
            public TeamStats? TeamStats { get; private set; }
            public IPlayerSlot? Slot { get; private set; }

            /// <summary>
            /// Stats for each player that occupied the slot.
            /// </summary>
            public readonly List<MemberStats> Members = [];

            /// <summary>
            /// Stats of the player that currently holds the slot.
            /// </summary>
            public MemberStats? Current;

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

        private class MemberStats : IMemberStats
        {
            public MatchStats? MatchStats => TeamStats?.MatchStats;
            public TeamStats? TeamStats => SlotStats?.TeamStats;
            public SlotStats? SlotStats { get; private set; }

            /// <summary>
            /// Whether the stats are for the current slot holder.
            /// </summary>
            public bool IsCurrent => SlotStats?.Current == this;

            public string? PlayerName { get; private set; }

            //public Player Player; // TODO: keep track of player so that we can send notifications to even those that are no longer the current slot holder?

            public int? PremadeGroupId;

            #region Ship Usage

            public readonly TimeSpan[] ShipUsage = new TimeSpan[8];

            public ShipType? CurrentShip = null;
            public DateTime? CurrentShipStartTime = null;
            public ShipType? PreviousShip = null;

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
            /// </summary>
            /// <remarks>
            /// Used upon death to calculate <see cref="KillDamage"/>, <see cref="TeamKillDamage"/>, <see cref="SoloKills"/>, and <see cref="Assists"/>.
            /// Used upon repel usage to calculate <see cref="ForcedRepDamage"/> and <see cref="ForcedReps"/>.
            /// </remarks>
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

            #region Wasted Energy

            /// <summary>
            /// Time (ticks) of the latest position packet.
            /// </summary>
            public ServerTick? LastPositionTime;

            /// <summary>
            /// Time (ticks) when the player hit full energy.
            /// </summary>
            public ServerTick? FullEnergyStartTime;

            /// <summary>
            /// Whether the player had stealth, cloak, xradar, or antiwarp on at <see cref="FullEnergyStartTime"/>.
            /// This is used to calculate how much recharge is wasted (maximum recharge rate - cost of having the utilities activated).
            /// </summary>
            public PlayerPositionStatus FullEnergyUtilityStatus;

            /// <summary>
            /// The amount of energy that would have recharged, but the player was already at full energy.
            /// </summary>
            public int WastedEnergy;

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

            #region Distance

            /// <summary>
            /// The sum of the distances to the nearest enemy player (in pixels), periodically taken.
            /// </summary>
            public ulong? DistanceToEnemySum;

            /// <summary>
            /// The number of samples taken for <see cref="DistanceToEnemySum"/>.
            /// </summary>
            public uint? DistanceToEnemySamples;

            /// <summary>
            /// The sum of the distances to the nearest teammate (in pixels), periodically taken.
            /// </summary>
            public ulong? DistanceToTeamSum;

            /// <summary>
            /// The number of samples taken for <see cref="DistanceToTeamSum"/>.
            /// </summary>
            public uint? DistanceToTeamSamples;

            #endregion

            /// <summary>
            /// Kills of enemy players.
            /// </summary>
            public short Kills { get; set; }

            /// <summary>
            /// Knockout kills of enemy players.
            /// </summary>
            public short Knockouts;

            /// <summary>
            /// Kills of enemy players made without the assistance others.
            /// </summary>
            public short SoloKills;

            /// <summary>
            /// Kills of teammates.
            /// </summary>
            public short TeamKills;

            /// <summary>
            /// Deaths.
            /// </summary>
            public short Deaths { get; set; }

            /// <summary>
            /// Kills assisted.
            /// </summary>
            public short Assists;

            /// <summary>
            /// Repels forced out of enemy players.
            /// </summary>
            public short ForcedReps;

            /// <summary>
            /// Duration of play.
            /// </summary>
            public TimeSpan PlayTime;

            /// <summary>
            /// # of times lagged out (changed to spec or disconnected).
            /// </summary>
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

            #region Rating

            /// <summary>
            /// The player's rating at the start of the match.
            /// </summary>
            public int InitialRating;

            /// <summary>
            /// The change in the player's rating due to the current match.
            /// This is an indicator of the player's individual performance in a match.
            /// </summary>
            public float RatingChange;

            #endregion

            #region Ranking

            // Matchmaking rating for the ranking system.
            //public int RankingMMR;

            // Standard deviation in the player's current MMR rank.
            //public int RankingReliabilityDeviation;

            // Volatility in the player's current MMR rank.
            //public int RankingVolatility;

            #endregion

            public void Initialize(SlotStats slotStats, string playerName, bool isInitial)
            {
                ArgumentException.ThrowIfNullOrEmpty(playerName);

                SlotStats = slotStats ?? throw new ArgumentNullException(nameof(slotStats));
                PlayerName = playerName;

                PremadeGroupId = isInitial ? slotStats.Slot!.PremadeGroupId : null;

                for (int shipIndex = 0; shipIndex < ShipUsage.Length; shipIndex++)
                {
                    ShipUsage[shipIndex] = TimeSpan.Zero;
                }

                InitialRating = DefaultRating;
                RatingChange = 0;
            }

            public void RemoveOldRecentDamage(short maximumEnergy, short rechargeRate)
            {
                // How many ticks it takes for the player's ship to reach full energy from empty (maximum energy and recharge rate assumed).
                uint fullEnergyTicks = (uint)float.Ceiling(maximumEnergy * 1000f / rechargeRate);

                // Remove nodes that are too old to be relevant.
                ServerTick cutoff = ServerTick.Now - fullEnergyTicks;
                LinkedListNode<DamageInfo>? node = RecentDamageTaken.First;
                while (node is not null)
                {
                    if (node.ValueRef.Timestamp + node.ValueRef.EmpBombShutdownTicks < cutoff)
                    {
                        // The node represents damage taken from too long ago.
                        // Discard the node and continue with the next node.
                        LinkedListNode<DamageInfo>? next = node.Next;
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
                LinkedListNode<DamageInfo>? node;
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

                // wasted energy
                LastPositionTime = null;
                FullEnergyStartTime = null;
                FullEnergyUtilityStatus = 0;
                WastedEnergy = 0;

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

                // rating
                InitialRating = DefaultRating;
                RatingChange = 0;
            }
        }

        [Flags]
        private enum FirstOut
        {
            /// <summary>
            /// The player was not the first player in the match to get knocked out first.
            /// </summary>
            No = 0x00,

            /// <summary>
            /// The player was the first player in the match to get knocked out.
            /// </summary>
            Yes = 0x01,

            /// <summary>
            /// The player was the first player in the match to get knocked out and met the criteria for being critical.
            /// The criteria for critical is: knocked out under 10 minutes and had less than 2 kills.
            /// </summary>
            YesCritical = 0x03,
        }

        private class PlayerData : IResettable
        {
            private MemberStats? _memberStats;

            /// <summary>
            /// The player's stats of the match in progress that the player is associated with.
            /// </summary>
            /// <remarks>
            /// This is set when player gets associated with the match (match starts or the player subs into an existing match).
            /// Also, if the player disconnects and reconnects, it is restored upon connect.
            /// The player stays associated, until the match ends or the player is associated with a new match.
            /// This is so that even if a player changes to spec, stats can still be updated for the player 
            /// (for example damage data already in transit before the other players recognize the player is no longer in ship).
            /// </remarks>
            public MemberStats? MemberStats
            {
                get => _memberStats;
                set
                {
                    // If there's an existing one, unregister it.
                    _memberStats?.MatchStats?.PlayerDataSet.Remove(this);

                    // Set the new value and register it if not null.
                    _memberStats = value;
                    _memberStats?.MatchStats?.PlayerDataSet.Add(this);
                }
            }

            /// <summary>
            /// Whether damage is being watched for the player via IWatchDamage.
            /// </summary>
            public bool IsWatchingDamage = false;

            /// <summary>
            /// Tracks recent weapon use. This is used to prevent recording duplicate packets.
            /// </summary>
            private readonly LinkedList<WeaponUse> WeaponUseList = new();

            /// <summary>
            /// Removes outdated weapon use log entries.
            /// </summary>
            public void TrimWeaponUseLog()
            {
                // Keep track of up to 5 seconds worth of weapon usage data.
                ServerTick cutoff = ServerTick.Now - 500u;

                LinkedListNode<WeaponUse>? node = WeaponUseList.First;
                while (node is not null)
                {
                    if (node.ValueRef.Timestamp >= cutoff)
                        return;

                    WeaponUseList.Remove(node);
                    s_weaponUseLinkedListNodePool.Return(node);
                    node = WeaponUseList.First;
                }
            }

            /// <summary>
            /// Adds a weapon use log entry.
            /// </summary>
            /// <param name="weapon">The weapon to log use of.</param>
            /// <param name="timestamp">The timestamp that the weapon was used.</param>
            /// <returns><see langword="true"/> if a log was added. Otherwise, <see langword="false"/> if there was already a matching log (this is a duplicate).</returns>
            public bool AddWeaponUse(WeaponCodes weapon, ServerTick timestamp)
            {
                LinkedListNode<WeaponUse>? node = WeaponUseList.Last;
                while (node is not null)
                {
                    ref WeaponUse weaponUse = ref node.ValueRef;
                    if (weaponUse.Timestamp == timestamp && weaponUse.Weapon == weapon)
                        return false; // dup

                    if (weaponUse.Timestamp < timestamp)
                        break;

                    node = node.Previous;
                }

                LinkedListNode<WeaponUse> addNode = s_weaponUseLinkedListNodePool.Get();
                addNode.ValueRef = new WeaponUse(weapon, timestamp);

                if (node is null)
                    WeaponUseList.AddFirst(addNode);
                else
                    WeaponUseList.AddAfter(node, addNode);

                return true;
            }

            /// <summary>
            /// Clears all weapon use log entries.
            /// </summary>
            public void ClearWeaponUseLog()
            {
                LinkedListNode<WeaponUse>? node;
                while ((node = WeaponUseList.First) is not null)
                {
                    WeaponUseList.Remove(node);
                    s_weaponUseLinkedListNodePool.Return(node);
                }
            }

            public bool TryReset()
            {
                MemberStats = null;
                IsWatchingDamage = false;
                ClearWeaponUseLog();
                return true;
            }
        }

        private readonly record struct WeaponUse(WeaponCodes Weapon, ServerTick Timestamp);

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

            public override bool Equals([NotNullWhen(true)] object? obj)
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

        private readonly struct DamageInfo
        {
            /// <summary>
            /// When the damage was inflicted.
            /// </summary>
            public readonly required ServerTick Timestamp { get; init; }

            /// <summary>
            /// The amount of direct damage dealt.
            /// </summary>
            public readonly required short Damage { get; init; }

            /// <summary>
            /// The amount of time recharge is stopped due to an EMP bomb/mine. 0 for everything else.
            /// </summary>
            public readonly required uint EmpBombShutdownTicks { get; init; }

            /// <summary>
            /// Identifies who (player, team, and slot) inflicted the damage.
            /// This can be damage taken from an enemy, from an teammate, or self damage.
            /// </summary>
            public readonly required PlayerTeamSlot Attacker { get; init; }
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
            /// All:EmpBomb - Whether the ship fires EMP bombs (0 = no, 1 = yes).
            /// </summary>
            public required ClientSettingIdentifier EmpBombId;

            /// <summary>
            /// All:CloakEnergy - Amount of energy required to have 'Cloak' activated (thousandths per hundredth of a second).
            /// </summary>
            public required ClientSettingIdentifier CloakEnergyId;

            /// <summary>
            /// All:StealthEnergy - Amount of energy required to have 'Stealth' activated (thousandths per hundredth of a second).
            /// </summary>
            public required ClientSettingIdentifier StealthEnergyId;

            /// <summary>
            /// All:XRadarEnergy - Amount of energy required to have 'X-Radar' activated (thousandths per hundredth of a second).
            /// </summary>
            public required ClientSettingIdentifier XRadarEnergyId;

            /// <summary>
            /// All:AntiWarpEnergy - Amount of energy required to have 'Anti-Warp' activated (thousandths per hundredth of a second).
            /// </summary>
            public required ClientSettingIdentifier AntiWarpEnergyId;
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
            /// All:EmpBomb - Whether the ship fires EMP bombs (0 = no, 1 = yes).
            /// </summary>
            public required bool HasEmpBomb;

            /// <summary>
            /// All:CloakEnergy - Amount of energy required to have 'Cloak' activated (thousandths per hundredth of a second).
            /// </summary>
            public required short CloakEnergy;

            /// <summary>
            /// All:StealthEnergy - Amount of energy required to have 'Stealth' activated (thousandths per hundredth of a second).
            /// </summary>
            public required short StealthEnergy;

            /// <summary>
            /// All:XRadarEnergy - Amount of energy required to have 'X-Radar' activated (thousandths per hundredth of a second).
            /// </summary>
            public required short XRadarEnergy;

            /// <summary>
            /// All:AntiWarpEnergy - Amount of energy required to have 'Anti-Warp' activated (thousandths per hundredth of a second).
            /// </summary>
            public required short AntiWarpEnergy;
        }

        private class ArenaData : IResettable
        {
            public InterfaceRegistrationToken<ILeagueHelp>? ILeagueHelpToken;

            bool IResettable.TryReset()
            {
                ILeagueHelpToken = null;
                return true;
            }
        }

        #endregion

        #region Pooled object policies

        private class TickRangeCalculatorPooledObjectPolicy : IPooledObjectPolicy<TickRangeCalculator>
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

        #endregion
    }
}

