namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Represents a method that handles calls from timer.
    /// </summary>
    /// <returns>True to continue timer events. False to end the timer.</returns>
    public delegate bool TimerDelegate();

    /// <summary>
    /// Represents a method that handles calls from timer.  The method has a single input parameter for passing "state".
    /// </summary>
    /// <typeparam name="TState">Type of the state parameter of the method that this delegate encapsulates.</typeparam>
    /// <param name="state">An object that represents the context of the timer.</param>
    /// <returns>True to continue timer events. False to end the timer.</returns>
    public delegate bool TimerDelegate<in TState>(TState state);

    /// <summary>
    /// Represents a method to be called back when a timer is stopped/removed.
    /// </summary>
    public delegate void TimerCleanupDelegate();

    /// <summary>
    /// Represents a method to be called back when a timer is stopped/removed.
    /// </summary>
    /// <typeparam name="TState">Type of the state parameter of the method that this delegate encapsulates.</typeparam>
    /// <param name="state">An object that represents the context of the timer.</param>
    public delegate void TimerCleanupDelegate<in TState>(TState state);

    /// <summary>
    /// Interface with methods to schedule timers that run on threads from the thread pool.
    /// See <see cref="IMainloopTimer"/> for timers that run on the main loop.
    /// </summary>
    public interface IServerTimer : IComponentInterface
    {
        /// <summary>
        /// Starts a timer.
        /// </summary>
        /// <param name="callback">The delegate that the timer should invoke.</param>
        /// <param name="initialDelay">How long to wait for the first call (in milliseconds).</param>
        /// <param name="interval">How long to wait between calls (in milliseconds).</param>
        /// <param name="key">A key that can be used to selectively cancel timers.</param>
        void SetTimer(
            TimerDelegate callback,
            int initialDelay,
            int interval,
            object? key);

        /// <summary>
        /// Starts a timer.
        /// </summary>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="callback">The delegate that the timer should invoke.</param>
        /// <param name="initialDelay">How long to wait for the first call (in milliseconds).</param>
        /// <param name="interval">How long to wait between calls (in milliseconds).</param>
        /// <param name="state">A closure argument that will get passed to the timer <paramref name="callback"/>.</param>
        /// <param name="key">A key that can be used to selectively cancel timers.</param>
        void SetTimer<TState>(
            TimerDelegate<TState> callback,
            int initialDelay,
            int interval,
            TState state,
            object? key);

        /// <summary>
        /// Stops and removes a timer.
        /// </summary>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="callback">The delegate to clear timer(s) for.</param>
        /// <param name="key">
        /// Timers that match this key will be removed. 
        /// Use <see langword="null"/> to clear all timers associated with <paramref name="callback"/>, regardless of key.
        /// </param>
        void ClearTimer(
            TimerDelegate callback,
            object? key);

        /// <summary>
        /// Stops and removes a timer.
        /// </summary>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="callback">The delegate to clear timer(s) for.</param>
        /// <param name="key">
        /// Timers that match this key will be removed. 
        /// Use <see langword="null"/> to clear all timers associated with <paramref name="callback"/>, regardless of key.
        /// </param>
        /// <param name="cleanupCallback">
        /// A <see cref="TimerCleanupDelegate{TState}"/> to call once for each timer that is stopped.
        /// </param>
        void ClearTimer(
            TimerDelegate callback,
            object? key,
            TimerCleanupDelegate cleanupCallback);

        /// <summary>
        /// Stops and removes a timer.
        /// </summary>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="callback">The delegate to clear timer(s) for.</param>
        /// <param name="key">
        /// Timers that match this key will be removed. 
        /// Use <see langword="null"/> to clear all timers associated with <paramref name="callback"/>, regardless of key.
        /// </param>
        void ClearTimer<TState>(
            TimerDelegate<TState> callback,
            object? key);

        /// <summary>
        /// Stops and removes a timer, with a <paramref name="cleanupCallback"/> to be invoked for each timer that is stopped.
        /// </summary>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="callback">The delegate to clear timer(s) for.</param>
        /// <param name="key">
        /// Timers that match this key will be removed. 
        /// Use <see langword="null"/> to clear all timers associated with <paramref name="callback"/>, regardless of key.
        /// </param>
        /// <param name="cleanupCallback">
        /// A <see cref="TimerCleanupDelegate{TState}"/> to call once for each timer that is stopped.
        /// It will be called with the <c>parameter</c> that was provided when the timer was started.
        /// </param>
        void ClearTimer<TState>(
            TimerDelegate<TState> callback,
            object? key,
            TimerCleanupDelegate<TState> cleanupCallback);
    }

    /// <summary>
    /// Interface with methods to schedule timers that run on the main loop.
    /// See <see cref="IServerTimer"/> for timers that run on threads from the thread pool.
    /// </summary>
    public interface IMainloopTimer : IComponentInterface
    {
        /// <summary>
        /// Starts a timer.
        /// </summary>
        /// <param name="callback">The delegate that the timer should invoke.</param>
        /// <param name="initialDelay">How long to wait for the first call (in milliseconds).</param>
        /// <param name="interval">How long to wait between calls (in milliseconds).</param>
        /// <param name="key">A key that can be used to selectively cancel timers.</param>
        void SetTimer(
            TimerDelegate callback,
            int initialDelay,
            int interval,
            object? key);

        /// <summary>
        /// Starts a timer.
        /// </summary>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="callback">The delegate that the timer should invoke.</param>
        /// <param name="initialDelay">How long to wait for the first call (in milliseconds).</param>
        /// <param name="interval">How long to wait between calls (in milliseconds).</param>
        /// <param name="state">A closure argument that will get passed to the timer <paramref name="callback"/>.</param>
        /// <param name="key">A key that can be used to selectively cancel timers.</param>
        void SetTimer<TState>(
            TimerDelegate<TState> callback,
            int initialDelay,
            int interval,
            TState state,
            object? key);

        /// <summary>
        /// Stops and removes timers.
        /// </summary>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="callback">The delegate to clear timer(s) for.</param>
        /// <param name="key">
        /// Timers that match this key will be removed. 
        /// Use <see langword="null"/> to clear all timers associated with <paramref name="callback"/>, regardless of key.
        /// </param>
        void ClearTimer(
            TimerDelegate callback,
            object? key);

        /// <summary>
        /// Stops and removes timers, with a <paramref name="cleanupCallback"/> to be invoked for each timer that is stopped.
        /// </summary>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="callback">The delegate to clear timer(s) for.</param>
        /// <param name="key">
        /// Timers that match this key will be removed. 
        /// Use <see langword="null"/> to clear all timers associated with <paramref name="callback"/>, regardless of key.
        /// </param>
        /// <param name="cleanupCallback">
        /// A <see cref="TimerCleanupDelegate{TState}"/> to call once for each timer that is stopped.
        /// </param>
        void ClearTimer(
            TimerDelegate callback,
            object? key,
            TimerCleanupDelegate cleanupCallback);

        /// <summary>
        /// Stops and removes timers.
        /// </summary>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="callback">The delegate to clear timer(s) for.</param>
        /// <param name="key">
        /// Timers that match this key will be removed. 
        /// Use <see langword="null"/> to clear all timers associated with <paramref name="callback"/>, regardless of key.
        /// </param>
        void ClearTimer<TState>(
            TimerDelegate<TState> callback,
            object? key);

        /// <summary>
        /// Stops and removes timers, with a <paramref name="cleanupCallback"/> to be invoked for each timer that is stopped.
        /// </summary>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="callback">The delegate to clear timer(s) for.</param>
        /// <param name="key">
        /// Timers that match this key will be removed. 
        /// Use <see langword="null"/> to clear all timers associated with <paramref name="callback"/>, regardless of key.
        /// </param>
        /// <param name="cleanupCallback">
        /// A <see cref="TimerCleanupDelegate{TState}"/> to call once for each timer that is stopped.
        /// It will be called with the <typeparamref name="TState"/> that was provided when the timer was started.
        /// </param>
        void ClearTimer<TState>(
            TimerDelegate<TState> callback,
            object? key,
            TimerCleanupDelegate<TState> cleanupCallback);
    }
}
