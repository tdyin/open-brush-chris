// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiltBrush
{
    // Credentials and provider connections live in Python. Never reconnect or replay audio.
    internal sealed class CHRISCloudTranscription
    {
        readonly string m_SessionId = Guid.NewGuid().ToString("N");
        readonly TaskCompletionSource<bool> m_Ready = new TaskCompletionSource<bool>();
        volatile bool m_FinishSent;

        public async Task Run(BlockingCollection<short[]> audio, CancellationToken cancellation, Action<string, string> post)
        {
            using (var socket = new ClientWebSocket())
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(new Uri("ws://127.0.0.1:8765/voice/transcribe"), connect.Token);
                await RunConnected(socket, audio, cancellation, post);
            }
        }

        internal async Task RunConnected(WebSocket socket, BlockingCollection<short[]> audio, CancellationToken cancellation, Action<string, string> post)
        {
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (stop.Token.Register(socket.Abort))
            {
                Task receive = null, send = null;
                try
                {
                    stop.CancelAfter(TimeSpan.FromSeconds(10));
                    await SendControl(socket, "start", stop.Token);
                    receive = Receive(socket, post, stop.Token);
                    await Task.WhenAny(m_Ready.Task, receive);
                    if (!m_Ready.Task.IsCompleted) { await receive; throw new IOException("Speech startup ended."); }
                    await m_Ready.Task;
                    stop.CancelAfter(Timeout.Infinite);
                    send = Task.Run(() => SendAudioUntilFinish(socket, audio, stop), stop.Token);
                    await Task.WhenAny(send, receive);
                    if (receive.IsCompleted) await receive;
                    await send;
                    await receive;
                }
                finally
                {
                    stop.Cancel();
                    // Observe both tasks and release their resources even after cancellation.
                    if (send != null) try { await send; } catch (Exception) { }
                    if (receive != null) try { await receive; } catch (Exception) { }
                }
            }
        }

        async Task SendAudioUntilFinish(WebSocket socket, BlockingCollection<short[]> audio, CancellationTokenSource stop)
        {
            foreach (var chunk in audio.GetConsumingEnumerable(stop.Token))
            {
                byte[] bytes = CHRISAudio.Encode(chunk);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, stop.Token);
            }
            m_FinishSent = true;
            stop.CancelAfter(TimeSpan.FromSeconds(20));
            await SendControl(socket, "finish", stop.Token);
        }

        Task SendControl(WebSocket socket, string type, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(new JObject { ["type"] = type, ["session_id"] = m_SessionId }.ToString(Formatting.None));
            return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        async Task Receive(WebSocket socket, Action<string, string> post, CancellationToken token)
        {
            bool ready = false;
            var buffer = new byte[4096];
            while (true)
            {
                using (var message = new MemoryStream())
                {
                    WebSocketReceiveResult frame;
                    do
                    {
                        frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > 16384)
                            throw new IOException("Invalid speech response.");
                        message.Write(buffer, 0, frame.Count);
                    } while (!frame.EndOfMessage);
                    var value = JObject.Parse(Encoding.UTF8.GetString(message.ToArray()));
                    if ((string)value["session_id"] != m_SessionId) throw new IOException("Speech session mismatch.");
                    string type = (string)value["type"];
                    if (type == "ready" && !ready)
                    {
                        ready = true;
                        post("ready", "");
                        m_Ready.TrySetResult(true);
                    }
                    else if (ready && (type == "partial" || type == "final"))
                    {
                        if (value["text"]?.Type != JTokenType.String || !CHRISVoiceSession.ValidText((string)value["text"], type == "partial"))
                            throw new IOException("Invalid transcript.");
                        if (type == "final" && !m_FinishSent) throw new IOException("Unexpected final transcript.");
                        post(type, (string)value["text"]);
                        if (type == "final") return;
                    }
                    else throw new IOException("Cloud speech unavailable. Check the connection and re-record.");
                }
            }
        }
    }
}
