using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that manages the client-side settings.
    /// Client-side settings are the settings sent to the client via a <see cref="S2C_ClientSettings"/> (includes ship settings and more).
    /// Settings are loaded from disk when an arena is loaded and when there is a config change.
    /// </summary>
    [CoreModuleInfo]
    public class ClientSettings : IModule, IClientSettings
    {
        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private INetwork _net;
        private IPlayerData _playerData;
        private IPrng _prng;
        private InterfaceRegistrationToken _iClientSettingsToken;

        private int _adkey;
        private int _pdkey;

        private readonly object _setMtx = new();

        private class ArenaClientSettingsData
        {
            public S2C_ClientSettings cs = new();

            /// <summary>
            /// prizeweight partial sums. 1-28 are used for now, representing prizes 1 to 28.
            /// 0 = null prize
            /// </summary>
            public ushort[] pwps = new ushort[32];
        }

        private class PlayerClientSettingsData
        {
        }

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            ILogManager logManager,
            INetwork net,
            IPlayerData playerData,
            IPrng prng)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));

            _adkey = _arenaManager.AllocateArenaData<ArenaClientSettingsData>();
            _pdkey = _playerData.AllocatePlayerData<PlayerClientSettingsData>();

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            _iClientSettingsToken = _broker.RegisterInterface<IClientSettings>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<IClientSettings>(ref _iClientSettingsToken) != 0)
                return false;

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);

            _arenaManager.FreeArenaData(_adkey);
            _playerData.FreePlayerData(_pdkey);

            return true;
        }

        #endregion

        #region IClientSettings Members

        void IClientSettings.SendClientSettings(Player p)
        {
            if (p == null)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (arena[_adkey] is not ArenaClientSettingsData ad)
                return;

            lock (_setMtx)
            {
                SendOneSettings(p, ad);
            }
        }

        uint IClientSettings.GetChecksum(Player p, uint key)
        {
            if (p == null)
                return 0;

            Arena arena = p.Arena;
            if (arena == null)
                return 0;

            if (arena[_adkey] is not ArenaClientSettingsData ad)
                return 0;

            lock (_setMtx)
            {
                return ad.cs.GetChecksum(key);
            }
        }

        Prize IClientSettings.GetRandomPrize(Arena arena)
        {
            if(arena == null)
                return 0;

            if (arena[_adkey] is not ArenaClientSettingsData ad)
                return 0;

            int max = ad.pwps[28];

            if (max == 0)
                return 0;

            int i = 0;
            int j = 28;

            int r;

            r = _prng.Number(0, max - 1);

            // binary search
            while (r >= ad.pwps[i])
            {
                int m = (i + j) / 2;
                if (r < ad.pwps[m])
                    j = m;
                else
                    i = m + 1;
            }

            return (Prize)i;
        }

        //ClientSettingOverrideKey IClientSettings.GetOverrideKey(string section, string key)
        //{
        //    return new ClientSettingOverrideKey();
        //}

        //void IClientSettings.ArenaOverride(Arena arena, ClientSettingOverrideKey key, int val)
        //{
            
        //}

        //void IClientSettings.PlayerOverride(Player p, ClientSettingOverrideKey key)
        //{
            
        //}

        #endregion

        [ConfigHelp("Misc", "SendUpdatedSettings", ConfigScope.Arena, typeof(bool), DefaultValue = "1", 
            Description ="Whether to send updates to players when the arena settings change.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            if (arena[_adkey] is not ArenaClientSettingsData ad)
                return;

            lock (_setMtx)
            {
                if (action == ArenaAction.Create)
                {
                    LoadSettings(ad, arena.Cfg);
                }
                else if(action == ArenaAction.ConfChanged)
                {
                    bool sendUpdated = _configManager.GetInt(arena.Cfg, "Misc", "SendUpdatedSettings", 1) != 0;

                    S2C_ClientSettings old = ad.cs;

                    LoadSettings(ad, arena.Cfg);

                    if (sendUpdated)
                    {
                        Span<byte> oldSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref old, 1));
                        Span<byte> newSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref ad.cs, 1));

                        if (!oldSpan.SequenceEqual(newSpan))
                        {
                            _logManager.LogA(LogLevel.Info, nameof(ClientSettings), arena, "Sending modified settings.");

                            _playerData.Lock();

                            try
                            {
                                foreach (Player p in _playerData.PlayerList)
                                {
                                    if (p.Arena == arena && p.Status == PlayerState.Playing)
                                        SendOneSettings(p, ad);
                                }
                            }
                            finally
                            {
                                _playerData.Unlock();
                            }
                        }
                    }
                }
                else if (action == ArenaAction.Destroy)
                {
                    // mark settings as destroyed (for asserting later)
                    ad.cs.Type = 0;
                }
            }
        }

        [ConfigHelp("Bullet", "ExactDamage", ConfigScope.Arena, typeof(bool), DefaultValue ="0", Description = "Whether to use exact bullet damage (Cont .36+)")]
        [ConfigHelp("Spectator", "HideFlags", ConfigScope.Arena, typeof(bool), DefaultValue = "0", Description = "Whether to show dropped flags to spectators (Cont .36+)")]
        [ConfigHelp("Spectator", "NoXRadar", ConfigScope.Arena, typeof(bool), DefaultValue = "0", Description = "Whether spectators are disallowed from having X radar (Cont .36+)")]
        [ConfigHelp("Misc", "SlowFrameCheck", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Description = "")]
        [ConfigHelp("Misc", "DisableScreenshot", ConfigScope.Arena, typeof(bool), DefaultValue = "0", Description = "Whether to disable Continuum's screenshot feature (Cont .37+)")]
        [ConfigHelp("Misc", "MaxTimerDrift", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Description = "")]
        [ConfigHelp("Soccer", "DisableWallPass", ConfigScope.Arena, typeof(bool), DefaultValue = "0", Description = "Whether to disable ball-passing through walls (Cont .38+)")]
        [ConfigHelp("Soccer", "DisableBallKilling", ConfigScope.Arena, typeof(bool), DefaultValue = "0", Description = "Whether to disable ball killing in safe zones (Cont .38+)")]
        [ConfigHelp("All", "ShrapnelMax", ConfigScope.Arena, typeof(byte), "Maximum amount of shrapnel released from a ship's bomb")]
        [ConfigHelp("All", "ShrapnelRate", ConfigScope.Arena, typeof(byte), "Amount of additional shrapnel gained by a 'Shrapnel Upgrade' prize.")]
        [ConfigHelp("All", "AntiWarpStatus", ConfigScope.Arena, typeof(byte), Range = "0-2", Description = "Whether ships are allowed to receive 'Anti-Warp' 0=no 1=yes 2=yes/start-with")]
        [ConfigHelp("All", "CloakStatus", ConfigScope.Arena, typeof(byte), Range = "0-2", Description = "Whether ships are allowed to receive 'Cloak' 0=no 1=yes 2=yes/start-with")]
        [ConfigHelp("All", "StealthStatus", ConfigScope.Arena, typeof(byte), Range = "0-2", Description = "Whether ships are allowed to receive 'Stealth' 0=no 1=yes 2=yes/start-with")]
        [ConfigHelp("All", "XRadarStatus", ConfigScope.Arena, typeof(byte), Range = "0-2", Description = "Whether ships are allowed to receive 'X-Radar' 0=no 1=yes 2=yes/start-with")]
        [ConfigHelp("All", "InitialGuns", ConfigScope.Arena, typeof(byte), Range = "0-3", Description = "Initial level a ship's guns fire")]
        [ConfigHelp("All", "MaxGuns", ConfigScope.Arena, typeof(byte), Range = "0-3", Description = "Maximum level a ship's guns can fire")]
        [ConfigHelp("All", "InitialBombs", ConfigScope.Arena, typeof(byte), Range = "0-3", Description = "Initial level a ship's bombs fire")]
        [ConfigHelp("All", "MaxBombs", ConfigScope.Arena, typeof(byte), Range = "0-3", Description = "Maximum level a ship's bombs can fire")]
        [ConfigHelp("All", "DoubleBarrel", ConfigScope.Arena, typeof(bool), "Whether ships fire with double barrel bullets")]
        [ConfigHelp("All", "EmpBomb", ConfigScope.Arena, typeof(bool), "Whether ships fire EMP bombs")]
        [ConfigHelp("All", "SeeMines", ConfigScope.Arena, typeof(bool), "Whether ships see mines on radar")]
        [ConfigHelp("All", "SeeBombLevel", ConfigScope.Arena, typeof(int), Range = "0-4", Description = "If ship can see bombs on radar (0=Disabled, 1=All, 2=L2 and up, 3=L3 and up, 4=L4 bombs only)")]
        [ConfigHelp("All", "DisableFastShooting", ConfigScope.Arena, typeof(bool), "If firing bullets, bombs, or thors is disabled after using afterburners (1=enabled) (Cont .36+)")]
        [ConfigHelp("All", "Radius", ConfigScope.Arena, typeof(int), Range = "0-255", DefaultValue = "14", Description = "The ship's radius from center to outside, in pixels. (Cont .37+)")]
        [ConfigHelp("Spawn", "Team0-X", ConfigScope.Arena, typeof(ushort), Range = "0-1024", Description = "Spawn location x-coordinate. If only Team0 variables are set, all teams use them.  If Team0 and Team1 variables are set, even teams use Team0 and odd teams use Team1. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team1-X", ConfigScope.Arena, typeof(ushort), Range = "0-1024", Description = "Spawn location x-coordinate. If only Team0 variables are set, all teams use them.  If Team0 and Team1 variables are set, even teams use Team0 and odd teams use Team1. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team2-X", ConfigScope.Arena, typeof(ushort), Range = "0-1024", Description = "Spawn location x-coordinate. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team3-X", ConfigScope.Arena, typeof(ushort), Range = "0-1024", Description = "Spawn location x-coordinate. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team0-Y", ConfigScope.Arena, typeof(ushort), Range = "0-1024", Description = "Spawn location y-coordinate. If only Team0 variables are set, all teams use them.  If Team0 and Team1 variables are set, even teams use Team0 and odd teams use Team1. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team1-Y", ConfigScope.Arena, typeof(ushort), Range = "0-1024", Description = "Spawn location y-coordinate. If only Team0 variables are set, all teams use them.  If Team0 and Team1 variables are set, even teams use Team0 and odd teams use Team1. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team2-Y", ConfigScope.Arena, typeof(ushort), Range = "0-1024", Description = "Spawn location y-coordinate. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team3-Y", ConfigScope.Arena, typeof(ushort), Range = "0-1024", Description = "Spawn location y-coordinate. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team0-Radius", ConfigScope.Arena, typeof(ushort), Range = "0-512", Description = "Spawn location radius. If only Team0 variables are set, all teams use them.  If Team0 and Team1 variables are set, even teams use Team0 and odd teams use Team1. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team1-Radius", ConfigScope.Arena, typeof(ushort), Range = "0-512", Description = "Spawn location radius. If only Team0 variables are set, all teams use them.  If Team0 and Team1 variables are set, even teams use Team0 and odd teams use Team1. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team2-Radius", ConfigScope.Arena, typeof(ushort), Range = "0-512", Description = "Spawn location radius. (Cont .38+)")]
        [ConfigHelp("Spawn", "Team3-Radius", ConfigScope.Arena, typeof(ushort), Range = "0-512", Description = "Spawn location radius. (Cont .38+)")]
        private void LoadSettings(ArenaClientSettingsData ad, ConfigHandle ch)
        {
            if (ad == null)
                return;

            if (ch == null)
                return;

            ref S2C_ClientSettings cs = ref ad.cs;

            cs.Type = (byte)S2CPacketType.Settings;
            cs.BitSet.ExactDamage = _configManager.GetInt(ch, "Bullet", "ExactDamage", 0) != 0;
            cs.BitSet.HideFlags = _configManager.GetInt(ch, "Spectator", "HideFlags", 0) != 0;
            cs.BitSet.NoXRadar = _configManager.GetInt(ch, "Spectator", "NoXRadar", 0) != 0;
            cs.BitSet.SlowFramerate = (byte)_configManager.GetInt(ch, "Misc", "SlowFrameCheck", 0);
            cs.BitSet.DisableScreenshot = _configManager.GetInt(ch, "Misc", "DisableScreenshot", 0) != 0;
            cs.BitSet.MaxTimerDrift = (byte)_configManager.GetInt(ch, "Misc", "MaxTimerDrift", 0);
            cs.BitSet.DisableWallPass = _configManager.GetInt(ch, "Soccer", "DisableWallPass", 0) != 0;
            cs.BitSet.DisableBallKilling = _configManager.GetInt(ch, "Soccer", "DisableBallKilling", 0) != 0;

            // ships
            string[] shipNames = Enum.GetNames<ShipType>();
            for (int i = 0; i < 8; i++)
            {
                ref ShipSettings ss = ref cs.Ships[i];
                string shipName = shipNames[i];

                // basic stuff
                for (int j = 0; j < ss.Int32Settings.Length; j++)
                    ss.Int32Settings[j] = _configManager.GetInt(ch, shipName, ClientSettingsConfig.ShipLongNames[j], 0);

                for (int j = 0; j < ss.Int16Settings.Length; j++)
                    ss.Int16Settings[j] = (short)_configManager.GetInt(ch, shipName, ClientSettingsConfig.ShipShortNames[j], 0);

                for (int j = 0; j < ss.ByteSettings.Length; j++)
                    ss.ByteSettings[j] = (byte)_configManager.GetInt(ch, shipName, ClientSettingsConfig.ShipByteNames[j], 0);

                // weapon bits
                ref WeaponBits wb = ref ss.Weapons;
                wb.ShrapnelMax = (byte)_configManager.GetInt(ch, shipName, "ShrapnelMax", 0);
                wb.ShrapnelRate = (byte)_configManager.GetInt(ch, shipName, "ShrapnelRate", 0);
                wb.AntiWarpStatus = (byte)_configManager.GetInt(ch, shipName, "AntiWarpStatus", 0);
                wb.CloakStatus = (byte)_configManager.GetInt(ch, shipName, "CloakStatus", 0);
                wb.StealthStatus = (byte)_configManager.GetInt(ch, shipName, "StealthStatus", 0);
                wb.XRadarStatus = (byte)_configManager.GetInt(ch, shipName, "XRadarStatus", 0);
                wb.InitialGuns = (byte)_configManager.GetInt(ch, shipName, "InitialGuns", 0);
                wb.MaxGuns = (byte)_configManager.GetInt(ch, shipName, "MaxGuns", 0);
                wb.InitialBombs = (byte)_configManager.GetInt(ch, shipName, "InitialBombs", 0);
                wb.MaxBombs = (byte)_configManager.GetInt(ch, shipName, "MaxBombs", 0);
                wb.DoubleBarrel = _configManager.GetInt(ch, shipName, "DoubleBarrel", 0) != 0;
                wb.EmpBomb = _configManager.GetInt(ch, shipName, "EmpBomb", 0) != 0;
                wb.SeeMines = _configManager.GetInt(ch, shipName, "SeeMines", 0) != 0;

                // strange bitfield
                ref MiscBits misc = ref ss.MiscBits;
                misc.SeeBombLevel = (byte)_configManager.GetInt(ch, shipName, "SeeBombLevel", 0);
                misc.DisableFastShooting = _configManager.GetInt(ch, shipName, "DisableFastShooting", 0) != 0;
                misc.Radius = (byte)_configManager.GetInt(ch, shipName, "Radius", 0);
            }

            // spawn locations
            for (int i = 0; i < 4; i++)
            {
                ref SpawnPosition spawnPosition = ref cs.SpawnPositions[i];
                spawnPosition.X = (ushort)_configManager.GetInt(ch, "Spawn", $"Team{i}-X", 0);
                spawnPosition.Y = (ushort)_configManager.GetInt(ch, "Spawn", $"Team{i}-Y", 0);
                spawnPosition.Radius = (ushort)_configManager.GetInt(ch, "Spawn", $"Team{i}-Radius", 0);
            }

            // rest of settings
            for (int i = 0; i < cs.Int32Settings.Length; i++)
                cs.Int32Settings[i] = _configManager.GetInt(ch, ClientSettingsConfig.LongNames[i].Section, ClientSettingsConfig.LongNames[i].Key, 0);

            for (int i = 0; i < cs.Int16Settings.Length; i++)
                cs.Int16Settings[i] = (short)_configManager.GetInt(ch, ClientSettingsConfig.ShortNames[i].Section, ClientSettingsConfig.ShortNames[i].Key, 0);

            for (int i = 0; i < cs.ByteSettings.Length; i++)
                cs.ByteSettings[i] = (byte)_configManager.GetInt(ch, ClientSettingsConfig.ByteNames[i].Section, ClientSettingsConfig.ByteNames[i].Key, 0);

            ushort total = 0;
            ad.pwps[0] = 0;
            for (int i = 0; i < cs.PrizeWeightSettings.Length; i++)
            {
                cs.PrizeWeightSettings[i] = (byte)_configManager.GetInt(ch, ClientSettingsConfig.PrizeWeightNames[i].Section, ClientSettingsConfig.PrizeWeightNames[i].Key, 0);
                ad.pwps[i + 1] = (total += cs.PrizeWeightSettings[i]);
            }

            if (_configManager.GetInt(ch, "Prize", "UseDeathPrizeWeights", 0) != 0)
            {
                // overrride prizeweights for greens dropped when a player is killed

                // likelyhood of an empty prize appearing
                total = ad.pwps[0] = (ushort)_configManager.GetInt(ch, "DPrizeWeight", "NullPrize", 0);
                for (int i = 0; i < cs.PrizeWeightSettings.Length; i++)
                {
                    ad.pwps[i + 1] = (total += (ushort)_configManager.GetInt(ch, ClientSettingsConfig.DeathPrizeWeightNames[i].Section, ClientSettingsConfig.DeathPrizeWeightNames[i].Key, 0));
                }
            }

            // funky ones
            cs.Int32Settings[0] *= 1000; // BulletDamageLevel
            cs.Int32Settings[1] *= 1000; // BombDamageLevel
            cs.Int32Settings[10] *= 1000; // BurstDamageLevel
            cs.Int32Settings[11] *= 1000; // BulletDamageUpgrade
            cs.Int32Settings[16] *= 1000; // InactiveShrapDamage
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            if (action == PlayerAction.LeaveArena || action == PlayerAction.Disconnect)
            {
                // reset/free player overrides on any arena change
                // TODO
            }
        }

        private void SendOneSettings(Player p, ArenaClientSettingsData ad)
        {
            if (p == null)
                return;

            if (ad == null)
                return;

            // do mask
            // TODO

            if (ad.cs.Type == (byte)S2CPacketType.Settings)
            {
                _net.SendToOne(
                    p,
                    ref ad.cs,
                    NetSendFlags.Reliable);
            }
        }
    }
}
