using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public interface IMainloopController : IComponentInterface
    {
        /// <summary>
        /// Signals the main loop to stop
        /// </summary>
        void Quit();
    }
}
