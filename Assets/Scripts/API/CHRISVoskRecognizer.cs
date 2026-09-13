// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TiltBrush
{
    // Thin binding to the pinned Vosk C API; all calls and ownership stay on one worker thread.
    internal sealed class CHRISVoskRecognizer : IDisposable
    {
        IntPtr m_Model;
        IntPtr m_Recognizer;
        string m_CompletedText = "";

        public CHRISVoskRecognizer(string modelPath)
        {
            try
            {
                vosk_set_log_level(-1);
                m_Model = vosk_model_new(Encoding.UTF8.GetBytes(modelPath + "\0"));
                if (m_Model == IntPtr.Zero) throw new InvalidOperationException("Speech model could not be loaded.");
                m_Recognizer = vosk_recognizer_new(m_Model, CHRISVoiceInput.SampleRate);
                if (m_Recognizer == IntPtr.Zero) throw new InvalidOperationException("Speech recognizer could not be created.");
            }
            catch { Dispose(); throw; }
        }

        public string Accept(short[] samples)
        {
            int result = vosk_recognizer_accept_waveform_s(m_Recognizer, samples, samples.Length);
            if (result < 0) throw new InvalidOperationException("Speech decoding failed.");
            if (result == 1)
            {
                m_CompletedText = Join(m_CompletedText, Text(vosk_recognizer_result(m_Recognizer), "text"));
                return m_CompletedText;
            }
            return Join(m_CompletedText, Text(vosk_recognizer_partial_result(m_Recognizer), "partial"));
        }

        public string Finish() => Join(m_CompletedText, Text(vosk_recognizer_final_result(m_Recognizer), "text"));

        static string Join(string first, string second) => (first + " " + second).Trim();
        static string Text(IntPtr pointer, string field)
        {
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("Missing recognition result.");
            int length = 0;
            while (length < 32768 && Marshal.ReadByte(pointer, length) != 0) length++;
            if (length == 32768) throw new InvalidOperationException("Recognition result too large.");
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            var value = JObject.Parse(Encoding.UTF8.GetString(bytes))[field];
            if (value?.Type != JTokenType.String) throw new InvalidOperationException("Malformed recognition result.");
            return (string)value;
        }

        public void Dispose()
        {
            if (m_Recognizer != IntPtr.Zero) { vosk_recognizer_free(m_Recognizer); m_Recognizer = IntPtr.Zero; }
            if (m_Model != IntPtr.Zero) { vosk_model_free(m_Model); m_Model = IntPtr.Zero; }
        }

        const string Library = "libvosk";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern void vosk_set_log_level(int level);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vosk_model_new(byte[] path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern void vosk_model_free(IntPtr model);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vosk_recognizer_new(IntPtr model, float rate);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern void vosk_recognizer_free(IntPtr recognizer);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int vosk_recognizer_accept_waveform_s(IntPtr recognizer, [In] short[] samples, int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vosk_recognizer_result(IntPtr recognizer);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vosk_recognizer_partial_result(IntPtr recognizer);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vosk_recognizer_final_result(IntPtr recognizer);
    }
}
