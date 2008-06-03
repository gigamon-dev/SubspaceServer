using System;
using System.Collections.Generic;
using System.Text;

using SS.Core;

namespace TurfReward
{
    class TurfRewardPoints : IModule, IArenaAttachableModule
    {
        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get { return null; }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            Console.WriteLine("TurfRewardPoints:Load");
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            Console.WriteLine("TurfRewardPoints:Unload");
            return true;
        }

        #endregion

        #region IModuleArenaAttachable Members

        void IArenaAttachableModule.AttachModule(Arena arena)
        {
            //arena.
            Console.WriteLine("TurfRewardPoints:AttachModule");
        }

        void IArenaAttachableModule.DetachModule(Arena arena)
        {
            Console.WriteLine("TurfRewardPoints:DetachModule");
        }

        #endregion
    }
}
