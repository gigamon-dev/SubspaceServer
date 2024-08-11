using SS.Packets.Game;
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
        /// Gets whether an <see cref="AuthCode"/> allows the player to move forward in the login process.
        /// </summary>
        /// <param name="authCode"></param>
        /// <returns></returns>
        public static bool IsOK(this AuthCode authCode)
        {
            return authCode == AuthCode.OK
                || authCode == AuthCode.SpecOnly
                || authCode == AuthCode.NoScores
                || authCode == AuthCode.AskDemographics;
        }
    }

    /// <summary>
    /// Interface representing an authentication attempt to be processed.
    /// </summary>
    public interface IAuthRequest
    {
        #region Input

        /// <summary>
        /// The player that attempting to authenticate.
        /// </summary>
        Player? Player { get; }

        /// <summary>
        /// The login request containing the <see cref="LoginPacket"/> and possibly <see cref="ExtraBytes"/> after the packet.
        /// </summary>
        ReadOnlySpan<byte> LoginBytes { get; }

        /// <summary>
        /// The login packet from the <see cref="LoginBytes"/>.
        /// </summary>
        ref readonly LoginPacket LoginPacket { get; }

        /// <summary>
        /// Additional data sent in the login request.
        /// </summary>
        /// <remarks>
        /// For a <see cref="LoginPacket"/> of type <see cref="C2SPacketType.Login"/> (VIE client login), this should be empty.
        /// For a <see cref="LoginPacket"/> of type <see cref="C2SPacketType.ContLogin"/>, this will be the "contid" field of 64 bytes which gets passed on to the biller.
        /// Other, future client types could potentially send arbitrary data meant for the biller to read, like continuum does.
        /// </remarks>
        ReadOnlySpan<byte> ExtraBytes { get; }

        #endregion

        /// <summary>
        /// The result to populate before calling <see cref="Done"/>.
        /// </summary>
        IAuthResult Result { get; }

        /// <summary>
        /// Tells the core module to process the <see cref="Result"/>.
        /// </summary>
        /// <remarks>
        /// Call this on the mainloop thread.
        /// Use <see cref="IMainloop.QueueMainWorkItem{TState}(Action{TState}, TState)"/> if not already on the mainloop thread.
        /// </remarks>
        void Done();
    }

    /// <summary>
    /// The result an authentication module fills to return an authentication response.
    /// If <see cref="Code"/> is a failure code, none of the other fields matter (except maybe <see cref="CustomText"/>, if you want to return a custom error message).
    /// </summary>
    public interface IAuthResult
    {
        /// <summary>
        /// Whether registration data is requested.
        /// </summary>
        bool DemoData { get; set; }

        /// <summary>
        /// The authentication code.
        /// </summary>
        AuthCode? Code { get; set; }

        /// <summary>
        /// Whether the player was authenticated (only if <see cref="Code"/> is <see cref="AuthCodeExtension.IsOK(AuthCode)"/>).
        /// </summary>
        /// <remarks>
        /// True if the user was authenticated by a billing server or a local password file.
        /// Keep in mind that a player may be allowed in (an OK <see cref="Code"/>), but may not have been authenticated.
        /// For example, if the billing server connection was lost, a player can still be allowed in with a ^ in front of their name.
        /// This allows players to play without scores or other capabilities, and for other players to know the player might not be who they say they are.
        /// <para>
        /// A player must be authenticated to be assigned a group (e.g. mod, smod, etc...) by the <see cref="ICapabilityManager"/>.
        /// </para>
        /// </remarks>
        bool Authenticated { get; set; }

        #region Name

        /// <summary>
        /// The name to assign the player.
        /// </summary>
        ReadOnlySpan<char> Name { get; }

        /// <summary>
        /// Sets the <see cref="Name"/>.
        /// </summary>
        /// <param name="value"></param>
        void SetName(ReadOnlySpan<char> value);

        #endregion

        #region SendName

        /// <summary>
        /// The client visible name.
        /// </summary>
        ReadOnlySpan<char> SendName { get; }

        /// <summary>
        /// Sets the <see cref="SendName"/>.
        /// </summary>
        /// <param name="value"></param>
        void SetSendName(ReadOnlySpan<char> value);

        #endregion

        #region Squad

        /// <summary>
        /// The squad to assign the player.
        /// </summary>
        ReadOnlySpan<char> Squad { get; }

        /// <summary>
        /// Sets the <see cref="Squad"/>.
        /// </summary>
        /// <param name="value"></param>
        void SetSquad(ReadOnlySpan<char> value);

        #endregion

        #region CustomText

        /// <summary>
        /// Custom text to return to the player if <see cref="Code"/> is <see cref="AuthCode.CustomText"/>.
        /// </summary>
        ReadOnlySpan<char> CustomText { get; }

        /// <summary>
        /// Gets the maximum # of characters that the custom text can contain.
        /// </summary>
        /// <returns>The maximum # of characters.</returns>
        int GetMaxCustomTextLength();

        /// <summary>
        /// Sets the <see cref="CustomText"/>.
        /// </summary>
        /// <param name="value">The value to set the custom text to.</param>
        void SetCustomText(ReadOnlySpan<char> value);

        #endregion
    }

    /// <summary>
    /// Interface for authentication modules to implement.
    /// </summary>
    /// <remarks>
    /// The <see cref="Modules.Core"/> module will call this when a player attempts to connect to the server. 
    /// Authentication modules can be chained in a somewhat fragile way by grabbing a reference to the old <see cref="IAuth"/> implementation 
    /// with <see cref="ComponentBroker.GetInterface{TInterface}(string)GetInterface"/> before registering its own 
    /// with <see cref="ComponentBroker.RegisterInterface"/>.
    /// </remarks>
    public interface IAuth : IComponentInterface
    {
        /// <summary>
        /// Authenticates a player.
        /// </summary>
        /// <remarks>
        /// This is called when the server needs to authenticate a login request.
        /// An implementation should populate the <paramref name="authRequest"/>'s <see cref="IAuthRequest.Result"/> and then call <see cref="IAuthRequest.Done"/>.
        /// 
        /// <para>
        /// The following members can be read as input:
        /// <list type="bullet">
        /// <item>The <see cref="IAuthRequest.Player"/> that made the request</item>
        /// <item>The request data (<see cref="IAuthRequest.LoginBytes"/>, and helpers <see cref="IAuthRequest.LoginPacket"/> and <see cref="IAuthRequest.ExtraBytes"/>)</item>
        /// </list>
        /// </para>
        /// 
        /// <para>
        /// The <paramref name="authRequest"/> can be held onto if the request needs to be processed asynchronously.
        /// However, if the <see cref="Player"/> disconnects, the <see cref="IAuthRequest"/> MUST be discarded.
        /// Use <see cref="ComponentCallbacks.NewPlayerCallback"/> to detect if a player disconnected.
        /// <see cref="ComponentCallbacks.PlayerActionCallback"/> only fires for players that are past authentication.
        /// </para>
        /// 
        /// <para>
        /// All references to an <see cref="IAuthRequest"/> object should dropped after calling <see cref="IAuthRequest.Done"/>, or if the player disconnects.
        /// Underneath the scenes, <see cref="IAuthRequest"/> objects are pooled. 
        /// Accessing the object afterwards means it would likely affect a different authentication attempt and have unintended consequences.
        /// </para>
        /// </remarks>
        /// <param name="authRequest">
        /// The authentication request to process.
        /// </param>
        void Authenticate(IAuthRequest authRequest);
    }
}
