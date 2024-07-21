using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.Modules.Scoring
{
    /// <summary>
    /// Scoring module for ball games.
    /// </summary>
    /// <remarks>
    /// This module keeps track of the score for each team and determines when a team has won the ball game.
    /// It also rewards points to players (depending on settings).
    /// </remarks>
    [CoreModuleInfo]
    public class BallGamePoints : IModule, IArenaAttachableModule, IBallGamePoints, IBallsAdvisor
    {
        private const int MaxTeams = 8;

        private IArenaManager _arenaManager;
        private IAllPlayerStats _allPlayerStats;
        private IArenaPlayerStats _arenaPlayerStats;
        private IBalls _balls;
        private IChat _chat;
        private IConfigManager _configManager;
        private ICommandManager _commandManager;
        private IMapData _mapData;
        private INetwork _network;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IScoreStats _scoreStats;

        private ArenaDataKey<ArenaData> _adKey;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IAllPlayerStats allPlayerStats,
            IArenaPlayerStats arenaPlayerStats,
            IBalls balls,
            IChat chat,
            IConfigManager configManager,
            ICommandManager commandManager,
            IMapData mapData,
            INetwork network,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IScoreStats scoreStats)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _allPlayerStats = allPlayerStats ?? throw new ArgumentNullException(nameof(allPlayerStats));
            _arenaPlayerStats = arenaPlayerStats ?? throw new ArgumentNullException(nameof(arenaPlayerStats));
            _balls = balls ?? throw new ArgumentNullException(nameof(balls));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _scoreStats = scoreStats ?? throw new ArgumentNullException(nameof(scoreStats));

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            ArenaActionCallback.Register(arena, Callback_ArenaAction);
            BallGoalCallback.Register(arena, Callback_BallGoal);

            _commandManager.AddCommand("setscore", Command_setscore, arena);
            _commandManager.AddCommand("score", Command_score, arena);
            _commandManager.AddCommand("resetgame", Command_resetgame, arena);

            ad.BallsAdvisorToken = arena.RegisterAdvisor<IBallsAdvisor>(this);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            arena.UnregisterAdvisor(ref ad.BallsAdvisorToken);

            _commandManager.RemoveCommand("setscore", Command_setscore, arena);
            _commandManager.RemoveCommand("score", Command_score, arena);
            _commandManager.RemoveCommand("resetgame", Command_resetgame, arena);

            ArenaActionCallback.Unregister(arena, Callback_ArenaAction);
            BallGoalCallback.Unregister(arena, Callback_BallGoal);

            return true;
        }

        #endregion

        #region IGoalPoints

        void IBallGamePoints.ResetGame(Arena arena, Player player)
        {
            ResetGame(arena, player);
        }

        ReadOnlySpan<int> IBallGamePoints.GetScores(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return ReadOnlySpan<int>.Empty;

            return ad.TeamScores;
        }

        void IBallGamePoints.SetScores(Arena arena, ReadOnlySpan<int> scores)
        {
            SetScores(arena, scores);
        }

        void IBallGamePoints.ResetScores(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            ResetTeamScores(ad);
            PrintScoreMessage(arena, null);
        }

        #endregion

        #region IBallsAdvisor

        bool IBallsAdvisor.AllowGoal(Arena arena, Player player, int ballId, MapCoordinate mapCoordinate, ref BallData ballData)
        {
            // Allow the goal if the tile is a goal and it can be scored on by the player's freq.
            _balls.GetGoalInfo(arena, player.Freq, mapCoordinate, out bool isScoreable, out _);
            return isScoreable;
        }

        #endregion

        #region Callbacks

        // Note: Soccer:Mode is a client setting. So, it's [ConfigHelp] is in ClientSettingsConfig.cs
        [ConfigHelp("Soccer", "CapturePoints", ConfigScope.Arena, typeof(int), DefaultValue = "1",
            Description = "If positive, these points are distributed to each goal/team. " +
            "When you make a goal, the points get transferred to your goal/team. " +
            "In timed games, team with most points in their goal wins. " +
            "If one team gets all the points, then they win as well. " +
            "If negative, teams are given 1 point for each goal, " +
            "first team to reach -CapturePoints points wins the game.")]
        [ConfigHelp("Soccer", "Reward", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Negative numbers equal absolute points given, positive numbers use FlagReward formula.")]
        [ConfigHelp("Soccer", "WinBy", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Have to beat other team by this many goals.")]
        [ConfigHelp("Soccer", "MinPlayers", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "The minimum number of players who must be playing for soccer points to be awarded.")]
        [ConfigHelp("Soccer", "MinTeams", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "The minimum number of teams that must exist for soccer points to be awarded.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == ArenaAction.Create
                // TODO: || action == ArenaAction.ConfChanged
                )
            {
                ad.Mode = _configManager.GetEnum(arena.Cfg, "Soccer", "Mode", SoccerMode.All);
                ad.CapturePoints = _configManager.GetInt(arena.Cfg, "Soccer", "CapturePoints", 1);
                ad.Reward = _configManager.GetInt(arena.Cfg, "Soccer", "Reward", 0);
                ad.WinBy = _configManager.GetInt(arena.Cfg, "Soccer", "WinBy", 0);
                ad.MinPlayers = _configManager.GetInt(arena.Cfg, "Soccer", "MinPlayers", 0);
                ad.MinTeams = _configManager.GetInt(arena.Cfg, "Soccer", "MinTeams", 0);
                ad.IsFrequencyShipTypes = _configManager.GetInt(arena.Cfg, "Misc", "FrequencyShipTypes", 0) != 0;
                ad.IsCustomGame = _configManager.GetInt(arena.Cfg, "Soccer", "CustomGame", 0) != 0;

                ad.IsStealPoints = ad.CapturePoints >= 0;
                ResetTeamScores(ad);
            }
        }

        private void Callback_BallGoal(Arena arena, Player player, byte ballId, MapCoordinate coordinate)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.IsCustomGame)
                return; // custom soccer games do their own goal handling (e.g. scramble)

            //if (!_balls.TryGetBallData(arena, ballId, out BallData ballData))
            //return;

            short scoringFreq = player.Freq; // TODO: investigate how ASSS has a value other than -1 when it accesses ballData.Freq;
            if (scoringFreq < 0)
                return;

            _balls.GetGoalInfo(arena, scoringFreq, coordinate, out _, out short? ownerFreq);

            bool nullGoal = false;

            if (ad.IsStealPoints && ownerFreq != null)
            {
                if (ad.TeamScores[ownerFreq.Value] > 0)
                {
                    ad.TeamScores[ownerFreq.Value]--;
                    ad.TeamScores[scoringFreq]++;
                }
                else
                {
                    nullGoal = true;
                }
            }
            else
            {
                ad.TeamScores[scoringFreq]++;
            }

            int points = 0;
            HashSet<Player> teamSet = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> enemySet = _objectPoolManager.PlayerSetPool.Get();

            try
            {

                _playerData.Lock();

                try
                {
                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (otherPlayer.Arena == arena
                            && otherPlayer.Status == PlayerState.Playing)
                        {
                            if (otherPlayer.Freq == player.Freq)
                                teamSet.Add(otherPlayer);
                            else
                                enemySet.Add(otherPlayer);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (ad.Reward != 0)
                {
                    points = RewardPoints(arena, scoringFreq);

                    _chat.SendSetMessage(teamSet, ChatSound.Goal, $"Team Goal! by {player.Name}  Reward:{points}");
                    _chat.SendSetMessage(enemySet, ChatSound.Goal, $"Enemy Goal! by {player.Name}  Reward:{points}");
                }
                else
                {
                    _chat.SendSetMessage(teamSet, ChatSound.Goal, $"Team Goal! by {player.Name}");
                    _chat.SendSetMessage(enemySet, ChatSound.Goal, $"Enemy Goal! by {player.Name}");

                    if (nullGoal)
                    {
                        _chat.SendArenaMessage(arena, $"Enemy goal had no points to give.");
                    }
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(teamSet);
                teamSet = null;
                _objectPoolManager.PlayerSetPool.Return(enemySet);
                enemySet = null;
            }

            S2C_Goal packet = new(scoringFreq, points);
            _network.SendToArena(arena, null, ref packet, NetSendFlags.Reliable | NetSendFlags.PriorityP4);

            // TODO: Verify that this is this needed.
            // One would think that the client is smart enough to update stats based on the S2C_Goal packet.
            // However, what rules does the client logic use regarding being in a safe zone and being in spec mode?
            _scoreStats.SendUpdates(arena, null);

            BallGameGoalCallback.Fire(arena, arena, player, ballId, coordinate);

            if (ad.Mode != SoccerMode.All)
            {
                PrintScoreMessage(arena, null);
                CheckGameOver(arena);
            }
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<freq 0 score> [<freq 1 score> [... [<freq 7 score>]]]",
            Description = """
                Changes score of current soccer game, based on arguments. Only supports
                first eight freqs, and arena must be in absolute scoring mode 
                (Soccer:CapturePoints < 0).
                """)]
        private void Command_setscore(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.Mode == SoccerMode.All)
            {
                _chat.SendMessage(player, "Arena cannot be in Soccer:Mode=all");
                return;
            }

            if (ad.IsStealPoints)
            {
                _chat.SendMessage(player, "Arena must be using absolute scoring (Soccer:CapturePoints < 0).");
                return;
            }

            Span<int> newScores = stackalloc int[MaxTeams];

            int i = 0;
            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token;
            while (i < newScores.Length
                && (token = remaining.GetToken(' ', out remaining)).Length > 0)
            {
                if (!int.TryParse(token, out int score))
                {
                    _chat.SendMessage(player, $"Invalid input: '{token}'. It must be an integer.");
                    return;
                }

                if (score < 0)
                    score = 0;

                newScores[i++] = score;
            }

            SetScores(arena, newScores[..i]);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Returns the current score of the soccer game in progress.")]
        private void Command_score(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            PrintScoreMessage(arena, player);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Resets soccer game scores and balls.")]
        private void Command_resetgame(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ResetGame(player.Arena, player);
        }

        #endregion

        private void CheckGameOver(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            // Find the team with the highest score.
            short freq = 0;
            for (short i = 1; i < ad.TeamScores.Length; i++)
            {
                if (ad.TeamScores[i] > ad.TeamScores[freq])
                    freq = i;
            }

            // Check if the team with the highest score qualifies for a win.
            if (ad.IsStealPoints
                && (ad.Mode == SoccerMode.LeftRight || ad.Mode == SoccerMode.TopBottom))
            {
                if (ad.TeamScores[(~freq) + 2] == 0)
                {
                    // The opposing team doesn't have any points left.
                    EndGame(arena, ad, freq);
                }
            }
            else if (ad.IsStealPoints
                && (ad.Mode == SoccerMode.QuadrantsDefend1
                    || ad.Mode == SoccerMode.QuadrantsDefend3
                    || ad.Mode == SoccerMode.SidesDefend1
                    || ad.Mode == SoccerMode.SidesDefend3))
            {
                int zeroScoreTeamCount = 0;
                for (int i = 0; i < ad.TeamScores.Length; i++)
                {
                    if (ad.TeamScores[i] == 0)
                        zeroScoreTeamCount++;
                }

                if (zeroScoreTeamCount == 3)
                {
                    // The other 3 teams do not have any points left.
                    EndGame(arena, ad, freq);
                }
            }
            else
            {
                // Absolute scoring for any of the modes.
                int minScoreToWin = Math.Abs(ad.CapturePoints);

                if (ad.TeamScores[freq] >= minScoreToWin)
                {
                    int freqCount = 0;
                    for (int i = 0; i < ad.TeamScores.Length; i++)
                    {
                        if (ad.TeamScores[i] + ad.WinBy <= ad.TeamScores[freq])
                            freqCount++;
                    }

                    if (freqCount == ad.TeamScores.Length - 1)
                    {
                        EndGame(arena, ad, freq);
                    }
                }
            }

            void EndGame(Arena arena, ArenaData ad, short freq)
            {
                _chat.SendArenaMessage(arena, ChatSound.Ding, $"Soccer game over.");
                int points = RewardPoints(arena, freq);
                _balls.EndGame(arena);
                ResetTeamScores(ad);

                // Note: ASSS doesn't send score stats updates here because it relies on the IBalls.EndGame to end the 'game' interval,
                // which triggers the score updates. However, in this server, it only triggers an update if it's on the 'reset' interval.
                if (points > 0)
                    _scoreStats.SendUpdates(arena, null);
            }
        }

        private void PrintScoreMessage(Arena arena, Player player)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                switch (ad.Mode)
                {
                    case SoccerMode.LeftRight:
                    case SoccerMode.TopBottom:
                        // 2 teams
                        if (ad.IsFrequencyShipTypes)
                        {
                            sb.Append($"SCORE: Warbirds:{ad.TeamScores[0]} Javelins:{ad.TeamScores[1]}");
                        }
                        else
                        {
                            sb.Append($"SCORE: Even:{ad.TeamScores[0]} Odds:{ad.TeamScores[1]}");
                        }

                        break;

                    case SoccerMode.QuadrantsDefend1:
                    case SoccerMode.QuadrantsDefend3:
                    case SoccerMode.SidesDefend1:
                    case SoccerMode.SidesDefend3:
                        // 4 teams
                        if (ad.IsFrequencyShipTypes)
                        {
                            sb.Append($"SCORE: Warbirds:{ad.TeamScores[0]} Javelins:{ad.TeamScores[1]} Spiders:{ad.TeamScores[2]} Leviathans:{ad.TeamScores[3]}");
                        }
                        else
                        {
                            sb.Append($"SCORE: Team0:{ad.TeamScores[0]} Team1:{ad.TeamScores[1]} Team2:{ad.TeamScores[2]} Team3:{ad.TeamScores[3]}");
                        }

                        break;

                    case SoccerMode.All:
                    default:
                        // no score message
                        break;
                }

                if (player != null)
                    _chat.SendMessage(player, sb);
                else
                    _chat.SendArenaMessage(arena, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        private int RewardPoints(Arena arena, short scoringFreq)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return 0;

            int playerCount = 0;
            Span<int> freqCounts = stackalloc int[4];

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena == arena
                            && player.Status == PlayerState.Playing
                            && player.Ship != ShipType.Spec)
                        {
                            playerCount++;
                            freqCounts[player.Freq % 4]++;

                            if (player.Freq == scoringFreq)
                            {
                                _allPlayerStats.IncrementStat(player, StatCodes.BallGamesWon, null, 1);

                                // Only reward points if not in a safe zone.
                                if ((player.Position.Status & PlayerPositionStatus.Safezone) == 0)
                                {
                                    set.Add(player);
                                }
                            }
                            else
                            {
                                _allPlayerStats.IncrementStat(player, StatCodes.BallGamesLost, null, 1);
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                int points = ad.Reward < 1
                    ? Math.Abs(ad.Reward)
                    : playerCount * playerCount * ad.Reward / 1000;

                int freqCount = 0;
                for (int i = 0; i < freqCounts.Length; i++)
                    if (freqCounts[i] > 0)
                        freqCount++;

                if (playerCount < ad.MinPlayers
                    || freqCount < ad.MinTeams)
                {
                    points = 0;
                }
                else
                {
                    foreach (Player player in set)
                    {
                        _allPlayerStats.IncrementStat(player, StatCodes.FlagPoints, null, points);
                    }
                }

                return points;
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        private void SetScores(Arena arena, ReadOnlySpan<int> scores)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.Mode == SoccerMode.All || ad.IsStealPoints)
                return;

            for (int i = 0; i < ad.TeamScores.Length && i < scores.Length; i++)
            {
                ad.TeamScores[i] = scores[i] < 0 ? 0 : scores[i];
            }

            PrintScoreMessage(arena, null);
            CheckGameOver(arena);
        }

        private void ResetGame(Arena arena, Player player)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (ad.Mode != SoccerMode.All)
            {
                if (player != null)
                    _chat.SendArenaMessage(arena, $"Resetting game. -{player.Name}");
                else
                    _chat.SendArenaMessage(arena, $"Resetting game.");

                _chat.SendArenaMessage(arena, ChatSound.Ding, $"Soccer game over.");

                _balls.EndGame(arena);
                ResetTeamScores(ad);
            }
        }

        private static void ResetTeamScores(ArenaData ad)
        {
            if (ad == null)
                return;

            if (ad.IsStealPoints)
            {
                for (int i = 0; i < ad.TeamScores.Length; i++)
                    ad.TeamScores[i] = ad.CapturePoints;
            }
            else
            {
                for (int i = 0; i < ad.TeamScores.Length; i++)
                    ad.TeamScores[i] = 0;
            }
        }

        #region Helper types

        private class ArenaData : IResettable
        {
            public AdvisorRegistrationToken<IBallsAdvisor> BallsAdvisorToken;

            // settings
            public SoccerMode Mode;
            public int CapturePoints;
            public bool IsStealPoints;
            public int Reward;
            public int WinBy;
            public int MinPlayers;
            public int MinTeams;
            public bool IsFrequencyShipTypes;
            public bool IsCustomGame;

            // state
            public readonly int[] TeamScores = new int[MaxTeams];

            public bool TryReset()
            {
                BallsAdvisorToken = null;
                Mode = SoccerMode.All;
                CapturePoints = 0;
                IsStealPoints = false;
                Reward = 0;
                WinBy = 0;
                MinPlayers = 0;
                MinTeams = 0;
                IsFrequencyShipTypes = false;
                IsCustomGame = false;
                Array.Clear(TeamScores);
                return true;
            }
        }

        #endregion
    }
}
