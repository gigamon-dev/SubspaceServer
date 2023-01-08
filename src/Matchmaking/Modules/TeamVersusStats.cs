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
            return true;
        }

        public bool DetachModule(Arena arena)
        {
            TeamVersusMatchStartedCallback.Unregister(arena, Callback_TeamVersusMatchStarted);
            TeamVersusMatchPlayerSubbedCallback.Unregister(arena, Callback_TeamVersusMatchPlayerSubbed);
            PlayerDamageCallback.Unregister(arena, Callback_PlayerDamage);
            PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPositionPacket);
            KillCallback.Unregister(arena, Callback_Kill);
            return true;
        }

        #endregion

        #region Callbacks

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {

        }

        private void Callback_TeamVersusMatchEnded(IMatchData matchData, MatchEndReason reason, ITeam winnerTeam)
        {
            if (!_matchStatsDictionary.Remove(matchData.MatchIdentifier, out MatchStats matchStats))
                return;

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
                GetPlayersToNotify(matchData, notifySet);

                Span<char> playerName = stackalloc char[Constants.MaxPlayerNameLength];

                foreach (short freq in freqs)
                {
                    if (!matchStats.Teams.TryGetValue(freq, out TeamStats teamStats))
                        continue;

                    SendHorizonalRule(notifySet);
                    _chat.SendSetMessage(notifySet, $"| Freq {freq,-4}            Ki/De TK LO SK AS FR WR WRk Mi PTime | DDealt/DTaken DmgE  DmgKi | AcB AcG |");
                    SendHorizonalRule(notifySet);

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
                                    name = name[..19];

                                playerName.TryWrite($" {name,-19}", out _);
                            }

                            // TODO: calculations

                            _chat.SendSetMessage(notifySet, $"| {playerName}  0/ 0  0  0  0  0  0  0   0  0 00:00 |      0/     0    0      0 |   0   0 |");
                        }
                    }
                }

                SendHorizonalRule(notifySet);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(notifySet);
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
                matchStats.MatchData = matchData;
                _matchStatsDictionary.Add(matchData.MatchIdentifier, matchStats);
            }

            matchStats.StartTimestamp = matchData.Started.Value;

            for (int teamIdx = 0; teamIdx < matchData.Teams.Count; teamIdx++)
            {
                ITeam team = matchData.Teams[teamIdx];
                TeamStats teamStats = new(); // TODO: get from a pool
                teamStats.Team = team;

                for (int slotIdx = 0; slotIdx < team.Slots.Count; slotIdx++)
                {
                    IPlayerSlot slot = team.Slots[slotIdx];
                    SlotStats slotStats = new(); // TODO: get from a pool
                    slotStats.Slot = slot;
                    
                    MemberStats memberStats = new(); // TODO: get from a pool
                    memberStats.PlayerName = slot.PlayerName;
                    slotStats.Members.Add(memberStats);
                    slotStats.Current = memberStats;

                    _playerMemberDictionary[memberStats.PlayerName] = memberStats;
                    Player player = slot.Player;
                    if (player is null)
                    {
                        player = _playerData.FindPlayer(memberStats.PlayerName);
                    }

                    if (player is not not null
                        && player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    {
                        //playerData.
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

            MemberStats memberStats = new(); // TODO: get from a pool
            memberStats.PlayerName = playerSlot.PlayerName;
            slotStats.Members.Add(memberStats);
            slotStats.Current = memberStats;

            _playerMemberDictionary[memberStats.PlayerName] = memberStats;
        }

        private void Callback_PlayerDamage(Player player, ServerTick timestamp, ReadOnlySpan<DamageData> damageDataSpan)
        {
            
        }

        private void Callback_PlayerPositionPacket(Player player, in C2S_PositionPacket positionPacket, bool hasExtraPositionData)
        {

        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Prize green)
        {
            if (!killer.TryGetExtraData(_pdKey, out PlayerData killerData))
                return;

            if (!killed.TryGetExtraData(_pdKey, out PlayerData killedData))
                return;

            MemberStats killerStats = killerData.MemberStats;
            // TODO: check that the kill was made in the right arena
            if (killerStats is not null)
            {
                killerStats.Kills++;
            }

            MemberStats killedStats = killedData.MemberStats;
            if (killedStats is not null)
            {
                killedStats.Deaths++;
            }

            // TODO: kill damage, team kill damage, solo kills
            //killedData.RecentDamageTaken
        }

        #endregion

        private void GetPlayersToNotify(IMatchData matchData, HashSet<Player> players)
        {
            if (matchData is null || players is null)
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

            if (!_matchStatsDictionary.TryGetValue(matchData.MatchIdentifier, out MatchStats matchStats))
                return;

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
                        memberStats.PlayerName = null;

                        // damage fields
                        memberStats.DamageTakenBullets = 0;
                        memberStats.DamageTakenBombs = 0;
                        memberStats.DamageDealtBullets = 0;
                        memberStats.DamageDealtBombs = 0;
                        memberStats.DamageTeam = 0;
                        memberStats.DamageSelf = 0;
                        memberStats.KillDamage = 0;
                        memberStats.TeamKillDamage = 0;

                        // accuracy fields
                        memberStats.GunFireCount = 0;
                        memberStats.BombFireCount = 0;
                        memberStats.MineFireCount = 0;
                        memberStats.GunHitCount = 0;
                        memberStats.BombHitCount = 0;
                        memberStats.MineHitCount = 0;

                        // items
                        memberStats.WastedRepels = 0;
                        memberStats.WastedRockets = 0;
                        memberStats.WastedThors = 0;
                        memberStats.WastedBursts = 0;
                        memberStats.WastedDecoys = 0;
                        memberStats.WastedPortals = 0;
                        memberStats.WastedBricks = 0;

                        // other
                        memberStats.Kills = 0;
                        memberStats.SoloKills = 0;
                        memberStats.Deaths = 0;
                        memberStats.Assists = 0;
                        memberStats.ForcedReps = 0;
                        memberStats.PlayTime = TimeSpan.Zero;
                        memberStats.LagOuts = 0;
                        memberStats.StartTime = null;

                        // TODO: return memberStats to a pool
                    }
                    slotStats.Members.Clear();
                    slotStats.Current = null;

                    // TODO: return slotStats to a pool
                }
                teamStats.Slots.Clear();

                // TODO: return teamStats to a pool
            }
            matchStats.Teams.Clear();

            matchStats.StartTimestamp = DateTime.MinValue;
            matchStats.EndTimestamp = DateTime.MinValue;
        }

        #region Helper types

        private class MatchStats
        {
            public IMatchData MatchData;

            /// <summary>
            /// Key = freq
            /// </summary>
            public readonly Dictionary<short, TeamStats> Teams = new();

            public DateTime StartTimestamp;
            public DateTime? EndTimestamp;
        }

        private class TeamStats
        {
            //public MatchStats MatchStats;
            public ITeam Team;

            /// <summary>
            /// Player slots
            /// e.g. in a 4v4 match, there would be 4 slots. 
            /// </summary>
            public readonly List<SlotStats> Slots = new();
        }

        private class SlotStats
        {
            //public MatchStats MatchStats;
            //public TeamStats TeamStats;
            public IPlayerSlot Slot;

            /// <summary>
            /// Stats for each player that occupied the slot.
            /// </summary>
            public readonly List<MemberStats> Members = new();

            /// <summary>
            /// Stats of the player that currently holds the slot.
            /// </summary>
            public MemberStats Current;
        }

        private class MemberStats
        {
            //public MatchStats MatchStats;
            //public TeamStats TeamStats;
            //public SlotStats SlotStats;

            public string PlayerName;

            //public Player Player; // TODO: keep track of player so that we can send notifications to even those that are no longer the current slot holder?


            #region Damage

            /// <summary>
            /// Amount of damage taken from enemy bullets.
            /// </summary>
            public uint DamageTakenBullets;

            /// <summary>
            /// Amount of damage taken from enemy bombs, mines, or thors.
            /// </summary>
            public uint DamageTakenBombs;

            /// <summary>
            /// Amount of damage dealt to enemies with bullets.
            /// </summary>
            public uint DamageDealtBullets;

            /// <summary>
            /// Amount of damage dealt to enemies with bombs, mines, or thors.
            /// </summary>
            public uint DamageDealtBombs;

            /// <summary>
            /// Amount of damage dealt to teammates.
            /// </summary>
            public uint DamageTeam;

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
            public readonly LinkedList<DamageInfo> RecentDamageTaken;
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

