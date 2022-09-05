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
        private uint timeDelta;

        public SecuritySeedChange(ServerTick ticks, uint greenSeed, uint doorSeed, uint timeDelta)
        {
            Header = new(ticks, EventType.SecuritySeedChange);
            this.greenSeed = LittleEndianConverter.Convert(greenSeed);
            this.doorSeed = LittleEndianConverter.Convert(doorSeed);
            this.timeDelta = LittleEndianConverter.Convert(timeDelta);
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

        /// <summary>
        /// How long ago (in ticks) were the seeds changed, relative to the event time.
        /// </summary>
        public uint TimeDelta
        {
            get => LittleEndianConverter.Convert(timeDelta);
            set => timeDelta = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
