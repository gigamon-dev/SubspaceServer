using SS.Core.ComponentInterfaces;

namespace SS.Core
{
    /// <summary>
    /// Interface for modules to implement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All modules must implement this interface.
    /// </para>
    /// <para>
    /// REQUIRED <see cref="IComponentInterface"/> dependencies are injected into a module's constructor.
    /// The injected dependencies are managed by the <see cref="ModuleManager"/>. DO NOT release them manually. 
    /// The <see cref="ModuleManager"/> will automatically release them after the module is unloaded.
    /// </para>
    /// <para>
    /// Other <see cref="IComponentInterface"/> dependencies can be manually retrieved using the <see cref="IComponentBroker"/>, and must be released back manually when done.
    /// Normally, if a dependency is manually retrieved on <see cref="Load(IComponentBroker)"/>, that dependency will be released in <see cref="Unload(IComponentBroker)"/>.
    /// </para>
    /// <para>
    /// A module can hook further into the module life-cycle by implementing <see cref="IModuleLoaderAware"/> and <see cref="IArenaAttachableModule"/>.
    /// </para>
    /// <para>
    /// Module life cycle:
    /// <list type="number">
    /// <item>Constructor([&lt;component interface&gt;, ...])</item>
    /// <item>Load(<see cref="IComponentBroker"/>) method</item>
    /// <item>[optional] <see cref="IModuleLoaderAware.PostLoad(IComponentBroker)"/></item>
    /// <item>[optional] <see cref="IArenaAttachableModule.AttachModule(Arena)"/></item>
    /// <item>[optional] <see cref="IArenaAttachableModule.DetachModule(Arena)"/></item>
    /// <item>[optional] <see cref="IModuleLoaderAware.PreUnload(IComponentBroker)"/></item>
    /// <item><see cref="IModule.Unload(IComponentBroker)"/></item>
    /// </list>
    /// </para>
    /// </remarks>
    public interface IModule
    {
        /// <summary>
        /// Loads the module.
        /// </summary>
        /// <remarks>
        /// A module should perform startup logic in this method.
        /// </remarks>
        /// <param name="broker">The global broker.</param>
        /// <returns><see langword="true"/> on success. <see langword="false"/> on failure.</returns>
        bool Load(IComponentBroker broker);

        /// <summary>
        /// Unloads the module.
        /// </summary>
        /// <remarks>
        /// A module should perform any cleanup it needs in this method.
        /// </remarks>
        /// <param name="broker">The global broker.</param>
        /// <returns><see langword="true"/> on success. <see langword="false"/> on failure.</returns>
        bool Unload(IComponentBroker broker);
    }

    /// <summary>
    /// Interface that a module can optionally implement if it needs to do work 
    /// after being loaded (<see cref="PostLoad(IComponentBroker)"/>) 
    /// or 
    /// before being unloaded (<see cref="PreUnload(IComponentBroker)"/>).
    /// </summary>
    public interface IModuleLoaderAware
    {
        /// <summary>
        /// This is called after all modules are loaded.
        /// It is a second initialization phase that allows modules to obtain references to interfaces exported by modules loaded after them.
        /// </summary>
        /// <param name="broker">The global broker.</param>
        void PostLoad(IComponentBroker broker);

        /// <summary>
        /// This is called before all modules are unloaded.
        /// </summary>
        /// <param name="broker">The global broker.</param>
        void PreUnload(IComponentBroker broker);
    }

    /// <summary>
    /// Interface that a module can optionally implement if it wants to be capable of being attached to an arena.
    /// </summary>
    /// <remarks>
    /// "Arena attaching" is how a module can decide to provide functionality for a subset of arenas rather functionality across all arenas.
    /// The modules to attach to an arena are configured using the Modules:AttachModules setting in the arena.conf file.
    /// The modules must implement this interface and must be loaded.
    /// </remarks>
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
