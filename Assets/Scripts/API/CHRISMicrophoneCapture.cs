// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using CSCore;
using CSCore.CoreAudioAPI;
using CSCore.SoundIn;
using CSCore.Streams;
#endif

namespace TiltBrush
{
    internal sealed class CHRISMicrophoneDevice
    {
        public readonly string Id;
        public readonly string Name;
        public string Label { get; private set; }

        public CHRISMicrophoneDevice(string id, string name)
        {
            Id = id;
            Name = Label = name;
        }

        internal static CHRISMicrophoneDevice[] LabelDevices(IEnumerable<CHRISMicrophoneDevice> devices)
        {
            var ordered = devices.OrderBy(device => device.Name, StringComparer.Ordinal)
                .ThenBy(device => device.Id, StringComparer.Ordinal).ToArray();
            foreach (var group in ordered.GroupBy(device => device.Name))
            {
                int index = 0, count = group.Count();
                foreach (var device in group)
                    device.Label = count == 1 ? device.Name : "(" + ++index + "/" + count + ") " + device.Name;
            }
            return ordered;
        }
    }

    // Worker-owned Windows input capture. No Unity APIs, disk audio or device work on the render thread.
    internal static class CHRISMicrophoneCapture
    {
        internal static CHRISMicrophoneDevice[] Devices()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            using (var enumerator = new MMDeviceEnumerator())
            using (var devices = enumerator.EnumAudioEndpoints(DataFlow.Capture, DeviceState.Active))
            {
                var inputs = new List<CHRISMicrophoneDevice>();
                foreach (var device in devices)
                    using (device) inputs.Add(new CHRISMicrophoneDevice(device.DeviceID, device.FriendlyName));
                return CHRISMicrophoneDevice.LabelDevices(inputs);
            }
#else
            return Array.Empty<CHRISMicrophoneDevice>();
#endif
        }

        internal static void Record(string selectedId, BlockingCollection<short[]> audio,
            Func<bool> finishing, Func<bool> cancelled, Action<string, string> post)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            using (var enumerator = new MMDeviceEnumerator())
            {
                var device = selectedId == null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia) :
                    enumerator.GetDevice(selectedId);
                if (device == null) throw new InvalidOperationException("Selected microphone unavailable.");
                using (device)
                using (var capture = new WasapiCapture(true, AudioClientShareMode.Shared, 30) { Device = device })
                {
                    capture.Initialize();
                    using (var input = new CHRISCapturedAudio(capture.WaveFormat, audio, finishing, cancelled))
                    {
                        Exception failure = null;
                        capture.DataAvailable += (_, args) =>
                        {
                            try
                            {
                                input.AppendCaptured(args.Data, args.Offset, args.ByteCount);
                                if (!finishing()) input.Drain();
                            }
                            catch (Exception exception) { Interlocked.CompareExchange(ref failure, exception, null); }
                        };
                        capture.Stopped += (_, args) =>
                        {
                            if (args.Exception != null) Interlocked.CompareExchange(ref failure, args.Exception, null);
                        };
                        if (cancelled()) return;
                        capture.Start();
                        var elapsed = Stopwatch.StartNew();
                        post("listening", "");
                        while (!finishing() && !cancelled() && Volatile.Read(ref failure) == null && elapsed.Elapsed.TotalSeconds < 60)
                            Thread.Sleep(10);
                        capture.Stop(); // CSCore joins its capture thread here, on this worker only.
                        if (cancelled()) return;
                        if (failure != null) throw new InvalidOperationException("Microphone capture failed.", failure);
                        if (!finishing()) throw new InvalidOperationException("Recording reached 60 seconds.");
                        input.Finish();
                        if (cancelled()) return;
                        post("microphone-stopped", "");
                        audio.CompleteAdding(); // Explicit Finish, after the last pre-finish chunk.
                    }
                }
            }
#else
            throw new PlatformNotSupportedException();
#endif
        }
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    // Freeze intake at Finish, then drain accepted bytes and resampler output after the capture thread joins.
    // This prevents both a lost buffered tail and new speech entering during the driver's Stop latency.
    internal sealed class CHRISCapturedAudio : IDisposable
    {
        readonly WriteableBufferingSource m_Input;
        readonly ISampleSource m_Source;
        readonly CHRISPcmChunks m_Chunks;
        readonly Func<bool> m_Finishing, m_Cancelled;
        readonly float[] m_Samples = new float[2400];

        public CHRISCapturedAudio(WaveFormat format, BlockingCollection<short[]> output,
            Func<bool> finishing, Func<bool> cancelled)
        {
            m_Input = new WriteableBufferingSource(format) { FillWithZeros = false };
            m_Source = m_Input.ToSampleSource().ToMono().ChangeSampleRate(CHRISAudio.SampleRate);
            m_Chunks = new CHRISPcmChunks(output);
            m_Finishing = finishing;
            m_Cancelled = cancelled;
        }

        public void AppendCaptured(byte[] data, int offset, int count)
        {
            if (m_Finishing() || m_Cancelled()) return;
            if (m_Input.Length + count > m_Input.MaxBufferSize)
                throw new InvalidOperationException("Microphone processing fell behind.");
            m_Input.Write(data, offset, count);
        }

        public void Drain()
        {
            int count;
            while (!m_Cancelled() && (count = m_Source.Read(m_Samples, 0, m_Samples.Length)) > 0)
                m_Chunks.Add(m_Samples, count);
        }

        public void Finish()
        {
            Drain();
            if (!m_Cancelled()) m_Chunks.Finish();
        }

        public void Dispose() => m_Source.Dispose();
    }
#endif

    internal sealed class CHRISPcmChunks
    {
        readonly BlockingCollection<short[]> m_Output;
        short[] m_Chunk = new short[2400];
        int m_Count, m_Total;
        public CHRISPcmChunks(BlockingCollection<short[]> output) { m_Output = output; }

        public void Add(float[] samples, int count)
        {
            if (count < 0 || count > samples.Length || m_Total + count > CHRISAudio.SampleRate * 60)
                throw new InvalidOperationException("Recording limit exceeded.");
            m_Total += count;
            for (int i = 0; i < count; i++)
            {
                float value = samples[i];
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("Invalid audio sample.");
                m_Chunk[m_Count++] = (short)Math.Round(Math.Max(-1, Math.Min(1, value)) * 32767);
                if (m_Count == m_Chunk.Length)
                {
                    Send(m_Chunk);
                    m_Chunk = new short[2400];
                    m_Count = 0;
                }
            }
        }

        public void Finish()
        {
            if (m_Count == 0) return;
            var last = new short[m_Count];
            Array.Copy(m_Chunk, last, m_Count);
            Send(last);
            m_Count = 0;
        }

        void Send(short[] chunk)
        {
            if (!m_Output.TryAdd(chunk)) throw new InvalidOperationException("Recognition fell behind.");
        }
    }
}
