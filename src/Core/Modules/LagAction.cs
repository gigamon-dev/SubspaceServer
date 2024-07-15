using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that watches player lag and takes action on those that pass certain thresholds.
    /// It watches a player's ping and packetloss statistics.
    /// Also, it looks for spikes (not receiving data from a player for an amount of time).
    /// 
    /// <para>
    /// Actions taken include:
    /// <list type="bullet">
    /// <item>Forcing a player into spectator mode.</item>
    /// <item>Penalizing a player by ignoring a percentage of their weapons fire.</item>
    /// <item>Disallowing a player from picking up flags/balls.</item>
    /// </list>
    /// </para>
    /// </summary>
    [CoreModuleInfo]
    public class LagAction : IModule
    {
        private IArenaManager _arenaManager;
        private IChat _chat;
        private IConfigManager _configManager;
        private IGame _game;
        private ILagQuery _lagQuery;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private INetwork _network;
        private IPlayerData _playerData;

        private ArenaDataKey<ArenaLagLimits> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        private TimeSpan _checkInterval;
        private Thread _checkThread;
        private CancellationTokenSource _cancellationTokenSource;
        private CancellationToken _cancellationToken;

        private readonly Action<Player> _mainloopWorkItem_checkPlayer;

        public LagAction()
        {
            _mainloopWorkItem_checkPlayer = MainloopWorkItem_CheckPlayer;
		}

		#region Module members

		[ConfigHelp("Lag", "CheckInterval", ConfigScope.Global, typeof(int), DefaultValue = "300", 
            Description = "How often to check each player for out-of-bounds lag values (in ticks).")]
        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IChat chat,
            IConfigManager configManager,
            IGame game,
            ILagQuery lagQuery,
            ILogManager logManager,
            IMainloop mainloop,
            INetwork network,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _lagQuery = lagQuery ?? throw new ArgumentNullException(nameof(lagQuery));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _adKey = _arenaManager.AllocateArenaData<ArenaLagLimits>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            _checkInterval = TimeSpan.FromMilliseconds(_configManager.GetInt(_configManager.Global, "Lag", "CheckInterval", 300) * 10);

            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _cancellationTokenSource = new CancellationTokenSource();
            _cancellationToken = _cancellationTokenSource.Token;
            _checkThread = new Thread(CheckThread);
            _checkThread.Name = nameof(LagAction);
            _checkThread.Start();

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _cancellationTokenSource.Cancel();
            _checkThread.Join();
            _checkThread = null;
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _arenaManager.FreeArenaData(ref _adKey);
            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        #endregion

        [ConfigHelp("Lag", "PingToSpec", ConfigScope.Arena, typeof(int), DefaultValue = "600",
            Description = "The average ping at which to force a player to spec.")]
        [ConfigHelp("Lag", "PingToStartIgnoringWeapons", ConfigScope.Arena, typeof(int), DefaultValue = "300",
            Description = "The average ping at which to start ignoring weapons.")]
        [ConfigHelp("Lag", "PingToIgnoreAllWeapons", ConfigScope.Arena, typeof(int), DefaultValue = "1000",
            Description = "The average ping at which all weapons should be ignored.")]
        [ConfigHelp("Lag", "PingToDisallowFlags", ConfigScope.Arena, typeof(int), DefaultValue = "1000",
            Description = "The average ping at which a player isn't allowed to pick up flags or balls.")]
        [ConfigHelp("Lag", "S2CLossToSpec", ConfigScope.Arena, typeof(int), DefaultValue = "150",
            Description = "The S2C packetloss at which to force a player to spec. Units 0.1%.")]
        [ConfigHelp("Lag", "S2CLossToStartIgnoringWeapons", ConfigScope.Arena, typeof(int), DefaultValue = "40",
            Description = "The S2C packetloss at which to start ignoring weapons. Units 0.1%.")]
        [ConfigHelp("Lag", "S2CLossToIgnoreAllWeapons", ConfigScope.Arena, typeof(int), DefaultValue = "500",
            Description = "The S2C packetloss at which all weapons should be ignored. Units 0.1%.")]
        [ConfigHelp("Lag", "S2CLossToDisallowFlags", ConfigScope.Arena, typeof(int), DefaultValue = "50",
            Description = "The S2C packetloss at which a player isn't allowed to pick up flags or balls. Units 0.1%.")]
        [ConfigHelp("Lag", "WeaponLossToSpec", ConfigScope.Arena, typeof(int), DefaultValue = "150",
            Description = "The weapon packetloss at which to force a player to spec. Units 0.1%.")]
        [ConfigHelp("Lag", "WeaponLossToStartIgnoringWeapons", ConfigScope.Arena, typeof(int), DefaultValue = "40",
            Description = "The weapon packetloss at which to start ignoring weapons. Units 0.1%.")]
        [ConfigHelp("Lag", "WeaponLossToIgnoreAllWeapons", ConfigScope.Arena, typeof(int), DefaultValue = "500",
            Description = "The weapon packetloss at which all weapons should be ignored. Units 0.1%.")]
        [ConfigHelp("Lag", "WeaponLossToDisallowFlags", ConfigScope.Arena, typeof(int), DefaultValue = "50",
            Description = "The weapon packetloss at which a player isn't allowed to pick up flags or balls. Units 0.1%.")]
        [ConfigHelp("Lag", "C2SLossToSpec", ConfigScope.Arena, typeof(int), DefaultValue = "150",
            Description = "The C2S packetloss at which to force a player to spec. Units 0.1%.")]
        [ConfigHelp("Lag", "C2SLossToDisallowFlags", ConfigScope.Arena, typeof(int), DefaultValue = "50",
            Description = "The C2S packetloss at which a player isn't allowed to pick up flags or balls. Units 0.1%.")]
        [ConfigHelp("Lag", "SpikeToSpec", ConfigScope.Arena, typeof(int), DefaultValue = "3000",
            Description = "The amount of time (in ms) the server can get no data from a player before forcing him to spectator mode.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaLagLimits lagLimits))
                    return;

                ConfigHandle ch = arena.Cfg;

                lock (lagLimits.Lock)
                {
                    lagLimits.Ping = new PingLimits
                    {
                        ForceSpec = _configManager.GetInt(ch, "Lag", "PingToSpec", 600),
                        IgnoreWeaponStart = _configManager.GetInt(ch, "Lag", "PingToStartIgnoringWeapons", 300),
                        IgnoreWeaponAll = _configManager.GetInt(ch, "Lag", "PingToIgnoreAllWeapons", 1000),
                        NoFlags = _configManager.GetInt(ch, "Lag", "PingToDisallowFlags", 500),
                    };

                    lagLimits.S2CLoss = new S2CPacketlossLimits
                    {
                        ForceSpec = _configManager.GetInt(ch, "Lag", "S2CLossToSpec", 150),
                        IgnoreWeaponStart = _configManager.GetInt(ch, "Lag", "S2CLossToStartIgnoringWeapons", 40),
                        IgnoreWeaponAll = _configManager.GetInt(ch, "Lag", "S2CLossToIgnoreAllWeapons", 500),
                        NoFlags = _configManager.GetInt(ch, "Lag", "S2CLossToDisallowFlags", 50),
                    };

                    lagLimits.WeaponLoss = new S2CPacketlossLimits
                    {
                        ForceSpec = _configManager.GetInt(ch, "Lag", "WeaponLossToSpec", 150),
                        IgnoreWeaponStart = _configManager.GetInt(ch, "Lag", "WeaponLossToStartIgnoringWeapons", 40),
                        IgnoreWeaponAll = _configManager.GetInt(ch, "Lag", "WeaponLossToIgnoreAllWeapons", 500),
                        NoFlags = _configManager.GetInt(ch, "Lag", "WeaponLossToDisallowFlags", 50),
                    };

                    lagLimits.C2SLoss = new C2SPacketlossLimits
                    {
                        ForceSpec = _configManager.GetInt(ch, "Lag", "C2SLossToSpec", 150),
                        NoFlags = _configManager.GetInt(ch, "Lag", "C2SLossToDisallowFlags", 50),
                    };

                    lagLimits.SpikeForceSpec = TimeSpan.FromMilliseconds(_configManager.GetInt(ch, "Lag", "SpikeToSpec", 3000));

                    lagLimits.SpecFreq = arena.SpecFreq;
                }
            }
        }

        private void CheckThread()
        {
            WaitHandle cancellationWaitHandle = _cancellationToken.WaitHandle;

            while (!_cancellationToken.IsCancellationRequested)
            {
                DateTime now = DateTime.UtcNow;
                Player toCheck = null;
                PlayerData toCheckPlayerData = null;
                int playerCount;

                _playerData.Lock();

                try
                {
                    DateTime? lastChecked = null;

                    foreach (Player p in _playerData.Players)
                    {
                        if (p.Status == PlayerState.Playing
                            && p.IsStandard
                            && p.TryGetExtraData(_pdKey, out PlayerData pd))
                        {
                            lock (pd.Lock)
                            {
                                if (!pd.IsChecking
                                    && now - pd.LastCheck > _checkInterval
                                    && (toCheck == null || pd.LastCheck < lastChecked))
                                {
                                    toCheck = p;
                                    toCheckPlayerData = pd;
                                }
                            }
                        }
                    }

                    playerCount = _playerData.Players.Count;
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (toCheck != null)
                {
                    lock (toCheckPlayerData.Lock)
                    {
                        toCheckPlayerData.IsChecking = true;
                    }

                    // TODO: Review threading logic and maybe move the logic onto this worker thread if it's safe. For now, using the mainloop thread to do it.
                    // Queue the actual lag check to be done by the mainloop thread.
                    _mainloop.QueueMainWorkItem(_mainloopWorkItem_checkPlayer, toCheck);
                }

                cancellationWaitHandle.WaitOne(playerCount > 0 ? _checkInterval / playerCount : _checkInterval);
            }
        }

        private void MainloopWorkItem_CheckPlayer(Player player)
        {
            if (player == null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            try
            {
                if (player.Status != PlayerState.Playing)
                    return;

                Arena arena = player.Arena;
                if (arena == null)
                    return;

                if (!arena.TryGetExtraData(_adKey, out ArenaLagLimits lagLimits))
                    return;

                lock (lagLimits.Lock)
                {
                    CheckSpike(player, lagLimits);
                    CheckLag(player, lagLimits);
                }
            }
            finally
            {
                lock (pd.Lock)
                {
                    pd.IsChecking = false;
                    pd.LastCheck = DateTime.UtcNow;
                }
            }
        }

        private void CheckSpike(Player player, ArenaLagLimits lagLimits)
        {
            if (player == null)
                return;

            TimeSpan lastReceive = _network.GetLastReceiveTimeSpan(player);
            if (lastReceive > lagLimits.SpikeForceSpec
                && Spec(player, lagLimits.SpecFreq, "spike"))
            {
                _chat.SendMessage(player, $"You have been specced for a {lastReceive.TotalMilliseconds} ms spike.");
            }
        }

        private void CheckLag(Player player, ArenaLagLimits lagLimits)
        {
            // gather data
            _lagQuery.QueryPositionPing(player, out PingSummary positionPing);
            _lagQuery.QueryClientPing(player, out ClientPingSummary clientPing);
            _lagQuery.QueryReliablePing(player, out PingSummary reliablePing);
            _lagQuery.QueryPacketloss(player, out PacketlossSummary packetloss);

            // average all pings together with reliable ping counted twice
            int averagePing = (positionPing.Average + clientPing.Average + (2 * reliablePing.Average)) / 4;

            // check conditions that force spec
            if (averagePing > lagLimits.Ping.ForceSpec)
            {
                player.Flags.NoShip = true;

                if (Spec(player, lagLimits.SpecFreq, "ping"))
                    _chat.SendMessage(player, $"You have been specced for excessive ping ({averagePing} > {lagLimits.Ping.ForceSpec}.");
            }
            else if (packetloss.s2c > lagLimits.S2CLoss.ForceSpec)
            {
                player.Flags.NoShip = true;

                if (Spec(player, lagLimits.SpecFreq, "s2c ploss"))
                    _chat.SendMessage(player, $"You have been specced for excessive S2C packetloss ({packetloss.s2c:P2} > {lagLimits.S2CLoss.ForceSpec:P2}.");
            }
            else if (packetloss.s2cwpn > lagLimits.WeaponLoss.ForceSpec)
            {
                player.Flags.NoShip = true;

                if (Spec(player, lagLimits.SpecFreq, "s2c wpn ploss"))
                    _chat.SendMessage(player, $"You have been specced for excessive S2C weapon packetloss ({packetloss.s2cwpn:P2} > {lagLimits.WeaponLoss.ForceSpec:P2}.");
            }
            else if (packetloss.c2s > lagLimits.C2SLoss.ForceSpec)
            {
                player.Flags.NoShip = true;

                if (Spec(player, lagLimits.SpecFreq, "c2s ploss"))
                    _chat.SendMessage(player, $"You have been specced for excessive C2S packetloss ({packetloss.c2s:P2} > {lagLimits.C2SLoss.ForceSpec:P2}.");
            }
            else
            {
                player.Flags.NoShip = false;
            }

            // check conditions for disallowing flags/balls
            player.Flags.NoFlagsBalls = averagePing > lagLimits.Ping.NoFlags
                || packetloss.s2c > lagLimits.S2CLoss.NoFlags
                || packetloss.s2cwpn > lagLimits.WeaponLoss.NoFlags
                || packetloss.c2s > lagLimits.C2SLoss.NoFlags;

            // calculate weapon ignore percent
            double ignore1 = (double)(averagePing - lagLimits.Ping.IgnoreWeaponStart) / (lagLimits.Ping.IgnoreWeaponAll - lagLimits.Ping.IgnoreWeaponStart);
            double ignore2 = (double)(packetloss.s2c - lagLimits.S2CLoss.IgnoreWeaponStart) / (lagLimits.S2CLoss.IgnoreWeaponAll - lagLimits.S2CLoss.IgnoreWeaponStart);
            double ignore3 = (double)(packetloss.s2cwpn - lagLimits.WeaponLoss.IgnoreWeaponStart) / (lagLimits.WeaponLoss.IgnoreWeaponAll - lagLimits.WeaponLoss.IgnoreWeaponStart);

            // use the max of the 3
            _game.SetIgnoreWeapons(player, Math.Clamp(Math.Max(Math.Max(ignore1, ignore2), ignore3), 0d, 1d));
        }

        private bool Spec(Player player, short specFreq, string reason)
        {
            if (player.Ship == ShipType.Spec)
                return false;

            _game.SetShipAndFreq(player, ShipType.Spec, specFreq);
            _logManager.LogP(LogLevel.Info, nameof(LagAction), player, $"specced for: {reason}");
            return true;
        }

        private struct PingLimits
        {
            /// <summary>
            /// Ping at which to force a player to spec.
            /// </summary>
            public int ForceSpec { get; init; }

            /// <summary>
            /// Ping at which to start ignoring weapons.
            /// </summary>
            public int IgnoreWeaponStart { get; init; }

            /// <summary>
            /// Ping at which all weapons should be ignored.
            /// </summary>
            public int IgnoreWeaponAll { get; init; }

            /// <summary>
            /// Ping at which a player isn't allowed to pick up flags or balls.
            /// </summary>
            public int NoFlags { get; init; }
        }

        private struct S2CPacketlossLimits
        {
            /// <summary>
            /// Packetloss at which to force a player to spec.
            /// </summary>
            public double ForceSpec;

            /// <summary>
            /// Packetloss at which to start ignoring weapons.
            /// </summary>
            public double IgnoreWeaponStart;

            /// <summary>
            /// Packetloss at which all weapons should be ignored.
            /// </summary>
            public double IgnoreWeaponAll;

            /// <summary>
            /// Packetloss at which a player isn't allowed to pick up flags or balls.
            /// </summary>
            public double NoFlags;
        }

        private struct C2SPacketlossLimits
        {
            /// <summary>
            /// Packetloss at which to force a player to spec.
            /// </summary>
            public double ForceSpec;

            /// <summary>
            /// Packetloss at which a player isn't allowed to pick up flags or balls.
            /// </summary>
            public double NoFlags;
        }

        private class ArenaLagLimits : IResettable
        {
            /// <summary>
            /// Limits for average ping.
            /// </summary>
            public PingLimits Ping;

            /// <summary>
            /// S2C packetloss limits.
            /// </summary>
            public S2CPacketlossLimits S2CLoss;

            /// <summary>
            /// S2C weapon packetloss limits.
            /// </summary>
            public S2CPacketlossLimits WeaponLoss;

            /// <summary>
            /// C2S packetloss limits.
            /// </summary>
            public C2SPacketlossLimits C2SLoss;

            /// <summary>
            /// Length of time of not receiving data at which to force a player to spec.
            /// </summary>
            public TimeSpan SpikeForceSpec;

            /// <summary>
            /// The spectator frequency of the arena.
            /// </summary>
            public short SpecFreq;

            public readonly object Lock = new();

			public bool TryReset()
			{
                lock (Lock)
                {
                    Ping = default;
                    S2CLoss = default;
                    WeaponLoss = default;
                    C2SLoss = default;
                    SpikeForceSpec = default;
                    SpecFreq = default;
                }

                return true;
            }
        }

        private class PlayerData : IResettable
        {
            /// <summary>
            /// When the last lag check was performed.
            /// </summary>
            public DateTime LastCheck = DateTime.MinValue;

            /// <summary>
            /// Whether the player is currently being checked.
            /// </summary>
            public bool IsChecking = false;

            /// <summary>
            /// Lock to hold before accessing any of the data members of this class.
            /// </summary>
            public readonly object Lock = new();

			public bool TryReset()
			{
                lock (Lock)
                {
                    LastCheck = DateTime.MinValue;
                    IsChecking = false;
                }

                return true;
            }
        }
    }
}
