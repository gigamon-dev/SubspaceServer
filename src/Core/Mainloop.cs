using System;
using System.Collections.Generic;
using System.Text;
using System.Timers;
using System.Threading;

namespace SS.Core
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TArg"></typeparam>
    /// <param name="arg"></param>
    /// <returns>true to continue timer events</returns>
    public delegate bool TimerDelegate<TArg>(TArg arg);

    /// <summary>
    /// The equivalent of ASSS' mainloop.[ch] but without using the main thread
    /// in a loop that processes the timers sequentially.
    /// 
    /// Threads from the thread pool are used.  Therefore, multiple
    /// timers can process in parallel.
    /// 
    /// TODO: figure out cleaner way to do the MainLoopTimer class
    /// calling Stop() while the callback is being executed wont actually stop the timer
    /// for now it'll be fine since it will keep trying to stop the timer in Dispose(), but this is not optimal.
    /// </summary>
    public class Mainloop : IModule
    {
        private interface ITimer : IDisposable
        {
            void Stop();
            Delegate CallbackDelegate { get; }
            object Key { get; }
        }

        private class MainloopTimer<TArg> : System.Timers.Timer, ITimer
        {
            private Mainloop _owner;
            private TimerDelegate<TArg> _callback;
            private TArg _parameter;
            private object _key;
            private double _interval;

            // used to tell if the timer function is executing
            // -1 - timer disposed
            //  0 - not currently processing
            //  1 - currently processing
            private int _syncPoint = 0;

            public MainloopTimer(
                Mainloop owner, 
                TimerDelegate<TArg> callback,
                double initialDelay, 
                double interval,
                TArg parameter,
                object key)
            {
                if (initialDelay <= 0)
                    throw new ArgumentOutOfRangeException("initialDelay");

                _owner = owner;
                _callback = callback;
                _parameter = parameter;
                _key = key;
                _interval = interval;

                this.Interval = initialDelay;
                this.AutoReset = false;
                this.Elapsed += new ElapsedEventHandler(timer_Elapsed);
            }

            private void timer_Elapsed(object sender, ElapsedEventArgs e)
            {
                // only process if the syncPoint is 0 (in which case we also change it to 1)
                if (Interlocked.CompareExchange(ref _syncPoint, 1, 0) != 0)
                {
                    // syncPoint was not 0, so we couldn't change it to 1
                    // this means we don't run
                    return;
                }

                if (_callback(_parameter) && (_interval != 0))
                {
                    // keep timer running
                    this.Interval = _interval;
                    _syncPoint = 0;
                    Start();
                }
                else
                {
                    // timer not to run anymore
                    _owner.RemoveTimer(this);
                    Dispose();
                }
            }

            protected override void Dispose(bool disposing)
            {
                Stop();

                // if the timer is currently processing, then the syncPoint will be 1
                int originalSyncPoint;
                while (((originalSyncPoint = Interlocked.CompareExchange(ref _syncPoint, -1, 0)) != 0) &&
                    (originalSyncPoint != -1))
                {
                    Thread.Sleep(0);
                    Stop();
                }

                // getting to here means we were able to change the syncPoint to -1 (or it was already -1)
                // which means the timer definitely stopped
                // continue with the normal disposal of this timer
                base.Dispose(disposing);
            }

            #region ITimer Members

            public Delegate CallbackDelegate
            {
                get { return _callback; }
            }

            public object Key
            {
                get { return _key; }
            }

            #endregion
        }

        private List<ITimer> _timerList = new List<ITimer>();
        private object _lockObj = new object();

        public Mainloop()
        {
        }

        public void SetTimer<TArg>(
            TimerDelegate<TArg> callback,
            int initialDelay,
            int interval,
            TArg parameter,
            object key)
        {
            MainloopTimer<TArg> timer = new MainloopTimer<TArg>(
                this, 
                callback, 
                initialDelay, 
                interval, 
                parameter, 
                key);

            lock (_lockObj)
            {
                _timerList.Add(timer);
            }

            timer.Start();
        }

        public void ClearTimer<TArg>(
            TimerDelegate<TArg> callback,
            object key)
        {
            Delegate callbackDelegate = callback;

            List<ITimer> timersToDispose = new List<ITimer>();

            lock (_lockObj)
            {
                for (int x = _timerList.Count-1; x >= 0; x--)
                {
                    ITimer timer = _timerList[x];
                    if ((timer.CallbackDelegate == callbackDelegate) &&
                        (timer.Key.Equals(key) || (key == null)))
                    {
                        timer.Stop();

                        _timerList.RemoveAt(x);
                        timersToDispose.Add(timer);
                    }
                }
            }

            while (timersToDispose.Count > 0)
            {
                timersToDispose[0].Dispose();
                timersToDispose.RemoveAt(0);
            }
        }

        /// <summary>
        /// used by the MainloopTimer class to remove itself
        /// </summary>
        /// <param name="timerToRemove"></param>
        private void RemoveTimer(ITimer timerToRemove)
        {
            lock (_lockObj)
            {
                _timerList.Remove(timerToRemove);
            }
        }

        #region IModule Members

        Type[] IModule.ModuleDependencies
        {
            get { return new Type[] { }; }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IModule> moduleDependencies)
        {
            return true;
        }

        bool IModule.Unload()
        {
            return true;
        }

        #endregion
    }
}
