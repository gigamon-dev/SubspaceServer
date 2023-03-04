using SS.Utilities;
using System;

namespace SS.Core.Map
{
    /// <summary>
    /// Represents x and y coordinates on a map.
    /// </summary>
    public readonly struct MapCoordinate : IEquatable<MapCoordinate>, IComparable<MapCoordinate>
    {
        /// <summary>
        /// Special coordinate for moving carryable flags outside of the map (to fake neuting).
        /// </summary>
        public static readonly MapCoordinate FlagOutsideMapCoordinate = new() { X = -1, Y = -1 };

        public MapCoordinate(short x, short y)
        {
            if (x < 0 || x > 1023)
                throw new ArgumentOutOfRangeException(nameof(x));

            if (y < 0 || y > 1023)
                throw new ArgumentOutOfRangeException(nameof(y));

            X = x;
            Y = y;
        }

        public short X { get; init; }

        public short Y { get; init; }

        public static bool operator ==(MapCoordinate left, MapCoordinate right)
        {
            return left.X == right.X && left.Y == right.Y;
        }

        public static bool operator !=(MapCoordinate left, MapCoordinate right)
        {
            return !(left == right);
        }

        public static implicit operator MapCoordinate((short X, short Y) coordTuple) => new(coordTuple.X, coordTuple.Y);

        /// <summary>
        /// Converts a span representation of a <see cref="MapCoordinate"/>.
        /// </summary>
        /// <param name="input">
        /// A span containing the characters to convert.
        /// The format should be x,y.
        /// </param>
        /// <param name="mapCoordinate">
        /// When this method returns, contains the <see cref="MapCoordinate"/> equivalent of the <paramref name="input"/> string, if the conversion succeeded. 
        /// Otherwise, contains the <see langword="default"/>, <see cref="MapCoordinate"/>.</param>
        /// <returns><see langword="true"/> if <paramref name="input"/> was converted succesfully; otherwise <see langword="false"/></returns>
        public static bool TryParse(ReadOnlySpan<char> input, out MapCoordinate mapCoordinate)
        {
            ReadOnlySpan<char> token;
            if ((token = input.GetToken(',', out input)).Length <= 0
                || !short.TryParse(token, out short x)
                || x < 0 
                || x > 1023)
            {
                mapCoordinate = default;
                return false;
            }

            input = input.TrimStart(',');
            if (!short.TryParse(input, out short y)
                || y < 0
                || y > 1023)
            {
                mapCoordinate = default;
                return false;
            }

            mapCoordinate = new MapCoordinate(x, y);
            return true;
        }

        public void Deconstruct(out short x, out short y)
        {
            x = X;
            y = Y;
        }

        public override int GetHashCode()
        {
            return (((ushort)X) << 10) | ((ushort)Y);
        }

        public override bool Equals(object obj)
        {
            return obj is MapCoordinate coordinate && Equals(coordinate);
        }

        #region IEquatable<MapCoordinate> Members

        public bool Equals(MapCoordinate other)
        {
            return X == other.X && Y == other.Y;
        }

        #endregion

        #region IComparable<MapCoordinate> Members

        public int CompareTo(MapCoordinate other)
        {
            // this is a guess of how tiles are ordered 
            // i plan to hopefully use this for figuring out indexes of turf flags
            // e.g. dump the coords of flags into a sorted list, resulting in the index when all flags are loaded
            int retVal = Y.CompareTo(other.Y);
            if (retVal != 0)
                return retVal;

            return X.CompareTo(other.X);
        }

        #endregion
    }
}
