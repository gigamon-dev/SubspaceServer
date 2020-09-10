using SS.Core;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;

namespace TurfReward
{
    [ModuleInfo(info_turf_reward)]
    public class TurfModule : IModule, IArenaAttachableModule, ITurfReward
    {
        private const string info_turf_reward = 
            "This module simulates what the turf_reward module could look like. " +
            "It is not a real implementation. " +
            "It is being used to test external assembly loading and arena attaching.";

        private ILogManager _log;
        private InterfaceRegistrationToken _iTurfRewardToken;

        #region IModule Members

        Type[] IModule.InterfaceDependencies { get; } = new Type[]
        {
            typeof(ILogManager),
        };

        bool IModule.Load(ModuleManager mm, IReadOnlyDictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _log = interfaceDependencies[typeof(ILogManager)] as ILogManager;

            _log.LogM(LogLevel.Drivel, nameof(TurfReward), "Load");
            _iTurfRewardToken = mm.RegisterInterface<ITurfReward>(this);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            _log.LogM(LogLevel.Drivel, nameof(TurfReward), "Unload");

            if (mm.UnregisterInterface<ITurfReward>(ref _iTurfRewardToken) != 0)
                return false;
            
            return true;
        }

        #endregion

        #region IModuleArenaAttachable Members

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            _log.LogA(LogLevel.Drivel, nameof(TurfReward), arena, "AttachModule");
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            _log.LogA(LogLevel.Drivel, nameof(TurfReward), arena, "DetachModule");
            return true;
        }

        #endregion
    }
}
