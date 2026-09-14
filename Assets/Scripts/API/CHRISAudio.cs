// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using UnityEngine.Networking;

namespace TiltBrush
{
    // The local voice protocol uses signed PCM16 little-endian, mono, 24 kHz.
    internal static class CHRISAudio
    {
        public const int SampleRate = 24000;
        public const int MaxPlaybackBytes = SampleRate * 2 * 60;

        public static byte[] Encode(short[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                bytes[i * 2] = (byte)samples[i];
                bytes[i * 2 + 1] = (byte)(samples[i] >> 8);
            }
            return bytes;
        }

        public static float[] Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length % 2 != 0 || bytes.Length > MaxPlaybackBytes)
                throw new ArgumentException("Invalid confirmation audio length.");
            var samples = new float[bytes.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)(bytes[i * 2] | bytes[i * 2 + 1] << 8) / 32768f;
            return samples;
        }
    }

    // Reject oversized responses as they arrive, before retaining or decoding them.
    internal sealed class CHRISAudioDownload : DownloadHandlerScript
    {
        readonly MemoryStream m_Bytes = new MemoryStream();
        public bool TooLarge { get; private set; }
        public CHRISAudioDownload() : base(new byte[16384]) { }
        protected override bool ReceiveData(byte[] data, int length)
        {
            if (data == null || length < 0 || m_Bytes.Length + length > CHRISAudio.MaxPlaybackBytes)
            {
                TooLarge = true;
                return false;
            }
            m_Bytes.Write(data, 0, length);
            return true;
        }
        public byte[] TakeBytes() => m_Bytes.ToArray();
        public void Clear() => m_Bytes.Dispose();
    }
}
