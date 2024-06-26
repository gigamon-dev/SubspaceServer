using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// The equivalent of ASSS' mainloop.[ch]
    /// </summary>
    [CoreModuleInfo]
    public sealed class Mainloop : IModule, IMainloop, IMainloopTimer, IServerTimer, IDisposable
    {
        private ComponentBroker _broker;
        private IObjectPoolManager _objectPoolManager;
        private InterfaceRegistrationToken<IMainloop> _iMainloopToken;
        private InterfaceRegistrationToken<IMainloopTimer> _iMainloopTimerToken;
        private InterfaceRegistrationToken<IServerTimer> _iServerTimerToken;

        // for main loop
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly CancellationToken _cancellationToken;
        private Thread _mainThread;
        private ExitCode _quitCode = ExitCode.None;

        // for main loop workitems
        private readonly BlockingCollection<IRunInMainWorkItem> _runInMainQueue = new(); // TODO: maybe we should use bounding?
        private readonly AutoResetEvent _runInMainAutoResetEvent = new(false);

        // for IMainloopTimer
        private readonly LinkedList<MainloopTimer> _mainloopTimerList = new();
        private readonly AutoResetEvent _mainloopTimerAutoResetEvent = new(false);
        private readonly object _mainloopTimerLock = new();
        private MainloopTimer _timerProcessing = null;

        // for IServerTimer
        private readonly LinkedList<ThreadPoolTimer> _serverTimerList = new();
        private readonly object _serverTimerLock = new();

        private static readonly ObjectPool<Job> s_jobPool;

        static Mainloop()
        {
			// Use DefaultObjectPoolProvider so that it will create a DisposableObjectPool.
			DefaultObjectPoolProvider provider = new()
            {
                MaximumRetained = 65536 // a number it should never reach
            };
            s_jobPool = provider.Create<Job>();
		}

        public Mainloop()
        {
            _cancellationToken = _cancellationTokenSource.Token;
        }

        #region IModule Members

        public bool Load(ComponentBroker broker)
        {
            _broker = broker;

            _objectPoolManager = _broker.GetInterface<IObjectPoolManager>();

            _iMainloopTimerToken = broker.RegisterInterface<IMainloopTimer>(this);
            _iServerTimerToken = broker.RegisterInterface<IServerTimer>(this);
            _iMainloopToken = broker.RegisterInterface<IMainloop>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            // Make sure all timers are stopped.
            // There shouldn't be any left if all modules were correctly written to stop their timers when unloading.
            lock (_mainloopTimerLock)
            {
                if (_mainloopTimerList.Count > 0)
                {
                    _mainloopTimerList.Clear();
                }
            }

            lock (_serverTimerLock)
            {
                if (_serverTimerList.Count > 0)
                {
                    foreach (var timer in _serverTimerList)
                    {
                        timer.Stop();
                    }

                    foreach (var timer in _serverTimerList)
                    {
                        timer.Dispose();
                    }

                    _serverTimerList.Clear();
                }
            }

            if (broker.UnregisterInterface(ref _iMainloopToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iServerTimerToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iMainloopTimerToken) != 0)
                return false;

            if (_objectPoolManager != null)
                _broker.ReleaseInterface(ref _objectPoolManager);

            return true;
        }

        #endregion

        #region IMainloop Members

        ExitCode IMainloop.RunLoop()
        {
            _mainThread = Thread.CurrentThread;

            try
            {
                _mainThread.Name = nameof(Mainloop);
            }
            catch
            {
                // ignore any errors
            }

            SynchronizationContext oldSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new MainloopSynchronizationContext(this));

            WaitHandle[] waitHandles = new WaitHandle[]
            {
                _cancellationToken.WaitHandle,
                _runInMainAutoResetEvent, 
                _mainloopTimerAutoResetEvent
            };

            while (!_cancellationToken.IsCancellationRequested)
            {
                MainloopCallback.Fire(_broker);

                // Wait until the next timer needs to be processed
                // perhaps keep track of the one, so we can immediately process it?
                // perhaps keep the list ordered such that the next one due is in front?

                LinkedListNode<MainloopTimer> dueNext = null;

                lock (_mainloopTimerLock)
                {
                    for(LinkedListNode<MainloopTimer> node = _mainloopTimerList.First; node != null; node = node.Next)
                    {
                        if (dueNext == null || node.Value.WhenDue < dueNext.Value.WhenDue)
                            dueNext = node;
                    }
                }

                TimeSpan waitTime;
                if (dueNext != null)
                {
                    waitTime = dueNext.Value.WhenDue - DateTime.UtcNow;
                    if (waitTime <= TimeSpan.Zero)
                    {
                        // already due
                        ProcessTimers();
                        waitTime = TimeSpan.Zero;
                    }
                }
                else
                {
                    waitTime = TimeSpan.FromMilliseconds(-1); // wait indefinitely
                }

                // wait until:
                // the next timer needs to be run 
                // OR 
                // we're signaled (to quit, that another timer was added, or that another work item was added)
                int waitHandleIndex = WaitHandle.WaitAny(waitHandles, waitTime);
                switch (waitHandleIndex)
                {
                    case WaitHandle.WaitTimeout:
                        // process timers, that's why we timed out
                        ProcessTimers();
                        break;

                    case 0:
                        // we're being told to quit
                        continue;

                    case 1:
                        // at least one workitem was added
                        DrainRunInMain();

                        if (_runInMainQueue.Count > 0)
                        {
                            // make sure we come back around to process more
                            _runInMainAutoResetEvent.Set();
                        }

                        break;

                    case 2:
                        // at least one timer was added
                        break;
                }
            }

            SynchronizationContext.SetSynchronizationContext(oldSynchronizationContext);

            // We've been told to stop.
            // At this point, nothing else can be added to the workitem queue.
            // Process any that remain before exiting.
            while (_runInMainQueue.Count > 0)
            {
                DrainRunInMain();
            }

            return _quitCode;
        }

        private void ProcessTimers()
        {
            DateTime now = DateTime.UtcNow;

            Monitor.Enter(_mainloopTimerLock);

            LinkedListNode<MainloopTimer> node = _mainloopTimerList.First;
            while (node != null)
            {
                if (node.Value.WhenDue <= now)
                {
                    MainloopTimer timer = node.Value;

                    _timerProcessing = timer;
                    Monitor.Exit(_mainloopTimerLock);

                    bool wantsToKeepRunning = false;

                    try
                    {
                        wantsToKeepRunning = timer.CallbackInvoker.Invoke();
                    }
                    catch (Exception ex)
                    {
                        WriteLog(LogLevel.Warn, $"Caught exception while invoking main loop timer. {ex}");
                    }

                    Monitor.Enter(_mainloopTimerLock);
                    _timerProcessing = null;

                    if (timer.Stop || !wantsToKeepRunning || timer.Interval == Timeout.Infinite)
                    {
                        // Stop/remove the timer.
                        LinkedListNode<MainloopTimer> next = node.Next;
                        _mainloopTimerList.Remove(node);
                        node = next;

                        if (timer.Stop)
                        {
                            // At least one other thread asked us to stop/remove the timer while we were processing it.
                            // That thread, or threads, is waiting. Signal back that it's been removed.
                            timer.Stopped = true;
                            Monitor.PulseAll(_mainloopTimerLock);
                        }

                        continue;
                    }
                    else
                    {
                        timer.WhenDue = now.AddMilliseconds(timer.Interval);
                    }
                }

                node = node.Next;
            }

            Monitor.Exit(_mainloopTimerLock);
        }

        private void DrainRunInMain()
        {
            // This differs from ASSS in is that it will only process up to the initial count.
            // This protects against the possibility not being able to fully drain if producers
            // out produce what the single consumer (mainloop thread) can process.

            int maxToProcess = _runInMainQueue.Count;
            int count = 0;

            while (++count <= maxToProcess 
                && _runInMainQueue.TryTake(out IRunInMainWorkItem workItem))
            {
                if (workItem != null)
                {
                    try
                    {
                        workItem.Process();
                    }
                    catch (Exception ex)
                    {
                        WriteLog(LogLevel.Warn, $"Caught an exception while processing a work item. {ex}");
                    }
                    finally
                    {
                        if (workItem is IDisposable disposable)
                            disposable.Dispose();
                    }
                }
            }
        }

        void IMainloop.Quit(ExitCode code)
        {
            _quitCode = code;
            _runInMainQueue.CompleteAdding();
            _cancellationTokenSource.Cancel();
        }

        bool IMainloop.QueueMainWorkItem<TState>(Action<TState> callback, TState state)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            if (_runInMainQueue.IsAddingCompleted)
                return false;

            RunInMainWorkItem<TState> workItem = (_objectPoolManager?.GetPool<RunInMainWorkItem<TState>>() ?? Pool<RunInMainWorkItem<TState>>.Default).Get();
            workItem.Set(callback, state);

            bool added;

            try
            {
                added = _runInMainQueue.TryAdd(workItem);
            }
            catch (InvalidOperationException)
            {
                // Queue is marked as complete for adding.
                added = false;
            }

            if (added)
            {
                _runInMainAutoResetEvent.Set();
            }
            else
            {
                workItem.Dispose();
            }

            return added;
        }

        void IMainloop.WaitForMainWorkItemDrain()
        {
            if (_mainThread == null)
            {
                // Can't wait for main work items to drain if there is no thread processing the mainloop yet.
                // This can happen if there is an issue during the initial load of modules,
                // where it to aborts loading and never gets to running the mainloop.
                return;
            }

            if (Thread.CurrentThread == _mainThread)
            {
                DrainRunInMain();
            }
            else
            {
                WaitForMainWorkItem.CreateAndWait(_runInMainQueue, _runInMainAutoResetEvent);
            }
        }

        bool IMainloop.QueueThreadPoolWorkItem<TState>(Action<TState> callback, TState state)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            return ThreadPool.QueueUserWorkItem(callback, state, false);
        }

        bool IMainloop.IsMainloop => Thread.CurrentThread == _mainThread;

        #endregion

        #region IMainloopTimer Members

        void IMainloopTimer.SetTimer(TimerDelegate callback, int initialDelay, int interval, object key)
        {
            MainloopTimer_SetTimer(new TimerCallbackInvoker(callback), initialDelay, interval, key);
        }

        void IMainloopTimer.SetTimer<TState>(TimerDelegate<TState> callback, int initialDelay, int interval, TState state, object key)
        {
            MainloopTimer_SetTimer(new TimerCallbackInvoker<TState>(callback, state), initialDelay, interval, key);
        }

        private void MainloopTimer_SetTimer(
            ITimerCallbackInvoker callbackInvoker,
            int initialDelay,
            int interval,
            object key)
        {
            if (callbackInvoker == null)
                throw new ArgumentNullException(nameof(callbackInvoker));

            lock(_mainloopTimerLock)
            {
                _mainloopTimerList.AddLast(new MainloopTimer(initialDelay, interval, key, callbackInvoker));
            }

            _mainloopTimerAutoResetEvent.Set();
        }

        void IMainloopTimer.ClearTimer(TimerDelegate callback, object key)
        {
            MainloopTimer_ClearTimer(callback, key, null);
        }

        void IMainloopTimer.ClearTimer(TimerDelegate callback, object key, TimerCleanupDelegate cleanupCallback)
        {
            MainloopTimer_ClearTimer(callback, key, _ => cleanupCallback());
        }

        void IMainloopTimer.ClearTimer<TState>(TimerDelegate<TState> callback, object key)
        {
            MainloopTimer_ClearTimer(callback, key, null);
        }

        void IMainloopTimer.ClearTimer<TState>(TimerDelegate<TState> callback, object key, TimerCleanupDelegate<TState> cleanupCallback)
        {
            MainloopTimer_ClearTimer(callback, key,
                (timer) =>
                {
                    if (timer.CallbackInvoker is TimerCallbackInvoker<TState> callbackInvoker)
                    {
                        cleanupCallback?.Invoke(callbackInvoker.State);
                    }
                });
        }

        private void MainloopTimer_ClearTimer(Delegate callback, object key, Action<MainloopTimer> timerDisposedAction)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            LinkedList<MainloopTimer> timersRemoved = null;

            lock (_mainloopTimerLock)
            {
                LinkedListNode<MainloopTimer> node, next, inProgress = null;
                for (node = _mainloopTimerList.First; node != null; node = next)
                {
                    next = node.Next;

                    MainloopTimer timer = node.Value;

                    if (timer.CallbackInvoker.Callback.Equals(callback)
                        && ((key == null) || (key == timer.Key)))
                    {
                        if (timer == _timerProcessing)
                        {
                            // Can't touch the node, the mainloop thread is working on it.  We'll wait on it later.
                            inProgress = node;
                        }
                        else
                        {
                            _mainloopTimerList.Remove(node);

                            if (timerDisposedAction != null)
                            {
                                if (timersRemoved == null)
                                    timersRemoved = new LinkedList<MainloopTimer>();

                                timersRemoved.AddLast(node);
                            }
                        }
                    }
                }

                if (inProgress != null)
                {
                    // We want to remove the timer that the mainloop is currently executing.
                    MainloopTimer timer = inProgress.Value;
                    timer.Stop = true;

                    while (timer.Stopped == false)
                        Monitor.Wait(_mainloopTimerLock);

                    // The mainloop removed it for us.
                    System.Diagnostics.Debug.Assert(inProgress.List == null);
                    System.Diagnostics.Debug.Assert(inProgress.Previous == null);
                    System.Diagnostics.Debug.Assert(inProgress.Next == null);
                    System.Diagnostics.Debug.Assert(inProgress.Value != null);

                    if (timerDisposedAction != null)
                    {
                        if (timersRemoved == null)
                            timersRemoved = new LinkedList<MainloopTimer>();

                        timersRemoved.AddLast(timer);
                    }
                }
            }

            if (timerDisposedAction != null && timersRemoved != null)
            {
                for (LinkedListNode<MainloopTimer> node = _mainloopTimerList.First; node != null; node = node.Next)
                {
                    timerDisposedAction.Invoke(node.Value);
                }
            }
        }

        #endregion

        #region IServerTimer Members

        void IServerTimer.SetTimer(
            TimerDelegate callback,
            int initialDelay,
            int interval,
            object key)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            ServerTimer_SetTimer(
                new TimerCallbackInvoker(callback),
                initialDelay,
                interval,
                key);
        }

        void IServerTimer.SetTimer<TState>(
            TimerDelegate<TState> callback,
            int initialDelay,
            int interval,
            TState state,
            object key)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            ServerTimer_SetTimer(
                new TimerCallbackInvoker<TState>(callback, state),
                initialDelay,
                interval,
                key);
        }

        private void ServerTimer_SetTimer(
            ITimerCallbackInvoker callbackInvoker,
            int initialDelay,
            int interval,
            object key)
        {
            if (callbackInvoker == null)
                throw new ArgumentNullException(nameof(callbackInvoker));

            ThreadPoolTimer timer = new(
                this,
                initialDelay,
                interval,
                key,
                callbackInvoker);

            lock (_serverTimerLock)
            {
                _serverTimerList.AddLast(timer);
            }

            timer.Start();
        }

        void IServerTimer.ClearTimer(
            TimerDelegate callback,
            object key)
        {
            ServerTimer_ClearTimer(callback, key, null);
        }

        void IServerTimer.ClearTimer(
            TimerDelegate callback,
            object key,
            TimerCleanupDelegate cleanupCallback)
        {
            ServerTimer_ClearTimer(callback, key, _ => cleanupCallback());
        }

        void IServerTimer.ClearTimer<TState>(TimerDelegate<TState> callback, object key)
        {
            ServerTimer_ClearTimer(callback, key, null);
        }

        void IServerTimer.ClearTimer<TState>(
            TimerDelegate<TState> callback,
            object key,
            TimerCleanupDelegate<TState> cleanupCallback)
        {
            ServerTimer_ClearTimer(callback, key,
                (timer) =>
                {
                    if (timer.CallbackInvoker is TimerCallbackInvoker<TState> callbackInvoker)
                    {
                        cleanupCallback?.Invoke(callbackInvoker.State);
                    }
                });
        }

        private void ServerTimer_ClearTimer(Delegate callback, object key, Action<ThreadPoolTimer> timerDisposedAction)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            LinkedList<ThreadPoolTimer> timersToDispose = null;

            lock (_serverTimerLock)
            {
                LinkedListNode<ThreadPoolTimer> node = _serverTimerList.First;
                while (node != null)
                {
                    LinkedListNode<ThreadPoolTimer> next = node.Next;
                    ThreadPoolTimer timer = node.Value;

                    if (timer.CallbackInvoker.Callback.Equals(callback)
                        && ((key == null) || (key == timer.Key)))
                    {
                        timer.Stop();

                        _serverTimerList.Remove(node);

                        if (timersToDispose == null)
                            timersToDispose = new LinkedList<ThreadPoolTimer>();

                        timersToDispose.AddLast(node);
                    }

                    node = next;
                }
            }

            if (timersToDispose != null)
            {
                foreach (var timer in timersToDispose)
                {
                    timer.Dispose();
                }

                if (timerDisposedAction != null)
                {
                    foreach (var timer in timersToDispose)
                    {
                        timerDisposedAction(timer);
                    }
                }

                timersToDispose.Clear();
            }
        }

        /// <summary>
        /// Used by <see cref="ThreadPoolTimer"/> to remove itself.
        /// </summary>
        /// <param name="timerToRemove"></param>
        private void RemoveTimer(ThreadPoolTimer timerToRemove)
        {
            if (timerToRemove == null)
                throw new ArgumentNullException(nameof(timerToRemove));

            bool itemRemoved;

            lock (_serverTimerLock)
            {
                itemRemoved = _serverTimerList.Remove(timerToRemove);
            }

            if (itemRemoved)
            {
                timerToRemove.Dispose();
            }
        }

        #endregion

        private void WriteLog(LogLevel level, string message)
        {
            ILogManager _logManager = _broker.GetInterface<ILogManager>();

            if (_logManager != null)
            {
                try
                {
                    _logManager.LogM(level, nameof(Mainloop), message);
                }
                finally
                {
                    _broker.ReleaseInterface(ref _logManager);
                }
            }
            else
            {
                if (level == LogLevel.Error)
                    Console.Error.WriteLine($"{(LogCode)level} <{nameof(Mainloop)}> {message}");
                else
                    Console.WriteLine($"{(LogCode)level} <{nameof(Mainloop)}> {message}");
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Dispose();
            _runInMainAutoResetEvent.Dispose();
            _mainloopTimerAutoResetEvent.Dispose();
            _runInMainQueue.Dispose();
        }

        #region Main Work Item Helpers

        private interface IRunInMainWorkItem
        {
            void Process();
        }

        private class WaitForMainWorkItem : IRunInMainWorkItem
        {
            private bool waitResolved;
            private readonly object lockObj = new();

            private WaitForMainWorkItem()
            {
            }

            public void Process()
            {
                lock (lockObj)
                {
                    waitResolved = true;
                    Monitor.Pulse(lockObj);
                }
            }

            // private constructor and this method forces whoever creates it, to wait on it.  So hopefully no misuse.
            public static void CreateAndWait(BlockingCollection<IRunInMainWorkItem> runInMainQueue, AutoResetEvent runInMainAutoResetEvent)
            {
                if (runInMainQueue == null)
                    throw new ArgumentNullException(nameof(runInMainQueue));

                if (runInMainAutoResetEvent == null)
                    throw new ArgumentNullException(nameof(runInMainAutoResetEvent));

                WaitForMainWorkItem workItem = new(); // TODO: object pooling
                workItem.Wait(runInMainQueue, runInMainAutoResetEvent);
            }

            private void Wait(BlockingCollection<IRunInMainWorkItem> runInMainQueue, AutoResetEvent runInMainAutoResetEvent)
            {
                if (runInMainQueue == null)
                    throw new ArgumentNullException(nameof(runInMainQueue));

                if (runInMainAutoResetEvent == null)
                    throw new ArgumentNullException(nameof(runInMainAutoResetEvent));

                lock (lockObj)
                {
                    waitResolved = false;

                    if (runInMainQueue.IsAddingCompleted) // checking in advance, but still can be marked between this check and the add
                        return;

                    try
                    {
                        runInMainQueue.Add(this);
                    }
                    catch (InvalidOperationException)
                    {
                        // The queue is marked as complete for adding.
                        return;
                    }

                    runInMainAutoResetEvent.Set();

                    while (!waitResolved)
                    {
                        Monitor.Wait(lockObj);
                    }
                }
            }
        }

        private class RunInMainWorkItem<TState> : PooledObject, IRunInMainWorkItem
        {
            private Action<TState> _callback;
            private TState _state;

            public void Set(Action<TState> callback, TState state)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                _state = state;
            }

            public void Process()
            {
                _callback?.Invoke(_state);
            }

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    _callback = null;
                    _state = default;
                }

                base.Dispose(isDisposing);
            }
        }

        #endregion

        #region Timer Helpers

        private interface ITimerCallbackInvoker
        {
            bool Invoke();

            Delegate Callback { get; }
        }

        private class TimerCallbackInvoker : ITimerCallbackInvoker
        {
            private readonly TimerDelegate _callback;

            public TimerCallbackInvoker(TimerDelegate callback)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            }

            public Delegate Callback => _callback;

            public bool Invoke()
            {
                return _callback();
            }
        }

        private class TimerCallbackInvoker<TState> : ITimerCallbackInvoker
        {
            private readonly TimerDelegate<TState> _callback;

            public TimerCallbackInvoker(TimerDelegate<TState> callback, TState state)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                State = state;
            }

            public Delegate Callback => _callback;

            public TState State { get; }

            public bool Invoke()
            {
                return _callback(State);
            }
        }

        private class MainloopTimer
        {
            public DateTime WhenDue { get; set; }
            public int Interval { get; }
            public object Key { get; }
            public ITimerCallbackInvoker CallbackInvoker { get; }
            public bool Stop { get; set; } = false;
            public bool Stopped { get; set; } = false;

            public MainloopTimer(
                int initialDelay,
                int interval,
                object key,
                ITimerCallbackInvoker callbackInvoker)
            {
                if (initialDelay < 0)
                    throw new ArgumentOutOfRangeException(nameof(initialDelay), "must be >= 0");

                if (interval <= 0 && interval != Timeout.Infinite)
                    throw new ArgumentOutOfRangeException(nameof(interval), "must be > 0 or Timeout.Infinite");

                WhenDue = DateTime.UtcNow.AddMilliseconds(initialDelay);
                Interval = interval;
                Key = key;
                CallbackInvoker = callbackInvoker ?? throw new ArgumentNullException(nameof(callbackInvoker));
            }
        }

        /// <summary>
        /// Encapsulates logic for a timer that runs on the thread pool.
        /// </summary>
        private class ThreadPoolTimer
        {
            private readonly Timer _timer;
            private readonly Mainloop _owner;
            private readonly int _initialDelay;
            private readonly int _interval;
            public object Key { get; }
            public ITimerCallbackInvoker CallbackInvoker;

            // for synchronization
            private readonly object _lockObj = new();
            private bool _stop = false;
            private readonly ManualResetEvent _timerExecuting = new(true);
            private bool _disposed = false;

            public ThreadPoolTimer(
                Mainloop owner,
                int initialDelay,
                int interval,
                object key,
                ITimerCallbackInvoker callbackInvoker)
            {
                if (initialDelay < 0)
                    throw new ArgumentOutOfRangeException(nameof(initialDelay), "must be >= 0");

                if (interval <= 0 && interval != Timeout.Infinite)
                    throw new ArgumentOutOfRangeException(nameof(interval), "must be > 0 or Timeout.Infinite");

                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _initialDelay = initialDelay;
                _interval = interval;
                Key = key;
                CallbackInvoker = callbackInvoker ?? throw new ArgumentNullException(nameof(callbackInvoker));

                // Creating the timer, but not starting it yet.
                _timer = new Timer(TimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
            }

            private void TimerElapsed(object state)
            {
                bool isContinuing = false;

                lock (_lockObj)
                {
                    if (_stop)
                        return; // we've been told to stop (plus we know someone else is taking care of removing us from the timer list)

                    _timerExecuting.Reset();
                }

                try
                {
                    try
                    {
                        // execute the callback
                        bool wantsToKeepRunning = CallbackInvoker.Invoke();

                        lock (_lockObj)
                        {
                            if (_stop || (wantsToKeepRunning == false) || (_interval <= 0))
                            {
                                return;
                            }

                            _timer.Change(_interval, Timeout.Infinite);
                            isContinuing = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        // exception means dont run again
                        _owner.WriteLog(LogLevel.Warn, $"Caught exception while invoking thread pool timer. {ex}");
                    }
                    finally
                    {
                        // note: releasing any threads waiting on us before trying to remove ourself from the timer list because
                        // a thread waiting for us could be holding the lock to the timer list
                        _timerExecuting.Set();
                    }
                }
                finally
                {
                    if (isContinuing == false)
                    {
                        // make sure that this timer object is removed from the timer list (it may have been removed already)
                        _owner.RemoveTimer(this);
                    }
                }
            }

            public void Start()
            {
                _timer.Change(_initialDelay, Timeout.Infinite);
            }

            public void Stop()
            {
                Stop(false);
            }

            private void Stop(bool waitIfExecuting)
            {
                lock (_lockObj)
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    _stop = true;
                }

                if (waitIfExecuting)
                {
                    // block until the timer is no longer executing
                    _timerExecuting.WaitOne();

                    // there is still a chance the timer runs after this (race condition), 
                    // but at least we are certain it will not try to execute the callback because _stop == true
                }
            }

            #region IDisposable Members

            public void Dispose() => Dispose(true);

            protected virtual void Dispose(bool disposing)
            {
                if (_disposed)
                {
                    return;
                }

                if (disposing)
                {
                    Stop(true);
                    _timer.Dispose();
                }

                _disposed = true;
            }

            #endregion
        }

        #endregion

        private sealed class MainloopSynchronizationContext : SynchronizationContext
        {
            private readonly IMainloop _mainloop;

            public MainloopSynchronizationContext(IMainloop mainloop)
            {
                _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            }

            // For async/await, only post is needed
            public override void Post(SendOrPostCallback d, object state)
            {
                _mainloop.QueueMainWorkItem((state) => d(state), state);
            }

            // but, here's an implementation for send too just in case
            public override void Send(SendOrPostCallback d, object state)
            {
                if (_mainloop.IsMainloop)
                {
                    d(state);
                }
                else
                {
                    Job job = s_jobPool.Get();

                    try
                    {
                        job.Set(d, state);

                        // Queue the job up for the mainloop to process.
                        if (!_mainloop.QueueMainWorkItem(Mainloop_ExecuteJob, job))
                            throw new Exception("Unable to queue main loop work item.");

                        // Wait for it to complete.
                        job.Wait();
                    }
                    finally
                    {
                        s_jobPool.Return(job);
                    }
                }

                // local static helper function to execute a job
                static void Mainloop_ExecuteJob(Job job)
                {
                    job.Execute();
                }
            }
        }

        private sealed class Job : IResettable, IDisposable
		{
            private SendOrPostCallback _callback;
            private object _state;
            private readonly AutoResetEvent _autoResetEvent = new(false);

            public void Set(SendOrPostCallback callback, object state)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                _state = state;
                _autoResetEvent.Reset();
            }

            public void Execute()
            {
                _callback(_state);
                _autoResetEvent.Set();
            }

            public void Wait()
            {
                _autoResetEvent.WaitOne();
            }

			bool IResettable.TryReset()
			{
				Set(null, null);
				return true;
			}

			#region IDisposable

			public void Dispose()
            {
                _autoResetEvent.Dispose();
            }

			#endregion
		}
    }
}
