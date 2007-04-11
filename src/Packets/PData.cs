using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    /// <summary>
    /// the player data packet
    /// 
    /// this is the literal packet that gets sent to standard clients.
    /// some data the server uses is kept in here directly
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack=1)]
    public struct PData
    {
        /// <summary>
        /// the type byte
        /// </summary>
        public byte PktType;

        /// <summary>
        /// which ship this player is in
        /// </summary>
        public sbyte Ship;

        /// <summary>
        /// whether this player wants voice messages
        /// </summary>
        public byte AcceptsAudio;

        /// <summary>
        /// the player's name (may not be nul terminated)
        /// </summary>
        unsafe public fixed byte Name[20];

        /// <summary>
        /// the player's squad (may not be nul terminated)
        /// </summary>
        unsafe public fixed byte Squad[20];

        /// <summary>
        /// kill points (not authoritative)
        /// </summary>
        public int KillPoints;

        /// <summary>
        /// flag points (not authoritative)
        /// </summary>
        public int FlagPoints;

        /// <summary>
        /// the player id number
        /// </summary>
        public short Pid;

        /// <summary>
        /// frequency
        /// </summary>
        public short Freq;
        
        /// <summary>
        /// kill count (not authoritative)
        /// </summary>
        public short Wins;

        /// <summary>
        /// death count (not authoritative)
        /// </summary>
        public short Losses;

        /// <summary>
        /// the pid of the player this one is attached to (-1 for nobody)
        /// </summary>
        public short AttachedTo;

        /// <summary>
        /// how many flags are being carried (not authoritative)
        /// </summary>
        public short FlagsCarried;

        /// <summary>
        /// misc. bits (see below)
        /// </summary>
        public byte MiscBits;

        // flag bits for the miscbits field
        public const byte HasCrownBit = 0x01;
        public const byte SendDamageBit = 0x02; // FIXME: not implemented in continuum yet

        public bool HasCrown
        {
            get
            {
                return (MiscBits & HasCrownBit) != 0;
            }

            set
            {
                if (value)
                {
                    // set has crown
                    MiscBits |= HasCrownBit;
                }
                else
                {
                    // unset has crown
                    MiscBits &= 0xFE;
                }
            }
        }

        unsafe public string NameManaged
        {
            get
            {
                byte[] nameBytes = new byte[20];
                int nullIndex = 20;

                fixed (byte* p = Name)
                {
                    for (int x = 0; x < 20; x++)
                    {
                        nameBytes[x] = p[x];

                        if (nameBytes[x] == '\0')
                        {
                            nullIndex = x;
                            break;
                        }
                    }
                }

                return Encoding.ASCII.GetString(nameBytes, 0, nullIndex);
            }

            set
            {
                byte[] arr = ASCIIEncoding.ASCII.GetBytes(value);

                fixed (byte* p = Name)
                {
                    for (int x = 0; x < 20; x++)
                    {
                        if(x<arr.Length)
                            p[x] = arr[x];
                        else
                            p[x] = 0;
                    }
                }
            }
        }
        /*
        unsafe public void Test()
        {
            fixed (PData* p = &this)
            {
                //byte[]* bufarr = (byte[]*)p;
                //byte* buf = (byte*)p;
            }
        }
        */
    }
}
