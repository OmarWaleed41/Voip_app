using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.SDL3;

namespace Voip;

public partial class MainWindow : Window
{
    // --- Tunables -----------------------------------------------------
    // Only advertise/accept host candidates on this subnet (our WireGuard mesh).
    // With X_ICEIncludeAllInterfaceAddresses = true below, SIPSorcery gathers
    // candidates from every local interface, so this filter is what actually
    // decides "use the WireGuard path" instead of guessing/rewriting one.
    private const string WIREGUARD_SUBNET_PREFIX = "10.0.0.";

    // Frame duration used by the audio source (160 samples @ 8kHz = 20ms).
    // Keep this in sync with the `160` passed to SDL3AudioSource below.
    private const int AUDIO_FRAME_MS = 20;

    // How many frames to buffer per peer before we start playing them out.
    // Bigger = smoother through jitter, smaller = lower latency. 3 frames
    // (~60ms) is a reasonable starting point for a LAN/VPN mesh; raise it
    // if you still hear crackle, lower it if the delay feels too long.
    private const int JITTER_TARGET_FRAMES = 3;

    // Max frames to hold per peer's outgoing send queue before we start
    // dropping instead of piling up latency behind a slow/stalled link.
    private const int SEND_QUEUE_CAPACITY = 50;
    // --------------------------------------------------------------------

    private readonly ConcurrentDictionary<string, RTCPeerConnection> _activePeers = new();
    private readonly ConcurrentDictionary<string, JitterBuffer> _jitterBuffers = new();
    private readonly ConcurrentDictionary<string, BlockingCollection<(uint duration, byte[] sample)>> _sendQueues = new();
    private readonly ConcurrentDictionary<string, Task> _sendWorkers = new();

    private SDL3AudioSource? _audioSource;
    private SDL3AudioEndPoint? _audioSink;
    private AudioEncoder _audioEncoder = new AudioEncoder();

    private RTCConfiguration? _rtcConfig;
    private UdpClient? _signalingSocket;
    private const int SIGNALING_PORT = 5000;

    private DispatcherTimer? _statsTimer;
    private Timer? _playoutTimer;

    public MainWindow()
    {
        InitializeComponent();
        InitializeAudioHardware();
        StartSignalingListener();
    }

    private void Log(string message)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var logBlock = this.FindControl<SelectableTextBlock>("LogTextBlock");
            var scrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");

            if (logBlock != null)
            {
                logBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            }
            scrollViewer?.ScrollToEnd();
        });
    }

    private void StartStatsLogging()
    {
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (s, e) =>
        {
            if (_audioSource != null)
            {
                var s1 = _audioSource.GetStats();
                Log($"[SRC] active={s1.IsActive} overrun={s1.OverrunCount} dropped={s1.DroppedFrames}");
            }
            if (_audioSink != null)
            {
                var s2 = _audioSink.GetStats();
                Log($"[SINK] active={s2.IsActive} underrun={s2.UnderrunCount} dropped={s2.DroppedFrames} queueDepth={s2.QueueDepth}");
            }
        };
        _statsTimer.Start();
    }

    private void InitializeAudioHardware()
    {
        try
        {
            SDL3Helper.InitSDL(); // must happen before any SDL3AudioSource/SDL3AudioEndPoint use

            _rtcConfig = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>(),

                // Default ICE gathering only looks at the interface the OS routing
                // table would pick for the destination (or the internet-facing
                // interface if the destination isn't known yet, which is exactly
                // what happens when we createOffer() before any signaling has
                // occurred). That's how host candidates ended up advertising the
                // LAN IP instead of the WireGuard IP. Gathering from every local
                // interface and then filtering (see AddPeerToMesh) is the reliable
                // fix instead of string-rewriting one hardcoded LAN address.
                X_ICEIncludeAllInterfaceAddresses = true
            };

            _audioSource = new SDL3AudioSource(null, _audioEncoder, 160); // 160 samples = 20ms @ 8kHz
            _audioSource.OnAudioSourceEncodedSample += BroadcastLocalAudio;
            _audioSource.StartAudio();

            _audioSink = new SDL3AudioEndPoint(null, _audioEncoder);

            // Pulls buffered frames out of each peer's jitter buffer at a steady
            // cadence, instead of pushing straight to the sink the instant a
            // packet arrives off the network.
            _playoutTimer = new Timer(_ => PumpJitterBuffers(), null, 0, AUDIO_FRAME_MS);

            Log("SDL3 Audio Source/Sink initialized successfully.");
        }
        catch (Exception ex)
        {
            Log($"Audio Initialization Error: {ex.Message}");
        }
    }

    private void StartSignalingListener()
    {
        try
        {
            // Allow socket address reuse for local testing
            _signalingSocket = new UdpClient();
            _signalingSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _signalingSocket.Client.Bind(new IPEndPoint(IPAddress.Any, SIGNALING_PORT));

            Task.Run(async () =>
            {
                while (_signalingSocket != null)
                {
                    var result = await _signalingSocket.ReceiveAsync();
                    string rawMessage = Encoding.UTF8.GetString(result.Buffer);
                    string senderIp = result.RemoteEndPoint.Address.ToString();

                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await HandleIncomingSignaling(
                            peerId: senderIp,
                            message: rawMessage,
                            sendSignalingMessage: (replyPayload) => SendRawNetworkMessage(senderIp, replyPayload)
                        );
                    });
                }
            });

            Log($"Listening for signaling on UDP port {SIGNALING_PORT}...");
        }
        catch (Exception ex)
        {
            Log($"Failed to bind signaling socket: {ex.Message}");
        }
    }

    private void SendRawNetworkMessage(string targetIp, string payload)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            _signalingSocket?.Send(bytes, bytes.Length, targetIp, SIGNALING_PORT);
        }
        catch (Exception ex)
        {
            Log($"Network Send Error ({targetIp}): {ex.Message}");
        }
    }

    private async void CallPeerButton_Click(object? sender, RoutedEventArgs e)
    {
        var peerIpBox = this.FindControl<TextBox>("PeerIpTextBox");
        string targetIp = peerIpBox?.Text?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(targetIp))
        {
            Log($"Initiating P2P call to {targetIp}...");
            await StartCallWithPeer(targetIp);
        }
        else
        {
            Log("Please enter a valid IP address.");
        }
    }

    public async Task StartCallWithPeer(string targetIp)
    {
        await AddPeerToMesh(
            peerId: targetIp,
            isInitiator: true,
            sendSignalingMessage: (payload) => SendRawNetworkMessage(targetIp, payload)
        );
    }

    public async Task<RTCPeerConnection> AddPeerToMesh(string peerId, bool isInitiator, Action<string> sendSignalingMessage)
    {
        var peerConnection = new RTCPeerConnection(_rtcConfig);

        if (_audioSource != null)
        {
            MediaStreamTrack audioTrack = new MediaStreamTrack(_audioSource.GetAudioSourceFormats());
            peerConnection.addTrack(audioTrack);
        }

        // Dedicated jitter buffer for this peer's incoming audio, and a
        // dedicated outbound queue/worker so a slow path to this peer can't
        // stall audio capture or delivery to everyone else in the mesh.
        _jitterBuffers[peerId] = new JitterBuffer(JITTER_TARGET_FRAMES);
        StartSendWorker(peerId, peerConnection);

        // Capture candidates generated during ICE gathering
        peerConnection.onicecandidate += (candidate) =>
        {
            if (candidate != null && !string.IsNullOrEmpty(candidate.candidate))
            {
                string candidateStr = candidate.candidate;

                // With X_ICEIncludeAllInterfaceAddresses=true we now gather host
                // candidates from every local interface, including the WireGuard
                // one. Only advertise the WireGuard-subnet host candidate to
                // peers on the mesh; drop other-subnet host candidates instead
                // of rewriting them to a hardcoded address that may not match
                // this machine.
                if (candidateStr.Contains("typ host") && !candidateStr.Contains(WIREGUARD_SUBNET_PREFIX))
                {
                    Log($"[ICE Candidate Skipped - not on {WIREGUARD_SUBNET_PREFIX}x]: {candidateStr}");
                    return;
                }

                Log($"[ICE Candidate Sent]: {candidateStr}");
                sendSignalingMessage($"CANDIDATE:{candidateStr}");
            }
        };

        peerConnection.onconnectionstatechange += (state) =>
        {
            Log($"[PEER STATE] {peerId}: {state}");
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var statusBlock = this.FindControl<TextBlock>("StatusTextBlock");
                if (statusBlock != null)
                {
                    statusBlock.Text = $"Status: {peerId} is {state}";
                }

                if (state == RTCPeerConnectionState.connected && _statsTimer == null)
                {
                    StartStatsLogging();
                }
            });

            if (state == RTCPeerConnectionState.closed ||
                state == RTCPeerConnectionState.failed ||
                state == RTCPeerConnectionState.disconnected)
            {
                RemovePeer(peerId);
            }
        };

        peerConnection.OnAudioFormatsNegotiated += formats =>
        {
            _audioSource?.SetAudioSourceFormat(formats[0]);
            _audioSink?.SetAudioSinkFormat(formats[0]); // this also starts playback internally
        };

        peerConnection.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (media == SDPMediaTypesEnum.audio && _jitterBuffers.TryGetValue(peerId, out var jitterBuffer))
            {
                // Enqueue only - PumpJitterBuffers() on the playout timer is
                // what actually forwards frames to the sink at a steady pace.
                jitterBuffer.Add(rep, rtpPkt.Header.SyncSource, rtpPkt.Header.SequenceNumber,
                    rtpPkt.Header.Timestamp, rtpPkt.Header.PayloadType, rtpPkt.Header.MarkerBit == 1, rtpPkt.Payload);
            }
        };

        _activePeers[peerId] = peerConnection;

        if (isInitiator)
        {
            var offer = peerConnection.createOffer();
            await peerConnection.setLocalDescription(offer);

            // Give SIPSorcery a brief window to gather local candidates
            await Task.Delay(500);

            sendSignalingMessage($"OFFER:{peerConnection.localDescription.sdp}");
            Log($"SDP Offer sent to {peerId}.");
        }

        return peerConnection;
    }

    public async Task HandleIncomingSignaling(string peerId, string message, Action<string> sendSignalingMessage)
    {
        if (message.StartsWith("OFFER:"))
        {
            Log($"Received SDP OFFER from {peerId}");
            string sdp = message.Substring(6);

            var pc = await AddPeerToMesh(peerId, isInitiator: false, sendSignalingMessage);
            pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });

            var answer = pc.createAnswer();
            await pc.setLocalDescription(answer);

            await Task.Delay(500);

            sendSignalingMessage($"ANSWER:{pc.localDescription.sdp}");
            Log($"SDP ANSWER sent to {peerId}.");
        }
        else if (message.StartsWith("ANSWER:"))
        {
            Log($"Received SDP ANSWER from {peerId}");
            string sdp = message.Substring(7);
            if (_activePeers.TryGetValue(peerId, out var pc))
            {
                pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
            }
        }
        else if (message.StartsWith("CANDIDATE:"))
        {
            string candidateInit = message.Substring(10);
            Log($"Received remote CANDIDATE from {peerId}");
            if (_activePeers.TryGetValue(peerId, out var pc))
            {
                pc.addIceCandidate(new RTCIceCandidateInit { candidate = candidateInit });
            }
        }
    }

    /// <summary>
    /// Runs on the audio capture thread. Never blocks on network I/O -
    /// frames are handed off to each peer's own bounded queue and a
    /// per-peer worker task does the actual SendAudio() call, so one slow
    /// or congested peer can't delay capture or delivery to the others.
    /// </summary>
    private void BroadcastLocalAudio(uint duration, byte[] sample)
    {
        foreach (var kvp in _activePeers)
        {
            if (kvp.Value.connectionState == RTCPeerConnectionState.connected &&
                _sendQueues.TryGetValue(kvp.Key, out var queue))
            {
                // If the queue is full the peer's link can't keep up - drop this
                // frame for that peer rather than blocking capture or growing
                // latency further. Better to skip a frame than to fall behind.
                queue.TryAdd((duration, sample));
            }
        }
    }

    private void StartSendWorker(string peerId, RTCPeerConnection peerConnection)
    {
        var queue = new BlockingCollection<(uint duration, byte[] sample)>(SEND_QUEUE_CAPACITY);
        _sendQueues[peerId] = queue;

        var worker = Task.Run(() =>
        {
            try
            {
                foreach (var (duration, sample) in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        peerConnection.SendAudio(duration, sample);
                    }
                    catch (Exception ex)
                    {
                        Log($"Send Audio Error ({peerId}): {ex.Message}");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Queue was disposed during shutdown/peer removal - expected.
            }
        });

        _sendWorkers[peerId] = worker;
    }

    private void RemovePeer(string peerId)
    {
        _activePeers.TryRemove(peerId, out _);
        _jitterBuffers.TryRemove(peerId, out _);

        if (_sendQueues.TryRemove(peerId, out var queue))
        {
            queue.CompleteAdding();
        }
        _sendWorkers.TryRemove(peerId, out _);
    }

    /// <summary>
    /// Fires every AUDIO_FRAME_MS on a background timer. For each connected
    /// peer, pulls the next ready frame out of that peer's jitter buffer (if
    /// any is available) and forwards it to the shared audio sink. This is
    /// what actually smooths out network jitter - packets can arrive early,
    /// late, or slightly out of order, but they get played out on a steady
    /// clock instead of the instant they land.
    /// </summary>
    private void PumpJitterBuffers()
    {
        if (_audioSink == null) return;

        foreach (var kvp in _jitterBuffers)
        {
            if (kvp.Value.TryDequeueNext(out var rep, out var ssrc, out var seq, out var ts, out var pt, out var marker, out var payload))
            {
                try
                {
                    _audioSink.GotAudioRtp(rep, ssrc, seq, ts, pt, marker, payload);
                }
                catch (Exception ex)
                {
                    Log($"Audio Playout Error ({kvp.Key}): {ex.Message}");
                }
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _playoutTimer?.Dispose();
        _signalingSocket?.Dispose();

        if (_audioSource != null)
        {
            _audioSource.OnAudioSourceEncodedSample -= BroadcastLocalAudio;
            _audioSource.CloseAudio();
        }

        foreach (var peer in _activePeers.Values)
        {
            peer.Close("Application shutdown");
        }

        foreach (var queue in _sendQueues.Values)
        {
            queue.CompleteAdding();
        }

        _activePeers.Clear();
        _jitterBuffers.Clear();
        _sendQueues.Clear();
        _sendWorkers.Clear();

        base.OnClosed(e);
    }

    /// <summary>
    /// Per-peer audio jitter buffer. Holds incoming RTP audio frames keyed by
    /// sequence number so mild reordering resolves itself, and only starts
    /// releasing frames once a small target depth has been reached, so
    /// variation in packet arrival time gets absorbed here instead of showing
    /// up as audible jitter at playback.
    /// </summary>
    private sealed class JitterBuffer
    {
        private sealed class BufferedFrame
        {
            public IPEndPoint? RemoteEndPoint;
            public uint Ssrc;
            public ushort SeqNum;
            public uint Timestamp;
            public int PayloadType;
            public bool Marker;
            public byte[] Payload = Array.Empty<byte>();
        }

        private readonly object _lock = new();
        private readonly SortedDictionary<ushort, BufferedFrame> _frames = new();
        private readonly int _targetDepth;
        private bool _isPrimed;

        public JitterBuffer(int targetDepthFrames)
        {
            _targetDepth = Math.Max(1, targetDepthFrames);
        }

        public void Add(IPEndPoint? rep, uint ssrc, ushort seq, uint ts, int payloadType, bool marker, byte[] payload)
        {
            lock (_lock)
            {
                _frames[seq] = new BufferedFrame
                {
                    RemoteEndPoint = rep,
                    Ssrc = ssrc,
                    SeqNum = seq,
                    Timestamp = ts,
                    PayloadType = payloadType,
                    Marker = marker,
                    Payload = payload
                };

                // Safety valve: if a peer's link hiccups badly and frames pile
                // up faster than we drain them, don't grow unbounded latency.
                while (_frames.Count > _targetDepth * 4)
                {
                    RemoveLowestKey();
                }
            }
        }

        public bool TryDequeueNext(out IPEndPoint? rep, out uint ssrc, out ushort seq, out uint ts, out int payloadType, out bool marker, out byte[] payload)
        {
            lock (_lock)
            {
                rep = null; ssrc = 0; seq = 0; ts = 0; payloadType = 0; marker = false; payload = Array.Empty<byte>();

                if (!_isPrimed)
                {
                    if (_frames.Count < _targetDepth)
                    {
                        return false;
                    }
                    _isPrimed = true;
                }

                if (_frames.Count == 0)
                {
                    // Buffer ran dry (network stall) - drop back into priming
                    // mode so we build up a small cushion again rather than
                    // playing frames out one-at-a-time as they trickle in.
                    _isPrimed = false;
                    return false;
                }

                using var enumerator = _frames.GetEnumerator();
                enumerator.MoveNext();
                var frame = enumerator.Current.Value;
                _frames.Remove(enumerator.Current.Key);

                rep = frame.RemoteEndPoint;
                ssrc = frame.Ssrc;
                seq = frame.SeqNum;
                ts = frame.Timestamp;
                payloadType = frame.PayloadType;
                marker = frame.Marker;
                payload = frame.Payload;
                return true;
            }
        }

        private void RemoveLowestKey()
        {
            using var enumerator = _frames.GetEnumerator();
            if (enumerator.MoveNext())
            {
                _frames.Remove(enumerator.Current.Key);
            }
        }
    }
}