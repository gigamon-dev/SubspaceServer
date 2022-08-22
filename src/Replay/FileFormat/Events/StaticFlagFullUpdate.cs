using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    /// <summary>
    /// A full ownership update (all flags). 
    /// Normally, at the start of a recording.
    /// Or, if the flag game is reset mid-recording.
    /// </summary>
    /// <remarks>
    /// This event is a variable length, based on the flag count.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StaticFlagFullUpdate
    {
        #region Static members

        public static readonly int Length;

        static StaticFlagFullUpdate()
        {
            Length = Marshal.SizeOf(typeof(StaticFlagFullUpdate));
        }

        #endregion

        public EventHeader Header;
        private short flagCount;
        // Followed by an array of short with the length of flagCount which contains the owner freq.

        public StaticFlagFullUpdate(ServerTick ticks, short flagCount)
        {
            Header = new(ticks, EventType.StaticFlagFullUpdate);
            this.flagCount = LittleEndianConverter.Convert(flagCount);
        }

        #region Helper properties

        public short FlagCount => LittleEndianConverter.Convert(flagCount);

        #endregion
    }
}
