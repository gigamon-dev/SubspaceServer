using SS.Core.ComponentInterfaces;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core
{
    /// <summary>
    /// Interface that provides a mechanism for synchronous loading and unloading of modules. The asynchronous equivalent of this interface is <see cref="IAsyncModule"/>.
    /// </summary>
    /// <remarks>
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
    /// Interface that provides a mechanism for asynchronous loading and unloading of modules. The synchronous equivalent of this interface is <see cref="IModule"/>.
    /// </summary>
    /// <inheritdoc cref="IModule" path="/remarks"/>
    public interface IAsyncModule
    {
        /// <summary>
        /// Loads the module.
        /// </summary>
        /// <remarks>
        /// A module should perform startup logic in this method.
        /// </remarks>
        /// <param name="broker">The global broker.</param>
        /// <param name="cancellationToken">The token to observe for cancellation.</param>
        /// <returns><see langword="true"/> on success. <see langword="false"/> on failure.</returns>
        Task<bool> LoadAsync(IComponentBroker broker, CancellationToken cancellationToken);

        /// <summary>
        /// Unloads the module.
        /// </summary>
        /// <remarks>
        /// A module should perform any cleanup it needs in this method.
        /// </remarks>
        /// <param name="broker">The global broker.</param>
        /// <param name="cancellationToken">The token to observe for cancellation.</param>
        /// <returns><see langword="true"/> on success. <see langword="false"/> on failure.</returns>
        Task<bool> UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Interface that provides a mechanism for modules to do work 
    /// after being loaded (<see cref="PostLoad(IComponentBroker)"/>) 
    /// or 
    /// before being unloaded (<see cref="PreUnload(IComponentBroker)"/>).
    /// The asynchronous equivalent of this interface is <see cref="IAsyncModuleLoaderAware"/>.
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
    /// Interface that provides a mechanism for modules to asynchronously do work 
    /// after being loaded (<see cref="PostLoad(IComponentBroker)"/>) 
    /// or
    /// before being unloaded (<see cref="PreUnload(IComponentBroker)"/>).
    /// The synchronous equivalent of this interface is <see cref="IModuleLoaderAware"/>.
    /// </summary>
    public interface IAsyncModuleLoaderAware
    {
        /// <summary>
        /// This is called after all modules are loaded.
        /// It is a second initialization phase that allows modules to obtain references to interfaces exported by modules loaded after them.
        /// </summary>
        /// <param name="broker">The global broker.</param>
        /// <param name="cancellationToken">The token to observe for cancellation.</param>
        Task PostLoadAsync(IComponentBroker broker, CancellationToken cancellationToken);

        /// <summary>
        /// This is called before all modules are unloaded.
        /// </summary>
        /// <param name="broker">The global broker.</param>
        /// <param name="cancellationToken">The token to observe for cancellation.</param>
        Task PreUnloadAsync(IComponentBroker broker, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Interface that provides a mechanism for modules to be able to be attached to an arena.
    /// The asynchronous equivalent of this interface is <see cref="IAsyncModuleLoaderAware"/>.
    /// </summary>
    /// <remarks>
    /// "Arena attaching" is a mechanism for a module to provide functionality for a subset of arenas rather for all arenas.
    /// The modules to attach to an arena are configured using the Modules:AttachModules setting in the arena.conf file.
    /// The modules must implement this <see cref="IArenaAttachableModule"/> or <see cref="IAsyncArenaAttachableModule"/>, and must be loaded.
    /// </remarks>
    public interface IArenaAttachableModule
    {
        /// <summary>
        /// This is called when a module is to be attached to an arena.
        /// </summary>
        /// <param name="arena">The arena the module is to be attached to.</param>
        /// <returns><see langword="true"/> on success. <see langword="false"/> on failure.</returns>
        bool AttachModule(Arena arena);

        /// <summary>
        /// This is called when a module is to be detached from an arena.
        /// </summary>
        /// <param name="arena">The arena the module is to be detached from..</param>
        /// <returns><see langword="true"/> on success. <see langword="false"/> on failure.</returns>
        bool DetachModule(Arena arena);
    }

    /// <summary>
    /// Interface that provides a mechanism for modules to be able to be asynchronously attached to an arena.
    /// The synchronous equivalent of this interface is <see cref="IModuleLoaderAware"/>.
    /// </summary>
    /// <inheritdoc cref="IArenaAttachableModule" path="/remarks"/>
    public interface IAsyncArenaAttachableModule
    {
        /// <summary>
        /// This is called when a module is to be attached to an arena.
        /// </summary>
        /// <param name="arena">The arena the module is to be attached to.</param>
        /// <param name="cancellationToken">The token to observe for cancellation.</param>
        /// <returns><see langword="true"/> on success. <see langword="false"/> on failure.</returns>
        Task<bool> AttachModuleAsync(Arena arena, CancellationToken cancellationToken);

        /// <summary>
        /// This is called when a module is to be detached from an arena.
        /// </summary>
        /// <param name="arena">The arena the module is to be detached from..</param>
        /// <param name="cancellationToken">The token to observe for cancellation.</param>
        /// <returns><see langword="true"/> on success. <see langword="false"/> on failure.</returns>
        Task<bool> DetachModuleAsync(Arena arena, CancellationToken cancellationToken);
    }
}
