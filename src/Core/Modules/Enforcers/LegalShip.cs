using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using LegalShipSettings = SS.Core.ConfigHelp.Constants.Arena.LegalShip;

namespace SS.Core.Modules.Enforcers
{
    /// <summary>
    /// Module that enforces legal ships by arena and by freq.
    /// The setting to specify which ships are allowed in the arena is Legalship:ArenaMask.
    /// The setting to specify which ships are allowed for a freq is Legalship:Freq#Mask, where # is the freq # (e.g. Freq0Mask for freq 0, Freq1Mask for freq 1, etc).
    /// Each ship is represented as a bit within the mask, <see cref="ShipMask"/>.
    /// </summary>
    [CoreModuleInfo]
    public sealed class LegalShip : IModule, IArenaAttachableModule, IFreqManagerEnforcerAdvisor
    {
        private readonly IArenaManager _arenaManager;
        private readonly IConfigManager _configManager;
        private readonly IObjectPoolManager _objectPoolManager;

        private ArenaDataKey<ArenaData> _adKey;

        public LegalShip(
            IArenaManager arenaManager,
            IConfigManager configManager,
            IObjectPoolManager objectPoolManager)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            ad.AdvisorToken = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.AdvisorToken != null)
            {
                arena.UnregisterAdvisor(ref ad.AdvisorToken);
            }

            return true;
        }

        #endregion

        #region IFreqManagerEnforcerAdvisor

        [ConfigHelp<int>("LegalShip", "ArenaMask", ConfigScope.Arena, Min = 0, Max = 255, Default = 255,
            Description = "The ship mask of allowed ships in the arena. 1=warbird, 2=javelin, etc.")]
        [ConfigHelp<int>("LegalShip", "Freq0Mask", ConfigScope.Arena, Min = 0, Max = 255, Default = 255,
            Description = "The ship mask of allowed ships for freq 0. Ships must also be allowed on the arena mask. You can define a mask for any freq (FreqXMask).")]
        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder? errorMessage)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return ShipMask.None;

            ConfigHandle ch = arena.Cfg!;

            // arena
            ShipMask arenaMask;
            if (ad.ArenaMask == null)
                ad.ArenaMask = arenaMask = (ShipMask)_configManager.GetInt(ch, "LegalShip", "ArenaMask", LegalShipSettings.ArenaMask.Default);
            else
                arenaMask = ad.ArenaMask.Value;

            // freq
            if (!ad.FreqMasks.TryGetValue(freq, out ShipMask freqMask))
            {
                Span<char> freqMaskKey = stackalloc char["Freq####Mask".Length];
                if (freq >= 0 && freq <= 9999 && freqMaskKey.TryWrite($"Freq{freq}Mask", out int charsWritten))
                {
                    freqMaskKey = freqMaskKey[..charsWritten];
                }
                else
                {
                    "Freq0Mask".CopyTo(freqMaskKey);
                    freqMaskKey = freqMaskKey[.."Freq0Mask".Length];
                }

                ad.FreqMasks[freq] = freqMask = (ShipMask)_configManager.GetInt(ch, "LegalShip", freqMaskKey, LegalShipSettings.Freq0Mask.Default);
            }

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
                        {
                            errorMessage.Append("You may not leave spectator mode in this arena.");
                        }
                        else
                        {
                            errorMessage.Append($"Your allowed ships in this arena are: ");
                            errorMessage.Append(shipBuilder);
                        }
                    }
                    else
                    {
                        if (playerMask == ShipMask.None)
                        {
                            errorMessage.Append("You may not leave spectator mode on this frequency.");
                        }
                        else
                        {
                            errorMessage.Append($"Your allowed ships on this frequency are: ");
                            errorMessage.Append(shipBuilder);
                        }
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
                if (arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                    arenaData.ClearSettings();
        }

        #endregion

        #region Helper types

        private class ArenaData : IResettable
        {
            public AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor>? AdvisorToken = null;

            // settings
            public ShipMask? ArenaMask = null;
            public readonly Dictionary<short, ShipMask> FreqMasks = [];

            public void ClearSettings()
            {
                ArenaMask = null;
                FreqMasks.Clear();
            }

            public bool TryReset()
            {
                AdvisorToken = null;
                ClearSettings();
                return true;
            }
        }

        #endregion
    }
}
