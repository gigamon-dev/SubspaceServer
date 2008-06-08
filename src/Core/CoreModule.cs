using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;
using SS.Utilities;
using SS.Core.ComponentInterfaces;

namespace SS.Core
{
    /// <summary>
    /// authentication return codes
    /// </summary>
    public enum AuthCode
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

    public static class AuthCodeExtension
    {
        /// <summary>
        /// which authentication result codes result in the player moving forward in the login process
        /// </summary>
        /// <param name="authCode"></param>
        /// <returns></returns>
        public static bool AuthIsOK(this AuthCode authCode)
        {
            return authCode == AuthCode.OK || authCode == AuthCode.SpecOnly || authCode == AuthCode.NoScores;
        }
    }

    public class CoreModule : IModule, IAuth
    {
        private ModuleManager _mm;
        private IPlayerData _playerData;
        private INetwork _net;
        //private IChatNet _chatnet;
        private ILogManager _logManager;
        private IConfigManager _configManager;
        private IServerTimer _mainLoop;
        //private IMapNewsDownload _map;
        private IArenaManagerCore _arenaManager;
        //private ICapabilityManager _capManager;
        private int _pdkey;

        private const short ClientVersion_VIE = 134;
        private const short ClientVersion_Cont = 39;

        private class CorePlayerData
        {
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get
            {
                return new Type[] 
                {
                    typeof(IPlayerData), 
                    typeof(INetwork), 
                    typeof(ILogManager), 
                    typeof(IConfigManager), 
                    typeof(IServerTimer), 
                    typeof(IArenaManagerCore)
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;
            _playerData = interfaceDependencies[typeof(IPlayerData)] as IPlayerData;
            _net = interfaceDependencies[typeof(INetwork)] as INetwork;
            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            _configManager = interfaceDependencies[typeof(IConfigManager)] as IConfigManager;
            _mainLoop = interfaceDependencies[typeof(IServerTimer)] as IServerTimer;
            _arenaManager = interfaceDependencies[typeof(IArenaManagerCore)] as IArenaManagerCore;

            _pdkey = _playerData.AllocatePlayerData<CorePlayerData>();

            _net.AddPacket((int)Packets.C2SPacketType.Login, playerLogin);
            _net.AddPacket((int)Packets.C2SPacketType.ContLogin, playerLogin);

            mm.RegisterInterface<IAuth>(this);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            return true;
        }

        #endregion

        private void playerLogin(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            CorePlayerData pdata = p[_pdkey] as CorePlayerData;
            if (pdata == null)
                return;

            if (!p.IsStandard)
            {
                _logManager.Log(LogLevel.Malicious, "<core> [pid={0}] login packet from wrong client type ({1})", p.Id, p.Type);
            }
#if CFG_RELAX_LENGTH_CHECKS
            else if ((p.Type == ClientType.Vie && len < LoginPacket.LengthVIE) ||
                (p.Type == ClientType.Continuum && len < LoginPacket.LengthContinuum))
#endif
            else if ((p.Type == ClientType.Vie && len != LoginPacket.LengthVIE) ||
                (p.Type == ClientType.Continuum && len != LoginPacket.LengthContinuum))
                _logManager.Log(LogLevel.Malicious, "<core> [pid={0}] bad login packet length ({1})", p.Id, len);
            else if (p.Status != PlayerState.Connected)
                _logManager.Log(LogLevel.Malicious, "<core> [pid={0}] login request from wrong stage: {1}", p.Id, p.Status);
            else
            {
                LoginPacket pkt = new LoginPacket(data);

#if !CFG_RELAX_LENGTH_CHECKS
                // VIE clients can only have one version. 
                // Continuum clients will need to ask for an update
                if(p.Type == ClientType.Vie && pkt.CVersion != ClientVersion_VIE)
                {
                    failVersionWith(p, AuthCode.LockedOut, null, "bad VIE client version");
                    return;
                }
#endif

                // copy into storage for use by authenticator
                if (len > 512)
                    len = 512;

                // TODO: much much more....
            }
        }

        private void failVersionWith(Player p, AuthCode authCode, string text, string logmsg)
        {
            AuthData auth = new AuthData();

            if (p.Type == ClientType.Continuum && text != null)
            {
                auth.code = AuthCode.CustomText;
                auth.customtext = text;
            }
            else
                auth.code = authCode;

            _playerData.WriteLock();
            try
            {
                p.Status = PlayerState.WaitAuth;
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            authDone(p, auth);

            _logManager.Log(LogLevel.Drivel, "<core> [pid={0}] login request denied: {1}", p.Id, logmsg);
        }

        private void authDone(Player p, AuthData auth)
        {
            
        }

        #region IAuth Members

        void IAuth.Authenticate(Player p, LoginPacket lp, int lplen, AuthDoneDelegate done)
        {
            defaultAuth(p, lp, lplen, done);   
        }

        #endregion

        private void defaultAuth(Player p, LoginPacket lp, int lplen, AuthDoneDelegate done)
        {
            AuthData auth = new AuthData();

            auth.demodata = false;
            auth.code = AuthCode.OK;
            auth.authenticated = false;

            string name = lp.Name;
            auth.name = name.Length > 23 ? name.Substring(0, 23) + '\0' : name; // TODO: figure out if this is really needs to be null terminated
            auth.sendname = name.Length > 19 ? name.Substring(0, 19) + +'\0' : name;
            auth.squad = null;

            done(p, auth);
        }
    }
}
