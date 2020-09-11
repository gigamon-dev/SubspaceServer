using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using SS.Core.ComponentInterfaces;
using SS.Core.ComponentCallbacks;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class LagData : IModule, ILagCollect, ILagQuery
    {
        private const int MaxPing = 10000;
        private const int PacketlossMinPackets = 200;
        private const int MaxBucket = 25;
        private const int BucketWidth = 20;


        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private InterfaceRegistrationToken _iLagCollectToken;
        private InterfaceRegistrationToken _iLagQueryToken;

        private class PingStats
        {
            public int[] Buckets = new int[MaxBucket];

            /// <summary>
            /// Current ping in milliseconds
            /// </summary>
            public int Current;

            /// <summary>
            /// Average ping in milliseconds
            /// </summary>
            public int Average;

            /// <summary>
            /// Maximum ping in milliseconds
            /// </summary>
            public int Max;

            /// <summary>
            /// Minimum ping in milliseconds
            /// </summary>
            public int Min;

            public void Add(int ms)
            {
                if (ms > MaxPing)
                    ms = MaxPing;

                if (ms < 0)
                    ms = 0;

                Current = ms;

                Buckets[msToBucket(ms)]++;

                Average = (Average * 7 + ms) / 8;

                if (ms < Min)
                    Min = ms;

                if (ms > Max)
                    Max = ms;
            }
        }

        private class PlayerLagStats
        {
            public PingStats PositionPacketPing = new PingStats();
            public PingStats ReliablePing = new PingStats();
            public ClientLatencyData ClientReportedPing;
            public TimeSyncData Packetloss;
            public TimeSyncHistory TimeSync = new TimeSyncHistory();
            public ReliableLagData ReliableLayerData;

            public uint WeaponSendCount;
            public uint WeaponRecieveCount;
            public uint LastWeaponSentCount;

            public void Reset()
            {
                
            }

            public void UpdateClientLatencyStats(ref ClientLatencyData data)
            {
                ClientReportedPing = data;
                WeaponRecieveCount = data.weaponcount;
                WeaponSendCount = LastWeaponSentCount;
            }
        }

        /// <summary>
        /// per player data key
        /// </summary>
        private int _lagkey;

        private object _mtx = new object();

        #region IModule Members

        public bool Load(ComponentBroker broker, IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _lagkey = _playerData.AllocatePlayerData<PlayerLagStats>();

            PlayerActionCallback.Register(_broker, playerAction);

            _iLagCollectToken = _broker.RegisterInterface<ILagCollect>(this);
            _iLagQueryToken = _broker.RegisterInterface<ILagQuery>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<ILagCollect>(ref _iLagCollectToken) != 0)
                return false;

            if (_broker.UnregisterInterface<ILagQuery>(ref _iLagQueryToken) != 0)
                return false;

            PlayerActionCallback.Unregister(_broker, playerAction);

            _playerData.FreePlayerData(_lagkey);

            return true;
        }

        #endregion

        #region ILagCollect Members

        private void pedanticLock()
        {
#if CFG_PEDANTIC_LOCKING
            Monitor.Enter(_mtx);
#endif
        }

        private void pedanticUnlock()
        {
#if CFG_PEDANTIC_LOCKING
            Monitor.Exit(_mtx);
#endif
        }

        void ILagCollect.Position(Player p, int ms, int clipping, uint wpnSent)
        {
            if(p == null)
                return;

            PlayerLagStats lagStats = p[_lagkey] as PlayerLagStats;
            if (lagStats == null)
                return;

            pedanticLock();

            try
            {
                lagStats.PositionPacketPing.Add(ms * 2); // convert one-way to round-trip
                lagStats.LastWeaponSentCount = wpnSent;
            }
            finally
            {
                pedanticUnlock();
            }
        }

        void ILagCollect.RelDelay(Player p, int ms)
        {
            if (p == null)
                return;

            PlayerLagStats lagStats = p[_lagkey] as PlayerLagStats;
            if (lagStats == null)
                return;

            pedanticLock();

            try
            {
                lagStats.ReliablePing.Add(ms);
            }
            finally
            {
                pedanticUnlock();
            }
        }

        void ILagCollect.ClientLatency(Player p, ref ClientLatencyData data)
        {
            if (p == null)
                return;

            PlayerLagStats lagStats = p[_lagkey] as PlayerLagStats;
            if (lagStats == null)
                return;

            pedanticLock();

            try
            {
                lagStats.UpdateClientLatencyStats(ref data);
            }
            finally
            {
                pedanticUnlock();
            }
        }

        void ILagCollect.TimeSync(Player p, ref TimeSyncData data)
        {
            if (p == null)
                return;

            PlayerLagStats lagStats = p[_lagkey] as PlayerLagStats;
            if (lagStats == null)
                return;

            pedanticLock();

            try
            {
                lagStats.Packetloss = data;
                lagStats.TimeSync.Update(ref data);
            }
            finally
            {
                pedanticUnlock();
            }
        }

        void ILagCollect.RelStats(Player p, ref ReliableLagData data)
        {
            if (p == null)
                return;

            PlayerLagStats lagStats = p[_lagkey] as PlayerLagStats;
            if (lagStats == null)
                return;

            pedanticLock();

            try
            {
                lagStats.ReliableLayerData = data;
            }
            finally
            {
                pedanticUnlock();
            }
        }

        void ILagCollect.Clear(Player p)
        {
            if (p == null)
                return;

            PlayerLagStats lagStats = p[_lagkey] as PlayerLagStats;
            if (lagStats == null)
                return;

            pedanticLock();

            try
            {
                lagStats.Reset();
            }
            finally
            {
                pedanticUnlock();
            }
        }

        #endregion

        #region ILagQuery Members

        void ILagQuery.QueryPPing(Player p, out PingSummary ping)
        {
            throw new NotImplementedException();
        }

        void ILagQuery.QueryCPing(Player p, out PingSummary ping)
        {
            throw new NotImplementedException();
        }

        void ILagQuery.QueryRPing(Player p, out PingSummary ping)
        {
            throw new NotImplementedException();
        }

        void ILagQuery.QueryPLoss(Player p, out PacketlossSummary packetloss)
        {
            throw new NotImplementedException();
        }

        void ILagQuery.QueryRelLag(Player p, ReliableLagData reliableLag)
        {
            throw new NotImplementedException();
        }

        #endregion

        private void playerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            PlayerLagStats lagStats = p[_lagkey] as PlayerLagStats;
            if (lagStats == null)
                return;

            if(action == PlayerAction.Connect)
                lagStats.Reset();
        }

        private static int msToBucket(int ms)
        {
            return ((ms) < 0) ? 0 : (((ms) < (MaxBucket * BucketWidth)) ? ((ms) / BucketWidth) : MaxBucket - 1);
        }
    }
}
