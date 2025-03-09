using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules.Scoring
{
    /// <summary>
    /// Module that keeps track of a per-arena point jackpot that can be awarded to players that win a game.
    /// The jackpot can be configured to increment based on kills (Kill:JackpotBountyPercent in arena.conf).
    /// It also provides the <see cref="IJackpot"/> interface to provide access to other modules.
    /// <para>
    /// The jackpot value will be saved and restored across arena reloads if the <see cref="Persist"/> module is being used.
    /// </para>
    /// </summary>
    [CoreModuleInfo]
    public sealed class Jackpot : IAsyncModule, IJackpot
    {
        // required dependencies
        private readonly IArenaManager _arenaManager;
        private readonly IConfigManager _configManager;

        // optional dependencies
        private IPersist? _persist;

        private InterfaceRegistrationToken<IJackpot>? _jackpotRegistrationToken;

        private ArenaDataKey<ArenaData> _adKey;
        private DelegatePersistentData<Arena>? _persistRegistration;

        public Jackpot(
            IArenaManager arenaManager,
            IConfigManager configManager)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        }

        #region Module members

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);
            KillCallback.Register(broker, Callback_Kill);

            _persist = broker.GetInterface<IPersist>();

            if (_persist != null)
            {
                _persistRegistration = new((int)PersistKey.Jackpot, PersistInterval.Game, PersistScope.PerArena, Persist_GetData, Persist_SetData, Persist_ClearData);
                await _persist.RegisterPersistentDataAsync(_persistRegistration);
            }

            _jackpotRegistrationToken = broker.RegisterInterface<IJackpot>(this);

            return true;
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _jackpotRegistrationToken) != 0)
                return false;

            if (_persist != null)
            {
                if (_persistRegistration != null)
                    await _persist.UnregisterPersistentDataAsync(_persistRegistration);

                broker.ReleaseInterface(ref _persist);
            }

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            KillCallback.Unregister(broker, Callback_Kill);

            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        #endregion

        #region IJackpot

        void IJackpot.ResetJackpot(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            lock (ad.Lock)
            {
                ad.Jackpot = 0;
            }
        }

        void IJackpot.AddJackpot(Arena arena, int points)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            lock (ad.Lock)
            {
                ad.Jackpot += points;
            }
        }

        int IJackpot.GetJackpot(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return 0;

            lock (ad.Lock)
            {
                return ad.Jackpot;
            }
        }

        void IJackpot.SetJackpot(Arena arena, int points)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            lock (ad.Lock)
            {
                ad.Jackpot = points;
            }
        }

        #endregion

        #region Callbacks

        [ConfigHelp<int>("Kill", "JackpotBountyPercent", ConfigScope.Arena, Default = 0,
            Description = "The percent of a player's bounty added to the jackpot on each kill. Units: 0.1%.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                lock (ad.Lock)
                {
                    ad.BountyRatio = _configManager.GetInt(arena.Cfg!, "Kill", "JackpotBountyPercent", ConfigHelp.Constants.Arena.Kill.JackpotBountyPercent.Default) / 1000d;
                }
            }
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            lock (ad.Lock)
            {
                ad.Jackpot += (int)(bounty * ad.BountyRatio);
            }
        }

        #endregion

        #region Persist

        private void Persist_GetData(Arena? arena, Stream outStream)
        {
            if (arena is null)
                return;

            int points = ((IJackpot)this).GetJackpot(arena);

            if (points > 0)
            {
                Span<byte> data = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(data, points);
                outStream.Write(data);
            }
        }

        private void Persist_SetData(Arena? arena, Stream inStream)
        {
            if (arena is null)
                return;

            Span<byte> data = stackalloc byte[4];
            Span<byte> remaining = data;
            int bytesRead;

            while (remaining.Length > 0
                && (bytesRead = inStream.Read(data)) > 0)
            {
                remaining = remaining[bytesRead..];
            }

            if (remaining.Length != 0)
            {
                ((IJackpot)this).ResetJackpot(arena);
            }
            else
            {
                int points = BinaryPrimitives.ReadInt32LittleEndian(data);
                ((IJackpot)this).SetJackpot(arena, points);
            }
        }

        private void Persist_ClearData(Arena? arena)
        {
            if (arena is not null)
            {
                ((IJackpot)this).ResetJackpot(arena);
            }
        }

        #endregion

        private class ArenaData : IResettable
        {
            // setting
            public double BountyRatio;

            // state
            public int Jackpot;

            public readonly Lock Lock = new();

            public bool TryReset()
            {
                lock (Lock)
                {
                    BountyRatio = 0;
                    Jackpot = 0;
                }

                return true;
            }
        }
    }
}
