using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using System;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for relocating, 'warping', players that move onto specially designated map regions defined in extended lvl files.
    /// Players can be warped to a new (x,y) coordinate on the map, and even be sent to another arena.
    /// </summary>
    [CoreModuleInfo]
    public class AutoWarp : IModule
    {
        private IArenaManager _arenaManager;
        private IGame _game;
        private IPrng _prng;

        #region IModule Members

        public bool Load(
            ComponentBroker broker, 
            IArenaManager arenaManager, 
            IGame game,
            IPrng prng)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _prng = prng?? throw new ArgumentNullException(nameof(prng));

            MapRegionCallback.Register(broker, Callback_MapRegion);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            MapRegionCallback.Unregister(broker, Callback_MapRegion);
            return true;
        }

        #endregion

        private void Callback_MapRegion(Player player, MapRegion region, short x, short y, bool entering)
        {
            if (player is null
                || region is null
                || !entering
                || region.AutoWarpDestinations.Count <= 0)
            {
                return;
            }

            var destination = region.AutoWarpDestinations.Count == 1
                ? region.AutoWarpDestinations[0]
                : region.AutoWarpDestinations[_prng.Number(0, region.AutoWarpDestinations.Count - 1)];

            if (string.IsNullOrWhiteSpace(destination.ArenaName))
            {
                _game.WarpTo(player, destination.X, destination.Y);
            }
            else
            {
                _arenaManager.SendToArena(player, destination.ArenaName, destination.X, destination.Y);
            }
        }
    }
}
