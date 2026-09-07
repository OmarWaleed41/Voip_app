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
    
    // Valid SIPSorcery Hardware & Media Interfaces
    private SDL3AudioSource? _audioSource;
    private AudioEncoder _audioEncoder = new AudioEncoder();
    
    private RTCConfiguration? _rtcConfig;
    private UdpClient? _signalingSocket;
    private const int SIGNALING_PORT = 5000;

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

    private void InitializeAudioHardware()
    {
        try
        {
            _rtcConfig = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            };

            int samplingRate = 8000;

            // SDL3AudioSource acts as the hardware audio capture engine
            _audioSource = new SDL3AudioSource(null, _audioEncoder, samplingRate);
            _audioSource.OnAudioSourceEncodedSample += BroadcastLocalAudio;
            _audioSource.StartAudio();

            Log("SDL3 Audio Source initialized successfully.");
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
            _signalingSocket = new UdpClient(SIGNALING_PORT);

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

        peerConnection.onicecandidate += (candidate) =>
        {
            if (candidate != null)
            {
                sendSignalingMessage($"CANDIDATE:{candidate.candidate}");
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
        };

        // Automatic internal RTP packet handling
        peerConnection.OnRtpPacketReceived += (IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                // Raw payload received from peer connection
                byte[] payload = rtpPacket.Payload;
            }
        };

        _activePeers[peerId] = peerConnection;

        if (isInitiator)
        {
            var offer = peerConnection.createOffer();
            await peerConnection.setLocalDescription(offer);
            
            sendSignalingMessage($"OFFER:{offer.sdp}");
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

            sendSignalingMessage($"ANSWER:{answer.sdp}");
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