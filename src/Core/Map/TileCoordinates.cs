using System;
using System.Diagnostics.CodeAnalysis;

namespace SS.Core.Map
{
    /// <summary>
    /// The (x,y) coordinates of a tile on a map.
    /// </summary>
    /// <remarks>
    /// A tile position coordinate has a range of [0..1023].
    /// This differs from the pixel coordinates that represent a player's position, since a tile is 16 pixels.
    /// </remarks>
    public readonly struct TileCoordinates : IEquatable<TileCoordinates>, IComparable<TileCoordinates>, ISpanParsable<TileCoordinates>, ISpanFormattable
    {
        /// <summary>
        /// Special coordinates for moving carriable flags outside of the map (to fake neuting).
        /// </summary>
        public static readonly TileCoordinates FlagOutsideMapCoordinates = new() { X = -1, Y = -1 };

        /// <summary>
        /// Initializes a new <see cref="TileCoordinates"/> instance.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        public TileCoordinates(short x, short y)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(x);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(x, 1023);
            ArgumentOutOfRangeException.ThrowIfNegative(y);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(y, 1023);

            X = x;
            Y = y;
        }

        /// <summary>
        /// The x-coordinate.
        /// </summary>
        public short X { get; init; }

        /// <summary>
        /// The y-coordinate.
        /// </summary>
        public short Y { get; init; }

        /// <summary>
        /// Whether <see cref="X"/> and <see cref="Y"/> represent a valid position on a map.
        /// </summary>
        /// <remarks>
        /// <see cref="FlagOutsideMapCoordinates"/> is an example of a position that is not valid.
        /// </remarks>
        public bool IsValid => (X >= 0) && (X <= 1023) && (Y >= 0) && (Y <= 1023);

        public void Deconstruct(out short x, out short y)
        {
            x = X;
            y = Y;
        }

        #region Equality

        public override int GetHashCode()
        {
            return (((ushort)X) << 10) | ((ushort)Y);
        }

        public override bool Equals([NotNullWhen(true)] object? obj)
        {
            return obj is TileCoordinates other && Equals(other);
        }

        public static bool operator ==(TileCoordinates left, TileCoordinates right)
        {
            return left.X == right.X && left.Y == right.Y;
        }

        public static bool operator !=(TileCoordinates left, TileCoordinates right)
        {
            return !(left == right);
        }

        public bool Equals(TileCoordinates other)
        {
            return X == other.X && Y == other.Y;
        }

        #endregion

        #region Comparison

        public int CompareTo(TileCoordinates other)
        {
            // NOTICE: This logic is used to determine the Ids of static, turf-style flags.

            int retVal = Y.CompareTo(other.Y);
            if (retVal != 0)
                return retVal;

            return X.CompareTo(other.X);
        }

        #endregion

        #region Parsing

        /// <summary>
        /// Tries to parse a string into a <see cref="TileCoordinates"/>.
        /// </summary>
        /// <param name="source">The string to parse.</param>
        /// <param name="result">
        /// When this method returns, the <see cref="TileCoordinates"/> represented by <paramref name="source"/>, if the conversion succeeded. 
        /// Otherwise, contains the <see langword="default"/>, <see cref="TileCoordinates"/>.</param>
        /// <returns><see langword="true"/> if <paramref name="source"/> was able to be parsed as a <see cref="TileCoordinates"/>; otherwise <see langword="false"/>.</returns>
        public static bool TryParse([NotNullWhen(true)] string? source, out TileCoordinates result) => TryParse(source.AsSpan(), out result);

        /// <summary>
        /// Tries to parse a span of characters into a <see cref="TileCoordinates"/>.
        /// </summary>
        /// <param name="source">The span of characters to parse.</param>
        /// <param name="result">
        /// When this method returns, the <see cref="TileCoordinates"/> represented by <paramref name="source"/>, if the conversion succeeded. 
        /// Otherwise, contains the <see langword="default"/>, <see cref="TileCoordinates"/>.</param>
        /// <returns><see langword="true"/> if <paramref name="source"/> was able to be parsed as a <see cref="TileCoordinates"/>; otherwise <see langword="false"/>.</returns>
        public static bool TryParse(ReadOnlySpan<char> source, out TileCoordinates result) => TryParse(source, out result, false);

        static bool IParsable<TileCoordinates>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out TileCoordinates result) => TryParse(s.AsSpan(), out result);

        static bool ISpanParsable<TileCoordinates>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out TileCoordinates result) => TryParse(s, out result);

        private static bool TryParse(ReadOnlySpan<char> source, out TileCoordinates result, bool throwOnError)
        {
            // Trim white-space.
            source = source.Trim();
            if (source.IsEmpty)
            {
                if (throwOnError)
                    throw new FormatException();

                result = default;
                return false;
            }

            // Remove parenthesis if a pair exists.
            if (source[0] == '(' && source[^1] == ')')
            {
                source = source[1..^1];
            }

            // Split
            Span<Range> ranges = stackalloc Range[2];
            int numRanges = source.Split(ranges, ',', StringSplitOptions.None);
            if (numRanges != 2)
            {
                if (throwOnError)
                    throw new FormatException();

                result = default;
                return false;
            }

            // Parse the x-coordinate.
            ReadOnlySpan<char> coordinate = source[ranges[0]];
            if (coordinate.Length <= 0 || !short.TryParse(coordinate, out short x) || x < 0 || x > 1023)
            {
                if (throwOnError)
                    throw new FormatException();

                result = default;
                return false;
            }

            // Parse the y-coordinate.
            coordinate = source[ranges[1]];
            if (coordinate.Length <= 0 || !short.TryParse(coordinate, out short y) || y < 0 || y > 1023)
            {
                if (throwOnError)
                    throw new FormatException();

                result = default;
                return false;
            }

            result = new TileCoordinates(x, y);
            return true;
        }

        /// <summary>
        /// Parses a string into a <see cref="TileCoordinates"/>.
        /// </summary>
        /// <param name="source">The string to parse.</param>
        /// <returns>The resulting <see cref="TileCoordinates"/>.</returns>
        /// <exception cref="FormatException"><paramref name="source"/> was not a valid <see cref="TileCoordinates"/>.</exception>
        public static TileCoordinates Parse(string source) => Parse(source.AsSpan());

        /// <summary>
        /// Parses a span of characters into a <see cref="TileCoordinates"/>.
        /// </summary>
        /// <param name="source">The span of characters to parse.</param>
        /// <returns>The resulting <see cref="TileCoordinates"/>.</returns>
        /// <exception cref="FormatException"><paramref name="source"/> was not a valid <see cref="TileCoordinates"/>.</exception>
        public static TileCoordinates Parse(ReadOnlySpan<char> source)
        {
            _ = TryParse(source, out TileCoordinates result, true);
            return result;
        }

        static TileCoordinates IParsable<TileCoordinates>.Parse(string s, IFormatProvider? provider) => Parse(s.AsSpan());

        static TileCoordinates ISpanParsable<TileCoordinates>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

        #endregion

        #region Formatting

        /// <summary>
        /// Tries to format the current <see cref="TileCoordinates"/> into the provided span of characters.
        /// </summary>
        /// <param name="destination"></param>
        /// <param name="charsWritten"></param>
        /// <returns></returns>
        public bool TryFormat(Span<char> destination, out int charsWritten) => TryFormat(destination, out charsWritten, []);

        /// <summary>
        /// Tries to format the current <see cref="TileCoordinates"/> into the provided span of characters.
        /// </summary>
        /// <param name="destination"></param>
        /// <param name="charsWritten"></param>
        /// <param name="format"></param>
        /// <returns></returns>
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format)
        {
            if (!format.IsEmpty && format.Equals("g", StringComparison.OrdinalIgnoreCase))
            {
                // General short format (no parenthesis)
                return destination.TryWrite($"{X},{Y}", out charsWritten);
            }
            else
            {
                // General long format (has parenthesis)
                return destination.TryWrite($"({X},{Y})", out charsWritten);
            }
        }

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) => TryFormat(destination, out charsWritten, format);

        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            Span<char> chars = stackalloc char["(####,####)".Length];
            TryFormat(chars, out int charsWritten);
            return chars[..charsWritten].ToString();
        }

        #endregion
    }
}
