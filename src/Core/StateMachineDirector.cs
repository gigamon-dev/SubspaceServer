using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Threading;

namespace SS.Core
{
    public enum StateMachineExecutionOption
    {
        /// <summary>
        /// Execute synchronously on the same thread.
        /// </summary>
        Synchronous = 0,

        /// <summary>
        /// Execute on the mainloop thread.
        /// </summary>
        Mainloop,

        /// <summary>
        /// Execute on a thread from the .NET thread pool.
        /// </summary>
        ThreadPool
    }

    /// <summary>
    /// Assists with state machine transitions such that handlers are executed in the desired manner.
    /// </summary>
    /// <typeparam name="TState">The state enum type.</typeparam>
    /// <typeparam name="TItem">The state machine type.</typeparam>
    /// <param name="mainloop">The mainloop service.</param>
    public class StateMachineDirector<TState, TItem>(IMainloop mainloop) 
        where TState : struct, Enum
        where TItem : class
    {
        private static readonly ObjectPool<ExecuteHandlerDTO> _executeHandlerDTOPool = new DefaultObjectPool<ExecuteHandlerDTO>(new DefaultPooledObjectPolicy<ExecuteHandlerDTO>());
        
        private readonly IMainloop _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));

        // TODO: Do we really need to support more than one handler per state?
        private readonly Dictionary<TState, List<Registration>> _registrations = new(64);
        
        // TODO: Do we need to support registrations for transitions (from+to combinations)?
        //private readonly Dictionary<(TState, TState), List<Registration>> _transitionRegistrations = new(64);

        /// <summary>
        /// Whether to allow handlers to be executed synchronously when already in the desired context
        /// (e.g. already on the mainloop thread or already on a thread pool thread).
        /// </summary>
        public bool AllowSynchronousContinuations { get; set; } = false;

        /// <summary>
        /// Register a state change handler.
        /// </summary>
        /// <param name="state"></param>
        /// <param name="handler"></param>
        public void Register(TState state, Action<TItem, TState> handler, StateMachineExecutionOption option)
        {
            ArgumentNullException.ThrowIfNull(handler);

            if (!_registrations.TryGetValue(state, out List<Registration>? registrationList))
            {
                registrationList = new(1); // most likely just 1 registration per state
                _registrations.Add(state, registrationList);
            }

            registrationList.Add(new Registration(handler, option));
        }

        /// <summary>
        /// Unregisters a state change handler.
        /// </summary>
        /// <param name="state"></param>
        /// <param name="handler"></param>
        public bool Unregister(TState state, Action<TItem, TState> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            if (_registrations.TryGetValue(state, out List<Registration>? registrationList))
            {
                for (int i = registrationList.Count - 1; i >= 0; i--)
                {
                    if (registrationList[i].Handler == handler)
                    {
                        registrationList.RemoveAt(i);

                        if (registrationList.Count == 0)
                        {
                            _registrations.Remove(state);
                        }

                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Processes a change of an object's state. This executes any registered handlers.
        /// </summary>
        public void ProcessStateChange(TItem item, TState newState)
        {
            if (_registrations.TryGetValue(newState, out List<Registration>? registrationList))
            {
                for (int i = 0; i < registrationList.Count; i++)
                {
                    Registration registration = registrationList[i];

                    ExecuteHandlerDTO dto = _executeHandlerDTOPool.Get();
                    dto.Handler = registration.Handler;
                    dto.Item = item;
                    dto.State = newState;

                    switch (registration.Option)
                    {
                        case StateMachineExecutionOption.Mainloop:
                            if (AllowSynchronousContinuations && _mainloop.IsMainloop)
                            {
                                ExecuteHandler(dto);
                            }
                            else
                            {
                                _mainloop.QueueMainWorkItem(ExecuteHandler, dto);
                            }
                            break;

                        case StateMachineExecutionOption.ThreadPool:
                            if (AllowSynchronousContinuations && Thread.CurrentThread.IsThreadPoolThread)
                            {
                                ExecuteHandler(dto);
                            }
                            else
                            {
                                _mainloop.QueueThreadPoolWorkItem(ExecuteHandler, dto);
                            }
                            break;

                        case StateMachineExecutionOption.Synchronous:
                        default:
                            ExecuteHandler(dto);
                            break;
                    }
                }
            }

            static void ExecuteHandler(ExecuteHandlerDTO dto)
            {
                try
                {
                    if (dto.Handler is not null && dto.Item is not null)
                        dto.Handler(dto.Item, dto.State);
                }
                finally
                {
                    _executeHandlerDTOPool.Return(dto);
                }
            }
        }

        #region Helper types

        private readonly record struct Registration(Action<TItem, TState> Handler, StateMachineExecutionOption Option);

        private class ExecuteHandlerDTO : IResettable
        {
            public Action<TItem, TState>? Handler;
            public TItem? Item;
            public TState State;

            bool IResettable.TryReset()
            {
                Handler = null;
                Item = null;
                State = default;

                return true;
            }
        }

        #endregion
    }
}
