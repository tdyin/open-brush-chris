// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
namespace TiltBrush
{
    // Identifies one immutable review and rejects late audio after any invalidation.
    internal sealed class CHRISReadoutSession
    {
        public bool Enabled { get; private set; } = true;
        public long Generation { get; private set; }
        public string ReviewKey { get; private set; }
        string m_AttemptedKey;

        public void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            Enabled = enabled;
            Cancel();
        }

        public bool Observe(string key)
        {
            if (ReviewKey == key) return false;
            Cancel();
            ReviewKey = key;
            return true;
        }

        public bool TryBegin()
        {
            if (!Enabled || ReviewKey == null || ReviewKey == m_AttemptedKey) return false;
            m_AttemptedKey = ReviewKey;
            Generation++;
            return true;
        }

        public bool Accepts(long generation, string key) =>
            Enabled && generation == Generation && key != null && key == ReviewKey;

        public void Cancel() { Generation++; ReviewKey = null; }
    }
}
