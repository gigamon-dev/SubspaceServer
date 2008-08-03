using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SS.Core.ComponentInterfaces;
using System.Reflection;

namespace SS.Core.Modules
{
    public class PlayerCommand : IModule
    {
        private ModuleManager _mm;
        private IPlayerData _playerData;
        private IChat _chat;
        private ICommandManager _commandManager;
        //private ILogManager _logManager;
        //private ICapabilityManager _capabilityManager;
        //private IConfigManager _configManager;

        private DateTime _startedAt;

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get{
                return new Type[] {
                    typeof(IPlayerData), 
                    typeof(IChat), 
                    typeof(ICommandManager)
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;

            _playerData = interfaceDependencies[typeof(IPlayerData)] as IPlayerData;
            _chat = interfaceDependencies[typeof(IChat)] as IChat;
            _commandManager = interfaceDependencies[typeof(ICommandManager)] as ICommandManager;

            _startedAt = DateTime.Now;

            // TODO: do some sort of derivative of that command group thing asss does

            _commandManager.AddCommand("uptime", command_uptime, null, 
@"Targets: none
Args: none
Displays how long the server has been running.");

            _commandManager.AddCommand("version", command_version, null, 
@"Targets: none
Args: none
Displays version information about the server. It might also print out some information about the machine that it's running on.");

            _commandManager.AddCommand("sheep", command_sheep, null, null);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            return true;
        }

        #endregion

        private void command_uptime(string command, string parameters, Player p, ITarget target)
        {
            TimeSpan ts = DateTime.Now - _startedAt;

            _chat.SendMessage(p, "uptime: {0} days {1} hours {2} minutes {3} seconds", ts.Days, ts.Hours, ts.Minutes, ts.Seconds);
        }

        private void command_version(string command, string parameters, Player p, ITarget target)
        {
            Assembly assembly = Assembly.GetEntryAssembly();

            _chat.SendMessage(p, "Subspace Server .NET {0}, .NET Framework {1}", assembly.GetName().Version, Environment.Version);
            _chat.SendMessage(p, "running on {0}, host:{1}", Environment.OSVersion, System.Net.Dns.GetHostName());
        }

        private void command_sheep(string command, string parameters, Player p, ITarget target)
        {
            if (target.Type != TargetType.Arena)
                return;

            string sheepMessage = null;//_configManager.

            if (sheepMessage != null)
                _chat.SendSoundMessage(p, ChatSound.Sheep, sheepMessage);
            else
                _chat.SendSoundMessage(p, ChatSound.Sheep, "Sheep successfully cloned -- hello Dolly");
        }
    }
}
