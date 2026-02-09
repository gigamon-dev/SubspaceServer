using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.League;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

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
        private readonly ILeagueRepository _leagueRepository;
        private readonly IPlayerData _playerData;

        private InterfaceRegistrationToken<ILeagueManager>? _leagueManagerRegistrationToken;

        private const string InitLeagueMatchCommandName = "initleaguematch";

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
            ILeagueRepository leagueRepository,
            IPlayerData playerData)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _leagueRepository = leagueRepository ?? throw new ArgumentNullException(nameof(leagueRepository));
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
            _commandManager.AddCommand(InitLeagueMatchCommandName, Command_initleaguematch);

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
            _commandManager.RemoveCommand(InitLeagueMatchCommandName, Command_initleaguematch);

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
            _activeMatches.TryRemove(leagueMatch.LeagueGame.SeasonGameId, out _);
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
            Args = "[-f | -u] <match id>", 
            Description = """
                Manually initializes a league match. This sets the match's state to "In Progress" in the database.
                Normally, a match needs to be in the "Pending" state for it to be initialized.
                However, if there was previously an issue and the match is already is "In Progress", the -f argument can be used to force it.
                It is up to the underlying game mode as to what happens when initializing a match. In general,
                a game mode will reserve an arena for the match, set up the match, including teams, and announce the match to the zone.
                Use the -u argument to uninitialize a previously initialized league match. Whether a match can be uninitialized depends 
                on the current state of the match and if the underlying game mode allows it. In general, an attempt to unitialize will 
                succeed if the match hasn't already begun. When uninitialized, the match's state is updated back to "Pending" in the database.
                """)]
        private void Command_initleaguematch(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            bool force = false;
            bool cancel = false;
            Span<Range> ranges = stackalloc Range[2];
            int numRanges = parameters.Split(ranges, ' ', StringSplitOptions.TrimEntries);
            ReadOnlySpan<char> idSpan;

            if (numRanges == 1)
            {
                idSpan = parameters[ranges[0]];
            }
            else if (numRanges == 2)
            {
                if (parameters[ranges[0]].Equals("-u", StringComparison.OrdinalIgnoreCase))
                {
                    cancel = true;
                    idSpan = parameters[ranges[1]];
                }
                else if (parameters[ranges[0]].Equals("-f", StringComparison.OrdinalIgnoreCase))
                {
                    force = true;
                    idSpan = parameters[ranges[1]];
                }
                else
                {
                    PrintUsage(player);
                    return;
                }
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

            if (cancel)
            {
                UninitializeGameAsync(player.Name!, seasonGameId);
            }
            else
            {
                InitializeGameAsync(player.Name!, seasonGameId, force);
            }

            async void InitializeGameAsync(string playerName, long seasonGameId, bool force)
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
                        _chat.SendMessage(player, $"Error initializing league game Id {seasonGameId}. Database error. {ex.Message}");
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

                _activeMatches[match.LeagueGame.SeasonGameId] = match;
            }

            async void UninitializeGameAsync(string playerName, long seasonGameId)
            {
                // Find the match.
                if (!_activeMatches.TryGetValue(seasonGameId, out ILeagueMatch? match))
                {
                    _chat.SendMessage(player, "Match not found.");
                    return;
                }

                // Find the game mode.
                if (!_registeredGameTypes.TryGetValue(match.LeagueGame.GameTypeId, out ILeagueGameMode? gameMode))
                {
                    _chat.SendMessage(player, "Match found, but the game mode is not registered.");
                    return;
                }

                // Ask the game mode to cancel the match.
                if (!gameMode.CancelMatch(match))
                {
                    _chat.SendMessage(player, "The match is not in a state which can be cancelled.");
                    return;
                }

                // Successfully cancelled
                _activeMatches.Remove(seasonGameId, out _);
                _chat.SendMessage(player, $"League match {seasonGameId} uninitialized.");

                // Update the match's database record (change state from "In Progress" to "Pending")
                try
                {
                    GameStartStatus result = await _leagueRepository.UndoStartGameAsync(seasonGameId, CancellationToken.None);

                    Player? player = _playerData.FindPlayer(playerName);
                    if (player is not null)
                    {
                        if (result == GameStartStatus.NotFound)
                            _chat.SendMessage(player, $"Database update failed: League game Id {seasonGameId} not found.");
                        else if (result == GameStartStatus.Conflict)
                            _chat.SendMessage(player, $"Database update failed: League game Id {seasonGameId} could not be updated due to it being in the wrong state.");
                    }
                }
                catch (Exception ex)
                {
                    Player? player = _playerData.FindPlayer(playerName);
                    if (player is not null)
                    {
                        _chat.SendMessage(player, $"Database update failed for League game Id {seasonGameId}. {ex.Message}");
                    }
                }
            }

            void PrintUsage(Player player)
            {
                _chat.SendMessage(player, $"Usage: {InitLeagueMatchCommandName} <league game id>");
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
