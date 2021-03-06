﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    /// <summary>
    /// player login packet
    /// </summary>
    public readonly struct LoginPacket
    {
        static LoginPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateByteDataLocation();
            flags = locationBuilder.CreateByteDataLocation();
            name = locationBuilder.CreateDataLocation(32);
            password = locationBuilder.CreateDataLocation(32);
            macid = locationBuilder.CreateUInt32DataLocation();
            blah = locationBuilder.CreateByteDataLocation();
            timezonebias = locationBuilder.CreateUInt16DataLocation();
            unk1 = locationBuilder.CreateUInt16DataLocation();
            cversion = locationBuilder.CreateInt16DataLocation();
            field444 = locationBuilder.CreateInt32DataLocation();
            field555 = locationBuilder.CreateInt32DataLocation();
            d2 = locationBuilder.CreateUInt32DataLocation();
            blah2 = locationBuilder.CreateDataLocation(12);
            LengthVIE = locationBuilder.NumBytes;
            contid = locationBuilder.CreateDataLocation(64);
            LengthContinuum = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly ByteDataLocation flags;
        private static readonly DataLocation name;
        private static readonly DataLocation password;
        private static readonly UInt32DataLocation macid;
        private static readonly ByteDataLocation blah;
        private static readonly UInt16DataLocation timezonebias;
        private static readonly UInt16DataLocation unk1;
        private static readonly Int16DataLocation cversion;
        private static readonly Int32DataLocation field444;
        private static readonly Int32DataLocation field555;
        private static readonly UInt32DataLocation d2;
        private static readonly DataLocation blah2;
        private static readonly DataLocation contid; // cont only
        public static readonly int LengthVIE;
        public static readonly int LengthContinuum;

        private readonly byte[] data;

        public LoginPacket(byte[] data)
        {
            this.data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public Span<byte> NameSpan
        {
            get { return new Span<byte>(data, name.ByteOffset, name.NumBytes); }
        }

        public string Name
        {
            get { return NameSpan.ReadNullTerminatedASCII(); }
        }

        public Span<byte> PasswordSpan
        {
            get { return new Span<byte>(data, password.ByteOffset, name.NumBytes); }
        }

        public uint MacId
        {
            get { return macid.GetValue(data); }
        }

        public short CVersion
        {
            get { return cversion.GetValue(data); }            
        }

        public uint D2
        {
            get { return d2.GetValue(data); }
        }
    }
}
