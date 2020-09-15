using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Represents a method that contains work to be done.
    /// </summary>
    /// <typeparam name="TState">Type of the state parameter of the method that this delegate encapsulates.</typeparam>
    /// <param name="state">An object that represents the context.</param>
    public delegate void WorkerDelegate<in TState>(TState state);

    public interface IMainloop : IComponentInterface
    {
        /// <summary>
        /// Called by the main thread to process the timers and work items.
        /// </summary>
        /// <returns>The exist code to be returned to the OS.</returns>
        int RunLoop();

        /// <summary>
        /// Signals the main loop to stop.
        /// </summary>
        void Quit(ExitCode code);

        /// <summary>
        /// Queues up work to be done on the main loop thread.
        /// If your module uses this method, remember to call <see cref="WaitForMainWorkItemDrain"/> when unloading
        /// to ensure that everything that was scheduled completes before unloading.
        /// </summary>
        /// <typeparam name="TParam">type of state object</typeparam>
        /// <param name="callback">delegate to call</param>
        /// <param name="state">state to pass to the delegate</param>
        /// <returns>true if the the delegate was successfully queued to run; otherwise false</returns>
        bool QueueMainWorkItem<TParam>(WorkerDelegate<TParam> func, TParam param);

        /// <summary>
        /// Blocks until the all work items that were queued from 
        /// <see cref="QueueMainWorkItem{TParam}(WorkerDelegate{TParam}, TParam)"/>
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
        bool QueueThreadPoolWorkItem<TParam>(WorkerDelegate<TParam> func, TParam param);
    }
}
