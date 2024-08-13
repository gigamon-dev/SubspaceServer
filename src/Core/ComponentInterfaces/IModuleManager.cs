using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that manages server modules.
    /// </summary>
    public interface IModuleManager : IComponentInterface
    {
        #region Arena Attach/Detach

        /// <summary>
        /// Attaches a module to an arena.
        /// </summary>
        /// <param name="moduleTypeName">
        /// The <see cref="Type"/> name of module. 
        /// It can be the <see cref="Type.FullName"/> or for built-in modules, the <see cref="Type.AssemblyQualifiedName"/>.
        /// </param>
        /// <param name="arena">The arena to attach the module to.</param>
        /// <returns>True on success. False on failure.</returns>
        Task<bool> AttachModuleAsync(string moduleTypeName, Arena arena);

        /// <summary>
        /// Attaches a module to an arena.
        /// </summary>
        /// <param name="type">The type of the module to attach.</param>
        /// <param name="arena">The arena to attach the module to.</param>
        /// <returns>True on success. False on failure.</returns>
        Task<bool> AttachModuleAsync(Type type, Arena arena);

        /// <summary>
        /// Detaches a module from an arena.
        /// </summary>
        /// <param name="moduleTypeName">
        /// The <see cref="Type"/> name of module. 
        /// It can be the <see cref="Type.FullName"/> or for built-in modules, the <see cref="Type.AssemblyQualifiedName"/>.
        /// </param>
        /// <param name="arena">The arena to detach the module from.</param>
        /// <returns>True on success. False on failure.</returns>
        Task<bool> DetachModuleAsync(string moduleTypeName, Arena arena);

        /// <summary>
        /// Detaches a module from an arena.
        /// </summary>
        /// <param name="type">The type of the module to detach.</param>
        /// <param name="arena">The arena to detach the module from.</param>
        /// <returns>True on success. False on failure.</returns>
        Task<bool> DetachModuleAsync(Type type, Arena arena);

        /// <summary>
        /// Detaches all modules that are attached to an arena.
        /// </summary>
        /// <param name="arena">The arena to detach modules from.</param>
        /// <returns>True on success.  False on failure.</returns>
        Task<bool> DetachAllFromArenaAsync(Arena arena);

        #endregion

        #region Load

        /// <summary>
        /// Locates a module type by name, creates an instance of it, and loads it.
        /// </summary>
        /// <remarks>
        /// This method does NOT support loading modules from plug-in assemblies. 
        /// For that, use <see cref="LoadModuleAsync(string, string?)"/> with a path instead.
        /// </remarks>
        /// <param name="moduleTypeName">
        /// The <see cref="Type"/> name of module. 
        /// It can be the <see cref="Type.FullName"/> or the <see cref="Type.AssemblyQualifiedName"/>.
        /// </param>
        /// <returns><see langword="true"/> if the module was loaded; otherwise, <see langword="false"/>.</returns>
        Task<bool> LoadModuleAsync(string moduleTypeName);

        /// <summary>
        /// Locates a module type by name and path, creates an instance of it, and loads it.
        /// </summary>
        /// <remarks>
        /// This method supports loading modules from plug-in assemblies.
        /// </remarks>
        /// <param name="moduleTypeName">
        /// The <see cref="Type"/> name of module. 
        /// It can be the <see cref="Type.FullName"/>, or for built-in modules the <see cref="Type.AssemblyQualifiedName"/>.
        /// </param>
        /// <param name="path">The path of the assembly that contains the module. <see langword="null"/> for built-in modules.</param>
        /// <returns><see langword="true"/> if the module was loaded; otherwise, <see langword="false"/>.</returns>
        Task<bool> LoadModuleAsync(string moduleTypeName, string? path);

        /// <summary>
        /// Creates an instance of a module and loads it.
        /// </summary>
        /// <typeparam name="TModule">The type of the module to instantiate and load.</typeparam>
        /// <returns><see langword="true"/> if the module was loaded; otherwise, <see langword="false"/>.</returns>
        Task<bool> LoadModuleAsync<TModule>() where TModule : class;

        /// <summary>
        /// Creates an instance of a module and loads it.
        /// </summary>
        /// <param name="moduleType">The type of the module to instantiate and load.</param>
        /// <returns><see langword="true"/> if the module was loaded; otherwise, <see langword="false"/>.</returns>
        Task<bool> LoadModuleAsync(Type moduleType);

        /// <summary>
        /// Loads a module that's already been constructed.
        /// </summary>
        /// <param name="module">The module instance to load.</param>
        /// <returns><see langword="true"/> if the module was loaded; otherwise, <see langword="false"/>.</returns>
        Task<bool> LoadModuleAsync(IModule module);

        /// <summary>
        /// Loads a module that's already been constructed.
        /// </summary>
        /// <param name="module">The module instance to load.</param>
        /// <returns><see langword="true"/> if the module was loaded; otherwise, <see langword="false"/>.</returns>
        Task<bool> LoadModuleAsync(IAsyncModule module);

        /// <summary>
        /// Loads a module that's already been constructed.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to load.</typeparam>
        /// <param name="module">The module instance to load.</param>
        /// <returns><see langword="true"/> if the module was loaded; otherwise, <see langword="false"/>.</returns>
        Task<bool> LoadModuleAsync<TModule>(TModule module) where TModule : class;

        #endregion

        #region Unload

        /// <summary>
        /// Unloads a module.
        /// </summary>
        /// <param name="moduleTypeName">
        /// The <see cref="Type"/> name of module. 
        /// It can be the <see cref="Type.FullName"/> or for built-in modules, the <see cref="Type.AssemblyQualifiedName"/>.
        /// </param>
        /// <returns></returns>
        Task<int> UnloadModuleAsync(string moduleTypeName);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        Task<bool> UnloadModuleAsync(Type type);

        /// <summary>
        /// Attempts to unloads all modules.
        /// </summary>
        Task UnloadAllModulesAsync();

        #endregion

        #region Utility

        /// <summary>
        /// Gets the types and descriptions of the loaded modules.
        /// </summary>
        /// <param name="arena">The arena to filter by attached modules, or <see langword="null"/> to not filter by arena.</param>
        /// <returns>An enumerable that provides the types and descriptions of the loaded modules.</returns>
        IEnumerable<(Type Type, string? Description)> GetModuleTypesAndDescriptions(Arena? arena);

        /// <summary>
        /// Gets info about loaded modules.
        /// </summary>
        /// <param name="moduleTypeName">
        /// The <see cref="Type"/> name of module. 
        /// It can be the <see cref="Type.FullName"/> or for built-in modules, the <see cref="Type.AssemblyQualifiedName"/>.
        /// </param>
        /// <returns>
        /// A collection containing info about modules that matched the criteria.
        /// Note: There can be multiple modules with the same name.
        /// <list type="bullet">
        /// <item>There can be multiple copies of the same assembly loaded. In fact, they can be different versions.</item>
        /// <item>Two modules could have used the same namespace and type name. (unlikely)</item>
        /// </list>
        /// </returns>
        IEnumerable<ModuleInfo> GetModuleInfo(string moduleTypeName);

        /// <summary>
        /// Gets info about a loaded module.
        /// </summary>
        /// <param name="type">The type of module to get info about.</param>
        /// <param name="moduleInfo">Info about the module.</param>
        /// <returns><see langword="true"/> if the module was found; otherwise, <see langword="false"/>.</returns>
        bool TryGetModuleInfo(Type type, [MaybeNullWhen(false)] out ModuleInfo moduleInfo);

        #endregion
    }

    /// <summary>
    /// Information about a module.
    /// </summary>
    public readonly record struct ModuleInfo
    {
        public required Type Type { get; init; }
        public required bool IsPlugin { get; init; }
        public required string? Description { get; init; }
        public required IReadOnlySet<Arena> AttachedArenas { get; init; }
    }
}
