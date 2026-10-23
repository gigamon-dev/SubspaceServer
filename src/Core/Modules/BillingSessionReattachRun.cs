using SS.Packets.Billing;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    /// <summary>
    /// How a player that was given to a <see cref="BillingSessionReattachRun"/> came out of it.
    /// </summary>
    internal enum BillingSessionReattachOutcome
    {
        /// <summary>
        /// The billing server answered. See <see cref="BillingSessionReattachResult.Status"/>.
        /// </summary>
        Answered,

        /// <summary>
        /// The billing server answered, but only after the player was reported as <see cref="TimedOut"/>.
        /// </summary>
        AnsweredLate,

        /// <summary>
        /// The player was sent to the billing server and it has not answered in time.
        /// It may still have given the session back, and may still answer.
        /// </summary>
        TimedOut,

        /// <summary>
        /// The time allowed ran out before the player could be sent to the billing server.
        /// </summary>
        NotSent,
    }

    /// <summary>
    /// The outcome for one player of a <see cref="BillingSessionReattachRun"/>.
    /// </summary>
    /// <param name="ConnectionId">The id the billing server knows the player by.</param>
    /// <param name="Outcome">How the player came out of the run.</param>
    /// <param name="Status">The billing server's answer. Only meaningful if it answered.</param>
    internal readonly record struct BillingSessionReattachResult(int ConnectionId, BillingSessionReattachOutcome Outcome, SessionReattachStatus Status);

    /// <summary>
    /// Asks a billing server to give back the sessions of the players who were logged in through it
    /// when the connection was lost, once the connection is restored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the bookkeeping only: which players go in which packet, how many packets may be waiting
    /// on an answer, and what became of each player. It does no I/O and reads no clock, so the caller
    /// sends what it writes, feeds it what arrives, and tells it the time.
    /// </para>
    /// <para>
    /// The number of packets waiting on an answer is the only thing that limits how fast players are
    /// sent. A billing server serves many zones and a zone cannot see the others, so there is no rate
    /// that is safe to pick here. The next packet goes out when an answer comes in.
    /// </para>
    /// <para>
    /// Not thread-safe. The caller is expected to synchronize.
    /// </para>
    /// </remarks>
    internal sealed class BillingSessionReattachRun
    {
        /// <summary>
        /// The most players that fit in a packet without it having to be sent as a big packet.
        /// </summary>
        public const int EntriesPerPacketLimit = 12;

        /// <summary>
        /// How many times a player is sent when the billing server keeps answering <see cref="SessionReattachStatus.TryAgain"/>.
        /// </summary>
        public const int MaxAttempts = 3;

        private readonly int _maxEntries;
        private readonly int _maxUnanswered;
        private readonly TimeSpan _packetTimeout;
        private readonly DateTime _stopTime;

        private readonly Queue<Entry> _queue = new();
        private readonly Dictionary<ushort, Packet> _unanswered = [];
        private readonly Dictionary<ushort, Packet> _timedOut = [];

        // The sequence is 16 bits on the wire. Kept wider to tell when it has run out.
        private int _nextSequence;
        private uint _entriesSent;
        private bool _stopped;

        /// <summary>
        /// Initializes a new run.
        /// </summary>
        /// <param name="maxEntries">The most players the billing server accepts in one packet.</param>
        /// <param name="maxUnanswered">The most packets the billing server allows to be waiting on an answer.</param>
        /// <param name="packetTimeout">How long to wait for the answer to a packet.</param>
        /// <param name="stopTime">When to give up on whatever is not yet answered, so that the end can be sent in the time the billing server allows.</param>
        public BillingSessionReattachRun(int maxEntries, int maxUnanswered, TimeSpan packetTimeout, DateTime stopTime)
        {
            _maxEntries = Math.Clamp(maxEntries, 1, EntriesPerPacketLimit);
            _maxUnanswered = Math.Max(maxUnanswered, 1);
            _packetTimeout = packetTimeout;
            _stopTime = stopTime;
        }

        /// <summary>
        /// The size of the largest packet <see cref="TryWriteNextPacket"/> can write.
        /// </summary>
        public static int MaxPacketLength => S2B_SessionReattachHeader.Length + (EntriesPerPacketLimit * S2B_SessionReattachEntry.Length);

        /// <summary>
        /// How many packets are waiting on an answer.
        /// </summary>
        public int UnansweredCount => _unanswered.Count;

        /// <summary>
        /// How many players are waiting to be sent.
        /// </summary>
        public int QueuedCount => _queue.Count;

        /// <summary>
        /// Whether there is nothing left to send and nothing left to wait for, which is when the end is to be sent.
        /// </summary>
        public bool IsComplete => _queue.Count == 0 && _unanswered.Count == 0;

        /// <summary>
        /// Adds a player to ask the billing server about.
        /// </summary>
        /// <param name="connectionId">The id the billing server knows the player by.</param>
        /// <param name="userId">The id the billing server gave for the player when they logged in.</param>
        /// <param name="name">The name the billing server gave for the player when they logged in, as it sent it.</param>
        public void Add(int connectionId, uint userId, ReadOnlySpan<byte> name)
        {
            _queue.Enqueue(new Entry(connectionId, userId, name));
        }

        /// <summary>
        /// Writes the next packet to send, if one may be sent now.
        /// </summary>
        /// <param name="now">The current time.</param>
        /// <param name="buffer">Where to write the packet. <see cref="MaxPacketLength"/> is always enough.</param>
        /// <param name="bytesWritten">The length of the packet.</param>
        /// <returns><see langword="true"/> if a packet was written, and is now waiting on an answer.</returns>
        public bool TryWriteNextPacket(DateTime now, Span<byte> buffer, out int bytesWritten)
        {
            bytesWritten = 0;

            if (_stopped
                || now >= _stopTime
                || _queue.Count == 0
                || _unanswered.Count >= _maxUnanswered
                || _nextSequence > ushort.MaxValue)
            {
                return false;
            }

            int count = Math.Min(_queue.Count, _maxEntries);
            int length = S2B_SessionReattachHeader.Length + (count * S2B_SessionReattachEntry.Length);
            if (buffer.Length < length)
                return false;

            ushort sequence = (ushort)_nextSequence++;
            Packet packet = new(now, count);

            MemoryMarshal.AsRef<S2B_SessionReattachHeader>(buffer) = new(sequence, (byte)count);
            Span<S2B_SessionReattachEntry> entries = MemoryMarshal.Cast<byte, S2B_SessionReattachEntry>(buffer[S2B_SessionReattachHeader.Length..length]);

            for (int i = 0; i < count; i++)
            {
                Entry entry = _queue.Dequeue();
                entry.Attempts++;
                entries[i] = new(entry.ConnectionId, entry.UserId, entry.Name);
                packet.Entries.Add(entry);
            }

            _unanswered.Add(sequence, packet);
            _entriesSent += (uint)count;
            bytesWritten = length;
            return true;
        }

        /// <summary>
        /// Takes a player out of the run, because they are no longer there to be logged in.
        /// </summary>
        /// <remarks>
        /// Nothing more is reported for the player. If they were already sent, the billing server may
        /// well give the session back, to a player who has left. It handles what a zone sends in order,
        /// so logging the player off now ends that session as soon as it exists.
        /// </remarks>
        /// <param name="connectionId">The id the billing server knows the player by.</param>
        /// <returns>
        /// <see langword="true"/> if the player was sent to the billing server and it has yet to answer,
        /// in which case the caller is to log the player off.
        /// </returns>
        public bool Remove(int connectionId)
        {
            if (MarkLeft(_unanswered, connectionId) || MarkLeft(_timedOut, connectionId))
                return true;

            int count = _queue.Count;
            for (int i = 0; i < count; i++)
            {
                Entry entry = _queue.Dequeue();
                if (entry.ConnectionId != connectionId)
                    _queue.Enqueue(entry);
            }

            return false;

            static bool MarkLeft(Dictionary<ushort, Packet> packets, int connectionId)
            {
                foreach (Packet packet in packets.Values)
                {
                    Span<Entry> entries = CollectionsMarshal.AsSpan(packet.Entries);
                    for (int i = 0; i < entries.Length; i++)
                    {
                        if (entries[i].ConnectionId == connectionId && !entries[i].HasLeft)
                        {
                            entries[i].HasLeft = true;
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Processes an answer from the billing server.
        /// </summary>
        /// <remarks>
        /// A player the billing server could not answer for is sent again rather than reported, unless
        /// that has been tried enough or there is no time left.
        /// </remarks>
        /// <param name="now">The current time.</param>
        /// <param name="data">The packet, starting with the type.</param>
        /// <param name="results">Where to add what became of each player that was answered for.</param>
        /// <returns>
        /// <see langword="false"/> if the packet is not the answer to a packet that is waiting on one.
        /// Nothing is changed in that case, so the packet it was meant for is left to time out.
        /// </returns>
        public bool TryProcessResponse(DateTime now, ReadOnlySpan<byte> data, ICollection<BillingSessionReattachResult> results)
        {
            if (data.Length < B2S_SessionReattachResponseHeader.Length)
                return false;

            ref readonly B2S_SessionReattachResponseHeader header = ref MemoryMarshal.AsRef<B2S_SessionReattachResponseHeader>(data);
            if (header.Type != (byte)B2SPacketType.SessionReattachResponse)
                return false;

            if (data.Length != B2S_SessionReattachResponseHeader.Length + (header.Count * B2S_SessionReattachResponseEntry.Length))
                return false;

            ushort sequence = header.Sequence;
            bool isLate = false;
            if (!_unanswered.TryGetValue(sequence, out Packet? packet))
            {
                if (!_timedOut.TryGetValue(sequence, out packet))
                    return false;

                isLate = true;
            }

            ReadOnlySpan<B2S_SessionReattachResponseEntry> answers = MemoryMarshal.Cast<byte, B2S_SessionReattachResponseEntry>(data[B2S_SessionReattachResponseHeader.Length..]);
            if (answers.Length != packet.Entries.Count)
                return false;

            // The answers are for the players that were sent, in the order they were sent.
            for (int i = 0; i < answers.Length; i++)
            {
                if (answers[i].ConnectionId != packet.Entries[i].ConnectionId)
                    return false;
            }

            if (isLate)
                _timedOut.Remove(sequence);
            else
                _unanswered.Remove(sequence);

            bool canRetry = !isLate && !_stopped && now < _stopTime;

            for (int i = 0; i < answers.Length; i++)
            {
                Entry entry = packet.Entries[i];
                if (entry.HasLeft)
                    continue;

                SessionReattachStatus status = answers[i].Status;

                if (status == SessionReattachStatus.TryAgain && canRetry && entry.Attempts < MaxAttempts)
                {
                    _queue.Enqueue(entry);
                    continue;
                }

                results.Add(new(entry.ConnectionId, isLate ? BillingSessionReattachOutcome.AnsweredLate : BillingSessionReattachOutcome.Answered, status));
            }

            return true;
        }

        /// <summary>
        /// Gives up on packets that have waited too long for an answer, and on everything once the time to stop has come.
        /// </summary>
        /// <param name="now">The current time.</param>
        /// <param name="results">Where to add what became of each player that was given up on.</param>
        public void CheckTimeouts(DateTime now, ICollection<BillingSessionReattachResult> results)
        {
            if (!_stopped && now >= _stopTime)
            {
                _stopped = true;

                while (_queue.TryDequeue(out Entry entry))
                {
                    // One that was sent before, and is here to be sent again, was answered. So nothing is pending for it.
                    results.Add(new(entry.ConnectionId, BillingSessionReattachOutcome.NotSent, default));
                }
            }

            if (_unanswered.Count == 0)
                return;

            List<ushort>? expired = null;

            foreach ((ushort sequence, Packet packet) in _unanswered)
            {
                if (_stopped || now - packet.SentTime >= _packetTimeout)
                    (expired ??= []).Add(sequence);
            }

            if (expired is null)
                return;

            foreach (ushort sequence in expired)
            {
                _unanswered.Remove(sequence, out Packet? packet);
                _timedOut.Add(sequence, packet!);

                foreach (Entry entry in packet!.Entries)
                {
                    if (!entry.HasLeft)
                        results.Add(new(entry.ConnectionId, BillingSessionReattachOutcome.TimedOut, default));
                }
            }
        }

        /// <summary>
        /// Writes the packet that tells the billing server the zone is done asking.
        /// </summary>
        /// <param name="buffer">Where to write the packet. Must be at least <see cref="S2B_SessionReattachEnd.Length"/>.</param>
        /// <returns>The length of the packet.</returns>
        public int WriteEnd(Span<byte> buffer)
        {
            MemoryMarshal.AsRef<S2B_SessionReattachEnd>(buffer) = new((ushort)_nextSequence, _entriesSent);
            return S2B_SessionReattachEnd.Length;
        }

        /// <summary>
        /// Reads a <see cref="B2S_BillerCapabilities"/> packet.
        /// </summary>
        /// <param name="data">The packet, starting with the type.</param>
        /// <param name="capabilities">The packet.</param>
        /// <returns><see langword="false"/> if it is not one, or is too short to be one.</returns>
        public static bool TryReadCapabilities(ReadOnlySpan<byte> data, out B2S_BillerCapabilities capabilities)
        {
            // Longer is allowed, for a billing server that has more to say than is known about here.
            if (data.Length < B2S_BillerCapabilities.Length || data[0] != (byte)B2SPacketType.BillerCapabilities)
            {
                capabilities = default;
                return false;
            }

            capabilities = MemoryMarshal.AsRef<B2S_BillerCapabilities>(data);
            return true;
        }

        private struct Entry(int connectionId, uint userId, ReadOnlySpan<byte> name)
        {
            public readonly int ConnectionId = connectionId;
            public readonly uint UserId = userId;
            public readonly S2B_SessionReattachEntry.NameInlineArray Name = new(name);
            public int Attempts;
            public bool HasLeft;
        }

        private sealed class Packet(DateTime sentTime, int capacity)
        {
            public readonly DateTime SentTime = sentTime;
            public readonly List<Entry> Entries = new(capacity);
        }
    }
}
