using Microsoft.Extensions.ObjectPool;
using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace SS.Core
{
    /// <summary>
    /// Interface for a service that provides access to a pool of <see cref="StringBuilder"/>s.
    /// </summary>
    internal interface IStringBuilderPoolProvider
    {
        /// <summary>
        /// Gets a pool of StringBuilder objects.
        /// </summary>
        ObjectPool<StringBuilder> StringBuilderPool { get; }
    }

    /// <summary>
    /// An interpolated string handler that uses a <see cref="System.Text.StringBuilder"/> as the backing store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="System.Text.StringBuilder"/> object is aquired from a pool, so the object passed into the constructor must implement <see cref="IStringBuilderPoolProvider"/>.
    /// Since it's operating on a <see cref="System.Text.StringBuilder"/>, it simply uses a wrapped <see cref="StringBuilder.AppendInterpolatedStringHandler"/>.
    /// </para>
    /// <para>
    /// A method that uses a <see cref="StringBuilderBackedInterpolatedStringHandler"/> can access the <see cref="System.Text.StringBuilder"/> with the <see cref="StringBuilder"/> property.
    /// When done, the method should call <see cref="Clear"/> to return the <see cref="System.Text.StringBuilder"/> to the pool.
    /// </para>
    /// </remarks>
    [InterpolatedStringHandler]
    public struct StringBuilderBackedInterpolatedStringHandler
    {
        private readonly IStringBuilderPoolProvider _stringBuilderPoolProvider;
        private StringBuilder _stringBuilder;
        private StringBuilder.AppendInterpolatedStringHandler _wrappedHandler;

        public StringBuilderBackedInterpolatedStringHandler(int literalLength, int formatCount, object stringBuilderPoolProvider)
            : this(literalLength, formatCount, (stringBuilderPoolProvider ?? throw new ArgumentNullException(nameof(stringBuilderPoolProvider))) as IStringBuilderPoolProvider, null)
        {
        }

        public StringBuilderBackedInterpolatedStringHandler(int literalLength, int formatCount, object stringBuilderPoolProvider, IFormatProvider provider)
            : this(literalLength, formatCount, (stringBuilderPoolProvider ?? throw new ArgumentNullException(nameof(stringBuilderPoolProvider))) as IStringBuilderPoolProvider, provider)
        {
        }

        internal StringBuilderBackedInterpolatedStringHandler(int literalLength, int formatCount, IStringBuilderPoolProvider stringBuilderPoolProvider, IFormatProvider provider)
        {
            _stringBuilderPoolProvider = stringBuilderPoolProvider ?? throw new ArgumentNullException(nameof(stringBuilderPoolProvider));
            _stringBuilder = _stringBuilderPoolProvider.StringBuilderPool.Get();
            _wrappedHandler = new StringBuilder.AppendInterpolatedStringHandler(literalLength, formatCount, _stringBuilder, provider);
        }

        public void AppendLiteral(string value)
        {
            _wrappedHandler.AppendLiteral(value);
        }

        #region AppendFormatted

        #region AppendFormatted T

        public void AppendFormatted<T>(T value) => _wrappedHandler.AppendFormatted<T>(value);

        public void AppendFormatted<T>(T value, string format) => _wrappedHandler.AppendFormatted<T>(value, format);

        public void AppendFormatted<T>(T value, int alignment) => _wrappedHandler.AppendFormatted<T>(value, alignment);

        public void AppendFormatted<T>(T value, int alignment, string format) => _wrappedHandler.AppendFormatted<T>(value, alignment, format);

        #endregion

        #region AppendFormatted ReadOnlySpan<char>

        public void AppendFormatted(ReadOnlySpan<char> value) => _wrappedHandler.AppendFormatted(value);

        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string format = null) => _wrappedHandler.AppendFormatted(value, alignment, format);

        #endregion

        #region AppendFormatted string

        public void AppendFormatted(string value) => _wrappedHandler.AppendFormatted(value);

        public void AppendFormatted(string value, int alignment = 0, string format = null) => _wrappedHandler.AppendFormatted(value, alignment, format);

        #endregion

        #region AppendFormatted object

        public void AppendFormatted(object value, int alignment = 0, string format = null) => _wrappedHandler.AppendFormatted(value, alignment, format);

        #endregion

        #endregion

        /// <summary>
        /// Gets the <see cref="System.Text.StringBuilder"/>.
        /// </summary>
        public StringBuilder StringBuilder => _stringBuilder;

        /// <summary>
        /// Returns the <see cref="StringBuilder"/> to the pool.
        /// </summary>
        public void Clear()
        {
            if (_stringBuilder is not null)
            {
                _stringBuilderPoolProvider.StringBuilderPool.Return(_stringBuilder);
                _stringBuilder = null;
                _wrappedHandler = default;
            }
        }

        /// <summary>
        /// Copies the contents of the <see cref="StringBuilder"/> into <paramref name="destination"/> and returns it to the pool.
        /// </summary>
        /// <param name="destination">The destination to copy to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="destination"/> was null.</exception>
        public void CopyToAndClear(StringBuilder destination)
        {
            if (destination is null)
                throw new ArgumentNullException(nameof(destination));

            if (_stringBuilder is not null)
            {
                destination.Append(_stringBuilder);
                _stringBuilderPoolProvider.StringBuilderPool.Return(_stringBuilder);
                _stringBuilder = null;
                _wrappedHandler = default;
            }
        }
    }
}
