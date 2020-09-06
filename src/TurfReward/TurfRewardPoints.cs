using SS.Core;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;

namespace TurfReward
{
    [ModuleInfo("Another test module.")]
    public class TurfRewardPoints : IModule, IArenaAttachableModule, ITurfRewardPoints
    {
        private ILogManager _log;
        private ITurfReward _turfReward;

        #region IModule Members

        Type[] IModule.InterfaceDependencies { get; } = new Type[]
        {
            typeof(ILogManager),
            typeof(ITurfReward),
        };

        bool IModule.Load(ModuleManager mm, IReadOnlyDictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _log = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            _turfReward = interfaceDependencies[typeof(ITurfReward)] as ITurfReward;

            _log.Log(LogLevel.Drivel, $"<{nameof(TurfRewardPoints)}> Load");
            mm.RegisterInterface<ITurfRewardPoints>(this);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            mm.UnregisterInterface<ITurfRewardPoints>();
            _log.Log(LogLevel.Drivel, $"<{nameof(TurfRewardPoints)}> Unload");
            return true;
        }

        #endregion

        #region IModuleArenaAttachable Members

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            _log.Log(LogLevel.Drivel, $"<{nameof(TurfRewardPoints)}> {{{arena}}} AttachModule");
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            _log.Log(LogLevel.Drivel, $"<{nameof(TurfRewardPoints)}> {{{arena}}} DetachModule");
            return true;
        }

        #endregion
    }
}
