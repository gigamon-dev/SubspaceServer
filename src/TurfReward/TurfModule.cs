using SS.Core;
using SS.Core.ComponentInterfaces;
using System;

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
        private InterfaceRegistrationToken<ITurfReward> _iTurfRewardToken;

        #region IModule Members

        public bool Load(ComponentBroker broker, ILogManager log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));

            _log.LogM(LogLevel.Drivel, nameof(TurfReward), "Load");
            _iTurfRewardToken = broker.RegisterInterface<ITurfReward>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            _log.LogM(LogLevel.Drivel, nameof(TurfReward), "Unload");

            if (broker.UnregisterInterface(ref _iTurfRewardToken) != 0)
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
