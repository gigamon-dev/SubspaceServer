using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// authentication return codes
    /// </summary>
    public enum AuthCode : byte
    {
        OK = 0x00, // success
        NewName = 0x01, // fail
        BadPassword = 0x02, // fail
        ArenaFull = 0x03, // fail
        LockedOut = 0x04, // fail
        NoPermission = 0x05, // fail
        SpecOnly = 0x06,  // success
        TooManyPoints = 0x07, // fail
        TooSlow = 0x08, // fail
        NoPermission2 = 0x09,// fail
        NoNewConn = 0x0A,// fail
        BadName = 0x0B,// fail
        OffensiveName = 0x0C, // fail
        NoScores = 0x0D, // sucess
        ServerBusy = 0x0E, // fail
        TooLowUsage = 0x0F, // fail
        NoName = 0x10, // fail
        TooManyDemo = 0x11, // fail
        NoDemo = 0x12, // fail
        CustomText = 0x13, // fail
    }

    public class AuthData
    {
        public bool demodata;
        public AuthCode code;
        public bool authenticated;
        public string name;
        public string sendname;
        public string squad;
        public string customtext;
    }

    public delegate void AuthDoneDelegate(Player p, AuthData data);

    /// <summary>
    /// the core module will call this when a player attempts to connect to
    /// the server. authentication modules can be chained in a somewhat
    /// fragile way by grabbing a reference to the old value with
    /// mm.GetInterface before registering your own with mm.RegisterInterface.
    /// </summary>
    public interface IAuth : IComponentInterface
    {
        /// <summary>
        /// authenticate a player.
        /// this is called when the server needs to authenticate a login
        /// request. the full login packet will be given. an implementation
        /// must called the Done callback to complete the authentication
        /// procedure, but of course it doesn't have to call it within this
        /// function.
        /// </summary>
        /// <param name="p">the player being authenticated</param>
        /// <param name="lp">the login packet provided by the player</param>
        /// <param name="lplen">the length of the provided packet</param>
        /// <param name="done">
        /// the function to call when the authentication result
        /// is known. call it with the player and a filled-in AuthData.
        /// </param>
        void Authenticate(Player p, LoginPacket lp, int lplen, AuthDoneDelegate done);
    }
}
