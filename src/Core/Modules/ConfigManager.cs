using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Configuration;
using SS.Utilities.ObjectPool;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that manages configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This differs from ASSS in that changes made via the <see cref="SetStr"/> and <see cref="SetInt"/> methods 
    /// can update or insert into existing conf files, rather than just append to the end of a base conf file.
    /// </para>
    /// <para>
    /// To achieve this, it keeps an in-memory object model representation of each conf file (see <see cref="ConfFile"/>).
    /// Keep in mind that this means, if a setting is changed in a shared <see cref="ConfFile"/> (e.g. settings shared by multiple arenas)
    /// it will affect all 'base' configurations (<see cref="ConfDocument"/> objects) that use the <see cref="ConfFile"/>.
    /// </para>
    /// <para>
    /// Also, ASSS watches only base conf files for changes (not #included files).
    /// Whereas, this watches all used conf files, and reloads dependent base configurations when necessary.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public class ConfigManager : IModule, IModuleLoaderAware, IConfigManager, IConfigLogger
    {
        private ComponentBroker _broker;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IServerTimer _serverTimer;

        private InterfaceRegistrationToken<IConfigManager> _iConfigManagerToken;

        private ConfigHandle _globalConfigHandle;

        /// <summary>
        /// Path --> ConfFile
        /// </summary>
        private readonly Dictionary<string, ConfFile> _files = new();

        /// <summary>
        /// Path --> ConfDocument
        /// </summary>
        private readonly Dictionary<string, DocumentInfo> _documents = new();

        // Lock that synchronizes access.  Many can read at the same time.  Only one can write or modify the collections and objects within them at a given time.
        private readonly ReaderWriterLockSlim _rwLock = new();

        private readonly DefaultObjectPool<List<DocumentInfo>> _documentInfoListPool = new(new ListPooledObjectPolicy<DocumentInfo>() { InitialCapacity = 32 });

        #region Module members

        public bool Load(
            ComponentBroker broker, 
            IMainloop mainloop,
            IServerTimer serverTimer)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _serverTimer = serverTimer ?? throw new ArgumentNullException(nameof(serverTimer));

            _globalConfigHandle = ((IConfigManager)this).OpenConfigFile(null, null, GlobalChanged);
            if (_globalConfigHandle is null)
            {
                Log(LogLevel.Error, "Failed to open global.conf.");
                return false;
            }

            SetTimers();

            _iConfigManagerToken = broker.RegisterInterface<IConfigManager>(this);

            return true;

            void GlobalChanged()
            {
                // fire the callback on the mainloop thread
                _mainloop.QueueMainWorkItem<object>(
                    _ => { GlobalConfigChangedCallback.Fire(_broker); },
                    null);
            }
        }

        bool IModuleLoaderAware.PostLoad(ComponentBroker broker)
        {
            _logManager = broker.GetInterface<ILogManager>();
            return true;
        }

        bool IModuleLoaderAware.PreUnload(ComponentBroker broker)
        {
            if (_logManager is not null)
                broker.ReleaseInterface(ref _logManager);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iConfigManagerToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region Timers

        [ConfigHelp("Config", "FlushDirtyValuesInterval", ConfigScope.Global,  typeof(int), DefaultValue = "500", 
            Description = "How often to write modified config settings back to disk (in ticks).")]
        [ConfigHelp("Config", "CheckModifiedFilesInterval", ConfigScope.Global, typeof(int), DefaultValue = "1500",
            Description = "How often to check for modified config files on disk (in ticks).")]
        private void SetTimers()
        {
            int dirty = ((IConfigManager)this).GetInt(_globalConfigHandle, "Config", "FlushDirtyValuesInterval", 500);
            _serverTimer.ClearTimer(ServerTimer_SaveChanges, null);
            _serverTimer.SetTimer(ServerTimer_SaveChanges, 700, dirty * 10, null);

            int files = ((IConfigManager)this).GetInt(_globalConfigHandle, "Config", "CheckModifiedFilesInterval", 1500);
            _serverTimer.ClearTimer(ServerTimer_ReloadModified, null);
            _serverTimer.SetTimer(ServerTimer_ReloadModified, 1500, files * 10, null);
        }

        private bool ServerTimer_ReloadModified()
        {
            List<DocumentInfo> notifyList = null;

            try
            {
                _rwLock.EnterUpgradeableReadLock();

                try
                {
                    // check if any document needs to be reloaded
                    // or any file has been modified on disk and needs to be reloaded
                    // note: checking files second since it requires I/O
                    if (HasWork())
                    {
                        _rwLock.EnterWriteLock();

                        try
                        {
                            // reload files that have been modified on disk
                            // note: this is done first, because it affects documents
                            // (a document that consists of a file that was reloaded will need to be reloaded afterwards)
                            foreach (ConfFile file in _files.Values)
                            {
                                if (file.IsReloadNeeded)
                                {
                                    Log(LogLevel.Info, $"Reloading conf file '{file.Path}' from disk.");

                                    try
                                    {
                                        file.Load();
                                    }
                                    catch (Exception ex)
                                    {
                                        Log(LogLevel.Warn, $"Failed to reload conf file '{file.Path}'. {ex.Message}");
                                    }
                                }
                            }

                            // reload each document that needs to be reloaded
                            // note: a document may need to be reloaded if any of the files it consists of was reloaded
                            // or a file shared by multiple documents was updated by 1 of the documents (the other documents will need reloading)
                            foreach (DocumentInfo docInfo in _documents.Values)
                            {
                                if (docInfo.Document.IsReloadNeeded)
                                {
                                    Log(LogLevel.Info, $"Reloading settings for base conf '{docInfo.Path}'.");
                                    docInfo.Document.Load();
                                    docInfo.IsChangeNotificationPending = true;
                                }

                                if (docInfo.IsChangeNotificationPending)
                                {
                                    docInfo.IsChangeNotificationPending = false;
                                    notifyList ??= _documentInfoListPool.Get();
                                    notifyList.Add(docInfo);
                                }
                            }

                            // TODO: remove documents that are no longer referenced by a handle

                            // TODO: remove files that are no longer referenced by a document
                        }
                        finally
                        {
                            _rwLock.ExitWriteLock();
                        }
                    }
                }
                finally
                {
                    _rwLock.ExitUpgradeableReadLock();
                }
            }
            finally
            {
                if (notifyList is not null)
                {
                    // Fire pending document change notifications (outside of reader/writer lock).
                    foreach (DocumentInfo docInfo in notifyList)
                    {
                        _mainloop.QueueMainWorkItem(MainloopWork_NotifyChanged, docInfo);
                    }

                    _documentInfoListPool.Return(notifyList);
                }
            }

            return true;


            bool HasWork()
            {
                foreach (DocumentInfo documentInfo in _documents.Values)
                    if (documentInfo.Document.IsReloadNeeded || documentInfo.IsChangeNotificationPending)
                        return true;

                foreach (ConfFile file in _files.Values)
                    if (file.IsReloadNeeded)
                        return true;

                return false;
            }

            static void MainloopWork_NotifyChanged(DocumentInfo docInfo)
            {
                docInfo.NotifyChanged();
            }
        }

        private bool ServerTimer_SaveChanges()
        {
            _rwLock.EnterUpgradeableReadLock();

            try
            {
                if (IsAnyFileDirty())
                {
                    _rwLock.EnterWriteLock();

                    try
                    {
                        foreach (ConfFile file in _files.Values)
                        {
                            if (file.IsDirty)
                            {
                                Log(LogLevel.Info, $"Saving changes to conf file '{file.Path}'.");

                                try
                                {
                                    // save the file
                                    // Note: Also updates file.LastModified so that it doesn't appear as being modified to us and get reloaded
                                    file.Save();
                                }
                                catch (Exception ex)
                                {
                                    Log(LogLevel.Warn, $"Failed to save changes to conf file '{file.Path}'. {ex.Message}");
                                }
                            }
                        }
                    }
                    finally
                    {
                        _rwLock.ExitWriteLock();
                    }
                }
            }
            finally
            {
                _rwLock.ExitUpgradeableReadLock();
            }

            return true;


            bool IsAnyFileDirty()
            {
                foreach (ConfFile file in _files.Values)
                    if (file.IsDirty)
                        return true;

                return false;
            }
        }

        #endregion

        #region IConfigManager

        ConfigHandle IConfigManager.Global => _globalConfigHandle;

        ConfigHandle IConfigManager.OpenConfigFile(string arena, string name)
        {
            return OpenConfigFile(
                arena, 
                name, 
                (documentInfo) => documentInfo.CreateHandle(
                    arena is not null ? ConfigScope.Arena : ConfigScope.Global, 
                    name));
        }

        ConfigHandle IConfigManager.OpenConfigFile(string arena, string name, ConfigChangedDelegate changedCallback)
        {
            return OpenConfigFile(
                arena,
                name,
                (documentInfo) => documentInfo.CreateHandle(
                    arena is not null ? ConfigScope.Arena : ConfigScope.Global,
                    name,
                    changedCallback));
        }

        ConfigHandle IConfigManager.OpenConfigFile<TState>(string arena, string name, ConfigChangedDelegate<TState> changedCallback, TState state)
        {
            return OpenConfigFile(
                arena, 
                name, 
                (documentInfo) => documentInfo.CreateHandle(
                    arena is not null ? ConfigScope.Arena : ConfigScope.Global,
                    name,
                    changedCallback,
                    state));
        }

        void IConfigManager.CloseConfigFile(ConfigHandle handle)
        {
            if (handle is null)
                throw new ArgumentNullException(nameof(handle));

            if (handle is not DocumentHandle documentHandle)
                throw new ArgumentException("Only handles created by this module are valid.", nameof(handle));

            _rwLock.EnterWriteLock();

            try
            {
                documentHandle.DocumentInfo?.CloseHandle(documentHandle);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        string IConfigManager.GetStr(ConfigHandle handle, ReadOnlySpan<char> section, ReadOnlySpan<char> key)
        {
            if (handle is null)
                throw new ArgumentNullException(nameof(handle));

            if (handle is not DocumentHandle documentHandle)
                throw new ArgumentException("Only handles created by this module are valid.", nameof(handle));

            if (documentHandle.DocumentInfo is not null)
            {
                _rwLock.EnterReadLock();

                try
                {
                    if (documentHandle.DocumentInfo is not null)
                    {
                        if (documentHandle.DocumentInfo.Document.TryGetValue(section, key, out string value))
                        {
                            return value;
                        }
                        else
                        {
                            return null;
                        }
                    }
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }
            }

            throw new InvalidOperationException("Handle is closed.");
        }

        int IConfigManager.GetInt(ConfigHandle handle, ReadOnlySpan<char> section, ReadOnlySpan<char> key, int defaultValue)
        {
            if (handle is not DocumentHandle documentHandle)
                throw new ArgumentException("Only handles created by this module are valid.", nameof(handle));

            string value = ((IConfigManager)this).GetStr(handle, section, key);

            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;

            if (int.TryParse(value, out int result))
                return result;

            ReadOnlySpan<char> valueSpan = value.AsSpan().Trim();
            if (valueSpan.Equals("Y", StringComparison.OrdinalIgnoreCase)
                || valueSpan.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                || valueSpan.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            else if (valueSpan.Equals("N", StringComparison.OrdinalIgnoreCase)
                || valueSpan.Equals("No", StringComparison.OrdinalIgnoreCase)
                || valueSpan.Equals(bool.FalseString, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            _logManager.LogM(LogLevel.Drivel, nameof(ConfigManager), $"Failed to parse {section}:{key} as an integer, using the provided default value ({defaultValue}).");

            return defaultValue; // Note: This differs from ASSS which returns 0.
        }

        T IConfigManager.GetEnum<T>(ConfigHandle handle, ReadOnlySpan<char> section, ReadOnlySpan<char> key, T defaultValue)
        {
            string value = ((IConfigManager)this).GetStr(handle, section, key);

            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;

            return Enum.TryParse(value, true, out T enumValue)
                ? enumValue
                : defaultValue;
        }

        void IConfigManager.SetStr(ConfigHandle handle, string section, string key, string value, string comment, bool permanent, ModifyOptions options)
        {
            if (handle is null)
                throw new ArgumentNullException(nameof(handle));

            if (handle is not DocumentHandle documentHandle)
                throw new ArgumentException("Only handles created by this module are valid.", nameof(handle));    

            if (documentHandle.DocumentInfo is not null)
            {
                _rwLock.EnterWriteLock();

                try
                {
                    if (documentHandle.DocumentInfo is not null)
                    {
                        documentHandle.DocumentInfo.Document.UpdateOrAddProperty(section, key, value, permanent, comment, options);

                        // set a flag so that we remember to fire change notifications (done in a timer)
                        documentHandle.DocumentInfo.IsChangeNotificationPending = true;

                        return;
                    }
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }

            throw new InvalidOperationException("Handle is closed.");
        }

        void IConfigManager.SetInt(ConfigHandle handle, string section, string key, int value, string comment, bool permanent, ModifyOptions options)
        {
            ((IConfigManager)this).SetStr(handle, section, key, value.ToString("D", CultureInfo.InvariantCulture), comment, permanent, options);
        }

        void IConfigManager.SetEnum<T>(ConfigHandle handle, string section, string key, T value, string comment, bool permanent, ModifyOptions options)
        {
            ((IConfigManager)this).SetStr(handle, section, key, value.ToString("G"), comment, permanent, options);
        }

        void IConfigManager.SaveStandaloneCopy(ConfigHandle handle, string filePath)
        {
            if (handle is null)
                throw new ArgumentNullException(nameof(handle));

            if (handle is not DocumentHandle documentHandle)
                throw new ArgumentException("Only handles created by this module are valid.", nameof(handle));

            _rwLock.EnterWriteLock();

            try
            {
                if (documentHandle.DocumentInfo is null)
                    throw new InvalidOperationException("Handle is closed.");

                documentHandle.DocumentInfo.Document.SaveAsStandaloneConf(filePath);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        #endregion

        #region IConfigLogger

        void IConfigLogger.Log(LogLevel level, string message)
        {
            Log(level, message);
        }

        #endregion

        private void Log(LogLevel level, string message)
        {
            if (_logManager is not null)
            {
                _logManager.LogM(level, nameof(ConfigManager), message);
            }
            else
            {
                Console.Error.WriteLine($"{(LogCode)level} <{nameof(ConfigManager)}> {message}");
            }
        }

        // This is called by the ConfigFileProvider which will only be used when a write lock is already held.
        private ConfFile GetConfFile(string arena, string name)
        {
            if (!_rwLock.IsWriteLockHeld)
                return null;

            // determine the path of the file
            string path = LocateConfigFile(arena, name);
            if (path is null)
            {
                Log(LogLevel.Warn, $"File not found for arena '{arena}', name '{name}'.");
                return null;
            }

            // check if we already have it loaded
            if (_files.TryGetValue(path, out ConfFile file))
            {
                return file;
            }

            // not loaded yet, let's do it
            try
            {
                file = new ConfFile(path, this);
                file.Load();
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warn, $"Failed to load '{path}' for arena '{arena}', name '{name}'. {ex.Message}");
                return null;
            }

            _files.Add(path, file);
            return file;
        }

        private ConfigHandle OpenConfigFile(string arena, string name, Func<DocumentInfo, ConfigHandle> createHandle)
        {
            if (createHandle is null)
                throw new ArgumentNullException(nameof(createHandle));

            string path = LocateConfigFile(arena, name);
            if (string.IsNullOrWhiteSpace(path))
            {
                Log(LogLevel.Warn, $"File not found in search paths (arena='{arena}', name='{name}').");
                return null;
            }

            _rwLock.EnterWriteLock();

            try
            {
                if (!_documents.TryGetValue(path, out DocumentInfo documentInfo))
                {
                    ConfFileProvider fileProvider = new(this, arena);
                    ConfDocument document = new(name, fileProvider, this);
                    document.Load();

                    documentInfo = new DocumentInfo(path, document);
                    _documents.Add(path, documentInfo);
                }

                return createHandle(documentInfo);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        private static string LocateConfigFile(string arena, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                name = string.IsNullOrWhiteSpace(arena) ? "global.conf" : "arena.conf";
            }

            return PathUtil.FindFileOnPath(Constants.ConfigSearchPaths, name, arena);
        }

        #region Helper types

        /// <summary>
        /// Helper class that also keeps track of additional context, an optional arena,
        /// for which files are to be retrieved.
        /// </summary>
        private class ConfFileProvider : IConfFileProvider
        {
            private readonly ConfigManager _manager;
            private readonly string _arena;

            public ConfFileProvider(ConfigManager manager, string arena)
            {
                _manager = manager ?? throw new ArgumentNullException(nameof(manager));
                _arena = arena;
            }

            public ConfFile GetFile(string name) => _manager.GetConfFile(_arena, name);
        }

        private interface IConfigChangedInvoker
        {
            void Invoke();
        }

        private class ConfigChangedInvoker : IConfigChangedInvoker
        {
            private readonly ConfigChangedDelegate _callback;

            public ConfigChangedInvoker(ConfigChangedDelegate callback)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            }

            public void Invoke()
            {
                _callback();
            }
        }

        private class ConfigChangedInvoker<T> : IConfigChangedInvoker
        {
            private readonly ConfigChangedDelegate<T> _callback;
            private readonly T _state;

            public ConfigChangedInvoker(ConfigChangedDelegate<T> callback, T state)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                _state = state;
            }

            public void Invoke()
            {
                _callback?.Invoke(_state);
            }
        }

        private class DocumentHandle : ConfigHandle
        {
            private readonly IConfigChangedInvoker _invoker;

            public DocumentHandle(
                DocumentInfo documentInfo,
                ConfigScope scope,
                string filename,
                IConfigChangedInvoker invoker)
            {
                DocumentInfo = documentInfo ?? throw new ArgumentNullException(nameof(documentInfo));
                Scope = scope;
                FileName = filename;
                _invoker = invoker; // can be null
            }

            public DocumentInfo DocumentInfo { get; internal set; }

            public ConfigScope Scope { get; private set; }

            public string FileName { get; private set; }

            public void NotifyConfigChanged()
            {
                _invoker?.Invoke();
            }
        }

        private class DocumentInfo
        {
            private readonly LinkedList<DocumentHandle> _handles = new();
            private readonly object _lockObj = new();

            public DocumentInfo(string path, ConfDocument document)
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("A path is required.", nameof(path));

                Path = path;
                Document = document ?? throw new ArgumentNullException(nameof(document));
            }

            public string Path { get; }

            public ConfDocument Document { get; }

            private bool _isChangeNotificationPending = false;
            public bool IsChangeNotificationPending
            {
                get
                {
                    lock (_lockObj)
                    {
                        return _isChangeNotificationPending;
                    }
                }

                set
                {
                    lock (_lockObj)
                    {
                        _isChangeNotificationPending = value;
                    }
                }
            }

            public DocumentHandle CreateHandle(ConfigScope scope, string fileName)
            {
                DocumentHandle handle = new(this, scope, fileName,  null);

                lock (_lockObj)
                {
                    _handles.AddLast(handle);
                }

                return handle;
            }

            public DocumentHandle CreateHandle(ConfigScope scope, string fileName, ConfigChangedDelegate callback)
            {
                DocumentHandle handle = new(
                    this,
                    scope,
                    fileName,
                    callback is not null ? new ConfigChangedInvoker(callback) : null);

                lock (_lockObj)
                {
                    _handles.AddLast(handle);
                }

                return handle;
            }

            public DocumentHandle CreateHandle<TState>(ConfigScope scope, string fileName, ConfigChangedDelegate<TState> callback, TState state)
            {
                DocumentHandle handle = new(
                    this,
                    scope,
                    fileName,
                    callback is not null ? new ConfigChangedInvoker<TState>(callback, state) : null);

                lock (_lockObj)
                {
                    _handles.AddLast(handle);
                }

                return handle;
            }

            public bool CloseHandle(DocumentHandle handle)
            {
                if (handle is null)
                    throw new ArgumentNullException(nameof(handle));

                if (handle.DocumentInfo != this)
                    return false;

                handle.DocumentInfo = null;

                lock (_lockObj)
                {
                    return _handles.Remove(handle);
                }
            }

            public void NotifyChanged()
            {
                lock (_lockObj)
                {
                    foreach (DocumentHandle handle in _handles)
                    {
                        handle.NotifyConfigChanged();
                    }
                }
            }
        }

        #endregion
    }
}
