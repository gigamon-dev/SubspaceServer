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
        private InterfaceRegistrationToken _iTurfRewardPointsToken;

        #region IModule Members

        public bool Load(ComponentBroker broker, ILogManager log, ITurfReward turfReward)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _turfReward = turfReward ?? throw new ArgumentNullException(nameof(turfReward));

            _log.LogM(LogLevel.Drivel, nameof(TurfRewardPoints), "Load");
            _iTurfRewardPointsToken = broker.RegisterInterface<ITurfRewardPoints>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            _log.LogM(LogLevel.Drivel, nameof(TurfRewardPoints), "Unload");

            if (broker.UnregisterInterface<ITurfRewardPoints>(ref _iTurfRewardPointsToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IModuleArenaAttachable Members

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            _log.LogA(LogLevel.Drivel, nameof(TurfRewardPoints), arena, "AttachModule");
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            _log.LogA(LogLevel.Drivel, nameof(TurfRewardPoints), arena, "DetachModule");
            return true;
        }

        #endregion
    }
}
