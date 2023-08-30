using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;
using SS.Utilities;
using System.Diagnostics;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that tracks stats for team versus matches.
    /// </summary>
    [ModuleInfo($"""
        Tracks stats for team versus matches.
        For use with the {nameof(TeamVersusMatch)} module.
        """)]
    public class TeamVersusStats : IModule, IArenaAttachableModule
    {
        private IArenaManager _arenaManager;
        private IChat _chat;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IWatchDamage _watchDamage;

        private PlayerDataKey<PlayerData> _pdKey;
        private readonly Dictionary<MatchIdentifier, MatchStats> _matchStatsDictionary = new();
        private readonly Dictionary<string, MemberStats> _playerMemberDictionary = new(StringComparer.OrdinalIgnoreCase);
        //private readonly ObjectPool<MatchStats> _matchStatsObjectPool = new NonTransientObjectPool<MatchStats>(new MatchStatsPooledObjectPolicy());

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IWatchDamage watchDamage)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _watchDamage = watchDamage ?? throw new ArgumentNullException(nameof(watchDamage));

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            TeamVersusMatchEndedCallback.Register(broker, Callback_TeamVersusMatchEnded); // This is on the global level since a match can end after an arena has been destroyed.
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            TeamVersusMatchEndedCallback.Unregister(broker, Callback_TeamVersusMatchEnded);
            _playerData.FreePlayerData(ref _pdKey);
            return true;
        }

        public bool AttachModule(Arena arena)
        {
            TeamVersusMatchStartedCallback.Register(arena, Callback_TeamVersusMatchStarted);
            TeamVersusMatchPlayerSubbedCallback.Register(arena, Callback_TeamVersusMatchPlayerSubbed);
            PlayerDamageCallback.Register(arena, Callback_PlayerDamage);
            PlayerPositionPacketCallback.Register(arena, Callback_PlayerPositionPacket);
            KillCallback.Register(arena, Callback_Kill);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            return true;
        }

        public bool DetachModule(Arena arena)
        {
            TeamVersusMatchStartedCallback.Unregister(arena, Callback_TeamVersusMatchStarted);
            TeamVersusMatchPlayerSubbedCallback.Unregister(arena, Callback_TeamVersusMatchPlayerSubbed);
            PlayerDamageCallback.Unregister(arena, Callback_PlayerDamage);
            PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPositionPacket);
            KillCallback.Unregister(arena, Callback_Kill);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            return true;
        }

        #endregion

        #region Callbacks

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

        private void Callback_TeamVersusMatchEnded(IMatchData matchData, MatchEndReason reason, ITeam winnerTeam)
        {
            if (!_matchStatsDictionary.Remove(matchData.MatchIdentifier, out MatchStats matchStats))
                return;

            DateTime now = DateTime.UtcNow;

            Span<short> freqs = stackalloc short[matchStats.Teams.Count];
            int index = 0;
            foreach (short freq in matchStats.Teams.Keys)
            {
                freqs[index++] = freq;
            }
            freqs.Sort();

            HashSet<Player> notifySet = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                GetPlayersToNotify(matchStats, notifySet);

                Span<char> playerName = stackalloc char[Constants.MaxPlayerNameLength];

                foreach (short freq in freqs)
                {
                    if (!matchStats.Teams.TryGetValue(freq, out TeamStats teamStats))
                        continue;

                    SendHorizonalRule(notifySet);
                    _chat.SendSetMessage(notifySet, $"| Freq {freq,-4}            Ki/De TK SK AS FR WR WRk Mi LO PTime | DDealt/DTaken DmgE  DmgKi | AcB AcG |");
                    SendHorizonalRule(notifySet);

                    foreach (SlotStats slotStats in teamStats.Slots)
                    {
                        for (int memberIndex = 0; memberIndex < slotStats.Members.Count; memberIndex++)
                        {
                            MemberStats memberStats = slotStats.Members[memberIndex];

                            if (memberStats.StartTime is not null)
                            {
                                memberStats.PlayTime += now - memberStats.StartTime.Value;
                                memberStats.StartTime = null;
                            }

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
                            uint damageDealt = memberStats.DamageDealtBombs + memberStats.DamageDealtBullets;
                            uint damageTaken = memberStats.DamageTakenBombs + memberStats.DamageTakenBullets + memberStats.DamageTakenTeam + memberStats.DamageSelf;
                            uint totalDamage = damageDealt + damageTaken;
                            float? damageEfficiency = totalDamage > 0 ? (float)damageDealt / totalDamage : null;
                            uint bombMineFireCount = memberStats.BombFireCount + memberStats.MineFireCount;
                            uint bombMineHitCount = memberStats.BombHitCount + memberStats.MineHitCount;
                            float? bombAccuracy = bombMineFireCount > 0 ? (float)bombMineHitCount / bombMineFireCount * 100 : null;
                            float? gunAccuracy = memberStats.BulletFireCount > 0 ? (float)memberStats.BulletHitCount / memberStats.BulletFireCount * 100 : null;

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
                                $"{(int)memberStats.PlayTime.TotalMinutes,3}:{memberStats.PlayTime:ss}"+
                                $" | {damageDealt,6}/{damageTaken,6} {damageEfficiency,4:0%} {memberStats.KillDamage,6}" +
                                $" | {bombAccuracy,3:N0} {gunAccuracy,3:N0} |");
                        }
                    }
                }

                SendHorizonalRule(notifySet);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(notifySet);
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

            ResetMatchStats(matchStats);
            // TODO: return matchStats to a pool

            void SendHorizonalRule(HashSet<Player> notifySet)
            {
                _chat.SendSetMessage(notifySet, $"+-----------------------------------------------------------+---------------------------+---------+");
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

            MemberStats memberStats = new(); // TODO: get from a pool
            memberStats.Initialize(slotStats, playerSlot.PlayerName);
            slotStats.Members.Add(memberStats);
            slotStats.Current = memberStats;

            _playerMemberDictionary[memberStats.PlayerName] = memberStats;
            Player subInPlayer = playerSlot.Player ?? _playerData.FindPlayer(playerSlot.PlayerName);
            if (subInPlayer is not null)
            {
                SetStartedPlaying(subInPlayer, memberStats);
            }
        }

        private void Callback_PlayerDamage(Player player, ServerTick timestamp, ReadOnlySpan<DamageData> damageDataSpan)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            MemberStats playerStats = playerData.MemberStats;
            if (playerStats is null || !playerStats.IsCurrent)
                return;

            if (player.Arena != playerStats.MatchStats.MatchData.Arena)
                return;

            for (int i = 0; i < damageDataSpan.Length; i++)
            {
                ref readonly DamageData damageData = ref damageDataSpan[i];

                uint damage = (uint)Math.Clamp(damageData.Damage, (short)0, damageData.Energy);

                if (player.Id == damageData.AttackerPlayerId)
                {
                    playerStats.DamageSelf += damage;
                }
                else
                {
                    Player attackerPlayer = _playerData.PidToPlayer(damageData.AttackerPlayerId);
                    MemberStats attackerStats = null;
                    if (attackerPlayer is not null
                        && attackerPlayer.TryGetExtraData(_pdKey, out PlayerData attackerPlayerData))
                    {
                        attackerStats = attackerPlayerData.MemberStats;
                    }

                    if (damageData.WeaponData.Type == WeaponCodes.Bullet || damageData.WeaponData.Type == WeaponCodes.BounceBullet)
                    {
                        // bullet damage
                        playerStats.DamageTakenBullets += damage;

                        if (attackerStats is not null)
                        {
                            attackerStats.DamageDealtBullets += damage;
                            attackerStats.BulletHitCount++;
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

            if (player.Arena != memberStats.MatchStats.MatchData.Arena)
                return;

            switch (positionPacket.Weapon.Type)
            {
                case WeaponCodes.Bullet:
                case WeaponCodes.BounceBullet:
                    memberStats.BulletFireCount++;
                    break;

                case WeaponCodes.Bomb:
                case WeaponCodes.ProxBomb:
                case WeaponCodes.Thor:
                    if (positionPacket.Weapon.Alternate)
                        memberStats.MineFireCount++;
                    else
                        memberStats.BombFireCount++;
                    break;

                case WeaponCodes.Burst:
                    //memberStats.BulletFireCount += All:BurstShrapnel // TODO: maybe? and if so, also remember to add BulletHitCount logic
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

            MemberStats killedStats = killedData.MemberStats;
            MemberStats killerStats = killerData.MemberStats;

            // Check that the kill was made in the right arena.
            if (killed.Arena != killedStats.MatchStats.MatchData.Arena)
                return;

            if (killerStats is not null)
            {
                if (killer.Freq == killed.Freq)
                {
                    killerStats.TeamKills++;
                }
                else
                {
                    killerStats.Kills++;
                }
            }

            if (killedStats is not null)
            {
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
            }

            // TODO: kill damage, team kill damage, solo kills
            //killedData.RecentDamageTaken
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

            if (player.Arena != memberStats.MatchStats.MatchData.Arena)
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

        private void ResetMatchStats(MatchStats matchStats)
        {
            // Clear the existing object.
            foreach (TeamStats teamStats in matchStats.Teams.Values)
            {
                foreach (SlotStats slotStats in teamStats.Slots)
                {
                    foreach (MemberStats memberStats in slotStats.Members)
                    {
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
            public readonly Dictionary<short, TeamStats> Teams = new();

            public DateTime StartTimestamp;
            public DateTime? EndTimestamp;

            public void Initialize(IMatchData matchData)
            {
                MatchData = matchData ?? throw new ArgumentNullException(nameof(matchData));
            }

            public void Reset()
            {
                MatchData = null;
                Teams.Clear();
                StartTimestamp = DateTime.MinValue;
                EndTimestamp = null;
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


            #region Damage

            /// <summary>
            /// Amount of damage taken from enemy bullets, including bursts.
            /// </summary>
            public uint DamageTakenBullets;

            /// <summary>
            /// Amount of damage taken from enemy bombs, mines, shrapnel, or thors.
            /// </summary>
            /// <remarks>
            /// This does not include damage from teammates (see <see cref="DamageTakenTeam"/>) or self damage (see <see cref="DamageSelf"/>).
            /// </remarks>
            public uint DamageTakenBombs;

            /// <summary>
            /// Amount of damage taken from teammates.
            /// </summary>
            public uint DamageTakenTeam;

            /// <summary>
            /// Amount of damage dealt to enemies with bullets, including bursts.
            /// </summary>
            public uint DamageDealtBullets;

            /// <summary>
            /// Amount of damage dealt to enemies with bombs, mines, shrapnel, or thors.
            /// </summary>
            /// <remarks>
            /// This does not include damage to teammates (see <see cref="DamageDealtTeam"/>) or self damage (see <see cref="DamageSelf"/>).
            /// </remarks>
            public uint DamageDealtBombs;

            /// <summary>
            /// Amount of damage dealt to teammates.
            /// </summary>
            public uint DamageDealtTeam;

            /// <summary>
            /// Amount of self damage.
            /// </summary>
            public uint DamageSelf;

            /// <summary>
            /// Amount of damage attributed to an enemy being killed.
            /// Damage dealt to an enemy decays based on their recharge.
            /// This may give a better picture than kills and assists combined?
            /// </summary>
            public uint KillDamage;

            /// <summary>
            /// Amount of damage done to teammates that were killed.
            /// </summary>
            public uint TeamKillDamage;

            #endregion

            #region Accuracy

            /// <summary>
            /// The # of guns fired.
            /// </summary>
            public uint BulletFireCount;

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
            public uint BulletHitCount;

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
            public short Assists; // what criteria? last person other than the killer that did >= X amount of recent damage to the killed player?
            //public float Assists; // maybe base it on recent damage taken by the killed player and divide among teamates?
            public short ForcedReps; // what criteria? last person to do recent damage?
            //public float ForcedReps; // maybe base it on recent damage taken by the player that repped and divide among teamates?

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
            }

            public void Reset()
            {
                SlotStats = null;
                PlayerName = null;

                // damage fields
                DamageTakenBullets = 0;
                DamageTakenBombs = 0;
                DamageDealtBullets = 0;
                DamageDealtBombs = 0;
                DamageDealtTeam = 0;
                DamageSelf = 0;
                KillDamage = 0;
                TeamKillDamage = 0;

                // accuracy fields
                BulletFireCount = 0;
                BombFireCount = 0;
                MineFireCount = 0;
                BulletHitCount = 0;
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

        private readonly record struct TeamSlot(short Freq, short SlotId);

        private class PlayerData
        {
            /// <summary>
            /// The player's stats. <see langword="null"/> if not in a match with stats being tracked.
            /// </summary>
            public MemberStats MemberStats;

            /// <summary>
            /// Whether damage is being watched for the player via IWatchDamage.
            /// </summary>
            public bool IsWatchingDamage = false;

            /// <summary>
            /// Recent damage taken by the player in order from oldest to newest.
            /// This can be used upon death to determine how much "Kill Damage" to credit each attacker.
            /// </summary>
            public readonly LinkedList<DamageInfo> RecentDamageTaken = new();
        }

        private struct PlayerTeamSlot
        {
            string PlayerName;
            short Freq;
            short SlotId;
        }

        private struct DamageInfo
        {
            ServerTick Timestamp;
            short Damage;

            /// <summary>
            /// Identifies who caused the damage.
            /// Keep in mind that this could be friendly fire.
            /// </summary>
            PlayerTeamSlot Attacker;
        }

        #endregion
    }
}

