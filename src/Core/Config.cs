using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Threading;

using SS.Utilities;
using SS.Core.ComponentInterfaces;
using System.Diagnostics;

namespace SS.Core
{
    

    public delegate void ConfigChangedDelegate(object clos);

    /// <summary>
    /// configuration manager
    /// 
    /// modules get all of their settings from here and nowhere else. there
    /// are two types of configuration files: global and arena. global ones
    /// apply to the whole zone, and are stored in the conf directory of the
    /// zone directory. arena ones are usually stored in arenas/arenaname,
    /// but this can be customized with the search path.
    /// 
    /// the main global configuration file is maintained internally to this
    /// moudule and you don't have to open or close it. just use GLOBAL as
    /// your ConfigHandle. arena configuration files are also maintained for
    /// you as arena->cfg. so typically you will only need to call GetStr and
    /// GetInt.
    ///
    /// there can also be secondary global or arena config files, specified
    /// with the second parameter of OpenConfigFile. these are used for staff
    /// lists and a few other special things. in general, you should use a
    /// new section in the global or arena config files rather than using a
    /// different file.
    ///
    /// setting configuration values is relatively straightforward. the info
    /// parameter to SetStr and SetInt should describe who initiated the
    /// change and when. this information may be written back to the
    /// configuration files.
    ///
    /// FlushDirtyValues and CheckModifiedFiles do what they say. there's no
    /// need to call them in general; the config module performs those
    /// actions internally based on timers also.
    /// </summary>
    public class ConfigManager : IModule, IModuleLoaderAware, IConfigManager
    {
        private readonly Dictionary<string, ConfigFile> _opened = new Dictionary<string, ConfigFile>();
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
            return true;
        }

        #endregion

        #region IModuleLoaderAware Members

        bool IModuleLoaderAware.PostLoad(ModuleManager mm)
        {
            // TODO: get logging interface
            Console.WriteLine("[ConfigManager] PostLoad");
            _logManager = mm.GetInterface<ILogManager>();
            return true;
        }

        bool IModuleLoaderAware.PreUnload(ModuleManager mm)
        {
            // TODO: release logging interface
            Console.WriteLine("[ConfigManager] PreUnload");
            mm.ReleaseInterface<ILogManager>();
            return true;
        }

        #endregion
    }

    
    internal class ConfigFile : IDisposable
    {
        //private const int MAX_SECTION_LENGTH = 64;
        //private const int MAX_KEY_LENGTH = 64;

        private readonly object mutex = new object();
        internal readonly LinkedList<ConfigHandle> Handles;
	    internal readonly Dictionary<string, string> _table;
	    //StringChunk *strings;  // collection of strings to conserve allocations and free all at once, dont think i need
	    //private LinkedList<object> dirty; // this must be used for setting config values (not implemented in this release)
        //private bool anychanged = false;
        private DateTime lastmod;
	    internal readonly string filename, arena, name;

        internal ConfigFile(string filename, string arena, string name)
        {
            // TODO: when i figure out what goes in each list and dictionary
            Handles = new LinkedList<ConfigHandle>();
            _table = new Dictionary<string, string>();
            //strings = new
            //dirty = new

            this.filename = filename;
            this.arena = arena;
            this.name = name;

            try
            {
                this.lastmod = File.GetLastWriteTimeUtc(filename);
            }
            catch
            {
                this.lastmod = DateTime.MinValue;
            }
        }

        internal void Lock()
        {
            Monitor.Enter(mutex);
        }

        internal void Unlock()
        {
            Monitor.Exit(mutex);
        }

        internal void doLoad(string arena, string name)
        {
            using (APPContext ctx = new APPContext(
                new APPFileFinderFunc(ConfigManager.locateConfigFile),
                new APPReportErrFunc(reportError),
                arena))
            {

                ctx.AddFile(name);

                string key = null;
                string line = null;
                string val = null;

                while (ctx.GetLine(out line))
                {
                    line = line.Trim();

                    if (line[0] == '[')
                    {
                        // new section: copy to key name
                        // skip leading brackets/spaces
                        key = StringUtils.TrimWhitespaceAndExtras(line, '[', ']');
                        key += ":";
                    }
                    else
                    {
                        string unescaped;
                        line = unescapeString(out unescaped, line, '=');

                        if (string.IsNullOrEmpty(unescaped))
                            continue;

                        if (!string.IsNullOrEmpty(line) && line[0] == '=')
                        {
                            line = line.Substring(1).Trim();
                            unescaped = unescaped.Trim();

                            unescapeString(out val, line, '\0');

                            if (unescaped.IndexOf(':') != -1)
                            {
                                // this syntax lets you specify a section and key on
                                // one line. it does _not_ modify the "current section"
                                _table[unescaped] = val;
                            }
                            else if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(unescaped))
                            {
                                _table[key + unescaped] = val;
                            }
                            else
                            {
                                reportError("ignoring value not in any section");
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(unescaped))
                            {
                                // there is no value for this key, so enter it with the empty string
                                _table[key + unescaped] = string.Empty;
                            }
                            else
                            {
                                reportError("ignoring value not in any section");
                            }
                        }
                    }
                }
            }
        }

        internal void writeDirtyValuesOne(bool callCallbacks)
        {
            // i think that this method writes changed settings to file
            // TODO: not necessary to get things running
        }

        private static void reportError(string error)
        {
            // TODO: use log manager
            Console.WriteLine("<config> " + error);
        }

        /// <summary>
        /// escapes the config file syntactic characters. currently just = and \
        /// </summary>
        /// <param name="src"></param>
        /// <returns></returns>
        private static string escapeString(string src)
        {
            StringBuilder sb = new StringBuilder(src);
            sb.Replace(@"=", @"\=");
            sb.Replace(@"\", @"\\");
            return sb.ToString();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="dst"></param>
        /// <param name="source"></param>
        /// <param name="stopon"></param>
        /// <returns>the remainder of source if stopped on a character</returns>
        private static string unescapeString(out string dst, string source, char stopon)
        {
            StringBuilder sb = new StringBuilder();
            int x;
            for (x = 0; x < source.Length; x++)
            {
                if (source[x] == stopon)
                    break;

                if (source[x] == '\\')
                {
                    x++;
                    if (x < source.Length)
                        sb.Append(source[x]);
                }
                else
                {
                    sb.Append(source[x]);
                }
            }

            dst = sb.ToString();
            if (x == source.Length)
                return string.Empty;
            else
                return source.Substring(x);
        }

        #region IDisposable Members

        public void Dispose()
        {
            if (Handles.Count > 0)
            {
                Handles.Clear();
            }

            _table.Clear();
            //filename = null;
            //arena = null;
            //name = null;
        }

        #endregion
    }

    /// <summary>
    /// other modules should manipulate config files through ConfigHandles
    /// TODO: I'm thinking of removing this class, it seems like a waste and complicates matters, why not just put the callback in the ConfigFile class
    /// </summary>
    public class ConfigHandle
    {
        internal ConfigFile file;
        internal ConfigChangedDelegate func;
        internal object clos;

        internal ConfigHandle(ConfigFile file, ConfigChangedDelegate func, object clos)
        {
            this.file = file;
            this.func = func;
            this.clos = clos;

            file.Lock();
            file.Handles.AddLast(this);
            file.Unlock();
        }
    }
}
