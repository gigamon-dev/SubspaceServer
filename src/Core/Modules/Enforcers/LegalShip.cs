using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.Modules.Enforcers
{
    /// <summary>
    /// Module that enforces legal ships by arena and by freq.
    /// The setting to specify which ships are allowed in the arena is Legalship:ArenaMask.
    /// The setting to specify which ships are allowed for a freq is Legalship:Freq#Mask, where # is the freq # (e.g. Freq0Mask for freq 0, Freq1Mask for freq 1, etc).
    /// Each ship is represented as a bit within the mask, <see cref="ShipMask"/>.
    /// </summary>
    [CoreModuleInfo]
    public class LegalShip : IModule, IArenaAttachableModule, IFreqManagerEnforcerAdvisor
    {
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private IObjectPoolManager _objectPoolManager;

        private ArenaDataKey<ArenaData> _adKey;

        #region Module members

        public bool Load(
            ComponentBroker broker, 
            IArenaManager arenaManager,
            IConfigManager configManager, 
            IObjectPoolManager objectPoolManager)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            ad.AdvisorToken = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            if (ad.AdvisorToken != null)
            {
                arena.UnregisterAdvisor(ref ad.AdvisorToken);
            }

            return true;
        }

        #endregion

        #region IFreqManagerEnforcerAdvisor

        [ConfigHelp("Legalship", "ArenaMask", ConfigScope.Arena, typeof(int), Range = "0-255", DefaultValue = "255", 
            Description = "The ship mask of allowed ships in the arena. 1=warbird, 2=javelin, etc.")]
        [ConfigHelp("Legalship", "Freq0Mask", ConfigScope.Arena, typeof(int), Range = "0-255", DefaultValue = "255",
            Description = "The ship mask of allowed ships for freq 0. Ships must also be allowed on the arena mask. You can define a mask for any freq (FreqXMask).")]
        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder errorMessage)
        {
            if (player.Arena == null || !player.Arena.TryGetExtraData(_adKey, out ArenaData ad))
                return ShipMask.None;

            // arena
            ShipMask arenaMask;
            if (ad.ArenaMask == null)
                ad.ArenaMask = arenaMask = (ShipMask)_configManager.GetInt(player.Arena.Cfg, "Legalship", "ArenaMask", 255);
            else
                arenaMask = ad.ArenaMask.Value;

            // freq
            if (!ad.FreqMasks.TryGetValue(freq, out ShipMask freqMask))
                ad.FreqMasks[freq] = freqMask = (ShipMask)_configManager.GetInt(player.Arena.Cfg, "Legalship", $"Freq{freq}Mask", 255);

            // combined
            ShipMask playerMask = arenaMask & freqMask;

            if (errorMessage != null && !playerMask.HasShip(ship))
            {
                StringBuilder shipBuilder = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    if (playerMask != ShipMask.None)
                    {
                        for (int i = 0; i < (int)ShipType.Spec; i++)
                        {
                            if (playerMask.HasShip((ShipType)i))
                            {
                                if (shipBuilder.Length > 0)
                                    shipBuilder.Append(", ");

                                shipBuilder.Append((ShipType)i);
                            }
                        }
                    }

                    if (freqMask == ShipMask.All)
                    {
                        if (playerMask == ShipMask.None)
                            errorMessage.Append("You may not leave spectator mode in this arena.");
                        else
                            errorMessage.Append($"Your allowed ships in this arena are: {shipBuilder}");
                    }
                    else
                    {
                        if (playerMask == ShipMask.None)
                            errorMessage.Append("You may not leave spectator mode on this frequency.");
                        else
                            errorMessage.Append($"Your allowed ships on this frequency are: {shipBuilder}");
                    }
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(shipBuilder);
                }
            }

            return playerMask;
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.ConfChanged)
                if (arena.TryGetExtraData(_adKey, out ArenaData arenaData))
                    arenaData.ClearSettings();
        }

        #endregion

        #region Helper types

        private class ArenaData : IPooledExtraData
        {
            public AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor> AdvisorToken = null;
            
            // settings
            public ShipMask? ArenaMask = null;
            public Dictionary<short, ShipMask> FreqMasks = new();

            public void ClearSettings()
            {
                ArenaMask = null;
                FreqMasks.Clear();
            }

            void IPooledExtraData.Reset()
            {
                AdvisorToken = null;
                ClearSettings();
            }
        }

        #endregion
    }
}
