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
    public class ConfigManager : IModule, IModuleLoaderAware, IConfigManager
    {
        private readonly Dictionary<string, ConfigFile> _opened = new Dictionary<string, ConfigFile>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<ConfigFile> _files = new LinkedList<ConfigFile>();
        private readonly object cfgmtx = new object(); // protects _opened and _files

        private ConfigHandle _global;
        public event EventHandler GlobalConfigChanged;

        private IServerTimer _timerManager;
        private ILogManager _logManager;

        public ConfigManager()
        {
        }

        /// <summary>
        /// The global config handle (global.conf)
        /// </summary>
        public ConfigHandle Global
        {
            get { return _global; }
        }

        private void global_changed(object clos)
        {
            if (GlobalConfigChanged != null)
                GlobalConfigChanged(this, EventArgs.Empty);
        }

        public ConfigHandle OpenConfigFile(string arena, string name, ConfigChangedDelegate configChanged, object clos)
        {
            string fname;

            // make sure at least the base file exists
            if (locateConfigFile(out fname, arena, name) == -1)
                return null;

            // first try to get it out of the table
            lock (cfgmtx)
            {
                ConfigFile cf;
                if (_opened.TryGetValue(fname, out cf) == false)
                {
                    // if not, make a new one
                    cf = new ConfigFile(fname, arena, name);

                    cf.doLoad(arena, name);

                    _opened.Add(fname, cf);
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

        internal static int locateConfigFile(out string dest, string arena, string name)
        {
            Dictionary<char, string> repls = new Dictionary<char, string>(2);
            repls.Add('n', name);
            repls.Add('b', arena);

            if (string.IsNullOrEmpty(name))
                repls['n'] = string.IsNullOrEmpty(arena) == false ? "arena.conf" : "global.conf";

            return PathUtil.find_file_on_path(out dest, Constants.CFG_CONFIG_SEARCH_PATH, repls);
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get
            {
                return new Type[] {
                    typeof(IServerTimer)
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _timerManager = interfaceDependencies[typeof(IServerTimer)] as IServerTimer;


            _global = OpenConfigFile(null, null, global_changed, null);
            if (_global == null)
                return false;

            // TODO: set timer to watch for when the server should write to config files (this is not a priority so i will hold off doing this for now)
            
            // TODO: set timer to watch for when the server should reload a file
            // TODO: instead of using timers to check for config files that have to be reloaded maybe use the FileSystemWatcher class
            mm.RegisterInterface<IConfigManager>(this);

            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            mm.UnregisterInterface<IConfigManager>();
            return true;
        }

        #endregion

        #region IModuleLoaderAware Members

        bool IModuleLoaderAware.PostLoad(ModuleManager mm)
        {
            _logManager = mm.GetInterface<ILogManager>();
            return true;
        }

        bool IModuleLoaderAware.PreUnload(ModuleManager mm)
        {
            mm.ReleaseInterface<ILogManager>();
            return true;
        }

        #endregion
    }
}
