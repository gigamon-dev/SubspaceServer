using System;
using System.Net;

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
        /// <param name="p">The player encrypting data for.</param>
        /// <param name="data">The buffer to encrypt.  This is guaranteed to be larger than <paramref name="len"/> by at least 4 bytes.</param>
        /// <param name="len">The # of bytes to encrypt within <paramref name="data"/>.</param>
        /// <returns>length of the resulting data</returns>
        int Encrypt(Player p, Span<byte> data, int len);

        /// <summary>
        /// Decrypts data for a player.
        /// </summary>
        /// <remarks>Data is encrypted in place.</remarks>
        /// <param name="p">The player decrypting data for.</param>
        /// <param name="data">The buffer to decrypt.  This is guaranteed to be larger than <paramref name="len"/> by at least 4 bytes.</param>
        /// <param name="len">The # of bytes to decrypt within <paramref name="data"/>.</param>
        /// <returns>length of the resulting data</returns>
        int Decrypt(Player p, Span<byte> data, int len);

        /// <summary>
        /// Called when encryption info for a player is no longer needed (e.g. the player disconnects).
        /// This allows for cleanup to be done, if needed.
        /// </summary>
        /// <param name="p">The player to cleanup for.</param>
        void Void(Player p);
    }

    /// <summary>
    /// Interface with special methods for encryption modules to use to access the network module.
    /// </summary>
    public interface INetworkEncryption : IComponentInterface
    {
        // TODO: instead of ConnectionInitCallback, create a "pipeline" of possible handlers and call them in sequence until one handles it
        //delegate bool ConnectionInitHandler(IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld);
        //void AddConnectionInitHandler(ConnectionInitHandler handler);
        //void RemoveConnectionInitHandler(ConnectionInitHandler handler);

        /// <summary>
        /// Sends data immediately without encryption and without buffering.
        /// </summary>
        /// <param name="remoteEndpoint"></param>
        /// <param name="data"></param>
        /// <param name="ld"></param>
        void ReallyRawSend(IPEndPoint remoteEndpoint, ReadOnlySpan<byte> data, ListenData ld);

        /// <summary>
        /// Gets a player object for a new connection.
        /// </summary>
        /// <param name="clientType"></param>
        /// <param name="remoteEndpoint"></param>
        /// <param name="iEncryptName"></param>
        /// <param name="ld"></param>
        /// <returns></returns>
        Player NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, string iEncryptName, ListenData ld);
    }
}
