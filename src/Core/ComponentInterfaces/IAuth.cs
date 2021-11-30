using SS.Packets.Game;
using SS.Utilities;
using System;

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
        NoScores = 0x0D, // success
        ServerBusy = 0x0E, // fail
        TooLowUsage = 0x0F, // fail
        AskDemographics = 0x10, // success
        TooManyDemo = 0x11, // fail
        NoDemo = 0x12, // fail
        CustomText = 0x13, // fail, cont only
    }

    public static class AuthCodeExtension
    {
        /// <summary>
        /// which authentication result codes result in the player moving forward in the login process
        /// </summary>
        /// <param name="authCode"></param>
        /// <returns></returns>
        public static bool AuthIsOK(this AuthCode authCode)
        {
            return authCode == AuthCode.OK
                || authCode == AuthCode.SpecOnly
                || authCode == AuthCode.NoScores
                || authCode == AuthCode.AskDemographics;
        }
    }

    /// <summary>
    /// An authentication module must fill in one of these structs to return an authentication response.
    /// If code is a failure code, none of the other fields matter (except maybe customtext, if you want to return a custom error message).
    /// </summary>
    public class AuthData
    {
        /// <summary>
        /// Whether registration data is requested.
        /// </summary>
        public bool DemoData;

        /// <summary>
        /// The authentication code.
        /// </summary>
        public AuthCode Code;

        /// <summary>
        /// Whether the player should be considered as having been authenticated.
        /// True if the user was authenticated by a billing server or a local password file.
        /// This is used to determine if the player can be placed into a group by <see cref="ICapabilityManager"/>.
        /// </summary>
        public bool Authenticated;

        private string _name;

        /// <summary>
        /// The name to assign to the player.
        /// </summary>
        /// <exception cref="ArgumentException">Value cannot be null or white-space.</exception>
        /// <exception cref="ArgumentException">Value is too long.</exception>
        public string Name
        {
            get => _name;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(value));

                if (StringUtils.DefaultEncoding.GetByteCount(value) > Player.MaxNameLength - 1) // -1 for null-terminator
                    throw new ArgumentException($"Does not fit into {Player.MaxNameLength - 1} bytes when encoded.", nameof(value));

                _name = value;
            }
        }

        private string _sendName;

        /// <summary>
        /// The client visible name.
        /// </summary>
        /// <exception cref="ArgumentException">Value cannot be null or white-space.</exception>
        /// <exception cref="ArgumentException">Value is too long.</exception>
        public string SendName
        {
            get => _sendName;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(value));

                if (StringUtils.DefaultEncoding.GetByteCount(value) > S2C_PlayerData.NameBytesLength) // null-terminator not required
                    throw new ArgumentException($"Does not fit into {S2C_PlayerData.NameBytesLength} bytes when encoded.", nameof(value));

                _sendName = value;
            }
        }

        private string _squad;

        /// <summary>
        /// The squad to assign the player.
        /// </summary>
        /// <exception cref="ArgumentException">Value is too long.</exception>
        public string Squad
        {
            get => _squad;
            set
            {
                if (value != null && StringUtils.DefaultEncoding.GetByteCount(value) > Player.MaxSquadLength - 1) // -1 for null-terminator
                    throw new ArgumentException($"Does not fit into {Player.MaxSquadLength - 1} bytes when encoded.", nameof(value));

                _squad = value;
            }
        }

        /// <summary>
        /// Custom text to return to the player if <see cref="Code"/> is <see cref="AuthCode.CustomText"/>.
        /// </summary>
        public string CustomText;
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
        void Authenticate(Player p, in LoginPacket lp, int lplen, AuthDoneDelegate done);
    }
}
