using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Threading;

using SS.Utilities;

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
    public class ConfigManager : IModule
    {
        private readonly Dictionary<string, ConfigFile> _opened = new Dictionary<string, ConfigFile>();
        private readonly LinkedList<ConfigFile> _files = new LinkedList<ConfigFile>();
        private readonly object cfgmtx = new object(); // protects _opened and _files

        private readonly ConfigHandle _global;
        public event EventHandler GlobalConfigChanged;

        public ConfigManager()
        {
            _global = OpenConfigFile(null, null, global_changed, null);
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

        /// <summary>
        /// Open a new config file.
        /// 
        /// This opens a new config file to be managed by the config module.
        /// You should close each file you open with CloseConfigFile. The
        /// filename to open isn't specified directly, but indirectly by
        /// providing an optional arena the file is associated with, and a
        /// filename. These elements are plugged into the config file search
        /// path to find the actual file. For example, if you want to open
        /// the file groupdef.conf in the global conf directory, you'd pass
        /// in NULL for arena and "groupdef.conf" for name. If you wanted the
        /// file "staff.conf" in arenas/foo, you'd pass in "foo" for arena
        /// and "staff.conf" for name. If name is NULL, it looks for
        /// "arena.conf" in the arena directory. If name and arena are both
        /// NULL, it looks for "global.conf" in the global conf directory.
        /// 
        /// The optional callback function can call GetStr or GetInt on this
        /// (or other) config files, but it should not call any other
        /// functions in the config interface.
        /// </summary>
        /// <param name="arena">the name of the arena to use when searching for the file</param>
        /// <param name="name">the name of the desired config file</param>
        /// <param name="configChanged">a callback function that will get called whenever some values in the config file have changed (due to either something in the server calling SetStr or SetInt, or the file changing on disk and the server noticing)</param>
        /// <param name="clos">a closure argument for the callback function</param>
        /// <returns>a ConfigHandle for the new file, or NULL if an error occured</returns>
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

                // create handle while holding cfgmtx to preserve invariant:
                // all open files have at least one handle
                return new ConfigHandle(cf, configChanged, clos);
            }
        }

        /// <summary>
        /// Closes a previously opened file.
        /// Don't forget to call this when you're done with a config file.
        /// </summary>
        /// <param name="ch">the config file to close</param>
        public void CloseConfigFile(ConfigHandle ch)
        {
            if (ch == null)
                return;

            lock (cfgmtx)
            {
                ConfigFile cf = ch.file;
                cf.Lock();

                System.Diagnostics.Debug.Assert(cf.Handles.Remove(ch));

                if (cf.Handles.Count == 0)
                {
                    cf.writeDirtyValuesOne(false);
                    cf.Unlock();
                    _opened.Remove(cf.filename);
                    _files.Remove(cf);
                    cf.Dispose(); //cf.free_file();
                }
                else
                {
                    cf.Unlock();
                }
            }
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

        /// <summary>
        /// Gets a string value from a config file.
        /// </summary>
        /// <param name="ch">the config file to use (ConfigFile.Global for global.conf)</param>
        /// <param name="section">which section of the file the key is in</param>
        /// <param name="key">the name of the key to read</param>
        /// <returns>the value of the key as a string, or NULL if the key isn't present</returns>
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

        /// <summary>
        /// Changes a config file value.
        /// The change will show up immediately in following calls to GetStr
        /// or GetInt, and if permanent is true, it will eventually be
        /// written back to the source file. The writing back might not
        /// happen immediately, though.
        /// </summary>
        /// <param name="ch">the config file to use</param>
        /// <param name="section">which section of the file the key is in</param>
        /// <param name="key">the name of the key to change</param>
        /// <param name="value">the new value of the key</param>
        /// <param name="info">a string describing the circumstances, for logging and auditing purposes. for example, "changed by ... with ?quickfix on may 2 2004"</param>
        /// <param name="permanent">whether this change should be written back to the config file.</param>
        public void SetStr(ConfigHandle ch, string section, string key, string value, string info, bool permanent)
        {
        }

        /// <summary>
        /// Changes a config file value.
        /// Same as SetStr, but the new value is specified as an integer.
        /// </summary>
        /// <see cref="SetStr"/>
        /// <param name="ch"></param>
        /// <param name="section"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="info"></param>
        /// <param name="permanent"></param>
        public void SetInt(ConfigHandle ch, string section, string key, int value, string info, bool permanent)
        {
            SetStr(ch, section, key, value.ToString(), info, permanent);
        }

        /// <summary>
        /// Gets an integer value from a config file.
        /// </summary>
        /// <param name="ch">the config file to use</param>
        /// <param name="section">which section of the file the key is in</param>
        /// <param name="key">the name of the key to read</param>
        /// <param name="defvalue">the value to be returned if the key isn't found</param>
        /// <returns>the value of the key converted to an integer, or defvalue if it wasn't found. one special conversion is done: if the key has a string value that starts with a letter "y", then 1 is returned instead of 0.</returns>
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

        Type[] IModule.ModuleDependencies
        {
            get
            {
                return new Type[] {
                    typeof(Mainloop)
                }; 
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IModule> moduleDependencies)
        {
            // TODO: use mainloop

            return true;
        }

        bool IModule.Unload()
        {
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
