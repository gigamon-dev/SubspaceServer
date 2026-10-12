using SS.Core.Modules;
using SS.Packets.Billing;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SS.Core.Tests
{
    public class BillingSessionReattachRunTests
    {
        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan PacketTimeout = TimeSpan.FromSeconds(30);
        private static readonly DateTime StopTime = Start.AddSeconds(175);

        private static BillingSessionReattachRun CreateRun(int maxEntries = 12, int maxUnanswered = 4, int players = 0)
        {
            BillingSessionReattachRun run = new(maxEntries, maxUnanswered, PacketTimeout, StopTime);

            for (int i = 0; i < players; i++)
                run.Add(i, (uint)(1000 + i), Encoding.ASCII.GetBytes($"player{i}"));

            return run;
        }

        /// <summary>
        /// Writes the next packet, which the test expects there to be.
        /// </summary>
        private static byte[] WritePacket(BillingSessionReattachRun run, DateTime now)
        {
            byte[] buffer = new byte[BillingSessionReattachRun.MaxPacketLength];
            Assert.True(run.TryWriteNextPacket(now, buffer, out int bytesWritten));
            return buffer[..bytesWritten];
        }

        private static bool CanWritePacket(BillingSessionReattachRun run, DateTime now)
        {
            return run.TryWriteNextPacket(now, new byte[BillingSessionReattachRun.MaxPacketLength], out _);
        }

        /// <summary>
        /// Builds the answer to a packet, with the same status for each of its players.
        /// </summary>
        private static byte[] BuildResponse(ReadOnlySpan<byte> request, byte status)
        {
            int count = request[3];
            byte[] response = new byte[4 + (count * 5)];
            response[0] = 0x36;
            request.Slice(1, 2).CopyTo(response.AsSpan(1));
            response[3] = (byte)count;

            for (int i = 0; i < count; i++)
            {
                request.Slice(4 + (i * 40), 4).CopyTo(response.AsSpan(4 + (i * 5)));
                response[4 + (i * 5) + 4] = status;
            }

            return response;
        }

        #region Packet layouts

        [Fact]
        public void PacketSizes()
        {
            Assert.Equal(4, S2B_SessionReattachHeader.Length);
            Assert.Equal(40, S2B_SessionReattachEntry.Length);
            Assert.Equal(7, S2B_SessionReattachEnd.Length);
            Assert.Equal(9, B2S_BillerCapabilities.Length);
            Assert.Equal(4, B2S_SessionReattachResponseHeader.Length);
            Assert.Equal(5, B2S_SessionReattachResponseEntry.Length);
            Assert.Equal(4 + (40 * 12), BillingSessionReattachRun.MaxPacketLength);
        }

        [Theory]
        [InlineData(true, 0x0F)]
        [InlineData(false, 0x07)]
        public void ServerCapabilities_SessionReattachBit(bool supportsSessionReattach, byte expectedMask)
        {
            S2B_ServerCapabilities packet = new(multiCastChat: true, supportDemographics: true, supportsBanEnforcement: true, supportsSessionReattach: supportsSessionReattach);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));

            Assert.Equal(new byte[] { 0x15, expectedMask, 0, 0, 0 }, bytes.ToArray());
            Assert.Equal(supportsSessionReattach, packet.SupportsSessionReattach);
        }

        [Fact]
        public void Reattach_Layout()
        {
            BillingSessionReattachRun run = CreateRun();
            run.Add(0x01020304, 0x0A0B0C0D, "Alice"u8);
            run.Add(7, 99, "Bob"u8);

            byte[] packet = WritePacket(run, Start);

            Assert.Equal(4 + (2 * 40), packet.Length);
            Assert.Equal(0x16, packet[0]);
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1)));
            Assert.Equal(2, packet[3]);

            // First entry: connection id, user id, name. All little-endian, the name padded with zeroes.
            Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, packet[4..8]);
            Assert.Equal(new byte[] { 0x0D, 0x0C, 0x0B, 0x0A }, packet[8..12]);
            Assert.Equal("Alice"u8.ToArray(), packet[12..17]);
            Assert.All(packet[17..44], b => Assert.Equal(0, b));

            // Second entry.
            Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(44)));
            Assert.Equal(99u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(48)));
            Assert.Equal("Bob"u8.ToArray(), packet[52..55]);
            Assert.All(packet[55..84], b => Assert.Equal(0, b));
        }

        [Fact]
        public void Reattach_NameAsSentByTheBillingServer()
        {
            // The billing server gives a name as 24 bytes. One that fills them has no terminator.
            byte[] full = Encoding.ASCII.GetBytes(new string('x', 24));
            byte[] terminated = new byte[24];
            "Carol"u8.CopyTo(terminated);
            terminated[10] = (byte)'!'; // Not part of the name, since it is after the terminator.

            BillingSessionReattachRun run = CreateRun();
            run.Add(1, 1, full);
            run.Add(2, 2, terminated);

            byte[] packet = WritePacket(run, Start);

            Assert.Equal(full, packet[12..36]);
            Assert.All(packet[36..44], b => Assert.Equal(0, b));

            Assert.Equal("Carol"u8.ToArray(), packet[52..57]);
            Assert.All(packet[57..84], b => Assert.Equal(0, b));
        }

        [Fact]
        public void Reattach_SequenceCountsUpFromZero()
        {
            BillingSessionReattachRun run = CreateRun(maxEntries: 12, maxUnanswered: 4, players: 30);

            byte[] first = WritePacket(run, Start);
            byte[] second = WritePacket(run, Start);
            byte[] third = WritePacket(run, Start);

            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(1)));
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(second.AsSpan(1)));
            Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(third.AsSpan(1)));

            Assert.Equal(12, first[3]);
            Assert.Equal(12, second[3]);
            Assert.Equal(6, third[3]);
            Assert.Equal(4 + (6 * 40), third.Length);

            // In the order they were added.
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(first.AsSpan(4)));
            Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(second.AsSpan(4)));
            Assert.Equal(29, BinaryPrimitives.ReadInt32LittleEndian(third.AsSpan(4 + (5 * 40))));
        }

        [Theory]
        [InlineData(0, 1)] // At least one, or nothing could be sent.
        [InlineData(5, 5)]
        [InlineData(12, 12)]
        [InlineData(200, 12)] // No more than fit in a packet.
        public void Reattach_EntriesPerPacket(int maxEntries, int expectedInFullPacket)
        {
            BillingSessionReattachRun run = CreateRun(maxEntries: maxEntries, players: 50);

            byte[] packet = WritePacket(run, Start);

            Assert.Equal(expectedInFullPacket, packet[3]);
        }

        [Fact]
        public void Reattach_BufferTooSmall()
        {
            BillingSessionReattachRun run = CreateRun(players: 2);

            Assert.False(run.TryWriteNextPacket(Start, new byte[4 + 40], out int bytesWritten));
            Assert.Equal(0, bytesWritten);
            Assert.Equal(2, run.QueuedCount);
            Assert.Equal(0, run.UnansweredCount);
        }

        [Fact]
        public void End_Layout()
        {
            BillingSessionReattachRun run = CreateRun(maxEntries: 12, maxUnanswered: 4, players: 30);
            List<BillingSessionReattachResult> results = [];

            while (!run.IsComplete)
            {
                byte[] packet = WritePacket(run, Start);
                Assert.True(run.TryProcessResponse(Start, BuildResponse(packet, 0), results));
            }

            byte[] end = new byte[S2B_SessionReattachEnd.Length];
            Assert.Equal(7, run.WriteEnd(end));

            Assert.Equal(0x17, end[0]);
            Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(1)));
            Assert.Equal(30u, BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(3)));
        }

        [Fact]
        public void End_NobodyToAskFor()
        {
            BillingSessionReattachRun run = CreateRun();

            Assert.True(run.IsComplete);
            Assert.False(CanWritePacket(run, Start));

            byte[] end = new byte[S2B_SessionReattachEnd.Length];
            run.WriteEnd(end);

            Assert.Equal(new byte[] { 0x17, 0, 0, 0, 0, 0, 0 }, end);
        }

        #endregion

        #region Capabilities

        [Fact]
        public void Capabilities_Parse()
        {
            // type, mask (4), window in seconds (2), max entries, max unanswered
            byte[] data = [0x35, 0x01, 0x00, 0x00, 0x00, 0xB4, 0x00, 12, 4];

            Assert.True(BillingSessionReattachRun.TryReadCapabilities(data, out B2S_BillerCapabilities capabilities));
            Assert.True(capabilities.SupportsSessionReattach);
            Assert.Equal(180, capabilities.ReattachWindowSeconds);
            Assert.Equal(12, capabilities.ReattachMaxEntries);
            Assert.Equal(4, capabilities.ReattachMaxUnanswered);
        }

        [Fact]
        public void Capabilities_WindowIsLittleEndian()
        {
            byte[] data = [0x35, 0x01, 0x00, 0x00, 0x00, 0x2C, 0x01, 12, 4];

            Assert.True(BillingSessionReattachRun.TryReadCapabilities(data, out B2S_BillerCapabilities capabilities));
            Assert.Equal(300, capabilities.ReattachWindowSeconds);
        }

        [Theory]
        [InlineData(new byte[] { 0x35, 0x00, 0x00, 0x00, 0x00, 0xB4, 0x00, 12, 4 })]
        [InlineData(new byte[] { 0x35, 0xFE, 0xFF, 0xFF, 0xFF, 0xB4, 0x00, 12, 4 })] // Every bit but the one that matters.
        public void Capabilities_NotSupported(byte[] data)
        {
            Assert.True(BillingSessionReattachRun.TryReadCapabilities(data, out B2S_BillerCapabilities capabilities));
            Assert.False(capabilities.SupportsSessionReattach);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(8)]
        public void Capabilities_TooShort(int length)
        {
            byte[] data = new byte[length];
            if (length > 0)
                data[0] = 0x35;

            Assert.False(BillingSessionReattachRun.TryReadCapabilities(data, out _));
        }

        [Fact]
        public void Capabilities_LongerIsAllowed()
        {
            byte[] data = [0x35, 0x01, 0x00, 0x00, 0x00, 0xB4, 0x00, 12, 4, 0xAA, 0xBB];

            Assert.True(BillingSessionReattachRun.TryReadCapabilities(data, out B2S_BillerCapabilities capabilities));
            Assert.True(capabilities.SupportsSessionReattach);
            Assert.Equal(4, capabilities.ReattachMaxUnanswered);
        }

        [Fact]
        public void Capabilities_WrongType()
        {
            byte[] data = [0x36, 0x01, 0x00, 0x00, 0x00, 0xB4, 0x00, 12, 4];

            Assert.False(BillingSessionReattachRun.TryReadCapabilities(data, out _));
        }

        #endregion

        #region Unanswered packet limit

        [Fact]
        public void Unanswered_LimitIsNotExceeded()
        {
            BillingSessionReattachRun run = CreateRun(maxEntries: 12, maxUnanswered: 4, players: 100);

            for (int i = 0; i < 4; i++)
                WritePacket(run, Start);

            Assert.Equal(4, run.UnansweredCount);
            Assert.Equal(100 - 48, run.QueuedCount);

            // Not for any amount of waiting short of the timeout. Only an answer makes room.
            Assert.False(CanWritePacket(run, Start));
            Assert.False(CanWritePacket(run, Start.AddSeconds(29)));
        }

        [Fact]
        public void Unanswered_EachAnswerMakesRoomForOne()
        {
            BillingSessionReattachRun run = CreateRun(maxEntries: 12, maxUnanswered: 4, players: 100);
            List<BillingSessionReattachResult> results = [];
            Queue<byte[]> sent = new();

            for (int i = 0; i < 4; i++)
                sent.Enqueue(WritePacket(run, Start));

            int packets = 4;

            while (sent.TryDequeue(out byte[]? request))
            {
                Assert.True(run.TryProcessResponse(Start, BuildResponse(request, 0), results));

                if (run.QueuedCount > 0)
                {
                    sent.Enqueue(WritePacket(run, Start));
                    packets++;

                    Assert.Equal(4, run.UnansweredCount);
                    Assert.False(CanWritePacket(run, Start));
                }
            }

            Assert.Equal(9, packets); // 100 players, 12 to a packet
            Assert.Equal(100, results.Count);
            Assert.True(run.IsComplete);
        }

        [Fact]
        public void Unanswered_AtLeastOneIsAllowed()
        {
            BillingSessionReattachRun run = CreateRun(maxEntries: 12, maxUnanswered: 0, players: 30);

            WritePacket(run, Start);
            Assert.False(CanWritePacket(run, Start));
        }

        [Fact]
        public void Answers_InAnyOrder()
        {
            BillingSessionReattachRun run = CreateRun(maxEntries: 2, maxUnanswered: 3, players: 6);
            List<BillingSessionReattachResult> results = [];

            byte[] first = WritePacket(run, Start);
            byte[] second = WritePacket(run, Start);
            byte[] third = WritePacket(run, Start);

            Assert.True(run.TryProcessResponse(Start, BuildResponse(third, 0), results));
            Assert.Equal([4, 5], results.Select(r => r.ConnectionId));

            Assert.True(run.TryProcessResponse(Start, BuildResponse(first, 1), results));
            Assert.True(run.TryProcessResponse(Start, BuildResponse(second, 0), results));

            Assert.Equal([4, 5, 0, 1, 2, 3], results.Select(r => r.ConnectionId));
            Assert.Equal(SessionReattachStatus.NoSession, results[2].Status);
            Assert.True(run.IsComplete);
        }

        #endregion

        #region Responses

        [Theory]
        [InlineData(0, SessionReattachStatus.Accepted)]
        [InlineData(1, SessionReattachStatus.NoSession)]
        [InlineData(2, SessionReattachStatus.AlreadyAttached)]
        [InlineData(3, SessionReattachStatus.Banned)]
        [InlineData(4, SessionReattachStatus.NameUsedElsewhere)]
        [InlineData(5, SessionReattachStatus.WindowClosed)]
        [InlineData(6, SessionReattachStatus.NameMismatch)]
        public void Response_Status(byte status, SessionReattachStatus expected)
        {
            BillingSessionReattachRun run = CreateRun(players: 1);
            List<BillingSessionReattachResult> results = [];

            byte[] request = WritePacket(run, Start);
            Assert.True(run.TryProcessResponse(Start, BuildResponse(request, status), results));

            BillingSessionReattachResult result = Assert.Single(results);
            Assert.Equal(0, result.ConnectionId);
            Assert.Equal(BillingSessionReattachOutcome.Answered, result.Outcome);
            Assert.Equal(expected, result.Status);
            Assert.True(run.IsComplete);
        }

        [Theory]
        [InlineData(8)]
        [InlineData(200)]
        [InlineData(255)]
        public void Response_UnknownStatusIsPassedOnAsIs(byte status)
        {
            // What it means is not known here. The caller treats anything it does not know as not logged in.
            BillingSessionReattachRun run = CreateRun(players: 1);
            List<BillingSessionReattachResult> results = [];

            byte[] request = WritePacket(run, Start);
            Assert.True(run.TryProcessResponse(Start, BuildResponse(request, status), results));

            BillingSessionReattachResult result = Assert.Single(results);
            Assert.Equal(BillingSessionReattachOutcome.Answered, result.Outcome);
            Assert.Equal((SessionReattachStatus)status, result.Status);
            Assert.False(Enum.IsDefined(result.Status));
            Assert.True(run.IsComplete);
        }

        [Fact]
        public void Response_StatusPerPlayer()
        {
            BillingSessionReattachRun run = CreateRun(players: 3);
            List<BillingSessionReattachResult> results = [];

            byte[] request = WritePacket(run, Start);
            byte[] response = BuildResponse(request, 0);
            response[4 + 5 + 4] = 3; // The second player.

            Assert.True(run.TryProcessResponse(Start, response, results));

            Assert.Equal(
                [SessionReattachStatus.Accepted, SessionReattachStatus.Banned, SessionReattachStatus.Accepted],
                results.Select(r => r.Status));
        }

        [Fact]
        public void Response_TryAgain_IsSentAgain()
        {
            BillingSessionReattachRun run = CreateRun(players: 2);
            List<BillingSessionReattachResult> results = [];

            byte[] request = WritePacket(run, Start);
            byte[] response = BuildResponse(request, 0);
            response[4 + 5 + 4] = 7; // The second player.

            Assert.True(run.TryProcessResponse(Start, response, results));

            // The first is done with. The second is not reported, since it is not over for it.
            Assert.Equal(0, Assert.Single(results).ConnectionId);
            Assert.False(run.IsComplete);
            Assert.Equal(1, run.QueuedCount);

            byte[] again = WritePacket(run, Start);
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(again.AsSpan(1)));
            Assert.Equal(1, again[3]);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(again.AsSpan(4)));
            Assert.Equal(1001u, BinaryPrimitives.ReadUInt32LittleEndian(again.AsSpan(8)));

            Assert.True(run.TryProcessResponse(Start, BuildResponse(again, 0), results));
            Assert.Equal(2, results.Count);
            Assert.Equal(SessionReattachStatus.Accepted, results[1].Status);
            Assert.True(run.IsComplete);

            // The end counts what was sent, so the player that was sent twice is counted twice.
            byte[] end = new byte[S2B_SessionReattachEnd.Length];
            run.WriteEnd(end);
            Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(1)));
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(3)));
        }

        [Fact]
        public void Response_TryAgain_IsGivenUpOn()
        {
            BillingSessionReattachRun run = CreateRun(players: 1);
            List<BillingSessionReattachResult> results = [];

            for (int attempt = 1; attempt < BillingSessionReattachRun.MaxAttempts; attempt++)
            {
                byte[] request = WritePacket(run, Start);
                Assert.True(run.TryProcessResponse(Start, BuildResponse(request, 7), results));
                Assert.Empty(results);
            }

            byte[] last = WritePacket(run, Start);
            Assert.True(run.TryProcessResponse(Start, BuildResponse(last, 7), results));

            BillingSessionReattachResult result = Assert.Single(results);
            Assert.Equal(SessionReattachStatus.TryAgain, result.Status);
            Assert.True(run.IsComplete);
        }

        [Fact]
        public void Response_TryAgain_NotSentAgainWhenOutOfTime()
        {
            BillingSessionReattachRun run = CreateRun(players: 1);
            List<BillingSessionReattachResult> results = [];

            byte[] request = WritePacket(run, StopTime.AddSeconds(-1));
            Assert.True(run.TryProcessResponse(StopTime, BuildResponse(request, 7), results));

            Assert.Equal(SessionReattachStatus.TryAgain, Assert.Single(results).Status);
            Assert.True(run.IsComplete);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        public void Response_TooShortForTheHeader(int length)
        {
            BillingSessionReattachRun run = CreateRun(players: 1);
            List<BillingSessionReattachResult> results = [];

            byte[] response = BuildResponse(WritePacket(run, Start), 0);

            Assert.False(run.TryProcessResponse(Start, response.AsSpan(0, length), results));
            Assert.Empty(results);
            Assert.Equal(1, run.UnansweredCount);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(1)]
        [InlineData(-5)]
        [InlineData(5)]
        public void Response_LengthDoesNotMatchTheCount(int difference)
        {
            BillingSessionReattachRun run = CreateRun(players: 2);
            List<BillingSessionReattachResult> results = [];

            byte[] response = BuildResponse(WritePacket(run, Start), 0);
            byte[] resized = new byte[response.Length + difference];
            response.AsSpan(0, Math.Min(response.Length, resized.Length)).CopyTo(resized);

            Assert.False(run.TryProcessResponse(Start, resized, results));
            Assert.Empty(results);
            Assert.Equal(1, run.UnansweredCount);

            // The packet is still waiting, so the answer it should have had is still taken.
            Assert.True(run.TryProcessResponse(Start, response, results));
            Assert.Equal(2, results.Count);
        }

        [Fact]
        public void Response_CountDoesNotMatchThePacket()
        {
            BillingSessionReattachRun run = CreateRun(players: 2);
            List<BillingSessionReattachResult> results = [];

            byte[] response = BuildResponse(WritePacket(run, Start), 0);

            // One entry fewer, with a length that agrees with it.
            byte[] fewer = response[..^5];
            fewer[3] = 1;

            Assert.False(run.TryProcessResponse(Start, fewer, results));
            Assert.Empty(results);
            Assert.Equal(1, run.UnansweredCount);
        }

        [Fact]
        public void Response_UnknownSequence()
        {
            BillingSessionReattachRun run = CreateRun(players: 1);
            List<BillingSessionReattachResult> results = [];

            byte[] response = BuildResponse(WritePacket(run, Start), 0);
            response[1] = 9;

            Assert.False(run.TryProcessResponse(Start, response, results));
            Assert.Empty(results);
            Assert.Equal(1, run.UnansweredCount);
        }

        [Fact]
        public void Response_AnsweredTwice()
        {
            BillingSessionReattachRun run = CreateRun(players: 1);
            List<BillingSessionReattachResult> results = [];

            byte[] response = BuildResponse(WritePacket(run, Start), 0);

            Assert.True(run.TryProcessResponse(Start, response, results));
            Assert.False(run.TryProcessResponse(Start, response, results));
            Assert.Single(results);
        }

        [Fact]
        public void Response_ForOtherPlayers()
        {
            BillingSessionReattachRun run = CreateRun(players: 2);
            List<BillingSessionReattachResult> results = [];

            byte[] response = BuildResponse(WritePacket(run, Start), 0);
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(4 + 5), 77);

            Assert.False(run.TryProcessResponse(Start, response, results));
            Assert.Empty(results);
            Assert.Equal(1, run.UnansweredCount);
        }

        [Fact]
        public void Response_WrongType()
        {
            BillingSessionReattachRun run = CreateRun(players: 1);
            List<BillingSessionReattachResult> results = [];

            byte[] response = BuildResponse(WritePacket(run, Start), 0);
            response[0] = 0x35;

            Assert.False(run.TryProcessResponse(Start, response, results));
            Assert.Empty(results);
        }

        #endregion

        #region Timeouts

        [Fact]
        public void Timeout_Packet()
        {
            BillingSessionReattachRun run = CreateRun(maxEntries: 2, maxUnanswered: 1, players: 4);
            List<BillingSessionReattachResult> results = [];

            WritePacket(run, Start);

            run.CheckTimeouts(Start.AddSeconds(29), results);
            Assert.Empty(results);
            Assert.Equal(1, run.UnansweredCount);

            run.CheckTimeouts(Start.AddSeconds(30), results);
            Assert.Equal([0, 1], results.Select(r => r.ConnectionId));
            Assert.All(results, r => Assert.Equal(BillingSessionReattachOutcome.TimedOut, r.Outcome));
            Assert.Equal(0, run.UnansweredCount);

            // It carries on with the rest.
            byte[] second = WritePacket(run, Start.AddSeconds(30));
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(second.AsSpan(4)));

            // Once only.
            results.Clear();
            run.CheckTimeouts(Start.AddSeconds(40), results);
            Assert.Empty(results);
        }

        [Fact]
        public void Timeout_LateAnswer()
        {
            BillingSessionReattachRun run = CreateRun(players: 2);
            List<BillingSessionReattachResult> results = [];

            byte[] request = WritePacket(run, Start);
            run.CheckTimeouts(Start.AddSeconds(30), results);
            Assert.True(run.IsComplete);
            results.Clear();

            Assert.True(run.TryProcessResponse(Start.AddSeconds(31), BuildResponse(request, 0), results));

            Assert.Equal(2, results.Count);
            Assert.All(results, r =>
            {
                Assert.Equal(BillingSessionReattachOutcome.AnsweredLate, r.Outcome);
                Assert.Equal(SessionReattachStatus.Accepted, r.Status);
            });

            // And only the once.
            Assert.False(run.TryProcessResponse(Start.AddSeconds(32), BuildResponse(request, 0), results));
        }

        [Fact]
        public void Timeout_LateTryAgainIsNotSentAgain()
        {
            BillingSessionReattachRun run = CreateRun(players: 1);
            List<BillingSessionReattachResult> results = [];

            byte[] request = WritePacket(run, Start);
            run.CheckTimeouts(Start.AddSeconds(30), results);
            results.Clear();

            Assert.True(run.TryProcessResponse(Start.AddSeconds(31), BuildResponse(request, 7), results));

            Assert.Equal(SessionReattachStatus.TryAgain, Assert.Single(results).Status);
            Assert.Equal(0, run.QueuedCount);
        }

        [Fact]
        public void StopTime_GivesUpOnEverything()
        {
            BillingSessionReattachRun run = CreateRun(maxEntries: 2, maxUnanswered: 1, players: 5);
            List<BillingSessionReattachResult> results = [];

            WritePacket(run, StopTime.AddSeconds(-1));

            run.CheckTimeouts(StopTime, results);

            Assert.Equal(5, results.Count);
            Assert.Equal(
                [2, 3, 4],
                results.Where(r => r.Outcome == BillingSessionReattachOutcome.NotSent).Select(r => r.ConnectionId));
            Assert.Equal(
                [0, 1],
                results.Where(r => r.Outcome == BillingSessionReattachOutcome.TimedOut).Select(r => r.ConnectionId));

            // Which leaves the end to be sent, and nothing else.
            Assert.True(run.IsComplete);
            Assert.False(CanWritePacket(run, StopTime));

            byte[] end = new byte[S2B_SessionReattachEnd.Length];
            run.WriteEnd(end);
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(1)));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(3)));
        }

        [Fact]
        public void StopTime_NothingIsSentAfterIt()
        {
            BillingSessionReattachRun run = CreateRun(players: 5);

            // Even if the timeouts have yet to be checked.
            Assert.False(CanWritePacket(run, StopTime));
            Assert.Equal(5, run.QueuedCount);
        }

        #endregion

        #region Players leaving

        [Fact]
        public void Remove_BeforeBeingSent()
        {
            BillingSessionReattachRun run = CreateRun(players: 3);
            List<BillingSessionReattachResult> results = [];

            // Nothing was sent for the player, so there is nothing to log off.
            Assert.False(run.Remove(1));

            byte[] request = WritePacket(run, Start);
            Assert.Equal(2, request[3]);
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(4)));
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(44)));

            Assert.True(run.TryProcessResponse(Start, BuildResponse(request, 0), results));
            Assert.Equal([0, 2], results.Select(r => r.ConnectionId));
        }

        [Fact]
        public void Remove_TheOnlyPlayer()
        {
            BillingSessionReattachRun run = CreateRun(players: 1);

            Assert.False(run.Remove(0));
            Assert.True(run.IsComplete);
            Assert.False(CanWritePacket(run, Start));
        }

        [Fact]
        public void Remove_WhileUnanswered()
        {
            BillingSessionReattachRun run = CreateRun(players: 3);
            List<BillingSessionReattachResult> results = [];

            byte[] request = WritePacket(run, Start);

            // The billing server may give the session back, so the player is to be logged off.
            Assert.True(run.Remove(1));

            // The answer still has to be for all that were sent, and says nothing for the one who left.
            Assert.True(run.TryProcessResponse(Start, BuildResponse(request, 0), results));
            Assert.Equal([0, 2], results.Select(r => r.ConnectionId));
        }

        [Fact]
        public void Remove_WhileUnanswered_NotReportedAsTimedOut()
        {
            BillingSessionReattachRun run = CreateRun(players: 2);
            List<BillingSessionReattachResult> results = [];

            WritePacket(run, Start);
            Assert.True(run.Remove(0));

            run.CheckTimeouts(Start.AddSeconds(30), results);
            Assert.Equal(1, Assert.Single(results).ConnectionId);
        }

        [Fact]
        public void Remove_AfterTimingOut()
        {
            BillingSessionReattachRun run = CreateRun(players: 2);
            List<BillingSessionReattachResult> results = [];

            byte[] request = WritePacket(run, Start);
            run.CheckTimeouts(Start.AddSeconds(30), results);
            results.Clear();

            // Still not answered for, so still to be logged off.
            Assert.True(run.Remove(0));

            Assert.True(run.TryProcessResponse(Start.AddSeconds(31), BuildResponse(request, 0), results));
            Assert.Equal(1, Assert.Single(results).ConnectionId);
        }

        [Fact]
        public void Remove_OnceAnswered()
        {
            BillingSessionReattachRun run = CreateRun(players: 1);
            List<BillingSessionReattachResult> results = [];

            byte[] request = WritePacket(run, Start);
            Assert.True(run.TryProcessResponse(Start, BuildResponse(request, 0), results));

            // The caller knows the player is logged in by then, and logs them off as it would anyone.
            Assert.False(run.Remove(0));
        }

        [Fact]
        public void Remove_Unknown()
        {
            BillingSessionReattachRun run = CreateRun(players: 2);

            Assert.False(run.Remove(55));
            Assert.Equal(2, run.QueuedCount);
        }

        #endregion
    }
}
