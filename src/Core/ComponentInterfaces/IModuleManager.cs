using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public delegate void EnumerateModulesDelegate(Type moduleType, string description);

    public interface IModuleManager : IComponentInterface
    {
        #region Arena Attach/Detach

        /// <summary>
        /// Attaches a module to an arena.
        /// </summary>
        /// <param name="moduleTypeName">Name of the module to attach.</param>
        /// <param name="arena">The arena to attach the module to.</param>
        /// <returns>True on success. False on failure.</returns>
        bool AttachModule(string moduleTypeName, Arena arena);

        /// <summary>
        /// Attaches a module to an arena.
        /// </summary>
        /// <param name="type">The type of the module to attach.</param>
        /// <param name="arena">The arena to attach the module to.</param>
        /// <returns>True on success. False on failure.</returns>
        bool AttachModule(Type type, Arena arena);

        /// <summary>
        /// Detaches a module from an arena.
        /// </summary>
        /// <param name="moduleTypeName">Name of the module to detach.</param>
        /// <param name="arena">The arena to detach the module from.</param>
        /// <returns>True on success. False on failure.</returns>
        bool DetachModule(string moduleTypeName, Arena arena);

        /// <summary>
        /// Detaches a module from an arena.
        /// </summary>
        /// <param name="type">The type of the module to detach.</param>
        /// <param name="arena">The arena to detach the module from.</param>
        /// <returns>True on success. False on failure.</returns>
        bool DetachModule(Type type, Arena arena);

        /// <summary>
        /// Detaches all modules that are attached to an arena.
        /// </summary>
        /// <param name="arena">The arena to detach modules from.</param>
        /// <returns>True on success.  False on failure.</returns>
        bool DetachAllFromArena(Arena arena);

        #endregion

        #region Add Module

        /// <summary>
        /// Adds (registers) a module to be loaded later.
        /// </summary>
        /// <param name="moduleTypeName">Name of the module to add.</param>
        /// <param name="path">Path of the assembly containing the module. NULL for built-in modules.</param>
        /// <returns></returns>
        bool AddModule(string moduleTypeName, string path);

        /// <summary>
        /// Adds (registers) a module to be loaded later.
        /// </summary>
        /// <param name="moduleTypeName">Name of the module to add.</param>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        bool AddModule(string moduleTypeName);

        /// <summary>
        /// Adds (registers) a module to be loaded later.
        /// </summary>
        /// <param name="module">The module to add.</param>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        bool AddModule(IModule module);

        /// <summary>
        /// Adds (registers) a module to be loaded later.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to add.</typeparam>
        /// <param name="module">The module to add.</param>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        bool AddModule<TModule>(TModule module) where TModule : class, IModule;

        /// <summary>
        /// Adds (registers) a module to be loaded later.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to add.</typeparam>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        bool AddModule<TModule>() where TModule : class, IModule;

        #endregion

        #region Load Module

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <param name="moduleTypeName"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        bool LoadModule(string moduleTypeName, string path);

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <param name="moduleTypeName">The type to load in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns>True if the module was added and loaded. Otherwise, false.</returns>
        bool LoadModule(string moduleTypeName);

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <param name="module">The module to add and load.</param>
        /// <returns>True if the module was added and loaded. Otherwise, false.</returns>
        bool LoadModule(IModule module);

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to add and load.</typeparam>
        /// <param name="module">The module to add and load.</param>
        /// <returns>True if the module was added and loaded. Otherwise, false.</returns>
        bool LoadModule<TModule>(TModule module) where TModule : class, IModule;

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to add and load.</typeparam>
        /// <returns>True if the module was added and loaded. Otherwise, false.</returns>
        bool LoadModule<TModule>() where TModule : class, IModule;

        #endregion

        #region Unload Module

        /// <summary>
        /// Unloads a module.
        /// </summary>
        /// <param name="moduleTypeName">The type to unload in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns></returns>
        bool UnloadModule(string moduleTypeName);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        bool UnloadModule(Type type);

        #endregion

        #region Bulk Operations

        /// <summary>
        /// Attempts to load any remaining known modules that have no been loaded yet.
        /// </summary>
        void LoadAllModules();

        /// <summary>
        /// Attempts to unloads all modules.
        /// </summary>
        void UnloadAllModules();

        #endregion

        #region Utility

        /// <summary>
        /// Enumerates over all modules calling the provided delegate for each module.
        /// </summary>
        /// <param name="enumerationCallback"></param>
        /// <param name="arena"></param>
        void EnumerateModules(EnumerateModulesDelegate enumerationCallback, Arena arena);

        /// <summary>
        /// Gets info about modules.
        /// </summary>
        /// <param name="moduleTypeName">The name of the module to get info about.</param>
        /// <returns>
        /// A collection containing info about modules that matched the criteria.
        /// Note: There can be multiple modules with the same name.
        /// <list type="bullet">
        /// <item>There can be multiple copies of the same assembly loaded. In fact, they can be different versions.</item>
        /// <item>Two modules could have used the same namespace and type name. (unlikely)</item>
        /// </list>
        /// The result contains enough information for one to distinguish the difference between each.
        /// Most importantly the <see cref="ModuleManager.ModuleInfo.ModuleTypeName"/> and <see cref="ModuleManager.ModuleInfo.AssemblyPath"/>.
        /// </returns>
        ModuleManager.ModuleInfo[] GetModuleInfo(string moduleTypeName);

        /// <summary>
        /// Gets info about a module.
        /// </summary>
        /// <param name="type">The type of module to get info about.</param>
        /// <returns>Info about the module.</returns>
        ModuleManager.ModuleInfo GetModuleInfo(Type type);

        #endregion
    }
}
