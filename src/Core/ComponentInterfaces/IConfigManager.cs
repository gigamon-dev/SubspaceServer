using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
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
    /// module and you don't have to open or close it. just use GLOBAL as
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
    public interface IConfigManager : IComponentInterface
    {
        ConfigHandle Global
        {
            get;
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
        ConfigHandle OpenConfigFile(string arena, string name, ConfigChangedDelegate configChanged, object clos);

        /// <summary>
        /// Closes a previously opened file.
        /// Don't forget to call this when you're done with a config file.
        /// </summary>
        /// <param name="ch">the config file to close</param>
        void CloseConfigFile(ConfigHandle ch);

        /// <summary>
        /// Gets a string value from a config file.
        /// </summary>
        /// <param name="ch">the config file to use (ConfigFile.Global for global.conf)</param>
        /// <param name="section">which section of the file the key is in</param>
        /// <param name="key">the name of the key to read</param>
        /// <returns>the value of the key as a string, or NULL if the key isn't present</returns>
        string GetStr(ConfigHandle ch, string section, string key);

        /// <summary>
        /// Gets an integer value from a config file.
        /// </summary>
        /// <param name="ch">the config file to use</param>
        /// <param name="section">which section of the file the key is in</param>
        /// <param name="key">the name of the key to read</param>
        /// <param name="defvalue">the value to be returned if the key isn't found</param>
        /// <returns>the value of the key converted to an integer, or defvalue if it wasn't found. one special conversion is done: if the key has a string value that starts with a letter "y", then 1 is returned instead of 0.</returns>
        int GetInt(ConfigHandle ch, string section, string key, int defvalue);

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
        void SetStr(ConfigHandle ch, string section, string key, string value, string info, bool permanent);

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
        void SetInt(ConfigHandle ch, string section, string key, int value, string info, bool permanent);
    }
}
