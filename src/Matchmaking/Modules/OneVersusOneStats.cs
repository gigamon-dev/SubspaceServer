using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.Json;
using System.Text;
using System.Text.Json;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that tracks stats for 1v1 matches.
    /// </summary>
    [ModuleInfo($"""
        Tracks stats for 1v1 matches.
        For use with the {nameof(OneVersusOneMatch)} module.
        """)]
    public class OneVersusOneStats : IModule, IArenaAttachableModule
    {
        private readonly IChat _chat;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IMapData _mapData;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IWatchDamage _watchDamage;

        // optional
        private IGameStatsRepository? _gameStatsRepository;

        private PlayerDataKey<PlayerData> _pdKey;

        private string? _zoneServerName;
        private readonly Dictionary<string, (string LvlFilename, uint Checksum)> _arenaMapInfo = [];
        private readonly Dictionary<MatchIdentifier, MatchStats> _matchStats = new(32);
        private readonly DefaultObjectPool<MatchStats> _matchStatsObjectPool = new(new DefaultPooledObjectPolicy<MatchStats>(), Constants.TargetPlayerCount);

        public OneVersusOneStats(
            IChat chat,
            IConfigManager configManager,
            ILogManager logManager,
            IMapData mapData,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IWatchDamage watchDamage)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _watchDamage = watchDamage ?? throw new ArgumentNullException(nameof(watchDamage));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
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
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _playerData.FreePlayerData(ref _pdKey);

            if (_gameStatsRepository is not null)
                broker.ReleaseInterface(ref _gameStatsRepository);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            OneVersusOneMatchStartedCallback.Register(arena, Callback_OneVersusOneMatchStarted);
            OneVersusOneMatchEndedCallback.Register(arena, Callback_OneVersusOneMatchEnded);
            ArenaActionCallback.Register(arena, Callback_ArenaAction);
            PlayerDamageCallback.Register(arena, Callback_PlayerDamage);
            PlayerPositionPacketCallback.Register(arena, Callback_PlayerPositionPacket);
            KillCallback.Register(arena, Callback_Kill);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            OneVersusOneMatchStartedCallback.Unregister(arena, Callback_OneVersusOneMatchStarted);
            OneVersusOneMatchEndedCallback.Unregister(arena, Callback_OneVersusOneMatchEnded);
            ArenaActionCallback.Unregister(arena, Callback_ArenaAction);
            PlayerDamageCallback.Unregister(arena, Callback_PlayerDamage);
            PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPositionPacket);
            KillCallback.Unregister(arena, Callback_Kill);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            return true;
        }

        #endregion

        #region Callbacks

        private async void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.Create)
            {
                string? mapPath = await _mapData.GetMapFilenameAsync(arena, null);
                if (mapPath is not null)
                    mapPath = Path.GetFileName(mapPath);

                _arenaMapInfo[arena.Name] = (mapPath!, _mapData.GetChecksum(arena, 0));
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.LeaveArena)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                    return;

                RemoveDamageWatching(player, playerData);
            }
            else if (action == PlayerAction.Disconnect)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                    return;

                if (playerData.CurrentMatchStats != null)
                {
                    if (playerData.CurrentMatchStats.PlayerStats1.Player == player)
                        playerData.CurrentMatchStats.PlayerStats1.Player = null;
                    else if (playerData.CurrentMatchStats.PlayerStats2.Player == player)
                        playerData.CurrentMatchStats.PlayerStats2.Player = null;

                    playerData.CurrentMatchStats = null;
                }

                if (playerData.CurrentPlayerStats != null)
                {
                    playerData.CurrentPlayerStats = null;
                }
            }
        }

        private void Callback_OneVersusOneMatchStarted(Arena arena, int boxId, Player player1, Player player2)
        {
            if (!player1.TryGetExtraData(_pdKey, out PlayerData? player1Data)
                || !player2.TryGetExtraData(_pdKey, out PlayerData? player2Data))
            {
                return;
            }

            MatchIdentifier matchIdentifier = new(arena, boxId);
            if (!_matchStats.TryGetValue(matchIdentifier, out MatchStats? matchStats))
            {
                matchStats = _matchStatsObjectPool.Get();
                _matchStats.Add(matchIdentifier, matchStats);
            }

            matchStats.SetStart(player1, player2);
            player1Data.CurrentMatchStats = player2Data.CurrentMatchStats = matchStats;
            player1Data.CurrentPlayerStats = matchStats.PlayerStats1;
            player2Data.CurrentPlayerStats = matchStats.PlayerStats2;

            _watchDamage.AddCallbackWatch(player1);
            player1Data.IsWatchingDamage = true;

            _watchDamage.AddCallbackWatch(player2);
            player2Data.IsWatchingDamage = true;
        }

        private async void Callback_OneVersusOneMatchEnded(Arena arena, int boxId, OneVersusOneMatchEndReason reason, string? winnerPlayerName)
        {
            MatchIdentifier matchIdentifier = new(arena, boxId);
            if (!_matchStats.Remove(matchIdentifier, out MatchStats? matchStats))
                return;

            try
            {
                matchStats.SetEnd();

                //
                // End damage watches
                //

                if (matchStats.PlayerStats1.Player != null
                    && matchStats.PlayerStats1.Player.TryGetExtraData(_pdKey, out PlayerData? player1Data))
                {
                    RemoveDamageWatching(matchStats.PlayerStats1.Player, player1Data);
                }

                if (matchStats.PlayerStats2.Player != null
                    && matchStats.PlayerStats2.Player.TryGetExtraData(_pdKey, out PlayerData? player2Data))
                {
                    RemoveDamageWatching(matchStats.PlayerStats2.Player, player2Data);
                }

                if (reason != OneVersusOneMatchEndReason.Aborted)
                {
                    //
                    // Output stats
                    //

                    if (reason == OneVersusOneMatchEndReason.Decided)
                    {
                        PlayerStats? winnerStats = null;
                        PlayerStats? loserStats = null;

                        if (string.Equals(winnerPlayerName, matchStats.PlayerStats1.PlayerName, StringComparison.OrdinalIgnoreCase))
                        {
                            winnerStats = matchStats.PlayerStats1;
                            loserStats = matchStats.PlayerStats2;
                        }
                        else if (string.Equals(winnerPlayerName, matchStats.PlayerStats2.PlayerName, StringComparison.OrdinalIgnoreCase))
                        {
                            winnerStats = matchStats.PlayerStats2;
                            loserStats = matchStats.PlayerStats1;
                        }

                        if (winnerStats != null && loserStats != null)
                        {
                            // Update win streak counters.
                            int? winStreak = null;
                            if (winnerStats.Player != null
                                && winnerStats.Player.TryGetExtraData(_pdKey, out PlayerData? winnerPlayerData))
                            {
                                winStreak = ++winnerPlayerData.WinStreak;
                            }

                            if (loserStats.Player != null
                                && loserStats.Player.TryGetExtraData(_pdKey, out PlayerData? loserPlayerData))
                            {
                                loserPlayerData.WinStreak = 0;
                            }

                            // Basic win/lose info to the arena
                            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                            try
                            {
                                sb.Append($"{winnerStats.PlayerName} defeated {loserStats.PlayerName}");

                                if (winnerStats.EndEnergy != null)
                                    sb.Append($" (energy {winnerStats.EndEnergy.Value})");

                                if (winStreak != null && winStreak > 1)
                                    sb.Append($" (won {winStreak} in a row)");

                                _chat.SendArenaMessage(arena, sb);
                            }
                            finally
                            {
                                _objectPoolManager.StringBuilderPool.Return(sb);
                            }
                        }
                    }

                    // Detailed stats to the players and those specifically spectating the players
                    HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
                    try
                    {
                        if (matchStats.PlayerStats1.Player != null)
                            set.Add(matchStats.PlayerStats1.Player);

                        if (matchStats.PlayerStats2.Player != null)
                            set.Add(matchStats.PlayerStats2.Player);

                        // TODO: determine who is spectating the player and add them to the set too

                        if (reason == OneVersusOneMatchEndReason.Draw)
                        {
                            _chat.SendSetMessage(set, $"{matchStats.PlayerStats1.PlayerName} vs {matchStats.PlayerStats2.PlayerName} ended in a draw (double knockout)");
                        }

                        PlayerStats playerStats1 = matchStats.PlayerStats1;
                        PlayerStats playerStats2 = matchStats.PlayerStats2;

                        uint damageDealt1 = playerStats1.BombDamageDealt + playerStats1.GunDamageDealt;
                        uint damageDealt2 = playerStats2.BombDamageDealt + playerStats2.GunDamageDealt;
                        uint totalDamageDealt = damageDealt1 + damageDealt2;
                        float? damageDealtRatio1 = totalDamageDealt > 0 ? (float)damageDealt1 / totalDamageDealt : null;
                        float? damageDealtRatio2 = totalDamageDealt > 0 ? (float)damageDealt2 / totalDamageDealt : null;
                        uint damageTaken1 = damageDealt2 + playerStats1.DamageSelf;
                        uint damageTaken2 = damageDealt1 + playerStats2.DamageSelf;
                        uint totalDamageTaken = damageTaken1 + damageTaken2;
                        float? damageTakenRatio1 = totalDamageTaken > 0 ? (float)damageTaken1 / totalDamageTaken : null;
                        float? damageTakenRatio2 = totalDamageTaken > 0 ? (float)damageTaken2 / totalDamageTaken : null;
                        uint totalDamage1 = damageDealt1 + damageTaken1;
                        uint totalDamage2 = damageDealt2 + damageTaken2;
                        float? damageEfficiency1 = totalDamage1 > 0 ? (float)damageDealt1 / totalDamage1 : null;
                        float? damageEfficiency2 = totalDamage2 > 0 ? (float)damageDealt2 / totalDamage2 : null;
                        uint bombFireCount1 = playerStats1.BombFireCount + playerStats1.MineFireCount;
                        uint bombFireCount2 = playerStats2.BombFireCount + playerStats2.MineFireCount;
                        uint bombHitCount1 = playerStats1.BombHitCount + playerStats1.MineHitCount;
                        uint bombHitCount2 = playerStats2.BombHitCount + playerStats2.MineHitCount;
                        float? bombAccuracy1 = bombFireCount1 > 0 ? (float)bombHitCount1 / bombFireCount1 : null;
                        float? bombAccuracy2 = bombFireCount2 > 0 ? (float)bombHitCount2 / bombFireCount2 : null;
                        float? gunAccuracy1 = playerStats1.GunFireCount > 0 ? (float)playerStats1.GunHitCount / playerStats1.GunFireCount : null;
                        float? gunAccuracy2 = playerStats2.GunFireCount > 0 ? (float)playerStats2.GunHitCount / playerStats2.GunFireCount : null;

                        _chat.SendSetMessage(set, $"+----------------------+--------+------------------------------+------------------+------------------+");
                        _chat.SendSetMessage(set, $"| Name                 | Energy | DDealt    % Dtaken    % DmgE | Bomb Accuracy    | Gun Accuracy     |");
                        _chat.SendSetMessage(set, $"+----------------------+--------+------------------------------+------------------+------------------+");
                        _chat.SendSetMessage(set, $"| {playerStats1.PlayerName,-20} |  {playerStats1.EndEnergy,5} | {damageDealt1,6} {damageDealtRatio1,4:0%} {damageTaken1,6} {damageTakenRatio1,4:0%} {damageEfficiency1,4:0%} | {bombAccuracy1,4:0%} {bombHitCount1,5}/{bombFireCount1,5} | {gunAccuracy1,4:0%} {playerStats1.GunHitCount,5}/{playerStats1.GunFireCount,5} |");
                        _chat.SendSetMessage(set, $"| {playerStats2.PlayerName,-20} |  {playerStats2.EndEnergy,5} | {damageDealt2,6} {damageDealtRatio2,4:0%} {damageTaken2,6} {damageTakenRatio2,4:0%} {damageEfficiency2,4:0%} | {bombAccuracy2,4:0%} {bombHitCount2,5}/{bombFireCount2,5} | {gunAccuracy2,4:0%} {playerStats2.GunHitCount,5}/{playerStats2.GunFireCount,5} |");
                        _chat.SendSetMessage(set, $"+----------------------+--------+------------------------------+------------------+------------------+");
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(set);
                    }

                    //
                    // Save game stats to database
                    //

                    await SaveGameToDatabase(matchIdentifier, matchStats, winnerPlayerName);
                }

                //
                // Clear current match
                //

                if (matchStats.PlayerStats1.Player != null)
                {
                    ClearCurrentMatchFromPlayerData(matchStats.PlayerStats1.Player);
                }

                if (matchStats.PlayerStats2.Player != null)
                {
                    ClearCurrentMatchFromPlayerData(matchStats.PlayerStats2.Player);
                }
            }
            finally
            {
                _matchStatsObjectPool.Return(matchStats);
            }

            async Task SaveGameToDatabase(MatchIdentifier matchIdentifier, MatchStats matchStats, string? winnerPlayerName)
            {
                if (_gameStatsRepository is null)
                    return;

                if (matchStats.StartTimestamp is null || matchStats.EndTimestamp is null)
                    return;

                // TODO: pool the MemoryStream and Utf8JsonWriter combination
                using MemoryStream gameJsonStream = new();
                using Utf8JsonWriter writer = new(gameJsonStream, default);

                writer.WriteStartObject(); // game object
                writer.WriteNumber("game_type_id"u8, 1); // TODO: configuration
                writer.WriteString("zone_server_name"u8, _zoneServerName);
                writer.WriteString("arena"u8, matchIdentifier.Arena.Name);
                writer.WriteNumber("box_number"u8, matchIdentifier.BoxId);

                // Map info
                if (_arenaMapInfo.TryGetValue(matchIdentifier.Arena.Name, out var mapInfo))
                {
                    writer.WriteString("lvl_file_name"u8, mapInfo.LvlFilename);
                    writer.WriteNumber("lvl_checksum"u8, mapInfo.Checksum);
                }
                else
                {
                    writer.WriteString("lvl_file_name"u8, "");
                    writer.WriteNumber("lvl_checksum"u8, 0);
                }

                writer.WriteString("start_timestamp"u8, matchStats.StartTimestamp.Value);
                writer.WriteString("end_timestamp"u8, matchStats.EndTimestamp.Value);
                writer.WriteString("replay_path"u8, (string?)null); // TODO: add the ability automatically record games

                // Player info
                writer.WriteStartObject("players");
                WritePlayerInfo(matchStats.PlayerStats1);
                WritePlayerInfo(matchStats.PlayerStats2);
                writer.WriteEndObject(); // players object

                // Stats
                writer.WriteStartArray("solo_stats"u8);
                WriteMemberStats(matchStats, matchStats.PlayerStats1, matchStats.PlayerStats2, winnerPlayerName);
                WriteMemberStats(matchStats, matchStats.PlayerStats2, matchStats.PlayerStats1, winnerPlayerName);
                writer.WriteEndArray(); // solo_stats

                writer.WriteEndObject(); // game object

                writer.Flush();
                gameJsonStream.Position = 0;

                // DEBUG - REMOVE ME ***************************************************
                //StreamReader reader = new(gameJsonStream, Encoding.UTF8);
                //string data = reader.ReadToEnd();
                //Console.WriteLine(data);
                //gameJsonStream.Position = 0;
                // DEBUG - REMOVE ME ***************************************************

                matchStats.GameId = await _gameStatsRepository.SaveGameAsync(gameJsonStream);

                void WritePlayerInfo(PlayerStats playerStats)
                {
                    writer.WriteStartObject(playerStats.PlayerName!);

                    writer.WriteString("squad"u8, playerStats.Squad);

                    if (playerStats.XRes is not null && playerStats.YRes is not null)
                    {
                        writer.WriteNumber("x_res"u8, playerStats.XRes.Value);
                        writer.WriteNumber("y_res"u8, playerStats.YRes.Value);
                    }

                    writer.WriteEndObject();
                }

                void WriteMemberStats(MatchStats matchStats, PlayerStats playerStats, PlayerStats otherPlayerStats, string? winnerPlayerName)
                {
                    writer.WriteStartObject();
                    writer.WriteString("player"u8, playerStats.PlayerName);
                    writer.TryWriteTimeSpanAsISO8601("play_duration"u8, matchStats.EndTimestamp!.Value - matchStats.StartTimestamp!.Value);

                    string? shipTypeStr = playerStats.ShipType!.Value switch
                    {
                        ShipType.Warbird => "warbird",
                        ShipType.Javelin => "javelin",
                        ShipType.Spider => "spider",
                        ShipType.Leviathan => "leviathan",
                        ShipType.Terrier => "terrier",
                        ShipType.Weasel => "weasel",
                        ShipType.Lancaster => "lancaster",
                        ShipType.Shark => "shark",
                        _ => null
                    };

                    if (shipTypeStr is not null)
                    {
                        writer.WriteStartObject("ship_usage"u8);
                        writer.TryWriteTimeSpanAsISO8601(shipTypeStr, matchStats.EndTimestamp.Value - matchStats.StartTimestamp.Value);
                        writer.WriteEndObject(); // ship_usage
                    }

                    // It's possible to be a draw. Neither a winner or loser.
                    bool isWinner = string.Equals(playerStats.PlayerName, winnerPlayerName, StringComparison.OrdinalIgnoreCase);
                    bool isLoser = string.Equals(otherPlayerStats.PlayerName, winnerPlayerName, StringComparison.OrdinalIgnoreCase);

                    writer.WriteBoolean("is_winner"u8, isWinner);
                    writer.WriteNumber("score"u8, isWinner ? 1 : 0);
                    writer.WriteNumber("kills"u8, isWinner ? 1 : 0);
                    writer.WriteNumber("deaths"u8, isLoser ? 1 : 0);

                    if (playerStats.EndEnergy is not null)
                        writer.WriteNumber("end_energy"u8, playerStats.EndEnergy.Value);

                    writer.WriteNumber("gun_damage_dealt"u8, playerStats.GunDamageDealt);
                    writer.WriteNumber("bomb_damage_dealt"u8, playerStats.BombDamageDealt);
                    writer.WriteNumber("gun_damage_taken"u8, otherPlayerStats.GunDamageDealt);
                    writer.WriteNumber("bomb_damage_taken"u8, otherPlayerStats.BombDamageDealt);
                    writer.WriteNumber("self_damage"u8, playerStats.DamageSelf);
                    writer.WriteNumber("gun_fire_count"u8, playerStats.GunFireCount);
                    writer.WriteNumber("bomb_fire_count"u8, playerStats.BombFireCount);
                    writer.WriteNumber("mine_fire_count"u8, playerStats.MineFireCount);
                    writer.WriteNumber("gun_hit_count"u8, playerStats.GunHitCount);
                    writer.WriteNumber("bomb_hit_count"u8, playerStats.BombHitCount);
                    writer.WriteNumber("mine_hit_count"u8, playerStats.MineHitCount);
                    writer.WriteEndObject();
                }
            }

            void ClearCurrentMatchFromPlayerData(Player player)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                    return;

                playerData.CurrentMatchStats = null;
                playerData.CurrentPlayerStats = null;
            }
        }

        private void Callback_PlayerDamage(Player player, ServerTick timestamp, ReadOnlySpan<DamageData> damageDataSpan)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (playerData.CurrentMatchStats == null)
                return;

            PlayerStats? playerStats = playerData.CurrentPlayerStats;
            if (playerStats == null)
                return;

            PlayerStats opponentStats;
            if (playerData.CurrentMatchStats.PlayerStats1 == playerStats)
            {
                opponentStats = playerData.CurrentMatchStats.PlayerStats2;
            }
            else if (playerData.CurrentMatchStats.PlayerStats2 == playerStats)
            {
                opponentStats = playerData.CurrentMatchStats.PlayerStats1;
            }
            else
            {
                return;
            }

            for (int i = 0; i < damageDataSpan.Length; i++)
            {
                ref readonly DamageData damageData = ref damageDataSpan[i];

                uint damage = (uint)Math.Clamp(damageData.Damage, (short)0, damageData.Energy);

                if (damageData.AttackerPlayerId == opponentStats.Player?.Id)
                {
                    // damaged by the opponent
                    if (damageData.WeaponData.Type == WeaponCodes.Bullet || damageData.WeaponData.Type == WeaponCodes.BounceBullet)
                    {
                        // bullet damage
                        opponentStats.GunDamageDealt += damage;
                        opponentStats.GunHitCount++;
                    }
                    else if (damageData.WeaponData.Type == WeaponCodes.Bomb
                        || damageData.WeaponData.Type == WeaponCodes.ProxBomb
                        || damageData.WeaponData.Type == WeaponCodes.Thor)
                    {
                        // bomb damage
                        opponentStats.BombDamageDealt += damage;

                        if (damageData.WeaponData.Alternate)
                            opponentStats.MineHitCount++;
                        else
                            opponentStats.BombHitCount++;
                    }
                    else if (damageData.WeaponData.Type == WeaponCodes.Shrapnel)
                    {
                        // consider it bomb damage
                        opponentStats.BombDamageDealt += damage;
                    }
                }
                else if (damageData.AttackerPlayerId == player.Id)
                {
                    // self damage
                    playerStats.DamageSelf += damage;
                }
            }
        }

        private void Callback_PlayerPositionPacket(Player player, ref readonly C2S_PositionPacket positionPacket, ref readonly ExtraPositionData extra, bool hasExtraPositionData)
        {
            if (player == null)
                return;

            if (positionPacket.Weapon.Type == WeaponCodes.Null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            PlayerStats? playerStats = playerData.CurrentPlayerStats;
            if (playerStats == null)
                return;

            switch (positionPacket.Weapon.Type)
            {
                case WeaponCodes.Bullet:
                case WeaponCodes.BounceBullet:
                    playerStats.GunFireCount++;
                    break;

                case WeaponCodes.Bomb:
                case WeaponCodes.ProxBomb:
                case WeaponCodes.Thor:
                    if (positionPacket.Weapon.Alternate)
                        playerStats.MineFireCount++;
                    else
                        playerStats.BombFireCount++;
                    break;

                default:
                    break;
            }
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Prize green)
        {
            if (killer == null || !killer.TryGetExtraData(_pdKey, out PlayerData? killerPlayerData))
                return;

            if (killerPlayerData.CurrentPlayerStats is null)
                return;

            killerPlayerData.CurrentPlayerStats.EndEnergy = killer.Position.Energy;
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (playerData.CurrentPlayerStats is null)
                return;

            playerData.CurrentPlayerStats.ShipType = newShip;
        }

        #endregion

        private void RemoveDamageWatching(Player player, PlayerData playerData)
        {
            if (player == null || playerData == null)
                return;

            if (playerData.IsWatchingDamage)
            {
                _watchDamage.RemoveCallbackWatch(player);
                playerData.IsWatchingDamage = false;
            }
        }

        #region Helper types

        private class MatchStats : IResettable
        {
            public readonly PlayerStats PlayerStats1 = new();
            public readonly PlayerStats PlayerStats2 = new();

            public DateTime? StartTimestamp;
            public DateTime? EndTimestamp;

            public long? GameId;

            public void SetStart(Player player1, Player player2)
            {
                ArgumentNullException.ThrowIfNull(player1);
                ArgumentNullException.ThrowIfNull(player2);

                PlayerStats1.Reset();
                PlayerStats1.PlayerName = player1.Name;
                PlayerStats1.Player = player1;
                PlayerStats1.Squad = player1.Squad;
                PlayerStats1.XRes = player1.Xres;
                PlayerStats1.YRes = player1.Yres;
                PlayerStats1.ShipType = player1.Ship;

                PlayerStats2.Reset();
                PlayerStats2.PlayerName = player2.Name;
                PlayerStats2.Player = player2;
                PlayerStats2.Squad = player2.Squad;
                PlayerStats2.XRes = player2.Xres;
                PlayerStats2.YRes = player2.Yres;
                PlayerStats2.ShipType = player2.Ship;

                StartTimestamp = DateTime.UtcNow;
                EndTimestamp = null;
            }

            public void SetEnd()
            {
                EndTimestamp = DateTime.UtcNow;
            }

            bool IResettable.TryReset()
            {
                PlayerStats1.Reset();
                PlayerStats2.Reset();

                StartTimestamp = null;
                EndTimestamp = null;

                GameId = null;

                return true;
            }
        }

        private class PlayerStats
        {
            /// <summary>
            /// The name of the player.
            /// </summary>
            public string? PlayerName;

            /// <summary>
            /// The player. 
            /// </summary>
            /// <remarks>
            /// Note: This will be null if the player disconnected before the end of a match.
            /// So, <see cref="PlayerName"/> is the main player identifier.
            /// </remarks>
            public Player? Player;

            /// <summary>
            /// The player's squad.
            /// </summary>
            public string? Squad;

            /// <summary>
            /// The player's x resolution.
            /// </summary>
            public short? XRes;

            /// <summary>
            /// The player's y resoluation.
            /// </summary>
            public short? YRes;

            /// <summary>
            /// The ship the player is using.
            /// </summary>
            public ShipType? ShipType;

            /// <summary>
            /// Amount of damage dealt to enemies with bullets.
            /// </summary>
            public uint GunDamageDealt;

            /// <summary>
            /// Amount of damage dealt to enemies with bombs, mines, or thors.
            /// </summary>
            public uint BombDamageDealt;

            /// <summary>
            /// Amount of self damage.
            /// </summary>
            public uint DamageSelf;

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
            /// The # of hits made on enemies with guns.
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

            /// <summary>
            /// The player's energy at the time of the opponent's death.
            /// </summary>
            public short? EndEnergy;

            public void Reset()
            {
                PlayerName = null;
                Player = null;
                Squad = null;
                XRes = null;
                YRes = null;
                ShipType = null;
                GunDamageDealt = 0;
                BombDamageDealt = 0;
                DamageSelf = 0;
                GunFireCount = 0;
                BombFireCount = 0;
                MineFireCount = 0;
                GunHitCount = 0;
                BombHitCount = 0;
                MineHitCount = 0;
                EndEnergy = null;
            }
        }

        private class PlayerData : IResettable
        {
            /// <summary>
            /// The stats for the player's current match.
            /// </summary>
            public MatchStats? CurrentMatchStats;

            /// <summary>
            /// The player's stats for the current match.
            /// </summary>
            public PlayerStats? CurrentPlayerStats;

            /// <summary>
            /// The # of games won in a row.
            /// Gets set to 0 on a loss.
            /// </summary>
            public int WinStreak; // TODO: persist this?

            /// <summary>
            /// Whether damage is being watched for the player via IWatchDamage.
            /// </summary>
            public bool IsWatchingDamage = false;

            public bool TryReset()
            {
                CurrentMatchStats = null;
                CurrentPlayerStats = null;
                WinStreak = 0;
                IsWatchingDamage = false;
                return true;
            }
        }

        private readonly record struct MatchIdentifier(Arena Arena, int BoxId); // immutable, value equality

        #endregion
    }
}
