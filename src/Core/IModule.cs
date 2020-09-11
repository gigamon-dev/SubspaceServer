using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace SS.Core
{
    /// <summary>
    /// Interface for modules to implement.
    /// 
    /// <para>
    /// A module needs to include the following:
    /// <list type="number">
    /// <item>
    /// It must implement the <see cref="IModule"/> interface.
    /// </item>
    /// <item>
    /// It needs to have a default parameterless constructor if it is intended to be created by the 
    /// <see cref="ModuleManager"/> (the normal case).
    /// </item>
    /// <item>
    /// It needs to have a method named "Load".
    /// <para>
    /// The Load method must have a return type of <see cref="bool"/>. Returning true means success, false 
    /// indiciates failure.
    /// </para>
    /// 
    /// <para>
    /// The Load method must have at least one parameter.
    /// 
    /// <para>
    /// The first parameter must be of type <see cref="ComponentBroker"/>. The global <see cref="ComponentBroker"/> 
    /// will be passed into it.  Use the broker to register for callbacks or to get other interfaces that not required 
    /// to load.
    /// </para>
    /// 
    /// <para>
    /// All other parameters, if any, are for injecting interface dependencies that are REQUIRED for the module to load.
    /// Interface dependency parameters must all be interface types derived from <see cref="IComponentInterface"/>.  
    /// When the <see cref="ModuleManager"/> loads the module, it will gather the required dependencies and call the 
    /// Load method with all the dependencies passed in.  The <see cref="ModuleManager"/> will only call the Load method 
    /// when it can fulfill all of the dependencies. Therefore, only include dependency parameters for interfaces that 
    /// are REQUIRED for the module to load. Optional interfaces can be loaded during steps that occur after load, such 
    /// as <see cref="IModuleLoaderAware.PostLoad(ComponentBroker)"/>. DO NOT release the interfaces that the 
    /// <see cref="ModuleManager"/> passes in.  The <see cref="ModuleManager"/> manages getting and releasing them.
    /// For an idea of built-in interfaces that can be used, see the SS.Core.ComponentInterfaces namespace.
    /// </para>
    /// 
    /// </para>
    /// </item>
    /// </list>
    /// 
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
        /// A module should perform cleanup in this method. Examples of cleanup include:
        /// <list type="bullet">
        /// <item>Unregistering any <see cref="IComponentInterface"/>s it previously registered as being the implementor of.</item>
        /// <item>Unregistering from any Callbacks it previously subscribed to.</item>
        /// </list>
        /// </summary>
        /// <param name="broker">The global component broker.</param>
        /// <returns>True on success.  False on failure.</returns>
        bool Unload(ComponentBroker broker);
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
        /// It is a second initialization phase that allows modules to obtain references
        /// to interfaces exported by modules loaded after them.
        /// Iterfaces obtained in this method should release them in <see cref="PostLoad(ComponentBroker)"/>.
        /// </summary>
        /// <param name="broker"></param>
        /// <returns>True on success.  False on failure.</returns>
        bool PostLoad(ComponentBroker broker);

        /// <summary>
        /// This is called before all modules are unloaded.
        /// It is for cleaning up activity done in <see cref="PostLoad(ComponentBroker)"/>.
        /// </summary>
        /// <param name="broker"></param>
        /// <returns>True on success.  False on failure.</returns>
        bool PreUnload(ComponentBroker broker);
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
        /// This is called when a module is detached from an arena.
        /// </summary>
        /// <param name="arena">The arena the module is being detached from.</param>
        /// <returns>True on success.  False on failure.</returns>
        bool DetachModule(Arena arena);
    }
}
