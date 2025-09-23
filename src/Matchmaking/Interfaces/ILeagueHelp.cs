using SS.Core;
namespace SS.Matchmaking.Interfaces
{
    public interface ILeagueHelp : IComponentInterface
    {
        /// <summary>
        /// Prints help information.
        /// </summary>
        /// <param name="player">The player to send the help information to.</param>
        void PrintHelp(Player player);
    }
}
