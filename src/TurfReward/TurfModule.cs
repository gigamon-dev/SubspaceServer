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

        #region IModule Members

        Type[] IModule.InterfaceDependencies => new Type[]
        {
            typeof(ILogManager),
        };

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _log = interfaceDependencies[typeof(ILogManager)] as ILogManager;

            _log.Log(LogLevel.Drivel, $"<{nameof(TurfReward)}> Load");
            mm.RegisterInterface<ITurfReward>(this);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            mm.UnregisterInterface<ITurfReward>();
            _log.Log(LogLevel.Drivel, $"<{nameof(TurfReward)}> Unload");
            return true;
        }

        #endregion

        #region IModuleArenaAttachable Members

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            _log.Log(LogLevel.Drivel, $"<{nameof(TurfReward)}> {{{arena}}} AttachModule");
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            _log.Log(LogLevel.Drivel, $"<{nameof(TurfReward)}> {{{arena}}} DetachModule");
            return true;
        }

        #endregion
    }
}
