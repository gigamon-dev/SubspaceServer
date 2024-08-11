using System;
using System.Collections.Immutable;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that functions as an intermediary between components by managing interfaces, callbacks, and advisors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interfaces are how components expose their functionality to other components.
    /// A module can implement an interface and register the interface on a <see cref="ComponentBroker"/> to expose it to modules.
    /// A module can use a <see cref="ComponentBroker"/> to obtain interfaces of other modules too.
    /// </para>
    /// <para>
    /// Callbacks are an implementation of the publisher-subscriber pattern where
    /// any component can be a publisher, and any component can be a subscriber. 
    /// There can be multiple publishers and multiple subscribers.
    /// Registering for a callback on an <see cref="Arena"/> means you only want events for that specific arena.
    /// Registering for a callback on the root <see cref="ComponentBroker"/> means you want all events, including those fired for an arena.
    /// </para>
    /// <para>
    /// Advisors are interfaces that are expected to possibly have more than one implementation.
    /// So, using advisors actually means getting a collection of implementations for a specified advisor interface type, 
    /// and then asking each implementation in the collection for advice on how to proceed with a given task.
    /// </para>
    /// </remarks>
    public interface IComponentBroker : IComponentInterface
    {
        #region Interfaces

        /// <summary>
        /// Registers an implementation of an interface to be exposed to others via the broker.
        /// </summary>
        /// <typeparam name="TInterface">The <see cref="Type"/> of interface to register.</typeparam>
        /// <param name="instance">The implementer instance to register.</param>
        /// <param name="name">An optional name to register the <paramref name="instance"/> as.</param>
        /// <returns></returns>
        InterfaceRegistrationToken<TInterface> RegisterInterface<TInterface>(TInterface instance, string? name = null) where TInterface : class, IComponentInterface;

        /// <summary>
        /// Unregisters an implementation of an interface.
        /// It will refuse to unregister if the reference count indicates it it still in use.
        /// </summary>
        /// <typeparam name="TInterface">The <see cref="Type"/> of interface to un-register.</typeparam>
        /// <param name="token">The unique token that was returned from <see cref="RegisterInterface{TInterface}(TInterface, string)"/>.</param>
        /// <returns>The reference count. Therefore, 0 means success.</returns>
        int UnregisterInterface<TInterface>(ref InterfaceRegistrationToken<TInterface>? token) where TInterface : class, IComponentInterface;

        /// <summary>
        /// Retrieves the currently registered instance that implements an interface.
        /// Remember to call <see cref="ReleaseInterface"/> when done using it.
        /// </summary>
        /// <param name="interfaceType">The <see cref="Type"/> of interface to retrieve.</param>
        /// <param name="key">
        /// An optional name of the instance to get.
        /// Used when there are purposely multiple implementers of the same interface.
        /// </param>
        /// <returns>The currently registered instance.  Otherwise, null.</returns>
        IComponentInterface? GetInterface(Type interfaceType, object? key = null);

        /// <summary>
        /// Retrieves the currently registered instance that implements an interface.
        /// Remember to call <see cref="ReleaseInterface"/> when done using it.
        /// </summary>
        /// <typeparam name="TInterface">The <see cref="Type"/> of interface to retrieve.</typeparam>
        /// <param name="key">
        /// An optional name of the instance to get.
        /// Used when there are purposely multiple implementers of the same interface.
        /// </param>
        /// <returns>The currently registered instance.  Otherwise, null.</returns>
        TInterface? GetInterface<TInterface>(object? key = null) where TInterface : class, IComponentInterface;

        /// <summary>
        /// Releases an interface.
        /// </summary>
        /// <param name="interfaceType">The <see cref="Type"/> of interface to release.</param>
        /// <param name="instance">The instance to release.</param>
        /// <param name="key">
        /// An optional name of the instance to release.
        /// Used when there are purposely multiple implementers of the same interface.
        /// </param>
        void ReleaseInterface(Type interfaceType, IComponentInterface instance, object? key = null);

        /// <summary>
        /// Releases an interface.
        /// </summary>
        /// <typeparam name="TInterface">The <see cref="Type"/> of interface to release.</typeparam>
        /// <param name="instance">The instance to release.</param>
        /// <param name="key">
        /// An optional name of the instance to release.
        /// Used when there are purposely multiple implementers of the same interface.
        /// </param>
        void ReleaseInterface<TInterface>(ref TInterface? instance, object? key = null) where TInterface : class, IComponentInterface;

        #endregion

        #region Callbacks

        /// <summary>
        /// Registers a handler for a "callback" (publisher/subscriber event).
        /// </summary>
        /// <typeparam name="TDelegate">
        /// Delegate representing the type of event.
        /// The type itself is used as a unique identifier, so each event should have its own unique delegate.
        /// </typeparam>
        /// <param name="handler">The handler to register.</param>
        void RegisterCallback<TDelegate>(TDelegate handler) where TDelegate : Delegate;

        /// <summary>
        /// Unregisters a handler for a "callback" (publisher/subscriber event).
        /// </summary>
        /// <typeparam name="TDelegate">
        /// Delegate representing the type of event.
        /// The type itself is used as a unique identifier, so each event should have its own unique delegate.
        /// </typeparam>
        /// <param name="handler">The handler to un-register.</param>
        void UnregisterCallback<TDelegate>(TDelegate handler) where TDelegate : Delegate;

        /// <summary>
        /// Gets the current delegate for a "callback" (publisher/subscriber event).
        /// </summary>
        /// <typeparam name="TDelegate">
        /// Delegate representing the type of event.
        /// The type itself is used as a unique identifier, so each event should have its own unique delegate.
        /// </typeparam>
        /// <returns>The delegate if found. Otherwise null.</returns>
        TDelegate? GetCallback<TDelegate>() where TDelegate : Delegate;

        #endregion

        #region Advisors

        /// <summary>
        /// Registers an advisor.
        /// </summary>
        /// <typeparam name="TAdvisor">The type of advisor to register.</typeparam>
        /// <param name="advisor">The advisor to register.</param>
        /// <returns>A token that can be used to unregister the advisor.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="advisor"/> was null.</exception>
        AdvisorRegistrationToken<TAdvisor> RegisterAdvisor<TAdvisor>(TAdvisor advisor) where TAdvisor : IComponentAdvisor;

        /// <summary>
        /// Unregisters an advisor.
        /// </summary>
        /// <typeparam name="TAdvisor">The type of advisor to unregister.</typeparam>
        /// <param name="token">Token of the advisor to unregister.</param>
        /// <returns>True if the advisor was unregistered. Otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="token"/> was null.</exception>
        bool UnregisterAdvisor<TAdvisor>(ref AdvisorRegistrationToken<TAdvisor>? token) where TAdvisor : IComponentAdvisor;

        /// <summary>
        /// Gets a collection of advisors that have been registered.
        /// </summary>
        /// <remarks>
        /// The advisors returned will include any registered on this <see cref="ComponentBroker"/> instance and those from any parent <see cref="ComponentBroker"/>.
        /// In other words, calling this method on an <see cref="Arena"/> will get those registered on the arena level and on the global level.
        /// Calling this method on the global <see cref="ComponentBroker"/> will return only those registered on the global level.
        /// </remarks>
        /// <typeparam name="TAdvisor">The type of advisors to get.</typeparam>
        /// <returns>A collection of advisors. The collection is purposely thread-safe.</returns>
        ImmutableArray<TAdvisor> GetAdvisors<TAdvisor>() where TAdvisor : IComponentAdvisor;

        #endregion

        /// <summary>
        /// The parent broker. <see langword="null" /> means there's no parent.
        /// </summary>
        IComponentBroker? Parent { get; }
    }
}
