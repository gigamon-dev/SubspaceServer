using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SS.Core.Modules
{
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

        private object _setMtx = new object();

        private class ArenaClientSettingsData
        {
            public ClientSettingsPacket cs = new ClientSettingsPacket();

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

            ArenaClientSettingsData ad = arena[_adkey] as ArenaClientSettingsData;

            lock (_setMtx)
            {
                SendOneSettings(p, ad);
            }
        }

        uint IClientSettings.GetChecksum(Player p, uint key)
        {
            // TODO: implement this.  For now, 0 means skip checks for settings checksum.
            return 0;
        }

        Prize IClientSettings.GetRandomPrize(Arena arena)
        {
            if(arena == null)
                return 0;

            ArenaClientSettingsData ad = arena[_adkey] as ArenaClientSettingsData;
            if (ad == null)
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

        ClientSettingOverrideKey IClientSettings.GetOverrideKey(string section, string key)
        {
            return new ClientSettingOverrideKey();
        }

        void IClientSettings.ArenaOverride(Arena arena, ClientSettingOverrideKey key, int val)
        {
            
        }

        void IClientSettings.PlayerOverride(Player p, ClientSettingOverrideKey key)
        {
            
        }

        #endregion

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            ArenaClientSettingsData ad = arena[_adkey] as ArenaClientSettingsData;

            lock (_setMtx)
            {
                if (action == ArenaAction.Create)
                {
                    LoadSettings(ad, arena.Cfg);
                }
                else if(action == ArenaAction.ConfChanged)
                {
                }
                else if (action == ArenaAction.Destroy)
                {
                    // mark settings as destroyed (for asserting later)
                }
            }
        }

        private void LoadSettings(ArenaClientSettingsData ad, ConfigHandle ch)
        {
            if (ad == null)
                return;

            if (ch == null)
                return;

            ref ClientSettingsPacket cs = ref ad.cs;

            cs.Type = (byte)S2CPacketType.Settings;
            cs.BitSet.ExactDamage = _configManager.GetInt(ch, "Bullet", "ExactDamage", 0) != 0;
            cs.BitSet.HideFlags = _configManager.GetInt(ch, "Spectator", "HideFlags", 0) != 0;
            cs.BitSet.NoXRadar = _configManager.GetInt(ch, "Spectator", "NoXRadar", 0) != 0;
            cs.BitSet.SlowFramerate = (byte)_configManager.GetInt(ch, "Misc", "SlowFrameCheck", 0);
            cs.BitSet.DisableScreenshot = _configManager.GetInt(ch, "Misc", "DisableScreenshot", 0) != 0;
            cs.BitSet.MaxTimerDrift = (byte)_configManager.GetInt(ch, "Misc", "MaxTimerDrift", 0);
            cs.BitSet.DisableBallThroughWalls = _configManager.GetInt(ch, "Soccer", "DisableWallPass", 0) != 0;
            cs.BitSet.DisableBallKilling = _configManager.GetInt(ch, "Soccer", "DisableBallKilling", 0) != 0;

            // ships
            for (int i = 0; i < 8; i++)
            {
                ref ShipSettings ss = ref cs.Ships[i];
                string shipName = ClientSettingsConfig.ShipNames[i];

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
                string xName = "Team#-X".Replace('#', char.Parse(i.ToString()));
                string yName = "Team#-Y".Replace('#', char.Parse(i.ToString()));
                string rName = "Team#-Radius".Replace('#', char.Parse(i.ToString()));

                cs.SpawnPositions[i].X = (ushort)_configManager.GetInt(ch, "Spawn", xName, 0);
                cs.SpawnPositions[i].Y = (ushort)_configManager.GetInt(ch, "Spawn", yName, 0);
                cs.SpawnPositions[i].Radius = (ushort)_configManager.GetInt(ch, "Spawn", rName, 0);
            }

            // rest of settings
            for (int i = 0; i < cs.Int32Settings.Length; i++)
                cs.Int32Settings[i] = _configManager.GetInt(ch, ClientSettingsConfig.LongNames[i], null, 0);

            for (int i = 0; i < cs.Int16Settings.Length; i++)
                cs.Int16Settings[i] = (short)_configManager.GetInt(ch, ClientSettingsConfig.ShortNames[i], null, 0);

            for (int i = 0; i < cs.ByteSettings.Length; i++)
                cs.ByteSettings[i] = (byte)_configManager.GetInt(ch, ClientSettingsConfig.ByteNames[i], null, 0);

            ushort total = 0;
            ad.pwps[0] = 0;
            for (int i = 0; i < cs.PrizeWeightSettings.Length; i++)
            {
                cs.PrizeWeightSettings[i] = (byte)_configManager.GetInt(ch, ClientSettingsConfig.PrizeWeightNames[i], null, 0);
                ad.pwps[i + 1] = (total += cs.PrizeWeightSettings[i]);
            }

            if (_configManager.GetInt(ch, "Prize", "UseDeathPrizeWeights", 0) != 0)
            {
                // overrride prizeweights for greens dropped when a player is killed

                // likelyhood of an empty prize appearing
                total = ad.pwps[0] = (ushort)_configManager.GetInt(ch, "DPrizeWeight", "NullPrize", 0);
                for (int i = 0; i < cs.PrizeWeightSettings.Length; i++)
                {
                    ad.pwps[i + 1] = (total += (ushort)_configManager.GetInt(ch, ClientSettingsConfig.DeathPrizeWeightNames[i], null, 0));
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
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref ad.cs, 1))
                    , NetSendFlags.Reliable);
            }
        }
    }
}
