using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that implements capability management (<see cref="ICapabilityManager"/>) functionality by group (<see cref="IGroupManager"/>).
    ///
    /// <para>
    /// Groups are configured in the "groupdef.conf" global config file.  
    /// Within it, groups are defined and capablities assigned to each group. 
    /// Sections are the group names and the properties within them are the capabilities.
    /// </para>
    /// 
    /// <para>
    /// Groups are further configured in the "staff.conf" global config file.
    /// The [GroupPasswords] section can be used to set passwords for groups that can be logged into.
    /// Group membership can be configured globally using the [(global)] section, 
    /// or per arena using the base arena name for the section name 
    /// (e.g. [turf] for turf, turf1, ..., turf{N}  arenas).
    /// </para>
    /// </summary>
    [CoreModuleInfo]
    public class CapabilityManager : IModule, ICapabilityManager, IGroupManager
    {
        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private IArenaManager _arenaManager;
        private ILogManager _logManager;
        private IConfigManager _configManager;
        private InterfaceRegistrationToken _iCapabilityManagerToken;
        private InterfaceRegistrationToken _iGroupManagerToken;

        /// <summary>
        /// Enumeration representing the source of group membership.
        /// </summary>
        private enum GroupSource
        {
            /// <summary>
            /// No source, default value.
            /// </summary>
            Default,

            /// <summary>
            /// Global config, Global section: [(global)]
            /// </summary>
            Global,

            /// <summary>
            /// Global config, Arena section: [&lt;arena base name&gt;]
            /// </summary>
            Arena,

#if CFG_USE_ARENA_STAFF_LIST
            /// <summary>
            /// Arena config, [Staff] section
            /// </summary>
            ArenaList, 
#endif
            /// <summary>
            /// Temporary, not persisted in a config file.
            /// </summary>
            Temp,
        }

        private class PlayerData
        {
            /// <summary>
            /// The player's current group.
            /// </summary>
            public string Group;

            /// <summary>
            /// The source of the <see cref="Group"/>.
            /// </summary>
            public GroupSource Source;
        }

        private int _pdkey;

        private ConfigHandle _groupDefConfHandle;
        private ConfigHandle _staffConfHandle;

        private const string Group_Default = "default";
        private const string Group_None = "none";

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IPlayerData playerData,
            IArenaManager arenaManager,
            ILogManager logManager,
            IConfigManager configManager)
        {
            _broker = broker;
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

            _pdkey = _playerData.AllocatePlayerData<PlayerData>();

            PlayerActionCallback.Register(_broker, Callback_PlayerAction);
            NewPlayerCallback.Register(_broker, Callback_NewPlayer);

            _groupDefConfHandle = _configManager.OpenConfigFile(null, "groupdef.conf");
            _staffConfHandle = _configManager.OpenConfigFile(null, "staff.conf");

            _iCapabilityManagerToken = _broker.RegisterInterface<ICapabilityManager>(this);
            _iGroupManagerToken = _broker.RegisterInterface<IGroupManager>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<ICapabilityManager>(ref _iCapabilityManagerToken) != 0)
                return false;

            if (_broker.UnregisterInterface<IGroupManager>(ref _iGroupManagerToken) != 0)
                return false;

            _configManager.CloseConfigFile(_groupDefConfHandle);
            _configManager.CloseConfigFile(_staffConfHandle);

            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);
            NewPlayerCallback.Unregister(_broker, Callback_NewPlayer);

            _playerData.FreePlayerData(_pdkey);

            return true;
        }

        #endregion

        #region ICapabilityManager Members

        bool ICapabilityManager.HasCapability(Player p, string capability)
        {
            if (p == null)
                return false;

            if (p[_pdkey] is not PlayerData pd)
                return false;

            return _configManager.GetStr(_groupDefConfHandle, pd.Group, capability) != null;
        }

        bool ICapabilityManager.HasCapability(string name, string capability)
        {
            string group = _configManager.GetStr(_staffConfHandle, Constants.AG_GLOBAL, name);
            if (string.IsNullOrEmpty(group))
                group = Group_Default;

            return _configManager.GetStr(_groupDefConfHandle, group, capability) != null;
        }

        bool ICapabilityManager.HasCapability(Player p, Arena arena, string capability)
        {
            if (p == null || arena == null)
                return false;

            PlayerData tempPd = new PlayerData();
            UpdateGroup(p, tempPd, arena, false);

            return _configManager.GetStr(_groupDefConfHandle, tempPd.Group, capability) != null;
        }

        bool ICapabilityManager.HigherThan(Player a, Player b)
        {
            if (a == null || b == null)
                return false;

            if (b[_pdkey] is not PlayerData bpd)
                return false;

            return ((ICapabilityManager)this).HasCapability(a, $"higher_than_{bpd.Group}");
        }

        #endregion

        #region IGroupManager Members

        string IGroupManager.GetGroup(Player p)
        {
            if (p == null)
                return null;

            if (p[_pdkey] is not PlayerData pd)
                return null;

            return pd.Group;
        }

        void IGroupManager.SetPermGroup(Player p, string group, bool global, string info)
        {
            if (p == null)
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            // first set it for the current session
            pd.Group = group;

            // now set it permanently
            if (global)
            {
                _configManager.SetStr(_staffConfHandle, Constants.AG_GLOBAL, p.Name, group, info, true);
                pd.Source = GroupSource.Global;
            }
            else if (p.Arena != null)
            {
                _configManager.SetStr(_staffConfHandle, p.Arena.BaseName, p.Name, group, info, true);
                pd.Source = GroupSource.Arena;
            }
        }

        void IGroupManager.SetTempGroup(Player p, string group)
        {
            if (p == null)
                return;

            if (string.IsNullOrWhiteSpace(group))
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            pd.Group = group;
            pd.Source = GroupSource.Temp;
        }

        void IGroupManager.RemoveGroup(Player p, string info)
        {
            if (p == null)
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            // in all cases, set current group to default
            pd.Group = Group_Default;

            switch (pd.Source)
            {
                case GroupSource.Default:
                    break; // player is in the default group already, nothing to do

                case GroupSource.Global:
                    _configManager.SetStr(_staffConfHandle, Constants.AG_GLOBAL, p.Name, Group_Default, info, true);
                    break;

                case GroupSource.Arena:
                    _configManager.SetStr(_staffConfHandle, p.Arena.BaseName, p.Name, Group_Default, info, true);
                    break;
#if CFG_USE_ARENA_STAFF_LIST
                case CapSource.ArenaList:
                    _configManager.SetStr(p.Arena.Cfg, "Staff", p.Name, Group_Default, info);
                    break;
#endif
                case GroupSource.Temp:
                    break;
            }
        }

        bool IGroupManager.CheckGroupPassword(string group, string pw)
        {
            string correctPw = _configManager.GetStr(_staffConfHandle, "GroupPasswords", group);

            if (string.IsNullOrWhiteSpace(correctPw))
                return false;

            return string.Equals(correctPw, pw, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            switch (action)
            {
                case PlayerAction.PreEnterArena:
                    UpdateGroup(p, pd, arena, true);
                    break;

                case PlayerAction.Connect:
                    UpdateGroup(p, pd, null, true);
                    break;

                case PlayerAction.Disconnect:
                case PlayerAction.LeaveArena:
                    pd.Group = Group_None;
                    break;
            }
        }

        private void Callback_NewPlayer(Player p, bool isNew)
        {
            if (p == null)
                return;

            if (isNew)
            {
                if (p[_pdkey] is not PlayerData pd)
                    return;

                pd.Group = Group_None;
            }
        }

        private void UpdateGroup(Player p, PlayerData pd, Arena arena, bool log)
        {
            if (p == null || pd == null)
                return;

            if (!p.Flags.Authenticated)
            {
                // if the player hasn't been authenticated against either the
                // biller or password file, don't assign groups based on name.
                pd.Group = Group_Default;
                pd.Source = GroupSource.Default;
                return;
            }

            string g;
            if (arena != null && !string.IsNullOrEmpty(g = _configManager.GetStr(_staffConfHandle, arena.BaseName, p.Name)))
            {
                pd.Group = g;
                pd.Source = GroupSource.Arena;

                if (log)
                    _logManager.LogP(LogLevel.Drivel, nameof(CapabilityManager), p, "assigned to group '{0}' (arena)", pd.Group);
            }
#if CFG_USE_ARENA_STAFF_LIST
            else if (arena != null && arena.Cfg != null && !string.IsNullOrEmpty(g = _configManager.GetStr(arena.Cfg, "Staff", p.Name)))
            {
                pd.Group = g;
                pd.Source = CapSource.ArenaList;
                if (log)
                    _logManager.LogP(LogLevel.Drivel, "CapabilityManager", p, "assigned to group '{0}' (arenaconf)", pd.Group);
            }
#endif
            else if (!string.IsNullOrEmpty(g = _configManager.GetStr(_staffConfHandle, Constants.AG_GLOBAL, p.Name)))
            {
                // only global groups available for now
                pd.Group = g;
                pd.Source = GroupSource.Global;
                if (log)
                    _logManager.LogP(LogLevel.Drivel, nameof(CapabilityManager), p, "assigned to group '{0}' (global)", pd.Group);
            }
            else
            {
                pd.Group = Group_Default;
                pd.Source = GroupSource.Default;
            }
        }
    }
}
