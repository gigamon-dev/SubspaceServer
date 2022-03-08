using SS.Core.ComponentAdvisors;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.Modules.Enforcers
{
    /// <summary>
    /// Module that enforces that players can only spectate.
    /// </summary>
    public class LockSpec : IModule, IArenaAttachableModule, IFreqManagerEnforcerAdvisor
    {
        private readonly Dictionary<Arena, AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor>> _arenaTokens = new();

        #region Module members

        public bool Load(ComponentBroker broker)
        {
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            return _arenaTokens.TryAdd(arena, arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this));
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (_arenaTokens.Remove(arena, out var token))
            {
                arena.UnregisterAdvisor(ref token);
            }

            return true;
        }

        #endregion

        #region IFreqManagerEnforcerAdvisor

        ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder errorMessage)
        {
            errorMessage?.Append("This arena does not allow players to leave spectator mode.");
            return ShipMask.None; // only allow spec
        }

        #endregion
    }
}
