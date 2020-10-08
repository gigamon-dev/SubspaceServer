using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for pseudo-random number generator methods.
    /// </summary>
    public interface IPrng : IComponentInterface
    {
        /// <summary>
        /// Fills a buffer with cryptographically secure random bytes.
        /// </summary>
        /// <param name="data">The buffer to fill.</param>
        void GoodFillBuffer(Span<byte> data);

        /// <summary>
        /// Fills a buffer with random bytes.
        /// </summary>
        /// <param name="data">The buffer to fill.</param>
        void FillBuffer(Span<byte> data);

        /// <summary>
        /// Gets a random number between two inclusive bounds.
        /// </summary>
        /// <param name="start">The lower bound.</param>
        /// <param name="end">The upper bound.</param>
        /// <returns>A random number in [<paramref name="start"/> - <paramref name="end"/>]</returns>
        int Number(int start, int end);

        /// <summary>
        /// Gets a random 32-bit integer.
        /// </summary>
        /// <returns>A random 32-bit unsigned integer.</returns>
        uint Get32();

        /// <summary>
        /// Gets a number from 0 to <see cref="Constants.RandMax"/>.
        /// </summary>
        /// <returns>A random integer in [0, <see cref="Constants.RandMax"/>]</returns>
        int Rand();

        /// <summary>
        /// Gets a random floating point number greater or equal to 0 and less than 1.
        /// </summary>
        /// <returns>A floating point value in [0.0, 1.0).</returns>
        double Uniform();
    }
}
