using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that tracks the association of players with ongoing matches, 
    /// whether it be because a player: participated in the match, is playing in the match, or is spectating the match.
    /// </summary>
    /// <remarks>
    /// This module also provides logic for position packet filtering as an <see cref="IPlayerPositionAdvisor"/>.
    /// </remarks>
    public class MatchFocus(
        IComponentBroker broker,
        IGame game,
        ILogManager logManager,
        IObjectPoolManager objectPoolManager,
        IPlayerData playerData) : IModule, IArenaAttachableModule, IMatchFocus, IPlayerPositionAdvisor, IBricksAdvisor
    {
        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly IGame _game = game ?? throw new ArgumentNullException(nameof(game));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        private InterfaceRegistrationToken<IMatchFocus>? _iMatchFocusRegistrationToken;

        private PlayerDataKey<PlayerData> _playerDataKey;
        private readonly Dictionary<Arena, ArenaData> _arenaDataDictionary = new(Constants.TargetArenaCount);
        private readonly Dictionary<IMatch, MatchFocusData> _matchDictionary = new(TargetMatchCount);

        private readonly static ObjectPool<ArenaData> s_arenaDataPool = new DefaultObjectPool<ArenaData>(new DefaultPooledObjectPolicy<ArenaData>(), Constants.TargetArenaCount);
        private readonly static ObjectPool<MatchFocusData> s_matchFocusPool = new DefaultObjectPool<MatchFocusData>(new DefaultPooledObjectPolicy<MatchFocusData>(), TargetMatchCount);

        /// <summary>
        /// The expected upper limit on the # of simultaneous matches.
        /// </summary>
        /// <remarks>
        /// This is used to initialize collections with enough starting capacity such that they will likely never need to be resized.
        /// </remarks>
        private const int TargetMatchCount = 64;

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _playerDataKey = _playerData.AllocatePlayerData<PlayerData>();

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            MatchStartingCallback.Register(broker, Callback_MatchStarting);
            MatchEndedCallback.Register(broker, Callback_MatchEnded);
            MatchAddPlayingCallback.Register(broker, Callback_MatchAddPlaying);
            MatchRemovePlayingCallback.Register(broker, Callback_MatchRemovePlaying);

            _iMatchFocusRegistrationToken = broker.RegisterInterface<IMatchFocus>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iMatchFocusRegistrationToken) != 0)
                return false;

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            MatchStartingCallback.Unregister(broker, Callback_MatchStarting);
            MatchEndedCallback.Unregister(broker, Callback_MatchEnded);
            MatchAddPlayingCallback.Unregister(broker, Callback_MatchAddPlaying);
            MatchRemovePlayingCallback.Unregister(broker, Callback_MatchRemovePlaying);

            _playerData.FreePlayerData(ref _playerDataKey);

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
            {
                arenaData = s_arenaDataPool.Get();
                _arenaDataDictionary.Add(arena, arenaData);
            }

            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            SpectateChangedCallback.Register(arena, Callback_SpectateChanged);
            arenaData.IPlayerPositionAdvisorRegistrationToken = arena.RegisterAdvisor<IPlayerPositionAdvisor>(this);
            arenaData.IBricksAdvisorRegistrationToken = arena.RegisterAdvisor<IBricksAdvisor>(this);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!_arenaDataDictionary.TryGetValue(arena, out ArenaData? arenaData))
                return false;

            if (!arena.UnregisterAdvisor(ref arenaData.IPlayerPositionAdvisorRegistrationToken))
                return false;

            if (!arena.UnregisterAdvisor(ref arenaData.IBricksAdvisorRegistrationToken))
                return false;

            if (!_arenaDataDictionary.Remove(arena))
                return false;

            s_arenaDataPool.Return(arenaData);

            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            SpectateChangedCallback.Unregister(arena, Callback_SpectateChanged);

            return true;
        }

        #endregion

        #region IPlayerPositionAdvisor

        bool IPlayerPositionAdvisor.EditIndividualPositionPacket(Player player, Player toPlayer, ref C2S_PositionPacket positionPacket, ref ExtraPositionData extra, ref int extraLength)
        {
            if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return false;

            if (!toPlayer.TryGetExtraData(_playerDataKey, out PlayerData? toPlayerData))
                return false;

            if (playerData.PlayingInMatch == GetFocusedMatchData(toPlayer))
            {
                // The player whose position packet is under consideration is in the match that the toPlayer is focused on (spectating or playing in).
                // OR
                // (null == null) The player whose position packet is under consideration is not in a match and the toPlayer is not focused on a match.
                return false;
            }

            // Drop the packet.
            positionPacket.X = positionPacket.Y = -1;
            return true;
        }

        #endregion

        #region IBricksAdvisor

        bool IBricksAdvisor.IsValidForPlayer(Player player, ref readonly BrickData brickData)
        {
            if (GetFocusedMatchData(player)?.Match is not IMatchData matchData)
                return true;

            // Check if the brick is for a team in the match.
            for (int teamIdx = 0; teamIdx < matchData.Teams.Count; teamIdx++)
            {
                ITeam team = matchData.Teams[teamIdx];
                if (team.Freq == brickData.Freq)
                    return true;
            }

            return false;
        }

        #endregion

        #region IMatchFocus

        bool IMatchFocus.TryGetPlayers(IMatch match, HashSet<Player> players, MatchFocusReasons? filterReasons, Arena? arenaFilter)
        {
            if (!_matchDictionary.TryGetValue(match, out MatchFocusData? matchFocusData))
                return false;

            // Participants
            if (filterReasons is null || (filterReasons & MatchFocusReasons.Participant) != 0)
            {
                foreach (Player player in matchFocusData.Participants)
                {
                    if (arenaFilter is null || player.Arena == arenaFilter)
                        players.Add(player);
                }
            }

            // Playing
            if (filterReasons is null || (filterReasons & MatchFocusReasons.Playing) != 0)
            {
                foreach (Player player in matchFocusData.Playing)
                {
                    if (arenaFilter is null || player.Arena == arenaFilter)
                        players.Add(player);
                }
            }

            // Spectating
            if (filterReasons is null || (filterReasons & MatchFocusReasons.Spectating) != 0)
            {
                foreach (Player player in matchFocusData.Spectators)
                {
                    if (arenaFilter is null || player.Arena == arenaFilter)
                        players.Add(player);
                }
            }

            return true;
        }

        IMatch? IMatchFocus.GetPlayingMatch(Player player)
        {
            if (player is null || !player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return null;

            return playerData.PlayingInMatch?.Match;
        }

        IMatch? IMatchFocus.GetFocusedMatch(Player player)
        {
            if (player is null)
                return null;

            return GetFocusedMatchData(player)?.Match;
        }

        #endregion

        #region Callback handlers

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (player is null || !player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return;

            string? playerName = player.Name;
            if (playerName is null)
                return;

            if (action == PlayerAction.Connect)
            {
                foreach (MatchFocusData matchFocusData in _matchDictionary.Values)
                {
                    if (matchFocusData.DisconnectedParticipantNameSet.Remove(playerName))
                    {
                        // A participant that previously disconnected has reconnected.
                        matchFocusData.Participants.Add(player);
                    }

                    if (matchFocusData.DisconnectedPlayingNameSet.Remove(playerName))
                    {
                        // A playing player that previously disconnected has reconnected.
                        SetPlaying(player, playerData, matchFocusData);
                    }
                }
            }
            else if (action == PlayerAction.LeaveArena)
            {
                if (playerData.SpectatingMatch is not null)
                {
                    // The SpectateChanged callback will get invoked. However, the handler will not clear the spectating state.
                    // To the handler, it will look like the player is just no longer directly following the player.
                    // Therefore, we need to clear the spectating state here since the player is no longer in the same arena.
                    ClearSpectating(player, playerData);
                }
            }
            else if (action == PlayerAction.Disconnect)
            {
                foreach (MatchFocusData matchFocusData in _matchDictionary.Values)
                {
                    if (matchFocusData.Participants.Remove(player))
                    {
                        matchFocusData.DisconnectedParticipantNameSet.Add(playerName);
                    }
                }

                if (playerData.PlayingInMatch is not null)
                {
                    ClearPlaying(player, playerData, true);
                }

                // Spectators are removed when they leave the arena.
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return;
            
            if (oldShip == ShipType.Spec && newShip != ShipType.Spec)
            {
                // The player was in spectator mode and changed into a ship.

                // If the player was spectating a match, clear it.
                ClearSpectating(player, playerData);

                MatchFocusData? matchFocusData = playerData.PlayingInMatch;
                if (matchFocusData is null)
                    return;

                // The player is in a match.

                // If a player is playing and then changes to spectator mode, 
                // those that were spectating will still be spectating that player if there are no other players to spectate.
                // In this special case, they do not send a SpecRequest packet to clear.
                // If the player enters a ship, they will resume spectating that player without sending another SpecRequest packet.

                // Make sure that any players spectating the player are set as spectating the match as well.
                // There's a chance the player's match could have changed between moving to spec and entering a ship.
                SetSpectators(player, matchFocusData);
            }
        }

        private void Callback_MatchStarting(IMatch match)
        {
            if (match is null)
                return;

            MatchFocusData matchFocusData = GetOrAddMatchFocusData(match);

            HashSet<string> playingNames = _objectPoolManager.NameHashSetPool.Get();
            HashSet<Player> playing = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> spectating = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                // Playing (including those that have disconnected)
                var advisors = _broker.GetAdvisors<IMatchFocusAdvisor>();
                foreach (var advisor in advisors)
                {
                    if (playingNames.Count > 0)
                        playingNames.Clear();

                    // Ask the advisor to provide the names of the players that are playing in the match.
                    if (advisor.TryGetPlaying(match, playingNames))
                    {
                        foreach (string name in playingNames)
                        {
                            Player? player = _playerData.FindPlayer(name);
                            if (player is not null)
                                playing.Add(player);
                            else
                                matchFocusData.DisconnectedPlayingNameSet.Add(name);
                        }

                        // Players have been provided by an advisor. No need to query the rest.
                        break;
                    }
                }

                foreach (Player player in playing)
                {
                    if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                        continue;

                    SetPlaying(player, playerData, matchFocusData);
                }

                // Spectators
                _game.GetSpectators(playing, spectating);
                foreach (Player player in spectating)
                {
                    if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                        continue;

                    SetSpectating(player, playerData, matchFocusData);
                }
            }
            finally
            {
                _objectPoolManager.NameHashSetPool.Return(playingNames);
                _objectPoolManager.PlayerSetPool.Return(playing);
                _objectPoolManager.PlayerSetPool.Return(spectating);
            }
        }

        private void Callback_MatchEnded(IMatch match)
        {
            if (match is null)
                return;

            if (!_matchDictionary.Remove(match, out MatchFocusData? matchFocusData))
                return;

            // Disassociate anyone spectating the match.
            while (matchFocusData.Spectators.Count > 0)
            {
                foreach (Player player in matchFocusData.Spectators)
                {
                    ClearSpectating(player);

                    // Enumerator can't be used after clear.
                    break;
                }
            }

            // Disassociate anyone that was playing in the match.
            while (matchFocusData.Playing.Count > 0)
            {
                foreach (Player player in matchFocusData.Playing)
                {
                    ClearPlaying(player);

                    // Enumerator can't be used after clear.
                    break;
                }
            }

            // Return the object to the pool.
            // Note: This also resets the object, clearing the remaining data.
            s_matchFocusPool.Return(matchFocusData);
        }

        private void Callback_MatchAddPlaying(IMatch match, string playerName, Player? player)
        {
            if (!_matchDictionary.TryGetValue(match, out MatchFocusData? matchFocusData))
                return;

            if (player is not null)
            {
                if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                    return;

                SetPlaying(player, playerData, matchFocusData);

                // Make sure that any players spectating the player are set as spectating the match as well.
                SetSpectators(player, matchFocusData);
            }
            else if (playerName is not null)
            {
                matchFocusData.DisconnectedPlayingNameSet.Add(playerName);
            }
        }

        private void Callback_MatchRemovePlaying(IMatch match, string playerName, Player? player)
        {
            if (!_matchDictionary.TryGetValue(match, out MatchFocusData? matchFocusData))
                return;

            if (player is not null)
            {
                if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                    return;

                if (playerData.PlayingInMatch == matchFocusData)
                {
                    ClearPlaying(player, playerData);
                }
            }
            else if (playerName is not null)
            {
                matchFocusData.DisconnectedPlayingNameSet.Remove(playerName);
            }
        }

        private void Callback_SpectateChanged(Player player, Player? target)
        {
            if (player is null || !player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return;

            _logManager.LogP(LogLevel.Drivel, nameof(MatchFocus), player, $"Spectate changed to: {(target is null ? "(none)" : target.Name)}");

            IMatch? match = null;

            if (target is not null)
            {
                // Check if the target player is playing in a match.
                var advisors = _broker.GetAdvisors<IMatchFocusAdvisor>();
                foreach (var advisor in advisors)
                {
                    match = advisor.GetMatch(target);
                    if (match is not null)
                        break;
                }
            }

            if (match is not null)
            {
                SetSpectating(player, playerData, GetOrAddMatchFocusData(match));
            }
            else
            {
                // No match.
                // However, only clear spectating if it was a switch to a new target player.
                // When just moving off of a player, let the player continue spectating the match they're watching.
                if (target is not null)
                {
                    ClearSpectating(player, playerData);
                }
            }
        }

        #endregion

        private MatchFocusData GetOrAddMatchFocusData(IMatch match)
        {
            if (!_matchDictionary.TryGetValue(match, out MatchFocusData? matchFocusData))
            {
                matchFocusData = s_matchFocusPool.Get();
                matchFocusData.Match = match;
                _matchDictionary.Add(match, matchFocusData);
            }

            return matchFocusData;
        }

        private void SetPlaying(Player player, PlayerData playerData, MatchFocusData matchFocusData)
        {
            IMatch? oldMatch = GetFocusedMatchData(player)?.Match;

            if (playerData.SpectatingMatch is not null)
            {
                ClearSpectating(player, playerData);
            }

            if (playerData.PlayingInMatch is not null)
            {
                if (playerData.PlayingInMatch == matchFocusData)
                {
                    // Already set as playing in this match.
                    return;
                }

                // The player was playing in another match.
                ClearPlaying(player, playerData, false, false);
            }

            matchFocusData.Playing.Add(player);
            matchFocusData.Participants.Add(player);
            playerData.PlayingInMatch = matchFocusData;

            MatchFocusChangedCallback.Fire(player.Arena ?? _broker, player, oldMatch, GetFocusedMatchData(player)?.Match);
        }

        private void ClearPlaying(Player player, PlayerData? playerData = null, bool isDisconnect = false, bool invokeCallback = true)
        {
            if (playerData is null && !player.TryGetExtraData(_playerDataKey, out playerData))
                return;

            MatchFocusData? matchFocusData = playerData.PlayingInMatch;
            if (matchFocusData is not null)
            {
                IMatch? oldMatch = invokeCallback ? GetFocusedMatchData(player)?.Match : null;

                matchFocusData.Playing.Remove(player);
                playerData.PlayingInMatch = null;

                if (isDisconnect)
                {
                    matchFocusData.DisconnectedPlayingNameSet.Add(player.Name!);
                }

                if (invokeCallback)
                {
                    MatchFocusChangedCallback.Fire(player.Arena ?? _broker, player, oldMatch, GetFocusedMatchData(player)?.Match);
                }
            }
        }

        private void SetSpectators(Player target, MatchFocusData matchFocusData)
        {
            HashSet<Player> spectators = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                // Check if anyone is spectating the target player.
                _game.GetSpectators(target, spectators);

                // Set them as spectating the match.
                foreach (Player spectator in spectators)
                {
                    if (!spectator.TryGetExtraData(_playerDataKey, out PlayerData? spectatorData))
                        continue;

                    SetSpectating(spectator, spectatorData, matchFocusData);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(spectators);
            }
        }

        private void SetSpectating(Player player, PlayerData playerData, MatchFocusData matchFocusData)
        {
            IMatch? oldMatch = GetFocusedMatchData(player)?.Match;

            if (playerData.SpectatingMatch is not null)
            {
                if (playerData.SpectatingMatch == matchFocusData)
                {
                    // Already set as spectating this match.
                    return;
                }

                // The player was spectating a different match.
                ClearSpectating(player, playerData, false);
            }

            matchFocusData.Spectators.Add(player);
            playerData.SpectatingMatch = matchFocusData;

            MatchFocusChangedCallback.Fire(player.Arena ?? _broker, player, oldMatch, GetFocusedMatchData(player)?.Match);
        }

        private void ClearSpectating(Player player, PlayerData? playerData = null, bool invokeCallback = true)
        {
            if (playerData is null && !player.TryGetExtraData(_playerDataKey, out playerData))
                return;

            if (playerData.SpectatingMatch is not null)
            {
                IMatch? oldMatch = invokeCallback ? GetFocusedMatchData(player)?.Match : null;

                playerData.SpectatingMatch.Spectators.Remove(player);
                playerData.SpectatingMatch = null;

                if (invokeCallback)
                {
                    MatchFocusChangedCallback.Fire(player.Arena ?? _broker, player, oldMatch, GetFocusedMatchData(player)?.Match);
                }
            }
        }

        private MatchFocusData? GetFocusedMatchData(Player player)
        {
            if (!player.TryGetExtraData(_playerDataKey, out PlayerData? playerData))
                return null;

            return playerData.SpectatingMatch ?? playerData.PlayingInMatch;
        }

        #region Helper types

        private class MatchFocusData : IResettable
        {
            public IMatch? Match;

            /// <summary>
            /// Players participated in the match (and are still connected).
            /// </summary>
            /// <remarks>
            /// See <see cref="DisconnectedParticipantNameSet"/> for names of participants that have disconnected.
            /// </remarks>
            public readonly HashSet<Player> Participants = new(Constants.TargetPlayerCount);

            /// <summary>
            /// Names of <see cref="Participants"/> that have disconnected from the server.
            /// If the player reconnects, this is used to add them back into the <see cref="Participants"/> collection.
            /// </summary>
            public readonly HashSet<string> DisconnectedParticipantNameSet = new(Constants.TargetPlayerCount, StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Players that are currently playing in the match.
            /// </summary>
            public readonly HashSet<Player> Playing = new(Constants.TargetPlayerCount); // TODO: what do to about players that get knocked out? are they playing still? we still want to send them notifications, but what if they're allowed to join another match?

            /// <summary>
            /// Names of <see cref="Playing"/> that have disconnected from the server.
            /// If the player reconnects, this is used to add them back into the <see cref="Playing"/> collection.
            /// </summary>
            public readonly HashSet<string> DisconnectedPlayingNameSet = new(Constants.TargetPlayerCount, StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Players that are spectating the match.
            /// </summary>
            public readonly HashSet<Player> Spectators = new(Constants.TargetPlayerCount);

            bool IResettable.TryReset()
            {
                Match = null;
                Participants.Clear();
                DisconnectedParticipantNameSet.Clear();
                Playing.Clear();
                DisconnectedPlayingNameSet.Clear();
                Spectators.Clear();
                return true;
            }
        }

        private class PlayerData : IResettable
        {
            public MatchFocusData? PlayingInMatch;
            public MatchFocusData? SpectatingMatch;

            bool IResettable.TryReset()
            {
                PlayingInMatch = null;
                SpectatingMatch = null;
                return true;
            }
        }

        private class ArenaData : IResettable
        {
            public AdvisorRegistrationToken<IPlayerPositionAdvisor>? IPlayerPositionAdvisorRegistrationToken;
            public AdvisorRegistrationToken<IBricksAdvisor>? IBricksAdvisorRegistrationToken;

            bool IResettable.TryReset()
            {
                IPlayerPositionAdvisorRegistrationToken = null;
                IBricksAdvisorRegistrationToken = null;
                return true;
            }
        }

        #endregion
    }
}
