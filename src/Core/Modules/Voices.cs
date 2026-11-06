using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that relays voice messages (wave messages) between players.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A client uploads a voice message with C2S <see cref="C2SPacketType.RelayVoice"/> (0x0E):
    /// <c>[0x0E][u8 voice index][i16 target player id][voice data]</c>, where the voice data is a zlib-compressed wave file.
    /// A large one arrives as sized data. Each player has Voices:NumSamples voice slots, indexed by the voice index.
    /// A packet with voice data stores it in that slot; one without voice data reuses what the slot holds.
    /// </para>
    /// <para>
    /// A target id of 0 or more sends the slot's voice to that player as S2C <see cref="S2CPacketType.Voice"/> (0x0C):
    /// <c>[0x0C][i16 sender player id][voice data]</c>, unchanged, as sized data. A negative target id only stores it.
    /// The voice is sent only if the target is playing in the same arena, is not the sender, accepts audio messages
    /// (<see cref="S2C_PlayerData.AcceptAudio"/>), the arena allows them (Message:AllowAudioMessages), and no other voice is still being sent to the target.
    /// </para>
    /// <para>
    /// To limit memory use, a player's slots together hold at most Voices:PerPlayerLimit bytes, and all players' slots at most Voices:MaxVoiceMemory bytes.
    /// A voice's buffer is rented from <see cref="ArrayPool{T}.Shared"/> and is shared, by reference count, between its slot and any transfers still sending it,
    /// so storing and relaying voices does not allocate.
    /// </para>
    /// <para>
    /// The module does nothing unless Voices:Enabled is set in global.conf (off by default). While it is off, it allocates nothing:
    /// no pool, no per-player data and no packet handlers.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class Voices(
        IComponentBroker broker,
        IConfigManager configManager,
        ILogManager logManager,
        INetwork network,
        IPlayerData playerData) : IModule
    {
        /// <summary>
        /// C2S 0x0E header: type, voice index, target player id.
        /// </summary>
        private const int C2SHeaderLength = 4;

        /// <summary>
        /// S2C 0x0C header: type, sender player id.
        /// </summary>
        private const int S2CHeaderLength = 3;

        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        /// <summary>
        /// Pool of <see cref="VoiceSample"/> objects. Created only when the module is enabled.
        /// </summary>
        private DefaultObjectPool<VoiceSample>? _voiceSamplePool;

        private PlayerDataKey<PlayerData> _pdKey;

        /// <summary>
        /// The # of voice slots each player has (Voices:NumSamples).
        /// </summary>
        private int _numSamples;

        /// <summary>
        /// The most bytes of voice data a player's slots can hold together (Voices:PerPlayerLimit).
        /// </summary>
        private int _perPlayerLimit;

        /// <summary>
        /// The most bytes of voice data all players' slots can hold together (Voices:MaxVoiceMemory).
        /// </summary>
        private int _maxVoiceMemory;

        /// <summary>
        /// Cached delegate for <see cref="GetVoiceData"/>, so that each relay does not allocate one.
        /// </summary>
        private GetSizedSendDataDelegate<VoiceSendContext>? _getVoiceData;

        /// <summary>
        /// Whether Voices:Enabled was set when the module loaded.
        /// </summary>
        private bool _isEnabled;

        /// <summary>
        /// The # of bytes all players' slots hold.
        /// </summary>
        private int _allocatedVoiceSum = 0;

        #region Module members

        [ConfigHelp<bool>("Voices", "Enabled", ConfigScope.Global, Default = false,
            Description = "Whether players' voice messages (C2S 0x0E) are relayed to other players (S2C 0x0C). Players can only send them in arenas where Message:AllowAudioMessages is set.")]
        [ConfigHelp<int>("Voices", "NumSamples", ConfigScope.Global, Default = 4, Min = 1, Max = 256,
            Description = "The # of voice slots each player has. Continuum uses 4.")]
        [ConfigHelp<int>("Voices", "PerPlayerLimit", ConfigScope.Global, Default = 32000, Min = 1,
            Description = "The most bytes of voice data a player's voice slots can hold together.")]
        [ConfigHelp<int>("Voices", "MaxVoiceMemory", ConfigScope.Global, Default = 16000000, Min = 1,
            Description = "The most bytes of voice data all players' voice slots can hold together.")]
        bool IModule.Load(IComponentBroker broker)
        {
            _isEnabled = _configManager.GetBool(_configManager.Global, "Voices", "Enabled", ConfigHelp.Constants.Global.Voices.Enabled.Default);
            if (!_isEnabled)
            {
                // Like a server without this module: voice packets are not handled, and nothing is allocated.
                _logManager.LogM(LogLevel.Info, nameof(Voices), "Voice messages are off (Voices:Enabled).");
                return true;
            }

            // A blank setting gives the default.
            _numSamples = Math.Clamp(
                _configManager.GetInt(_configManager.Global, "Voices", "NumSamples", ConfigHelp.Constants.Global.Voices.NumSamples.Default),
                ConfigHelp.Constants.Global.Voices.NumSamples.Min,
                ConfigHelp.Constants.Global.Voices.NumSamples.Max);

            _perPlayerLimit = Math.Max(
                _configManager.GetInt(_configManager.Global, "Voices", "PerPlayerLimit", ConfigHelp.Constants.Global.Voices.PerPlayerLimit.Default),
                ConfigHelp.Constants.Global.Voices.PerPlayerLimit.Min);

            _maxVoiceMemory = Math.Max(
                _configManager.GetInt(_configManager.Global, "Voices", "MaxVoiceMemory", ConfigHelp.Constants.Global.Voices.MaxVoiceMemory.Default),
                ConfigHelp.Constants.Global.Voices.MaxVoiceMemory.Min);

            _voiceSamplePool = new DefaultObjectPool<VoiceSample>(new DefaultPooledObjectPolicy<VoiceSample>(), Constants.TargetPlayerCount);
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            _getVoiceData = GetVoiceData;

            _logManager.LogM(LogLevel.Info, nameof(Voices), $"Voice messages are on (slots:{_numSamples}, per-player limit:{_perPlayerLimit}, memory limit:{_maxVoiceMemory}).");

            PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            _network.AddPacket(C2SPacketType.RelayVoice, Packet_RelayVoice);
            _network.AddSizedPacket(C2SPacketType.RelayVoice, SizedPacket_RelayVoice);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (!_isEnabled)
                return true;

            _network.RemovePacket(C2SPacketType.RelayVoice, Packet_RelayVoice);
            _network.RemoveSizedPacket(C2SPacketType.RelayVoice, SizedPacket_RelayVoice);

            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);

            _playerData.FreePlayerData(ref _pdKey);

            return true;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.Disconnect)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    return;

                lock (pd.Lock)
                {
                    for (int i = 0; i < pd.Samples.Length; i++)
                        FreeSample(pd, i);

                    pd.SizedIndex = -1;
                }
            }
        }

        private void Packet_RelayVoice(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            if (data.Length < C2SHeaderLength)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Voices), player, $"Bad voice packet (length={data.Length}).");
                return;
            }

            if (player.Status != PlayerState.Playing)
                return;

            int index = data[1];
            if (index >= _numSamples)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Voices), player, $"Bad voice index {index}.");
                return;
            }

            short targetId = BinaryPrimitives.ReadInt16LittleEndian(data[2..]);
            ReadOnlySpan<byte> voiceData = data[C2SHeaderLength..];
            VoiceSample? sample;

            lock (pd.Lock)
            {
                pd.EnsureSlots(_numSamples);

                if (index == pd.SizedIndex)
                {
                    // The client is asking to send a voice that it hasn't finished uploading.
                    return;
                }

                if (!voiceData.IsEmpty)
                {
                    FreeSample(pd, index);

                    if (!TryAllocateSample(player, pd, index, S2CHeaderLength + voiceData.Length))
                        return;

                    voiceData.CopyTo(pd.Samples[index]!.Data[S2CHeaderLength..]);
                }

                // Take a reference for the relay, so the slot can be replaced or freed meanwhile.
                sample = pd.Samples[index];
                sample?.AddReference();
            }

            if (sample is null)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(Voices), player, $"Voice request for empty slot {index}.");
                return;
            }

            Relay(player, index, sample, targetId);
        }

        private void SizedPacket_RelayVoice(Player player, ReadOnlySpan<byte> data, int offset, int totalLength)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            VoiceSample? completedSample = null;
            int completedIndex = -1;
            short completedTarget = -1;

            lock (pd.Lock)
            {
                if (offset == -1)
                {
                    // Cancelled: drop the partial voice.
                    if (pd.SizedIndex != -1)
                    {
                        FreeSample(pd, pd.SizedIndex);
                        pd.SizedIndex = -1;
                    }

                    return;
                }

                if (data.IsEmpty)
                {
                    // Completed.
                    if (pd.SizedIndex != -1)
                    {
                        completedIndex = pd.SizedIndex;
                        completedSample = pd.Samples[completedIndex];
                        completedSample?.AddReference(); // for the relay
                        completedTarget = pd.SizedTarget;
                        pd.SizedIndex = -1;
                    }
                }
                else if (offset == 0)
                {
                    // Started: the first piece holds the header.
                    if (data.Length < C2SHeaderLength || totalLength <= C2SHeaderLength)
                    {
                        _logManager.LogP(LogLevel.Malicious, nameof(Voices), player, $"Bad sized voice packet (length={data.Length} of {totalLength}).");
                        return;
                    }

                    if (player.Status != PlayerState.Playing)
                        return;

                    int index = data[1];
                    if (index >= _numSamples || pd.SizedIndex != -1)
                    {
                        _logManager.LogP(LogLevel.Malicious, nameof(Voices), player, $"Bad sized voice index {index}.");
                        return;
                    }

                    pd.EnsureSlots(_numSamples);
                    FreeSample(pd, index);

                    if (!TryAllocateSample(player, pd, index, S2CHeaderLength + totalLength - C2SHeaderLength))
                        return;

                    data[C2SHeaderLength..].CopyTo(pd.Samples[index]!.Data[S2CHeaderLength..]);
                    pd.SizedIndex = index;
                    pd.SizedTarget = BinaryPrimitives.ReadInt16LittleEndian(data[2..]);
                }
                else if (pd.SizedIndex != -1)
                {
                    // In progress. The network module ensures that offset + length does not exceed the total length.
                    data.CopyTo(pd.Samples[pd.SizedIndex]!.Data[(S2CHeaderLength + offset - C2SHeaderLength)..]);
                }
            }

            if (completedSample is not null)
            {
                Relay(player, completedIndex, completedSample, completedTarget);
            }
        }

        /// <summary>
        /// Sends a stored voice to its target.
        /// </summary>
        /// <param name="sample">The voice, with a reference taken for this relay. The relay passes the reference on to the transfer, or releases it.</param>
        private void Relay(Player player, int index, VoiceSample sample, short targetId)
        {
            if (!TryStartRelay(player, index, sample, targetId))
            {
                ReleaseSample(sample);
            }
        }

        private bool TryStartRelay(Player player, int index, VoiceSample sample, short targetId)
        {
            if (targetId < 0)
                return false; // only store it

            Player? target = _playerData.PidToPlayer(targetId);
            Arena? arena = player.Arena;

            if (target is null
                || target == player
                || target.Status != PlayerState.Playing
                || arena is null
                || target.Arena != arena
                || target.Packet.AcceptAudio == 0
                || _configManager.GetInt(arena.Cfg!, "Message", "AllowAudioMessages", 0) == 0)
            {
                return false;
            }

            if (!target.TryGetExtraData(_pdKey, out PlayerData? targetData))
                return false;

            lock (targetData.Lock)
            {
                if (targetData.IsReceivingVoice)
                {
                    _logManager.LogP(LogLevel.Drivel, nameof(Voices), player, $"Not sending voice {index} to {target.Name}: another voice is still being sent to them.");
                    return false;
                }

                targetData.IsReceivingVoice = true;
            }

            // The transfer owns the reference from here; GetVoiceData releases it when the transfer ends.
            if (!_network.SendSized(target, sample.Length, _getVoiceData!, new VoiceSendContext(target, sample)))
            {
                lock (targetData.Lock)
                {
                    targetData.IsReceivingVoice = false;
                }

                return false;
            }

            _logManager.LogP(LogLevel.Drivel, nameof(Voices), player, $"Relayed voice {index} to {target.Name} ({sample.Length} bytes).");
            return true;
        }

        private void GetVoiceData(VoiceSendContext context, int offset, Span<byte> dataSpan)
        {
            if (dataSpan.IsEmpty)
            {
                // The transfer ended (completed or cancelled).
                if (context.Target.TryGetExtraData(_pdKey, out PlayerData? targetData))
                {
                    lock (targetData.Lock)
                    {
                        targetData.IsReceivingVoice = false;
                    }
                }

                ReleaseSample(context.Sample);
                return;
            }

            ReadOnlySpan<byte> remaining = context.Sample.Data[offset..];
            if (remaining.Length > dataSpan.Length)
                remaining = remaining[..dataSpan.Length];

            remaining.CopyTo(dataSpan);
        }

        /// <summary>
        /// Allocates a slot, if the limits allow it, and fills in the S2C 0x0C header.
        /// </summary>
        /// <remarks>Call with <see cref="PlayerData.Lock"/> held, after freeing the slot.</remarks>
        private bool TryAllocateSample(Player player, PlayerData pd, int index, int length)
        {
            if (pd.Used + length > _perPlayerLimit)
            {
                _logManager.LogP(LogLevel.Info, nameof(Voices), player, $"Voice {index} not stored ({length} bytes): over the per-player limit of {_perPlayerLimit} bytes.");
                return false;
            }

            if (Interlocked.Add(ref _allocatedVoiceSum, length) > _maxVoiceMemory)
            {
                Interlocked.Add(ref _allocatedVoiceSum, -length);
                _logManager.LogP(LogLevel.Info, nameof(Voices), player, $"Voice {index} not stored ({length} bytes): the server's voice memory limit has been reached.");
                return false;
            }

            VoiceSample sample = _voiceSamplePool!.Get();
            sample.Initialize(length); // the slot's reference

            Span<byte> data = sample.Data;
            data[0] = (byte)S2CPacketType.Voice;
            BinaryPrimitives.WriteInt16LittleEndian(data[1..], (short)player.Id);

            pd.Samples[index] = sample;
            pd.Used += length;
            return true;
        }

        /// <summary>
        /// Empties a slot, releasing the slot's reference to its voice.
        /// </summary>
        /// <remarks>Call with <see cref="PlayerData.Lock"/> held.</remarks>
        private void FreeSample(PlayerData pd, int index)
        {
            VoiceSample? sample = pd.Samples[index];
            if (sample is null)
                return;

            pd.Samples[index] = null;
            pd.Used -= sample.Length;
            Interlocked.Add(ref _allocatedVoiceSum, -sample.Length);

            ReleaseSample(sample);
        }

        /// <summary>
        /// Releases a reference to a voice. The last one returns its buffer and the voice to their pools.
        /// </summary>
        private void ReleaseSample(VoiceSample sample)
        {
            if (sample.RemoveReference())
            {
                _voiceSamplePool!.Return(sample);
            }
        }

        private readonly record struct VoiceSendContext(Player Target, VoiceSample Sample);

        /// <summary>
        /// A stored voice: the complete S2C 0x0C packet, in a buffer rented from <see cref="ArrayPool{T}.Shared"/>.
        /// </summary>
        /// <remarks>
        /// Shared by reference count between the slot that holds it and any transfers still sending it,
        /// so that it can be replaced or freed while it is being sent.
        /// </remarks>
        private sealed class VoiceSample : IResettable
        {
            private byte[]? _buffer;
            private int _referenceCount;

            /// <summary>
            /// The # of bytes in the packet.
            /// </summary>
            public int Length { get; private set; }

            /// <summary>
            /// The packet.
            /// </summary>
            public Span<byte> Data => _buffer.AsSpan(0, Length);

            /// <summary>
            /// Rents the buffer and takes the first reference.
            /// </summary>
            public void Initialize(int length)
            {
                _buffer = ArrayPool<byte>.Shared.Rent(length);
                Length = length;
                _referenceCount = 1;
            }

            public void AddReference() => Interlocked.Increment(ref _referenceCount);

            /// <summary>
            /// Releases a reference.
            /// </summary>
            /// <returns><see langword="true"/> if it was the last one.</returns>
            public bool RemoveReference() => Interlocked.Decrement(ref _referenceCount) == 0;

            bool IResettable.TryReset()
            {
                if (_buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = null;
                }

                Length = 0;
                _referenceCount = 0;
                return true;
            }
        }

        private sealed class PlayerData : IResettable
        {
            public readonly Lock Lock = new();

            /// <summary>
            /// The stored voices, each as the complete S2C 0x0C packet to send.
            /// Each holds one reference to its <see cref="VoiceSample"/>, released on disconnect.
            /// Sized by <see cref="EnsureSlots"/> on the player's first voice packet, then kept when this object is reused.
            /// </summary>
            public VoiceSample?[] Samples = [];

            /// <summary>
            /// Makes sure there are <paramref name="count"/> slots.
            /// </summary>
            /// <remarks>Call with <see cref="Lock"/> held. Allocates only the first time, or if Voices:NumSamples changed.</remarks>
            public void EnsureSlots(int count)
            {
                if (Samples.Length != count)
                {
                    // Only reached before any voice is stored (or after all were freed), since the count is fixed while the module is loaded.
                    Samples = new VoiceSample?[count];
                }
            }

            /// <summary>
            /// The # of bytes <see cref="Samples"/> hold.
            /// </summary>
            public int Used;

            /// <summary>
            /// The slot a sized upload is filling, or -1.
            /// </summary>
            public int SizedIndex = -1;

            /// <summary>
            /// The target of the sized upload.
            /// </summary>
            public short SizedTarget;

            /// <summary>
            /// Whether a voice is being sent to this player.
            /// </summary>
            public bool IsReceivingVoice;

            public bool TryReset()
            {
                lock (Lock)
                {
                    Array.Clear(Samples);
                    Used = 0;
                    SizedIndex = -1;
                    SizedTarget = 0;
                    IsReceivingVoice = false;
                }

                return true;
            }
        }
    }
}
