using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace SS.Core
{
    /// <summary>
    /// Interface for modules to implement.
    /// 
    /// <para>
    /// Note: A module also needs to include a default parameterless constructor if it is intended to be created by the <see cref="ModuleManager"/> (the normal case).
    /// </para>
    /// 
    /// <para>
    /// Module life cycle:
    /// <list type="number">
    /// <item><see cref="IModule.Load(ModuleManager, Dictionary{Type, IComponentInterface})"/></item>
    /// <item>[optional] <see cref="IModuleLoaderAware.PostLoad(ModuleManager)"/></item>
    /// <item>[optional] <see cref="IArenaAttachableModule.AttachModule(Arena)"/></item>
    /// <item>[optional] <see cref="IArenaAttachableModule.DetachModule(Arena)"/></item>
    /// <item>[optional] <see cref="IModuleLoaderAware.PreUnload(ModuleManager)"/></item>
    /// <item><see cref="IModule.Unload(ModuleManager)"/></item>
    /// </list>
    /// </para>
    /// </summary>
    public interface IModule
    {
        /// <summary>
        /// The <see cref="IComponentInterface"/>s that a module declares are REQUIRED to load properly.
        /// The <see cref="ModuleManager"/> uses this list of dependencies to check whether
        /// all of them are available. When the dependencies can be fulfilled, the <see cref="ModuleManager"/>
        /// will then call the <see cref="Load(ModuleManager, Dictionary{Type, IComponentInterface})"/>
        /// method with references to the interfaces.
        /// <para>
        /// Do not include optional interfaces here. Only those that are required.
        /// </para>
        /// For the built-in interfaces see: SS.Core.ComponentInterfaces
        /// </summary>
        Type[] InterfaceDependencies { get; }

        /// <summary>
        /// This is where a module should initialize itself.
        /// </summary>
        /// <param name="mm">The global component broker.</param>
        /// <param name="dependencies"></param>
        /// <returns>True on success.  False on failure.</returns>
        bool Load(ModuleManager mm, IReadOnlyDictionary<Type, IComponentInterface> interfaceDependencies);

        /// <summary>
        /// This is where a module should perform cleanup. Including:
        /// <list type="bullet">
        /// <item>Unregistering any <see cref="IComponentInterface"/>s it previously registered as being the implementor of.</item>
        /// <item>Unregistering from any Callbacks it previously subscribed to.</item>
        /// </list>
        /// </summary>
        /// <param name="mm">The global component broker.</param>
        /// <returns>True on success.  False on failure.</returns>
        bool Unload(ModuleManager mm);
    }

    /// <summary>
    /// Interface that a module can optionally implement if it needs to do work 
    /// after being loaded (<see cref="PostLoad(ModuleManager)"/>) 
    /// or 
    /// before being unloaded (<see cref="PreUnload(ModuleManager)"/>).
    /// </summary>
    public interface IModuleLoaderAware
    {
        /// <summary>
        /// This is called after all modules are loaded.
        /// </summary>
        /// <param name="mm"></param>
        /// <returns>True on success.  False on failure.</returns>
        bool PostLoad(ModuleManager mm);

        /// <summary>
        /// This is called before all modules are unloaded.
        /// </summary>
        /// <param name="mm"></param>
        /// <returns>True on success.  False on failure.</returns>
        bool PreUnload(ModuleManager mm);
    }

    /// <summary>
    /// Interface that a module can optionally implement if it wants to be capable of being attached to an arena.
    /// "Arena attaching" is how a module can decide to provide functionality for a subset of arenas rather functionality across all arenas.
    /// </summary>
    public interface IArenaAttachableModule
    {
        /// <summary>
        /// This is called when a module ia attached to an arena.
        /// </summary>
        /// <param name="arena">The arena the module is being attached to.</param>
        /// <returns>True on success.  False on failure.</returns>
        bool AttachModule(Arena arena);

        /// <summary>
        /// This is called hwen a  module is detached from an arena.
        /// </summary>
        /// <param name="arena">The arena the module is being detached from.</param>
        /// <returns>True on success.  False on failure.</returns>
        bool DetachModule(Arena arena);
    }
}
