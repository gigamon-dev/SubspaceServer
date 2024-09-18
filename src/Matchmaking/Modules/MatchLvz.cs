using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module for controlling lvz objects for matches.
    /// This includes the statbox, scoreboard, and timer.
    /// </summary>
    public class MatchLvz(
        IConfigManager configManager,
        ILvzObjects lvzObjects) : IModule, IArenaAttachableModule
    {
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
        private const int StatBoxObjectsPerHeader = 4;

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

        private const int StatBox_Initialize_MaxChanges = (StatBoxNumLines * (StatBox_NameChange_MaxChanges + StatBox_SetLives_MaxChanges + StatBox_SetRepels_MaxChanges + StatBox_SetRockets_MaxChanges));
        private const int StatBox_Initialize_MaxToggles = StatBox_RefreshHeaderAndFrame_MaxToggles + (StatBoxNumLines * (StatBox_NameChange_MaxToggles + StatBox_SetLives_MaxToggles + StatBox_SetRepels_MaxToggles + StatBox_SetRockets_MaxToggles));

        private const int StatBox_Clear_MaxToggles = StatBox_RefreshHeaderAndFrame_MaxToggles + StatBoxCharacterObjectCount + StatBoxStrikethroughObjectsCount;

        private const int StatBox_RefreshForSub_MaxChanges = StatBox_NameChange_MaxChanges;
        private const int StatBox_RefreshForSub_MaxToggles = StatBox_RefreshHeaderAndFrame_MaxToggles + StatBox_NameChange_MaxToggles + StatBoxStrikethroughObjectsCount;

        private const int StatBox_RefreshForKill_MaxChanges = StatBoxObjectsPerNumericValue; // remaining lives
        private const int StatBox_RefreshForKill_MaxToggles = StatBoxObjectsPerNumericValue + StatBoxStrikethroughObjectsPerLine; // remaining lives + strikethrough (for knockout)

        private const int StatBox_RefreshHeaderAndFrame_MaxToggles = (StatBoxMaxFrames * StatBoxObjectsPerFrame) + StatBoxObjectsPerHeader;

        private const int StatBox_NameChange_MaxChanges = StatBoxNameLength;
        private const int StatBox_NameChange_MaxToggles = StatBoxNameLength;

        private const int StatBox_SetLives_MaxChanges = StatBoxObjectsPerNumericValue;
        private const int StatBox_SetLives_MaxToggles = StatBoxObjectsPerNumericValue;

        private const int StatBox_SetRepels_MaxChanges = StatBoxObjectsPerNumericValue;
        private const int StatBox_SetRepels_MaxToggles = StatBoxObjectsPerNumericValue;

        private const int StatBox_SetRockets_MaxChanges = StatBoxObjectsPerNumericValue;
        private const int StatBox_SetRockets_MaxToggles = StatBoxObjectsPerNumericValue;

        private const int StatBox_RefreshItems_MaxChanges = StatBox_SetRepels_MaxChanges + StatBox_SetRockets_MaxChanges;
        private const int StatBox_RefreshItems_MaxToggles = StatBox_SetRepels_MaxToggles + StatBox_SetRockets_MaxToggles;

        #endregion

        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILvzObjects _lvzObjects = lvzObjects ?? throw new ArgumentNullException(nameof(lvzObjects));

        private readonly Dictionary<Arena, ArenaLvzData> _arenaDataDictionary = new(Constants.TargetArenaCount);

        private const int TargetBoxesPerArena = 16;
        private static readonly DefaultObjectPool<ArenaLvzData> s_arenaDataPool = new(new DefaultPooledObjectPolicy<ArenaLvzData>(), Constants.TargetArenaCount);

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
            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            _arenaDataDictionary.Add(arena, s_arenaDataPool.Get());

            ArenaActionCallback.Register(arena, Callback_ArenaAction);
            PlayerActionCallback.Register(arena, Callback_PlayerAction);

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

                // TODO: Add a setting to control which information to show on the statbox: remaining lives, kills, deaths, repels, rockets, etc...
                //_configManager.GetEnum<StatBoxDisplay>(arena.Cfg!, "SS.Matchmaking.MatchLvz", "StatBoxDisplayMode", StatBoxDisplay.Lives | StatBoxDisplay.Repels | StatBoxDisplay.Rockets);

                arenaData.Initialize(arena, _lvzObjects, statBoxStartingObjectId);
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterGame)
            {
                if (arena is null || !_arenaDataDictionary.TryGetValue(arena!, out ArenaLvzData? arenaData))
                    return;

                // TODO: add logic to track which match (box) a player is a participant in or spectating
                // Include a delay to refresh lvz when they switch between matches (help limit bandwidth use)
                // Perhaps add another module, MatchFocus, to manage which box a player is focused on?
            }
        }

        private void Callback_TeamVersusMatchStarted(IMatchData matchData)
        {
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            var matchLvzState = arenaData.GetOrAddMatch(matchData.MatchIdentifier.BoxIdx);

            Span<LvzObjectChange> changes = stackalloc LvzObjectChange[StatBox_Initialize_MaxChanges];
            Span<LvzObjectToggle> toggles = stackalloc LvzObjectToggle[StatBox_Initialize_MaxToggles];

            // Display statbox
            matchLvzState.InitializeStatBox(matchData, changes, out int changesWritten, toggles, out int togglesWritten);
            changes = changes[..changesWritten];
            toggles = toggles[..togglesWritten];

            // TODO: target players participating and anyone spectating them instead of the whole arena
            _lvzObjects.SetAndToggle(arena, changes, toggles);
        }

        private void Callback_TeamVersusMatchEnded(IMatchData matchData, MatchEndReason reason, ITeam? winnerTeam)
        {
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            var matchLvzState = arenaData.GetOrAddMatch(matchData.MatchIdentifier.BoxIdx);

            Span<LvzObjectToggle> toggles = stackalloc LvzObjectToggle[StatBox_Clear_MaxToggles];

            // Hide statbox
            matchLvzState.ClearStatBox(toggles, out int togglesWritten);

            toggles = toggles[..togglesWritten];

            // TODO: target players participating and anyone spectating them instead of the whole arena
            _lvzObjects.Toggle(arena, toggles);
        }

        private void Callback_TeamVersusMatchPlayerSubbed(IPlayerSlot slot, string? subOutPlayerName)
        {
            IMatchData matchData = slot.MatchData;
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            var matchLvzState = arenaData.GetOrAddMatch(matchData.MatchIdentifier.BoxIdx);

            Span<LvzObjectChange> changes = stackalloc LvzObjectChange[StatBox_RefreshForSub_MaxChanges];
            Span<LvzObjectToggle> toggles = stackalloc LvzObjectToggle[StatBox_RefreshForSub_MaxToggles];

            // Update player name in statbox.
            // A change in name can affect the statbox frame size and any strikethrough indicator sizes.
            matchLvzState.RefreshStatBoxForSub(slot, changes, out int changesWritten, toggles, out int togglesWritten);

            changes = changes[..changesWritten];
            toggles = toggles[..togglesWritten];

            // TODO: target players participating and anyone spectating them instead of the whole arena
            _lvzObjects.SetAndToggle(arena, changes, toggles);
        }

        private void Callback_TeamVersusMatchPlayerKilled(IPlayerSlot killedSlot, IPlayerSlot killerSlot, bool isKnockout)
        {
            IMatchData matchData = killedSlot.MatchData;
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            var matchLvzState = arenaData.GetOrAddMatch(matchData.MatchIdentifier.BoxIdx);

            Span<LvzObjectChange> changes = stackalloc LvzObjectChange[StatBox_RefreshForKill_MaxChanges];
            Span<LvzObjectToggle> toggles = stackalloc LvzObjectToggle[StatBox_RefreshForKill_MaxToggles];

            matchLvzState.RefreshStatBoxForKill(killedSlot, changes, out int changesWritten, toggles, out int togglesWritten);

            changes = changes[..changesWritten];
            toggles = toggles[..togglesWritten];

            // TODO: target players participating and anyone spectating them instead of the whole arena
            _lvzObjects.SetAndToggle(arena, changes, toggles);
        }

        // If we wanted to show kills and deaths, then this is how it could be done. For now, opting to show remaining lives for the slot since that information is much more useful.
        // The TeamVersusMatch module only tracks data relevant to run a match (e.g. remaining lives left).
        // The TeamVersusStats module has stats such as kills, deaths, and much more.
        private void Callback_TeamVersusStatsPlayerKilled(IPlayerSlot killedSlot, IMemberStats killedStats, IPlayerSlot killerSlot, IMemberStats killerStats, bool isKnockout)
        {
            // Update the # of deaths for the player killed.
            //killedSlot
            //killedStats.Deaths

            // Update # of kills for the player that made the kill. Keep in mind that there may be no change in value if it's a team kill.
            //killerSlot
            //killedStats.Kills
        }

        private void Callback_TeamVersusMatchPlayerItemsChanged(IPlayerSlot playerSlot, ItemChanges itemChanges)
        {
            IMatchData matchData = playerSlot.MatchData;
            Arena? arena = matchData.Arena;
            if (arena is null || !_arenaDataDictionary.TryGetValue(arena, out ArenaLvzData? arenaData))
                return;

            var matchLvzState = arenaData.GetOrAddMatch(matchData.MatchIdentifier.BoxIdx);

            Span<LvzObjectChange> changes = stackalloc LvzObjectChange[StatBox_RefreshItems_MaxChanges];
            Span<LvzObjectToggle> toggles = stackalloc LvzObjectToggle[StatBox_RefreshItems_MaxToggles];

            matchLvzState.RefreshStatBoxItems(playerSlot, itemChanges, changes, out int changesWritten, toggles, out int togglesWritten);

            changes = changes[..changesWritten];
            toggles = toggles[..togglesWritten];

            // TODO: target players participating and anyone spectating them instead of the whole arena
            _lvzObjects.SetAndToggle(arena, changes, toggles);
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

        private class ArenaLvzData : IResettable
        {
            /// <summary>
            /// Key = boxId
            /// </summary>
            public readonly Dictionary<int, MatchLvzState> BoxStates = new(TargetBoxesPerArena);

            /// <summary>
            /// Lvz objects for statbox characters
            /// </summary>
            private readonly ObjectData[] _characterObjects = new ObjectData[StatBoxCharactersPerLine * StatBoxNumLines];

            /// <summary>
            /// Lvz objects for statbox characters
            /// </summary>
            public ReadOnlyMemory<ObjectData> CharacterObjects => _characterObjects;

            public void Initialize(Arena arena, ILvzObjects lvzObjects, int startingObjectId)
            {
                // statbox characters
                for (int i = 0; i < _characterObjects.Length; i++)
                {
                    int lineIndex = i / StatBoxCharactersPerLine;
                    int charIndex = i % StatBoxCharactersPerLine;
                    short objectId = (short)(startingObjectId + (lineIndex * 100) + charIndex);

                    lvzObjects.TryGetDefaultInfo(arena, objectId, out _, out _characterObjects[i]);
                }
            }

            public MatchLvzState GetOrAddMatch(int boxIdx)
            {
                if (!BoxStates.TryGetValue(boxIdx, out MatchLvzState? matchLvzState))
                {
                    matchLvzState = new MatchLvzState(this);
                    BoxStates.Add(boxIdx, matchLvzState);
                }

                return matchLvzState;
            }

            bool IResettable.TryReset()
            {
                BoxStates.Clear();
                Array.Clear(_characterObjects);
                return true;
            }
        }

        /// <summary>
        /// The state of lvz objects for an individual match.
        /// </summary>
        private class MatchLvzState
        {
            //private readonly ArenaLvzData _arenaLvzData;

            // TODO: Timer

            // TODO: Scoreboard

            // Statbox header and frame
            private static readonly int[] _headerObjectIds = [0, 1, 2, 3];
            private readonly HashSet<short> _headerAndFrameEnabledObjects = new(StatBoxHeaderAndFramesObjectCount);

            // Statbox characters
            private readonly LvzState[] _characterObjects = new LvzState[StatBoxCharactersPerLine * StatBoxNumLines];
            private readonly Memory<LvzState>[] _lines = new Memory<LvzState>[StatBoxNumLines];

            // Statbox strikethroughs
            private readonly HashSet<short> _strikethoughEnabledObjects = new(StatBoxCharactersPerLine * StatBoxNumLines);

            private int _nameDisplayLength = 4;

            private IMatchData? _matchData;
            private int _playersPerTeam;

            public MatchLvzState(ArenaLvzData arenaLvzData)
            {
                //_arenaLvzData = arenaLvzData ?? throw new ArgumentNullException(nameof(arenaLvzData));

                for (int i = 0; i < _characterObjects.Length; i++)
                {
                    _characterObjects[i] = new LvzState(arenaLvzData.CharacterObjects.Slice(i, 1));
                }

                for (int lineIdx = 0; lineIdx < StatBoxNumLines; lineIdx++)
                {
                    _lines[lineIdx] = _characterObjects.AsMemory(lineIdx * StatBoxCharactersPerLine, StatBoxCharactersPerLine);
                }
            }

            public void InitializeStatBox(IMatchData matchData, Span<LvzObjectChange> changes, out int changesWritten, Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < (StatBox_Initialize_MaxChanges))
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(changes));

                if (toggles.Length < (StatBox_Initialize_MaxToggles))
                    throw new ArgumentException("Not large enough to hold all possible toggles.", nameof(toggles));

                changesWritten = 0;
                togglesWritten = 0;

                if (_matchData is not null)
                    return;

                // TODO: maybe support other variants such as 2v2v2, for now just doing 2 teams from 1v1 up to 4v4
                if (matchData.Configuration.NumTeams != 2)
                    return;

                _matchData = matchData;

                _playersPerTeam = matchData.Configuration.PlayersPerTeam;

                SetHeaderAndFrame(matchData, ref toggles, ref togglesWritten);

                for (int teamIdx = 0; teamIdx < matchData.Teams.Count; teamIdx++)
                {
                    ITeam team = matchData.Teams[teamIdx];

                    for (int slotIdx = 0; slotIdx < team.Slots.Count; slotIdx++)
                    {
                        IPlayerSlot slot = team.Slots[slotIdx];

                        SetName(slot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                        SetLives(slot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                        SetRepels(slot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                        SetRockets(slot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                        //SetStrikethrough(slot, ref toggles, ref togglesWritten);
                    }
                }
            }

            public void RefreshStatBoxForSub(IPlayerSlot slot, Span<LvzObjectChange> changes, out int changesWritten, Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < StatBox_RefreshForSub_MaxChanges)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(changes));

                if (toggles.Length < StatBox_RefreshForSub_MaxToggles)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(toggles));

                // objects for frames + player name for the slot + strikethroughs

                changesWritten = 0;
                togglesWritten = 0;

                IMatchData matchData = slot.MatchData;
                if (_matchData != matchData)
                    return;

                // When the player name of a slot changes, it can affect the frame size and any existing strikethrough indicator sizes.

                // Refresh the header and frame in case the width changed.
                SetHeaderAndFrame(matchData, ref toggles, ref togglesWritten);

                // Update the player name of the slot to the player that subbed in.
                SetName(slot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);

                // Refresh strikethroughs in case the width changed.
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

            public void RefreshStatBoxForKill(IPlayerSlot killedSlot, Span<LvzObjectChange> changes, out int changesWritten, Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < StatBox_RefreshForKill_MaxChanges)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(changes));

                if (toggles.Length < StatBox_RefreshForKill_MaxToggles)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(toggles));

                changesWritten = 0;
                togglesWritten = 0;

                IMatchData matchData = killedSlot.MatchData;
                if (_matchData != matchData)
                    return;

                SetLives(killedSlot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                SetStrikethrough(killedSlot, ref toggles, ref togglesWritten);
            }

            public void RefreshStatBoxItems(IPlayerSlot slot, ItemChanges itemChanges, Span<LvzObjectChange> changes, out int changesWritten, Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (changes.Length < StatBox_RefreshItems_MaxChanges)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(changes));

                if (toggles.Length < StatBox_RefreshItems_MaxToggles)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(toggles));
                
                changesWritten = 0;
                togglesWritten = 0;

                if (_matchData != slot.MatchData)
                    return;

                if ((itemChanges & ItemChanges.Repels) == ItemChanges.Repels)
                {
                    // Update repel count
                    SetRepels(slot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                }

                if ((itemChanges & ItemChanges.Rockets) == ItemChanges.Rockets)
                {
                    // Update rocket count
                    SetRockets(slot, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
                }
            }

            public void ClearStatBox(Span<LvzObjectToggle> toggles, out int togglesWritten)
            {
                if (toggles.Length < StatBox_Clear_MaxToggles)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(toggles));

                _matchData = null;

                togglesWritten = 0;

                // header and frame
                foreach (short objectId in _headerAndFrameEnabledObjects)
                {
                    toggles[0] = new LvzObjectToggle(objectId, false);
                    toggles = toggles[1..];
                    togglesWritten++;
                }

                _headerAndFrameEnabledObjects.Clear();

                // characters
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

                    // We could change the image to ' ', but there's no need to since it's disabled.
                }

                // strikethroughs
                foreach (short objectId in _strikethoughEnabledObjects)
                {
                    toggles[0] = new LvzObjectToggle(objectId, false);
                    toggles = toggles[1..];
                    togglesWritten++;
                }

                _strikethoughEnabledObjects.Clear();
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
                changesWritten = 0;
                togglesWritten = 0;

                // header and frame
                foreach (short objectId in from._headerAndFrameEnabledObjects)
                {
                    if (!to._headerAndFrameEnabledObjects.Contains(objectId))
                    {
                        toggles[0] = new LvzObjectToggle(objectId, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                    }
                }

                foreach (short objectId in to._headerAndFrameEnabledObjects)
                {
                    if (!from._headerAndFrameEnabledObjects.Contains(objectId))
                    {
                        toggles[0] = new LvzObjectToggle(objectId, true);
                        toggles = toggles[1..];
                        togglesWritten++;
                    }
                }

                // characters
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
                    }

                    if (!fromState.IsEnabled && toState.IsEnabled)
                    {
                        toggles[0] = new LvzObjectToggle(fromState.Default.Id, true);
                        toggles = toggles[1..];
                        togglesWritten++;
                    }
                }

                // strikethroughs
                foreach (short objectId in from._strikethoughEnabledObjects)
                {
                    if (!to._strikethoughEnabledObjects.Contains(objectId))
                    {
                        toggles[0] = new LvzObjectToggle(objectId, false);
                        toggles = toggles[1..];
                        togglesWritten++;
                    }
                }

                foreach (short objectId in to._strikethoughEnabledObjects)
                {
                    if (!from._strikethoughEnabledObjects.Contains(objectId))
                    {
                        toggles[0] = new LvzObjectToggle(objectId, true);
                        toggles = toggles[1..];
                        togglesWritten++;
                    }
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
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(toggles));

                // TODO: adjust lvz frames according to # of teams
                //int numTeams = matchData.Configuration.NumTeams;

                int playersPerTeam = matchData.Configuration.PlayersPerTeam;

                //
                // Header
                //

                // Header labels and icons
                ToggleObjects(0, 3, 0, ref toggles, ref togglesWritten);

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
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(changes));

                if (toggles.Length < StatBox_NameChange_MaxToggles)
                    throw new ArgumentException("Not large enough to hold all possible toggles.", nameof(toggles));

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

            private void SetLives(IPlayerSlot slot, ref Span<LvzObjectChange> changes, ref int changesWritten, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                if (changes.Length < 2)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(changes));

                if (toggles.Length < 2)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(toggles));

                if (_matchData != slot.MatchData)
                    return;

                byte value = (byte)slot.Lives;
                Color color = (value <= 1) ? Color.Red : Color.Yellow;
                Span<LvzState> charStates = SliceLives(_lines[GetLineIdx(slot)]).Span;
                Set2DigitNumber(value, color, charStates, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
            }

            private void SetRepels(IPlayerSlot slot, ref Span<LvzObjectChange> changes, ref int changesWritten, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                if (changes.Length < 2)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(changes));

                if (toggles.Length < 2)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(toggles));

                if (_matchData != slot.MatchData)
                    return;

                byte value = slot.Repels;
                Color color = (value == 0) ? Color.Red : Color.Yellow;
                Span<LvzState> charStates = SliceRepels(_lines[GetLineIdx(slot)]).Span;
                Set2DigitNumber(value, color, charStates, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
            }

            private void SetRockets(IPlayerSlot slot, ref Span<LvzObjectChange> changes, ref int changesWritten, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                if (changes.Length < 2)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(changes));

                if (toggles.Length < 2)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(toggles));

                if (_matchData != slot.MatchData)
                    return;

                byte value = slot.Rockets;
                Color color = (value == 0) ? Color.Red : Color.Yellow;
                Span<LvzState> charStates = SliceRockets(_lines[GetLineIdx(slot)]).Span;
                Set2DigitNumber(value, color, charStates, ref changes, ref changesWritten, ref toggles, ref togglesWritten);
            }

            private static void Set2DigitNumber(byte value, Color color, Span<LvzState> charStates, ref Span<LvzObjectChange> changes, ref int changesWritten, ref Span<LvzObjectToggle> toggles, ref int togglesWritten)
            {
                ArgumentOutOfRangeException.ThrowIfNotEqual(charStates.Length, 2, nameof(charStates));

                if (changes.Length < 2)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(changes));

                if (toggles.Length < 2)
                    throw new ArgumentException("Not large enough to hold all possible changes.", nameof(toggles));

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

            private static Memory<LvzState> SliceLives(Memory<LvzState> line)
            {
                return line.Slice(24, 2);
            }

            private static Memory<LvzState> SliceRepels(Memory<LvzState> line)
            {
                return line.Slice(27, 2);
            }

            private static Memory<LvzState> SliceRockets(Memory<LvzState> line)
            {
                return line.Slice(30, 2);
            }
        }

        private struct LvzState
        {
            public bool IsEnabled;
            private readonly ReadOnlyMemory<ObjectData> _default; // TODO: a copy of the ObjectData instead would use less memory
            public ObjectData Current;

            public LvzState(ReadOnlyMemory<ObjectData> defaultState)
            {
                ArgumentOutOfRangeException.ThrowIfNotEqual(defaultState.Length, 1, nameof(defaultState));

                IsEnabled = false;
                _default = defaultState;
                Current = Default;
            }

            public readonly ref readonly ObjectData Default => ref _default.Span[0];
        }

        #endregion
    }
}
