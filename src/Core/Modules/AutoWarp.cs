using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Utilities;
using System;
using System.Linq;
using System.Text;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class AutoWarp : IModule
    {
        private IArenaManager _arenaManager;
        private IGame _game;

        #region IModule Members

        public bool Load(ComponentBroker broker, IArenaManager arenaManager, IGame game)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));

            MapRegionCallback.Register(broker, Callback_MapRegion);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            MapRegionCallback.Unregister(broker, Callback_MapRegion);
            return true;
        }

        #endregion

        private void Callback_MapRegion(Player p, MapRegion region, short x, short y, bool entering)
        {
            if (p == null)
                return;

            if (region == null)
                return;

            if (!entering)
                return;

            var aw = region.AutoWarp;
            if (aw == null)
                return;

            if (string.IsNullOrEmpty(aw.ArenaName))
            {
                _game.WarpTo(p, aw.X, aw.Y);
            }
            else
            {
                _arenaManager.SendToArena(p, aw.ArenaName, aw.X, aw.Y);
            }
        }
    }
}
