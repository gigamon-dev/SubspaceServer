using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace SS.Core.Modules
{
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

        private enum CapSource
        {
            Default, 
            Global, 
            Arena, 
#if CFG_USE_ARENA_STAFF_LIST
            ArenaList, 
#endif
            Temp, 
        }

        private class PlayerData
        {
            public string Group;
            public CapSource Source;
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

            PlayerActionCallback.Register(_broker, playerAction);
            NewPlayerCallback.Register(_broker, newPlayer);

            _groupDefConfHandle = _configManager.OpenConfigFile(null, "groupdef.conf", null, null);
            _staffConfHandle = _configManager.OpenConfigFile(null, "staff.conf", null, null);

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

            PlayerActionCallback.Unregister(_broker, playerAction);
            NewPlayerCallback.Unregister(_broker, newPlayer);

            _playerData.FreePlayerData(_pdkey);

            return true;
        }

        #endregion

        #region ICapabilityManager Members

        bool ICapabilityManager.HasCapability(Player p, string capability)
        {
            if (p == null)
                return false;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return false;

            //return !string.IsNullOrEmpty(_configManager.GetStr(_groupDefConfHandle, pd.Group, capability));
            return _configManager.GetStr(_groupDefConfHandle, pd.Group, capability) != null;
        }

        bool ICapabilityManager.HasCapability(string name, string capability)
        {
            string group = _configManager.GetStr(_staffConfHandle, Constants.AG_GLOBAL, name);
            if (string.IsNullOrEmpty(group))
                group = Group_Default;

            return !string.IsNullOrEmpty(_configManager.GetStr(_groupDefConfHandle, group, capability));
        }

        bool ICapabilityManager.HasCapability(Player p, Arena arena, string capability)
        {
            if (p == null || arena == null)
                return false;

            PlayerData tempPd = new PlayerData();
            updateGroup(p, tempPd, arena, false);

            return !string.IsNullOrEmpty(_configManager.GetStr(_groupDefConfHandle, tempPd.Group, capability));
        }

        bool ICapabilityManager.HigherThan(Player a, Player b)
        {
            return false;
        }

        #endregion

        #region IGroupManager Members

        string IGroupManager.GetGroup(Player p)
        {
            if (p == null)
                return null;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return null;

            return pd.Group;
        }

        void IGroupManager.SetPermGroup(Player p, string group, bool global, string info)
        {
            if (p == null)
                return;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            // first set it for the current session
            pd.Group = group;

            // now set it permanently
            if (global)
            {
                _configManager.SetStr(_staffConfHandle, Constants.AG_GLOBAL, p.Name, group, info, true);
                pd.Source = CapSource.Global;
            }
            else if (p.Arena != null)
            {
                _configManager.SetStr(_staffConfHandle, p.Arena.BaseName, p.Name, group, info, true);
                pd.Source = CapSource.Arena;
            }
        }

        void IGroupManager.SetTempGroup(Player p, string group)
        {
            if (p == null)
                return;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            if (string.IsNullOrEmpty(group))
            {
                pd.Group = group;
                pd.Source = CapSource.Temp;
            }
        }

        void IGroupManager.RemoveGroup(Player p, string info)
        {
            if (p == null)
                return;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            // in all cases, set current group to default
            pd.Group = Group_Default;

            switch (pd.Source)
            {
                case CapSource.Default:
                    break; // player is in the default group already, nothing to do

                case CapSource.Global:
                    _configManager.SetStr(_staffConfHandle, Constants.AG_GLOBAL, p.Name, Group_Default, info, true);
                    break;

                case CapSource.Arena:
                    _configManager.SetStr(_staffConfHandle, p.Arena.BaseName, p.Name, Group_Default, info, true);
                    break;
#if CFG_USE_ARENA_STAFF_LIST
                case CapSource.ArenaList:
                    _configManager.SetStr(p.Arena.Cfg, "Staff", p.Name, Group_Default, info);
                    break;
#endif
                case CapSource.Temp:
                    break;
            }
        }

        bool IGroupManager.CheckGroupPassword(string group, string pw)
        {
            string correctPw = _configManager.GetStr(_staffConfHandle, "GroupPasswords", group);
            
            if (string.IsNullOrEmpty(correctPw))
                return false;

            return string.Compare(correctPw, pw, false) == 0;
        }

        #endregion

        private void playerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            switch (action)
            {
                case PlayerAction.PreEnterArena:
                    updateGroup(p, pd, arena, true);
                    break;

                case PlayerAction.Connect:
                    updateGroup(p, pd, null, true);
                    break;

                case PlayerAction.Disconnect:
                case PlayerAction.LeaveArena:
                    pd.Group = Group_None;
                    break;
            }   
        }

        private void newPlayer(Player p, bool isNew)
        {
            if (p == null)
                return;

            if (isNew)
            {
                PlayerData pd = p[_pdkey] as PlayerData;
                if (pd == null)
                    return;

                pd.Group = Group_None;
            }
        }

        private void updateGroup(Player p, PlayerData pd, Arena arena, bool log)
        {
            if (p == null || pd == null)
                return;

            if (!p.Flags.Authenticated)
            {
                // if the player hasn't been authenticated against either the
                // biller or password file, don't assign groups based on name.
                pd.Group = Group_Default;
                pd.Source = CapSource.Default;
                return;
            }

            string g;
            if (arena != null && !string.IsNullOrEmpty(g = _configManager.GetStr(_staffConfHandle, arena.BaseName, p.Name)))
            {
                pd.Group = g;
                pd.Source = CapSource.Arena;

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
                pd.Source = CapSource.Global;
                if (log)
                    _logManager.LogP(LogLevel.Drivel, nameof(CapabilityManager), p, "assigned to group '{0}' (global)", pd.Group);
            }
            else
            {
                pd.Group = Group_Default;
                pd.Source = CapSource.Default;
            }
        }
    }
}
