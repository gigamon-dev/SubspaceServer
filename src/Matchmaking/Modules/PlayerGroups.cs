using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Utilities;
using System.Collections.ObjectModel;
using System.Text;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that provides functionality for players to form groups with other players.
    /// Groups can be used along with the <see cref="MatchmakingQueues"/> module to form a premade team.
    /// </summary>
    [ModuleInfo($"""
        Functionality for players to form groups with other players.
        Designed to be used with the {nameof(MatchmakingQueues)} module,
        but functionality is separate and could find other uses.
        """)]
    public class PlayerGroups : IModule, IPlayerGroups
    {
        private ComponentBroker _broker;

        // required dependencies
        private IChat _chat;
        private ICapabilityManager _capabilityManager;
        private ICommandManager _commandManager;
        private ILogManager _logManager;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;

        // optional dependencies
        private IHelp _help;

        private InterfaceRegistrationToken<IPlayerGroups> _iPlayerGroupsToken;

        private PlayerDataKey<PlayerData> _pdKey;

        private readonly HashSet<PlayerGroup> _groups = new(128);
        private readonly ObjectPool<PlayerGroup> _playerGroupPool = new NonTransientObjectPool<PlayerGroup>(new PlayerGroupPooledObjectPolicy());

        private const string GroupCommandName = "group";

        #region Module members

        public bool Load(
            ComponentBroker broker,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _help = broker.GetInterface<IHelp>();

            _pdKey = _playerData.AllocatePlayerData(new PlayerDataPooledObjectPolicy());

            PlayerActionCallback.Register(broker, Callback_PlayerAction);

            _commandManager.AddCommand(GroupCommandName, Command_group);

            _iPlayerGroupsToken = broker.RegisterInterface<IPlayerGroups>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iPlayerGroupsToken) != 0)
                return false;

            _commandManager.RemoveCommand(GroupCommandName, Command_group);

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);

            _playerData.FreePlayerData(ref _pdKey);

            broker.ReleaseInterface(ref _help);

            return true;
        }

        #endregion

        #region IPlayerGroups

        IPlayerGroup IPlayerGroups.GetGroup(Player player)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return null;

            return playerData.Group;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.Disconnect)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                    return;

                if (pd.Group != null)
                {
                    // The player is in a group. Remove the player from the group.
                    RemoveMember(pd.Group, player, PlayerGroupMemberRemovedReason.Disconnect);
                }

                // Remove any pending groups invites.
                while (pd.PendingGroups.Count > 0)
                {
                    PlayerGroup group = pd.PendingGroups.First(); // TODO: Do this without using LINQ, possible allocation here.
                    RemovePending(group, player, PlayerGroupPendingRemovedReason.Disconnect);
                }
            }
        }

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "<none> | [invite <player> | deinvite <player> | accept <player> | decline <player> | leave | kick <player> | leader <player> | disband]",
            Description =
            "Commands for managing player groups.\n" +
            "  no sub-command (e.g. ?group or /?group) - prints group information.\n" +
            "  invite - invites a player to your group ^\n" +
            "  deinvite - cancels a pending invite ^\n" +
            "  accept - accepts an invite\n" +
            "  decline - declines an invite\n" +
            "  leave - leaves the current group\n" +
            "  kick - kicks a member of the group ^\n" +
            "  leader - makes the chosen group member the leader of the group ^\n" +
            "  disband - disbands the group ^\n" +
            "^ must be the group leader to use this command\n" +
            "For sub-commands that take a <player>, the command can be sent privately to that player (e.g. /?group invite), rather than having to type the player's name.")]
        private void Command_group(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            if (parameters.IsWhiteSpace())
            {
                if (!target.TryGetPlayerTarget(out Player targetPlayer))
                    targetPlayer = player;

                if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData targetPlayerData))
                    return;

                PlayerGroup group = targetPlayerData.Group;

                if (group == null)
                {
                    if (targetPlayer == player)
                        _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    else
                        _chat.SendMessage(player, $"{GroupCommandName}: {targetPlayer.Name} is not in a group.");

                    if (targetPlayer == player
                        || _capabilityManager.HasCapability(player, "seeplayergroupdetails"))
                    {
                        if (targetPlayerData.PendingGroups.Count > 0)
                        {
                            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                            try
                            {
                                foreach (PlayerGroup pendingGroup in targetPlayerData.PendingGroups)
                                {
                                    if (sb.Length > 0)
                                        sb.Append(", ");

                                    sb.Append(pendingGroup.Leader.Name);
                                }

                                _chat.SendMessage(player, $"{GroupCommandName}: Pending {(targetPlayerData.PendingGroups.Count > 1 ? "invites" : "invite")} from: {sb}.");
                            }
                            finally
                            {
                                _objectPoolManager.StringBuilderPool.Return(sb);
                            }
                        }
                    }

                    return;
                }

                if (group == playerData.Group
                    || _capabilityManager.HasCapability(player, "seeplayergroupdetails"))
                {
                    if (targetPlayer == player)
                        _chat.SendMessage(player, $"{GroupCommandName}: Your group:");
                    else
                        _chat.SendMessage(player, $"{GroupCommandName}: {targetPlayer.Name}'s group:");

                    // Print detailed info about the group.                
                    PrintDetailedGroupInfo(player, group);
                }
                else
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: {targetPlayer.Name} is in a group.");
                }

                return;
            }

            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token = remaining.GetToken(' ', out remaining);
            if (MemoryExtensions.Equals(token, "invite", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup group = playerData.Group;
                if (group != null)
                {
                    if (group.Leader != player)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: Only the group leader, {playerData.Group.Leader.Name}, can invite a player.");
                        return;
                    }

                    // Ask advisors if inviting is allowed.
                    // E.g., a matchmaking advisor may not allow inviting while searching for a match.
                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                    try
                    {
                        var advisors = _broker.GetAdvisors<IPlayerGroupAdvisor>();
                        foreach (IPlayerGroupAdvisor advisor in advisors)
                        {
                            if (!advisor.AllowSendInvite(group, sb))
                            {
                                if (sb.Length > 0)
                                {
                                    _chat.SendMessage(player, $"{GroupCommandName}: {sb}");
                                    return;
                                }
                            }
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }
                }

                if (!target.TryGetPlayerTarget(out Player targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                {
                    targetPlayer = _playerData.FindPlayer(remaining);
                    if (targetPlayer == null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                        return;
                    }
                }

                if (targetPlayer == null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You must specify who to invite.");
                    return;
                }

                if (!targetPlayer.IsStandard)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: {targetPlayer.Name} cannot be invited (non-playable client).");
                    return;
                }

                if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData targetPlayerData))
                    return;

                if (targetPlayerData.Group != null)
                {
                    _chat.SendMessage(
                        player,
                        $"{GroupCommandName}: {targetPlayer.Name} is already in {(targetPlayerData.Group == playerData.Group ? "your" : "another")} group.");

                    return;
                }

                //if (targetPlayerData.PendingGroups.Count > 10) // TODO: max pending invites?
                //{
                //    _chat.SendMessage(
                //        player,
                //        $"{GroupCommandName}: {targetPlayer.Name} can't be invited. The player has too many pending invites .");
                //}

                if (group == null)
                {
                    // Decline all pending invites.
                    while (playerData.PendingGroups.Count > 0)
                    {
                        RemovePending(playerData.PendingGroups.First(), player, PlayerGroupPendingRemovedReason.Decline);
                    }

                    // Create a group.
                    group = playerData.Group = _playerGroupPool.Get();
                    group.Leader = player;
                    group.Members.Add(player);
                    _groups.Add(group);
                }

                group.PendingMembers.Add(targetPlayer);
                targetPlayerData.PendingGroups.Add(group);

                _chat.SendMessage(targetPlayer, $"{player.Name} has invited you to a group. To accept: ?group accept {player.Name}. To decline: ?group decline {player.Name}");
                _chat.SendMessage(player, $"{GroupCommandName}: {targetPlayer.Name} has been invited. To cancel the invite, use: ?{GroupCommandName} uninvite {targetPlayer.Name}");
            }
            else if (MemoryExtensions.Equals(token, "uninvite", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup group = playerData.Group;
                if (group == null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    return;
                }

                if (group.Leader != player)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not the group leader.");
                    return;
                }

                if (!target.TryGetPlayerTarget(out Player targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                {
                    targetPlayer = _playerData.FindPlayer(remaining);
                    if (targetPlayer == null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                        return;
                    }
                }

                if (targetPlayer == null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You must specify whose invite to cancel.");
                    return;
                }

                if (!RemovePending(group, targetPlayer, PlayerGroupPendingRemovedReason.InviterCancel))
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: There is no pending invite to {targetPlayer.Name}.");
                    return;
                }
            }
            else if (MemoryExtensions.Equals(token, "accept", StringComparison.OrdinalIgnoreCase))
            {
                if (playerData.Group != null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are already in a group.");
                    return;
                }

                // Ask advisors if accepting an invite is allowed.
                // E.g., a matchmaking advisor may not allow a player to join a group while searching for a match.
                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                try
                {
                    var advisors = _broker.GetAdvisors<IPlayerGroupAdvisor>();
                    foreach (IPlayerGroupAdvisor advisor in advisors)
                    {
                        if (!advisor.AllowAcceptInvite(player, sb))
                        {
                            if (sb.Length > 0)
                            {
                                _chat.SendMessage(player, $"{GroupCommandName}: {sb}");
                                return;
                            }
                        }
                    }
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }

                PlayerGroup group;
                if (playerData.PendingGroups.Count <= 0)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You do not have any pending group invites.");
                    return;
                }
                else if (playerData.PendingGroups.Count == 1)
                { 
                    group = playerData.PendingGroups.First();
                }
                else
                {
                    if (!target.TryGetPlayerTarget(out Player targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                    {
                        targetPlayer = _playerData.FindPlayer(remaining);
                        if (targetPlayer == null)
                        {
                            _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                            return;
                        }
                    }

                    if (targetPlayer == null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: You have multiple invites and therefore need to specify which one you want to accept.");
                        return;
                    }

                    if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData targetPlayerData))
                        return;

                    group = targetPlayerData.Group;
                    if (group == null || !playerData.PendingGroups.Contains(group))
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: You do not have a pending invite from {targetPlayer.Name}.");
                        return;
                    }
                }

                group.AcceptInvite(player);
                playerData.Group = group;
                playerData.PendingGroups.Remove(group);

                // Decline all other invites.
                while (playerData.PendingGroups.Count > 0)
                {
                    RemovePending(playerData.PendingGroups.First(), player, PlayerGroupPendingRemovedReason.Decline);
                }

                // Message team members that the player joined the group.
                foreach (Player member in group.Members)
                {
                    if (member != player)
                    {
                        _chat.SendMessage(member, $"{GroupCommandName}: {player.Name} has joined the group.");
                    }
                }

                // Message the player about the group that was joined.
                _chat.SendMessage(player, $"{GroupCommandName}: Joined group:");
                PrintDetailedGroupInfo(player, group);
            }
            else if (MemoryExtensions.Equals(token, "decline", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup group;

                if (playerData.PendingGroups.Count <= 0)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You do not have any pending group invites.");
                    return;
                }
                else if (playerData.PendingGroups.Count == 1)
                {
                    group = playerData.PendingGroups.First();
                }
                else
                {
                    if (!target.TryGetPlayerTarget(out Player targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                    {
                        targetPlayer = _playerData.FindPlayer(remaining);
                        if (targetPlayer == null)
                        {
                            _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                            return;
                        }
                    }

                    if (targetPlayer == null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: You have multiple invites and therefore need to specify whose invite to decline.");
                        return;
                    }

                    if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData targetPlayerData))
                        return;

                    group = targetPlayerData.Group;
                    if (group == null || !playerData.PendingGroups.Contains(group))
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: There is no pending invite from {targetPlayer.Name}.");
                        return;
                    }
                }

                if (!RemovePending(group, player, PlayerGroupPendingRemovedReason.Decline))
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: There is no pending invite from {group.Leader.Name}.");
                    return;
                }
            }
            else if (MemoryExtensions.Equals(token, "leave", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup group = playerData.Group;
                if (group == null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    return;
                }

                RemoveMember(group, player, PlayerGroupMemberRemovedReason.Leave);
            }
            else if (MemoryExtensions.Equals(token, "kick", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup group = playerData.Group;
                if (group == null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    return;
                }

                if (group.Leader != player)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not the group leader.");
                    return;
                }

                if (!target.TryGetPlayerTarget(out Player targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                {
                    targetPlayer = _playerData.FindPlayer(remaining);
                    if (targetPlayer == null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                        return;
                    }
                }

                if (targetPlayer == null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You must specify who to kick.");
                    return;
                }

                if (targetPlayer == player)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: To remove yourself from the group use: ?{GroupCommandName} leave");
                    return;
                }

                if (!RemoveMember(group, targetPlayer, PlayerGroupMemberRemovedReason.Kick))
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: {targetPlayer.Name} is not in the group and therefore cannot be kicked.");
                    return;
                }
            }
            else if (MemoryExtensions.Equals(token, "leader", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup group = playerData.Group;
                if (group == null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    return;
                }

                if (group.Leader != player)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not the group leader.");
                    return;
                }

                if (!target.TryGetPlayerTarget(out Player targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                {
                    targetPlayer = _playerData.FindPlayer(remaining);
                    if (targetPlayer == null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                        return;
                    }
                }

                if (targetPlayer == null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You must specify which team member to make leader.");
                    return;
                }

                int index = group.Members.IndexOf(targetPlayer);
                if (index == -1)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: {targetPlayer.Name} is not in the group and therefore cannot be made the leader.");
                    return;
                }

                // Move the player to the beginning of the list.
                while (index > 0)
                {
                    group.Members[index] = group.Members[index - 1];
                    index--;
                }
                group.Members[0] = targetPlayer;

                group.Leader = targetPlayer;

                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    set.UnionWith(group.Members);
                    _chat.SendSetMessage(set, $"{GroupCommandName}: {group.Leader.Name} is now the group leader.");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }
            }
            else if (MemoryExtensions.Equals(token, "disband", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup group = playerData.Group;
                if (group == null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    return;
                }

                if (group.Leader != player)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not the group leader.");
                    return;
                }

                Disband(group);
            }
            else
            {
                if (_help != null)
                    _chat.SendMessage(player, $"{GroupCommandName}: Invalid input. For instructions see: ?{_help.HelpCommand} {GroupCommandName}");
                else
                    _chat.SendMessage(player, $"{GroupCommandName}: Invalid input.");
            }

            void PrintDetailedGroupInfo(Player player, PlayerGroup group)
            {
                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    foreach (Player member in group.Members)
                    {
                        if (sb.Length > 0)
                            sb.Append(", ");

                        if (member == group.Leader)
                            sb.Append("[leader] ");

                        sb.Append(member.Name);
                    }

                    foreach (Player invitee in group.PendingMembers)
                    {
                        if (sb.Length > 0)
                            sb.Append(", ");

                        sb.Append("[invited] ");
                        sb.Append(invitee.Name);
                    }

                    _chat.SendWrappedText(player, sb);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }
        }

        private bool RemovePending(PlayerGroup group, Player player, PlayerGroupPendingRemovedReason reason)
        {
            if (group == null || player == null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return false;

            if (!group.RemovePending(player))
                return false;

            playerData.PendingGroups.Remove(group);

            // Message the invitee.
            if (reason == PlayerGroupPendingRemovedReason.Decline)
            {
                _chat.SendMessage(player, $"{GroupCommandName}: You have declined the group invite from {group.Leader.Name}.");
            }
            else if (reason != PlayerGroupPendingRemovedReason.Disconnect)
            {
                _chat.SendMessage(player, $"{GroupCommandName}: The group invite from {group.Leader.Name} was canceled.");
            }

            // Message the inviter.
            if (reason == PlayerGroupPendingRemovedReason.Decline)
            {
                _chat.SendMessage(group.Leader, $"{GroupCommandName}: {player.Name} declined your group invite.");
            }
            else if (reason != PlayerGroupPendingRemovedReason.InviterDisconnect && reason != PlayerGroupPendingRemovedReason.Disband)
            {
                _chat.SendMessage(group.Leader, $"{GroupCommandName}: The group invite to {player.Name} was canceled.");
            }

            PlayerGroupPendingRemovedCallback.Fire(_broker, group, player, reason);

            // Check if the group should be disbanded.
            if (reason != PlayerGroupPendingRemovedReason.Disband)
            {
                DisbandIfEmpty(group);
            }

            return true;
        }

        private bool RemoveMember(PlayerGroup group, Player player, PlayerGroupMemberRemovedReason reason)
        {
            if (group == null || player == null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return false;

            bool wasLeader = player == group.Leader;

            // Remove any pending invites.
            if (wasLeader && group.PendingMembers.Count > 0)
            {
                PlayerGroupPendingRemovedReason pendingRemovedReason = reason switch {
                    PlayerGroupMemberRemovedReason.Disconnect => PlayerGroupPendingRemovedReason.InviterDisconnect,
                    PlayerGroupMemberRemovedReason.Disband => PlayerGroupPendingRemovedReason.Disband,
                    PlayerGroupMemberRemovedReason.Leave or PlayerGroupMemberRemovedReason.Kick or _ => PlayerGroupPendingRemovedReason.InviterCancel,
                };

                RemoveAllPending(group, pendingRemovedReason);
            }

            // Try to do the removal. Note: the leader may change.
            if (!group.RemoveMember(player))
                return false; // The player was not a member of the team.

            playerData.Group = null;

            // Message to the player that was removed.
            switch (reason)
            {
                case PlayerGroupMemberRemovedReason.Disconnect:
                    // no message, the player is already gone
                    break;

                case PlayerGroupMemberRemovedReason.Leave:
                    _chat.SendMessage(player, $"{GroupCommandName}: You have left the group.");
                    break;

                case PlayerGroupMemberRemovedReason.Kick:
                    _chat.SendMessage(player, $"{GroupCommandName}: You were kicked from the group.");
                    break;

                case PlayerGroupMemberRemovedReason.Disband:
                    _chat.SendMessage(player, $"{GroupCommandName}: Your group disbanded.");
                    break;
            }

            // Message to the team members.
            if (group.Members.Count > 0
                && (reason == PlayerGroupMemberRemovedReason.Disconnect || reason == PlayerGroupMemberRemovedReason.Leave || reason == PlayerGroupMemberRemovedReason.Kick))
            {
                HashSet<Player> remainingMembers = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    remainingMembers.UnionWith(group.Members);

                    if (reason == PlayerGroupMemberRemovedReason.Disconnect)
                    {
                        _chat.SendSetMessage(remainingMembers, $"{GroupCommandName}: {player.Name} left the group. (Disconnected)");
                    }
                    else if (reason == PlayerGroupMemberRemovedReason.Leave)
                    {
                        _chat.SendSetMessage(remainingMembers, $"{GroupCommandName}: {player.Name} left the group.");
                    }
                    else if (reason == PlayerGroupMemberRemovedReason.Kick)
                    {
                        _chat.SendSetMessage(remainingMembers, $"{GroupCommandName}: {player.Name} was kicked from the group.");
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(remainingMembers);
                }
            }

            // Fire the callback so that other modules know that a member was removed from the group.
            PlayerGroupMemberRemovedCallback.Fire(_broker, group, player, reason);

            // Check if the team should be disbanded.
            bool isDisbanded = reason == PlayerGroupMemberRemovedReason.Disband;
            if (!isDisbanded)
            {
                isDisbanded = DisbandIfEmpty(group);
            }

            // Check if the team needs to be assigned a new leader.
            if (!isDisbanded && wasLeader && group.Leader != null)
            {
                // Notify the team member that there is a new leader.
                HashSet<Player> members = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    members.UnionWith(group.Members);

                    _chat.SendSetMessage(members, $"{GroupCommandName}: {group.Leader.Name} is now the group leader.");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(members);
                }
            }

            return true;
        }

        private void RemoveAllPending(PlayerGroup group, PlayerGroupPendingRemovedReason reason)
        {
            if (group == null)
                return;

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                set.UnionWith(group.PendingMembers);

                foreach (Player player in set)
                {
                    RemovePending(group, player, reason);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        private void Disband(PlayerGroup group)
        {
            if (group == null)
                return;

            RemoveAllPending(group, PlayerGroupPendingRemovedReason.Disband);

            // Remove the remaining members.
            while (group.Members.Count > 0)
            {
                Player player = group.Members[^1];
                RemoveMember(group, player, PlayerGroupMemberRemovedReason.Disband);
            }

            _groups.Remove(group);
            _playerGroupPool.Return(group);

            PlayerGroupDisbandedCallback.Fire(_broker, group);
        }

        /// <summary>
        /// Disbands a group if it is empty.
        /// </summary>
        /// <param name="group"></param>
        /// <returns><see langword="true"/> if the group was disbanded. Otherwise, <see langword="false"/>.</returns>
        private bool DisbandIfEmpty(PlayerGroup group)
        {
            if (group == null)
                return false;

            if (group.Members.Count > 0
                && (group.Members.Count + group.PendingMembers.Count) > 1)
            {
                return false;
            }

            Disband(group);
            return true;
        }

        #endregion

        private class PlayerGroup : IPlayerGroup
        {
            public Player Leader;
            public readonly List<Player> Members = new(10); // ordered such that if the leader leaves, the first player will become leader
            private readonly ReadOnlyCollection<Player> _readOnlyMembers;
            public readonly HashSet<Player> PendingMembers = new(10);

            public PlayerGroup()
            {
                _readOnlyMembers = Members.AsReadOnly();
            }

            #region IPlayerGroup

            Player IPlayerGroup.Leader => Leader;

            ReadOnlyCollection<Player> IPlayerGroup.Members => _readOnlyMembers;

            IReadOnlySet<Player> IPlayerGroup.PendingMembers => PendingMembers;

            #endregion

            public bool AcceptInvite(Player player)
            {
                if (!RemovePending(player))
                    return false;

                Members.Add(player);
                return true;
            }

            public bool RemoveMember(Player player)
            {
                if (!Members.Remove(player))
                    return false;

                if (Leader == player)
                {
                    Leader = Members.Count > 0 ? Members[0] : null;
                }

                return true;
            }

            public bool RemovePending(Player player)
            {
                return PendingMembers.Remove(player);
            }

            public void Reset()
            {
                Leader = null;
                Members.Clear();
                PendingMembers.Clear();
            }
        }

        private class PlayerData
        {
            /// <summary>
            /// The player's current group. <see langword="null"/> when not in a group.
            /// </summary>
            public PlayerGroup Group;

            /// <summary>
            /// Groups that the player has a pending invite to.
            /// </summary>
            public readonly HashSet<PlayerGroup> PendingGroups = new();
        }

        private class PlayerDataPooledObjectPolicy : IPooledObjectPolicy<PlayerData>
        {
            public PlayerData Create()
            {
                return new PlayerData();
            }

            public bool Return(PlayerData obj)
            {
                if (obj == null)
                    return false;

                obj.Group = null;
                obj.PendingGroups.Clear();

                return true;
            }
        }

        private class PlayerGroupPooledObjectPolicy : IPooledObjectPolicy<PlayerGroup>
        {
            public PlayerGroup Create()
            {
                return new PlayerGroup();
            }

            public bool Return(PlayerGroup obj)
            {
                if (obj == null)
                    return false;

                obj.Reset();

                return true;
            }
        }
    }
}
