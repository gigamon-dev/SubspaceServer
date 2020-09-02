using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.Map
{
    /// <summary>
    /// wraps x and y coordinates of a map tile
    /// </summary>
    public readonly struct MapCoordinate : IEquatable<MapCoordinate>, IComparable<MapCoordinate>
    {
        private readonly short _xCoord;
        private readonly short _yCoord;

        public MapCoordinate(short xCoord, short yCoord)
        {
            if (xCoord < 0 || xCoord > 1023)
                throw new ArgumentOutOfRangeException("xCoord");

            if (yCoord < 0 || yCoord > 1023)
                throw new ArgumentOutOfRangeException("yCoord");

            _xCoord = xCoord;
            _yCoord = yCoord;
        }

        public short X
        {
            get { return _xCoord; }
        }

        public short Y
        {
            get { return _yCoord; }
        }

        public override int GetHashCode()
        {
            return (((ushort)_xCoord) << 10) | ((ushort)_yCoord);
        }

        public override bool Equals(object obj)
        {
            if (obj is MapCoordinate)
                return Equals((MapCoordinate)obj);

            return false;
        }

        #region IEquatable<MapCoordinate> Members

        public bool Equals(MapCoordinate other)
        {
            return _xCoord == other._xCoord && _yCoord == other._yCoord;
        }

        #endregion

        #region IComparable<MapCoordinate> Members

        public int CompareTo(MapCoordinate other)
        {
            // this is a guess of how tiles are ordered 
            // i plan to hopefully use this for figuring out indexes of turf flags
            // e.g. dump the coords of flags into a sorted list, resulting in the index when all flags are loaded
            int retVal = this._yCoord.CompareTo(other._yCoord);
            if (retVal != 0)
                return retVal;

            return this._xCoord.CompareTo(other._xCoord);
        }

        #endregion
    }
}
