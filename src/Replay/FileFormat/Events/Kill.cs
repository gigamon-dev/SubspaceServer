using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Kill
    {
        #region Static members

        public static readonly int Length;

        static Kill()
        {
            Length = Marshal.SizeOf(typeof(Kill));
        }

        #endregion

        public EventHeader Header;
        private short killer;
        private short killed;
        private short points;
        private short flags;

        public Kill(ServerTick ticks, short killer, short killed, short points, short flags)
        {
            Header = new(ticks, EventType.Kill);
            this.killer = LittleEndianConverter.Convert(killer);
            this.killed = LittleEndianConverter.Convert(killed);
            this.points = LittleEndianConverter.Convert(points);
            this.flags = LittleEndianConverter.Convert(flags);
        }

        #region Helper properties

        public short Killer
        {
            get => LittleEndianConverter.Convert(killer);
            set => killer = LittleEndianConverter.Convert(value);
        }

        public short Killed
        {
            get => LittleEndianConverter.Convert(killed);
            set => killed = LittleEndianConverter.Convert(value);
        }

        public short Points
        {
            get => LittleEndianConverter.Convert(points);
            set => points = LittleEndianConverter.Convert(value);
        }

        public short Flags
        {
            get => LittleEndianConverter.Convert(flags);
            set => flags = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
