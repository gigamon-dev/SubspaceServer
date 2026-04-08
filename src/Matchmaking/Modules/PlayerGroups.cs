using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Utilities.Collections;
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
    public sealed class PlayerGroups : IModule, IPlayerGroups
    {
        // required dependencies
        private readonly IComponentBroker _broker;
        private readonly IChat _chat;
        private readonly ICapabilityManager _capabilityManager;
        private readonly ICommandManager _commandManager;
        private readonly ILogManager _logManager;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;

        // optional dependencies
        private IHelp? _help;

        private InterfaceRegistrationToken<IPlayerGroups>? _iPlayerGroupsToken;

        private PlayerDataKey<PlayerData> _pdKey;

        private readonly HashSet<PlayerGroup> _groups = new(128);
        private readonly DefaultObjectPool<PlayerGroup> _playerGroupPool = new(new DefaultPooledObjectPolicy<PlayerGroup>(), Constants.TargetPlayerCount);

        private const string GroupCommandName = "group";
        private const string GroupAltCommandName = "gr";

        private static readonly Trie<string> _groupSubCommands = new(false)
        {
            { "invite", "invite" },
            { "uninvite", "uninvite" },
            { "accept", "accept" },
            { "decline", "decline" },
            { "leave", "leave" },
            { "kick", "kick" },
            { "leader", "leader" },
            { "disband", "disband" },
        };

        public PlayerGroups(
            IComponentBroker broker,
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
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _help = broker.GetInterface<IHelp>();

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            PlayerStartPlayingCallback.Register(broker, Callback_PlayerStartPlaying);

            _commandManager.AddCommand(GroupCommandName, Command_group);
            _commandManager.AddCommand(GroupAltCommandName, Command_group);

            _iPlayerGroupsToken = broker.RegisterInterface<IPlayerGroups>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iPlayerGroupsToken) != 0)
                return false;

            _commandManager.RemoveCommand(GroupCommandName, Command_group);
            _commandManager.RemoveCommand(GroupAltCommandName, Command_group);

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            PlayerStartPlayingCallback.Unregister(broker, Callback_PlayerStartPlaying);

            _playerData.FreePlayerData(ref _pdKey);

            broker.ReleaseInterface(ref _help);

            return true;
        }

        #endregion

        #region IPlayerGroups

        IPlayerGroup? IPlayerGroups.GetGroup(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return null;

            return playerData.Group;
        }

        #endregion

        #region Callbacks

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.Disconnect)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    return;

                if (pd.Group is not null)
                {
                    // The player is in a group. Remove the player from the group.
                    RemoveMember(pd.Group, player, PlayerGroupMemberRemovedReason.Disconnect);
                }

                // Remove any pending groups invites.
                RemovePendingInvites(player, pd, PlayerGroupPendingRemovedReason.Disconnect);
            }
        }

        private void Callback_PlayerStartPlaying(Player player)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            // The player started playing. Decline any pending invites.
            RemovePendingInvites(player, pd, PlayerGroupPendingRemovedReason.Decline);
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "<none> | [invite <player> | uninvite <player> | accept <player> | decline <player> | leave | kick <player> | leader <player> | disband]",
            Description = """
                Commands for managing player groups.
                  no sub-command (e.g. ?group or /?group) - prints group information.
                  invite   - invites a player to your group ^
                  uninvite - cancels a pending invite ^
                  accept   - accepts an invite
                  decline  - declines an invite
                  leave    - leaves the current group
                  kick     - kicks a member of the group ^
                  leader   - makes the chosen group member the leader of the group ^
                  disband  - disbands the group ^
                ^ must be the group leader to use this command
                For sub-commands that take a <player>, the command can be sent privately to that player 
                  E.g. /?group invite, rather than having to type the player's name.
                The full sub-command name does not need to be written. It will look for a command that starts with what is entered.
                  E.g. /?group i, rather than type invite.
                If more than one command matches (e.g. `?group d`), it will ask for clarification.
                """)]
        private void Command_group(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            if (parameters.IsWhiteSpace())
            {
                if (!target.TryGetPlayerTarget(out Player? targetPlayer))
                    targetPlayer = player;

                if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                    return;

                PlayerGroup? group = targetPlayerData.Group;

                if (group is null)
                {
                    if (targetPlayer == player)
                        _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    else
                        _chat.SendMessage(player, $"{GroupCommandName}: {targetPlayer.Name} is not in a group.");

                    if (targetPlayer == player
                        || _capabilityManager.HasCapability(player, CapabilityNames.SeePlayerGroupDeatils))
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

                                    sb.Append(pendingGroup.Leader!.Name);
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
                    || _capabilityManager.HasCapability(player, CapabilityNames.SeePlayerGroupDeatils))
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

            Span<Range> ranges = stackalloc Range[2];
            ReadOnlySpan<char> token;
            ReadOnlySpan<char> remaining;
            int numRanges = parameters.Split(ranges, ' ', StringSplitOptions.TrimEntries);
            if (numRanges == 1)
            {
                token = parameters[ranges[0]];
                remaining = [];
            }
            else if (numRanges == 2)
            {
                token = parameters[ranges[0]];
                remaining = parameters[ranges[1]];
            }
            else
            {
                return;
            }

            StringBuilder matchedCommands = _objectPoolManager.StringBuilderPool.Get();
            try
            {
                int matches = 0;
                string? lastMatch = null;
                foreach ((_, string subCommand) in _groupSubCommands.StartsWith(token))
                {
                    if (matchedCommands.Length > 0)
                        matchedCommands.Append(", ");

                    matchedCommands.Append(subCommand);
                    lastMatch = subCommand;
                    matches++;
                }

                if (matches == 0)
                {
                    if (_help is not null)
                        _chat.SendMessage(player, $"{GroupCommandName}: Invalid input. For instructions see: ?{_help.HelpCommand} {GroupCommandName}");
                    else
                        _chat.SendMessage(player, $"{GroupCommandName}: Invalid input.");

                    return;
                }
                else if (matches > 1)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: Please be more precise. Unable to determine which of the following sub-commands is desired:");
                    _chat.SendWrappedText(player, matchedCommands);
                    return;
                }
                else
                {
                    // 1 match only, process it
                    token = lastMatch;
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(matchedCommands);
            }
            

            if (token.Equals("invite", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup? group = playerData.Group;
                if (group is not null)
                {
                    if (group.Leader != player)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: Only the group leader, {group.Leader!.Name}, can invite a player.");
                        return;
                    }

                    // Ask advisors if inviting is allowed.
                    // E.g., a matchmaking advisor may not allow inviting while searching for a match.
                    StringBuilder message = _objectPoolManager.StringBuilderPool.Get();
                    try
                    {
                        if (!CanGroupSendInvite(_broker, group, message))
                        {
                            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                            try
                            {
                                sb.Append($"{GroupCommandName}: ");

                                if (message.Length > 0)
                                    sb.Append(message);
                                else
                                    sb.Append("Your group is currently not allowed to send an invite.");

                                _chat.SendMessage(player, sb);
                                return;
                            }
                            finally
                            {
                                _objectPoolManager.StringBuilderPool.Return(sb);
                            }
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(message);
                    }
                }
                else
                {
                    // Not in a group. This means a new one will need to be created.
                    // Ask advisors if creating a group is allowed.
                    StringBuilder message = _objectPoolManager.StringBuilderPool.Get();
                    try
                    {
                        if (!CanPlayerCreateGroup(_broker, player, message))
                        {
                            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                            try
                            {
                                sb.Append($"{GroupCommandName}: ");
                                
                                if (message.Length > 0)
                                    sb.Append(message);
                                else
                                    sb.Append("You currently cannot create a new group.");

                                _chat.SendMessage(player, sb);
                                return;
                            }
                            finally
                            {
                                _objectPoolManager.StringBuilderPool.Return(sb);
                            }
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(message);
                    }
                }

                if (!target.TryGetPlayerTarget(out Player? targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                {
                    targetPlayer = _playerData.FindPlayer(remaining);
                    if (targetPlayer is null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                        return;
                    }
                }

                if (targetPlayer is null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You must specify who to invite.");
                    return;
                }

                if (!targetPlayer.IsStandard)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: {targetPlayer.Name} cannot be invited (non-playable client).");
                    return;
                }

                if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                    return;

                if (targetPlayerData.Group is not null)
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

                {
                    // Ask advisors if the player can be invited.
                    StringBuilder message = _objectPoolManager.StringBuilderPool.Get();
                    try
                    {
                        if (!CanPlayerBeInvited(_broker, targetPlayer, message))
                        {
                            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                            try
                            {
                                sb.Append($"{GroupCommandName}: ");
                                
                                if (message.Length > 0)
                                    sb.Append(message);
                                else
                                    sb.Append($"Cannot invite {targetPlayer.Name}.");

                                _chat.SendMessage(player, sb);
                                return;
                            }
                            finally
                            {
                                _objectPoolManager.StringBuilderPool.Return(sb);
                            }
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(message);
                    }
                }

                if (group is null)
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

                    PlayerGroupCreatedCallback.Fire(_broker, group);
                    PlayerGroupMemberAddedCallback.Fire(_broker, group, player);
                }

                group.PendingMembers.Add(targetPlayer);
                targetPlayerData.PendingGroups.Add(group);

                _chat.SendMessage(targetPlayer, $"{player.Name} has invited you to a group. To accept: ?group accept {player.Name}. To decline: ?group decline {player.Name}");
                _chat.SendMessage(player, $"{GroupCommandName}: {targetPlayer.Name} has been invited. To cancel the invite, use: ?{GroupCommandName} uninvite {targetPlayer.Name}");
            }
            else if (token.Equals("uninvite", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup? group = playerData.Group;
                if (group is null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    return;
                }

                if (group.Leader != player)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not the group leader.");
                    return;
                }

                if (!target.TryGetPlayerTarget(out Player? targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                {
                    targetPlayer = _playerData.FindPlayer(remaining);
                    if (targetPlayer is null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                        return;
                    }
                }

                if (targetPlayer is null)
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
            else if (token.Equals("accept", StringComparison.OrdinalIgnoreCase))
            {
                if (playerData.Group is not null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are already in a group.");
                    return;
                }

                if (playerData.PendingGroups.Count <= 0)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You do not have any pending group invites.");
                    return;
                }

                // Ask advisors if accepting an invite is allowed.
                // E.g., a matchmaking advisor may not allow a player to join a group while searching for a match.
                StringBuilder message = _objectPoolManager.StringBuilderPool.Get();
                try
                {
                    if (!CanPlayerAcceptInvite(_broker, player, message))
                    {
                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                        try
                        {
                            sb.Append($"{GroupCommandName}: ");
                            
                            if (message.Length > 0)
                                sb.Append(message);
                            else
                                sb.Append($"You currently cannot accept an invite.");

                            _chat.SendMessage(player, sb);
                            return;
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                    }
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(message);
                }

                // Determine which group the player wants to join.
                PlayerGroup? group;
                if (playerData.PendingGroups.Count == 1)
                {
                    group = playerData.PendingGroups.First();
                }
                else
                {
                    if (!target.TryGetPlayerTarget(out Player? targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                    {
                        targetPlayer = _playerData.FindPlayer(remaining);
                        if (targetPlayer is null)
                        {
                            _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                            return;
                        }
                    }

                    if (targetPlayer is null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: You have multiple invites and therefore need to specify which one you want to accept.");
                        return;
                    }

                    if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                        return;

                    group = targetPlayerData.Group;
                    if (group is null || !playerData.PendingGroups.Contains(group))
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: You do not have a pending invite from {targetPlayer.Name}.");
                        return;
                    }
                }

                // Accept the invitation.
                group.AcceptInvite(player);
                playerData.Group = group;
                playerData.PendingGroups.Remove(group);

                PlayerGroupMemberAddedCallback.Fire(_broker, group, player);

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
            else if (token.Equals("decline", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup? group;

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
                    if (!target.TryGetPlayerTarget(out Player? targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                    {
                        targetPlayer = _playerData.FindPlayer(remaining);
                        if (targetPlayer is null)
                        {
                            _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                            return;
                        }
                    }

                    if (targetPlayer is null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: You have multiple invites and therefore need to specify whose invite to decline.");
                        return;
                    }

                    if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                        return;

                    group = targetPlayerData.Group;
                    if (group is null || !playerData.PendingGroups.Contains(group))
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: There is no pending invite from {targetPlayer.Name}.");
                        return;
                    }
                }

                if (!RemovePending(group, player, PlayerGroupPendingRemovedReason.Decline))
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: There is no pending invite from {group.Leader!.Name}.");
                    return;
                }
            }
            else if (token.Equals("leave", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup? group = playerData.Group;
                if (group is null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    return;
                }

                RemoveMember(group, player, PlayerGroupMemberRemovedReason.Leave);
            }
            else if (token.Equals("kick", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup? group = playerData.Group;
                if (group is null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    return;
                }

                if (group.Leader != player)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not the group leader.");
                    return;
                }

                if (!target.TryGetPlayerTarget(out Player? targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                {
                    targetPlayer = _playerData.FindPlayer(remaining);
                    if (targetPlayer is null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                        return;
                    }
                }

                if (targetPlayer is null)
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
            else if (token.Equals("leader", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup? group = playerData.Group;
                if (group is null)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not in a group.");
                    return;
                }

                if (group.Leader != player)
                {
                    _chat.SendMessage(player, $"{GroupCommandName}: You are not the group leader.");
                    return;
                }

                if (!target.TryGetPlayerTarget(out Player? targetPlayer) && !MemoryExtensions.IsWhiteSpace(remaining))
                {
                    targetPlayer = _playerData.FindPlayer(remaining);
                    if (targetPlayer is null)
                    {
                        _chat.SendMessage(player, $"{GroupCommandName}: Player '{remaining}' not found.");
                        return;
                    }
                }

                if (targetPlayer is null)
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
            else if (token.Equals("disband", StringComparison.OrdinalIgnoreCase))
            {
                PlayerGroup? group = playerData.Group;
                if (group is null)
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
                if (_help is not null)
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

            static bool CanPlayerCreateGroup(IComponentBroker broker, Player player, StringBuilder message)
            {
                var advisors = broker.GetAdvisors<IPlayerGroupAdvisor>();
                foreach (IPlayerGroupAdvisor advisor in advisors)
                {
                    if (!advisor.CanPlayerCreateGroup(player, message))
                    {
                        return false;
                    }
                }

                return true;
            }

            static bool CanGroupSendInvite(IComponentBroker broker, IPlayerGroup group, StringBuilder message)
            {
                var advisors = broker.GetAdvisors<IPlayerGroupAdvisor>();
                foreach (IPlayerGroupAdvisor advisor in advisors)
                {
                    if (!advisor.CanGroupSendInvite(group, message))
                    {
                        return false;
                    }
                }

                return true;
            }

            static bool CanPlayerBeInvited(IComponentBroker broker, Player player, StringBuilder message)
            {
                var advisors = broker.GetAdvisors<IPlayerGroupAdvisor>();
                foreach (IPlayerGroupAdvisor advisor in advisors)
                {
                    if (!advisor.CanPlayerBeInvited(player, message))
                    {
                        return false;
                    }
                }

                return true;
            }

            static bool CanPlayerAcceptInvite(IComponentBroker broker, Player player, StringBuilder message)
            {
                var advisors = broker.GetAdvisors<IPlayerGroupAdvisor>();
                foreach (IPlayerGroupAdvisor advisor in advisors)
                {
                    if (!advisor.CanPlayerAcceptInvite(player, message))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        #endregion

        /// <summary>
        /// Removes all of player's pending invites.
        /// </summary>
        private void RemovePendingInvites(Player player, PlayerData pd, PlayerGroupPendingRemovedReason reason)
        {
            while (pd.PendingGroups.Count > 0)
            {
                using var e = pd.PendingGroups.GetEnumerator();
                if (e.MoveNext())
                {
                    RemovePending(e.Current, player, reason);
                }
            }
        }

        /// <summary>
        /// Removes a specific group invite from a player.
        /// </summary>
        private bool RemovePending(PlayerGroup group, Player player, PlayerGroupPendingRemovedReason reason)
        {
            if (group is null || player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return false;

            if (!group.RemovePending(player))
                return false;

            playerData.PendingGroups.Remove(group);

            // Message the invitee.
            if (reason == PlayerGroupPendingRemovedReason.Decline)
            {
                _chat.SendMessage(player, $"{GroupCommandName}: You have declined the group invite from {group.Leader!.Name}.");
            }
            else if (reason != PlayerGroupPendingRemovedReason.Disconnect)
            {
                _chat.SendMessage(player, $"{GroupCommandName}: The group invite from {group.Leader!.Name} was canceled.");
            }

            // Message the inviter.
            if (reason == PlayerGroupPendingRemovedReason.Decline)
            {
                _chat.SendMessage(group.Leader!, $"{GroupCommandName}: {player.Name} declined your group invite.");
            }
            else if (reason != PlayerGroupPendingRemovedReason.InviterDisconnect && reason != PlayerGroupPendingRemovedReason.Disband)
            {
                _chat.SendMessage(group.Leader!, $"{GroupCommandName}: The group invite to {player.Name} was canceled.");
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
            if (group is null || player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return false;

            bool wasLeader = player == group.Leader;

            // Remove any pending invites.
            if (wasLeader && group.PendingMembers.Count > 0)
            {
                PlayerGroupPendingRemovedReason pendingRemovedReason = reason switch
                {
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
            if (!isDisbanded && wasLeader && group.Leader is not null)
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

        /// <summary>
        /// Removes all pending invites for a group.
        /// </summary>
        private void RemoveAllPending(PlayerGroup group, PlayerGroupPendingRemovedReason reason)
        {
            if (group is null)
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
            if (group is null)
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
            if (group is null)
                return false;

            if (group.Members.Count > 0
                && (group.Members.Count + group.PendingMembers.Count) > 1)
            {
                return false;
            }

            Disband(group);
            return true;
        }

        #region Helper types

        private class PlayerGroup : IPlayerGroup, IResettable
        {
            public Player? Leader;
            public readonly List<Player> Members = new(10); // ordered such that if the leader leaves, the first player will become leader
            private readonly ReadOnlyCollection<Player> _readOnlyMembers;
            public readonly HashSet<Player> PendingMembers = new(10);

            public PlayerGroup()
            {
                _readOnlyMembers = Members.AsReadOnly();
            }

            #region IPlayerGroup

            Player IPlayerGroup.Leader
            {
                get
                {
                    if (Leader is null)
                        throw new InvalidOperationException();

                    return Leader;
                }
            }

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

            bool IResettable.TryReset()
            {
                Leader = null;
                Members.Clear();
                PendingMembers.Clear();

                return true;
            }
        }

        private class PlayerData : IResettable
        {
            /// <summary>
            /// The player's current group. <see langword="null"/> when not in a group.
            /// </summary>
            public PlayerGroup? Group;

            /// <summary>
            /// Groups that the player has a pending invite to.
            /// </summary>
            public readonly HashSet<PlayerGroup> PendingGroups = [];

            bool IResettable.TryReset()
            {
                Group = null;
                PendingGroups.Clear();

                return true;
            }
        }

        #endregion
    }
}
