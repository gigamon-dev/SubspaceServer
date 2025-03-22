using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ArenaSettings = SS.Core.ConfigHelp.Constants.Arena;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that manages the client-side settings.
    /// Client-side settings are the settings sent to the client via a <see cref="S2C_ClientSettings"/> (includes ship settings and more).
    /// Settings are loaded from disk when an arena is loaded and when there is a config change.
    /// </summary>
    [CoreModuleInfo]
    public sealed class ClientSettings : IModule, IClientSettings
    {
        private readonly IComponentBroker _broker;
        private readonly IArenaManager _arenaManager;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly INetwork _network;
        private readonly IPlayerData _playerData;
        private readonly IPrng _prng;
        private InterfaceRegistrationToken<IClientSettings>? _iClientSettingsToken;

        private ArenaDataKey<ArenaData> _adkey;
        private PlayerDataKey<PlayerData> _pdkey;

        // Locking is technically not necessary since all the logic in this module is intended to be executed on the mainloop thread.
        // However, there is the off chance someone writing a module makes a mistake and calls the IClientSettings interface from a different thread.
        // TODO: Maybe remove the locking and instead add checks that throw an exception if it's not the mainloop thread?
        private readonly Lock _lock = new();

        #region Data for creating ClientSettingIdentifiers that represent bit fields

        private static readonly (string Key, int BitOffset, int BitLength)[] ShipWeaponBitfields =
        [
            ("ShrapnelMax", 0, 5),
            ("ShrapnelRate", 5, 5),
            ("CloakStatus", 10, 2),
            ("StealthStatus", 12, 2),
            ("XRadarStatus", 14, 2),
            ("AntiWarpStatus", 16, 2),
            ("InitialGuns", 18, 2),
            ("MaxGuns", 20, 2),
            ("InitialBombs", 22, 2),
            ("MaxBombs", 24, 2),
            ("DoubleBarrel", 26, 1),
            ("EmpBomb", 27, 1),
            ("SeeMines", 28, 1),
        ];

        private static readonly (string Key, int BitOffset, int BitLength)[] ShipMiscBitfields =
        [
            ("SeeBombLevel", 0, 2),
            ("DisableFastShooting", 2, 1),
            ("Radius", 3, 8),
        ];

        private static readonly (string Section, string Key, int BitOffset, int BitLength)[] BitSetBitfields =
        [
            ("Bullet", "ExactDamage", 8, 1),
            ("Spectator", "HideFlags", 9, 1),
            ("Spectator", "NoXRadar", 10, 1),
            ("Misc", "SlowFrameCheck", 11, 3),
            ("Misc", "DisableScreenshot", 14, 1),
            ("Misc", "MaxTimerDrift", 16, 3),
            ("Soccer", "DisableWallPass", 19, 1),
            ("Soccer", "DisableBallKilling", 20, 1),
        ];

        #endregion

        public ClientSettings(
            IComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            ILogManager logManager,
            INetwork network,
            IPlayerData playerData,
            IPrng prng)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));
        }

        #region IModule Members

        bool IModule.Load(IComponentBroker broker)
        {
#if DEBUG
            // Some sanity checks on conditions that should be true.
            {
                Debug.Assert((S2C_ClientSettings.Length % 4) == 0);

                Debug.Assert(S2C_ClientSettings.Int32InlineArray.Length == ClientSettingsConfig.LongNames.Length);
                Debug.Assert(S2C_ClientSettings.Int16InlineArray.Length == ClientSettingsConfig.ShortNames.Length);
                Debug.Assert(S2C_ClientSettings.ByteInlineArray.Length == ClientSettingsConfig.ByteNames.Length);
                Debug.Assert(S2C_ClientSettings.PrizeWeightInlineArray.Length == ClientSettingsConfig.PrizeWeightNames.Length);

                Debug.Assert(ShipSettings.Int32InlineArray.Length == ClientSettingsConfig.ShipLongNames.Length);
                Debug.Assert(ShipSettings.Int16InlineArray.Length == ClientSettingsConfig.ShipShortNames.Length);
                Debug.Assert(ShipSettings.ByteInlineArray.Length == ClientSettingsConfig.ShipByteNames.Length);
            }
#endif
            _adkey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdkey = _playerData.AllocatePlayerData<PlayerData>();

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            _iClientSettingsToken = _broker.RegisterInterface<IClientSettings>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iClientSettingsToken) != 0)
                return false;

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);

            _arenaManager.FreeArenaData(ref _adkey);
            _playerData.FreePlayerData(ref _pdkey);

            return true;
        }

        #endregion

        #region IClientSettings Members

        void IClientSettings.SendClientSettings(Player player)
        {
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (!arena.TryGetExtraData(_adkey, out ArenaData? arenaData))
                return;

            lock (_lock)
            {
                SendOneSettings(player, arenaData);
            }
        }

        uint IClientSettings.GetChecksum(Player player, uint key)
        {
            if (player is null)
                return 0;

            if (!player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                return 0;

            // ASSS calls DoMask() here which refreshes the player's settings.
            // However, the player might not have been sent the latest settings (an override could have been set, but not yet sent).
            // So, it makes more sense that DoMask() be called only before sending settings to a player.

            lock (_lock)
            {
                if (playerData.Settings.Type != (byte)S2CPacketType.Settings)
                    return 0;

                return playerData.Settings.GetChecksum(key);
            }
        }

        Prize IClientSettings.GetRandomPrize(Arena arena)
        {
            if (arena is null)
                return 0;

            if (!arena.TryGetExtraData(_adkey, out ArenaData? arenaData))
                return 0;

            int max = arenaData.pwps[28];

            if (max == 0)
                return 0;

            int i = 0;
            int j = 28;
            int r = _prng.Number(0, max - 1);

            // binary search
            while (r >= arenaData.pwps[i])
            {
                int m = (i + j) / 2;
                if (r < arenaData.pwps[m])
                    j = m;
                else
                    i = m + 1;
            }

            return (Prize)i;
        }

        bool IClientSettings.TryGetSettingsIdentifier(ReadOnlySpan<char> section, ReadOnlySpan<char> key, out ClientSettingIdentifier id)
        {
            // prizeweights
            if (section.Equals("PrizeWeight", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < ClientSettingsConfig.PrizeWeightNames.Length; i++)
                {
                    if (key.Equals(ClientSettingsConfig.PrizeWeightNames[i].Key, StringComparison.OrdinalIgnoreCase))
                    {
                        id = new ClientSettingIdentifier(false, ClientSettingIdentifierFieldType.Bit8, (int)Marshal.OffsetOf<S2C_ClientSettings>("PrizeWeightSettings") + i, 0, 8);
                        return true;
                    }
                }

                id = default;
                return false;
            }

            // ships
            string[] shipNames = Enum.GetNames<ShipType>();
            for (int shipIndex = 0; shipIndex < 8; shipIndex++)
            {
                if (!section.Equals(shipNames[shipIndex], StringComparison.OrdinalIgnoreCase))
                    continue;

                // Int32 settings
                for (int i = 0; i < ClientSettingsConfig.ShipLongNames.Length; i++)
                {
                    if (!key.Equals(ClientSettingsConfig.ShipLongNames[i], StringComparison.OrdinalIgnoreCase))
                        continue;

                    int byteOffset = (int)Marshal.OffsetOf<S2C_ClientSettings>("Ships")
                        + (int)Marshal.OffsetOf<AllShipSettings>(shipNames[shipIndex])
                        + (int)Marshal.OffsetOf<ShipSettings>("Int32Settings")
                        + (i * 4);

                    id = new ClientSettingIdentifier(true, ClientSettingIdentifierFieldType.Bit32, byteOffset, 0, 32);
                    return true;
                }

                // Int16 settings
                for (int i = 0; i < ClientSettingsConfig.ShipShortNames.Length; i++)
                {
                    if (!key.Equals(ClientSettingsConfig.ShipShortNames[i], StringComparison.OrdinalIgnoreCase))
                        continue;

                    int byteOffset =
                          (int)Marshal.OffsetOf<S2C_ClientSettings>("Ships")
                        + (int)Marshal.OffsetOf<AllShipSettings>(shipNames[shipIndex])
                        + (int)Marshal.OffsetOf<ShipSettings>("Int16Settings")
                        + (i * 2);

                    id = new ClientSettingIdentifier(true, ClientSettingIdentifierFieldType.Bit16, byteOffset, 0, 16);
                    return true;
                }

                // byte settings
                for (int i = 0; i < ClientSettingsConfig.ShipByteNames.Length; i++)
                {
                    if (!key.Equals(ClientSettingsConfig.ShipByteNames[i], StringComparison.OrdinalIgnoreCase))
                        continue;

                    int byteOffset =
                          (int)Marshal.OffsetOf<S2C_ClientSettings>("Ships")
                        + (int)Marshal.OffsetOf<AllShipSettings>(shipNames[shipIndex])
                        + (int)Marshal.OffsetOf<ShipSettings>("ByteSettings")
                        + i;

                    id = new ClientSettingIdentifier(false, ClientSettingIdentifierFieldType.Bit8, byteOffset, 0, 8);
                    return true;
                }

                // weapon bitfields
                foreach ((string bitfieldKey, int bitOffset, int bitLength) in ShipWeaponBitfields)
                {
                    if (!key.Equals(bitfieldKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int byteOffset =
                          (int)Marshal.OffsetOf<S2C_ClientSettings>("Ships")
                        + (int)Marshal.OffsetOf<AllShipSettings>(shipNames[shipIndex])
                        + (int)Marshal.OffsetOf<ShipSettings>("Weapons");

                    id = new ClientSettingIdentifier(false, ClientSettingIdentifierFieldType.Bit32, byteOffset, bitOffset, bitLength);
                    return true;
                }

                // misc bitfields
                foreach ((string bitfieldKey, int bitOffset, int bitLength) in ShipMiscBitfields)
                {
                    if (!key.Equals(bitfieldKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int byteOffset =
                          (int)Marshal.OffsetOf<S2C_ClientSettings>("Ships")
                        + (int)Marshal.OffsetOf<AllShipSettings>(shipNames[shipIndex])
                        + (int)Marshal.OffsetOf<ShipSettings>("Int16Settings")
                        + (10 * 2);

                    id = new ClientSettingIdentifier(false, ClientSettingIdentifierFieldType.Bit16, byteOffset, bitOffset, bitLength);
                    return true;
                }

                id = default;
                return false;
            }

            // spawn locations
            if (section.Equals("Spawn", StringComparison.OrdinalIgnoreCase))
            {
                Span<char> xName = ['T', 'e', 'a', 'm', '#', '-', 'X'];
                Span<char> yName = ['T', 'e', 'a', 'm', '#', '-', 'Y'];
                Span<char> rName = ['T', 'e', 'a', 'm', '#', '-', 'R', 'a', 'd', 'i', 'u', 's'];

                for (int i = 0; i < 4; i++)
                {
                    xName[4] = yName[4] = rName[4] = (char)('0' + i);

                    int byteOffSet = (int)Marshal.OffsetOf<S2C_ClientSettings>("SpawnPositions") + (i * 4);

                    if (key.Equals(xName, StringComparison.OrdinalIgnoreCase))
                    {
                        id = new ClientSettingIdentifier(false, ClientSettingIdentifierFieldType.Bit32, byteOffSet, 0, 10);
                        return true;
                    }
                    else if (key.Equals(yName, StringComparison.OrdinalIgnoreCase))
                    {
                        id = new ClientSettingIdentifier(false, ClientSettingIdentifierFieldType.Bit32, byteOffSet, 10, 10);
                        return true;
                    }
                    else if (key.Equals(rName, StringComparison.OrdinalIgnoreCase))
                    {
                        id = new ClientSettingIdentifier(false, ClientSettingIdentifierFieldType.Bit32, byteOffSet, 20, 9);
                        return true;
                    }
                }

                id = default;
                return false;
            }

            // Int32 settings
            for (int i = 0; i < ClientSettingsConfig.LongNames.Length; i++)
            {
                if (section.Equals(ClientSettingsConfig.LongNames[i].Section, StringComparison.OrdinalIgnoreCase)
                    && key.Equals(ClientSettingsConfig.LongNames[i].Key, StringComparison.OrdinalIgnoreCase))
                {
                    int byteOffset = (int)Marshal.OffsetOf<S2C_ClientSettings>("Int32Settings") + (i * 4);
                    id = new ClientSettingIdentifier(true, ClientSettingIdentifierFieldType.Bit32, byteOffset, 0, 32);
                    return true;
                }
            }

            // Int16 settings
            for (int i = 0; i < ClientSettingsConfig.ShortNames.Length; i++)
            {
                if (section.Equals(ClientSettingsConfig.ShortNames[i].Section, StringComparison.OrdinalIgnoreCase)
                    && key.Equals(ClientSettingsConfig.ShortNames[i].Key, StringComparison.OrdinalIgnoreCase))
                {
                    int byteOffset = (int)Marshal.OffsetOf<S2C_ClientSettings>("Int16Settings") + (i * 2);
                    id = new ClientSettingIdentifier(true, ClientSettingIdentifierFieldType.Bit16, byteOffset, 0, 16);
                    return true;
                }
            }

            // byte settings
            for (int i = 0; i < ClientSettingsConfig.ByteNames.Length; i++)
            {
                if (section.Equals(ClientSettingsConfig.ByteNames[i].Section, StringComparison.OrdinalIgnoreCase)
                    && key.Equals(ClientSettingsConfig.ByteNames[i].Key, StringComparison.OrdinalIgnoreCase))
                {
                    int byteOffset = (int)Marshal.OffsetOf<S2C_ClientSettings>("ByteSettings") + i;
                    id = new ClientSettingIdentifier(false, ClientSettingIdentifierFieldType.Bit8, byteOffset, 0, 8);
                    return true;
                }
            }

            // bitfields
            foreach ((string bitFieldSection, string bitFieldKey, int bitOffset, int bitLength) in BitSetBitfields)
            {
                if (section.Equals(bitFieldSection, StringComparison.OrdinalIgnoreCase)
                    && key.Equals(bitFieldKey, StringComparison.OrdinalIgnoreCase))
                {
                    int byteOffset = (int)Marshal.OffsetOf<S2C_ClientSettings>("BitSet");
                    id = new ClientSettingIdentifier(false, ClientSettingIdentifierFieldType.Bit32, byteOffset, bitOffset, bitLength);
                    return true;
                }
            }

            id = default;
            return false;
        }

        void IClientSettings.OverrideSetting(Arena arena, ClientSettingIdentifier id, int value)
        {
            if (arena is null || !arena.TryGetExtraData(_adkey, out ArenaData? arenaData))
                return;

            lock (_lock)
            {
                SetOverride(ref arenaData.OverrideData, id, value, true);
            }
        }

        void IClientSettings.OverrideSetting(Player player, ClientSettingIdentifier id, int value)
        {
            if (player is null || !player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                return;

            lock (_lock)
            {
                SetOverride(ref playerData.OverrideData, id, value, true);
            }
        }

        void IClientSettings.UnoverrideSetting(Arena arena, ClientSettingIdentifier id)
        {
            if (arena is null || !arena.TryGetExtraData(_adkey, out ArenaData? arenaData))
                return;

            lock (_lock)
            {
                SetOverride(ref arenaData.OverrideData, id, 0, false);
            }
        }

        void IClientSettings.UnoverrideSetting(Player player, ClientSettingIdentifier id)
        {
            if (player is null || !player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                return;

            lock (_lock)
            {
                SetOverride(ref playerData.OverrideData, id, 0, false);
            }
        }

        bool IClientSettings.TryGetSettingOverride(Arena arena, ClientSettingIdentifier id, out int value)
        {
            if (arena is null || !arena.TryGetExtraData(_adkey, out ArenaData? arenaData))
            {
                value = default;
                return false;
            }

            lock (_lock)
            {
                return TryGetOverride(ref arenaData.OverrideData, id, out value);
            }
        }

        bool IClientSettings.TryGetSettingOverride(Player player, ClientSettingIdentifier id, out int value)
        {
            if (player is null || !player.TryGetExtraData(_pdkey, out PlayerData? playerData))
            {
                value = default;
                return false;
            }

            lock (_lock)
            {
                return TryGetOverride(ref playerData.OverrideData, id, out value);
            }
        }

        int IClientSettings.GetSetting(Arena arena, ClientSettingIdentifier id)
        {
            if (arena is null || !arena.TryGetExtraData(_adkey, out ArenaData? arenaData))
            {
                return 0;
            }

            lock (_lock)
            {
                return GetSetting(ref arenaData.Settings, id);
            }
        }

        int IClientSettings.GetSetting(Player player, ClientSettingIdentifier id)
        {
            if (player is null || !player.TryGetExtraData(_pdkey, out PlayerData? playerData))
            {
                return 0;
            }

            lock (_lock)
            {
                return GetSetting(ref playerData.Settings, id);
            }
        }

        #endregion

        #region Callbacks

        [ConfigHelp<bool>("Misc", "SendUpdatedSettings", ConfigScope.Arena, Default = true,
            Description = "Whether to send updates to players when the arena settings change.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena is null)
                return;

            if (!arena.TryGetExtraData(_adkey, out ArenaData? arenaData))
                return;

            lock (_lock)
            {
                if (action == ArenaAction.Create)
                {
                    LoadSettings(arenaData, arena.Cfg!);
                }
                else if (action == ArenaAction.ConfChanged)
                {
                    bool sendUpdated = _configManager.GetBool(arena.Cfg!, "Misc", "SendUpdatedSettings", ArenaSettings.Misc.SendUpdatedSettings.Default);

                    S2C_ClientSettings old = arenaData.Settings;

                    LoadSettings(arenaData, arena.Cfg!);

                    if (sendUpdated)
                    {
                        ReadOnlySpan<byte> oldSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref old, 1));
                        ReadOnlySpan<byte> newSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref arenaData.Settings, 1));

                        if (!oldSpan.SequenceEqual(newSpan))
                        {
                            _logManager.LogA(LogLevel.Info, nameof(ClientSettings), arena, "Sending modified settings.");

                            _playerData.Lock();

                            try
                            {
                                foreach (Player player in _playerData.Players)
                                {
                                    if (player.Arena == arena && player.Status == PlayerState.Playing)
                                        SendOneSettings(player, arenaData);
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
                    arenaData.Settings.Type = 0;
                }
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (player is null || !player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                return;

            if (action == PlayerAction.LeaveArena || action == PlayerAction.Disconnect)
            {
                // reset player overrides on any arena change
                playerData.Reset();
            }
        }

        #endregion

        [ConfigHelp<bool>("Bullet", "ExactDamage", ConfigScope.Arena, Default = false, Description = "Whether to use exact bullet damage (Cont .36+)")]
        [ConfigHelp<bool>("Spectator", "HideFlags", ConfigScope.Arena, Default = false, Description = "Whether to show dropped flags to spectators (Cont .36+)")]
        [ConfigHelp<bool>("Spectator", "NoXRadar", ConfigScope.Arena, Default = false, Description = "Whether spectators are disallowed from having X radar (Cont .36+)")]
        [ConfigHelp<bool>("Misc", "SlowFrameCheck", ConfigScope.Arena, Default = false, Description = "Whether to check for slow frames on the client (possible cheat technique) (flawed on some machines, do not use)")]
        [ConfigHelp<bool>("Misc", "DisableScreenshot", ConfigScope.Arena, Default = false, Description = "Whether to disable Continuum's screenshot feature (Cont .37+)")]
        [ConfigHelp<byte>("Misc", "MaxTimerDrift", ConfigScope.Arena, Default = 0, Description = "Percentage how much client timer is allowed to differ from server timer.")]
        [ConfigHelp<bool>("Soccer", "DisableWallPass", ConfigScope.Arena, Default = false, Description = "Whether to disable ball-passing through walls (Cont .38+)")]
        [ConfigHelp<bool>("Soccer", "DisableBallKilling", ConfigScope.Arena, Default = false, Description = "Whether to disable ball killing in safe zones (Cont .38+)")]
        [ConfigHelp<byte>("All", "ShrapnelMax", ConfigScope.Arena, Description = "Maximum amount of shrapnel released from a ship's bomb")]
        [ConfigHelp<byte>("All", "ShrapnelRate", ConfigScope.Arena, Description = "Amount of additional shrapnel gained by a 'Shrapnel Upgrade' prize.")]
        [ConfigHelp<byte>("All", "AntiWarpStatus", ConfigScope.Arena, Min = 0, Max = 2, Description = "Whether ships are allowed to receive 'Anti-Warp' 0=no 1=yes 2=yes/start-with")]
        [ConfigHelp<byte>("All", "CloakStatus", ConfigScope.Arena, Min = 0, Max = 2, Description = "Whether ships are allowed to receive 'Cloak' 0=no 1=yes 2=yes/start-with")]
        [ConfigHelp<byte>("All", "StealthStatus", ConfigScope.Arena, Min = 0, Max = 2, Description = "Whether ships are allowed to receive 'Stealth' 0=no 1=yes 2=yes/start-with")]
        [ConfigHelp<byte>("All", "XRadarStatus", ConfigScope.Arena, Min = 0, Max = 2, Description = "Whether ships are allowed to receive 'X-Radar' 0=no 1=yes 2=yes/start-with")]
        [ConfigHelp<byte>("All", "InitialGuns", ConfigScope.Arena, Min = 0, Max = 3, Description = "Initial level a ship's guns fire")]
        [ConfigHelp<byte>("All", "MaxGuns", ConfigScope.Arena, Min = 0, Max = 3, Description = "Maximum level a ship's guns can fire")]
        [ConfigHelp<byte>("All", "InitialBombs", ConfigScope.Arena, Min = 0, Max = 3, Description = "Initial level a ship's bombs fire")]
        [ConfigHelp<byte>("All", "MaxBombs", ConfigScope.Arena, Min = 0, Max = 3, Description = "Maximum level a ship's bombs can fire")]
        [ConfigHelp<bool>("All", "DoubleBarrel", ConfigScope.Arena, Description = "Whether ships fire with double barrel bullets")]
        [ConfigHelp<bool>("All", "EmpBomb", ConfigScope.Arena, Description = "Whether ships fire EMP bombs")]
        [ConfigHelp<bool>("All", "SeeMines", ConfigScope.Arena, Description = "Whether ships see mines on radar")]
        [ConfigHelp<int>("All", "SeeBombLevel", ConfigScope.Arena, Min = 0, Max = 4, Description = "If ship can see bombs on radar (0=Disabled, 1=All, 2=L2 and up, 3=L3 and up, 4=L4 bombs only)")]
        [ConfigHelp<bool>("All", "DisableFastShooting", ConfigScope.Arena, Description = "If firing bullets, bombs, or thors is disabled after using afterburners (1=enabled) (Cont .36+)")]
        [ConfigHelp<int>("All", "Radius", ConfigScope.Arena, Default = 14, Min = 0, Max = 255, Description = "The ship's radius from center to outside, in pixels. (Cont .37+)")]
        [ConfigHelp<ushort>("Spawn", "Team0-X", ConfigScope.Arena, Default = 0, Min = 0, Max = 1023,
            Description = """
                If set to a value, this is the X coordinate for the center point
                where freq 0 will start. (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team1-X", ConfigScope.Arena, Default = 0, Min = 0, Max = 1023,
            Description = """
                If set to a value, this is the X coordinate for the center point
                where freq 1 will start. (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team2-X", ConfigScope.Arena, Default = 0, Min = 0, Max = 1023,
            Description = """
                If set to a value, this is the X coordinate for the center point
                where freq 2 will start. NOTE: if the Team2 settings are 0, Team0
                will apply to evens and Team1 will apply to odds. (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team3-X", ConfigScope.Arena, Default = 0, Min = 0, Max = 1023,
            Description = """
                If set to a value, this is the X coordinate for the center point
                where freq 3 will start. NOTE: Repeats, freq 4 will use Team0's,
                freq 5 will use Team1's, etc. (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team0-Y", ConfigScope.Arena, Default = 0, Min = 0, Max = 1023,
            Description = """
                If set to a value, this is the Y coordinate for the center point
                where freq 0 will start. (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team1-Y", ConfigScope.Arena, Default = 0, Min = 0, Max = 1023,
            Description = """
                If set to a value, this is the Y coordinate for the center point
                where freq 1 will start. (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team2-Y", ConfigScope.Arena, Default = 0, Min = 0, Max = 1023,
            Description = """
                If set to a value, this is the Y coordinate for the center point
                where freq 2 will start. NOTE: if the Team2 settings are 0, Team0
                will apply to evens and Team1 will apply to odds. (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team3-Y", ConfigScope.Arena, Default = 0, Min = 0, Max = 1023,
            Description = """
                If set to a value, this is the Y coordinate for the center point
                where freq 3 will start. NOTE: Repeats, freq 4 will use Team0's,
                freq 5 will use Team1's, etc. (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team0-Radius", ConfigScope.Arena, Default = 0, Min = 0, Max = 511,
            Description = """
                How large of a circle from the center point freq 0 can start.
                (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team1-Radius", ConfigScope.Arena, Default = 0, Min = 0, Max = 511,
            Description = """
                How large of a circle from the center point freq 1 can start.
                (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team2-Radius", ConfigScope.Arena, Default = 0, Min = 0, Max = 511,
            Description = """
                How large of a circle from the center point freq 2 can start.
                NOTE: if the Team2 settings are 0, Team0 will apply to evens and 
                Team1 will apply to odds. (Cont .38+)
                """)]
        [ConfigHelp<ushort>("Spawn", "Team3-Radius", ConfigScope.Arena, Default = 0, Min = 0, Max = 511,
            Description = """
                How large of a circle from the center point freq 3 can start.
                NOTE: Repeats, freq 4 will use Team0's, freq 5 will use Team1's, 
                etc. (Cont .38+)
                """)]
        [ConfigHelp<bool>("Prize", "UseDeathPrizeWeights", ConfigScope.Arena, Default = false, Description = "Whether to use the DPrizeWeight section for death prizes instead of the PrizeWeight section.")]
        [ConfigHelp<int>("DPrizeWeight", "NullPrize", ConfigScope.Arena, Default = 0, Description = "Likelihood of an empty prize appearing.")]
        private void LoadSettings(ArenaData arenaData, ConfigHandle ch)
        {
            if (arenaData is null)
                return;

            if (ch is null)
                return;

            ref S2C_ClientSettings cs = ref arenaData.Settings;

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
                for (int j = 0; j < ShipSettings.Int32InlineArray.Length; j++)
                {
                    ss.Int32Settings[j] = _configManager.GetInt(ch, shipName, ClientSettingsConfig.ShipLongNames[j], 0);
                }

                for (int j = 0; j < ShipSettings.Int16InlineArray.Length; j++)
                {
                    ss.Int16Settings[j] = (short)_configManager.GetInt(ch, shipName, ClientSettingsConfig.ShipShortNames[j], 0);
                }

                for (int j = 0; j < ShipSettings.ByteInlineArray.Length; j++)
                {
                    ss.ByteSettings[j] = (byte)_configManager.GetInt(ch, shipName, ClientSettingsConfig.ShipByteNames[j], 0);
                }

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
                ref MiscBits misc = ref MemoryMarshal.Cast<short, MiscBits>(((Span<short>)ss.Int16Settings).Slice(10, 1))[0];
                misc.SeeBombLevel = (byte)_configManager.GetInt(ch, shipName, "SeeBombLevel", 0);
                misc.DisableFastShooting = _configManager.GetInt(ch, shipName, "DisableFastShooting", 0) != 0;
                misc.Radius = (byte)_configManager.GetInt(ch, shipName, "Radius", 0);
            }

            // spawn locations
            Span<char> xKey = stackalloc char["Team#-X".Length];
            Span<char> yKey = stackalloc char["Team#-Y".Length];
            Span<char> rKey = stackalloc char["Team#-Radius".Length];

            "Team#-X".CopyTo(xKey);
            "Team#-Y".CopyTo(yKey);
            "Team#-Radius".CopyTo(rKey);

            for (int i = 0; i < 4; i++)
            {
                xKey[4] = yKey[4] = rKey[4] = (char)('0' + i);

                ref SpawnPosition spawnPosition = ref cs.SpawnPositions[i];
                spawnPosition.X = (ushort)_configManager.GetInt(ch, "Spawn", xKey, 0);
                spawnPosition.Y = (ushort)_configManager.GetInt(ch, "Spawn", yKey, 0);
                spawnPosition.Radius = (ushort)_configManager.GetInt(ch, "Spawn", rKey, 0);
            }

            // rest of settings
            for (int i = 0; i < S2C_ClientSettings.Int32InlineArray.Length; i++)
            {
                cs.Int32Settings[i] = _configManager.GetInt(ch, ClientSettingsConfig.LongNames[i].Section, ClientSettingsConfig.LongNames[i].Key, 0);
            }

            for (int i = 0; i < S2C_ClientSettings.Int16InlineArray.Length; i++)
            {
                cs.Int16Settings[i] = (short)_configManager.GetInt(ch, ClientSettingsConfig.ShortNames[i].Section, ClientSettingsConfig.ShortNames[i].Key, 0);

                if (i == 2)
                {
                    Debug.Assert(string.Equals("Misc", ClientSettingsConfig.ShortNames[i].Section, StringComparison.OrdinalIgnoreCase)
                        && string.Equals("SendPositionDelay", ClientSettingsConfig.ShortNames[i].Key, StringComparison.OrdinalIgnoreCase));

                    // Misc:SendPositionDelay of 0 will crash Continuum. Set it to 1 at least.
                    if (cs.Int16Settings[i] == 0)
                        cs.Int16Settings[i] = 1;
                }
                else if (i == 11)
                {
                    Debug.Assert(string.Equals("Radar", ClientSettingsConfig.ShortNames[i].Section, StringComparison.OrdinalIgnoreCase)
                        && string.Equals("MapZoomFactor", ClientSettingsConfig.ShortNames[i].Key, StringComparison.OrdinalIgnoreCase));

                    // Radar:MapZoomFactor of 0 will crash Continuum. Set it to 1 at least.
                    if (cs.Int16Settings[i] == 0)
                        cs.Int16Settings[i] = 1;
                }
                else if (i == 26)
                {
                    Debug.Assert(string.Equals("Prize", ClientSettingsConfig.ShortNames[i].Section, StringComparison.OrdinalIgnoreCase)
                        && string.Equals("PrizeNegativeFactor", ClientSettingsConfig.ShortNames[i].Key, StringComparison.OrdinalIgnoreCase));

                    // Prize:PrizeNegativeFactor of 0 will crash Continuum. Set it to 1, which means every prize is a negative prize. This will make it obvious when the setting is missing.
                    if (cs.Int16Settings[i] == 0)
                        cs.Int16Settings[i] = 1;
                }
            }

            for (int i = 0; i < S2C_ClientSettings.ByteInlineArray.Length; i++)
            {
                cs.ByteSettings[i] = (byte)_configManager.GetInt(ch, ClientSettingsConfig.ByteNames[i].Section, ClientSettingsConfig.ByteNames[i].Key, 0);
            }

            ushort total = 0;
            arenaData.pwps[0] = 0;
            for (int i = 0; i < S2C_ClientSettings.PrizeWeightInlineArray.Length; i++)
            {
                cs.PrizeWeightSettings[i] = (byte)_configManager.GetInt(ch, ClientSettingsConfig.PrizeWeightNames[i].Section, ClientSettingsConfig.PrizeWeightNames[i].Key, 0);
                arenaData.pwps[i + 1] = (total += cs.PrizeWeightSettings[i]);
            }

            if (_configManager.GetInt(ch, "Prize", "UseDeathPrizeWeights", 0) != 0)
            {
                // overrride prizeweights for greens dropped when a player is killed

                // likelyhood of an empty prize appearing
                total = arenaData.pwps[0] = (ushort)_configManager.GetInt(ch, "DPrizeWeight", "NullPrize", 0);

                for (int i = 0; i < S2C_ClientSettings.PrizeWeightInlineArray.Length; i++)
                {
                    arenaData.pwps[i + 1] = (total += (ushort)_configManager.GetInt(ch, ClientSettingsConfig.DeathPrizeWeightNames[i].Section, ClientSettingsConfig.DeathPrizeWeightNames[i].Key, 0));
                }
            }

            // funky ones
            cs.Int32Settings[0] *= 1000; // BulletDamageLevel
            cs.Int32Settings[1] *= 1000; // BombDamageLevel
            cs.Int32Settings[10] *= 1000; // BurstDamageLevel
            cs.Int32Settings[11] *= 1000; // BulletDamageUpgrade
            cs.Int32Settings[16] *= 1000; // InactiveShrapDamage
        }

        private void SetOverride(ref OverrideData overrideData, ClientSettingIdentifier id, int value, bool enabled)
        {
            if (!id.IsValid())
            {
                _logManager.LogM(LogLevel.Warn, nameof(ClientSettings), $"Invalid ClientSettingsIdentifier: {id}");
                return;
            }

            (_, ClientSettingIdentifierFieldType fieldType, int byteOffset, int bitOffset, int bitLength) = id;

            if (fieldType == ClientSettingIdentifierFieldType.Bit8)
            {
                byte mask = (byte)(0xFF >> (8 - bitLength) << bitOffset);
                Span<byte> maskBytes = overrideData.MaskBytes.Slice(byteOffset, 1);

                if (enabled)
                {
                    Span<byte> dataBytes = overrideData.DataBytes.Slice(byteOffset, 1);
                    dataBytes[0] = (byte)((dataBytes[0] & ~mask) | ((value << bitOffset) & mask));
                    maskBytes[0] |= mask;
                }
                else
                {
                    maskBytes[0] &= (byte)~mask;
                }
            }
            else if (fieldType == ClientSettingIdentifierFieldType.Bit16)
            {
                ushort mask = (ushort)(0xFFFF >> (16 - bitLength) << bitOffset);
                Span<byte> maskBytes = overrideData.MaskBytes.Slice(byteOffset, 2);

                if (enabled)
                {
                    Span<byte> dataBytes = overrideData.DataBytes.Slice(byteOffset, 2);

                    BinaryPrimitives.WriteUInt16LittleEndian(
                        dataBytes,
                        (ushort)((BinaryPrimitives.ReadUInt16LittleEndian(dataBytes) & ~mask) | ((value << bitOffset) & mask)));

                    BinaryPrimitives.WriteUInt16LittleEndian(
                        maskBytes,
                        (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(maskBytes) | mask));
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        maskBytes,
                        (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(maskBytes) & ~mask));
                }
            }
            else if (fieldType == ClientSettingIdentifierFieldType.Bit32)
            {
                uint mask = 0xFFFFFFFF >> (32 - bitLength) << bitOffset;
                Span<byte> maskBytes = overrideData.MaskBytes.Slice(byteOffset, 4);

                if (enabled)
                {
                    Span<byte> dataBytes = overrideData.DataBytes.Slice(byteOffset, 4);

                    BinaryPrimitives.WriteUInt32LittleEndian(
                        dataBytes,
                        (BinaryPrimitives.ReadUInt32LittleEndian(dataBytes) & ~mask) | (((uint)value << bitOffset) & mask));

                    BinaryPrimitives.WriteUInt32LittleEndian(
                        maskBytes,
                        BinaryPrimitives.ReadUInt32LittleEndian(maskBytes) | mask);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        maskBytes,
                        BinaryPrimitives.ReadUInt32LittleEndian(maskBytes) & ~mask);
                }
            }
        }

        private bool TryGetOverride(ref OverrideData overrideData, ClientSettingIdentifier id, out int value)
        {
            if (!id.IsValid())
            {
                _logManager.LogM(LogLevel.Warn, nameof(ClientSettings), $"Invalid ClientSettingsIdentifier: {id}");
                value = default;
                return false;
            }

            int bitLength = id.BitLength;
            int bitOffset = id.BitOffset;
            int byteOffset = id.ByteOffset;

            if (id.FieldType == ClientSettingIdentifierFieldType.Bit8)
            {
                uint mask = 0xFFu >> (8 - bitLength) << bitOffset;
                if ((overrideData.MaskBytes[byteOffset] & mask) != mask)
                {
                    value = default;
                    return false;
                }

                value = (int)(overrideData.DataBytes[byteOffset] & mask) << (32 - (bitOffset + bitLength));
                if (id.IsSigned)
                {
                    value >>= (32 - bitLength);
                }
                else
                {
                    value = value >>> (32 - bitLength);
                }

                return true;
            }
            else if (id.FieldType == ClientSettingIdentifierFieldType.Bit16)
            {
                uint mask = 0xFFFFu >> (16 - bitLength) << bitOffset;
                Span<byte> maskBytes = overrideData.MaskBytes.Slice(byteOffset, 2);
                if ((BinaryPrimitives.ReadUInt16LittleEndian(maskBytes) & mask) != mask)
                {
                    value = default;
                    return false;
                }

                Span<byte> dataBytes = overrideData.DataBytes.Slice(byteOffset, 2);
                value = (int)(BinaryPrimitives.ReadUInt16LittleEndian(dataBytes) & mask) << (32 - (bitOffset + bitLength));
                if (id.IsSigned)
                {
                    value >>= (32 - bitLength);
                }
                else
                {
                    value = value >>> (32 - bitLength);
                }

                return true;
            }
            else if (id.FieldType == ClientSettingIdentifierFieldType.Bit32)
            {
                uint mask = 0xFFFFFFFF >> (32 - bitLength) << bitOffset;
                Span<byte> maskBytes = overrideData.MaskBytes.Slice(byteOffset, 4);
                if ((BinaryPrimitives.ReadUInt32LittleEndian(maskBytes) & mask) != mask)
                {
                    value = default;
                    return false;
                }

                Span<byte> dataBytes = overrideData.DataBytes.Slice(byteOffset, 4);
                value = (int)(BinaryPrimitives.ReadUInt32LittleEndian(dataBytes) & mask) << (32 - (bitOffset + bitLength));
                if (id.IsSigned)
                {
                    value >>= (32 - bitLength);
                }
                else
                {
                    value = value >>> (32 - bitLength);
                }

                return true;
            }
            else
            {
                value = default;
                return false;
            }
        }

        private int GetSetting(ref S2C_ClientSettings settings, ClientSettingIdentifier id)
        {
            if (!id.IsValid())
            {
                _logManager.LogM(LogLevel.Warn, nameof(ClientSettings), $"Invalid ClientSettingsIdentifier: {id}");
                return 0;
            }

            int bitLength = id.BitLength;
            int bitOffset = id.BitOffset;
            int byteOffset = id.ByteOffset;

            if (id.FieldType == ClientSettingIdentifierFieldType.Bit8)
            {
                uint mask = 0xFFu >> (8 - bitLength) << bitOffset;
                int value = (int)(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref settings, 1))[byteOffset] & mask) << (32 - (bitOffset + bitLength));
                if (id.IsSigned)
                {
                    value >>= (32 - bitLength);
                }
                else
                {
                    value = value >>> (32 - bitLength);
                }

                return value;
            }
            else if (id.FieldType == ClientSettingIdentifierFieldType.Bit16)
            {
                uint mask = 0xFFFFu >> (16 - bitLength) << bitOffset;
                Span<byte> dataBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref settings, 1)).Slice(byteOffset, 2);
                int value = (int)(BinaryPrimitives.ReadUInt16LittleEndian(dataBytes) & mask) << (32 - (bitOffset + bitLength));
                if (id.IsSigned)
                {
                    value >>= (32 - bitLength);
                }
                else
                {
                    value = value >>> (32 - bitLength);
                }

                return value;
            }
            else if (id.FieldType == ClientSettingIdentifierFieldType.Bit32)
            {
                uint mask = 0xFFFFFFFF >> (32 - bitLength) << bitOffset;
                Span<byte> dataBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref settings, 1)).Slice(byteOffset, 4);
                int value = (int)(BinaryPrimitives.ReadUInt32LittleEndian(dataBytes) & mask) << (32 - (bitOffset + bitLength));
                if (id.IsSigned)
                {
                    value >>= (32 - bitLength);
                }
                else
                {
                    value = value >>> (32 - bitLength);
                }

                return value;
            }
            else
            {
                return 0;
            }
        }

        private void SendOneSettings(Player player, ArenaData arenaData)
        {
            if (player is null)
                return;

            if (arenaData is null)
                return;

            if (!player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                return;

            DoMask(ref playerData.Settings, ref arenaData.Settings, ref arenaData.OverrideData, ref playerData.OverrideData);

            if (playerData.Settings.Type == (byte)S2CPacketType.Settings)
            {
                _network.SendToOne(
                    player,
                    ref playerData.Settings,
                    NetSendFlags.Reliable);
            }

            static void DoMask(ref S2C_ClientSettings destination, ref S2C_ClientSettings source, ref OverrideData arenaOverrideData, ref OverrideData playerOverrideData)
            {
                Span<uint> destData = MemoryMarshal.Cast<S2C_ClientSettings, uint>(MemoryMarshal.CreateSpan(ref destination, 1));
                ReadOnlySpan<uint> srcData = MemoryMarshal.Cast<S2C_ClientSettings, uint>(MemoryMarshal.CreateReadOnlySpan(ref source, 1));

                ReadOnlySpan<uint> arenaData = MemoryMarshal.Cast<byte, uint>(arenaOverrideData.DataBytes);
                ReadOnlySpan<uint> arenaMask = MemoryMarshal.Cast<byte, uint>(arenaOverrideData.MaskBytes);

                ReadOnlySpan<uint> playerData = MemoryMarshal.Cast<byte, uint>(playerOverrideData.DataBytes);
                ReadOnlySpan<uint> playerMask = MemoryMarshal.Cast<byte, uint>(playerOverrideData.MaskBytes);

                for (int i = 0; i < destData.Length; i++)
                {
                    destData[i] = (((srcData[i] & ~arenaMask[i]) | (arenaData[i] & arenaMask[i])) & ~playerMask[i]) | (playerData[i] & playerMask[i]);
                }
            }
        }

        #region Helper types

        private class ArenaData : IResettable
        {
            public S2C_ClientSettings Settings;
            public OverrideData OverrideData;

            /// <summary>
            /// prizeweight partial sums. 1-28 are used for now, representing prizes 1 to 28.
            /// 0 = null prize
            /// </summary>
            public ushort[] pwps = new ushort[32];

            public bool TryReset()
            {
                Settings = default;
                OverrideData = default;
                Array.Clear(pwps);
                return true;
            }
        }

        private class PlayerData : IResettable
        {
            public S2C_ClientSettings Settings;
            public OverrideData OverrideData;

            public void Reset()
            {
                Settings = default;
                OverrideData = default;
            }

            bool IResettable.TryReset()
            {
                Reset();
                return true;
            }
        }

        private struct OverrideData
        {
            private S2C_ClientSettings _data;
            private S2C_ClientSettings _mask;

            public Span<byte> DataBytes => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _data, 1));
            public Span<byte> MaskBytes => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _mask, 1));
        }

        #endregion
    }
}
