using SS.Core.Configuration;
using System;
using System.Threading.Tasks;

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
    /// module and you don't have to open or close it. just use <see cref="Global"/> as
    /// your ConfigHandle. arena configuration files are also maintained for
    /// you as <see cref="Arena.Cfg"/>. so typically you will only need to call <see cref="GetStr"/> and
    /// <see cref="GetInt"/>.
    ///
    /// there can also be secondary global or arena config files, specified
    /// with the second parameter of OpenConfigFile. these are used for staff
    /// lists and a few other special things. in general, you should use a
    /// new section in the global or arena config files rather than using a
    /// different file.
    ///
    /// setting configuration values is relatively straightforward. the info
    /// parameter to <see cref="SetStr"/> and <see cref="SetInt"/> should describe who initiated the
    /// change and when. this information may be written back to the
    /// configuration files.
    /// </summary>
    public interface IConfigManager : IComponentInterface
    {
        /// <summary>
        /// Handle to the main global configuration file.
        /// </summary>
        ConfigHandle Global
        {
            get;
        }

        /// <summary>
        /// Opens a config file.
        /// </summary>
        /// <remarks>
        /// This opens a config file to be managed by the config module.
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
        /// </remarks>
        /// <param name="arena">
        /// The name of the arena to use when searching for the file.
        /// <see langword="null"/> to open a file in the global conf directory.
        /// </param>
        /// <param name="name">
        /// The name of the desired config file. 
        /// <see langword="null"/> to open the default file ("arena.conf" if an <paramref name="arena"/> is specified, or "global.conf" if no arena is specified).
        /// </param>
        /// <returns>A handle for the file, or <see langword="null"/> if an error occured.</returns>
        Task<ConfigHandle?> OpenConfigFile(string? arena, string? name);

        /// <summary>
        /// Opens a config file, with a callback to receive change notifications.
        /// </summary>
        /// <remarks>
        /// This opens a config file to be managed by the config module.
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
        /// </remarks>
        /// <param name="arena">
        /// The name of the arena to use when searching for the file.
        /// <see langword="null"/> to open a file in the global conf directory.
        /// </param>
        /// <param name="name">
        /// The name of the desired config file. 
        /// <see langword="null"/> to open the default file ("arena.conf" if an <paramref name="arena"/> is specified, or "global.conf" if no arena is specified).
        /// </param>
        /// <param name="changedCallback">
        /// A callback that will get called whenever a config file is changed
        /// (due to either a call to <see cref="SetStr"/> or <see cref="SetInt"/>, or the file changing on disk and the server noticing).
        /// <para>The callback function can call <see cref="GetStr"/> or <see cref="GetInt"/> on this (or other) config files, but it should not call any other functions in the config interface.
        /// </para>
        /// </param>
        /// <returns>A handle for the file, or <see langword="null"/> if an error occured.</returns>
        Task<ConfigHandle?> OpenConfigFile(string? arena, string? name, ConfigChangedDelegate changedCallback);

        /// <summary>
        /// Opens a config file, with a callback to receive change notifications.
        /// </summary>
        /// <remarks>
        /// This opens a config file to be managed by the config module.
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
        /// </remarks>
        /// <param name="arena">
        /// The name of the arena to use when searching for the file.
        /// <see langword="null"/> to open a file in the global conf directory.
        /// </param>
        /// <param name="name">
        /// The name of the desired config file. 
        /// <see langword="null"/> to open the default file ("arena.conf" if an <paramref name="arena"/> is specified, or "global.conf" if no arena is specified).
        /// </param>
        /// <param name="changedCallback">
        /// A callback that will get called whenever a config file is changed
        /// (due to either a call to <see cref="SetStr"/> or <see cref="SetInt"/>, or the file changing on disk and the server noticing).
        /// <para>The callback function can call <see cref="GetStr"/> or <see cref="GetInt"/> on this (or other) config files, but it should not call any other functions in the config interface.
        /// </para>
        /// </param>
        /// <param name="state">An object to be passed when <paramref name="changedCallback"/> is called.</param>
        /// <returns>A handle for the file, or <see langword="null"/> if an error occured.</returns>
        Task<ConfigHandle?> OpenConfigFile<TState>(string? arena, string? name, ConfigChangedDelegate<TState> changedCallback, TState state);

        /// <summary>
        /// Closes a previously opened file.
        /// Don't forget to call this when you're done with a config file.
        /// </summary>
        /// <param name="handle">the config file to close</param>
        void CloseConfigFile(ConfigHandle handle);

        /// <summary>
        /// Gets the value of a <see langword="string"/> setting from a config file.
        /// </summary>
        /// <param name="handle">Handle to the config file to read from. Use <see cref="Global"/> for the "/conf/global.conf" file. Use <see cref="Arena.Cfg"/> for an arena's arena.conf file.</param>
        /// <param name="section">The section to read.</param>
        /// <param name="key">The key to read.</param>
        /// <returns>The value of the setting, or <see langword="null"/> if not found.</returns>
        string? GetStr(ConfigHandle handle, ReadOnlySpan<char> section, ReadOnlySpan<char> key);

        /// <summary>
        /// Gets the value of an <see langword="int"/> setting from a config file.
        /// </summary>
        /// <param name="handle"><inheritdoc cref="GetStr" path="/param[@name='handle']"/></param>
        /// <param name="section"><inheritdoc cref="GetStr" path="/param[@name='section']"/></param>
        /// <param name="key"><inheritdoc cref="GetStr" path="/param[@name='key']"/></param>
        /// <param name="defaultValue">The value to return if the setting is not found or cannot be converted to an integer.</param>
        /// <returns>
        /// The value of the setting converted to an integer, or the <paramref name="defaultValue"/> if not found or unable to convert to integer.
        /// This additional handles conversion of "Y"/"N", "Yes"/"No", and "True"/"False" to 1 and 0 respectively.
        /// </returns>
        int GetInt(ConfigHandle handle, ReadOnlySpan<char> section, ReadOnlySpan<char> key, int defaultValue);

        /// <summary>
        /// Geta the value of a <see langword="bool"/> setting from a config file.
        /// </summary>
        /// <param name="handle"><inheritdoc cref="GetStr" path="/param[@name='handle']"/></param>
        /// <param name="section"><inheritdoc cref="GetStr" path="/param[@name='section']"/></param>
        /// <param name="key"><inheritdoc cref="GetStr" path="/param[@name='key']"/></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        bool GetBool(ConfigHandle handle, ReadOnlySpan<char> section, ReadOnlySpan<char> key, bool defaultValue);

        /// <summary>
        /// Gets the value of an <see langword="enum"/> setting from a config file.
        /// </summary>
        /// <typeparam name="T">The type of <see langword="enum"/>.</typeparam>
        /// <param name="handle"><inheritdoc cref="GetStr" path="/param[@name='handle']"/></param>
        /// <param name="section"><inheritdoc cref="GetStr" path="/param[@name='section']"/></param>
        /// <param name="key"><inheritdoc cref="GetStr" path="/param[@name='key']"/></param>
        /// <param name="defaultValue">The value to return if the setting is not found or cannot be converted to <typeparamref name="T"/>.</param>
        /// <returns>
        /// The value of the setting converted to a <typeparamref name="T"/>, or the <paramref name="defaultValue"/> if not found or unable to convert to <typeparamref name="T"/>.
        /// If the enum has <see cref="FlagsAttribute"/>, the value can be a combination. Otherwise, it will be a defined value of <typeparamref name="T"/>.
        /// </returns>
        T GetEnum<T>(ConfigHandle handle, ReadOnlySpan<char> section, ReadOnlySpan<char> key, T defaultValue) where T : struct, Enum;

        /// <summary>
        /// Changes a config file value.
        /// The change will show up immediately in following calls to GetStr
        /// or GetInt, and if permanent is true, it will eventually be
        /// written back to the source file. The writing back might not
        /// happen immediately, though.
        /// </summary>
        /// <param name="handle">the config file to use</param>
        /// <param name="section">which section of the file the key is in</param>
        /// <param name="key">the name of the key to change</param>
        /// <param name="value">the new value of the key</param>
        /// <param name="comment">A string describing the circumstances, for logging and auditing purposes. for example, "changed by ... with ?quickfix on may 2 2004".</param>
        /// <param name="permanent">Whether this change should be written back to the config file.</param>
        /// <param name="options">Options that affect how the setting is saved.</param>
        void SetStr(ConfigHandle handle, string section, string key, string value, string? comment, bool permanent, ModifyOptions options = ModifyOptions.None);

        /// <summary>
        /// Changes a config file value.
        /// The same as <see cref="SetStr"/>, but as an integer.
        /// </summary>
        /// <param name="handle">the config file to use</param>
        /// <param name="section">which section of the file the key is in</param>
        /// <param name="key">the name of the key to change</param>
        /// <param name="value">the new value of the key</param>
        /// <param name="comment">A string describing the circumstances, for logging and auditing purposes. for example, "changed by ... with ?quickfix on may 2 2004".</param>
        /// <param name="permanent">Whether this change should be written back to the config file.</param>
        /// <param name="options">Options that affect how the setting is saved.</param>
        void SetInt(ConfigHandle handle, string section, string key, int value, string? comment, bool permanent, ModifyOptions options = ModifyOptions.None);

        /// <summary>
        /// Changes a config file value.
        /// The same as <see cref="SetStr"/>, but as an <see cref="Enum"/>.
        /// </summary>
        /// <typeparam name="T">The type of Enum.</typeparam>
        /// <param name="handle">Handle to the config.</param>
        /// <param name="section">The section to write to.</param>
        /// <param name="key">They key to write.</param>
        /// <param name="value">The value to write.</param>
        /// <param name="comment">A string describing the circumstances, for logging and auditing purposes. for example, "changed by ... with ?quickfix on may 2 2004".</param>
        /// <param name="permanent">Whether this change should be written back to the config file.</param>
        /// <param name="options">Options that affect how the setting is saved.</param>
        void SetEnum<T>(ConfigHandle handle, string section, string key, T value, string comment, bool permanent, ModifyOptions options = ModifyOptions.None) where T : struct, Enum;

        /// <summary>
        /// Creates a standalone copy of a config file.
        /// </summary>
        /// <remarks>
        /// The resulting file is flattened, such that there are no #include or other preprocessor directives.
        /// </remarks>
        /// <param name="handle">Handle to the config to copy.</param>
        /// <param name="filePath">The complete path and filename to save the copy to.</param>
        /// <returns><see langword="true"/> if the file was sucessfully saved; otherwise, <see langword="false"/>.</returns>
        /// <exception cref="Exception">Error creating file.</exception>
        Task<bool> SaveStandaloneCopy(ConfigHandle handle, string filePath);
    }
}
