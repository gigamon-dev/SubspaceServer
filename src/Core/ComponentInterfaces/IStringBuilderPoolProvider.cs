using Microsoft.Extensions.ObjectPool;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    internal interface IStringBuilderPoolProvider
    {
        /// <summary>
        /// Pool of StringBuilder objects.
        /// </summary>
        /// <remarks>
        /// Only for use by <see cref="SendMessageInterpolatedStringHandler"/>.
        /// </remarks>
        ObjectPool<StringBuilder> StringBuilderPool { get; }
    }
}
