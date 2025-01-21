using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using System;
using FlagSettings = SS.Core.ConfigHelp.Constants.Arena.Flag;
using MiscSettings = SS.Core.ConfigHelp.Constants.Arena.Misc;

namespace SS.Core.Modules.Scoring
{
    /// <summary>
    /// Scoring module for carriable flag games.
    /// </summary>
    /// <remarks>
    /// For a warzone-style win, it watches for a flag drop and checks if all flags are dropped and owned by one team.
    /// For a running/jackpot style win, it watches for flag pickup and checks if 1 team is carrying all the flags.
    /// </remarks>
    [CoreModuleInfo]
    public sealed class FlagGamePoints : IModule, IArenaAttachableModule
    {
        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly IConfigManager _configManager;
        private readonly IPlayerData _playerData;

        private ArenaDataKey<ArenaData> _adKey;

        public FlagGamePoints(
            IArenaManager arenaManager,
            IChat chat,
            IConfigManager configManager,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        [ConfigHelp<FlagMode>("Flag", "FlagMode", ConfigScope.Arena, Default = FlagMode.None,
            Description = """
                Style of flag game.
                0 = carry all flags to win (e.g. running/jackpot),
                1 = own all dropped flags to win (e.g. warzone),
                -1 = None (no win condition)
                """)]
        [ConfigHelp<int>("Flag", "FlagReward", ConfigScope.Arena, Default = 5000,
            Description = "The basic flag reward is calculated as (players in arena)^2 * FlagReward / 1000.")]
        [ConfigHelp<bool>("Flag", "SplitPoints", ConfigScope.Arena, Default = false,
            Description = "Whether to split a flag reward between the members of a freq or give them each the full amount.")]
        [ConfigHelp<bool>("Misc", "VictoryMusic", ConfigScope.Arena, Default = true,
            Description = "Whether to play victory music or not.")]
        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            ConfigHandle ch = arena.Cfg!;
            ad.FlagMode = _configManager.GetEnum(ch, "Flag", "FlagMode", FlagMode.None);
            ad.FlagRewardRatio = _configManager.GetInt(ch, "Flag", "FlagReward", FlagSettings.FlagReward.Default) / 1000.0;
            ad.SplitPoints = _configManager.GetBool(ch, "Flag", "SplitPoints", FlagSettings.SplitPoints.Default);
            ad.IsVictoryMusicEnabled = _configManager.GetBool(ch, "Misc", "VictoryMusic", MiscSettings.VictoryMusic.Default);

            FlagGainCallback.Register(arena, Callback_FlagGain);
            FlagLostCallback.Register(arena, Callback_FlagLost);
            FlagOnMapCallback.Register(arena, Callback_FlagOnMap);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            FlagGainCallback.Unregister(arena, Callback_FlagGain);
            FlagLostCallback.Unregister(arena, Callback_FlagLost);
            FlagOnMapCallback.Unregister(arena, Callback_FlagOnMap);

            return true;
        }

        #endregion

        private void Callback_FlagGain(Arena arena, Player player, short flagId, FlagPickupReason reason)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ICarryFlagGame? carryFlagGame = arena.GetInterface<ICarryFlagGame>();
            if (carryFlagGame == null)
                return;

            try
            {
                if (ad.FlagMode == FlagMode.CarryAll)
                {
                    short flagCount = carryFlagGame.GetFlagCount(arena);
                    bool isWin = true;
                    for (short i = 0; i < flagCount; i++)
                    {
                        if (!carryFlagGame.TryGetFlagInfo(arena, i, out IFlagInfo? flagInfo)
                            || flagInfo.State != FlagState.Carried
                            || flagInfo.Freq != player.Freq)
                        {
                            isWin = false;
                            break;
                        }
                    }

                    if (isWin)
                    {
                        DoFlagWin(arena, ad, player.Freq);
                    }
                }
                else if (ad.FlagMode == FlagMode.OwnAllDropped)
                {
                    if (ad.IsVictoryMusicEnabled
                        && carryFlagGame.GetFlagCount(arena, player.Freq) == carryFlagGame.GetFlagCount(arena))
                    {
                        // start music
                        ad.IsMusicPlaying = true;
                        _chat.SendArenaMessage(arena, ChatSound.MusicLoop, "");
                    }
                }
            }
            finally
            {
                arena.ReleaseInterface(ref carryFlagGame);
            }
        }

        private void Callback_FlagLost(Arena arena, Player player, short flagId, FlagLostReason reason)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.IsMusicPlaying)
            {
                ad.IsMusicPlaying = false;
                _chat.SendArenaMessage(arena, ChatSound.MusicStop, "");
            }
        }

        private void Callback_FlagOnMap(Arena arena, short flagId, TileCoordinates coordinates, short freq)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.FlagMode != FlagMode.OwnAllDropped)
                return;

            if (freq == -1)
                return; // unowned

            ICarryFlagGame? carryFlagGame = arena.GetInterface<ICarryFlagGame>();
            if (carryFlagGame == null)
                return;

            short flagCount;
            bool isWin = true;

            try
            {
                // Check that all flags are dropped and that one team owns them all.
                flagCount = carryFlagGame.GetFlagCount(arena);
                for (short i = 0; i < flagCount; i++)
                {
                    if (!carryFlagGame.TryGetFlagInfo(arena, i, out IFlagInfo? flagInfo)
                        || flagInfo.State != FlagState.OnMap
                        || flagInfo.Freq != freq)
                    {
                        isWin = false;
                        break;
                    }
                }
            }
            finally
            {
                arena.ReleaseInterface(ref carryFlagGame);
            }

            if (isWin)
            {
                DoFlagWin(arena, ad, freq);
            }
        }

        private void DoFlagWin(Arena arena, ArenaData ad, short freq)
        {
            int playerCount = 0;
            int onFreq = 0;

            _playerData.Lock();

            try
            {
                foreach (Player otherPlayer in _playerData.Players)
                {
                    if (otherPlayer.Arena == arena
                        && otherPlayer.Status == PlayerState.Playing
                        && otherPlayer.Ship != ShipType.Spec)
                    {
                        playerCount++;

                        if (otherPlayer.Freq == freq)
                        {
                            onFreq++;
                        }
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            // reward
            int points = (int)(playerCount * playerCount * ad.FlagRewardRatio);

            // jackpot
            IJackpot? jackpot = arena.GetInterface<IJackpot>();
            if (jackpot != null)
            {
                try
                {
                    points += jackpot.GetJackpot(arena);
                }
                finally
                {
                    arena.ReleaseInterface(ref jackpot);
                }
            }

            // split points
            if (onFreq > 0 && ad.SplitPoints)
            {
                points /= onFreq;
            }

            // Reset the game with a win.
            ICarryFlagGame? carryFlagGame = arena.GetInterface<ICarryFlagGame>();
            if (carryFlagGame != null)
            {
                try
                {
                    carryFlagGame.ResetGame(arena, freq, points, true);
                }
                finally
                {
                    arena.ReleaseInterface(ref carryFlagGame);
                }
            }

            // End the 'game' interval for the arena.
            IPersistExecutor? persistExecutor = arena.GetInterface<IPersistExecutor>();
            if (persistExecutor != null)
            {
                try
                {
                    persistExecutor.EndInterval(PersistInterval.Game, arena);
                }
                finally
                {
                    arena.ReleaseInterface(ref persistExecutor);
                }
            }
        }

        private enum FlagMode
        {
            None = -1,

            /// <summary>
            /// A team wins when they're carrying all the flags. (jackpot/running-style)
            /// </summary>
            CarryAll = 0,

            /// <summary>
            /// A team wins when they own all of the dropped flags. (warzone style)
            /// </summary>
            OwnAllDropped = 1,
        }

        private class ArenaData : IResettable
        {
            // settings
            public FlagMode FlagMode;
            public double FlagRewardRatio;
            public bool SplitPoints;
            public bool IsVictoryMusicEnabled;

            // state
            public bool IsMusicPlaying = false;

            public bool TryReset()
            {
                FlagMode = FlagMode.None;
                FlagRewardRatio = 0;
                SplitPoints = false;
                IsVictoryMusicEnabled = false;
                IsMusicPlaying = false;
                return true;
            }
        }
    }
}
