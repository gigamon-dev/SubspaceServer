using System;
using System.Linq;
using System.Text;
using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Utilities;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides a default implementation of bandwidth limiters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a port of ASSS's bw_default module. The following is a description of what it does and how it works.
    /// </para>
    /// <para>
    /// The default bandwidth limiter attempts to figure out how much bandwidth is available between the server and the client.
    /// In the SubSpace protocol, the only way to tell if the other end received a packet is to send data reliably.
    /// The limiter is notified of certain reliable data events: 
    /// 1. When the server resends a reliable packet.
    /// 2. When the server receives an acknowlegement (ACK) that reliable packet was sucessfully received.
    /// The limiter uses these events to modify the "limit" it considers to be available.
    /// When the server resends a reliable packet, the limiter interprets it as a reason to decrease the "limit".
    /// Whereas, when the server receives an ACK, the limiter interprets it as a reason to increase the "limit".
    /// </para>
    /// <para>
    /// This "limit" is the overall # of bytes per second that the server thinks is possible to send to the client.
    /// The "limit" is split into a buckets, one for each <see cref="BandwidthPriority"/>. Each bucket is given a percentage 
    /// of the overall "limit". Each time the server does an iteration of sending data to a connection, it tells the
    /// limiter by calling <see cref="IBandwidthLimiter.Iter(DateTime)"/>. When called, the limiter recalculates how many
    /// bytes are available for each priority's bucket.  That is, it adds availablity based on the amount of time that has
    /// past since the last iteration.
    /// </para>
    /// <para>
    /// When data needs to be sent, the limiter's <see cref="IBandwidthLimiter.Check(int, BandwidthPriority)"/> method is called.
    /// There, the limiter decides if there is available bandwidth to send the data. It does this by starting at the priority's 
    /// bucket looking to pull out availablity. When exhausted, it pulls from the next lower priority bucket, and so on. This 
    /// priority / bucket mechanism ensures that higher priority traffic will always have some availablity that lower priority 
    /// traffic cannot use.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public class BandwidthDefault : IModule, IBandwidthLimiterProvider
    {
        private IConfigManager _configManager;
        private InterfaceRegistrationToken<IBandwidthLimiterProvider> _iBandwidthLimiterProviderToken;

        private Settings _settings;
        private NonTransientObjectPool<DefaultBandwidthLimiter> _bandwidthLimiterPool;

        #region IModule Members

        public bool Load(ComponentBroker broker, IConfigManager configManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

            _settings = new()
            {
                LimitLow = _configManager.GetInt(_configManager.Global, "Net", "LimitMinimum", 2500),
                LimitHigh = _configManager.GetInt(_configManager.Global, "Net", "LimitMaximum", 102400),
                LimitInitial = _configManager.GetInt(_configManager.Global, "Net", "LimitInitial", 5000),
                ClientCanBuffer = _configManager.GetInt(_configManager.Global, "Net", "SendAtOnce", 255),
                LimitScale = _configManager.GetInt(_configManager.Global, "Net", "LimitScale", Constants.MaxPacket * 1),
                MaxAvail = _configManager.GetInt(_configManager.Global, "Net", "Burst", Constants.MaxPacket * 4),
                UseHitLimit = _configManager.GetInt(_configManager.Global, "Net", "UseHitLimit", 0) != 0,
                PriorityLimits = new[]
                {
                    _configManager.GetInt(_configManager.Global, "Net", "PriLimit0", 20), // low pri unrel
                    _configManager.GetInt(_configManager.Global, "Net", "PriLimit1", 40), // reg pri unrel
                    _configManager.GetInt(_configManager.Global, "Net", "PriLimit2", 20), // high pri unrel
                    _configManager.GetInt(_configManager.Global, "Net", "PriLimit3", 15), // rel
                    _configManager.GetInt(_configManager.Global, "Net", "PriLimit4", 5),  // ack
                }
            };

            _bandwidthLimiterPool = new(new BandwidthLimiterPooledObjectPolicy(_settings));

            _iBandwidthLimiterProviderToken = broker.RegisterInterface<IBandwidthLimiterProvider>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iBandwidthLimiterProviderToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IBandwidthLimiterProvider Members

        IBandwidthLimiter IBandwidthLimiterProvider.New()
        {
            DefaultBandwidthLimiter bwLimiter = _bandwidthLimiterPool.Get();
            bwLimiter.Initialize(); // necessary, we dont want info from its previous use
            return bwLimiter;
        }

        void IBandwidthLimiterProvider.Free(IBandwidthLimiter limiter)
        {
            if (limiter is DefaultBandwidthLimiter bandwidthLimiter)
            {
                _bandwidthLimiterPool.Return(bandwidthLimiter);
            }
        }

        #endregion

        #region Helper classes

        private class Settings
        {
            /// <summary>
            /// The lowest the limit can go (bytes per second).
            /// </summary>
            public int LimitLow { get; init; }

            /// <summary>
            /// The highest the limit can go (bytes per second).
            /// </summary>
            public int LimitHigh { get; init; }

            /// <summary>
            /// The initial limit (bytes per second).
            /// </summary>
            public int LimitInitial { get; init; }

            /// <summary>
            /// An array representing the percentage of the limit allotted to each priority level 
            /// such that a traffic for a given priority can use its alotted amount plus any alotted to lower priorities.
            /// </summary>
            /// <remarks>
            /// This should be configured such that all the values in this array add up to 100.
            /// </remarks>
            public int[] PriorityLimits { get; init; }

            /// <summary>
            /// The maximum # of buffers a client is able to buffer.
            /// That is, the maximum window of reliable packets that a client can accept.
            /// </summary>
            public int ClientCanBuffer { get; init; }

            /// <summary>
            /// Scaling factor used to adjust the current overall limit when receiving an ACK or when resending data.
            /// </summary>
            public int LimitScale { get; init; }

            /// <summary>
            /// The maximum 'burst' a priority can gain from unused bandwidth (bytes).
            /// </summary>
            public int MaxAvail { get; init; }

            /// <summary>
            /// Whether to enable functionality for a greater limit increase due to receiving an ACK if it came after the limit was hit (a send was denied).
            /// </summary>
            /// <remarks>
            /// When an ACK is received, the current limit is increased. 
            /// With this setting on, the current limit will increased a greater amount if it detected that the limit was hit.
            /// </remarks>
            public bool UseHitLimit { get; init; }
        }

        private class BandwidthLimiterPooledObjectPolicy : PooledObjectPolicy<DefaultBandwidthLimiter>
        {
            private readonly Settings _config;

            public BandwidthLimiterPooledObjectPolicy(Settings config)
            {
                _config = config ?? throw new ArgumentNullException(nameof(config));
            }

            public override DefaultBandwidthLimiter Create()
            {
                return new DefaultBandwidthLimiter(_config);
            }

            public override bool Return(DefaultBandwidthLimiter obj)
            {
                if (obj == null)
                    return false;

                return true;
            }
        }

        private class DefaultBandwidthLimiter : IBandwidthLimiter
        {
            private readonly Settings _settings;

            private int _limit;
            private readonly int[] _avail = new int[(int)Enum.GetValues<BandwidthPriority>().Max() + 1];
            private bool _hitLimit;
            private DateTime _sinceTime;

            private const int Granularity = 8;
            private static readonly TimeSpan s_sliceTimeSpan = TimeSpan.FromMilliseconds(1000 / Granularity);

            public DefaultBandwidthLimiter(Settings settings)
            {
                _settings = settings ?? throw new ArgumentNullException(nameof(settings));

                Initialize();
            }

            public void Initialize()
            {
                _limit = _settings.LimitInitial;
                _hitLimit = false;
                _sinceTime = DateTime.UtcNow;

                // Rather than start with 0, start as if one iteration had already gone by.
                // This way, we'll have some bandwidth available to use immediately without having to wait.
                // Note: ASSS does not do this. In ASSS, the first iteration will likely add 0 slices.
                for (int pri = 0; pri < _avail.Length; pri++)
                {
                    _avail[pri] = _limit * _settings.PriorityLimits[pri] / 100 / Granularity;
                    if (_avail[pri] > _settings.MaxAvail)
                        _avail[pri] = _settings.MaxAvail;
                }
            }

            #region IBandwidthLimiter Members

            public void Iter(DateTime now)
            {
                int slices = (int)((now - _sinceTime).Duration() / s_sliceTimeSpan);
                _sinceTime += s_sliceTimeSpan * slices;

                for (int pri = 0; pri < _avail.Length; pri++)
                {
                    _avail[pri] += slices * (_limit * _settings.PriorityLimits[pri] / 100) / Granularity;
                    if (_avail[pri] > _settings.MaxAvail)
                        _avail[pri] = _settings.MaxAvail;
                }
            }

            public bool Check(int bytes, BandwidthPriority priority)
            {
                if (bytes <= 0)
                    return true;

                int pri = (int)priority;
                if (pri >= _avail.Length)
                    pri = _avail.Length - 1;

                Span<int> availCopy = stackalloc int[_avail.Length];
                _avail.CopyTo(availCopy);

                for (; pri >= 0; pri--)
                {
                    if (availCopy[pri] >= bytes)
                    {
                        availCopy[pri] -= bytes;
                        availCopy.CopyTo(_avail);
                        return true;
                    }
                    else
                    {
                        bytes -= availCopy[pri];
                        availCopy[pri] = 0;
                    }
                }

                _hitLimit = true;
                return false;
            }

            public void AdjustForAck()
            {
                if (_settings.UseHitLimit && _hitLimit)
                {
                    _limit += 4 * _settings.LimitScale * _settings.LimitScale / _limit;
                    _hitLimit = false;
                }
                else
                {
                    _limit += _settings.LimitScale * _settings.LimitScale / _limit;
                }

                _limit = Math.Clamp(_limit, _settings.LimitLow, _settings.LimitHigh);
            }

            public void AdjustForRetry()
            {
                _limit += (int)Math.Sqrt(_limit * _limit - 4 * _settings.LimitScale * _settings.LimitScale);
                _limit /= 2;
                _limit = Math.Clamp(_limit, _settings.LimitLow, _settings.LimitHigh);
            }

            public int GetCanBufferPackets()
            {
                int canSend = _limit / Constants.MaxPacket;
                canSend = Math.Clamp(canSend, 1, _settings.ClientCanBuffer);
                return canSend;
            }

            public void GetInfo(StringBuilder sb)
            {
                sb?.Append($"{_limit} B/s, burst {_settings.MaxAvail} B");
            }

            #endregion
        }

        #endregion
    }
}
