using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
    private readonly ConcurrentDictionary<string, RTCPeerConnection> _activePeers = new();
    
    private SDL3AudioSource? _audioSource;
    private SDL3AudioEndPoint? _audioSink;
    private AudioEncoder _audioEncoder = new AudioEncoder();
    
    private RTCConfiguration? _rtcConfig;
    private UdpClient? _signalingSocket;
    private const int SIGNALING_PORT = 5000;

    private DispatcherTimer? _statsTimer;

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

            _rtcConfig = new RTCConfiguration { iceServers = new List<RTCIceServer>() };

            _audioSource = new SDL3AudioSource(null, _audioEncoder, 160);
            _audioSource.OnAudioSourceEncodedSample += BroadcastLocalAudio;
            _audioSource.StartAudio();

            _audioSink = new SDL3AudioEndPoint(null, _audioEncoder);

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

        // Capture candidates generated during ICE gathering
        peerConnection.onicecandidate += (candidate) =>
        {
            if (candidate != null && !string.IsNullOrEmpty(candidate.candidate))
            {
                // IF using 10.0.0.x VPN/WireGuard interface, filter out 192.168.x.x local endpoints:
                string candidateStr = candidate.candidate;

                // Simple check: if local host candidate isn't on the target subnet, rewrite or filter it
                if (candidateStr.Contains("typ host") && !candidateStr.Contains("10.0.0."))
                {
                    // Replaces local LAN IP with your local VPN/WireGuard IP if auto-discovery grabs the wrong interface
                    // Replace '10.0.0.1' with your actual local 10.0.0.x IP address on this host
                    candidateStr = candidateStr.Replace("192.168.1.103", "10.0.0.1");
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
            });

            if (state == RTCPeerConnectionState.connected && _statsTimer == null)
            {
                StartStatsLogging();
            }
        };

        peerConnection.OnAudioFormatsNegotiated += formats =>
        {
            _audioSource?.SetAudioSourceFormat(formats[0]);
            _audioSink?.SetAudioSinkFormat(formats[0]); // this also starts playback internally
        };

        peerConnection.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (media == SDPMediaTypesEnum.audio && _audioSink != null)
            {
                _audioSink.GotAudioRtp(rep, rtpPkt.Header.SyncSource, rtpPkt.Header.SequenceNumber,
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

    private void BroadcastLocalAudio(uint duration, byte[] sample)
    {
        foreach (var peer in _activePeers.Values)
        {
            if (peer.connectionState == RTCPeerConnectionState.connected)
            {
                peer.SendAudio(duration, sample);
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
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

        _activePeers.Clear();
        base.OnClosed(e);
    }
}