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
    private AudioEncoder _audioEncoder = new AudioEncoder();
    
    private RTCConfiguration? _rtcConfig;
    private UdpClient? _signalingSocket;
    private const int SIGNALING_PORT = 5000;
    private string? _cachedWireGuardIp;
    public MainWindow()
    {
        InitializeComponent();
        InitializeAudioHardware();
        StartSignalingListener();
        _cachedWireGuardIp = GetWireGuardLocalIp();
        Log($"Detected WireGuard IP: {_cachedWireGuardIp ?? "none found"}");
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
            // Set iceServers to empty for direct IP / WireGuard links (no public STUN needed)
            _rtcConfig = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>()
            };

            int samplingRate = 8000;

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
                if (candidateStr.Contains("typ host"))
                {
                    var wgIp = _cachedWireGuardIp; // resolved once in InitializeAudioHardware or ctor
                    if (wgIp != null)
                    {
                        // replace whatever LAN IP got picked with the real WG IP
                        var match = System.Text.RegularExpressions.Regex.Match(candidateStr, @"\b\d{1,3}(\.\d{1,3}){3}\b");
                        if (match.Success && match.Value != wgIp)
                        {
                            candidateStr = candidateStr.Replace(match.Value, wgIp);
                        }
                    }
                }

                Log($"[ICE Candidate Sent - RAW]: {candidate.candidate}");
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

    private static string? GetWireGuardLocalIp()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.Name.Contains("wg", StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        return null;
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