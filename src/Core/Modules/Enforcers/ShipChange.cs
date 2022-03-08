using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Text;

namespace SS.Core.Modules.Enforcers
{
    public class ShipChange : IModule, IArenaAttachableModule, IFreqManagerEnforcerAdvisor
    {
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private IGame _game;
        private IPlayerData _playerData;

        //private IFlagCore _flagCore;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            IGame game,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            //_flagCore = broker.GetInterface<IFlagCore>();

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _arenaManager.FreeArenaData(_adKey);
            _playerData.FreePlayerData(_pdKey);

            //if (_flagCore != null)
            //    broker.ReleaseInterface(ref _flagCore);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            return true;
        }

        #endregion

        #region IFreqManagerEnforcerAdvisor

        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder errorMessage)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData pd)
                || pd.LastChange == null)
            {
                return ShipMask.All;
            }

            if (player.Arena == null || !player.Arena.TryGetExtraData(_adKey, out ArenaData ad))
            {
                return ShipMask.All;
            }

            if (pd.LastChange.Value + ad.ShipChangeInterval > DateTime.UtcNow 
                && ad.ShipChangeInterval > TimeSpan.Zero)
            {
                if (ship != player.Ship)
                    errorMessage?.Append("You've changed ship too recently. Please wait.");

                if (player.Ship != ShipType.Spec)
                    return player.Ship.GetShipMask(); // player can only stay in the current ship
                else
                    return ShipMask.None; // can't switch
            }

            if (player.Ship != ShipType.Spec
                && (ad.AntiwarpNonFlagger || ad.AntiwarpFlagger)
                && _game.IsAntiwarped(player, null))
            {
                int flags;

                // TODO: flag logic
                //if (_flagCore != null)
                //{
                //    flags = _flagCore.CountPlayerFlags(player);
                //}
                //else
                //{
                    flags = 0;
                //}

                if ((flags != 0 && ad.AntiwarpFlagger) || (flags == 0 && ad.AntiwarpNonFlagger))
                {
                    if (ship != player.Ship)
                        errorMessage?.Append("You are antiwarped!");

                    if (player.Ship != ShipType.Spec)
                        return player.Ship.GetShipMask(); // player can only stay in the current ship
                    else
                        return ShipMask.None; // can't switch
                }
            }

            return ShipMask.All;
        }

        #endregion

        #region Callbacks

        [ConfigHelp("Misc", "ShipChangeInterval", ConfigScope.Arena, typeof(int), DefaultValue = "500", 
            Description = "The allowable interval between player ship changes, in ticks.")]
        [ConfigHelp("Misc", "AntiwarpShipChange", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
            Description = "Whether to prevent players without flags from changing ships while antiwarped.")]
        [ConfigHelp("Misc", "AntiwarpFlagShipChange", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
            Description = "Whether to prevent players with flags from changing ships while antiwarped.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ad.ShipChangeInterval = TimeSpan.FromMilliseconds(_configManager.GetInt(arena.Cfg, "Misc", "ShipChangeInterval", 500) * 10);
                ad.AntiwarpNonFlagger = _configManager.GetInt(arena.Cfg, "Misc", "AntiwarpShipChange", 0) != 0;
                ad.AntiwarpFlagger = _configManager.GetInt(arena.Cfg, "Misc", "AntiwarpFlagShipChange", 0) != 0;
            }
        }

        private void Callback_ShipFreqChange(Player p, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            if (newShip != oldShip && newShip != ShipType.Spec)
            {
                pd.LastChange = DateTime.UtcNow;
            }
        }

        #endregion

        #region Helper types

        private class ArenaData : IPooledExtraData
        {
            public TimeSpan ShipChangeInterval;
            public bool AntiwarpNonFlagger;
            public bool AntiwarpFlagger;

            void IPooledExtraData.Reset()
            {
                ShipChangeInterval = TimeSpan.Zero;
                AntiwarpNonFlagger = false;
                AntiwarpFlagger = false;
            }
        }

        private class PlayerData : IPooledExtraData
        {
            public DateTime? LastChange = null;

            void IPooledExtraData.Reset()
            {
                LastChange = null;
            }
        }

        #endregion
    }
}
