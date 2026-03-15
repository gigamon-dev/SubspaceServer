using SS.Core;

namespace SS.Matchmaking.Interfaces
{
    public enum LeagueRole
    {
        /// <summary>
        /// Role for league managers.
        /// </summary>
        Manager = 1,

        /// <summary>
        /// Role for players that are allowed to participate in a league practice.
        /// A matchmaking queue can be configured to require that a player has this role.
        /// </summary>
        PracticePermit = 2,

        /// <summary>
        /// Role for players that are allowed to grant the 'Practice Permit' role.
        /// </summary>
        PermitManager = 3,
    }

    public interface ILeagueAuthorization : IComponentInterface
    {
        /// <summary>
        /// Register a <paramref name="leagueId"/> + <paramref name="role"/> combination to be tracked.
        /// </summary>
        /// <remarks>
        /// Data for registered <paramref name="leagueId"/> + <paramref name="role"/> combinations periodically polled and cached locally to keep in sync.
        /// <para>
        /// A <paramref name="leagueId"/> + <paramref name="role"/> combination can be registered multiple times and the system keeps track of the count.
        /// It will continue to poll for data until <see cref="Unregister"/> is called the same number of times.
        /// </para>
        /// </remarks>
        /// <param name="leagueId">Id of the league to track <paramref name="role"/> data for.</param>
        /// <param name="role">The role to track.</param>
        void Register(long leagueId, LeagueRole role);

        /// <summary>
        /// Unregister a <paramref name="leagueId"/> + <paramref name="role"/> combination.
        /// </summary>
        /// <param name="leagueId">Id of the league to stop tracking <paramref name="role"/> data for.</param>
        /// <param name="role">The role to stop tracking.</param>
        void Unregister(long leagueId, LeagueRole role);

        /// <summary>
        /// Gets whether a player has a specified <paramref name="role"/> for a specified league.
        /// </summary>
        /// <remarks>
        /// The <paramref name="leagueId"/> + <paramref name="role"/> combination needs to be registered beforehand so that it will have data.
        /// </remarks>
        /// <param name="playerName">The player to get data for.</param>
        /// <param name="leagueId">The league to get data for.</param>
        /// <param name="role">The role to look for.</param>
        /// <returns><see langword="true"/> if the player has the specified <paramref name="role"/>; otherwise, <see langword="false"/>.</returns>
        bool IsInRole(string playerName, long leagueId, LeagueRole role);

        /// <summary>
        /// Grants a specified league role to a specified player.
        /// </summary>
        /// <param name="executorPlayerName">The name of the player granting the role. <see langword="null"/> if being initiated by the system itself.</param>
        /// <param name="targetPlayerName">The name of the player to grant the role to.</param>
        /// <param name="leagueId">The league to grant the role for.</param>
        /// <param name="role">The role to grant.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<string?> GrantRoleAsync(string? executorPlayerName, ReadOnlyMemory<char> targetPlayerName, long leagueId, LeagueRole role, ReadOnlyMemory<char> notes, CancellationToken cancellationToken);

        /// <summary>
        /// Revokes a specified league role from a specified player.
        /// </summary>
        /// <param name="executorPlayerName">The name of the player revoking the role. <see langword="null"/> if being initiated by the system itself.</param>
        /// <param name="targetPlayerName">The name of the player to revoke the role from.</param>
        /// <param name="leagueId">The league to revoke the role for.</param>
        /// <param name="role">The role to revoke.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<string?> RevokeRoleAsync(string? executorPlayerName, ReadOnlyMemory<char> targetPlayerName, long leagueId, LeagueRole role, ReadOnlyMemory<char> notes, CancellationToken cancellationToken);

        /// <summary>
        /// Refreshes the role data for a specified <paramref name="leagueId"/> + <paramref name="role"/> combination..
        /// </summary>
        /// <param name="leagueId">The league to get data about.</param>
        /// <param name="role">The role to get data about.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>A task that indicates whether the data refresh was successful.</returns>
        Task<bool> RefreshAsync(long leagueId, LeagueRole role, CancellationToken cancellationToken);
    }
}
