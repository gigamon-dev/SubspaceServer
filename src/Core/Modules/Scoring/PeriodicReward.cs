using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities.ObjectPool;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PeriodicSettings = SS.Core.ConfigHelp.Constants.Arena.Periodic;

namespace SS.Core.Modules.Scoring
{
    /// <summary>
    /// Module for rewarding players periodically for flag games.
    /// </summary>
    [CoreModuleInfo]
    public sealed class PeriodicReward(
        IAllPlayerStats allPlayerStats,
        IArenaManager arenaManager,
        IChat chat,
        ICommandManager commandManager,
        IConfigManager configManager,
        IMainloopTimer mainloopTimer,
        INetwork network,
        IPlayerData playerData) : IModule, IPeriodicReward, IPeriodicRewardPoints
    {
        private readonly IAllPlayerStats _allPlayerStats = allPlayerStats ?? throw new ArgumentNullException(nameof(allPlayerStats));
        private readonly IArenaManager _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly IMainloopTimer _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        private ArenaDataKey<ArenaData> _adKey;

        private readonly DefaultObjectPool<TeamData> _teamDataPool = new(new DefaultPooledObjectPolicy<TeamData>(), Constants.TargetPlayerCount);
        private readonly DefaultObjectPool<Dictionary<short, TeamData>> _freqTeamDataDictionaryPool = new(new DictionaryPooledObjectPolicy<short, TeamData>() { InitialCapacity = Constants.TargetPlayerCount });
        private readonly DefaultObjectPool<Dictionary<short, short>> _freqPointsDictionaryPool = new(new DictionaryPooledObjectPolicy<short, short>() { InitialCapacity = Constants.TargetPlayerCount });

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _commandManager.AddCommand("periodicreward", Command_periodicreward);
            _commandManager.AddCommand("periodicreset", Command_periodicreset);
            _commandManager.AddCommand("periodicstop", Command_periodicstop);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            _commandManager.RemoveCommand("periodicreward", Command_periodicreward);
            _commandManager.RemoveCommand("periodicreset", Command_periodicreset);
            _commandManager.RemoveCommand("periodicstop", Command_periodicstop);

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        #endregion

        #region IPeriodicReward

        void IPeriodicReward.Reward(Arena arena)
        {
            Reward(arena);
        }

        void IPeriodicReward.Reset(Arena arena)
        {
            StartTimer(arena);
        }

        void IPeriodicReward.Stop(Arena arena)
        {
            StopTimer(arena);
        }

        #endregion

        #region IPeriodicRewardPoints

        void IPeriodicRewardPoints.GetRewardPoints(
            Arena arena,
            IPeriodicRewardPoints.ISettings settings,
            Dictionary<short, short> freqPoints)
        {
            if (arena is null || settings is null || freqPoints is null)
                return;

            Dictionary<short, TeamData> teams = _freqTeamDataDictionaryPool.Get();

            try
            {
                int totalPlayerCount = 0;

                IFlagGame? flagGame = arena.GetInterface<IFlagGame>();

                // Get the total player count, player count for each team, and flag count for each team.
                try
                {
                    _playerData.Lock();

                    try
                    {
                        foreach (Player player in _playerData.Players)
                        {
                            if (player.Arena != arena)
                                continue;

                            if (!settings.IncludeSpectators && player.Ship == ShipType.Spec)
                                continue;

                            if (!settings.IncludeSafeZones && ((player.Position.Status & PlayerPositionStatus.Safezone) != 0))
                                continue;

                            short freq = player.Freq;

                            if (!teams.TryGetValue(freq, out TeamData? teamData))
                            {
                                teamData = _teamDataPool.Get();
                                teamData.FlagCount = flagGame is not null ? flagGame.GetFlagCount(arena, freq) : 0;

                                teams.Add(freq, teamData);
                            }

                            teamData.PlayerCount++;
                            totalPlayerCount++;
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }
                }
                finally
                {
                    if (flagGame is not null)
                        arena.ReleaseInterface(ref flagGame);
                }

                if (totalPlayerCount < settings.RewardMinimumPlayers)
                    return; // not enough players for rewards

                // Calculate how much to award each team.
                foreach ((short freq, TeamData freqData) in teams)
                {
                    short points = (settings.RewardPoints > 0)
                        ? (short)(freqData.FlagCount * settings.RewardPoints)
                        : (short)(freqData.FlagCount * (-settings.RewardPoints) * totalPlayerCount);

                    if (settings.SplitPoints && freqData.PlayerCount > 0)
                        points = (short)(points / freqData.PlayerCount);

                    if (points > 0 || settings.SendZeroRewards)
                    {
                        freqPoints[freq] = points;
                    }
                }
            }
            finally
            {
                foreach (TeamData teamData in teams.Values)
                {
                    _teamDataPool.Return(teamData);
                }

                _freqTeamDataDictionaryPool.Return(teams);
            }
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ad.Settings = new(_configManager, arena.Cfg!);
                StartTimer(arena);
            }
            else if (action == ArenaAction.Destroy)
            {
                StopTimer(arena);
            }
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Rewards teams in the current arena as if the periodic timer elapsed.")]
        private void Command_periodicreward(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            ((IPeriodicReward)this).Reward(arena);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Resets the periodic timer in the current arena.")]
        private void Command_periodicreset(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            ((IPeriodicReward)this).Reset(arena);
            _chat.SendMessage(player, "Periodic reward timer reset.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Stops the periodic timer in the current arena.")]
        private void Command_periodicstop(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            ((IPeriodicReward)this).Stop(arena);
            _chat.SendMessage(player, "Periodic reward timer stopped.");
        }

        #endregion

        private bool MainloopTimer_Reward(Arena arena)
        {
            if (arena is null)
                return false;

            Reward(arena);
            return true;
        }

        private void StartTimer(Arena arena)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.Settings is null)
                return;

            StopTimer(arena);

            if (ad.Settings.RewardDelay > TimeSpan.Zero)
            {
                _mainloopTimer.SetTimer(MainloopTimer_Reward, (int)ad.Settings.RewardDelay.TotalMilliseconds, (int)ad.Settings.RewardDelay.TotalMilliseconds, arena, arena);
                ad.TimerRunning = true;
            }
        }

        private void StopTimer(Arena arena)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.TimerRunning)
            {
                _mainloopTimer.ClearTimer<Arena>(MainloopTimer_Reward, arena);
                ad.TimerRunning = false;
            }
        }

        private void Reward(Arena arena)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.Settings is null)
                return;

            Dictionary<short, short> freqPoints = _freqPointsDictionaryPool.Get();

            try
            {
                //
                // Find out how many points to award to each team.
                //

                IPeriodicRewardPoints? periodicRewardPoints = arena.GetInterface<IPeriodicRewardPoints>() ?? this;

                try
                {
                    periodicRewardPoints.GetRewardPoints(arena, ad.Settings, freqPoints);
                }
                finally
                {
                    if (periodicRewardPoints != this)
                        arena.ReleaseInterface(ref periodicRewardPoints);
                }

                if (freqPoints.Count > 0)
                {
                    //
                    // Send the reward packet.
                    //

                    const int MaxPacketLength = 513; // The maximum accepted by Continuum
                    int maxItems = (MaxPacketLength - 1) / PeriodicRewardItem.Length;

                    Span<byte> packet = stackalloc byte[1 + int.Min(freqPoints.Count, maxItems) * PeriodicRewardItem.Length];
                    packet[0] = (byte)S2CPacketType.PeriodicReward;

                    Span<PeriodicRewardItem> rewards = MemoryMarshal.Cast<byte, PeriodicRewardItem>(packet[1..]);
                    int index = 0;
                    foreach ((short freq, short points) in freqPoints)
                    {
                        rewards[index++] = new(freq, points);

                        if (index >= rewards.Length)
                        {
                            // We have the maximum #  of items that can be sent in a packet. Send it.
                            _network.SendToArena(arena, null, packet, NetSendFlags.Reliable);
                            index = 0;
                        }
                    }

                    if (index > 0)
                    {
                        _network.SendToArena(arena, null, packet[..(1 + (index * PeriodicRewardItem.Length))], NetSendFlags.Reliable);
                    }

                    // The client does not reward players that are in spectator mode.
                    // The client does not reward players that it thinks are in a safe zone (based on the last known position of that player).
                    // The server sends safe zone enter/leave position packets reliably in an attempt to keep clients in sync. Though, likely it is not perfect.
                    // For example, when reward is being processed, a player might have moved into or out of a safe zone, but the position packet has not made it
                    // to the server yet. That player will receive the 0x23 (Periodic Reward) packet and their score will get out of sync with the server and all other players.

                    //
                    // Record player stats.
                    //

                    _playerData.Lock();

                    try
                    {
                        foreach (Player player in _playerData.Players)
                        {
                            if (player.Arena != arena)
                                continue;

                            // The 0x23 (PeriodicReward) packet was sent to the arena.
                            // The game clients (Continuum and VIE) know to only increment points for players that are playing (in a ship and not in a safe zone).
                            // We must do the same, or it will get out of sync with the game clients.
                            if (player.Ship == ShipType.Spec || (player.Position.Status & PlayerPositionStatus.Safezone) != 0)
                                continue;

                            if (freqPoints.TryGetValue(player.Freq, out short points))
                            {
                                _allPlayerStats.IncrementStat(player, StatCodes.FlagPoints, null, points);
                            }
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }
                }
            }
            finally
            {
                _freqPointsDictionaryPool.Return(freqPoints);
            }
        }

        #region Helper types

        private class ArenaData : IResettable
        {
            public Settings? Settings;
            public bool TimerRunning;

            public bool TryReset()
            {
                Settings = default;
                TimerRunning = false;
                return true;
            }
        }

        private class Settings : IPeriodicRewardPoints.ISettings
        {
            public TimeSpan RewardDelay { get; }

            public int RewardMinimumPlayers { get; }

            public int RewardPoints { get; }

            public bool SplitPoints { get; }

            public bool SendZeroRewards { get; }

            public bool IncludeSpectators { get; }

            public bool IncludeSafeZones { get; }

            [ConfigHelp<int>("Periodic", "RewardDelay", ConfigScope.Arena, Default = 0,
                Description = "The interval between periodic rewards (in ticks). Zero to disable.")]
            [ConfigHelp<int>("Periodic", "RewardMinimumPlayers", ConfigScope.Arena, Default = 0,
                Description = "The minimum players necessary in the arena to give out periodic rewards.")]
            [ConfigHelp<int>("Periodic", "RewardPoints", ConfigScope.Arena, Default = 0,
                Description = """
                    Periodic rewards are calculated as follows: If this setting is
                    positive, you get this many points per flag. If it's negative,
                    you get it's absolute value points per flag, times the number of
                    players in the arena.
                    """)]
            [ConfigHelp<bool>("Periodic", "SplitPoints", ConfigScope.Arena, Default = false,
                Description = "Whether points are divided among players on a team.")]
            [ConfigHelp<bool>("Periodic", "SendZeroRewards", ConfigScope.Arena, Default = true,
                Description = "Whether frequencies with zero points will still get a reward notification during the ding.")]
            [ConfigHelp<bool>("Periodic", "IncludeSpectators", ConfigScope.Arena, Default = false,
                Description = """
                    Whether players in spectator mode affect reward calculations.
                    This only affects whether the player is included in player counts.
                    Players in spectator mode are never awarded points.
                    """)]
            [ConfigHelp<bool>("Periodic", "IncludeSafeZones", ConfigScope.Arena, Default = false,
                Description = """
                    Whether players in safe zones affect reward calculations.
                    This only affects whether the player is included in player counts.
                    Players in a safe zone are never awarded points.
                    """)]
            public Settings(IConfigManager configManager, ConfigHandle ch)
            {
                ArgumentNullException.ThrowIfNull(configManager);
                ArgumentNullException.ThrowIfNull(ch);

                RewardDelay = TimeSpan.FromMilliseconds(configManager.GetInt(ch, "Periodic", "RewardDelay", PeriodicSettings.RewardDelay.Default) * 10);
                RewardMinimumPlayers = configManager.GetInt(ch, "Periodic", "RewardMinimumPlayers", PeriodicSettings.RewardMinimumPlayers.Default);
                RewardPoints = configManager.GetInt(ch, "Periodic", "RewardPoints", PeriodicSettings.RewardPoints.Default);
                SplitPoints = configManager.GetBool(ch, "Periodic", "SplitPoints", PeriodicSettings.SplitPoints.Default);
                SendZeroRewards = configManager.GetBool(ch, "Periodic", "SendZeroRewards", PeriodicSettings.SendZeroRewards.Default);
                IncludeSpectators = configManager.GetBool(ch, "Periodic", "IncludeSpectators", PeriodicSettings.IncludeSpectators.Default);
                IncludeSafeZones = configManager.GetBool(ch, "Periodic", "IncludeSafeZones", PeriodicSettings.IncludeSafeZones.Default);
            }
        }

        private class TeamData : IResettable
        {
            public int PlayerCount = 0;
            public int FlagCount = 0;

            public bool TryReset()
            {
                PlayerCount = 0;
                FlagCount = 0;
                return true;
            }
        }

        #endregion
    }
}
