using System;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that helps to determine the arena to place a player in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If the player connected to the server through a port that specified a ConnectAs (Listen:ConnectAs in global.conf), it will use that as the base arena name.
    /// Otherwise, it will use the public arena names (General:PublicArenas in global.conf).
    /// If an existing arena is found, it uses the General:DesiredPlaying setting in the arena.conf to determine whether the player can be placed.
    /// If already at or above the desired # playing, it will attempt the next arena number, up to 9 (e.g., foo, foo1, foo2, ... foo9).
    /// </para>
    /// <para>This is equivalent to ap_multipub.c in ASSS.</para>
    /// </remarks>
    [CoreModuleInfo]
    public class ArenaPlaceMultiPub : IModule, IArenaPlace
    {
        private IConfigManager _configManager;
        private IArenaManager _arenaManager;
        private InterfaceRegistrationToken<IArenaPlace> _iArenaPlaceToken;

        private const int InitialArenaNameListCapacity = 8;
        private readonly ObjectPool<List<string>> _stringListPool = new DefaultObjectPool<List<string>>(new StringListPooledObjectPolicy());
        private readonly List<string> _pubNames = new(InitialArenaNameListCapacity);
        private readonly object _lock = new();

        #region IModule Members

        public bool Load(ComponentBroker broker, IConfigManager configManager, IArenaManager arenaManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));

            LoadPubNames();

            GlobalConfigChangedCallback.Register(broker, Callback_GlobalConfigChanged);

            _iArenaPlaceToken = broker.RegisterInterface<IArenaPlace>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iArenaPlaceToken) != 0)
                return false;

            GlobalConfigChangedCallback.Unregister(broker, Callback_GlobalConfigChanged);

            return true;
        }

        #endregion

        #region IArenaPlace Members

        [ConfigHelp("General", "DesiredPlaying", ConfigScope.Arena, typeof(int), DefaultValue = "15", 
            Description = """
                The limit at which the server will try to create a new public arena for incoming players.
                This setting works in conjunction with the General:PublicArenas global.conf setting.
                """)]
        bool IArenaPlace.TryPlace(Span<char> arenaName, ref int spawnX, ref int spawnY, Player player, out int charsWritten)
        {
            arenaName.Clear();
            charsWritten = 0;

            List<string> tryList = _stringListPool.Get();

            try
            {
                if (!string.IsNullOrWhiteSpace(player.ConnectAs))
                {
                    // The player connected through an ip/port that specified a ConnectAs, just try that arena.
                    tryList.Add(player.ConnectAs);
                }
                else
                {
                    // No ConnectAs, try the public arenas.
                    lock (_lock)
                    {
                        tryList.AddRange(_pubNames);
                    }
                }

                Span<char> buffer = stackalloc char[Constants.MaxArenaNameLength];

                for (int pass = 0; pass < 10; pass++)
                {
                    foreach (string name in tryList)
                    {
                        if (!Arena.TryCreateArenaName(buffer, name, pass, out int bufferWritten))
                            continue;

                        ReadOnlySpan<char> tryName = buffer[..bufferWritten];
                        Arena arena = _arenaManager.FindArena(tryName, out _, out int playing);
                        if (arena is null)
                        {
                            // doesn't exist yet, use as a backup only
                            if (charsWritten == 0)
                            {
                                tryName.CopyTo(arenaName);
                                charsWritten = tryName.Length;
                            }
                        }
                        else
                        {
                            int desired = _configManager.GetInt(arena.Cfg, "General", "DesiredPlaying", 15);
                            if (playing < desired)
                            {
                                // we have fewer playing than we want, place here
                                arena.Name.CopyTo(arenaName);
                                charsWritten = arena.Name.Length;
                                return true;
                            }
                        }
                    }
                }
            }
            finally
            {
                _stringListPool.Return(tryList);
            }

            return charsWritten > 0;
        }

        #endregion

        private void Callback_GlobalConfigChanged()
        {
            LoadPubNames();
        }

        [ConfigHelp("General", "PublicArenas", ConfigScope.Global, typeof(string), 
            Description = """
            A list of public arenas (base arena names) that the server should place players in when a specific arena is not requested.
            Allowed delimiters include: ' ' (space), ',', ':', and ';'.
            When omitted, the server will use the default public arena names: "0", "1", ...
            which on the client-side respectively are displayed as "(Public 0)", "(Public 1)", ...
            This setting works in conjunction with each arena's the General:DesiredPlaying arena.conf setting.
            """)]
        private void LoadPubNames()
        {
            string delimitedArenaNames = _configManager.GetStr(_configManager.Global, "General", "PublicArenas");

            lock (_lock)
            {
                List<string> arenaNames = _stringListPool.Get();

                try
                {
                    ReadOnlySpan<char> remaining = delimitedArenaNames;
                    ReadOnlySpan<char> token;
                    while (!(token = remaining.GetToken(" ,:;", out remaining)).IsEmpty)
                    {
                        // Try to find an existing instance to avoid an allocation.
                        string arenaName = null;
                        foreach (string pubName in _pubNames)
                        {
                            if (token.Equals(pubName, StringComparison.OrdinalIgnoreCase))
                            {
                                arenaName = pubName;
                                break;
                            }
                        }

                        // Only allocate if necessary.
                        arenaNames.Add(arenaName ?? token.ToString());
                    }

                    _pubNames.Clear();
                    _pubNames.AddRange(arenaNames);
                }
                finally
                {
                    _stringListPool.Return(arenaNames);
                }

                if (_pubNames.Count == 0)
                {
                    // No configured arena names.
                    // Just search for the default public arena names: "0", "1", ...
                    // which on the client-side respectively relate to "(Public 0)", "(Public 1)", ...
                    _pubNames.Add("");
                }
            }
        }

        #region Helper types

        private class StringListPooledObjectPolicy : IPooledObjectPolicy<List<string>>
        {
            public List<string> Create()
            {
                return new List<string>(InitialArenaNameListCapacity);
            }

            public bool Return(List<string> obj)
            {
                if (obj is null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        #endregion
    }
}
