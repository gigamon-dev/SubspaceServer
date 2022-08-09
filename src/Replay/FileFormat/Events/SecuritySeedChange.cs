using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SecuritySeedChange
    {
        #region Static members

        public static readonly int Length;

        static SecuritySeedChange()
        {
            Length = Marshal.SizeOf(typeof(SecuritySeedChange));
        }

        #endregion

        public EventHeader Header;
        private uint greenSeed;
        private uint doorSeed;
        private uint timestamp;

        public SecuritySeedChange(ServerTick ticks, uint greenSeed, uint doorSeed, uint timestamp)
        {
            Header = new(ticks, EventType.SecuritySeedChange);
            this.greenSeed = LittleEndianConverter.Convert(greenSeed);
            this.doorSeed = LittleEndianConverter.Convert(doorSeed);
            this.timestamp = LittleEndianConverter.Convert(timestamp);
        }

        #region Helper properties

        public uint GreenSeed
        {
            get => LittleEndianConverter.Convert(greenSeed);
            set => greenSeed = LittleEndianConverter.Convert(value);
        }

        public uint DoorSeed
        {
            get => LittleEndianConverter.Convert(doorSeed);
            set => doorSeed = LittleEndianConverter.Convert(value);
        }

        public uint Timestamp
        {
            get => LittleEndianConverter.Convert(timestamp);
            set => timestamp = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
