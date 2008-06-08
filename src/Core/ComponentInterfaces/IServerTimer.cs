using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public interface IServerTimer : IComponentInterface
    {
        /// <summary>
        /// Starts a timer
        /// </summary>
        /// <typeparam name="TArg">type of the parameter the callback accepts</typeparam>
        /// <param name="callback">the method to call</param>
        /// <param name="initialDelay">how long to wait for the first call (in milliseconds)</param>
        /// <param name="interval">how long to wait between calls (in milliseconds)</param>
        /// <param name="parameter">a closure argument that will get passed to the timer callback</param>
        /// <param name="key">a key that can be used to selectively cancel timers</param>
        void SetTimer<TArg>(
            TimerDelegate<TArg> callback,
            int initialDelay,
            int interval,
            TArg parameter,
            object key);

        /// <summary>
        /// Stops and removes a timer
        /// </summary>
        /// <typeparam name="TArg">type of the parameter the callback accepts</typeparam>
        /// <param name="callback">the timer method you want to clear</param>
        /// <param name="key">timers that match this key will be removed. 
        /// using NULL means to clear all timers with the given function, regardless of key</param>
        void ClearTimer<TArg>(
            TimerDelegate<TArg> callback,
            object key);

        /// <summary>
        /// Stops and removes a timer.  The cleanupCallback will be called for each timer that was stopped, with the state object from that timer.
        /// </summary>
        /// <typeparam name="TArg">type of the parameter the callback accepts</typeparam>
        /// <param name="callback">the timer method you want to clear</param>
        /// <param name="key">timers that match this key will be removed. 
        /// using NULL means to clear all timers with the given function, regardless of key</param>
        /// <param name="cleanupCallback">cleanup a CleanupFunc to call once for each timer being cancelled</param>
        void ClearTimer<TArg>(
            TimerDelegate<TArg> callback,
            object key,
            TimerCleanupDelegate<TArg> cleanupCallback);

        /// <summary>
        /// Calls a delegate to run on a thread from the thread pool.
        /// </summary>
        /// <typeparam name="TParam">type of state object</typeparam>
        /// <param name="callback">delegate to call</param>
        /// <param name="state">state to pass to the delegate</param>
        /// <returns>true if the the delegate was successfully queued to run; otherwise false</returns>
        bool RunInThread<TParam>(WorkerDelegate<TParam> callback, TParam state);
    }
}
