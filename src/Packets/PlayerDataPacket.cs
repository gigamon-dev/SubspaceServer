using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public readonly struct PlayerDataPacket
    {
        static PlayerDataPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            pktype = locationBuilder.CreateByteDataLocation();
            ship = locationBuilder.CreateSByteDataLocation();
            acceptaudio = locationBuilder.CreateByteDataLocation();
            name = locationBuilder.CreateDataLocation(20);
            squad = locationBuilder.CreateDataLocation(20);
            killpoints = locationBuilder.CreateInt32DataLocation();
            flagpoints = locationBuilder.CreateInt32DataLocation();
            pid = locationBuilder.CreateInt16DataLocation();
            freq = locationBuilder.CreateInt16DataLocation();
            wins = locationBuilder.CreateInt16DataLocation();
            losses = locationBuilder.CreateInt16DataLocation();
            attachedto = locationBuilder.CreateInt16DataLocation();
            flagscarried = locationBuilder.CreateInt16DataLocation();
            miscbits = locationBuilder.CreateByteDataLocation();
            Length = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation pktype;
        private static readonly SByteDataLocation ship;
        private static readonly ByteDataLocation acceptaudio;
        private static readonly DataLocation name;
        private static readonly DataLocation squad;
        private static readonly Int32DataLocation killpoints;
        private static readonly Int32DataLocation flagpoints;
        private static readonly Int16DataLocation pid;
        private static readonly Int16DataLocation freq;
        private static readonly Int16DataLocation wins;
        private static readonly Int16DataLocation losses;
        private static readonly Int16DataLocation attachedto;
        private static readonly Int16DataLocation flagscarried;
        private static readonly ByteDataLocation miscbits;
        public static readonly int Length;

        private readonly byte[] data;

        public PlayerDataPacket(byte[] data)
        {
            this.data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public byte[] Bytes
        {
            get { return data; }
        }

        public byte PkType
        {
            get { return pktype.GetValue(data); }
            set { pktype.SetValue(data, value); }
        }

        public sbyte Ship
        {
            get { return ship.GetValue(data); }
            set { ship.SetValue(data, value); }
        }

        public byte AcceptAudio
        {
            get { return acceptaudio.GetValue(data); }
            set { acceptaudio.SetValue(data, value); }
        }

        public string Name
        {
            get { return Encoding.ASCII.GetString(data, name.ByteOffset, name.NumBytes); }
            set
            {
                if (string.IsNullOrEmpty(value))
                    throw new ArgumentException("cannot be null or emmpty", "value");
                
                int fieldLength = name.NumBytes;
                if (value.Length < fieldLength)
                {
                    int bytesWritten = Encoding.ASCII.GetBytes(value, 0, value.Length, data, name.ByteOffset);
                    while (bytesWritten < fieldLength)
                    {
                        data[name.ByteOffset + bytesWritten] = 0;
                        bytesWritten++;
                    }
                }
                else
                {
                    Encoding.ASCII.GetBytes(value, 0, fieldLength, data, name.ByteOffset);
                }
            }
        }

        public string Squad
        {
            get { return Encoding.ASCII.GetString(data, squad.ByteOffset, squad.NumBytes); }
            set
            {
                if (value == null)
                    value = string.Empty;
                
                int fieldLength = squad.NumBytes;
                if (value.Length < fieldLength)
                {
                    int bytesWritten = Encoding.ASCII.GetBytes(value, 0, value.Length, data, squad.ByteOffset);
                    while (bytesWritten < fieldLength)
                    {
                        data[squad.ByteOffset + bytesWritten] = 0;
                        bytesWritten++;
                    }
                }
                else
                {
                    Encoding.ASCII.GetBytes(value, 0, fieldLength, data, squad.ByteOffset);
                }
            }
        }

        public int KillPoints
        {
            get { return killpoints.GetValue(data); }
            set { killpoints.SetValue(data, value); }
        }

        public int FlagPoints
        {
            get { return flagpoints.GetValue(data); }
            set { flagpoints.SetValue(data, value); }
        }

        public short Pid
        {
            get { return pid.GetValue(data); }
            set { pid.SetValue(data, value); }
        }

        public short Freq
        {
            get { return freq.GetValue(data); }
            set { freq.SetValue(data, value); }
        }

        public short Wins
        {
            get { return wins.GetValue(data); }
            set { wins.SetValue(data, value); }
        }

        public short Losses
        {
            get { return losses.GetValue(data); }
            set { losses.SetValue(data, value); }
        }

        public short AttachedTo
        {
            get { return attachedto.GetValue(data); }
            set { attachedto.SetValue(data, value); }
        }

        public short FlagsCarried
        {
            get { return flagscarried.GetValue(data); }
            set { flagscarried.SetValue(data, value); }
        }

        public byte MiscBits
        {
            get { return miscbits.GetValue(data); }
            set { miscbits.SetValue(data, value); }
        }
    }
}
