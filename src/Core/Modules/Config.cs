using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Threading;

using SS.Core.ComponentInterfaces;
using SS.Utilities;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class ConfigManager : IModule, IModuleLoaderAware, IConfigManager
    {
        private readonly Dictionary<string, ConfigFile> _opened = new Dictionary<string, ConfigFile>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<ConfigFile> _files = new LinkedList<ConfigFile>();
        private readonly object cfgmtx = new object(); // protects _opened and _files

        public event EventHandler GlobalConfigChanged;

        private IMainloopTimer _mainloopTimer;
        private ILogManager _logManager;
        private InterfaceRegistrationToken _iConfigManagerToken;

        /// <summary>
        /// The global config handle (global.conf)
        /// </summary>
        public ConfigHandle Global
        {
            get;
            private set;
        }

        private void global_changed(object clos)
        {
            if (GlobalConfigChanged != null)
                GlobalConfigChanged(this, EventArgs.Empty);
        }

        public ConfigHandle OpenConfigFile(string arena, string name, ConfigChangedDelegate configChanged, object clos)
        {
            // make sure at least the base file exists
            string path = LocateConfigFile(arena, name);
            if (path == null)
                return null;

            // first try to get it out of the table
            lock (cfgmtx)
            {
                if (_opened.TryGetValue(path, out ConfigFile cf) == false)
                {
                    // if not, make a new one
                    cf = new ConfigFile(path, arena, name);
                    cf.Load(
                        LocateConfigFile, 
                        message => LogError(path, message));

                    _opened.Add(path, cf);
                    _files.AddLast(cf);
                }

                // create handle while holding cfgmtx so that the file doesn't get
                // garbage collected before it has a reference
                return new ConfigHandle(cf, configChanged, clos);
            }
        }

        public void CloseConfigFile(ConfigHandle ch)
        {
            if (ch == null)
                return;

            ConfigFile cf = ch.file;
            bool removed = false;

            cf.Lock();

            try
            {
                removed = cf.Handles.Remove(ch);
            }
            finally
            {
                cf.Unlock();
            }

            Debug.Assert(removed);
        }

        /// <summary>
        /// Forces the config module to reload the file from disk.
        /// You shouldn't have to use this, as the server automatically
        /// checks config files for modifications periodically.
        /// </summary>
        /// <param name="ch">the config file to reload</param>
        public void ReloadConfigFile(ConfigHandle ch)
        {
            // TODO: 
        }

        //public static ConfigHandle AddRef(ConfigHandle ch);

        /// <summary>
        /// Forces the server to write changes back to config files on disk.
        /// You shouldn't have to call this, as it's done automatically
        /// periocally.
        /// </summary>
        public void FlushDirtyValues()
        {
            // TODO: 
        }

        /// <summary>
        /// Forces the server to check all open config files for
        /// modifications since they were last loaded.
        /// You shouldn't have to call this, as it's done automatically
        /// periocally.
        /// </summary>
        public void CheckModifiedFiles()
        {
            // TODO: 
        }

        /// <summary>
        /// Forces a reload of one or more config files.
        /// Pass in NULL to force a reload of all open files. Pass in a
        /// string to limit reloading to files containing that string.
        /// </summary>
        /// <param name="pathname"></param>
        /// <param name="callback"></param>
        /// <param name="clos"></param>
        public void ForceReload(string pathname, ConfigChangedDelegate callback, object clos)
        {
            // TODO: 
        }

        public string GetStr(ConfigHandle ch, string section, string key)
        {
            if (ch == null)
                return null;

            string result;
            ConfigFile cf = ch.file;
            cf.Lock();

            bool haveSection = !string.IsNullOrEmpty(section);
            bool haveKey = !string.IsNullOrEmpty(key);

            if (haveSection && haveKey)
            {
                cf._table.TryGetValue(section + ':' + key, out result);
            }
            else if (haveSection)
            {
                cf._table.TryGetValue(section, out result);
            }
            else if (haveKey)
            {
                cf._table.TryGetValue(key, out result);
            }
            else
            {
                result = null;
            }

            cf.Unlock();

            return result;
        }

        public void SetStr(ConfigHandle ch, string section, string key, string value, string info, bool permanent)
        {
        }

        public void SetInt(ConfigHandle ch, string section, string key, int value, string info, bool permanent)
        {
            SetStr(ch, section, key, value.ToString(), info, permanent);
        }

        public int GetInt(ConfigHandle ch, string section, string key, int defvalue)
        {
            string value = GetStr(ch, section, key);
            if (string.IsNullOrEmpty(value))
            {
                return defvalue;
            }
            else
            {
                int result;
                if (int.TryParse(value, out result) == true)
                    return result;

                return value[0] == 'y' || value[0] == 'Y' ? 1 : 0;
            }
        }

        private static string LocateConfigFile(string arena, string name)
        {
            Dictionary<char, string> repls = new Dictionary<char, string>(2);
            repls.Add('n', name);
            repls.Add('b', arena);

            if (string.IsNullOrEmpty(name))
                repls['n'] = string.IsNullOrEmpty(arena) == false ? "arena.conf" : "global.conf";

            return PathUtil.FindFileOnPath(Constants.CFG_CONFIG_SEARCH_PATH, repls);
        }

        private void LogError(string fileName, string message)
        {
            if (_logManager != null)
            {
                _logManager.LogM(LogLevel.Warn, nameof(ConfigManager), $"Error loading config file '{fileName}'. {message}");
            }
            else
            {
                Console.Error.WriteLine($"W <{nameof(ConfigManager)}> Error loading config file '{fileName}'. {message}");
            }
        }

        #region IModule Members

        public bool Load(ComponentBroker broker, IMainloopTimer mainloopTimer)
        {
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));

            Global = OpenConfigFile(null, null, global_changed, null);
            if (Global == null)
                return false;

            // TODO: set timer to watch for when the server should write to config files (this is not a priority so i will hold off doing this for now)

            // TODO: set timer to watch for when the server should reload a file
            // TODO: instead of using timers to check for config files that have to be reloaded maybe use the FileSystemWatcher class
            _iConfigManagerToken = broker.RegisterInterface<IConfigManager>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<IConfigManager>(ref _iConfigManagerToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IModuleLoaderAware Members

        bool IModuleLoaderAware.PostLoad(ComponentBroker broker)
        {
            _logManager = broker.GetInterface<ILogManager>();
            return true;
        }

        bool IModuleLoaderAware.PreUnload(ComponentBroker broker)
        {
            broker.ReleaseInterface(ref _logManager);
            return true;
        }

        #endregion
    }
}
