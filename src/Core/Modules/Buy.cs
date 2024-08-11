using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that allows players to buy items with points.
    /// </summary>
    [CoreModuleInfo]
    public class Buy(
        IArenaPlayerStats arenaPlayerStats,
        IChat chat,
        ICommandManager commandManager,
        IConfigManager configManager,
        IGame game,
        ILogManager logManager,
        IScoreStats scoreStats) : IModule, IArenaAttachableModule
    {
        private readonly IArenaPlayerStats _arenaPlayerStats = arenaPlayerStats ?? throw new ArgumentNullException(nameof(arenaPlayerStats));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly IGame _game = game ?? throw new ArgumentNullException(nameof(game));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IScoreStats _scoreStats = scoreStats ?? throw new ArgumentNullException(nameof(scoreStats));

        [ConfigHelp("Cost", "XRadar", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for XRadar. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Recharge", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Recharge Upgrade. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Energy", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Energy Upgrade. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Rotation", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Rotation Upgrade. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Stealth", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Stealth Ability. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Cloak", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Cloak Ability. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Gun", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Gun Upgrade. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Bomb", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Bomb Upgrade. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Bounce", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Bouncing Bullets. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Thrust", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Thrust Upgrade. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Speed", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Top Speed. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "MultiFire", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for MultiFire. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Prox", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Proximity Bombs. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Super", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Super. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Shield", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Shields. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Shrap", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Shrapnel Upgrade. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "AntiWarp", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for AntiWarp Ability. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Repel", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Repel. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Burst", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Burst. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Decoy", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Decoy. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Thor", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Thor. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Brick", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Brick. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Rocket", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Rocket. 0 to disallow purchase.")]
        [ConfigHelp("Cost", "Portal", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "Points cost for Portal. 0 to disallow purchase.")]
        private static readonly (string MatchString, string SettingKey, Prize Prize)[] _items = new[]
        {
            ("x",        "XRadar",    Prize.XRadar),
            ("recharge", "Recharge",  Prize.Recharge),
            ("energy",   "Energy",    Prize.Energy),
            ("rot",      "Rotation",  Prize.Rotation),
            ("stealth",  "Stealth",   Prize.Stealth),
            ("cloak",    "Cloak",     Prize.Cloak),
            ("gun",      "Gun",       Prize.Gun),
            ("bomb",     "Bomb",      Prize.Bomb),
            ("bounce",   "Bounce",    Prize.Bounce),
            ("thrust",   "Thrust",    Prize.Thrust),
            ("speed",    "Speed",     Prize.Speed),
            ("multi",    "MultiFire", Prize.Multifire),
            ("prox",     "Prox",      Prize.Prox),
            ("super",    "Super",     Prize.Super),
            ("shield",   "Shield",    Prize.Shield),
            ("shrap",    "Shrap",     Prize.Shrap),
            ("anti",     "AntiWarp",  Prize.Antiwarp),
            ("rep",      "Repel",     Prize.Repel),
            ("burst",    "Burst",     Prize.Burst),
            ("decoy",    "Decoy",     Prize.Decoy),
            ("thor",     "Thor",      Prize.Thor),
            ("brick",    "Brick",     Prize.Brick),
            ("rocket",   "Rocket",    Prize.Rocket),
            ("port",     "Portal",    Prize.Portal),
        };

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            _commandManager.AddCommand("buy", Command_buy, arena);
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            _commandManager.RemoveCommand("buy", Command_buy, arena);
            return true;
        }

        #endregion

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<item>",
            Description = "Buys an item with points.")]
        private void Command_buy(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (parameters.IsEmpty)
            {
                PrintCosts(arena.Cfg!, player);
                return;
            }

            int itemIndex = -1;
            for (int i = 0; i < _items.Length; i++)
            {
                if (parameters.Contains(_items[i].MatchString, StringComparison.OrdinalIgnoreCase))
                {
                    itemIndex = i;
                }
            }

            if (itemIndex == -1)
            {
                _chat.SendMessage(player, "Invalid item specified for purchase.");
                return;
            }

            int cost = _configManager.GetInt(arena.Cfg!, "Cost", _items[itemIndex].SettingKey, 0);
            if (cost == 0)
            {
                _chat.SendMessage(player, "That item isn't available for purchase.");
                return;
            }

            if (player.Ship == ShipType.Spec)
            {
                _chat.SendMessage(player, "Spectators cannot purchase items.");
                return;
            }

            bool anywhere = _configManager.GetInt(arena.Cfg!, "Cost", "PurchaseAnytime", 0) != 0;
            if (!anywhere && (player.Position.Status & PlayerPositionStatus.Safezone) != PlayerPositionStatus.Safezone)
            {
                _chat.SendMessage(player, "You must be in a safe zone to purchase items.");
                return;
            }

            if (!_arenaPlayerStats.TryGetStat(player, StatCodes.KillPoints, PersistInterval.Reset, out long killPoints)
                || !_arenaPlayerStats.TryGetStat(player, StatCodes.FlagPoints, PersistInterval.Reset, out long flagPoints)
                || killPoints + flagPoints < cost)
            {
                _chat.SendMessage(player, "You don't have enough points to purchase that item.");
                return;
            }

            // Deduct from flag points to keep kill average the same.
            _arenaPlayerStats.IncrementStat(player, StatCodes.FlagPoints, PersistInterval.Reset, -cost);
            _scoreStats.SendUpdates(arena, null);

            _game.GivePrize(player, _items[itemIndex].Prize, 1);
            _chat.SendMessage(player, $"Bought {_items[itemIndex].SettingKey}.");
            _logManager.LogP(LogLevel.Drivel, nameof(Buy), player, $"Bought {_items[itemIndex].SettingKey}.");

            void PrintCosts(ConfigHandle ch, Player player)
            {
                foreach ((_, string key, Prize prize) in _items)
                {
                    int cost = _configManager.GetInt(ch, "Cost", key, 0);
                    if (cost != 0)
                    {
                        _chat.SendMessage(player, $"buy: {key,-9} {cost,6}");
                    }
                }
            }
        }
    }
}
