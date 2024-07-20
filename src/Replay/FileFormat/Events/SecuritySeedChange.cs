using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SecuritySeedChange(ServerTick ticks, uint greenSeed, uint doorSeed, uint timeDelta)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<SecuritySeedChange>();

        #endregion

        public EventHeader Header = new(ticks, EventType.SecuritySeedChange);
        private uint greenSeed = LittleEndianConverter.Convert(greenSeed);
        private uint doorSeed = LittleEndianConverter.Convert(doorSeed);
        private uint timeDelta = LittleEndianConverter.Convert(timeDelta);

		#region Helper properties

		public uint GreenSeed
		{
			readonly get => LittleEndianConverter.Convert(greenSeed);
			set => greenSeed = LittleEndianConverter.Convert(value);
		}

		public uint DoorSeed
		{
			readonly get => LittleEndianConverter.Convert(doorSeed);
			set => doorSeed = LittleEndianConverter.Convert(value);
		}

		/// <summary>
		/// How long ago (in ticks) were the seeds changed, relative to the event time.
		/// </summary>
		public uint TimeDelta
		{
			readonly get => LittleEndianConverter.Convert(timeDelta);
			set => timeDelta = LittleEndianConverter.Convert(value);
		}

		#endregion
	}
}
