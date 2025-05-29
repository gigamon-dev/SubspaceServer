﻿using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Identifies additional features that a client can support.
    /// </summary>
    /// <remarks>
    /// Bots that login using VIE authentication can use this to tell the server which additional features they support.
    /// </remarks>
    [Flags]
    public enum ClientFeatures : ushort
    {
        /// <summary>
        /// No extra features (VIE client).
        /// </summary>
        None = 0,

        /// <summary>
        /// Whether the client supports watch damage functionality.
        /// </summary>
        /// <remarks>
        /// The client supports receiving <see cref="S2CPacketType.Damage"/> and sending <see cref="C2SPacketType.Damage"/>.
        /// </remarks>
        WatchDamage = 1,

        /// <summary>
        /// Supports the batch position packets.
        /// </summary>
        /// <remarks>
        /// The client supports receiving the <see cref="S2CPacketType.BatchedSmallPosition"/> and <see cref="S2CPacketType.BatchedLargePosition"/> position packets.
        /// </remarks>
        BatchPositions = 2,

        /// <summary>
        /// Supports the ability to be told where to warp to.
        /// </summary>
        /// <remarks>
        /// Supports the <see cref="S2CPacketType.WarpTo"/> packet.
        /// </remarks>
        WarpTo = 4,

        /// <summary>
        /// Supports LVZ functionality.
        /// </summary>
        /// <remarks>
        /// The client supports the <see cref="S2CPacketType.ToggleLVZ"/> and <see cref="S2CPacketType.ChangeLVZ"/> packets.
        /// Also, the extended version of the <see cref="S2CPacketType.MapFilename"/> which includes the additional size field and the ability to have up to 16 additional files (LVZs).
        /// </remarks>
        Lvz = 8,

        /// <summary>
        /// Supports redirects to another zone.
        /// </summary>
        /// <remarks>
        /// The client supports the <see cref="S2CPacketType.Redirect"/> packet.
        /// </remarks>
        Redirect = 16,

        /// <summary>
        /// Supports displaying the UI for selecting from a set of choices.
        /// </summary>
        /// <remarks>
        /// The client supports the <see cref="S2CPacketType.SelectBox"/> packet.
        /// </remarks>
        SelectBox = 32,

        /// <summary>
        /// The features that the Continuum client supports.
        /// </summary>
        /// <remarks>
        /// Continuum supports <see cref="WatchDamage"/>, <see cref="BatchPositions"/>, <see cref="WarpTo"/>, and <see cref="Lvz"/>.
        /// </remarks>
        Continuum = (WatchDamage | BatchPositions | WarpTo | Lvz | Redirect | SelectBox),
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LoginPacket
    {
        #region Static members

        public static readonly int VIELength;
        public static readonly int ContinuumLength;

        static LoginPacket()
        {
            VIELength = Marshal.SizeOf<LoginPacket>();
            ContinuumLength = VIELength + 64;
        }

        #endregion

        public byte Type;
        public byte Flags;
        public NameInlineArray Name;
        public PasswordInlineArray Password;
        private uint macId;
        public byte ConnectionType;
        private short timeZoneBias;
        private ushort unk1;
        private ushort cVersion;
        private short field444;
        private ushort clientFeature;
        private int field555;
        private uint d2;
        private uint serverIPv4Address;
        private ushort clientPort;
        private uint loginId;
        private ushort unk2;
        // The continuum login packet (0x24) has 64 more bytes (continuum id field) that come next (not included in this struct).
        // The zone server doesn't know how to interpret the bytes. It just passes them to the billing server.

        #region Helper properties

        public uint MacId
        {
            readonly get => LittleEndianConverter.Convert(macId);
            set => macId = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// The difference in minutes between the client's local time and Coordinated Universal Time (UTC).
        /// </summary>
        public short TimeZoneBias
        {
            readonly get => LittleEndianConverter.Convert(timeZoneBias);
            set => timeZoneBias = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// The version # of the client.
        /// </summary>
        public ushort CVersion
        {
            readonly get => LittleEndianConverter.Convert(cVersion);
            set => cVersion = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// For clients (usually bots) that login with VIE authentication to tell the server which additional features it supports.
        /// </summary>
        public ClientFeatures ClientFeatures
        {
            readonly get => (ClientFeatures)LittleEndianConverter.Convert(clientFeature);
            set => clientFeature = LittleEndianConverter.Convert((ushort)value);
        }

        public uint D2
        {
            readonly get => LittleEndianConverter.Convert(d2);
            set => d2 = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// The server address that the client says it connected to.
        /// </summary>
        public uint ServerIPv4Address
        {
            readonly get => LittleEndianConverter.Convert(serverIPv4Address);
            set => serverIPv4Address = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// The port that the client says it is using to connect.
        /// </summary>
        public ushort ClientPort
        {
            readonly get => LittleEndianConverter.Convert(clientPort);
            set => clientPort = LittleEndianConverter.Convert(value);
        }

        /// <summary> 
        /// When a client is redirected to another zone,
        /// the value from the <see cref="S2CPacketType.Redirect"/> packet.
        /// This can be used to pass along a one-time authentication code.
        /// </summary>
        public uint LoginId
        {
            readonly get => LittleEndianConverter.Convert(loginId);
            set => loginId = LittleEndianConverter.Convert(value);
        }

        #endregion

        #region Inline Array Types

        [InlineArray(Length)]
        public struct NameInlineArray
        {
            public const int Length = 32;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public void Clear()
            {
                ((Span<byte>)this).Clear();
            }
        }

        [InlineArray(Length)]
        public struct PasswordInlineArray
        {
            public const int Length = 32;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public void Clear()
            {
                ((Span<byte>)this).Clear();
            }
        }

        #endregion
    }
}
