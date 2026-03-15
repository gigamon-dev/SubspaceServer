using CommunityToolkit.HighPerformance.Buffers;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Interfaces;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that provides authorization for league players roles.
    /// </summary>
    /// <remarks>
    /// This module keeps track of which players are assigned to league roles.
    /// Other modules tell it which roles to keep track of for each league using <see cref="ILeagueAuthorization.Register"/>.
    /// In the background, it periodically queries the league database, keeping in sync with any changes to role assignments.
    /// </remarks>
    /// <param name="leagueRepository"></param>
    /// <param name="logManager"></param>
    public sealed class LeagueAuthorization(
        ILeagueRepository leagueRepository,
        ILogManager logManager) : IAsyncModule, ILeagueAuthorization, IDisposable
    {
        private readonly ILeagueRepository _leagueRepository = leagueRepository;
        private readonly ILogManager _logManager = logManager;

        private InterfaceRegistrationToken<ILeagueAuthorization>? _iLeagueAuthorizationToken;

        private readonly Dictionary<LeagueRoleKey, LeagueRoleRegistration> _registrations = [];
        private readonly Lock _lock = new();

        private Task? _refreshTask;
        private CancellationTokenSource? _refreshStopCts;
        private HashSet<string> _refreshGrantSet = new(Constants.TargetPlayerCount, StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);

        #region Module

        Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _refreshStopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _refreshTask = Task.Run(() => PeriodicRefreshAllAsync(_refreshStopCts.Token), _refreshStopCts.Token);

            _iLeagueAuthorizationToken = broker.RegisterInterface<ILeagueAuthorization>(this);
            return Task.FromResult(true);
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _iLeagueAuthorizationToken) != 0)
            {
                return false;
            }

            if (_refreshTask is not null)
            {
                _refreshStopCts!.Cancel();
                await _refreshTask.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            return true;
        }

        #endregion

        #region ILeagueAuthorization

        void ILeagueAuthorization.Register(long leagueId, LeagueRole role)
        {
            LeagueRoleKey key = new(leagueId, role);

            lock (_lock)
            {
                if (!_registrations.TryGetValue(key, out LeagueRoleRegistration? registration))
                {
                    registration = new();
                    _registrations.Add(key, registration);

                    // Refresh the data, but don't wait for it to happen.
                    _ = Task.Run(() => RefreshOneAsync(key, CancellationToken.None));
                }
                else
                {
                    registration.RegistrationCount++;
                }
            }
        }

        void ILeagueAuthorization.Unregister(long leagueId, LeagueRole role)
        {
            LeagueRoleKey key = new(leagueId, role);

            lock (_lock)
            {
                if (!_registrations.TryGetValue(key, out LeagueRoleRegistration? registration))
                    return;

                if (--registration.RegistrationCount == 0)
                {
                    _registrations.Remove(key);
                }
            }
        }

        bool ILeagueAuthorization.IsInRole(string playerName, long leagueId, LeagueRole role)
        {
            LeagueRoleKey key = new(leagueId, role);
            LeagueRoleRegistration? registration;

            lock (_lock)
            {
                if (!_registrations.TryGetValue(key, out registration))
                    return false;
            }

            lock (registration.Lock)
            {
                return registration.Grants.Contains(playerName);
            }
        }

        async Task<string?> ILeagueAuthorization.GrantRoleAsync(string? executorPlayerName, ReadOnlyMemory<char> targetPlayerName, long leagueId, LeagueRole role, ReadOnlyMemory<char> notes, CancellationToken cancellationToken)
        {
            string? errorMessage;

            try
            {
                if (await _leagueRepository.InsertLeaguePlayerRoleAsync(
                    targetPlayerName,
                    leagueId,
                    role,
                    executorPlayerName,
                    notes,
                    cancellationToken))
                {
                    errorMessage = null;
                }
                else
                {
                    errorMessage = "Player already has the role.";
                }
            }
            catch (Exception)
            {
                return "Database error.";
            }

            if (errorMessage is null)
            {
                // Role granted successfully.
                LeagueRoleKey key = new(leagueId, role);
                LeagueRoleRegistration? registration;

                lock (_lock)
                {
                    _registrations.TryGetValue(key, out registration);
                }

                if (registration is not null)
                {
                    lock (registration.Lock)
                    {
                        registration.Grants.Add(StringPool.Shared.GetOrAdd(targetPlayerName.Span));
                    }
                }
            }

            return errorMessage;
        }

        async Task<string?> ILeagueAuthorization.RevokeRoleAsync(string? executorPlayerName, ReadOnlyMemory<char> targetPlayerName, long leagueId, LeagueRole role, ReadOnlyMemory<char> notes, CancellationToken cancellationToken)
        {
            string? message;

            try
            {
                if (await _leagueRepository.DeleteLeaguePlayerRoleAsync(
                    targetPlayerName,
                    leagueId,
                    role,
                    executorPlayerName,
                    notes,
                    cancellationToken))
                {
                    message = null;
                }
                else
                {
                    message = "Record not found.";
                }
            }
            catch (Exception)
            {
                return "Database error.";
            }

            if (message is null)
            {
                // Role revoked successfully.
                LeagueRoleKey key = new(leagueId, role);
                LeagueRoleRegistration? registration;

                lock (_lock)
                {
                    _registrations.TryGetValue(key, out registration);
                }

                if (registration is not null)
                {
                    lock (registration.Lock)
                    {
                        registration.Grants.GetAlternateLookup<ReadOnlySpan<char>>().Remove(targetPlayerName.Span);
                    }
                }
            }

            return message;
        }

        async Task<bool> ILeagueAuthorization.RefreshAsync(long leagueId, LeagueRole role, CancellationToken cancellationToken)
        {
            LeagueRoleKey key = new(leagueId, role);
            return await RefreshOneAsync(key, cancellationToken);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _refreshStopCts?.Dispose();
        }

        #endregion

        private async Task PeriodicRefreshAllAsync(CancellationToken cancellationToken)
        {
            HashSet<LeagueRoleKey> keys = [];
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));

            while (await timer.WaitForNextTickAsync(cancellationToken) 
                && !cancellationToken.IsCancellationRequested)
            {
                keys.Clear();

                lock (_lock)
                {
                    keys.UnionWith(_registrations.Keys);
                }

                foreach (LeagueRoleKey key in keys)
                {
                    await RefreshOneAsync(key, cancellationToken);
                }
            }
        }

        private async Task<bool> RefreshOneAsync(LeagueRoleKey key, CancellationToken cancellationToken)
        {
            LeagueRoleRegistration? registration;

            lock (_lock)
            {
                if (!_registrations.TryGetValue(key, out registration))
                    return false;
            }

            // Only allow one refresh at a time.
            await _refreshSemaphore.WaitAsync(cancellationToken);

            try
            {
                // Check if the data has changed in the database by looking at the last updated date.
                DateTime? lastUpdated;

                try
                {
                    lastUpdated = await _leagueRepository.GetLeaguePlayerRoleLastUpdatedAsync(key.LeagueId, key.Role, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(LeagueAuthorization), $"Error getting league role last updated date for League: {key.LeagueId}, Role: {key.Role}. {ex.Message}");
                    return false;
                }

                lock (registration.Lock)
                {
                    if (registration.LastUpdated is not null && registration.LastUpdated == lastUpdated)
                    {
                        // Already up to date
                        return true;
                    }
                }

                // Refresh the data.
                try
                {
                    try
                    {
                        await _leagueRepository.GetLeaguePlayerRoleGrantsAsync(key.LeagueId, key.Role, _refreshGrantSet, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(LeagueAuthorization), $"Error getting league role grants for League: {key.LeagueId}, Role: {key.Role}. {ex.Message}");
                        return false;
                    }

                    lock (registration.Lock)
                    {
                        // Swap
                        (registration.Grants, _refreshGrantSet) = (_refreshGrantSet, registration.Grants);
                        registration.LastUpdated = lastUpdated;
                    }
                }
                finally
                {
                    _refreshGrantSet.Clear();
                }

                return true;
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }

        #region Helper types

        /// <summary>
        /// Represents a League + Role combination.
        /// </summary>
        /// <param name="LeagueId"></param>
        /// <param name="Role"></param>
        private readonly record struct LeagueRoleKey(long LeagueId, LeagueRole Role);

        /// <summary>
        /// Info for a League + Role combination.
        /// </summary>
        private class LeagueRoleRegistration
        {
            /// <summary>
            /// The number of times the <see cref="LeagueRoleKey"/> has been registered.
            /// When it hits 0, the registration will be removed and it will no longer periodically try to refresh the data.
            /// </summary>
            public int RegistrationCount = 1;

            /// <summary>
            /// The time the data was last updated in the database.
            /// Used to determine if data needs to be reloaded from the database.
            /// </summary>
            public DateTime? LastUpdated;

            /// <summary>
            /// The names of players who have the role.
            /// </summary>
            public HashSet<string> Grants = new(Constants.TargetPlayerCount, StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// For synchronization.
            /// </summary>
            public readonly Lock Lock = new();
        }

        #endregion
    }
}
