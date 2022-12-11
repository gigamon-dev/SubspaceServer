using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that manages freqs and ships for players.
    /// <remarks>
    /// This implementation uses <see cref="IFreqManagerEnforcerAdvisor"/> advisors and the <see cref="IFreqBalancer"/> interface
    /// to affect behavior and therefore should be able to handle most scenarios.
    /// </remarks>
    /// </summary>
    public class FreqManager : IModule, IFreqManager, IFreqBalancer, IFreqManagerEnforcerAdvisor
    {
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private IGame _game;
        private IPlayerData _playerData;

        private AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor> _iFreqEnforcerAdvisorRegistrationToken;
        private InterfaceRegistrationToken<IFreqManager> _iFreqManagerRegistrationToken;
        private InterfaceRegistrationToken<IFreqBalancer> _iFreqBalancerRegistrationToken;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        private readonly ObjectPool<Freq> _freqPool = new DefaultObjectPool<Freq>(new FreqPooledObjectPolicy(), 16);

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            IGame game,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            PreShipFreqChangeCallback.Register(broker, Callback_PreShipFreqChange);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _iFreqEnforcerAdvisorRegistrationToken = broker.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);
            _iFreqManagerRegistrationToken = broker.RegisterInterface<IFreqManager>(this);
            _iFreqBalancerRegistrationToken = broker.RegisterInterface<IFreqBalancer>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (!broker.UnregisterAdvisor(ref _iFreqEnforcerAdvisorRegistrationToken))
                return false;
            
            if (broker.UnregisterInterface(ref _iFreqManagerRegistrationToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iFreqBalancerRegistrationToken) != 0)
                return false;

            PreShipFreqChangeCallback.Unregister(broker, Callback_PreShipFreqChange);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _arenaManager.FreeArenaData(_adKey);
            _playerData.FreePlayerData(_pdKey);

            return true;
        }

        #endregion

        #region IFreqManager

        void IFreqManager.Initial(Player player, ref ShipType ship, ref short freq)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                ShipType workingShip = ship;

                if (ad.Config.AlwaysStartInSpec || !CanEnterGame(arena, player, null))
                {
                    workingShip = ShipType.Spec;
                }

                short workingFreqNum;

                if (workingShip == ShipType.Spec)
                {
                    workingFreqNum = arena.SpecFreq;
                }
                else
                {
                    // Find an initial freq using the balancer and enforcer.
                    workingFreqNum = FindEntryFreq(arena, player, null);

                    if (workingFreqNum == arena.SpecFreq)
                    {
                        workingShip = ShipType.Spec;
                    }
                    else
                    {
                        ShipMask mask = GetAllowableShips(arena, player, workingShip, workingFreqNum, null);
                        if ((mask & workingShip.GetShipMask()) == ShipMask.None)
                        {
                            // The curent ship isn't valid, get one that is.
                            workingShip = GetShip(mask);
                        }

                        // If the enforcers didn't let them take a ship, send the player to spec.
                        if (workingShip == ShipType.Spec)
                        {
                            workingFreqNum = arena.SpecFreq;
                        }
                    }
                }

                ship = workingShip;
                freq = workingFreqNum;
            }
        }

        void IFreqManager.ShipChange(Player player, ShipType workingShip, StringBuilder errorMessage)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            errorMessage?.Clear(); // Clear so that we can tell if an enforcer wrote a message.

            if (workingShip >= ShipType.Spec)
            {
                // Always allow switching to spec.
                _game.SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);
                return;
            }

            // See if the player is allowed to change their ship/freq.
            if (!IsUnlocked(arena, player, errorMessage))
            {
                return; // passes along errorMessage, if any
            }

            short workingFreqNum = player.Freq;

            if (player.Ship == ShipType.Spec)
            {
                // Trying to come out of spec. Check to see if the player can enter the game.
                if (!CanEnterGame(arena, player, errorMessage))
                {
                    return; // passes along errorMessage, if any
                }

                if (workingFreqNum == arena.SpecFreq)
                {
                    // The player is coming from the spec freq, assign them a new freq.
                    workingFreqNum = FindEntryFreq(arena, player, errorMessage);
                    if (workingFreqNum == arena.SpecFreq)
                    {
                        // An entry freq could not be found.

                        if (errorMessage != null)
                        {
                            if (errorMessage.Length > 0)
                            {
                                errorMessage?.Insert(0, "Couldn't find a frequency to place you on: ");
                            }
                            else
                            {
                                errorMessage.Append("Couldn't find a frequency to place you on.");
                            }
                        }
                    }
                }
                else if (!ad.Config.SpectatorsCountForTeamSize && !FreqNotFull(arena, workingFreqNum, errorMessage))
                {
                    errorMessage?.Append("Your frequency already has the maximum number of players in the game.");
                    return;
                }
            }

            // Make sure the ship is legal.
            if (workingShip != ShipType.Spec)
            {
                ShipMask mask = GetAllowableShips(arena, player, workingShip, workingFreqNum, errorMessage);
                if ((mask & workingShip.GetShipMask()) == ShipMask.None)
                {
                    if ((mask & player.Ship.GetShipMask()) != ShipMask.None)
                    {
                        // Default to the old ship.
                        workingShip = player.Ship;
                    }
                    else
                    {
                        workingShip = GetShip(mask);
                    }
                }
            }

            if (workingShip == ShipType.Spec && ad.Config.DisallowTeamSpectators)
            {
                errorMessage?.Append("You may only spectate on the spectator frequency.");
                workingFreqNum = arena.SpecFreq;
            }

            _game.SetShipAndFreq(player, workingShip, workingFreqNum); // UpdateFreqs will be called in the the ShipFreqChange callback.
        }

        void IFreqManager.FreqChange(Player player, short requestedFreqNum, StringBuilder errorMessage)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            errorMessage?.Clear(); // Clear so that we can tell if an enforcer wrote a message.

            ShipType workingShip = player.Ship;
            if (workingShip == ShipType.Spec && requestedFreqNum == arena.SpecFreq)
            {
                // Always allow switching to the spec freq if the person is spectating.
                _game.SetFreq(player, arena.SpecFreq);
                return;
            }

            // See if the player is allows to change their ship/freq.
            if (!IsUnlocked(arena, player, errorMessage))
            {
                return; // pass along errorMessage, if any
            }

            if (requestedFreqNum < 0 || requestedFreqNum >= ad.Config.NumberOfFrequencies)
            {
                // They requested a bad freq.
                if (workingShip == ShipType.Spec && player.Freq != arena.SpecFreq)
                {
                    _game.SetFreq(player, arena.SpecFreq);
                }
                else
                {
                    errorMessage?.Append("That frequency is not used in this arena.");
                }

                return;
            }

            // At this point, we have a valid freq, now we just need to make sure that
            // the user can change to it. First, make sure just changing to the freq is
            // allowed. Second, make sure that if spectators are only allowed on the
            // spec freq, that they have a ship they can use on that freq.

            if (!CanChangeToFreq(arena, player, requestedFreqNum, errorMessage))
            {
                return; // pass along errorMessage, if any
            }

            if (workingShip == ShipType.Spec && ad.Config.DisallowTeamSpectators)
            {
                // Since the player must come out of spec immediately, we must find them a ship.

                if (!CanEnterGame(arena, player, errorMessage))
                {
                    return; // pass along errorMessage, if any
                }

                ShipMask mask = GetAllowableShips(arena, player, ShipType.Spec, requestedFreqNum, errorMessage);
                workingShip = GetShip(mask);
            }
            else if (workingShip != ShipType.Spec)
            {
                // Since the player is already in game, we just need to make sure their ship is legal on their new freq,
                // or find them a new ship if it's not.
                ShipMask mask = GetAllowableShips(arena, player, ShipType.Spec, requestedFreqNum, errorMessage);
                if ((mask & workingShip.GetShipMask()) == ShipMask.None)
                {
                    workingShip = GetShip(mask);
                }
            }

            if (ad.Config.DisallowTeamSpectators && workingShip == ShipType.Spec && errorMessage != null)
            {
                // At this point, the player should have a ship other than ship spec, if one is required.
                // If they don't, then we fail and report the error.
                // Let's clarify why the person cannot change to the freq, since DisallowTeamSpectators is a little weird.
                if (errorMessage.Length > 0)
                {
                    errorMessage.Insert(0, $"You cannot change to freq {requestedFreqNum} because you could not enter a ship there. ");
                }
                else
                {
                    errorMessage.Append($"You cannot change to freq {requestedFreqNum} because you could not enter a ship there.");
                }
            }
            else
            {
                // The player passed all checks for being unlocked:
                // being able to change to the target freq and having a legal ship.
                _game.SetShipAndFreq(player, workingShip, requestedFreqNum); // UpdateFreqs will be called in the the ShipFreqChange callback.
            }
        }

        #endregion

        #region IFreqBalancer

        int IFreqBalancer.GetPlayerMetric(Player player)
        {
            return 1;
        }

        int IFreqBalancer.GetMaxMetric(Arena arena, short freq)
        {
            return GetMaxFreqSize(arena, freq);
        }

        int IFreqBalancer.GetMaximumDifference(Arena arena, short freq1, short freq2)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return int.MaxValue;

            if (ad.Config.DefaultBalancer_forceEvenTeams)
                return ad.Config.DefaultBalancer_maxDifference;
            else
                return int.MaxValue;
        }

        #endregion

        #region IFreqEnforcerAdvisor

        bool IFreqManagerEnforcerAdvisor.CanChangeToFreq(Player player, short newFreqNum, StringBuilder errorMessage)
        {
            if (player == null)
                return false;

            Arena arena = player.Arena;
            if (arena == null)
                return false;

            return FreqNotFull(arena, newFreqNum, errorMessage) && BalancerAllowChange(arena, player, newFreqNum, errorMessage);

            bool BalancerAllowChange(Arena arena, Player player, short newFreqNum, StringBuilder errorMessage)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                    return false;

                lock (ad.Lock)
                {
                    short oldFreqNum = player.Freq;

                    Freq oldFreq = GetFreq(arena, oldFreqNum);
                    Freq newFreq = GetFreq(arena, newFreqNum);

                    if (newFreq != null && newFreq.IsRequired && newFreq.Players.Count == 0)
                    {
                        // Changing to an empty required team: always allow.
                        return true;
                    }
                    else if (oldFreq != null && oldFreq.IsRequired && oldFreq.Players.Count == 1)
                    {
                        // The player cannot leave a required if they were the only player on it,
                        // unless they are going to another required team that has no players on it. <---- huh, what about this?
                        errorMessage?.Append("Your frequency requires at least one player.");
                        return false;
                    }
                    else
                    {
                        // See if there are required teams that need to be filled.
                        foreach (Freq freq in ad.Freqs)
                        {
                            if (freq.IsRequired && freq.Players.Count == 0)
                            {
                                // They shouldn't be changing to a team that isn't required
                                // when there are still required teams to start up.
                                errorMessage?.Append($"Frequency {freq.FreqNum} needs players first.");
                                return false;
                            }
                        }
                    }

                    IFreqBalancer balancer = arena.GetInterface<IFreqBalancer>();
                    bool isBrokerBalancer = true;

                    if (balancer == null)
                    {
                        balancer = this;
                        isBrokerBalancer = false;
                    }

                    try
                    {
                        int oldFreqMetric = GetFreqMetric(oldFreq, balancer);
                        int newFreqMetric = GetFreqMetric(newFreq, balancer);

                        int playerMetric = GetPlayerMetric(player, balancer);
                        int oldFreqMetricPotential = oldFreqMetric - playerMetric;
                        int newFreqMetricPotential = newFreqMetric + playerMetric;

                        int maxMetric = balancer.GetMaxMetric(arena, newFreqNum);

                        if (maxMetric != 0 && newFreqMetricPotential > maxMetric)
                        {
                            errorMessage?.Append("Changing to that team would make it too powerful.");
                            return false;
                        }
                        else if (oldFreq != null && oldFreqMetricPotential > 0 && balancer.GetMaximumDifference(arena, oldFreqNum, newFreqNum) < newFreqMetricPotential - oldFreqMetricPotential)
                        {
                            errorMessage?.Append("Changing to that team would disrupt the balance between it and your current team.");
                            return false;
                        }
                        else
                        {
                            foreach (Freq freq in ad.Freqs)
                            {
                                if (!freq.IsBalancedAgainst)
                                    continue;

                                if (freq == oldFreq || freq == newFreq)
                                    continue;

                                int freqMetric = GetFreqMetric(freq, balancer);

                                if (balancer.GetMaximumDifference(arena, newFreqNum, freq.FreqNum) < newFreqMetricPotential - freqMetric)
                                {
                                    errorMessage?.Append("Changing to that team would make the teams too uneven.");
                                    return false;
                                }
                            }

                            return true;
                        }
                    }
                    finally
                    {
                        if (isBrokerBalancer)
                            arena.ReleaseInterface(ref balancer);
                    } 
                }
            }
        }

        bool IFreqManagerEnforcerAdvisor.CanEnterGame(Player player, StringBuilder errorMessage)
        {
            return PlayerMeetsResolutionRequirements(player, errorMessage)
                && ArenaNotFull(player.Arena, errorMessage)
                && PlayerUnderLagLimits(player, errorMessage);

            bool PlayerMeetsResolutionRequirements(Player player, StringBuilder errorMessage)
            {
                if (player == null)
                    return false;

                Arena arena = player.Arena;
                if (arena == null)
                    return false;

                if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                    return false;

                if ((ad.Config.MaxXResolution > 0 && player.Xres > ad.Config.MaxXResolution)
                    || (ad.Config.MaxYResolution > 0 && player.Yres > ad.Config.MaxYResolution))
                {
                    errorMessage?.Append(
                        $"The maximum resolution allowed in this arena is {ad.Config.MaxXResolution} by {ad.Config.MaxYResolution} pixels. " +
                        $"Your resolution is too high ({player.Xres} by {player.Yres}.)");
                    return false;
                }
                else if (ad.Config.MaxResolutionPixels > 0 && (player.Xres * player.Yres) > ad.Config.MaxResolutionPixels)
                {
                    errorMessage?.Append(
                        $"The maximum display area allowed in this arena is {ad.Config.MaxResolutionPixels} pixels. Your display area is too big ({player.Xres * player.Yres}.)");
                    return false;
                }

                return true;
            }

            bool ArenaNotFull(Arena arena, StringBuilder errorMessage)
            {
                if (arena == null)
                    return false;

                if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                    return false;

                int count = 0;

                _playerData.Lock();

                try
                {
                    
                    foreach (Player p in _playerData.Players)
                    {
                        if (p.Arena == arena
                            && p.IsHuman
                            && p.Status == PlayerState.Playing
                            && p.Ship != ShipType.Spec)
                        {
                            count++;
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (count > ad.Config.MaxPlaying)
                {
                    errorMessage?.Append("There are already the maximum number of people playing allowed.");
                    return false;
                }

                return true;
            }

            bool PlayerUnderLagLimits(Player player, StringBuilder errorMessage)
            {
                // TODO: investigate if we can access the LagLimits module via an interface or maybe add an advisor?
                if (player.Flags.NoShip)
                {
                    errorMessage?.Append("You are too lagged to play in this arena.");
                    return false;
                }

                return true;
            }
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            lock (ad.Lock)
            {
                if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
                {
                    ad.Config = new Config(_configManager, arena.Cfg);
                    PruneFreqs(arena);
                }
                else if (action == ArenaAction.Destroy)
                {
                    ad.Freqs.Clear();
                }
            }

            void PruneFreqs(Arena arena)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                    return;

                lock (ad.Lock)
                {
                    for (int i = ad.Freqs.Count - 1; i >= 0; i--)
                    {
                        Freq freq = ad.Freqs[i];
                        if (freq.FreqNum >= ad.Config.RememberedTeams && freq.Players.Count == 0)
                        {
                            ad.Freqs.RemoveAt(i);
                            _freqPool.Return(freq);
                        }
                    }

                    // Now make sure that the required teams exist.
                    for (short i = 0; i < ad.Config.RememberedTeams; i++)
                    {
                        Freq freq = GetFreq(arena, i);
                        if (freq == null)
                        {
                            CreateFreq(arena, i);
                        }
                    }
                }
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            if (action == PlayerAction.PreEnterArena)
            {
                pd.Freq = null;
            }
            else if (action == PlayerAction.EnterArena)
            {
                UpdateFreqs(arena, player, player.Freq, arena.SpecFreq);
            }
            else if (action == PlayerAction.LeaveArena)
            {
                short freqNum = arena.SpecFreq;

                if (pd.Freq != null)
                    freqNum = pd.Freq.FreqNum;

                // Pretend all players leaving pass through the spec freq on their way out.
                UpdateFreqs(arena, player, arena.SpecFreq, freqNum);
            }
        }

        private void Callback_PreShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            UpdateFreqs(player.Arena, player, newFreq, oldFreq);
        }

        #endregion

        private void UpdateFreqs(Arena arena, Player player, short newFreqNum, short oldFreqNum)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            if (newFreqNum == oldFreqNum)
                return;

            // Fake players are not supported. For now, use a custom balancer if you need to support fake players.
            if (player.Type == ClientType.Fake)
                return;

            _playerData.Lock(); // TODO: review this lock
            
            try
            {
                // TODO: module level lock

                // We don't need to bother storing who's on the spectator frequency
                // since we will never be balancing against it or check if it's full.
                if (oldFreqNum != arena.SpecFreq)
                {
                    Freq freq = GetFreq(arena, oldFreqNum);
                    Debug.Assert(freq == pd.Freq);
                    
                    // Remove player from freq.
                    freq.Players.Remove(player);
                    pd.Freq = null;

                    // Possibly disband the freq altogether, if it's not required.
                    if (!freq.IsRemembered && freq.Players.Count == 0)
                    {
                        if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                            return;

                        ad.Freqs.Remove(freq);
                        _freqPool.Return(freq);
                    }
                }

                if (newFreqNum != arena.SpecFreq)
                {
                    Freq freq = GetFreq(arena, newFreqNum);
                    if (freq == null)
                    {
                        freq = CreateFreq(arena, newFreqNum);
                        if (freq == null)
                            return;
                    }

                    // Add player to freq.
                    freq.Players.Add(player);
                    pd.Freq = freq;
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private Freq CreateFreq(Arena arena, short freqNum)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return null;

            Freq freq = _freqPool.Get();
            freq.Initalize(freqNum, in ad.Config);
            ad.Freqs.Add(freq);
            return freq;
        }

        private int GetMaxFreqSize(Arena arena, short freqNum)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return 0;

            return freqNum >= ad.Config.FirstPrivateFreq
                ? ad.Config.MaxPrivateFreqSize
                : ad.Config.MaxPublicFreqSize;
        }

        private bool FreqNotFull(Arena arena, short freqNum, StringBuilder errorMessage)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
                return false;

            int max = GetMaxFreqSize(arena, freqNum);

            if (max <= 0)
            {
                errorMessage?.Append($"Frequency {freqNum} is not available.");
                return false;
            }
            else
            {
                int count = 0;

                _playerData.Lock();

                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        if (p.Arena == arena
                            && p.Freq == freqNum
                            && p.IsHuman
                            && p.Status == PlayerState.Playing
                            && (p.Ship != ShipType.Spec || ad.Config.SpectatorsCountForTeamSize))
                        {
                            count++;
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (count >= max)
                {
                    errorMessage?.Append($"Frequency {freqNum} is full.");
                    return false;
                }
            }

            return true;
        }

        private static ShipType GetShip(ShipMask mask)
        {
            foreach (ShipType checkShip in Enum.GetValues<ShipType>())
            {
                if (checkShip < ShipType.Spec
                    && (mask & checkShip.GetShipMask()) != ShipMask.None)
                {
                    return checkShip;
                }
            }

            return ShipType.Spec;
        }

        private short FindEntryFreq(Arena arena, Player player, StringBuilder errorMessage)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return arena.SpecFreq;

            IFreqBalancer balancer = arena.GetInterface<IFreqBalancer>();
            bool isBrokerBalancer = true;

            if (balancer == null)
            {
                balancer = this;
                isBrokerBalancer = false;
            }

            try
            {
                int playerMetric = GetPlayerMetric(player, balancer);

                short result = arena.SpecFreq;
                int resultMetric = -1;

                short i;

                for (i = 0; i < ad.Config.DesiredTeams; i++)
                {
                    if (!CanChangeToFreq(arena, player, i, errorMessage))
                        continue;

                    Freq freq = GetFreq(arena, i);
                    if (freq == null)
                    {
                        result = i;
                        break;
                    }

                    int freqMetric = GetFreqMetric(freq, balancer);
                    if (freqMetric <= balancer.GetMaxMetric(arena, i) - playerMetric
                        && (result == arena.SpecFreq || freqMetric < resultMetric))
                    {
                        // We have not found a freq yet or this freq is better.
                        result = i;
                        resultMetric = freqMetric;
                    }
                }

                if (result == arena.SpecFreq)
                {
                    // We couldn't find a freq yet.
                    // Note: Right now, i is desiredTeams + 1. This time we'll do things  slightly differently.
                    while (i < ad.Config.NumberOfFrequencies)
                    {
                        Freq freq = GetFreq(arena, i);

                        if (CanChangeToFreq(arena, player, i, errorMessage))
                        {
                            if (freq == null
                                || GetFreqMetric(freq, balancer) <= balancer.GetMaxMetric(arena, i) - playerMetric)
                            {
                                result = i;
                                break;
                            }
                        }
                        else if (freq == null)
                        {
                            // Failed on an empty freq.
                            // Abort, there is probably some sort of blocker that would be pointless to call repeatedly for other freqs.
                            break;
                        }

                        i++;
                    }
                }

                if (result != arena.SpecFreq)
                {
                    // Check one final time if we have a result.
                    // If so, clear any error messages that may have been set while checking other freqs.
                    errorMessage?.Clear();
                }

                return result;
            }
            finally
            {
                if (isBrokerBalancer)
                    arena.ReleaseInterface(ref balancer);
            }
        }

        private Freq GetFreq(Arena arena, short freqNum)
        {
            if (arena == null)
                return null;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return null;

            //return ad.Freqs.Find(f => f.FreqNum == freqNum); // TODO: check if this allocates a new delegate object each call

            foreach (Freq freq in ad.Freqs)
                if (freq.FreqNum == freqNum)
                    return freq;

            return null;
        }

        private static int GetPlayerMetric(Player player, IFreqBalancer balancer)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (balancer == null)
                throw new ArgumentNullException(nameof(balancer));

            return player.IsHuman ? balancer.GetPlayerMetric(player) : 0;
        }

        private int GetFreqMetric(Freq freq, IFreqBalancer balancer)
        {
            if (freq == null)
                return 0;

            if (balancer == null)
                throw new ArgumentNullException(nameof(balancer));

            _playerData.Lock(); // TODO: review this lock

            // TODO: module level lock

            try
            {
                int result = 0;

                foreach (Player player in freq.Players)
                {
                    result += GetPlayerMetric(player, balancer);
                }

                return result;
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private static ShipMask GetAllowableShips(Arena arena, Player player, ShipType ship, short freq, StringBuilder errorMessage)
        {
            if (freq == arena.SpecFreq)
                return ShipMask.None;

            ShipMask mask = ShipMask.All;

            foreach (var advisor in arena.GetAdvisors<IFreqManagerEnforcerAdvisor>())
            {
                mask &= advisor.GetAllowableShips(player, ship, freq, errorMessage);

                if (mask == ShipMask.None)
                    break; // The player can't use any ships, so no need to ask any additional advisors.
            }

            return mask;
        }

        private static bool CanChangeToFreq(Arena arena, Player player, short newFreq, StringBuilder errorMessage)
        {
            foreach (var advisor in arena.GetAdvisors<IFreqManagerEnforcerAdvisor>())
            {
                if (!advisor.CanChangeToFreq(player, newFreq, errorMessage))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool CanEnterGame(Arena arena, Player player, StringBuilder errorMessage)
        {
            foreach (var advisor in arena.GetAdvisors<IFreqManagerEnforcerAdvisor>())
            {
                if (!advisor.CanEnterGame(player, errorMessage))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsUnlocked(Arena arena, Player player, StringBuilder errorMessage)
        {
            foreach (var advisor in arena.GetAdvisors<IFreqManagerEnforcerAdvisor>())
            {
                if (!advisor.IsUnlocked(player, errorMessage))
                {
                    return false;
                }
            }

            return true;
        }

        #region Helper types

        private readonly struct Config
        {
            public readonly int NumberOfFrequencies;
            public readonly int RequiredTeams;
            public readonly int RememberedTeams;
            public readonly int DesiredTeams;
            public readonly int FirstPrivateFreq;
            public readonly int FirstBalancedFreq;
            public readonly int LastBalancedFreq;
            public readonly bool DisallowTeamSpectators;
            public readonly bool AlwaysStartInSpec;
            public readonly int MaxPlaying;
            public readonly int MaxPublicFreqSize;
            public readonly int MaxPrivateFreqSize;
            public readonly bool SpectatorsCountForTeamSize;
            public readonly int MaxXResolution;
            public readonly int MaxYResolution;
            public readonly int MaxResolutionPixels;
            public readonly bool DefaultBalancer_forceEvenTeams;
            public readonly int DefaultBalancer_maxDifference;

            [ConfigHelp("Team", "MaxFrequency", ConfigScope.Arena, typeof(int), DefaultValue = "10000",
                Description = "One more than the highest frequency allowed. Set this below PrivFreqStart to disallow private freqs.")]
            [ConfigHelp("Team", "DesiredTeams", ConfigScope.Arena, typeof(int), DefaultValue = "2",
                Description = "The number of teams that the freq balancer will form as players enter.")]
            [ConfigHelp("Team", "RequiredTeams", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "The number of teams that the freq manager will require to exist.")]
            [ConfigHelp("Team", "RememberedTeams", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "The number of teams that the freq manager will keep in memory. Must be at least as high as RequiredTeams.")]
            [ConfigHelp("Team", "PrivFreqStart", ConfigScope.Arena, typeof(int), DefaultValue = "100",
                Description = "Freqs above this value are considered private freqs.")]
            [ConfigHelp("Team", "BalancedAgainstStart", ConfigScope.Arena, typeof(int), DefaultValue = "1",
                Description = "Freqs >= BalancedAgainstStart and < BalancedAgainstEnd will be" +
                "checked for balance even when players are not changing to or from" +
                "these freqs. Set End < Start to disable this check.")]
            [ConfigHelp("Team", "BalancedAgainstEnd", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "Freqs >= BalancedAgainstStart and < BalancedAgainstEnd will be" +
                "checked for balance even when players are not changing to or from" +
                "these freqs. Set End < Start to disable this check.")]
            [ConfigHelp("Team", "DisallowTeamSpectators", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
                Description = "If players are allowed to spectate outside of the spectator frequency.")]
            [ConfigHelp("Team", "InitialSpec", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
                Description = "If players entering the arena are always assigned to spectator mode.")]
            [ConfigHelp("Team", "MaxPlaying", ConfigScope.Arena, typeof(int), DefaultValue = "100",
                Description = "This is the most players that will be allowed to play in the arena at once. Zero means no limit.")]
            [ConfigHelp("Team", "MaxPerTeam", ConfigScope.Arena, typeof(int), DefaultValue = "1000",
                Description = "The maximum number of players on a public freq. Zero means these teams are not accessible.")]
            [ConfigHelp("Team", "MaxPerPrivateTeam", ConfigScope.Arena, typeof(int), DefaultValue = "1000",
                Description = "The maximum number of players on a private freq. Zero means these teams are not accessible.")]
            [ConfigHelp("Team", "IncludeSpectators", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
                Description = "Whether to include spectators when enforcing maximum freq sizes.")]
            [ConfigHelp("Team", "MaxXres", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "Maximum screen width allowed in the arena. Zero means no limit.")]
            [ConfigHelp("Team", "MaxYres", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "Maximum screen height allowed in the arena. Zero means no limit.")]
            [ConfigHelp("Team", "MaxResArea", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "Maximum screen area (x*y) allowed in the arena, Zero means no limit.")]
            [ConfigHelp("Team", "ForceEvenTeams", ConfigScope.Arena, typeof(int), DefaultValue = "0",
                Description = "Whether the default balancer will enforce even teams. Does not apply if a custom balancer module is used.")]
            [ConfigHelp("Team", "MaxTeamDifference", ConfigScope.Arena, typeof(int), DefaultValue = "1",
                Description = "How many players difference the balancer should tolerate. Does not apply if a custom balancer module is used.")]
            public Config(IConfigManager configManager, ConfigHandle ch)
            {
                NumberOfFrequencies = Math.Clamp(configManager.GetInt(ch, "Team", "MaxFrequency", 10000), 1, 10000);

                DesiredTeams = configManager.GetInt(ch, "Team", "DesiredTeams", 2);
                if (DesiredTeams < 0 || DesiredTeams > NumberOfFrequencies)
                    DesiredTeams = 0;

                RequiredTeams = configManager.GetInt(ch, "Team", "RequiredTeams", 0);
                if (RequiredTeams < 0 || RequiredTeams > NumberOfFrequencies)
                    RequiredTeams = 0;

                RememberedTeams = configManager.GetInt(ch, "Team", "RememberedTeams", 0);
                if (RememberedTeams < 0 || RememberedTeams > NumberOfFrequencies)
                    RememberedTeams = 0;
                else if (RememberedTeams < RequiredTeams)
                    RememberedTeams = RequiredTeams;

                FirstPrivateFreq = Math.Clamp(configManager.GetInt(ch, "Team", "PrivFreqStart", 100), 0, 9999);
                FirstBalancedFreq = configManager.GetInt(ch, "Team", "BalancedAgainstStart", 1);
                LastBalancedFreq = configManager.GetInt(ch, "Team", "BalancedAgainstEnd", 0);
                DisallowTeamSpectators = configManager.GetInt(ch, "Team", "DisallowTeamSpectators", 0) != 0;
                AlwaysStartInSpec = configManager.GetInt(ch, "Team", "InitialSpec", 0) != 0;
                MaxPlaying = configManager.GetInt(ch, "Team", "MaxPlaying", 100);
                MaxPublicFreqSize = configManager.GetInt(ch, "Team", "MaxPerTeam", 1000);
                MaxPrivateFreqSize = configManager.GetInt(ch, "Team", "MaxPerPrivateTeam", 1000);
                SpectatorsCountForTeamSize = configManager.GetInt(ch, "Team", "IncludeSpectators", 0) != 0;
                MaxXResolution = configManager.GetInt(ch, "Team", "MaxXres", 0);
                MaxYResolution = configManager.GetInt(ch, "Team", "MaxYres", 0);
                MaxResolutionPixels = configManager.GetInt(ch, "Team", "MaxResArea", 0);
                DefaultBalancer_forceEvenTeams = configManager.GetInt(ch, "Team", "ForceEvenTeams", 0) != 0;
                DefaultBalancer_maxDifference = configManager.GetInt(ch, "Team", "MaxTeamDifference", 1);
                if (DefaultBalancer_maxDifference < 1)
                    DefaultBalancer_maxDifference = 1;
            }
        }

        private class ArenaData : IPooledExtraData
        {
            public readonly List<Freq> Freqs = new();
            public Config Config;

            public readonly object Lock = new(); // TODO: I think everything should be done serially on the arena level, ASSS has some strange locking using the PlayerData lock and a module level lock.

            public void Reset()
            {
                lock (Lock)
                {
                    Freqs.Clear();
                    Config = default;
                }
            }
        }

        private class PlayerData : IPooledExtraData
        {
            public Freq Freq;

            public void Reset()
            {
                Freq = null;
            }
        }

        private class Freq
        {
            public readonly HashSet<Player> Players = new();
            public short FreqNum { get; private set; }
            public bool IsRequired { get; private set; }
            public bool IsRemembered { get; private set; }
            public bool IsBalancedAgainst { get; private set; }

            public Freq()
            {
                Reset();
            }

            public void Initalize(short freqNum, in Config config)
            {
                FreqNum = freqNum;
                IsRequired = FreqNum < config.RequiredTeams;
                IsRemembered = FreqNum < config.RememberedTeams;
                IsBalancedAgainst = config.FirstBalancedFreq <= FreqNum && FreqNum < config.LastBalancedFreq;
            }

            public void Reset()
            {
                Players.Clear();
                FreqNum = -1;
                IsRequired = false;
                IsRemembered = false;
                IsBalancedAgainst = false;
            }
        }

        private class FreqPooledObjectPolicy : PooledObjectPolicy<Freq>
        {
            public override Freq Create()
            {
                return new Freq();
            }

            public override bool Return(Freq freq)
            {
                if (freq == null)
                    return false;

                freq.Reset();

                return true;
            }
        }

        #endregion
    }
}
