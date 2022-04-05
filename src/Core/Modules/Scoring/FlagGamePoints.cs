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
        private IChat _chat;
        private IConfigManager _configManager;
        private IPlayerData _playerData;

        private ArenaDataKey<ArenaData> _adKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            IConfigManager configManager,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
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

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            ad.CarryFlagGame = arena.GetInterface<ICarryFlagGame>();
            if (ad.CarryFlagGame == null)
                return false;

            ad.FlagMode = _configManager.GetEnum(arena.Cfg, "Flag", "FlagMode", FlagMode.None);

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

            if (ad.CarryFlagGame != null)
                arena.ReleaseInterface(ref ad.CarryFlagGame);

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
                for (short i = 0; i < ad.CarryFlagGame.GetFlagCount(arena); i++)
                {
                    if (!ad.CarryFlagGame.TryGetFlagInfo(arena, i, out IFlagInfo flagInfo)
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
                if (ad.CarryFlagGame.GetFlagCount(arena, player.Freq) == ad.CarryFlagGame.GetFlagCount(arena))
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
            for (short i = 0; i < ad.CarryFlagGame.GetFlagCount(arena); i++)
            {
                if (!ad.CarryFlagGame.TryGetFlagInfo(arena, i, out IFlagInfo flagInfo)
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

            // TODO: flag reward and jackpot logic

            ad.CarryFlagGame.ResetGame(arena, freq, 100);

            // TODO: maybe enter persist game interval here?
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
            public FlagMode FlagMode;
            public ICarryFlagGame CarryFlagGame;
            public bool IsMusicPlaying = false;
        }
    }
}
