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
    private RTCConfiguration? _rtcConfig;
    
    private UdpClient? _signalingSocket;
    private const int SIGNALING_PORT = 5000;

    public MainWindow()
    {
        InitializeComponent();
        InitializeAudioHardware();
        StartSignalingListener();
    }

    private void InitializeAudioHardware()
    {
        try
        {
            _rtcConfig = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>() // Peer-to-peer mesh configuration
            };

            IAudioEncoder audioEncoder = new AudioEncoder();
            string? audioInDeviceName = null;
            int samplingRate = 8000;

            _audioSource = new SDL3AudioSource(audioInDeviceName, audioEncoder, samplingRate);
            _audioSource.OnAudioSourceEncodedSample += BroadcastLocalAudio;

            _audioSource.StartAudio();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Audio Hardware Error: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------
    // 1. AVALONIA THREADING & SIGNALING SOCKET
    // -------------------------------------------------------------------
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

                    // Swap System.Windows.Threading with DispatcherUIThread.InvokeAsync
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to bind UDP signaling port {SIGNALING_PORT}: {ex.Message}");
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
            Debug.WriteLine($"Failed to send network payload to {targetIp}: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------
    // 2. UI EVENT HANDLERS
    // -------------------------------------------------------------------
    private async void CallPeerButton_Click(object? sender, RoutedEventArgs e)
    {
        // Fetch the control instance dynamically if x:Name field auto-generation fails
        var peerIpTextBox = this.FindControl<TextBox>("PeerIpTextBox");
        
        string targetIp = peerIpTextBox?.Text?.Trim() ?? string.Empty;
        
        if (!string.IsNullOrEmpty(targetIp))
        {
            await StartCallWithPeer(targetIp);
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

    // -------------------------------------------------------------------
    // 3. WEBRTC P2P SESSION MANAGEMENT
    // -------------------------------------------------------------------
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

        peerConnection.OnRtpPacketReceived += (IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                // Process incoming remote RTP audio payload
            }
        };

        _activePeers[peerId] = peerConnection;

        if (isInitiator)
        {
            var offer = peerConnection.createOffer();
            await peerConnection.setLocalDescription(offer);
            
            sendSignalingMessage($"OFFER:{offer.sdp}");
        }

        return peerConnection;
    }

    public async Task HandleIncomingSignaling(string peerId, string message, Action<string> sendSignalingMessage)
    {
        if (message.StartsWith("OFFER:"))
        {
            string sdp = message.Substring(6);
            
            var pc = await AddPeerToMesh(peerId, isInitiator: false, sendSignalingMessage);
            pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });

            var answer = pc.createAnswer();
            await pc.setLocalDescription(answer);

            sendSignalingMessage($"ANSWER:{answer.sdp}");
        }
        else if (message.StartsWith("ANSWER:"))
        {
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

    // -------------------------------------------------------------------
    // 4. AUDIO STREAMING & CLEANUP
    // -------------------------------------------------------------------
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