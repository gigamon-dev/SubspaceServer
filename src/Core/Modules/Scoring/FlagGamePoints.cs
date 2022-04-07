using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using System;

namespace SS.Core.Modules.Scoring
{
    // For a warzone-style win, watch for flag drop and check if all flags are dropped and one team owns them all.
    // For a running/jackpot style win, watch for flag pickup and check if 1 team is carrying all the flags.
    public class FlagGamePoints : IModule, IArenaAttachableModule
    {
        private IArenaManager _arenaManager;
        private ICarryFlagGame _carryFlagGame;
        private IChat _chat;
        private IConfigManager _configManager;
        private IPlayerData _playerData;

        private ArenaDataKey<ArenaData> _adKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            ICarryFlagGame carryFlagGame,
            IChat chat,
            IConfigManager configManager,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _carryFlagGame = carryFlagGame ?? throw new ArgumentNullException(nameof(carryFlagGame));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _arenaManager.FreeArenaData(_adKey);

            return true;
        }

        [ConfigHelp("Flag", "FlagMode", ConfigScope.Arena, typeof(FlagMode), DefaultValue = "None", 
            Description ="Style of flag game.\n" +
            "0 = carry all flags to win (e.g. running/jackpot)\n," +
            "1 = own all dropped flags to win (e.g. warzone),\n" +
            "-1 = None (no win condition)")]
        [ConfigHelp("Flag", "FlagReward", ConfigScope.Arena, typeof(int), DefaultValue = "5000", 
            Description = "The basic flag reward is calculated as (players in arena)^2 * FlagReward / 1000.")]
        [ConfigHelp("Flag", "SplitPoints", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
            Description = "Whether to split a flag reward between the members of a freq or give them each the full amount.")]
        [ConfigHelp("Misc", "VictoryMusic", ConfigScope.Arena, typeof(bool), DefaultValue = "1",
            Description = "Whether to play victory music or not.")]
        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            ad.FlagMode = _configManager.GetEnum(arena.Cfg, "Flag", "FlagMode", FlagMode.None);
            ad.FlagRewardRatio = _configManager.GetInt(arena.Cfg, "Flag", "FlagReward", 5000) / 1000.0;
            ad.SplitPoints = _configManager.GetInt(arena.Cfg, "Flag", "SplitPoints", 0) != 0;
            ad.IsVictoryMusicEnabled = _configManager.GetInt(arena.Cfg, "Misc", "VictoryMusic", 1) != 0;

            FlagGainCallback.Register(arena, Callback_FlagGain);
            FlagLostCallback.Register(arena, Callback_FlagLost);
            FlagOnMapCallback.Register(arena, Callback_FlagOnMap);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            FlagGainCallback.Unregister(arena, Callback_FlagGain);
            FlagLostCallback.Unregister(arena, Callback_FlagLost);
            FlagOnMapCallback.Unregister(arena, Callback_FlagOnMap);

            return true;
        }

        #endregion

        private void Callback_FlagGain(Arena arena, Player player, short flagId, FlagPickupReason reason)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.FlagMode == FlagMode.CarryAll)
            {
                bool isWin = true;
                for (short i = 0; i < _carryFlagGame.GetFlagCount(arena); i++)
                {
                    if (!_carryFlagGame.TryGetFlagInfo(arena, i, out IFlagInfo flagInfo)
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
                    && _carryFlagGame.GetFlagCount(arena, player.Freq) == _carryFlagGame.GetFlagCount(arena))
                {
                    // start music
                    ad.IsMusicPlaying = true;
                    _chat.SendArenaMessage(arena, ChatSound.MusicLoop, "");
                }
            }
        }

        private void Callback_FlagLost(Arena arena, Player player, short flagId, FlagLostReason reason)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.IsMusicPlaying)
            {
                ad.IsMusicPlaying = false;
                _chat.SendArenaMessage(arena, ChatSound.MusicStop, "");
            }
        }

        private void Callback_FlagOnMap(Arena arena, short flagId, MapCoordinate mapCoordinate, short freq)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.FlagMode != FlagMode.OwnAllDropped)
                return;

            if (freq == -1)
                return; // unowned

            // Check that all flags are dropped and that one team owns them all.
            bool isWin = true;
            for (short i = 0; i < _carryFlagGame.GetFlagCount(arena); i++)
            {
                if (!_carryFlagGame.TryGetFlagInfo(arena, i, out IFlagInfo flagInfo)
                    || flagInfo.State != FlagState.OnMap
                    || flagInfo.Freq != freq)
                {
                    isWin = false;
                    break;
                }
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
            IJackpot jackpot = arena.GetInterface<IJackpot>();
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
            _carryFlagGame.ResetGame(arena, freq, points);

            // End the 'game' interval for the arena.
            IPersistExecutor persistExecutor = arena.GetInterface<IPersistExecutor>();
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

        private class ArenaData
        {
            // settings
            public FlagMode FlagMode;
            public double FlagRewardRatio;
            public bool SplitPoints;
            public bool IsVictoryMusicEnabled;

            // state
            public bool IsMusicPlaying = false;
        }
    }
}
