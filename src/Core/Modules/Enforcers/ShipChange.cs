using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Text;
using MiscSettings = SS.Core.ConfigHelp.Constants.Arena.Misc;

namespace SS.Core.Modules.Enforcers
{
    /// <summary>
    /// Module that enforces rules for changing ships:
    /// <list>
    ///     <item>
    ///         <term>arena.conf: Misc:ShipChangeInterval</term>
    ///         <description>Minimum allowed time between ship changes.</description>
    ///     </item>
    ///     <item>
    ///         <term>arena.conf: Misc:AntiwarpShipChange</term>
    ///         <description>Whether to prevent players not carrying flags to change ships while antiwarped.</description>
    ///     </item>
    ///     <item>
    ///         <term>arena.conf: Misc:AntiwarpFlagShipChange</term>
    ///         <description>Whether to prevent players carrying flags to change ships while antiwarped.</description>
    ///     </item>
    /// </list>
    /// </summary>
    [CoreModuleInfo]
    public class ShipChange : IModule, IArenaAttachableModule, IFreqManagerEnforcerAdvisor
    {
        private readonly IArenaManager _arenaManager;
        private readonly IConfigManager _configManager;
        private readonly IGame _game;
        private readonly IPlayerData _playerData;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        public ShipChange(
            IArenaManager arenaManager,
            IConfigManager configManager,
            IGame game,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _arenaManager.FreeArenaData(ref _adKey);
            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            ad.AdvisorToken = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (!arena.UnregisterAdvisor(ref ad.AdvisorToken))
                return false;

            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            return true;
        }

        #endregion

        #region IFreqManagerEnforcerAdvisor

        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder? errorMessage)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
            {
                return ShipMask.All;
            }

            if (player.Arena is null || !player.Arena.TryGetExtraData(_adKey, out ArenaData? ad))
            {
                return ShipMask.All;
            }

            if (pd.LastChange is not null
                && ad.ShipChangeInterval > TimeSpan.Zero
                && pd.LastChange.Value + ad.ShipChangeInterval > DateTime.UtcNow)
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
                bool hasFlags = player.Packet.FlagsCarried > 0;

                if ((hasFlags && ad.AntiwarpFlagger) || (!hasFlags && ad.AntiwarpNonFlagger))
                {
                    if (ship != player.Ship)
                        errorMessage?.Append("You are antiwarped!");

                    return player.Ship.GetShipMask(); // player can only stay in the current ship
                }
            }

            return ShipMask.All;
        }

        #endregion

        #region Callbacks

        [ConfigHelp<int>("Misc", "ShipChangeInterval", ConfigScope.Arena, Default = 500,
            Description = "The allowable interval between player ship changes, in ticks.")]
        [ConfigHelp<bool>("Misc", "AntiwarpShipChange", ConfigScope.Arena, Default = false,
            Description = "Whether to prevent players without flags from changing ships while antiwarped.")]
        [ConfigHelp<bool>("Misc", "AntiwarpFlagShipChange", ConfigScope.Arena, Default = false,
            Description = "Whether to prevent players with flags from changing ships while antiwarped.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ConfigHandle ch = arena.Cfg!;
                ad.ShipChangeInterval = TimeSpan.FromMilliseconds(_configManager.GetInt(ch, "Misc", "ShipChangeInterval", MiscSettings.ShipChangeInterval.Default) * 10);
                ad.AntiwarpNonFlagger = _configManager.GetBool(ch, "Misc", "AntiwarpShipChange", MiscSettings.AntiwarpShipChange.Default);
                ad.AntiwarpFlagger = _configManager.GetBool(ch, "Misc", "AntiwarpFlagShipChange", MiscSettings.AntiwarpFlagShipChange.Default);
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            if (newShip != oldShip && newShip != ShipType.Spec)
            {
                pd.LastChange = DateTime.UtcNow;
            }
        }

        #endregion

        #region Helper types

        private class ArenaData : IResettable
        {
            public AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor>? AdvisorToken = null;

            #region Settings

            public TimeSpan ShipChangeInterval;
            public bool AntiwarpNonFlagger;
            public bool AntiwarpFlagger;

            #endregion

            public bool TryReset()
            {
                AdvisorToken = null;
                ShipChangeInterval = TimeSpan.Zero;
                AntiwarpNonFlagger = false;
                AntiwarpFlagger = false;
                return true;
            }
        }

        private class PlayerData : IResettable
        {
            public DateTime? LastChange = null;

            public bool TryReset()
            {
                LastChange = null;
                return true;
            }
        }

        #endregion
    }
}
