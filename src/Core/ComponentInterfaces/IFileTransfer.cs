using System;
using System.IO;
using System.Threading.Tasks;

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
    public delegate void FileUploadedDelegate<T>(string? filename, T arg);

    /// <summary>
    /// Interface for a service that provides functionality to transfer files between players and the server (to and from).
    /// </summary>
    public interface IFileTransfer : IComponentInterface
    {
        /// <summary>
        /// Sends a file to a player.
        /// </summary>
        /// <remarks>
        /// This method must be called on the mainloop thread.
        /// </remarks>
        /// <param name="player">The player to send file to.</param>
        /// <param name="path">The path of the file to send.</param>
        /// <param name="filename">The name of the uploaded file.</param>
        /// <param name="deleteAfter">Whether the file at <paramref name="path"/> should be deleted after it is sent.</param>
        /// <returns><see langword="true"/> if the file was queued to be sent. Otherwise, <see langword="false"/>.</returns>
        Task<bool> SendFileAsync(Player player, string path, ReadOnlyMemory<char> filename, bool deleteAfter);

        /// <summary>
        /// Sends a file to a player from a <see cref="Stream"/>.
        /// </summary>
        /// <remarks>
        /// This method must be called on the mainloop thread.
        /// </remarks>
        /// <param name="player">The player to send file to.</param>
        /// <param name="stream">The stream containing the data to send as a file.</param>
        /// <param name="filename">The name of the uploaded file.</param>
        /// <returns><see langword="true"/> if the file was queued to be sent. Otherwise, <see langword="false"/>.</returns>
        Task<bool> SendFileAsync(Player player, Stream stream, ReadOnlyMemory<char> filename);

        /// <summary>
        /// Sends a file to a player from a <see cref="MemoryStream"/>.
        /// </summary>
        /// <remarks>
        /// The <paramref name="stream"/> is limited to <see cref="MemoryStream"/> because this method executes synchronously.
        /// For other types of streams, use <see cref="SendFileAsync(Player, Stream, ReadOnlyMemory{char})"/>.
        /// </remarks>
        /// <param name="player">The player to send the file to.</param>
        /// <param name="stream">The stream containing the file data. The stream will be closed/disposed.</param>
        /// <param name="filename">The name of the file.</param>
        /// <returns><see langword="true"/> if the file was queued to be sent. Otherwise, <see langword="false"/>.</returns>
        bool SendFile(Player player, MemoryStream stream, ReadOnlySpan<char> filename);

        /// <summary>
        /// Requests a file from a player.
        /// </summary>
        /// <param name="player">The player to request a file from.</param>
        /// <param name="clientPath">The path of the file on the client to request.</param>
        /// <returns>
        /// The path of the temporary file. <see langword="null"/> if the transfer failed or was cancelled.
        /// </returns>
        Task<string?> RequestFileAsync(Player player, ReadOnlyMemory<char> clientPath);

        /// <summary>
        /// Gets the server-side current working directory for a player.
        /// The server-side current working directory is normally used for administrative purposes.
        /// </summary>
        /// <param name="player">The player to get the working directory for.</param>
        /// <returns>The working directory.</returns>
        string? GetWorkingDirectory(Player player);

        /// <summary>
        /// Sets the current working directory on the server for a player.
        /// The server-side current working directory is normally used for administrative purposes.
        /// </summary>
        /// <param name="player">The player to get the working directory for.</param>
        /// <param name="path">The path to set the working directory to.</param>
        void SetWorkingDirectory(Player player, string path);
    }
}
