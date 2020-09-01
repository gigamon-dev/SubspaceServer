using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public delegate void EnumerateModulesDelegate(Type moduleType, string description);

    public interface IModuleManager : IComponentInterface
    {
        /// <summary>
        /// Attaches a module to an arena.
        /// </summary>
        /// <param name="assemblyQualifiedName">Name of the module to attach.</param>
        /// <param name="arena">The arena to attach the module to.</param>
        /// <returns>True on success. False on failure.</returns>
        bool AttachModule(string assemblyQualifiedName, Arena arena);

        /// <summary>
        /// Detaches a module from an arena.
        /// </summary>
        /// <param name="assemblyQualifiedName">Name of the module to detach.</param>
        /// <param name="arena">The arena to detach the module from.</param>
        /// <returns>True on success. False on failure.</returns>
        bool DetachModule(string assemblyQualifiedName, Arena arena);

        /// <summary>
        /// Detaches all modules that are attached to an arena.
        /// </summary>
        /// <param name="arena">The arena to detach modules from.</param>
        /// <returns>True on success.  False on failure.</returns>
        bool DetachAllFromArena(Arena arena);

        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <param name="assemblyQualifiedName">The type to load in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        bool AddModule(string assemblyQualifiedName);

        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <param name="module">The module to add.</param>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        bool AddModule(IModule module);

        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to add.</typeparam>
        /// <param name="module">The module to add.</param>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        bool AddModule<TModule>(TModule module) where TModule : class, IModule;

        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to add.</typeparam>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        bool AddModule<TModule>() where TModule : class, IModule;

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <param name="assemblyQualifiedName">The type to load in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns>True if the module was added and loaded. Otherwise, false.</returns>
        bool LoadModule(string assemblyQualifiedName);

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

        /// <summary>
        /// Unloads a module.
        /// </summary>
        /// <param name="assemblyQualifiedName">The type to unload in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns></returns>
        bool UnloadModule(string assemblyQualifiedName);

        /// <summary>
        /// Attempts to load any remaining known modules that have no been loaded yet.
        /// </summary>
        void LoadAllModules();

        /// <summary>
        /// Attempts to unloads all modules.
        /// </summary>
        void UnloadAllModules();

        /// <summary>
        /// Enumerates over all modules calling the provided delegate for each module.
        /// </summary>
        /// <param name="enumerationCallback"></param>
        /// <param name="arena"></param>
        void EnumerateModules(EnumerateModulesDelegate enumerationCallback, Arena arena);

        /// <summary>
        /// Gets info about the module.
        /// </summary>
        /// <param name="assemblyQualifiedName">The name of the module to get info about.</param>
        /// <returns>Info about the module. NULL if no info or not found.</returns>
        string GetModuleInfo(string assemblyQualifiedName);
    }
}
