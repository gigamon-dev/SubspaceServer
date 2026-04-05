using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;
using System.Diagnostics;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module for controlling lvz objects for matches.
    /// This includes the statbox, scoreboard, and timer.
    /// </summary>
    public sealed class MatchLvz(
        IConfigManager configManager,
        ILvzObjects lvzObjects,
        IMainloopTimer mainloopTimer,
        IMatchFocus matchFocus,
        IObjectPoolManager objectPoolManager) : IModule, IArenaAttachableModule
    {
        private IComponentBroker? _broker;
        private IPlayerStatboxPreference? _statboxPreference;
        #region Constants

        /// <summary>
        /// The # of lines in the character display area.
        /// </summary>
        private const int StatBoxNumLines = 8;

        /// <summary>
        /// The # of characters per line in the character data display area.
        /// </summary>
        private const int StatBoxCharactersPerLine = 32;

        /// <summary>
        /// The # of lvz objects the statbox consists of for displaying the matrix of characters.
        /// </summary>
        private const int StatBoxCharacterObjectCount = StatBoxNumLines * StatBoxCharactersPerLine;

        /// <summary>
        /// The # of strikethrough objects per line in the character display area.
        /// </summary>
        private const int StatBoxStrikethroughObjectsPerLine = 32;

        /// <summary>
        /// The # of strikethrough objects for all lines.
        /// </summary>
        private const int StatBoxStrikethroughObjectsCount = StatBoxNumLines * StatBoxStrikethroughObjectsPerLine;

        /// <summary>
        /// The # of characters to use for displaying player names.
        /// </summary>
        private const int StatBoxNameLength = Constants.MaxPlayerNameLength;

        /// <summary>
        /// The # of lvz objects each frame consists of.
        /// </summary>
        private const int StatBoxObjectsPerFrame = 32;

        /// <summary>
        /// The maximum # of frames that can be shown at once (2 around the header + 1 for the top frame + 1 for the bottom frame).
        /// </summary>
        private const int StatBoxMaxFrames = 4;

        /// <summary>
        /// The # of objects that make up the header (labels and icons).
        /// </summary>
        private const int StatBoxObjectsPerHeader = 6; // 0-3 (Detailed), 4-5 (Simple K/D), all tracked per-preference

        /// <summary>
        /// The # of objects that the header + frames
        /// </summary>
        private const int StatBoxHeaderAndFramesObjectCount = (StatBoxObjectsPerFrame * StatBoxMaxFrames) + StatBoxObjectsPerHeader;

        /// <summary>
        /// The # of columns used to display item counts.
        /// </summary>
        private const int StatBoxNumItemColumns = 2; // repels and rockets

        /// <summary>
        /// The # of objects to display an item count.
        /// </summary>
        private const int StatBoxObjectsPerNumericValue = 2; // 2 digits each

        private const int Initialize_MaxChanges = InitializeStatBox_MaxChanges;
        private const int Initialize_MaxToggles = Scoreboard_MaxToggles + InitializeStatBox_MaxToggles;

        private const int Scoreboard_Timer_MaxToggles = 8; // 4 digits, toggle off and on
        private const int Scoreboard_Freqs_MaxToggles = 16; // each freq is 4 digits, toggle off and on
        private const int Scoreboard_Score_MaxToggles = 8; // 4 digits, toggle off and on
        private const int Scoreboard_MaxToggles = 1 + Scoreboard_Timer_MaxToggles + Scoreboard_Freqs_MaxToggles + Scoreboard_Score_MaxToggles;

        private const int InitializeStatBox_MaxChanges = (StatBoxNumLines * (StatBox_NameChange_MaxChanges + StatBox_NumColumns * StatBox_SetColumn_MaxChanges));
        private const int InitializeStatBox_MaxToggles = StatBox_RefreshHeaderAndFrame_MaxToggles + (StatBoxNumLines * (StatBox_NameChange_MaxToggles + StatBox_NumColumns * StatBox_SetColumn_MaxToggles));

        private const int Clear_MaxChanges = StatBoxCharacterObjectCount;
        private const int Clear_MaxToggles = Scoreboard_MaxToggles + StatBox_RefreshHeaderAndFrame_MaxToggles + StatBoxCharacterObjectCount + StatBoxStrikethroughObjectsCount;

        private const int StatBox_RefreshForSub_MaxChanges = StatBox_NameChange_MaxChanges + StatBox_NumColumns * StatBox_SetColumn_MaxChanges;
        private const int StatBox_RefreshForSub_MaxToggles = StatBox_RefreshHeaderAndFrame_MaxToggles + StatBox_NameChange_MaxToggles + StatBox_NumColumns * StatBox_SetColumn_MaxToggles + StatBoxStrikethroughObjectsCount;

        private const int RefreshForKill_MaxChanges = StatBox_RefreshForKill_MaxChanges;
        private const int RefreshForKill_MaxToggles = Scoreboard_Score_MaxToggles + StatBox_RefreshForKill_MaxToggles;

        private const int StatBox_RefreshForKill_MaxChanges = StatBox_SetColumn_MaxChanges;
        private const int StatBox_RefreshForKill_MaxToggles = StatBox_NumColumns * StatBox_SetColumn_MaxToggles + StatBoxStrikethroughObjectsPerLine;

        private const int StatBox_RefreshHeaderAndFrame_MaxToggles = (StatBoxMaxFrames * StatBoxObjectsPerFrame) + StatBoxObjectsPerHeader;

        private const int StatBox_NameChange_MaxChanges = StatBoxNameLength;
        private const int StatBox_NameChange_MaxToggles = StatBoxNameLength;

        private const int StatBox_NumColumns = 3;

        private const int StatBox_SetColumn_MaxChanges = StatBoxObjectsPerNumericValue;
        private const int StatBox_SetColumn_MaxToggles = StatBoxObjectsPerNumericValue;

        private const int StatBox_RefreshItems_MaxChanges = StatBox_NumColumns * StatBox_SetColumn_MaxChanges;
        private const int StatBox_RefreshItems_MaxToggles = StatBox_NumColumns * StatBox_SetColumn_MaxToggles;

        // For a stats kill update: up to 1 column per player (kills for killer, deaths for killed).
        private const int StatBox_RefreshStatsKill_MaxChanges = 2 * StatBox_SetColumn_MaxChanges;
        private const int StatBox_RefreshStatsKill_MaxToggles = 2 * StatBox_SetColumn_MaxToggles;

        // Maximum toggles when computing differences between any two match states.
        private const int GetDifferences_MaxToggles =
            Scoreboard_MaxToggles * 2 +
            StatBoxHeaderAndFramesObjectCount * 2 +
            StatBoxCharacterObjectCount * 2 +
            StatBoxStrikethroughObjectsCount * 2;

        #endregion

        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILvzObjects _lvzObjects = lvzObjects ?? throw new ArgumentNullException(nameof(lvzObjects));
        private readonly IMainloopTimer _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        private readonly IMatchFocus _matchFocus = matchFocus ?? throw new ArgumentNullException(nameof(matchFocus));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly Dictionary<Arena, ArenaLvzData> _arenaDataDictionary = new(Constants.TargetArenaCount);
        private readonly Dictionary<Player, PlayerData> _playerDataDictionary = new(Constants.TargetPlayerCount);

        private const int TargetBoxesPerArena = 16;
        private static readonly DefaultObjectPool<ArenaLvzData> s_arenaDataPool = new(new DefaultPooledObjectPolicy<ArenaLvzData>(), Constants.TargetArenaCount);
        private static readonly DefaultObjectPool<PlayerData> s_playerDataPool = new(new DefaultPooledObjectPolicy<PlayerData>(), Constants.TargetPlayerCount);
        //private static readonly DefaultObjectPool<MatchLvzState> s_matchLvzStatePool = new(new DefaultPooledObjectPolicy<MatchLvzState>(), Constants.TargetPlayerCount);


        #region Digit to image mappings

        private static readonly Dictionary<char, byte> _neutralDigitImages = new(10)
        {
            {'0', 48}, {'1', 49}, {'2', 50}, {'3', 51}, {'4', 52}, {'5', 53}, {'6', 54}, {'7', 55}, {'8', 56}, {'9', 57},
        };

        private static readonly Dictionary<char, byte> _redDigitImages = new(10)
        {
            {'0', 10}, {'1', 11}, {'2', 12}, {'3', 13}, {'4', 14}, {'5', 15}, {'6', 16}, {'7', 17}, {'8', 18}, {'9', 19},
        };

        private static readonly Dictionary<char, byte> _yellowDigitImages = new(10)
        {
            {'0', 20}, {'1', 21}, {'2', 22}, {'3', 23}, {'4', 24}, {'5', 25}, {'6', 26}, {'7', 27}, {'8', 28}, {'9', 29},
        };

        #endregion

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _broker = broker;
            _statboxPreference = broker.GetInterface<IPlayerStatboxPreference>();
            StatboxPreferenceChangedCallback.Register(broker, Callback_StatboxPreferenceChanged);
            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            _arenaDataDictionary.Add(arena, s_arenaDataPool.Get());

            ArenaActionCallback.Register(arena, Callback_ArenaAction);
            PlayerActionCallback.Register(arena, Callback_PlayerAction);
            MatchFocusChangedCallback.Register(arena, Callback_MatchFocusChanged);
            TeamVersusMatchStartedCallback.Register(arena, Callback_TeamVersusMatchStarted);
            TeamVersusMatchEndedCallback.Register(arena, Callback_TeamVersusMatchEnded);
            TeamVersusMatchPlayerSubbedCallback.Register(arena, Callback_TeamVersusMatchPlayerSubbed);
            TeamVersusMatchPlayerKilledCallback.Register(arena, Callback_TeamVersusMatchPlayerKilled);
            TeamVersusStatsPlayerKilledCallback.Register(arena, Callback_TeamVersusStatsPlayerKilled);
            TeamVersusMatchPlayerItemsChangedCallback.Register(arena, Callback_TeamVersusMatchPlayerItemsChanged);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            ArenaActionCallback.Unregister(arena, Callback_ArenaAction);
            PlayerActionCallback.Unregister(arena, Callback_PlayerAction);
            MatchFocusChangedCallback.Unregister(arena, Callback_MatchFocusChanged);
            TeamVersusMatchStartedCallback.Unregister(arena, Callback_TeamVersusMatchStarted);
            TeamVersusMatchEndedCallback.Unregister(arena, Callback_TeamVersusMatchEnded);
            TeamVersusMatchPlayerSubbedCallback.Unregister(arena, Callback_TeamVersusMatchPlayerSubbed);
            TeamVersusMatchPlayerKilledCallback.Unregister(arena, Callback_TeamVersusMatchPlayerKilled);
            TeamVersusStatsPlayerKilledCallback.Unregister(arena, Callback_TeamVersusStatsPlayerKilled);
            TeamVersusMatchPlayerItemsChangedCallback.Unregister(arena, Callback_TeamVersusMatchPlayerItemsChanged);

            if (_arenaDataDictionary.Remove(arena, out ArenaLvzData? arenaLvzData))
            {
                s_arenaDataPool.Return(arenaLvzData);
            }

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            StatboxPreferenceChangedCallback.Unregister(broker, Callback_StatboxPreferenceChanged);

            if (_statboxPreference is not null)
                broker.ReleaseInterface(ref _statboxPreference);

            _broker = null;
            return true;
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.Create)
            {
                if (!_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                {
                    arenaData = s_arenaDataPool.Get();
                    _arenaDataDictionary.Add(arena, arenaData);
                }

                int statBoxStartingObjectId = _configManager.GetInt(arena.Cfg!, "SS.Matchmaking.MatchLvz", "StatBoxStartingObjectId", 2000);

                arenaData.Initialize(arena, _lvzObjects, statBoxStartingObjectId);
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterArena)
            {
                _playerDataDictionary.Add(player, s_playerDataPool.Get());
            }
            if (action == PlayerAction.EnterGame)
            {
                if (arena is null)
                    return;

                IMatchData? matchData = _matchFocus.GetFocusedMatch(player) as IMatchData;
                if (matchData is not null
                    && matchData.Arena == arena)
                {
                    SetAndSendMatchLvz(player, matchData);
                }
            }
            else if (action == PlayerAction.LeaveArena)
            {
                if (_playerDataDictionary.Remove(player, out PlayerData? playerData))
                {
                    if (playerData.CurrentMatch is not null)
                    {
                        playerData.CurrentMatch.Players.Remove(player);
                        playerData.CurrentMatch = null;
                    }

                    s_playerDataPool.Return(playerData);
                }
            }
        }

        private void Callback_MatchFocusChanged(Player player, IMatch? oldMatch, IMatch? newMatch)
        {
            // TODO: add a delay (throttle) if switching to a different match
            SetAndSendMatchLvz(player, newMatch as IMatchData);
        }

        private void SetAndSendMatchLvz(Player player, IMatchData? matchData)
        {
            if (!_playerDataDictionary.TryGetValue(player, out PlayerData? playerData))
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            MatchLvzState? newState = null;

            if (matchData is not null)
            {
                if (matchData.Arena != arena) // sanity check
                    return;

                StatboxPreference pref = _statboxPreference?.GetPreference(player) ?? StatboxPreference.Detailed;
                newState = arenaData.GetOrAddMatch(matchData.MatchIdentifier, pref);

                if (!newState.IsInitialized && matchData.Started is not null)
                {
                    Span<LvzObjectChange> initChanges = stackalloc LvzObjectChange[Initialize_MaxChanges];
                    Span<LvzObjectToggle> initToggles = stackalloc LvzObjectToggle[Initialize_MaxToggles];
                    newState.Start(matchData, initChanges, out _, initToggles, out _);
                    // Purposely ignore changes/toggles — player hasn't been added yet.
                }

                if (!newState.IsInitialized)
                {
                    // Match hasn't started yet. Don't track the player — they'll be picked up properly
                    // when the match starts and SetAndSendMatchLvz is called again for all players.
                    return;
                }
            }

            MatchLvzState? oldState = playerData.CurrentMatch;

            if (newState is null)
            {
                if (oldState is not null)
                {
                    // The player stopped focusing on a match.
                    // Set the player's lvz back to the default.
                    Span<LvzObjectChange> changes = stackalloc LvzObjectChange[Initialize_MaxChanges];
                    Span<LvzObjectToggle> toggles = stackalloc LvzObjectToggle[GetDifferences_MaxToggles];
                    MatchLvzState.GetDifferences(oldState, arenaData.DefaultLvzState!, changes, out int changesWritten, toggles, out int togglesWritten);
                    _lvzObjects.SetAndToggle(player, changes[..changesWritten], toggles[..togglesWritten]);
                }
            }
            else
            {
                if (newState == oldState)
                    return; // Already on this state. Nothing to do. This shouldn't happen.

                MatchLvzState fromState = oldState ?? arenaData.DefaultLvzState!;
                Span<LvzObjectChange> changes = stackalloc LvzObjectChange[Initialize_MaxChanges];
                Span<LvzObjectToggle> toggles = stackalloc LvzObjectToggle[GetDifferences_MaxToggles];
                MatchLvzState.GetDifferences(fromState, newState, changes, out int changesWritten, toggles, out int togglesWritten);
                _lvzObjects.SetAndToggle(player, changes[..changesWritten], toggles[..togglesWritten]);
            }

            oldState?.Players.Remove(player);
            newState?.Players.Add(player);
            playerData.CurrentMatch = newState;
        }

        private void Callback_TeamVersusMatchStarted(IMatchData matchData)
        {
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            // Gather all players watching this match and route them to their preferred state.
            // States are created lazily via SetAndSendMatchLvz.
            HashSet<Player> allPlayers = new();
            _matchFocus.TryGetPlayers(matchData, allPlayers, MatchFocusReasons.Playing | MatchFocusReasons.Spectating, arena);

            foreach (Player player in allPlayers)
            {
                SetAndSendMatchLvz(player, matchData);
            }

            // Eagerly initialize the Simple state even if no player currently has Simple preference.
            // This ensures kill/death counts are tracked from the start, so a player who switches
            // from Off→Simple mid-match sees the correct accumulated state instead of 0/0.
            MatchLvzState simpleState = arenaData.GetOrAddMatch(matchData.MatchIdentifier, StatboxPreference.Simple);
            if (!simpleState.IsInitialized)
            {
                Span<LvzObjectChange> initChanges = stackalloc LvzObjectChange[Initialize_MaxChanges];
                Span<LvzObjectToggle> initToggles = stackalloc LvzObjectToggle[Initialize_MaxToggles];
                simpleState.Start(matchData, initChanges, out _, initToggles, out _);
                // Ignore changes/toggles — no players have been added to this state yet.
            }

            // Start a timer to refresh the game timer every 10 seconds
            _mainloopTimer.SetTimer(MainloopTimer_RefreshGameTimer, 10000, 10000, matchData, matchData);
        }

        private bool MainloopTimer_RefreshGameTimer(IMatchData matchData)
        {
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return false;

            Span<LvzObjectToggle> toggles = stackalloc LvzObjectToggle[Scoreboard_Timer_MaxToggles];
            bool anyState = false;
            foreach (StatboxPreference pref in new[] { StatboxPreference.Detailed, StatboxPreference.Simple, StatboxPreference.Off })
            {
                MatchLvzState? state = arenaData.TryGetMatch(matchData.MatchIdentifier, pref);
                if (state is null)
                    continue;

                anyState = true;
                if (state.Players.Count == 0)
                    continue;

                Span<LvzObjectToggle> remainingToggles = toggles;
                int togglesWritten = 0;
                state.RefreshScoreboardTimer(matchData, ref remainingToggles, ref togglesWritten);
                Span<LvzObjectToggle> sentToggles = toggles[..togglesWritten];

                foreach (Player player in state.Players)
                {
                    _lvzObjects.Toggle(player, sentToggles);
                }
            }

            return anyState;
        }

        private void Callback_TeamVersusMatchEnded(IMatchData matchData, MatchEndReason reason, ITeam? winnerTeam)
        {
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            _mainloopTimer.ClearTimer<IMatchData>(MainloopTimer_RefreshGameTimer, matchData);

            // Remove players first — while the state still reflects the live match, so that
            // SetAndSendMatchLvz can diff current→default and send the hide packets to each player.
            foreach (StatboxPreference pref in new[] { StatboxPreference.Detailed, StatboxPreference.Simple, StatboxPreference.Off })
            {
                MatchLvzState? state = arenaData.TryGetMatch(matchData.MatchIdentifier, pref);
                if (state is not null)
                    RemovePlayers(state);
            }

            // Clear internal state after players have been removed.
            Span<LvzObjectChange> clearChanges = stackalloc LvzObjectChange[Clear_MaxChanges];
            Span<LvzObjectToggle> clearToggles = stackalloc LvzObjectToggle[Clear_MaxToggles];
            foreach (StatboxPreference pref in new[] { StatboxPreference.Detailed, StatboxPreference.Simple, StatboxPreference.Off })
            {
                MatchLvzState? state = arenaData.TryGetMatch(matchData.MatchIdentifier, pref);
                if (state is null)
                    continue;

                state.Clear(clearChanges, out _, clearToggles, out _);
            }

            arenaData.RemoveMatch(matchData.MatchIdentifier);
        }

        private void RemovePlayers(MatchLvzState matchLvzState)
        {
            while (matchLvzState.Players.Count > 0)
            {
                foreach (Player player in matchLvzState.Players)
                {
                    // This removes the player from the collectuon, so the enumerator not usable after.
                    SetAndSendMatchLvz(player, null);
                    break;
                }
            }
        }

        private void Callback_TeamVersusMatchPlayerSubbed(IPlayerSlot slot, string? subOutPlayerName)
        {
            IMatchData matchData = slot.MatchData;
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            Span<LvzObjectChange> subChanges = stackalloc LvzObjectChange[StatBox_RefreshForSub_MaxChanges];
            Span<LvzObjectToggle> subToggles = stackalloc LvzObjectToggle[StatBox_RefreshForSub_MaxToggles];
            foreach (StatboxPreference pref in new[] { StatboxPreference.Detailed, StatboxPreference.Simple, StatboxPreference.Off })
            {
                MatchLvzState? state = arenaData.TryGetMatch(matchData.MatchIdentifier, pref);
                if (state is null || state.Players.Count == 0)
                    continue;

                // RefreshStatBoxForSub is a no-op for Off preference (HasStatbox = false).
                state.RefreshStatBoxForSub(slot, subChanges, out int changesWritten, subToggles, out int togglesWritten);

                foreach (Player player in state.Players)
                {
                    _lvzObjects.SetAndToggle(player, subChanges[..changesWritten], subToggles[..togglesWritten]);
                }
            }
        }

        private void Callback_TeamVersusMatchPlayerKilled(IPlayerSlot killedSlot, IPlayerSlot killerSlot, bool isKnockout)
        {
            IMatchData matchData = killedSlot.MatchData;
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            Span<LvzObjectChange> killChanges = stackalloc LvzObjectChange[RefreshForKill_MaxChanges];
            Span<LvzObjectToggle> killToggles = stackalloc LvzObjectToggle[RefreshForKill_MaxToggles];
            foreach (StatboxPreference pref in new[] { StatboxPreference.Detailed, StatboxPreference.Simple, StatboxPreference.Off })
            {
                MatchLvzState? state = arenaData.TryGetMatch(matchData.MatchIdentifier, pref);
                if (state is null || state.Players.Count == 0)
                    continue;

                state.RefreshForKill(killedSlot, killChanges, out int changesWritten, killToggles, out int togglesWritten);

                foreach (Player player in state.Players)
                {
                    _lvzObjects.SetAndToggle(player, killChanges[..changesWritten], killToggles[..togglesWritten]);
                }
            }
        }

        // For SIMPLE mode players, update kills and deaths in the statbox.
        // The TeamVersusMatch module only tracks data relevant to run a match (e.g. remaining lives left).
        // The TeamVersusStats module has stats such as kills, deaths, and much more.
        private void Callback_TeamVersusStatsPlayerKilled(IPlayerSlot killedSlot, IMemberStats killedStats, IPlayerSlot killerSlot, IMemberStats killerStats, bool isKnockout)
        {
            IMatchData matchData = killedSlot.MatchData;
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            MatchLvzState? simpleState = arenaData.TryGetMatch(matchData.MatchIdentifier, StatboxPreference.Simple);
            if (simpleState is null)
                return;

            Span<LvzObjectChange> changes = stackalloc LvzObjectChange[StatBox_RefreshStatsKill_MaxChanges];
            Span<LvzObjectToggle> toggles = stackalloc LvzObjectToggle[StatBox_RefreshStatsKill_MaxToggles];

            // Always update the state so it stays accurate for players who switch to Simple later.
            simpleState.RefreshForStatsKill(
                killedSlot, killedStats.Deaths,
                killerSlot, killerStats.Kills,
                changes, out int changesWritten,
                toggles, out int togglesWritten);

            if (simpleState.Players.Count == 0)
                return;

            changes = changes[..changesWritten];
            toggles = toggles[..togglesWritten];

            foreach (Player player in simpleState.Players)
            {
                _lvzObjects.SetAndToggle(player, changes, toggles);
            }
        }

        private void Callback_TeamVersusMatchPlayerItemsChanged(IPlayerSlot playerSlot, ItemChanges itemChanges)
        {
            IMatchData matchData = playerSlot.MatchData;
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            Span<LvzObjectChange> itemsChanges = stackalloc LvzObjectChange[StatBox_RefreshItems_MaxChanges];
            Span<LvzObjectToggle> itemsToggles = stackalloc LvzObjectToggle[StatBox_RefreshItems_MaxToggles];
            foreach (StatboxPreference pref in new[] { StatboxPreference.Detailed, StatboxPreference.Simple, StatboxPreference.Off })
            {
                MatchLvzState? state = arenaData.TryGetMatch(matchData.MatchIdentifier, pref);
                if (state is null || state.Players.Count == 0)
                    continue;

                // RefreshStatBoxItems is a no-op for Off (HasStatbox = false) or columns not configured for Repels/Rockets.
                state.RefreshStatBoxItems(playerSlot, itemChanges, itemsChanges, out int changesWritten, itemsToggles, out int togglesWritten);

                foreach (Player player in state.Players)
                {
                    _lvzObjects.SetAndToggle(player, itemsChanges[..changesWritten], itemsToggles[..togglesWritten]);
                }
            }
        }

        private void Callback_StatboxPreferenceChanged(Player player, StatboxPreference newPreference)
        {
            // Re-send the match LVZ for this player using their new preference.
            IMatchData? matchData = _matchFocus.GetFocusedMatch(player) as IMatchData;
            SetAndSendMatchLvz(player, matchData);
        }

        #endregion

        private static byte GetImageId(char c, Color color = Color.Neutral)
        {
            if (!char.IsAscii(c) || !char.IsBetween(c, ' ', '~'))
            {
                c = '?';
            }

            // ImageId aligns with the value of the character.
            byte imageId = (byte)c;

            // Digits can have a different color.
            if (color == Color.Red && char.IsAsciiDigit(c))
            {
                imageId = _redDigitImages[c];
            }
            else if (color == Color.Yellow && char.IsAsciiDigit(c))
            {
                imageId = _yellowDigitImages[c];
            }

            return imageId;
        }

        #region Helper types

        private enum Color
        {
            Neutral,
            Red,
            Yellow,
        }

        private enum StatboxColumn
        {
            Blank = 0,
            Lives,
            Kills,
            Deaths,
            Assists,
            Repels,
            Rockets,
        }

        private class ArenaLvzData : IResettable
        {
            /// <summary>
            /// Key = (match identifier, statbox preference). Stores all display states.
            /// </summary>
            public readonly Dictionary<(MatchIdentifier, StatboxPreference), MatchLvzState> BoxStates = new(TargetBoxesPerArena * 2);

            /// <summary>
            /// Lvz objects for statbox characters
            /// </summary>
            private readonly ObjectData[] _characterObjects = new ObjectData[StatBoxCharactersPerLine * StatBoxNumLines];

            /// <summary>
            /// Lvz objects for statbox characters
            /// </summary>
            public ReadOnlyMemory<ObjectData> CharacterObjects => _characterObjects;


            /// <summary>
            /// The default lvz state (nothing being displayed).
            /// </summary>
            public MatchLvzState? DefaultLvzState => _defaultLvzState;
            private MatchLvzState? _defaultLvzState;

            public void Initialize(Arena arena, ILvzObjects lvzObjects, int startingObjectId)
            {
                // For lvz toggles, no extra data is needed. We know the ObjectIds to toggle on and off.
                // For lvz changes, we need the ObjectData of an object.
                // The statbox is updated using lvz changes to switch ImageIds, so we need to know the ObjectData of each character in the statbox.

                // statbox characters
                for (int i = 0; i < _characterObjects.Length; i++)
                {
                    int lineIndex = i / StatBoxCharactersPerLine;
                    int charIndex = i % StatBoxCharactersPerLine;
                    short objectId = (short)(startingObjectId + (lineIndex * 100) + charIndex);

                    lvzObjects.TryGetDefaultInfo(arena, objectId, out _, out _characterObjects[i]);
                }

                _defaultLvzState = new MatchLvzState(this);
            }

            public MatchLvzState GetOrAddMatch(MatchIdentifier matchId, StatboxPreference preference)
            {
                var key = (matchId, preference);
                if (!BoxStates.TryGetValue(key, out MatchLvzState? state))
                {
                    state = new MatchLvzState(this, preference);
                    BoxStates.Add(key, state);
                }
                return state;
            }

            public MatchLvzState? TryGetMatch(MatchIdentifier matchId, StatboxPreference preference)
            {
                BoxStates.TryGetValue((matchId, preference), out MatchLvzState? state);
                return state;
            }

            public bool RemoveMatch(MatchIdentifier matchId)
            {
                bool removed = false;
                foreach (StatboxPreference pref in new[] { StatboxPreference.Detailed, StatboxPreference.Simple, StatboxPreference.Off })
                    removed |= BoxStates.Remove((matchId, pref), out _);
                return removed;
            }

            bool IResettable.TryReset()
            {
                BoxStates.Clear();
                Array.Clear(_characterObjects);
                _defaultLvzState = null;
                return true;
            }
        }

        /// <summary>
        /// The state of lvz objects for an individual match.
        /// </summary>
        private class MatchLvzState
        {
            //private readonly ArenaLvzData _arenaLvzData;

            private const string ExceptionMessage_InsufficientChangeBufferLength = "Not large enough to hold all possible changes.";
            private const string ExceptionMessage_InsufficientToggleBufferLength = "Not large enough to hold all possible toggles.";

            public readonly HashSet<Player> Players = new(Constants.TargetPlayerCount);

            #region Scoreboard data members

            private const short ScoreboardObjectId = 4040;
            private short? _scoreboard;

            // Timer
            private const short TimerMinutesTens0 = 4080;
            private const short TimerMinutesOnes0 = 4070;
            private const short TimerSecondsTens0 = 4060;
            private const short TimerSecondsOnes0 = 4058;
            private const short TimerSecondsOnesCountdownObjectId = 4059;            
            private TimerState _timerState;

            // Freqs
            private static readonly (short Thousands0, short Hundreds0, short Tens0, short Ones0)[] _freqObjectIds = [(4100, 4110, 4120, 4130), (4140, 4150, 4160, 4170)];
            private readonly FreqState[] _freqStates = new FreqState[2];

            // Scores
            private static readonly (short Tens0, short Ones0)[] _scoreObjectIds = [(4010, 4000), (4030, 4020)];
            private readonly ScoreState[] _scoreStates = new ScoreState[2];

            #endregion

            #region Statbox data members

            // Statbox header and frame
            private static readonly int[] _headerObjectIds = [0, 1, 2, 3];
            private readonly HashSet<short> _headerAndFrameEnabledObjects = new(StatBoxHeaderAndFramesObjectCount);


            // Statbox characters
            private readonly LvzState[] _characterObjects = new LvzState[StatBoxCharactersPerLine * StatBoxNumLines];
            private readonly Memory<LvzState>[] _lines = new Memory<LvzState>[StatBoxNumLines];

            // Statbox strikethroughs
            private readonly HashSet<short> _strikethoughEnabledObjects = new(StatBoxCharactersPerLine * StatBoxNumLines);

            private int _nameDisplayLength = 4;

            #endregion

            private IMatchData? _matchData;

            private readonly StatboxColumn _col0, _col1, _col2;
            private readonly StatboxPreference _preference;
            private bool HasStatbox => _preference != StatboxPreference.Off;
            public bool IsInitialized => _matchData is not null;

            private StatboxColumn GetColumn(int colIdx) => colIdx switch
            {
                0 => _col0,
                1 => _col1,
                2 => _col2,
                _ => StatboxColumn.Blank,
            };

            private int GetColumnIndex(StatboxColumn column)
            {
                if (_col0 == column) return 0;
                if (_col1 == column) return 1;
                if (_col2 == column) return 2;
                return -1;
            }

            private static short GetColumnInitValue(StatboxColumn column, IPlayerSlot slot) => column switch
            {
                StatboxColumn.Lives => (short)slot.Lives,
                StatboxColumn.Repels => slot.Repels,
                StatboxColumn.Rockets => slot.Rockets,
                _ => 0,
            };

            private static Color GetColumnColor(StatboxColumn column, byte value, int livesPerPlayer = 0) => column switch
            {
                StatboxColumn.Lives => value <= 1 ? Color.Red : Color.Yellow,
                StatboxColumn.Repels or StatboxColumn.Rockets => value == 0 ? Color.Red : Color.Yellow,
                StatboxColumn.Kills => Color.Yellow,
                StatboxColumn.Deaths => livesPerPlayer > 0 && value >= livesPerPlayer - 1 ? Color.Red : Color.Yellow,
                _ => value == 0 ? Color.Neutral : Color.Yellow,
            };

            public MatchLvzState(ArenaLvzData arenaLvzData, StatboxPreference preference = StatboxPreference.Detailed)
            {
                _preference = preference;
                (StatboxColumn col0, StatboxColumn col1, StatboxColumn col2) = preference switch
                {
                    StatboxPreference.Simple => (StatboxColumn.Blank, StatboxColumn.Kills, StatboxColumn.Deaths),
                    _ => (StatboxColumn.Lives, StatboxColumn.Repels, StatboxColumn.Rockets),
                };
                _col0 = col0;
                _col1 = col1;
                _col2 = col2;

                for (int i = 0; i < _characterObjects.Length; i++)
                {
                    _characterObjects[i] = new LvzState(arenaLvzData.CharacterObjects.Span[i]);
                }

                for (int lineIdx = 0; lineIdx < StatBoxNumLines; lineIdx++)
                {
                    _lines[lineIdx] = _characterObjects.AsMemory(lineIdx * StatBoxCharactersPerLine, StatBoxCharactersPerLine);
                }

            }

            public void Start(IMatchData matchData, Span<LvzObjectChange> changes, out int changesWritten, Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < Initialize_MaxChanges)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                if (toggles.Length < Initialize_MaxToggles)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                changesWritten = 0;
                togglesWritten = 0;

                if (_matchData is not null)
                    return;

                // TODO: maybe support other variants such as 2v2v2, for now just doing 2 teams from 1v1 up to 4v4
                if (matchData.Configuration.NumTeams != 2)
                    return;

                _matchData = matchData;

                // Show the scoreboard
                toggles[0] = new LvzObjectToggle(ScoreboardObjectId, true);
                toggles = toggles[1..];
                togglesWritten++;
                _scoreboard = ScoreboardObjectId;

                RefreshScoreboardTimer(matchData, ref toggles, ref togglesWritten);
                InitializeScoreboardFreqs(ref toggles, ref togglesWritten);
                InitializeScoreboardScores(ref toggles, ref togglesWritten);
                if (HasStatbox)
                    InitializeStatBox(matchData, ref changes, ref changesWritten, ref toggles, ref togglesWritten);

                void InitializeScoreboardFreqs(ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
                {
                    if (toggles.Length < Scoreboard_Freqs_MaxToggles)
                        throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                    for (int teamIdx = 0; teamIdx < _matchData.Teams.Count; teamIdx++)
                    {
                        RefreshFreq(_matchData.Teams[teamIdx].Freq, ref _freqStates[teamIdx], ref _freqObjectIds[teamIdx], ref toggles, ref togglesWritten);
                    }

                    void RefreshFreq(short freq, ref FreqState freqState, ref (short Thousands0, short Hundreds0, short Tens0, short Ones0) digitsObjectIds, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
                    {
                        // TODO: change this logic to not show leading zeros and align left/right depending on team?

                        int thousands = (freq / 1000) % 10;
                        int hundreds = (freq / 100) % 10;
                        int tens = (freq / 10) % 10;
                        int ones = freq % 10;

                        short thousandsObjectId = (short)(digitsObjectIds.Thousands0 + thousands);
                        short hundredsObjectId = (short)(digitsObjectIds.Hundreds0 + hundreds);
                        short tensObjectId = (short)(digitsObjectIds.Tens0 + tens);
                        short onesObjectId = (short)(digitsObjectIds.Ones0 + ones);

                        ToggleDigit(ref freqState.Thousands, thousandsObjectId, ref toggles, ref togglesWritten);
                        ToggleDigit(ref freqState.Hundreds, hundredsObjectId, ref toggles, ref togglesWritten);
                        ToggleDigit(ref freqState.Tens, tensObjectId, ref toggles, ref togglesWritten);
                        ToggleDigit(ref freqState.Ones, onesObjectId, ref toggles, ref togglesWritten);
                    }
                }

                void InitializeScoreboardScores(ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
                {
                    if (toggles.Length < Scoreboard_Score_MaxToggles)
                        throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                    for (int teamIdx = 0; teamIdx < _matchData.Teams.Count; teamIdx++)
                    {
                        RefreshScore(_matchData.Teams[teamIdx], ref _scoreStates[teamIdx], _scoreObjectIds[teamIdx].Tens0, _scoreObjectIds[teamIdx].Ones0, ref toggles, ref togglesWritten);
                    }
                }

                void InitializeStatBox(IMatchData matchData, ref Span<LvzObjectChange> changes, ref int changesWritten, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
                {
                    SetHeaderAndFrame(matchData, ref toggles, ref togglesWritten);

                    for (int teamIdx = 0; teamIdx < matchData.Teams.Count; teamIdx++)
                    {
                        ITeam team = matchData.Teams[teamIdx];
                        for (int slotIdx = 0; slotIdx < team.Slots.Count; slotIdx++)
                        {
                            IPlayerSlot slot = team.Slots[slotIdx];
                            SetName(slot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                            for (int c = 0; c < StatBox_NumColumns; c++)
                            {
                                StatboxColumn column = GetColumn(c);
                                if (column != StatboxColumn.Blank)
                                    SetColumn(slot, c, GetColumnInitValue(column, slot), ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                            }
                        }
                    }
                }
            }

            public void RefreshScoreboardTimer(IMatchData matchData, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                if (toggles.Length < Scoreboard_Timer_MaxToggles)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                if (matchData.Started is null || matchData.Configuration.TimeLimit is null)
                    return;

                DateTime now = DateTime.UtcNow;
                TimeSpan remaining = (matchData.Started.Value + matchData.Configuration.TimeLimit.Value) - now;
                if (remaining < TimeSpan.Zero && matchData.Configuration.OverTimeLimit is not null)
                    remaining = (matchData.Started.Value + matchData.Configuration.TimeLimit.Value + matchData.Configuration.OverTimeLimit.Value) - now;

                if (remaining < TimeSpan.Zero)
                    remaining = TimeSpan.Zero;

                if (remaining == TimeSpan.Zero)
                {
                    ToggleDigit(ref _timerState.MinutesTens, TimerMinutesTens0, ref toggles, ref togglesWritten);
                    ToggleDigit(ref _timerState.MinutesOnes, TimerMinutesOnes0, ref toggles, ref togglesWritten);
                    ToggleDigit(ref _timerState.SecondsTens, TimerSecondsTens0, ref toggles, ref togglesWritten);
                    ToggleDigit(ref _timerState.SecondsOnes, TimerSecondsOnes0, ref toggles, ref togglesWritten);
                }
                else
                {
                    int remainingSeconds = (int)remaining.TotalSeconds;

                    int minutes = (remainingSeconds / 60) % 100;
                    int seconds = (remainingSeconds % 60);

                    int minutesTens = (minutes / 10) % 10;
                    int minutesOnes = (minutes % 10);
                    int secondsTens = (seconds / 10);
                    // The ones position of seconds will count

                    short minutesTensObjectId = (short)(TimerMinutesTens0 + minutesTens);
                    short minutesOnesObjectId = (short)(TimerMinutesOnes0 + minutesOnes);
                    short secondsTensObjectId = (short)(TimerSecondsTens0 + secondsTens);

                    ToggleDigit(ref _timerState.MinutesTens, minutesTensObjectId, ref toggles, ref togglesWritten);
                    ToggleDigit(ref _timerState.MinutesOnes, minutesOnesObjectId, ref toggles, ref togglesWritten);
                    ToggleDigit(ref _timerState.SecondsTens, secondsTensObjectId, ref toggles, ref togglesWritten);

                    if (_timerState.SecondsOnes is not null && _timerState.SecondsOnes != TimerSecondsOnesCountdownObjectId)
                    {
                        toggles[0] = new(_timerState.SecondsOnes.Value, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                    }

                    // Always send the toggle on of the countdown so that it starts from 9 again.
                    _timerState.SecondsOnes = TimerSecondsOnesCountdownObjectId;
                    toggles[0] = new(TimerSecondsOnesCountdownObjectId, true);
                    toggles = toggles[1..];
                    togglesWritten++;
                }
            }

            public void RefreshStatBoxForSub(IPlayerSlot slot, Span<LvzObjectChange> changes, out int changesWritten, Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < StatBox_RefreshForSub_MaxChanges)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                if (toggles.Length < StatBox_RefreshForSub_MaxToggles)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                // objects for frames + player name for the slot + strikethroughs

                changesWritten = 0;
                togglesWritten = 0;

                if (!HasStatbox)
                    return;

                IMatchData matchData = slot.MatchData;
                if (_matchData != matchData)
                    return;

                // Refresh the header and frame in case the name width changed.
                SetHeaderAndFrame(matchData, ref toggles, ref togglesWritten);

                // Update the player name of the slot to the player that subbed in.
                SetName(slot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);

                // Reset all column values for the incoming player.
                // For stat columns (Kills/Deaths/Assists), this resets to 0; for slot-based columns (Lives/Repels/Rockets), it re-reads the slot.
                for (int c = 0; c < StatBox_NumColumns; c++)
                {
                    StatboxColumn column = GetColumn(c);
                    if (column != StatboxColumn.Blank)
                        SetColumn(slot, c, GetColumnInitValue(column, slot), ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                }

                if (GetColumnIndex(StatboxColumn.Lives) >= 0)
                {
                    // Refresh strikethroughs in case the name width changed.
                    for (int teamIdx = 0; teamIdx < matchData.Teams.Count; teamIdx++)
                    {
                        ITeam team = matchData.Teams[teamIdx];

                        for (int slotIdx = 0; slotIdx < team.Slots.Count; slotIdx++)
                        {
                            IPlayerSlot otherSlot = team.Slots[slotIdx];
                            SetStrikethrough(otherSlot, ref toggles, ref togglesWritten);
                        }
                    }
                }
            }

            public void RefreshForKill(IPlayerSlot killedSlot, Span<LvzObjectChange> changes, out int changesWritten, Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < RefreshForKill_MaxChanges)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                if (toggles.Length < RefreshForKill_MaxToggles)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                changesWritten = 0;
                togglesWritten = 0;

                RefreshScoreboardForKill(killedSlot, ref toggles, ref togglesWritten);
                RefreshStatBoxForKill(killedSlot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);

                void RefreshScoreboardForKill(IPlayerSlot killedSlot, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
                {
                    if (toggles.Length < Scoreboard_Score_MaxToggles)
                        throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(toggles));

                    IMatchData matchData = killedSlot.MatchData;

                    for (int teamIdx = 0; teamIdx < matchData.Teams.Count; teamIdx++)
                    {
                        RefreshScore(matchData.Teams[teamIdx], ref _scoreStates[teamIdx], _scoreObjectIds[teamIdx].Tens0, _scoreObjectIds[teamIdx].Ones0, ref toggles, ref togglesWritten);
                    }
                }

                void RefreshStatBoxForKill(IPlayerSlot killedSlot, ref Span<LvzObjectChange> changes, ref int changesWritten, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
                {
                    if (!HasStatbox)
                        return;

                    int livesColIdx = GetColumnIndex(StatboxColumn.Lives);
                    if (livesColIdx < 0)
                        return; // No lives column in this layout; K/D handled via RefreshForStatsKill.

                    if (changes.Length < StatBox_RefreshForKill_MaxChanges)
                        throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                    if (toggles.Length < StatBox_RefreshForKill_MaxToggles)
                        throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(toggles));

                    IMatchData matchData = killedSlot.MatchData;
                    if (_matchData != matchData)
                        return;

                    SetColumn(killedSlot, livesColIdx, (short)killedSlot.Lives, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                    SetStrikethrough(killedSlot, ref toggles, ref togglesWritten);

                    if (killedSlot.Lives == 0)
                    {
                        // Blank out remaining columns on knockdown.
                        for (int c = 0; c < StatBox_NumColumns; c++)
                        {
                            if (c == livesColIdx)
                                continue;
                            SetColumn(killedSlot, c, 0, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                        }
                    }
                }
            }

            public void RefreshStatBoxItems(IPlayerSlot slot, ItemChanges itemChanges, Span<LvzObjectChange> changes, out int changesWritten, Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < StatBox_RefreshItems_MaxChanges)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                if (toggles.Length < StatBox_RefreshItems_MaxToggles)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));
                
                changesWritten = 0;
                togglesWritten = 0;

                if (!HasStatbox)
                    return;

                if (_matchData != slot.MatchData)
                    return;

                for (int c = 0; c < StatBox_NumColumns; c++)
                {
                    StatboxColumn column = GetColumn(c);
                    if (column == StatboxColumn.Repels && (itemChanges & ItemChanges.Repels) != 0)
                        SetColumn(slot, c, slot.Repels, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                    else if (column == StatboxColumn.Rockets && (itemChanges & ItemChanges.Rockets) != 0)
                        SetColumn(slot, c, slot.Rockets, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                }
            }

            /// <summary>
            /// Updates the kills and deaths for SIMPLE mode.
            /// Called from <see cref="TeamVersusStatsPlayerKilledCallback"/> with up-to-date stats.
            /// </summary>
            public void RefreshForStatsKill(
                IPlayerSlot killedSlot, short killedDeaths,
                IPlayerSlot killerSlot, short killerKills,
                Span<LvzObjectChange> changes, out int changesWritten,
                Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < StatBox_RefreshStatsKill_MaxChanges)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                if (toggles.Length < StatBox_RefreshStatsKill_MaxToggles)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                changesWritten = 0;
                togglesWritten = 0;

                int killsColIdx = GetColumnIndex(StatboxColumn.Kills);
                int deathsColIdx = GetColumnIndex(StatboxColumn.Deaths);

                if (killsColIdx < 0 && deathsColIdx < 0)
                    return; // No kills or deaths columns configured.

                if (killsColIdx >= 0 && _matchData == killerSlot.MatchData)
                    SetColumn(killerSlot, killsColIdx, killerKills, ref changes, ref changesWritten, ref toggles, ref togglesWritten);

                if (deathsColIdx >= 0 && _matchData == killedSlot.MatchData)
                    SetColumn(killedSlot, deathsColIdx, killedDeaths, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
            }

            public void Clear(Span<LvzObjectChange> changes, out int changesWritten, Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < Clear_MaxChanges)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                if (toggles.Length < Clear_MaxToggles)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(toggles));

                _matchData = null;

                changesWritten = 0;
                togglesWritten = 0;

                toggles[0] = new LvzObjectToggle(ScoreboardObjectId, false);
                toggles = toggles[1..];
                togglesWritten++;
                _scoreboard = null;

                // scoreboard timer
                if (_timerState.MinutesTens is not null)
                {
                    toggles[0] = new LvzObjectToggle(_timerState.MinutesTens.Value, false);
                    toggles = toggles[1..];
                    togglesWritten++;
                    _timerState.MinutesTens = null;
                }

                if (_timerState.MinutesOnes is not null)
                {
                    toggles[0] = new LvzObjectToggle(_timerState.MinutesOnes.Value, false);
                    toggles = toggles[1..];
                    togglesWritten++;
                    _timerState.MinutesOnes = null;
                }

                if (_timerState.SecondsTens is not null)
                {
                    toggles[0] = new LvzObjectToggle(_timerState.SecondsTens.Value, false);
                    toggles = toggles[1..];
                    togglesWritten++;
                    _timerState.SecondsTens = null;
                }

                if (_timerState.SecondsOnes is not null)
                {
                    toggles[0] = new LvzObjectToggle(_timerState.SecondsOnes.Value, false);
                    toggles = toggles[1..];
                    togglesWritten++;
                    _timerState.SecondsOnes = null;
                }

                // scoreboard freqs
                for (int i = 0; i < _freqStates.Length; i++)
                {
                    ref FreqState freqState = ref _freqStates[i];

                    if (freqState.Thousands is not null)
                    {
                        toggles[0] = new LvzObjectToggle(freqState.Thousands.Value, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                        freqState.Thousands = null;
                    }

                    if (freqState.Hundreds is not null)
                    {
                        toggles[0] = new LvzObjectToggle(freqState.Hundreds.Value, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                        freqState.Hundreds = null;
                    }

                    if (freqState.Tens is not null)
                    {
                        toggles[0] = new LvzObjectToggle(freqState.Tens.Value, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                        freqState.Tens = null;
                    }

                    if (freqState.Ones is not null)
                    {
                        toggles[0] = new LvzObjectToggle(freqState.Ones.Value, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                        freqState.Ones = null;
                    }
                }

                // scoreboard scores
                for (int i = 0; i < _scoreStates.Length; i++)
                {
                    ref ScoreState scoreState = ref _scoreStates[i];

                    if (scoreState.Tens is not null)
                    {
                        toggles[0] = new LvzObjectToggle(scoreState.Tens.Value, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                        scoreState.Tens = null;
                    }

                    if (scoreState.Ones is not null)
                    {
                        toggles[0] = new LvzObjectToggle(scoreState.Ones.Value, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                        scoreState.Ones = null;
                    }
                }

                if (HasStatbox)
                {
                    // statbox header and frame
                    foreach (short objectId in _headerAndFrameEnabledObjects)
                    {
                        toggles[0] = new LvzObjectToggle(objectId, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                    }

                    _headerAndFrameEnabledObjects.Clear();

                    // statbox characters
                    for (int i = 0; i < _characterObjects.Length; i++)
                    {
                        ref LvzState state = ref _characterObjects[i];

                        if (state.IsEnabled)
                        {
                            state.IsEnabled = false;

                            toggles[0] = new LvzObjectToggle(state.Current.Id, false);
                            toggles = toggles[1..];
                            togglesWritten++;
                        }

                        if (state.Current.ImageId != state.Default.ImageId)
                        {
                            state.Current.ImageId = state.Default.ImageId;

                            changes[0] = new LvzObjectChange(new ObjectChange() { Image = true }, state.Current);
                            changes = changes[1..];
                            changesWritten++;
                        }
                    }

                    // statbox strikethroughs
                    foreach (short objectId in _strikethoughEnabledObjects)
                    {
                        toggles[0] = new LvzObjectToggle(objectId, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                    }

                    _strikethoughEnabledObjects.Clear();
                }
            }

            /// <summary>
            /// Gets the differences to apply when swapping from one match to another.
            /// </summary>
            /// <param name="from"></param>
            /// <param name="changes"></param>
            /// <param name="changesWritten"></param>
            /// <param name="toggles"></param>
            /// <param name="togglesWritten"></param>
            public static void GetDifferences(MatchLvzState from, MatchLvzState to, Span<LvzObjectChange> changes, out int changesWritten, Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < Initialize_MaxChanges)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                if (toggles.Length < GetDifferences_MaxToggles)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                changesWritten = 0;
                togglesWritten = 0;

                // scoreboard
                ToggleDifference(from._scoreboard, to._scoreboard, ref toggles, ref togglesWritten);

                // scoreboard timer
                ToggleDifference(from._timerState.MinutesTens, to._timerState.MinutesTens, ref toggles, ref togglesWritten);
                ToggleDifference(from._timerState.MinutesOnes, to._timerState.MinutesOnes, ref toggles, ref togglesWritten);
                ToggleDifference(from._timerState.SecondsTens, to._timerState.SecondsTens, ref toggles, ref togglesWritten);
                ToggleDifference(from._timerState.SecondsOnes, to._timerState.SecondsOnes, ref toggles, ref togglesWritten);

                // scoreboard freqs
                for (int i = 0; i < from._freqStates.Length; i++)
                {
                    ToggleDifference(from._freqStates[i].Thousands, to._freqStates[i].Thousands, ref toggles, ref togglesWritten);
                    ToggleDifference(from._freqStates[i].Hundreds, to._freqStates[i].Hundreds, ref toggles, ref togglesWritten);
                    ToggleDifference(from._freqStates[i].Tens, to._freqStates[i].Tens, ref toggles, ref togglesWritten);
                    ToggleDifference(from._freqStates[i].Ones, to._freqStates[i].Ones, ref toggles, ref togglesWritten);
                }

                // scoreboard score
                for (int i = 0; i < from._scoreStates.Length; i++)
                {
                    ToggleDifference(from._scoreStates[i].Tens, to._scoreStates[i].Tens, ref toggles, ref togglesWritten);
                    ToggleDifference(from._scoreStates[i].Ones, to._scoreStates[i].Ones, ref toggles, ref togglesWritten);
                }

                // statbox header and frame
                ToggleDifferences(from._headerAndFrameEnabledObjects, to._headerAndFrameEnabledObjects, ref toggles, ref togglesWritten);

                // statbox characters
                for (int i = 0; i < from._characterObjects.Length; i++)
                {
                    LvzState fromState = from._characterObjects[i];
                    LvzState toState = to._characterObjects[i];

                    if (toState.IsEnabled)
                    {
                        ObjectChange change = ObjectData.CalculateChange(ref fromState.Current, ref toState.Current);
                        if (change.HasChange)
                        {
                            changes[0] = new LvzObjectChange(change, toState.Current);
                            changes = changes[1..];
                            changesWritten++;
                        }
                    }

                    if (fromState.IsEnabled && !toState.IsEnabled)
                    {
                        toggles[0] = new LvzObjectToggle(fromState.Default.Id, false);
                        toggles = toggles[1..];
                        togglesWritten++;

                        // Also reset the ImageId back to the target's image (typically the LVZ default).
                        // Without this, the client retains the old ImageId for the disabled object.
                        // If a future match then enables that position with an image matching the
                        // assumed-default, no change packet would be sent, causing the stale image
                        // from the previous match to be displayed when the object is toggled on.
                        if (fromState.Current.ImageId != toState.Current.ImageId)
                        {
                            changes[0] = new LvzObjectChange(new ObjectChange { Image = true }, toState.Current);
                            changes = changes[1..];
                            changesWritten++;
                        }
                    }

                    if (!fromState.IsEnabled && toState.IsEnabled)
                    {
                        toggles[0] = new LvzObjectToggle(fromState.Default.Id, true);
                        toggles = toggles[1..];
                        togglesWritten++;
                    }
                }

                // statbox strikethroughs
                ToggleDifferences(from._strikethoughEnabledObjects, to._strikethoughEnabledObjects, ref toggles, ref togglesWritten);

                static void ToggleDifference(short? fromState, short? toState, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
                {
                    if (fromState != toState)
                    {
                        if (fromState is not null)
                        {
                            toggles[0] = new LvzObjectToggle(fromState.Value, false);
                            toggles = toggles[1..];
                            togglesWritten++;
                        }

                        if (toState is not null)
                        {
                            toggles[0] = new LvzObjectToggle(toState.Value, true);
                            toggles = toggles[1..];
                            togglesWritten++;
                        }
                    }
                }

                static void ToggleDifferences(HashSet<short> fromEnabledObjects, HashSet<short> toEnabledObjects, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
                {
                    foreach (short objectId in fromEnabledObjects)
                    {
                        if (!toEnabledObjects.Contains(objectId))
                        {
                            toggles[0] = new LvzObjectToggle(objectId, false);
                            toggles = toggles[1..];
                            togglesWritten++;
                        }
                    }

                    foreach (short objectId in toEnabledObjects)
                    {
                        if (!fromEnabledObjects.Contains(objectId))
                        {
                            toggles[0] = new LvzObjectToggle(objectId, true);
                            toggles = toggles[1..];
                            togglesWritten++;
                        }
                    }
                }
            }

            private static void RefreshScore(ITeam team, ref ScoreState scoreState, short tens0, short ones0, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                if (toggles.Length < 2)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                short score = team.Score;

                int tens = ((score / 10) % 10);
                int ones = (score % 10);

                short tensObjectId = (short)(tens0 + tens);
                short onesObjectId = (short)(ones0 + ones);

                ToggleDigit(ref scoreState.Tens, tensObjectId, ref toggles, ref togglesWritten);
                ToggleDigit(ref scoreState.Ones, onesObjectId, ref toggles, ref togglesWritten);
            }

            private static void ToggleDigit(ref short? state, short valueObjectId, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                if (state != valueObjectId)
                {
                    if (state is not null)
                    {
                        toggles[0] = new LvzObjectToggle(state.Value, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                    }

                    state = valueObjectId;
                    toggles[0] = new LvzObjectToggle(valueObjectId, true);
                    toggles = toggles[1..];
                    togglesWritten++;
                }
            }

            private static int GetLineIdx(IPlayerSlot slot)
            {
                int teamIdx = slot.Team.TeamIdx;
                int slotIdx = slot.SlotIdx;

                // TODO: This is assumes 2 teams, add support for more? like a 2v2v2?
                //int playerPerTeam = slot.MatchData.Configuration.PlayersPerTeam;

                if (teamIdx == 0)
                {
                    return 3 - slotIdx;
                }
                else
                {
                    return 4 + slotIdx;
                }
            }

            private void SetHeaderAndFrame(IMatchData matchData, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                if (toggles.Length < StatBox_RefreshHeaderAndFrame_MaxToggles)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                // TODO: adjust lvz frames according to # of teams
                //int numTeams = matchData.Configuration.NumTeams;

                int playersPerTeam = matchData.Configuration.PlayersPerTeam;

                //
                // Header
                //

                // Header labels and icons
                if (_preference == StatboxPreference.Simple)
                {
                    // Object 0 (Name label): ON; Objects 1-3 (Detailed icons): OFF; Objects 4-5 (K, D icons): ON
                    ToggleObject(0, true, ref toggles, ref togglesWritten);
                    ToggleObject(1, false, ref toggles, ref togglesWritten);
                    ToggleObject(2, false, ref toggles, ref togglesWritten);
                    ToggleObject(3, false, ref toggles, ref togglesWritten);
                    ToggleObject(4, true, ref toggles, ref togglesWritten);
                    ToggleObject(5, true, ref toggles, ref togglesWritten);
                }
                else
                {
                    // Objects 0-3 (all header labels/icons): ON; Objects 4-5 (Simple K/D icons): OFF
                    ToggleObjects(0, 3, 0, ref toggles, ref togglesWritten);
                    ToggleObject(4, false, ref toggles, ref togglesWritten);
                    ToggleObject(5, false, ref toggles, ref togglesWritten);
                }

                // Adjust the width of the frames based on the length of the longest name.
                RefreshNameDisplayLength(matchData);
                short skip = (short)(StatBoxNameLength - _nameDisplayLength + 2);

                // Header top frame
                ToggleObjects(100, 131, skip, ref toggles, ref togglesWritten);

                // Header bottom frame
                ToggleObjects(200, 231, skip, ref toggles, ref togglesWritten);

                //
                // Frames
                //

                switch (playersPerTeam)
                {
                    case 1:
                        // 1v1 top frame
                        ToggleObjects(300, 331, skip, ref toggles, ref togglesWritten);

                        // 1v1 bottom frame
                        ToggleObjects(400, 431, skip, ref toggles, ref togglesWritten);
                        break;

                    case 2:
                        // 2v2 top frame
                        ToggleObjects(500, 531, skip, ref toggles, ref togglesWritten);

                        // 2v2 bottom frame
                        ToggleObjects(600, 631, skip, ref toggles, ref togglesWritten);
                        break;

                    case 3:
                        // 3v3 top frame
                        ToggleObjects(700, 731, skip, ref toggles, ref togglesWritten);

                        // 3v3 bottom frame
                        ToggleObjects(800, 831, skip, ref toggles, ref togglesWritten);
                        break;

                    case 4:
                        // 4v4 top frame
                        ToggleObjects(900, 931, skip, ref toggles, ref togglesWritten);

                        // 4v4 bottom frame
                        ToggleObjects(1000, 1031, skip, ref toggles, ref togglesWritten);
                        break;

                    default:
                        break;
                }

                void RefreshNameDisplayLength(IMatchData matchData)
                {
                    _nameDisplayLength = 4; // minimum

                    for (int teamIdx = 0; teamIdx < matchData.Teams.Count; teamIdx++)
                    {
                        ITeam team = matchData.Teams[teamIdx];

                        for (int slotIdx = 0; slotIdx < team.Slots.Count; slotIdx++)
                        {
                            IPlayerSlot slot = team.Slots[slotIdx];
                            if (slot.PlayerName is null)
                                continue;

                            if (slot.PlayerName.Length > _nameDisplayLength)
                                _nameDisplayLength = slot.PlayerName.Length;
                        }
                    }

                    if (_nameDisplayLength > StatBoxNameLength)
                        _nameDisplayLength = StatBoxNameLength;
                }

                void ToggleObject(short objectId, bool enable, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
                {
                    bool isEnabled = _headerAndFrameEnabledObjects.Contains(objectId);
                    if (enable == isEnabled)
                        return;

                    toggles[0] = new(objectId, enable);
                    toggles = toggles[1..];
                    togglesWritten++;

                    if (enable)
                        _headerAndFrameEnabledObjects.Add(objectId);
                    else
                        _headerAndFrameEnabledObjects.Remove(objectId);
                }

                void ToggleObjects(short fromIds, short toIds, int skip, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
                {
                    short enableStart = (short)(fromIds + skip);

                    for (short objectId = fromIds; objectId <= toIds; objectId++)
                    {
                        bool enable = objectId >= enableStart;
                        bool isEnabled = _headerAndFrameEnabledObjects.Contains(objectId);

                        if (enable && isEnabled)
                            continue; // already enabled

                        if (!enable && !isEnabled)
                            continue; // already disabled

                        toggles[0] = new(objectId, enable);
                        toggles = toggles[1..];
                        togglesWritten++;

                        if (enable)
                            _headerAndFrameEnabledObjects.Add(objectId);
                        else
                            _headerAndFrameEnabledObjects.Remove(objectId);
                    }
                }
            }


            private void SetName(IPlayerSlot slot, ref Span<LvzObjectChange> changes, ref int changesWritten, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                if (changes.Length < StatBox_NameChange_MaxChanges)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                if (toggles.Length < StatBox_NameChange_MaxToggles)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                if (_matchData != slot.MatchData)
                    return;

                Span<LvzState> nameStates = SliceName(_lines[GetLineIdx(slot)]).Span;
                Span<char> nameChars = stackalloc char[nameStates.Length];
                ReadOnlySpan<char> name = slot.PlayerName;

                if (name.Length > nameChars.Length)
                    name = name[..nameChars.Length];

                // Right aligned, prepended with spaces.
                for (int i = nameChars.Length - 1; i >= 0; i--)
                {
                    char c;
                    if (name.Length > 0)
                    {
                        c = name[^1];
                        name = name[..^1];
                    }
                    else
                    {
                        c = ' ';
                    }

                    nameChars[i] = c;
                }

                // Characters
                for (int i = 0; i < nameStates.Length && i < nameChars.Length; i++)
                {
                    if (SetChar(ref nameStates[i], nameChars[i], out ObjectChange change, out bool toggled))
                    {
                        if (change.HasChange)
                        {
                            changes[0] = new LvzObjectChange(change, nameStates[i].Current);
                            changes = changes[1..];
                            changesWritten++;
                        }

                        if (toggled)
                        {
                            toggles[0] = new LvzObjectToggle(nameStates[i].Current.Id, nameStates[i].IsEnabled);
                            toggles = toggles[1..];
                            togglesWritten++;
                        }
                    }
                }
            }

            private void SetStrikethrough(IPlayerSlot slot, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                Memory<LvzState> lineStates = _lines[GetLineIdx(slot)];
                bool isKnockedOut = slot.Lives == 0;
                short skip = (short)(StatBoxNameLength - _nameDisplayLength + 3);
                short startingObjectId = (short)(lineStates.Span[0].Default.Id + 1000); // strikethrough objects are +1000 the corresponding character object

                for (short i = 0; i < lineStates.Length; i++)
                {
                    short objectId = (short)(startingObjectId + i);
                    bool enable = isKnockedOut && i >= skip;
                    bool isEnabled = _strikethoughEnabledObjects.Contains(objectId);

                    if (enable && isEnabled)
                        continue; // already enabled

                    if (!enable && !isEnabled)
                        continue; // already disabled

                    toggles[0] = new(objectId, enable);
                    toggles = toggles[1..];
                    togglesWritten++;

                    if (enable)
                        _strikethoughEnabledObjects.Add(objectId);
                    else
                        _strikethoughEnabledObjects.Remove(objectId);
                }
            }

            private void SetColumn(IPlayerSlot slot, int colIdx, short value, ref Span<LvzObjectChange> changes, ref int changesWritten, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                StatboxColumn column = GetColumn(colIdx);
                if (column == StatboxColumn.Blank)
                    return;

                if (changes.Length < 2)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                if (toggles.Length < 2)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                if (_matchData != slot.MatchData)
                    return;

                if (slot.Lives == 0 && _preference == StatboxPreference.Detailed)
                {
                    // Knocked-out players show blank columns in detailed mode.
                    Span<LvzState> charStates = SliceColumn(_lines[GetLineIdx(slot)], colIdx).Span;
                    for (int i = 0; i < charStates.Length; i++)
                    {
                        if (ClearChar(ref charStates[i], out _, out bool toggled) && toggled)
                        {
                            toggles[0] = new LvzObjectToggle(charStates[i].Current.Id, false);
                            toggles = toggles[1..];
                            togglesWritten++;
                        }
                    }
                    return;
                }

                byte cappedValue = (byte)Math.Min((int)value, 99);
                Color color = GetColumnColor(column, cappedValue, _matchData.Configuration.LivesPerPlayer);
                Span<LvzState> charStates2 = SliceColumn(_lines[GetLineIdx(slot)], colIdx).Span;
                Set2DigitNumber(cappedValue, color, charStates2, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
            }

            private static void Set2DigitNumber(byte value, Color color, Span<LvzState> charStates, ref Span<LvzObjectChange> changes, ref int changesWritten, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                ArgumentOutOfRangeException.ThrowIfNotEqual(charStates.Length, 2, nameof(charStates));

                if (changes.Length < 2)
                    throw new ArgumentException(ExceptionMessage_InsufficientChangeBufferLength, nameof(changes));

                if (toggles.Length < 2)
                    throw new ArgumentException(ExceptionMessage_InsufficientToggleBufferLength, nameof(toggles));

                Span<char> chars = stackalloc char[2];
                if (!chars.TryWrite($"{value,2}", out _)) // right aligned
                {
                    chars[0] = ' ';
                    chars[1] = '*';
                }

                for (int i = 0; i < 2; i++)
                {
                    if (char.IsDigit(chars[i]))
                    {
                        if (SetDigit(ref charStates[i], chars[i], color, out ObjectChange change, out bool toggled))
                        {
                            if (change.HasChange)
                            {
                                changes[0] = new LvzObjectChange(change, charStates[i].Current);
                                changes = changes[1..];
                                changesWritten++;
                            }

                            if (toggled)
                            {
                                toggles[0] = new LvzObjectToggle(charStates[i].Current.Id, charStates[i].IsEnabled);
                                toggles = toggles[1..];
                                togglesWritten++;
                            }
                        }
                    }
                    else if (SetChar(ref charStates[i], chars[i], out ObjectChange change, out bool toggled))
                    {
                        if (change.HasChange)
                        {
                            changes[0] = new LvzObjectChange(change, charStates[i].Current);
                            changes = changes[1..];
                            changesWritten++;
                        }

                        if (toggled)
                        {
                            toggles[0] = new LvzObjectToggle(charStates[i].Current.Id, charStates[i].IsEnabled);
                            toggles = toggles[1..];
                            togglesWritten++;
                        }
                    }
                }
            }

            private static bool SetDigit(ref LvzState charState, char c, Color color, out ObjectChange change, out bool toggled)
            {
                if (c == ' ')
                {
                    return ClearChar(ref charState, out change, out toggled);
                }
                else
                {
                    Dictionary<char, byte> imageDictionary = color switch
                    {
                        Color.Red => _redDigitImages,
                        Color.Yellow => _yellowDigitImages,
                        _ => _neutralDigitImages
                    };

                    if (!imageDictionary.TryGetValue(c, out byte imageId))
                    {
                        return ClearChar(ref charState, out change, out toggled);
                    }

                    bool altered = false;

                    if (!charState.IsEnabled)
                    {
                        charState.IsEnabled = true;

                        toggled = true;
                        altered = true;
                    }
                    else
                    {
                        toggled = false;
                    }

                    if (charState.Current.ImageId != imageId)
                    {
                        charState.Current.ImageId = imageId;

                        change = new ObjectChange { Image = true };
                        altered = true;
                    }
                    else
                    {
                        change = ObjectChange.None;
                    }

                    return altered;
                }
            }

            private static bool ClearChar(ref LvzState charState, out ObjectChange change, out bool toggled)
            {
                if (charState.IsEnabled)
                {
                    charState.IsEnabled = false;

                    change = ObjectChange.None;
                    toggled = true;
                    return true;
                }
                else
                {
                    change = ObjectChange.None;
                    toggled = false;
                    return false;
                }
            }

            private static bool SetChar(ref LvzState charState, char c, out ObjectChange change, out bool toggled)
            {
                if (c == ' ')
                {
                    if (charState.IsEnabled)
                    {
                        charState.IsEnabled = false;

                        change = ObjectChange.None;
                        toggled = true;
                        return true;
                    }
                    else
                    {
                        change = ObjectChange.None;
                        toggled = false;
                        return false;
                    }
                }
                else
                {
                    byte newImage = GetImageId(c);
                    bool altered = false;

                    if (!charState.IsEnabled)
                    {
                        charState.IsEnabled = true;

                        toggled = true;
                        altered = true;
                    }
                    else
                    {
                        toggled = false;
                    }

                    if (charState.Current.ImageId != newImage)
                    {
                        charState.Current.ImageId = newImage;

                        change = new ObjectChange { Image = true };
                        altered = true;
                    }
                    else
                    {
                        change = ObjectChange.None;
                    }

                    return altered;
                }
            }

            private static Memory<LvzState> SliceName(Memory<LvzState> line)
            {
                return line.Slice(3, 20);
            }

            private static Memory<LvzState> SliceColumn(Memory<LvzState> line, int colIdx) => colIdx switch
            {
                0 => line.Slice(24, 2),
                1 => line.Slice(27, 2),
                2 => line.Slice(30, 2),
                _ => Memory<LvzState>.Empty,
            };

            private struct TimerState
            {
                public short? MinutesTens;
                public short? MinutesOnes;

                public short? SecondsTens;
                public short? SecondsOnes;
            }

            private struct FreqState
            {
                public short? Thousands;
                public short? Hundreds;
                public short? Tens;
                public short? Ones;
            }

            private struct ScoreState
            {
                public short? Tens;
                public short? Ones;
            }
        }

        private struct LvzState
        {
            public bool IsEnabled;
            public readonly ObjectData Default;
            public ObjectData Current;

            public LvzState(ObjectData defaultState)
            {
                IsEnabled = false;
                Default = defaultState;
                Current = Default;
            }
        }

        private class PlayerData : IResettable
        {
            public MatchLvzState? CurrentMatch;

            // TODO: delay (throttle) switching to a different match's lvz.
            // When a player switches to a different match. Set this, and clear/start a timer that will process it by sending differences.
            //public MatchLvzState? SwitchToMatch;

            bool IResettable.TryReset()
            {
                CurrentMatch = null;
                return true;
            }
        }

        #endregion
    }
}
