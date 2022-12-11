using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Delegate for when a file upload completes successfully or unsuccessfully.
    /// </summary>
    /// <typeparam name="T">The type of state object.</typeparam>
    /// <param name="filename">
    /// On success, the name of the uploaded file.
    /// On failure, <see langword="null"/>.
    /// </param>
    /// <param name="arg">The state object.</param>
    public delegate void FileUploadedDelegate<T>(string filename, T arg);

    public interface IFileTransfer : IComponentInterface
    {
        /// <summary>
        /// Sends a file to a player.
        /// </summary>
        /// <param name="player">The player to send file to.</param>
        /// <param name="path">The path and file name.</param>
        /// <param name="filename">The name of the uploaded file.</param>
        /// <param name="deleteAfter">Whether the file should be deleted after it is sent.</param>
        /// <returns></returns>
        bool SendFile(Player player, string path, string filename, bool deleteAfter);

        /// <summary>
        /// Requests a file from a player.
        /// </summary>
        /// <typeparam name="T">Type of state argument for the <paramref name="uploaded"/> delegate.</typeparam>
        /// <param name="player">The player to request a file from.</param>
        /// <param name="path">The path and file name.</param>
        /// <param name="uploaded">
        /// Delegate to call when the transfer is complete.
        /// </param>
        /// <param name="arg">Argument to pass to <paramref name="uploaded"/> for representing the state.</param>
        /// <returns>
        /// True if the file was requested.
        /// False on failure to request, meaning <paramref name="uploaded"/> will not be called, and any cleanup should be done immediately.
        /// </returns>
        bool RequestFile<T>(Player player, string path, FileUploadedDelegate<T> uploaded, T arg);

        /// <summary>
        /// Gets the server-side current working directory for a player.
        /// The server-side current working directory is normally used for administrative purposes.
        /// </summary>
        /// <param name="player">The player to get the working directory for.</param>
        /// <returns>The working directory.</returns>
        string GetWorkingDirectory(Player player);

        /// <summary>
        /// Sets the current working directory on the server for a player.
        /// The server-side current working directory is normally used for administrative purposes.
        /// </summary>
        /// <param name="player">The player to get the working directory for.</param>
        /// <param name="path">The path to set the working directory to.</param>
        void SetWorkingDirectory(Player player, string path);
    }
}
