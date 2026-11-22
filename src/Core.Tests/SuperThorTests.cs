using SS.Core.Modules;
using SS.Packets.Game;

namespace SS.Core.Tests
{
    public class SuperSettingsTests
    {
        private static int ShipShort(string key) => Array.IndexOf(ClientSettingsConfig.ShipShortNames, key);
        private static int ShipByte(string key) => Array.IndexOf(ClientSettingsConfig.ShipByteNames, key);
        private static int Long(string section, string key) => Array.IndexOf(ClientSettingsConfig.LongNames, (section, key));
        private static int Short(string section, string key) => Array.IndexOf(ClientSettingsConfig.ShortNames, (section, key));
        private static int Byte(string section, string key) => Array.IndexOf(ClientSettingsConfig.ByteNames, (section, key));

        private static readonly string[] ZeroedShipShorts =
        [
            "BulletFireEnergy", "MultiFireEnergy", "BombFireEnergy", "BombFireEnergyUpgrade", "LandmineFireEnergy",
            "LandmineFireEnergyUpgrade", "CloakEnergy", "StealthEnergy", "AntiWarpEnergy", "XRadarEnergy",
            "AfterburnerEnergy", "BombThrust", "AttachBounty",
        ];

        private static readonly string[] FireDelays = ["BulletFireDelay", "MultiFireDelay", "BombFireDelay", "LandmineFireDelay"];

        private static readonly string[] ChangedShipShorts =
        [
            .. ZeroedShipShorts,
            .. FireDelays,
            "MaximumThrust", "MaximumSpeed", "MaximumRecharge",
            "InitialRotation", "InitialThrust", "InitialSpeed", "InitialRecharge", "InitialEnergy",
            "DamageFactor", "PrizeShareLimit",
        ];

        private static readonly (string Key, byte Value)[] ItemMaximums =
        [
            ("MaxMines", 128), ("RepelMax", 117), ("BurstMax", 128), ("DecoyMax", 128),
            ("ThorMax", 128), ("BrickMax", 128), ("RocketMax", 128), ("PortalMax", 128),
        ];

        /// <summary>
        /// Settings with a different value in every field, so that each change can be seen.
        /// </summary>
        private static S2C_ClientSettings CreateSettings()
        {
            S2C_ClientSettings settings = default;
            settings.Type = (byte)S2CPacketType.Settings;

            for (int ship = 0; ship < 8; ship++)
            {
                ref ShipSettings s = ref settings.Ships[ship];

                for (int i = 0; i < ShipSettings.Int32InlineArray.Length; i++)
                    s.Int32Settings[i] = 1000 + ship * 10 + i;

                for (int i = 0; i < ShipSettings.Int16InlineArray.Length; i++)
                    s.Int16Settings[i] = (short)(100 + ship * 100 + i);

                for (int i = 0; i < ShipSettings.ByteInlineArray.Length; i++)
                    s.ByteSettings[i] = (byte)(ship * 20 + i + 1);

                s.Weapons.bitfield = 0x1234_5678u + (uint)ship;
            }

            for (int i = 0; i < S2C_ClientSettings.Int32InlineArray.Length; i++)
                settings.Int32Settings[i] = 50_000 + i;

            for (int i = 0; i < S2C_ClientSettings.Int16InlineArray.Length; i++)
                settings.Int16Settings[i] = (short)(5_000 + i);

            for (int i = 0; i < S2C_ClientSettings.ByteInlineArray.Length; i++)
                settings.ByteSettings[i] = (byte)(10 + i);

            for (int i = 0; i < S2C_ClientSettings.PrizeWeightInlineArray.Length; i++)
                settings.PrizeWeightSettings[i] = (byte)(200 + i);

            return settings;
        }

        [Fact]
        public void ShipSettings_AreChanged()
        {
            S2C_ClientSettings original = CreateSettings();
            S2C_ClientSettings settings = original;

            AdminCommand.MakeSuperSettings(ref settings);

            for (int ship = 0; ship < 8; ship++)
            {
                ref ShipSettings before = ref original.Ships[ship];
                ref ShipSettings after = ref settings.Ships[ship];

                foreach (string key in ZeroedShipShorts)
                    Assert.Equal(0, after.Int16Settings[ShipShort(key)]);

                foreach (string key in FireDelays)
                    Assert.Equal(10, after.Int16Settings[ShipShort(key)]);

                short thrust = (short)(before.Int16Settings[ShipShort("MaximumThrust")] * 5 / 2);
                short speed = (short)(before.Int16Settings[ShipShort("MaximumSpeed")] * 3);
                short recharge = (short)(before.Int16Settings[ShipShort("MaximumRecharge")] * 16);

                Assert.Equal(thrust, after.Int16Settings[ShipShort("MaximumThrust")]);
                Assert.Equal(speed, after.Int16Settings[ShipShort("MaximumSpeed")]);
                Assert.Equal(recharge, after.Int16Settings[ShipShort("MaximumRecharge")]);

                Assert.Equal(before.Int16Settings[ShipShort("MaximumRotation")], after.Int16Settings[ShipShort("InitialRotation")]);
                Assert.Equal(thrust, after.Int16Settings[ShipShort("InitialThrust")]);
                Assert.Equal(speed, after.Int16Settings[ShipShort("InitialSpeed")]);
                Assert.Equal(recharge, after.Int16Settings[ShipShort("InitialRecharge")]);
                Assert.Equal(before.Int16Settings[ShipShort("MaximumEnergy")], after.Int16Settings[ShipShort("InitialEnergy")]);

                Assert.Equal(100, after.Int16Settings[ShipShort("DamageFactor")]);
                Assert.Equal(999, after.Int16Settings[ShipShort("PrizeShareLimit")]);

                foreach ((string key, byte value) in ItemMaximums)
                    Assert.Equal(value, after.ByteSettings[ShipByte(key)]);

                Assert.Equal(16, after.Weapons.ShrapnelRate);
            }
        }

        [Fact]
        public void ArenaSettings_AreChanged()
        {
            S2C_ClientSettings settings = CreateSettings();

            AdminCommand.MakeSuperSettings(ref settings);

            Assert.Equal(0, settings.Int32Settings[Long("Bullet", "BulletDamageLevel")]);
            Assert.Equal(0, settings.Int32Settings[Long("Bullet", "BulletDamageUpgrade")]);
            Assert.Equal(0, settings.Int32Settings[Long("Bomb", "BombDamageLevel")]);
            Assert.Equal(0, settings.Int32Settings[Long("Burst", "BurstDamageLevel")]);
            Assert.Equal(0, settings.Int16Settings[Short("Prize", "EngineShutdownTime")]);
            Assert.Equal(128, settings.ByteSettings[Byte("Team", "MaxPerTeam")]);
            Assert.Equal(128, settings.ByteSettings[Byte("Mine", "TeamMaxMines")]);
        }

        [Fact]
        public void OtherSettings_AreUnchanged()
        {
            S2C_ClientSettings original = CreateSettings();
            S2C_ClientSettings settings = original;

            AdminCommand.MakeSuperSettings(ref settings);

            Assert.Equal(original.Type, settings.Type);

            for (int ship = 0; ship < 8; ship++)
            {
                ref ShipSettings before = ref original.Ships[ship];
                ref ShipSettings after = ref settings.Ships[ship];

                for (int i = 0; i < ShipSettings.Int32InlineArray.Length; i++)
                    Assert.Equal(before.Int32Settings[i], after.Int32Settings[i]);

                for (int i = 0; i < ShipSettings.Int16InlineArray.Length; i++)
                {
                    if (!ChangedShipShorts.Contains(ClientSettingsConfig.ShipShortNames[i]))
                        Assert.Equal(before.Int16Settings[i], after.Int16Settings[i]);
                }

                for (int i = 0; i < ShipSettings.ByteInlineArray.Length; i++)
                {
                    if (!ItemMaximums.Any(item => item.Key == ClientSettingsConfig.ShipByteNames[i]))
                        Assert.Equal(before.ByteSettings[i], after.ByteSettings[i]);
                }

                // Only ShrapnelRate (bits 5-9) changes.
                const uint ShrapnelRateBits = 0b11_1110_0000;
                Assert.Equal(before.Weapons.bitfield & ~ShrapnelRateBits, after.Weapons.bitfield & ~ShrapnelRateBits);
            }

            int[] changedLongs = [Long("Bullet", "BulletDamageLevel"), Long("Bullet", "BulletDamageUpgrade"), Long("Bomb", "BombDamageLevel"), Long("Burst", "BurstDamageLevel")];
            for (int i = 0; i < S2C_ClientSettings.Int32InlineArray.Length; i++)
            {
                if (!changedLongs.Contains(i))
                    Assert.Equal(original.Int32Settings[i], settings.Int32Settings[i]);
            }

            for (int i = 0; i < S2C_ClientSettings.Int16InlineArray.Length; i++)
            {
                if (i != Short("Prize", "EngineShutdownTime"))
                    Assert.Equal(original.Int16Settings[i], settings.Int16Settings[i]);
            }

            for (int i = 0; i < S2C_ClientSettings.ByteInlineArray.Length; i++)
            {
                if (i != Byte("Team", "MaxPerTeam") && i != Byte("Mine", "TeamMaxMines"))
                    Assert.Equal(original.ByteSettings[i], settings.ByteSettings[i]);
            }

            for (int i = 0; i < S2C_ClientSettings.PrizeWeightInlineArray.Length; i++)
                Assert.Equal(original.PrizeWeightSettings[i], settings.PrizeWeightSettings[i]);
        }

        [Theory]
        [InlineData(20000, 30000, 5000)]
        [InlineData(-3, -7, -1)]
        public void Boosts_Use16BitArithmetic(short thrust, short speed, short recharge)
        {
            S2C_ClientSettings settings = CreateSettings();
            ref ShipSettings ship = ref settings.Ships[0];
            ship.Int16Settings[ShipShort("MaximumThrust")] = thrust;
            ship.Int16Settings[ShipShort("MaximumSpeed")] = speed;
            ship.Int16Settings[ShipShort("MaximumRecharge")] = recharge;

            AdminCommand.MakeSuperSettings(ref settings);

            // Thrust is multiplied as a signed value, then divided rounding toward 0. Speed and recharge keep the low 16 bits.
            Assert.Equal((ushort)(thrust * 5 / 2), (ushort)ship.Int16Settings[ShipShort("MaximumThrust")]);
            Assert.Equal((ushort)((ushort)speed * 3), (ushort)ship.Int16Settings[ShipShort("MaximumSpeed")]);
            Assert.Equal((ushort)((ushort)recharge << 4), (ushort)ship.Int16Settings[ShipShort("MaximumRecharge")]);
        }
    }

    public class ThorLevelTests
    {
        [Theory]
        [InlineData(-1, 0)]
        [InlineData(int.MinValue, 0)]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(7, 3)]
        [InlineData(int.MaxValue, 3)]
        public void ClampThorLevel(int value, sbyte expected)
        {
            Assert.Equal(expected, Game.ClampThorLevel(value));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        public void Level_IsWrittenToWeaponBits(byte level)
        {
            WeaponData weapon = default;
            weapon.Type = WeaponCodes.Thor;
            weapon.Shrap = 31;
            weapon.Alternate = true;
            weapon.Level = 2;

            weapon.Level = level;

            Assert.Equal(WeaponCodes.Thor, weapon.Type);
            Assert.Equal(level, weapon.Level);
            Assert.Equal(31, weapon.Shrap);
            Assert.True(weapon.Alternate);
        }
    }
}
