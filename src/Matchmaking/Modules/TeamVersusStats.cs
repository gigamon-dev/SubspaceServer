using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;

namespace SS.Matchmaking.Modules
{
    public class TeamVersusStats : IModule, IArenaAttachableModule
    {
        private IChat _chat;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IWatchDamage _watchDamage;

        private PlayerDataKey<PlayerData> _pdKey;
        private readonly Dictionary<MatchIdentifier, MatchStats> _matchStats = new();
        //private readonly ObjectPool<MatchStats> _matchStatsObjectPool = new NonTransientObjectPool<MatchStats>(new MatchStatsPooledObjectPolicy());

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IChat chat,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IWatchDamage watchDamage)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _watchDamage = watchDamage ?? throw new ArgumentNullException(nameof(watchDamage));

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            _playerData.FreePlayerData(ref _pdKey);
            return true;
        }

        public bool AttachModule(Arena arena)
        {
            PlayerDamageCallback.Register(arena, Callback_PlayerDamage);
            PlayerPositionPacketCallback.Register(arena, Callback_PlayerPositionPacket);
            KillCallback.Register(arena, Callback_Kill);
            return true;
        }

        public bool DetachModule(Arena arena)
        {
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

        private void Callback_PlayerDamage(Player player, ServerTick timestamp, ReadOnlySpan<DamageData> damageDataSpan)
        {
            
        }

        private void Callback_PlayerPositionPacket(Player player, in C2S_PositionPacket positionPacket)
        {

        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short pts, Prize green)
        {
            if (!killer.TryGetExtraData(_pdKey, out PlayerData killerData))
                return;

            if (!killed.TryGetExtraData(_pdKey, out PlayerData killedData))
                return;

            TeamMemberStats killerStats = killerData.CurrentTeamMemberStats;
            if (killerStats != null)
            {
                killerStats.Kills++;
            }

            TeamMemberStats killedStats = killedData.CurrentTeamMemberStats;
            if (killedStats != null)
            {
                killedStats.Deaths++;
            }

            // TODO: kill damage, team kill damage, solo kills
            //killedData.RecentDamageTaken
        }

        #endregion

        #region Helper types

        private readonly record struct MatchIdentifier(Arena Arena, int BoxId);

        private class MatchStats
        {
            /// <summary>
            /// Key = freq
            /// </summary>
            public readonly Dictionary<short, TeamStats> TeamStats = new();

            public DateTime? StartTimestamp;
            public DateTime? EndTimestamp;
        }

        private class TeamStats
        {
            /// <summary>
            /// Player slots
            /// e.g. in a 4v4 match, there would be 4 slots. 
            /// </summary>
            public readonly List<SlotInfo> Slots = new();
        }

        private struct SlotInfo
        {
            /// <summary>
            /// Stats for each player that occupied the slot.
            /// </summary>
            public List<TeamMemberStats> TeamMemberStats;

            /// <summary>
            /// Stats of the player that currently holds the slot.
            /// </summary>
            public TeamMemberStats Current;
        }

        private class TeamMemberStats
        {
            public string PlayerName;

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
            public MatchStats CurrentMatchStats;

            /// <summary>
            /// The freq and slot index that the player currently holds.
            /// </summary>
            public TeamSlot? CurrentTeamSlot;

            public TeamMemberStats CurrentTeamMemberStats
            {
                get
                {
                    if (CurrentMatchStats == null || CurrentTeamSlot == null)
                        return null;

                    if (!CurrentMatchStats.TeamStats.TryGetValue(CurrentTeamSlot.Value.Freq, out TeamStats teamStats))
                        return null;

                    return teamStats.Slots[CurrentTeamSlot.Value.SlotId].Current;
                }
            }

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

