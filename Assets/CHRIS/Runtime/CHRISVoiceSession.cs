// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;

namespace TiltBrush
{
    // Main-thread state for one explicitly started recording. Provider callbacks carry its ID.
    public sealed class CHRISVoiceSession
    {
        public enum Phase { Idle, Loading, Recording, Finalizing, Failed }
        public long Id { get; private set; }
        public Phase State { get; private set; }
        public string Transcript { get; private set; } = "";
        public string Error { get; private set; }
        public bool IsActive => State == Phase.Loading || State == Phase.Recording || State == Phase.Finalizing;

        public long Begin()
        {
            Id++;
            State = Phase.Loading;
            Transcript = "";
            Error = null;
            return Id;
        }

        public bool Ready(long id)
        {
            if (id != Id || State != Phase.Loading) return false;
            State = Phase.Recording;
            return true;
        }

        public bool Partial(long id, string text)
        {
            if (id != Id || (State != Phase.Recording && State != Phase.Finalizing)) return false;
            if (!ValidText(text, allowEmpty: true)) return Fail(id, "Transcript is too long or invalid. Please re-record.");
            Transcript = text;
            return true;
        }

        public bool RequestFinish()
        {
            if (State != Phase.Recording) return false;
            State = Phase.Finalizing;
            return true;
        }

        public bool Complete(long id, string text)
        {
            if (id != Id || State != Phase.Finalizing) return false;
            text = text?.Trim();
            if (!ValidText(text, allowEmpty: false))
            {
                Fail(id, "No valid speech recognized. Please re-record your request.");
                return false;
            }
            Transcript = text;
            State = Phase.Idle;
            return true;
        }

        public bool Fail(long id, string error)
        {
            if (id != Id || !IsActive) return false;
            State = Phase.Failed;
            Error = error;
            return true;
        }

        public void Cancel()
        {
            Id++;
            State = Phase.Idle;
            Transcript = "";
            Error = null;
        }

        public static bool ValidText(string text, bool allowEmpty)
        {
            if (text == null || text.Length > CHRISAssistanceClient.MaxPromptLength ||
                (!allowEmpty && string.IsNullOrWhiteSpace(text))) return false;
            foreach (char value in text)
                if (char.IsControl(value) && value != '\n' && value != '\t' && value != '\r') return false;
            return true;
        }
    }
}
