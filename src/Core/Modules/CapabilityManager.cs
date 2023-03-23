using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
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
        private InterfaceRegistrationToken<ICapabilityManager> _iCapabilityManagerToken;
        private InterfaceRegistrationToken<IGroupManager> _iGroupManagerToken;

        private PlayerDataKey<PlayerData> _pdkey;
        private readonly PlayerDataPooledObjectPolicy _playerDataPooledObjectPolicy = new();
        private readonly ObjectPool<PlayerData> _playerDataPool;

        private ConfigHandle _groupDefConfHandle;
        private ConfigHandle _staffConfHandle;

        private const string Group_Default = "default";
        private const string Group_None = "none";

        private Trie<string> _groupNameCache = new(false)
        {
            { Group_Default, Group_Default },
            { Group_None, Group_None }
        };

        public CapabilityManager()
        {
            _playerDataPool = new DefaultObjectPool<PlayerData>(_playerDataPooledObjectPolicy);
        }

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

            _pdkey = _playerData.AllocatePlayerData(_playerDataPooledObjectPolicy); // separate pool from ours, but it's ok

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
            if (broker.UnregisterInterface(ref _iCapabilityManagerToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iGroupManagerToken) != 0)
                return false;

            _configManager.CloseConfigFile(_groupDefConfHandle);
            _configManager.CloseConfigFile(_staffConfHandle);

            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);
            NewPlayerCallback.Unregister(_broker, Callback_NewPlayer);

            _playerData.FreePlayerData(ref _pdkey);

            return true;
        }

        #endregion

        #region ICapabilityManager Members

        bool ICapabilityManager.HasCapability(Player player, ReadOnlySpan<char> capability)
        {
            if (player == null)
                return false;

            if (!player.TryGetExtraData(_pdkey, out PlayerData pd))
                return false;

            return _configManager.GetStr(_groupDefConfHandle, pd.Group, capability) != null;
        }

        bool ICapabilityManager.HasCapability(ReadOnlySpan<char> name, ReadOnlySpan<char> capability)
        {
            string group = _configManager.GetStr(_staffConfHandle, Constants.ArenaGroup_Global, name);
            if (string.IsNullOrEmpty(group))
                group = Group_Default;

            return _configManager.GetStr(_groupDefConfHandle, group, capability) != null;
        }

        bool ICapabilityManager.HasCapability(Player player, Arena arena, ReadOnlySpan<char> capability)
        {
            if (player == null || arena == null)
                return false;

            PlayerData tempPlayerData = _playerDataPool.Get();
            try
            {
                UpdateGroup(player, tempPlayerData, arena, false);
                return _configManager.GetStr(_groupDefConfHandle, tempPlayerData.Group, capability) != null;
            }
            finally
            {
                _playerDataPool.Return(tempPlayerData);
            }
        }

        bool ICapabilityManager.HigherThan(Player a, Player b)
        {
            if (a == null || b == null)
                return false;

            if (!b.TryGetExtraData(_pdkey, out PlayerData bPlayerData))
                return false;

            const string prefix = "higher_than_";
            Span<char> capability = stackalloc char[prefix.Length + bPlayerData.Group.Length];
            if (!capability.TryWrite($"{prefix}{bPlayerData.Group}", out _))
                return false;

            return ((ICapabilityManager)this).HasCapability(a, capability);
        }

        #endregion

        #region IGroupManager Members

        string IGroupManager.GetGroup(Player player)
        {
            if (player == null)
                return null;

            if (!player.TryGetExtraData(_pdkey, out PlayerData playerData))
                return null;

            return playerData.Group;
        }

        void IGroupManager.SetPermGroup(Player player, ReadOnlySpan<char> group, bool global, string comment)
        {
            if (player == null)
                return;

            if (!player.TryGetExtraData(_pdkey, out PlayerData playerData))
                return;

            // first set it for the current session
            playerData.Group = GetOrAddCachedGroupName(group);

            // now set it permanently
            if (global)
            {
                _configManager.SetStr(_staffConfHandle, Constants.ArenaGroup_Global, player.Name, playerData.Group, comment, true);
                playerData.Source = GroupSource.Global;
            }
            else if (player.Arena != null)
            {
                _configManager.SetStr(_staffConfHandle, player.Arena.BaseName, player.Name, playerData.Group, comment, true);
                playerData.Source = GroupSource.Arena;
            }
        }

        void IGroupManager.SetTempGroup(Player player, ReadOnlySpan<char> group)
        {
            if (player == null)
                return;

            if (group.IsWhiteSpace())
                return;

            if (!player.TryGetExtraData(_pdkey, out PlayerData playerData))
                return;

            playerData.Group = GetOrAddCachedGroupName(group);
            playerData.Source = GroupSource.Temp;
        }

        void IGroupManager.RemoveGroup(Player player, string comment)
        {
            if (player == null)
                return;

            if (!player.TryGetExtraData(_pdkey, out PlayerData playerData))
                return;

            // in all cases, set current group to default
            playerData.Group = Group_Default;

            switch (playerData.Source)
            {
                case GroupSource.Default:
                    break; // player is in the default group already, nothing to do

                case GroupSource.Global:
                    _configManager.SetStr(_staffConfHandle, Constants.ArenaGroup_Global, player.Name, Group_Default, comment, true);
                    break;

                case GroupSource.Arena:
                    _configManager.SetStr(_staffConfHandle, player.Arena.BaseName, player.Name, Group_Default, comment, true);
                    break;
#if CFG_USE_ARENA_STAFF_LIST
                case GroupSource.ArenaList:
                    _configManager.SetStr(player.Arena.Cfg, "Staff", player.Name, Group_Default, comment, true);
                    break;
#endif
                case GroupSource.Temp:
                    break;
            }
        }

        bool IGroupManager.CheckGroupPassword(ReadOnlySpan<char> group, ReadOnlySpan<char> pw)
        {
            string correctPw = _configManager.GetStr(_staffConfHandle, "GroupPasswords", group);

            if (string.IsNullOrWhiteSpace(correctPw))
                return false;

            return pw.Equals(correctPw, StringComparison.Ordinal);
        }

        #endregion

        #region Callbacks

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (player == null)
                return;

            if (!player.TryGetExtraData(_pdkey, out PlayerData pd))
                return;

            switch (action)
            {
                case PlayerAction.PreEnterArena:
                    UpdateGroup(player, pd, arena, true);
                    break;

                case PlayerAction.Connect:
                    UpdateGroup(player, pd, null, true);
                    break;

                case PlayerAction.Disconnect:
                case PlayerAction.LeaveArena:
                    pd.Group = Group_None;
                    break;
            }
        }

        private void Callback_NewPlayer(Player player, bool isNew)
        {
            if (player == null)
                return;

            if (isNew)
            {
                if (!player.TryGetExtraData(_pdkey, out PlayerData pd))
                    return;

                pd.Group = Group_None;
            }
        }

        #endregion

        private void UpdateGroup(Player player, PlayerData playerData, Arena arena, bool log)
        {
            if (player == null || playerData == null)
                return;

            if (!player.Flags.Authenticated)
            {
                // if the player hasn't been authenticated against either the
                // biller or password file, don't assign groups based on name.
                playerData.Group = Group_Default;
                playerData.Source = GroupSource.Default;
                return;
            }

            string group;
            if (arena != null && !string.IsNullOrEmpty(group = _configManager.GetStr(_staffConfHandle, arena.BaseName, player.Name)))
            {
                playerData.Group = GetOrAddCachedGroupName(group);
                playerData.Source = GroupSource.Arena;

                if (log)
                    _logManager.LogP(LogLevel.Drivel, nameof(CapabilityManager), player, $"Assigned to group '{playerData.Group}' (arena).");
            }
#if CFG_USE_ARENA_STAFF_LIST
            else if (arena != null && arena.Cfg != null && !string.IsNullOrEmpty(group = _configManager.GetStr(arena.Cfg, "Staff", player.Name)))
            {
                playerData.Group = GetOrAddCachedGroupName(group);
                playerData.Source = GroupSource.ArenaList;

                if (log)
                    _logManager.LogP(LogLevel.Drivel, nameof(CapabilityManager), player, $"Assigned to group '{playerData.Group}' (arenaconf)");
            }
#endif
            else if (!string.IsNullOrEmpty(group = _configManager.GetStr(_staffConfHandle, Constants.ArenaGroup_Global, player.Name)))
            {
                // only global groups available for now
                playerData.Group = GetOrAddCachedGroupName(group);
                playerData.Source = GroupSource.Global;

                if (log)
                    _logManager.LogP(LogLevel.Drivel, nameof(CapabilityManager), player, $"Assigned to group '{playerData.Group}' (global).");
            }
            else
            {
                playerData.Group = Group_Default;
                playerData.Source = GroupSource.Default;
            }
        }

        private string GetOrAddCachedGroupName(ReadOnlySpan<char> group)
        {
            if (_groupNameCache.TryGetValue(group, out string groupStr))
                return groupStr;

            // Add it to the cache.
            groupStr = group.ToString();
            _groupNameCache.Add(group, groupStr);
            return groupStr;
        }

        #region Helper Types

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

        private class PlayerDataPooledObjectPolicy : IPooledObjectPolicy<PlayerData>
        {
            public PlayerData Create()
            {
                return new PlayerData();
            }

            public bool Return(PlayerData obj)
            {
                if (obj is null)
                    return false;

                obj.Group = Group_Default;
                obj.Source = GroupSource.Default;
                return true;
            }
        }

        #endregion
    }
}
