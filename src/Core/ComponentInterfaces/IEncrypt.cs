using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that can encrypt and decrypt data for players.
    /// </summary>
    public interface IEncrypt : IComponentInterface
    {
        /// <summary>
        /// Encrypts data for a player.
        /// </summary>
        /// <remarks>Data is encrypted in place.</remarks>
        /// <param name="player">The player encrypting data for.</param>
        /// <param name="data">The buffer to encrypt.  This is guaranteed to be larger than <paramref name="length"/> by at least 4 bytes.</param>
        /// <param name="length">The # of bytes to encrypt within <paramref name="data"/>.</param>
        /// <returns>length of the resulting data</returns>
        int Encrypt(Player player, Span<byte> data, int length);

        /// <summary>
        /// Decrypts data for a player.
        /// </summary>
        /// <remarks>Data is encrypted in place.</remarks>
        /// <param name="player">The player decrypting data for.</param>
        /// <param name="data">The buffer to decrypt.  This is guaranteed to be larger than <paramref name="length"/> by at least 4 bytes.</param>
        /// <param name="length">The # of bytes to decrypt within <paramref name="data"/>.</param>
        /// <returns>length of the resulting data</returns>
        int Decrypt(Player player, Span<byte> data, int length);

        /// <summary>
        /// Called when encryption info for a player is no longer needed (e.g. the player disconnects).
        /// This allows for cleanup to be done, if needed.
        /// </summary>
        /// <param name="player">The player to cleanup for.</param>
        void Void(Player player);
    }
}
