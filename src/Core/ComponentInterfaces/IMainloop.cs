using System;

namespace SS.Core.ComponentInterfaces
{
    public interface IMainloop : IComponentInterface
    {
        /// <summary>
        /// Called by the thread that wants to do all of the 'main loop' work (process the timers and work items).
        /// This method blocks until another thread calls <see cref="Quit(ExitCode)"/>.
        /// Normally, the actual main thread of the application would call this.
        /// </summary>
        /// <returns>The exit code that was passed into the call to <see cref="Quit(ExitCode)"/>.</returns>
        ExitCode RunLoop();

        /// <summary>
        /// Signals the main loop to stop.
        /// This would normally be <see cref="ExitCode.None"/> for a normal shutdown or <see cref="ExitCode.Recycle"/> to signal a restart.
        /// </summary>
        void Quit(ExitCode code);

        /// <summary>
        /// Queues up work to be done on the main loop thread.
        /// If your module uses this method, remember to call <see cref="WaitForMainWorkItemDrain"/> when unloading
        /// to ensure that everything that was scheduled completes before unloading.
        /// </summary>
        /// <typeparam name="TState">type of state object</typeparam>
        /// <param name="callback">delegate to call</param>
        /// <param name="state">state to pass to the delegate</param>
        /// <returns>true if the the delegate was successfully queued to run; otherwise false</returns>
        bool QueueMainWorkItem<TState>(Action<TState> callback, TState state);

        /// <summary>
        /// Blocks until the all work items that were queued from 
        /// <see cref="QueueMainWorkItem{TState}(Action{TState}, TState)"/>
        /// are completed.
        /// </summary>
        void WaitForMainWorkItemDrain();

        /// <summary>
        /// Queues up work to be done on a thread pool thread.
        /// </summary>
        /// <typeparam name="TParam">type of state object</typeparam>
        /// <param name="callback">delegate to call</param>
        /// <param name="state">state to pass to the delegate</param>
        /// <returns>true if the the delegate was successfully queued to run; otherwise false</returns>
        bool QueueThreadPoolWorkItem<TState>(Action<TState> callback, TState state);

        /// <summary>
        /// Gets whether the current thread is the mainloop thread.
        /// </summary>
        bool IsMainloop { get; }
    }
}
