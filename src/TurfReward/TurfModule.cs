using System;
using System.Collections.Generic;
using System.Text;

using SS.Core;

namespace TurfReward
{
    class TurfModule : IModule, IArenaAttachableModule
    {
        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get { return null; }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            Console.WriteLine("TurfReward:Load");
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            Console.WriteLine("TurfReward:Unload");
            return true;
        }

        #endregion

        #region IModuleArenaAttachable Members

        void IArenaAttachableModule.AttachModule(Arena arena)
        {
            //arena.
            Console.WriteLine("TurfReward:AttachModule");
        }

        void IArenaAttachableModule.DetachModule(Arena arena)
        {
            Console.WriteLine("TurfReward:DetachModule");
        }

        #endregion
    }
}
