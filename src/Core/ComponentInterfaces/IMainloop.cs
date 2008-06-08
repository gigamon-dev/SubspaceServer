using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    internal interface IMainloop : IComponentInterface
    {
        /// <summary>
        /// called by the main thread which starts processing the timers
        /// </summary>
        void RunLoop();
    }
}
