using System;
using System.Collections.Generic;
using System.Text;
using SS.Core.ComponentInterfaces;

namespace SS.Core.Modules
{
    /// <summary>
    /// the equivalent of ap_multipub.c
    /// </summary>
    [CoreModuleInfo]
    public class ArenaPlaceMultiPub : IModule, IArenaPlace
    {
        private IConfigManager _configManager;
        private IArenaManager _arenaManager;
        private InterfaceRegistrationToken _iArenaPlaceToken;

        private string[] _pubNames;

        #region IModule Members

        public bool Load(ComponentBroker broker, IConfigManager configManager, IArenaManager arenaManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));

            LoadPubNames();

            _iArenaPlaceToken = broker.RegisterInterface<IArenaPlace>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<IArenaPlace>(ref _iArenaPlaceToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IArenaPlace Members

        [ConfigHelp("General", "DesiredPlaying", ConfigScope.Global, typeof(int), DefaultValue = "15", 
            Description = "This controls when the server will create new public arenas.")]
        bool IArenaPlace.Place(out string arenaName, ref int spawnX, ref int spawnY, Player p)
        {
            arenaName = string.Empty;

            // if the player connected through an ip/port that specified a connectas field, then try just that arena
            IEnumerable<string> tryList;
            if (string.IsNullOrEmpty(p.ConnectAs))
                tryList = _pubNames;
            else
                tryList = new string[] { p.ConnectAs };

            for (int pass = 0; pass < 10; pass++)
            {
                foreach (string name in tryList)
                {
                    string tryName = pass == 0 ? name : name + pass;

                    Arena arena = _arenaManager.FindArena(tryName, out _, out int playing);
                    if (arena == null)
                    {
                        // doesn't exist yet, use a backup only
                        if(string.IsNullOrEmpty(arenaName))
                            arenaName = tryName;
                    }
                    else
                    {
                        int desired = _configManager.GetInt(arena.Cfg, "General", "DesiredPlaying", 15);
                        if (playing < desired)
                        {
                            // we have fewer playing than we want, dump here
                            arenaName = tryName;
                            return true;
                        }
                    }
                }
            }

            return !string.IsNullOrEmpty(arenaName);
        }

        #endregion

        [ConfigHelp("General", "PublicArenas", ConfigScope.Global, typeof(string), 
            "A list of public arenas (base arena names) that the server should place players in when a specific arena is not requested. " +
            "Allowed delimiters include: ' ' (space), ',', ':', and ';'.")]
        private void LoadPubNames()
        {
            string delimitedArenaNames = _configManager.GetStr(_configManager.Global, "General", "PublicArenas");
            if (string.IsNullOrEmpty(delimitedArenaNames))
                _pubNames = new string[0];
            else
                _pubNames = delimitedArenaNames.Split(new char[] { ' ', ',', ':', ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
