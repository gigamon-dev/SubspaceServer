using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides compatibility to use bots written for subgame commands.
    /// </summary>
    [CoreModuleInfo]
    public class SubgameCompatibility : IModule
    {
        private IChat _chat;
        private ICommandManager _commandManager;
        private ILagQuery _lagQuery;
        private IObjectPoolManager _objectPoolManager;

        private IBilling _billing;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IChat chat,
            ICommandManager commandManager,
            ILagQuery lagQuery,
            IObjectPoolManager objectPoolManager)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _lagQuery = lagQuery ?? throw new ArgumentNullException(nameof(lagQuery));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _billing = broker.GetInterface<IBilling>();

            _commandManager.AddCommand("sg_einfo", Command_sg_einfo);
            _commandManager.AddCommand("sg_tinfo", Command_sg_tinfo);
            _commandManager.AddCommand("sg_lag", Command_sg_lag);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _commandManager.RemoveCommand("sg_einfo", Command_sg_einfo);
            _commandManager.RemoveCommand("sg_tinfo", Command_sg_tinfo);
            _commandManager.RemoveCommand("sg_lag", Command_sg_lag);

            if (_billing is not null)
            {
                broker.ReleaseInterface(ref _billing);
            }

            return true;
        }

        #endregion

        #region Commands

        private void Command_sg_einfo(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
            {
                targetPlayer = player;
            }

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append($"{targetPlayer.Name}: UserId: ");

                if (_billing is not null && _billing.TryGetUserId(targetPlayer, out uint userId))
                {
                    sb.Append(userId);
                }
                else
                {
                    sb.Append("n/a");
                }

                // TODO: Proxy, Idle
                sb.Append($"  Res: {targetPlayer.Xres}x{targetPlayer.Yres}  Client: {targetPlayer.ClientName}  Proxy: (unknown)  Idle: (unknown)");

                if (targetPlayer.IsStandard)
                {
                    int? drift = _lagQuery.QueryTimeSyncDrift(targetPlayer);
                    sb.Append($"  Timer drift: ");
                    if (drift is not null)
                    {
                        sb.Append(drift.Value);
                    }
                    else
                    {
                        sb.Append("n/a");
                    }
                }

                _chat.SendMessage(player, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }            
        }

        private void Command_sg_tinfo(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
            {
                targetPlayer = player;
            }

            if (!targetPlayer.IsStandard)
                return;

            List<TimeSyncRecord> records = new(); // TODO: pool
            _lagQuery.QueryTimeSyncHistory(targetPlayer, records);

            if (records.Count == 0)
                return;

            _chat.SendMessage(player, $"{"ServerTime",11} {"UserTime",11} {"Diff",11}");

            foreach (TimeSyncRecord record in records)
            {
                _chat.SendMessage(player, $"{record.ServerTime,11} {record.ClientTime,11} {record.ServerTime - record.ClientTime,11}");
            }
        }

        private void Command_sg_lag(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
            {
                targetPlayer = player;
            }

            _lagQuery.QueryPositionPing(targetPlayer, out PingSummary positionPing);
            _lagQuery.QueryClientPing(targetPlayer, out ClientPingSummary clientPing);
            _lagQuery.QueryReliablePing(targetPlayer, out PingSummary reliablePing);
            _lagQuery.QueryPacketloss(targetPlayer, out PacketlossSummary packetloss);

            int current = (positionPing.Current + clientPing.Current + 2 * reliablePing.Current) / 4;
            int average = (positionPing.Average + clientPing.Average + 2 * reliablePing.Average) / 4;
            int low = Math.Min(Math.Min(positionPing.Min, clientPing.Min), reliablePing.Min);
            int high = Math.Min(Math.Min(positionPing.Max, clientPing.Max), reliablePing.Max);

            _chat.SendMessage(player, $"PING Current:{current} ms  Average:{average} ms  Low:{low} ms  High:{high} ms  S2C:{packetloss.s2c * 100d,4:F1}%  C2S:{packetloss.c2s * 100d,4:F1}%");
        }

        #endregion
    }
}
