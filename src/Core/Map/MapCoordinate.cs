using System;

namespace SS.Core.Map
{
    /// <summary>
    /// Represents x and y coordinates on a map.
    /// </summary>
    public readonly struct MapCoordinate : IEquatable<MapCoordinate>, IComparable<MapCoordinate>
    {
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
