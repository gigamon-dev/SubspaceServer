using CommunityToolkit.HighPerformance;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.League;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that manages league functionality for starting up matches.
    /// Other modules that implement custom game modes can register themselves with this module through the <see cref="ILeagueManager"/> interface.
    /// When a match is to begin, this module will call the appropriate game mode module according to registration.
    /// </summary>
    public sealed class LeagueManager : IModule, ILeagueManager
    {
        private readonly IChat _chat;
        private readonly ICapabilityManager _capabilityManager;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly ILeagueAuthorization _leagueAuthorization;
        private readonly ILeagueRepository _leagueRepository;
        private readonly IMatchmakingQueues _matchmakingQueues;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;

        private InterfaceRegistrationToken<ILeagueManager>? _leagueManagerRegistrationToken;

        private const string StartLeagueMatchCommandName = "startleaguematch";
        private const string LeaguePermitCommandName = "leaguepermit";

        private readonly string[] LeagueHelpKeys = [
            nameof(TeamVersusMatch), 
            nameof(TeamVersusStats)
        ];

        private readonly Dictionary<long, ILeagueGameMode> _registeredGameTypes = [];

        private readonly ConcurrentDictionary<long, ILeagueMatch> _activeMatches = [];
        private readonly ReadOnlyDictionary<long, ILeagueMatch> _readOnlyActiveMatches;

        public LeagueManager(
            IChat chat,
            ICapabilityManager capabilityManager,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILeagueAuthorization leagueAuthorization,
            ILeagueRepository leagueRepository,
            IMatchmakingQueues matchmakingQueues,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _leagueAuthorization = leagueAuthorization ?? throw new ArgumentNullException(nameof(leagueAuthorization));
            _leagueRepository = leagueRepository ?? throw new ArgumentNullException(nameof(leagueRepository));
            _matchmakingQueues = matchmakingQueues ?? throw new ArgumentNullException(nameof(matchmakingQueues));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _readOnlyActiveMatches = _activeMatches.AsReadOnly();
        }

        #region Module members

        public bool Load(IComponentBroker broker)
        {
            LeagueMatchEndedCallback.Register(broker, Callback_LeagueMatchEnded);

            _commandManager.AddCommand("leaguehelp", Command_leaguehelp);
            _commandManager.AddCommand("roster", Command_roster);
            _commandManager.AddCommand("schedule", Command_schedule);
            _commandManager.AddCommand("standings", Command_standings);
            _commandManager.AddCommand("results", Command_results);
            _commandManager.AddCommand(StartLeagueMatchCommandName, Command_startleaguematch);
            _commandManager.AddCommand(LeaguePermitCommandName, Command_leaguepermit);

            // TODO: timer to periodically check for scheduled games and start them automatically when the time comes

            _leagueManagerRegistrationToken = broker.RegisterInterface<ILeagueManager>(this);
            return true;
        }

        public bool Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _leagueManagerRegistrationToken) != 0)
                return false;

            _commandManager.RemoveCommand("leaguehelp", Command_leaguehelp);
            _commandManager.RemoveCommand("roster", Command_roster);
            _commandManager.RemoveCommand("schedule", Command_schedule);
            _commandManager.RemoveCommand("standings", Command_standings);
            _commandManager.RemoveCommand("results", Command_results);
            _commandManager.RemoveCommand(StartLeagueMatchCommandName, Command_startleaguematch);
            _commandManager.RemoveCommand(LeaguePermitCommandName, Command_leaguepermit);

            LeagueMatchEndedCallback.Unregister(broker, Callback_LeagueMatchEnded);

            return true;
        }

        #endregion

        #region ILeagueManager

        bool ILeagueManager.Register(long gameTypeId, ILeagueGameMode gameMode)
        {
            return _registeredGameTypes.TryAdd(gameTypeId, gameMode);
        }

        bool ILeagueManager.Unregister(long gameTypeId, ILeagueGameMode gameMode)
        {
            if (!_registeredGameTypes.Remove(gameTypeId, out ILeagueGameMode? removedMode))
                return false;

            if (removedMode != gameMode)
            {
                _registeredGameTypes.Add(gameTypeId, removedMode);
                return false;
            }

            return true;
        }

        #endregion

        private void Callback_LeagueMatchEnded(ILeagueMatch leagueMatch)
        {
            _activeMatches.TryRemove(leagueMatch.SeasonGameId, out _);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "",
            Description = """
                Displays a basic overview of commands related to league functionality.
                """)]
        private void Command_leaguehelp(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            // TODO: maybe include info about what targets each accepts (A - arena/none, P - player, T - team)
            _chat.SendMessage(player, "--- General Information -------------------------------------------------------");
            PrintCommand(player, "schedule", "Print upcoming or recent league matches.");
            PrintCommand(player, "standings", "Print the current league standings.");
            PrintCommand(player, "results", "Print a team's match results.");
            PrintCommand(player, "roster", "Print a team's roster.");

            if (_capabilityManager.HasCapability(player, Constants.Capabilities.IsStaff))
            {
                _chat.SendMessage(player, "--- Staff ---------------------------------------------------------------------");
                PrintCommand(player, "startleaguematch", "Starts a league match (reserves an arena and announces the match)");
            }

            foreach (string key in LeagueHelpKeys)
            {
                ILeagueHelp? leagueHelp = arena.GetInterface<ILeagueHelp>(key);
                if (leagueHelp is null)
                    continue;

                try
                {
                    leagueHelp.PrintHelp(player);
                }
                finally
                {
                    arena.ReleaseInterface(ref leagueHelp, key);
                }
            }

            _chat.SendMessage(player, "-------------------------------------------------------------------------------");
            _chat.SendMessage(player, "View command instructions with: ?man <command>");
            _chat.SendMessage(player, "List all available commands with: ?commands");

            void PrintCommand(Player player, string command, string description)
            {
                _chat.SendMessage(player, $"?{command,-10}  {description}");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<team name>",
            Description = """
                Prints the roster of a league team.
                """)]
        private void Command_roster(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!TryGetDefaultSeasonId(player, arena, out long seasonId))
                return;

            if (parameters.IsWhiteSpace())
            {
                _chat.SendMessage(player, "Usage: ?roster <team name>");
                return;
            }

            PrintRosterAsync(player.Name!, seasonId, parameters.ToString());

            async void PrintRosterAsync(string playerName, long seasonId, string teamName)
            {
                await _leagueRepository.PrintRosterAsync(playerName, seasonId, teamName);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "",
            Description = """
                Prints scheduled league matches.
                """)]
        private void Command_schedule(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!TryGetDefaultSeasonId(player, arena, out long seasonId))
                return;

            PrintScheduleAsync(player.Name!, seasonId);

            async void PrintScheduleAsync(string playerName, long seasonId)
            {
                await _leagueRepository.PrintScheduleAsync(playerName, seasonId, _readOnlyActiveMatches).ConfigureAwait(false);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "",
            Description = """
                Prints league standings.
                """)]
        private void Command_standings(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!TryGetDefaultSeasonId(player, arena, out long seasonId))
                return;

            PrintStandingsAsync(player.Name!, seasonId);

            async void PrintStandingsAsync(string playerName, long seasonId)
            {
                await _leagueRepository.PrintStandingsAsync(playerName, seasonId).ConfigureAwait(false);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<team name>",
            Description = """
                Prints the results for a given team.
                """)]
        private void Command_results(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!TryGetDefaultSeasonId(player, arena, out long seasonId))
                return;

            if (parameters.IsWhiteSpace())
            {
                _chat.SendMessage(player, "Usage: ?results <team name>");
                return;
            }

            PrintResultsAsync(player.Name!, seasonId, parameters.ToString());

            async void PrintResultsAsync(string playerName, long seasonId, string teamName)
            {
                await _leagueRepository.PrintResultsAsync(playerName, seasonId, teamName);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[-f] <match id>", 
            Description = """
                Manually starts a league match.
                This updates the match's state to "In Progress", initializes the match to be played, and announces it to the zone.
                Normally, a game should be in the "Pending" state for it to be started.
                However, if there was an issue and already is "In Progress", the -f argument can be used to force start.
                """)]
        private void Command_startleaguematch(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            bool force = false;
            Span<Range> ranges = stackalloc Range[2];
            int numRanges = parameters.Split(ranges, ' ', StringSplitOptions.TrimEntries);
            ReadOnlySpan<char> idSpan;

            if (numRanges == 1)
            {
                idSpan = parameters[ranges[0]];
            }
            else if (numRanges == 2)
            {
                if (!parameters[ranges[0]].Equals("-f", StringComparison.OrdinalIgnoreCase))
                {
                    PrintUsage(player);
                    return;
                }

                force = true;
                idSpan = parameters[ranges[1]];
            }
            else
            {
                PrintUsage(player);
                return;
            }

            if (!long.TryParse(idSpan, out long seasonGameId))
            {
                PrintUsage(player);
                return;
            }

            StartGame(player.Name!, seasonGameId, force);

            async void StartGame(string playerName, long seasonGameId, bool force)
            {
                // Call the database: league.start_game
                // Deserialize the json into a game info object which has:
                // - the match type (game_type_id)
                // - which teams are participating in the match and their assigned freqs
                // - and the rosters for each team (includes whether each player is captain)
                // This should be enough info for the match to know which players can join each team's freq, and who is captain (additional commands).
                GameStartStatus status;
                LeagueGameInfo? gameStartInfo;
                try
                {
                    (status, gameStartInfo) = await _leagueRepository.StartGameAsync(seasonGameId, force, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Player? player = _playerData.FindPlayer(playerName);
                    if (player is not null)
                    {
                        _chat.SendMessage(player, $"Database error when trying to initialize league game Id {seasonGameId}. {ex.Message}");
                    }

                    return;
                }

                if (gameStartInfo is null)
                {
                    Player? player = _playerData.FindPlayer(playerName);
                    if (player is not null)
                    {
                        if (status == GameStartStatus.NotFound)
                            _chat.SendMessage(player, $"League game Id {seasonGameId} not found.");
                        else if (status == GameStartStatus.Conflict)
                            _chat.SendMessage(player, $"League game Id {seasonGameId} could not be updated due to it being in the wrong state.");
                    }

                    return;
                }

                if (!_registeredGameTypes.TryGetValue(gameStartInfo.GameTypeId, out ILeagueGameMode? gameMode))
                {
                    Player? player = _playerData.FindPlayer(playerName);
                    if (player is not null)
                    {
                        _chat.SendMessage(player, $"League game Id {seasonGameId} was for an unknown game type: {gameStartInfo.GameTypeId}.");
                    }

                    return;
                }

                // Tell the game mode to initialize the match.
                // This normally reserve an arena and create the match itself.
                ILeagueMatch? match = gameMode.CreateMatch(gameStartInfo);
                if (match is null)
                {
                    Player? player = _playerData.FindPlayer(playerName);
                    if (player is not null)
                    {
                        _chat.SendMessage(player, $"The game module failed to start league game Id {seasonGameId} of game type: {gameStartInfo.GameTypeId}.");
                    }

                    return;
                }

                _activeMatches[match.SeasonGameId] = match;
            }

            void PrintUsage(Player player)
            {
                _chat.SendMessage(player, $"Usage: {StartLeagueMatchCommandName} <league game id>");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "[ request <queue> | list <queue> | [grant | revoke] <queue> <player> ] ",
            Description = """
                Certain matchmaking queues are for league practices and require a permit to play.
                Use this command to request or manage league practice permits.
                  Verb      Description
                  -------   -----------
                  request - Request a league permit. To request for another player send it as a private message to that player.
                            League staff will review the request. Each league's rules may differ.
                            However, usually only a single name is allowed per person (in other words, no aliases allowed).
                  list    - Lists pending requests. *
                  grant   - Assigns the role to a player. *^
                  revoke  - Decline a permit request and/or remove the role from a player. *^
                * The 'Permit Manager' role is required.
                ^ The target player can be specified by sending the command as a private message instead of typing the <player> name.
                """)]
        private void Command_leaguepermit(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Span<Range> ranges = stackalloc Range[2];
            if (parameters.Split(ranges, ' ', StringSplitOptions.None) != 2)
            {
                PrintUsage(player);
                return;
            }

            ReadOnlySpan<char> verb = parameters[ranges[0]];
            ReadOnlySpan<char> queueName;
            if (verb.Equals("request", StringComparison.OrdinalIgnoreCase))
            {
                queueName = parameters[ranges[1]];

                if (!TryGetLeagueIdByQueueName(queueName, out long leagueId))
                    return;

                if (!target.TryGetPlayerTarget(out Player? targetPlayer))
                {
                    targetPlayer = player;
                }

                RequestPermitAsync(targetPlayer.Name!, leagueId, player.Name);
            }
            else if (verb.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                queueName = parameters[ranges[1]];

                if (!TryGetLeagueIdByQueueName(queueName, out long leagueId))
                    return;

                if (!IsAuthorized(player, leagueId))
                    return;

                PrintPendingPermitRequestsAsync(player.Name!, leagueId);
            }
            else
            {
                bool isGrant = verb.Equals("grant", StringComparison.OrdinalIgnoreCase);
                bool isRevoke = !isGrant && verb.Equals("revoke", StringComparison.OrdinalIgnoreCase);

                if (!isGrant && !isRevoke)
                {
                    PrintUsage(player);
                    return;
                }

                ReadOnlySpan<char> targetPlayerNameSpan;
                if (target.TryGetPlayerTarget(out Player? targetPlayer))
                {
                    queueName = parameters[ranges[1]]; ;
                    targetPlayerNameSpan = targetPlayer.Name;
                }
                else
                {
                    ReadOnlySpan<char> remaining = parameters[ranges[1]];
                    if (remaining.Split(ranges, ' ', StringSplitOptions.None) != 2)
                    {
                        _chat.SendMessage(player, $"{LeaguePermitCommandName}: A target player must be specified.");
                        return;
                    }

                    queueName = remaining[ranges[0]];
                    targetPlayerNameSpan = remaining[ranges[1]];
                }

                if (!TryGetLeagueIdByQueueName(queueName, out long leagueId))
                    return;

                if (!IsAuthorized(player, leagueId))
                    return;

                char[]? targetPlayerNameArray = ArrayPool<char>.Shared.Rent(targetPlayerNameSpan.Length);
                targetPlayerNameSpan.CopyTo(targetPlayerNameArray);

                SetLeaguePermitAsync(player.Name!, isGrant, targetPlayerNameArray, targetPlayerNameSpan.Length, leagueId);
            }

            void PrintUsage(Player player)
            {
                _chat.SendMessage(player, $"{LeaguePermitCommandName}: Invalid syntax. For information on how to use the command, see: ?man {LeaguePermitCommandName}");
            }

            bool TryGetLeagueIdByQueueName(ReadOnlySpan<char> queueName, out long leagueId)
            {
                IMatchmakingQueue? queue = _matchmakingQueues.GetQueue(queueName);
                if (queue is null)
                {
                    _chat.SendMessage(player, $"{LeaguePermitCommandName}: Queue '{queueName}' not found.");
                    leagueId = default;
                    return false;
                }

                if (queue.Options.PermitLeagueId is null)
                {
                    _chat.SendMessage(player, $"{LeaguePermitCommandName}: Queue '{queueName}' does not require a league permit.");
                    leagueId = default;
                    return false;
                }

                leagueId = queue.Options.PermitLeagueId.Value;
                return true;
            }

            bool IsAuthorized(Player player, long leagueId)
            {
                if (!_leagueAuthorization.IsInRole(player.Name!, leagueId, LeagueRole.PermitManager))
                {
                    _chat.SendMessage(player, $"{LeaguePermitCommandName}: You are not authorized for the league of the specified queue.");
                    return false;
                }

                return true;
            }

            async void RequestPermitAsync(string playerName, long leagueId, string? byPlayerName)
            {
                bool isSelfRequest = playerName.Equals(byPlayerName, StringComparison.OrdinalIgnoreCase);
                long? requestId;
                string? errorMessage;
                Player? player;

                try
                {
                    (requestId, errorMessage) = await _leagueRepository.RequestLeaguePermitAsync(playerName, leagueId, isSelfRequest ? null : byPlayerName, CancellationToken.None);
                }
                catch (Exception)
                {
                    player = _playerData.FindPlayer(byPlayerName);
                    if (player is null)
                        return;

                    _chat.SendMessage(player, $"{LeaguePermitCommandName}: Database error.");
                    return;
                }

                if (errorMessage is not null)
                {
                    player = _playerData.FindPlayer(byPlayerName);
                    if (player is null)
                        return;

                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                    try
                    {
                        sb.Append($"{LeaguePermitCommandName}: {errorMessage}");

                        if (requestId is not null)
                        {
                            sb.Append($" -- Request #{requestId.Value}");
                        }

                        _chat.SendMessage(player, sb);
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }

                    return;
                }

                if (requestId is not null)
                {
                    player = _playerData.FindPlayer(byPlayerName);
                    if (player is null)
                        return;

                    if (isSelfRequest)
                    {
                        _chat.SendMessage(player, $"{LeaguePermitCommandName}: Submitted league permit request #{requestId.Value}.");
                    }
                    else
                    {
                        _chat.SendMessage(player, $"{LeaguePermitCommandName}: Submitted league permit request #{requestId.Value} for {playerName}.");
                    }

                    if (!isSelfRequest)
                    {
                        player = _playerData.FindPlayer(playerName);
                        if (player is null)
                            return;

                        _chat.SendMessage(player, $"{LeaguePermitCommandName}: {byPlayerName} submitted league permit request #{requestId.Value} on your behalf.");
                    }
                }
            }

            async void PrintPendingPermitRequestsAsync(string playerName, long leagueId)
            {
                await _leagueRepository.PrintPendingPermitRequestsAsync(playerName, leagueId);
            }

            async void SetLeaguePermitAsync(string executorPlayerName, bool isGrant, char[] targetPlayerName, int targetPlayerNameLength, long leagueId)
            {
                try
                {
                    ReadOnlyMemory<char> targetPlayerNameMemory = targetPlayerName.AsMemory(0, targetPlayerNameLength);
                    string? errorMessage = null;

                    if (isGrant)
                    {
                        errorMessage = await _leagueAuthorization.GrantRoleAsync(
                            executorPlayerName,
                            targetPlayerNameMemory,
                            leagueId,
                            LeagueRole.PracticePermit,
                            null,
                            CancellationToken.None);
                    }
                    else
                    {
                        errorMessage = await _leagueAuthorization.RevokeRoleAsync(
                            executorPlayerName,
                            targetPlayerNameMemory,
                            leagueId,
                            LeagueRole.PracticePermit,
                            null,
                            CancellationToken.None);
                    }

                    Player? player = _playerData.FindPlayer(executorPlayerName);
                    if (player is not null)
                    {
                        if (errorMessage is null)
                        {
                            _chat.SendMessage(player, $"{LeaguePermitCommandName}: {(isGrant ? "Granted" : "Revoked")} permit to {targetPlayerNameMemory.Span}.");
                        }
                        else
                        {
                            _chat.SendMessage(player, $"{LeaguePermitCommandName}: Failed to {(isGrant ? "grant permit to" : "revoke permit from")} {targetPlayerNameMemory.Span}. {errorMessage}");
                        }
                    }

                    if (isGrant && errorMessage is null)
                    {
                        player = _playerData.FindPlayer(targetPlayerNameMemory.Span);
                        if (player is not null)
                        {
                            _chat.SendMessage(player, "You have been granted a league permit.");
                        }
                    }
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(targetPlayerName);
                }
            }
        }

        private bool TryGetDefaultSeasonId(Player player, Arena arena, out long seasonId)
        {
            string? defaultSeasonIdStr = _configManager.GetStr(arena.Cfg!, "SS.Matchmaking.League", "DefaultSeasonId");
            if (!string.IsNullOrWhiteSpace(defaultSeasonIdStr))
            {
                if (long.TryParse(defaultSeasonIdStr, out seasonId))
                {
                    return true;
                }
            }

            _chat.SendMessage(player, "This arena is not configured for league.");
            seasonId = default;
            return false;
        }
    }
}
