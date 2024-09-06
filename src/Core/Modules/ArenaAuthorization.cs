using SS.Core.ComponentAdvisors;
using SS.Core.ComponentInterfaces;
using System;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides the ability to restrict players from entering an arena based on a configured capability (General:NeedCap in the arena.conf).
    /// </summary>
    public class ArenaAuthorization(ICapabilityManager capabilityManager, IConfigManager configManager) : IModule, IArenaAuthorizationAdvisor
    {
        private readonly ICapabilityManager _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

        private AdvisorRegistrationToken<IArenaAuthorizationAdvisor>? _iArenaAuthorizationAdvisorToken;

        #region Module members

        public bool Load(IComponentBroker broker)
        {
            _iArenaAuthorizationAdvisorToken = broker.RegisterAdvisor<IArenaAuthorizationAdvisor>(this);
            return true;
        }

        public bool Unload(IComponentBroker broker)
        {
            if (!broker.UnregisterAdvisor(ref _iArenaAuthorizationAdvisorToken))
                return false;

            return true;
        }

        #endregion

        #region IArenaAuthorizationAdvisor

        [ConfigHelp("General", "NeedCap", ConfigScope.Arena,
            Description = """
            If this setting is present for an arena, any player entering
            the arena must have the capability specified this setting.
            This can be used to restrict arenas to certain groups of
            players. For example, setting it to "isstaff" will only
            allow staff members to enter the arena.
            """)]
        bool IArenaAuthorizationAdvisor.IsAuthorizedToEnter(Player player, Arena arena, StringBuilder? errorMessage)
        {
            ConfigHandle? configHandle = arena.Cfg;
            if (configHandle is null)
            {
                return false;
            }

            string? capability = _configManager.GetStr(configHandle, "General", "NeedCap");
            if (string.IsNullOrWhiteSpace(capability))
                return true;

            return _capabilityManager.HasCapability(player, arena, capability);
        }

        #endregion
    }
}
