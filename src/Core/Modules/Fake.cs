using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for managing fake players.
    /// </summary>
    [CoreModuleInfo]
    public class Fake : IModule, IFake
    {
        private ICommandManager _commandManager;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IPlayerData _playerData;

        private IChatNetwork _chatNetwork;
        private INetwork _network;

        private InterfaceRegistrationToken<IFake> _iFakeToken;

        #region Module methods

        public bool Load(
            ComponentBroker broker,
            ICommandManager commandManager,
            ILogManager logManager,
            IMainloop mainloop,
            IPlayerData playerData)
        {
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _chatNetwork = broker.GetInterface<IChatNetwork>();
            _network = broker.GetInterface<INetwork>();

            _commandManager.AddCommand("makefake", Command_makefake);
            _commandManager.AddCommand("killfake", Command_killfake);

            _iFakeToken = broker.RegisterInterface<IFake>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iFakeToken) != 0)
                return false;

            _commandManager.RemoveCommand("makefake", Command_makefake);
            _commandManager.RemoveCommand("killfake", Command_killfake);

            if (_chatNetwork != null)
                broker.ReleaseInterface(ref _chatNetwork);

            if (_network != null)
                broker.ReleaseInterface(ref _network);

            return true;
        }

        #endregion

        #region Command handlers

        private void Command_makefake(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            CreateFakePlayer(parameters, player.Arena, ShipType.Spec, 9999);
        }

        private void Command_killfake(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (target.TryGetPlayerTarget(out Player targetPlayer))
            {
                EndFaked(targetPlayer);
            }
        }

        #endregion

        public Player CreateFakePlayer(ReadOnlySpan<char> name, Arena arena, ShipType ship, short freq)
        {
            name = name.Trim();
            if (name.IsEmpty)
                return null;

            Player player = _playerData.NewPlayer(ClientType.Fake);
            if (player is null)
                return null;

            if (name.Length > Constants.MaxPlayerNameLength)
            {
                name = name[..Constants.MaxPlayerNameLength];
            }

            player.Packet.Name.Set(name);
            player.Name = name.ToString();
            player.Packet.Squad.Set("");
            player.Squad = string.Empty;
            player.ClientName = "<internal fake player>";
            player.Ship = ship;
            player.Freq = freq;
            player.Arena = arena;

            _network?.SendToArena(arena, player, ref player.Packet, NetSendFlags.Reliable);
            _chatNetwork?.SendToArena(arena, player, $"ENTERING:{player.Name}:{ship:D}:{freq:D}");

            player.Status = PlayerState.Playing;

            _logManager.LogP(LogLevel.Info, nameof(Fake), player, $"Fake player created.");

            return player;
        }

        public bool EndFaked(Player player)
        {
            if (player is null)
                return false;

            if (player.Type != ClientType.Fake)
                return false;

            if (player.Status != PlayerState.Playing || player.Arena == null)
                _logManager.LogP(LogLevel.Warn, nameof(Fake), player, $"Fake player in bad status.");

            _mainloop.QueueMainWorkItem(MainloopWork_EndFake, player);
            return true;

            void MainloopWork_EndFake(Player player)
            {
                Arena arena = player.Arena;

                if (arena is not null)
                {
                    S2C_PlayerLeaving packet = new((short)player.Id);
                    _network?.SendToArena(arena, player, ref packet, NetSendFlags.Reliable);
                    _chatNetwork?.SendToArena(arena, player, $"LEAVING:{player.Name}");
                }

                _logManager.LogP(LogLevel.Info, nameof(Fake), player, $"Fake player destroyed.");

                _playerData.FreePlayer(player);
            }
        }
    }
}
