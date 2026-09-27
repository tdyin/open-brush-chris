// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;

namespace TiltBrush
{
    // Measurement-only probe, off by default and self-contained (the step 3 baseline build carries this
    // same file). Started through the loopback API:
    //   chris.perf.frametime=<seconds>[,label]   app frame interval for N seconds, then logged
    //   chris.perf.latency=1 / =0               start / stop-and-log the device-update timing estimate
    // Raw samples are written to <persistentDataPath>/CHRIS/perf/*.csv so percentiles can be recomputed.
    // Frame time is the unscaled frame interval of the app's own loop (paced by the XR runtime). It does
    // NOT observe compositor or display misses; intervals over 1.5x the refresh period are app-side only.
    // The latency figure is a DEVICE-UPDATE TIMING ESTIMATE: the device's Input System lastUpdateTime
    // (the timestamp of the device's most recent state event, not of the mapped control) to the point
    // CHRIS applies the mapped action. On Windows, keyboard and mouse events are timestamped when Unity
    // processes Windows input, not when the key physically moved, so the wait from the physical input
    // to that processing is invisible here. It excludes stroke creation, pose rendering, compositing and
    // display. Raw CSV files are written synchronously, so stopping latency capture is refused while a
    // frame-time window is running (the export could otherwise disturb that measurement).
    public static class CHRISPerfProbe
    {
        // Robust summary used by both recorders; values in milliseconds.
        public struct Summary
        {
            public int Count;
            public double Median, P95, Max;
            public override string ToString() => Count == 0 ? "n=0" :
                string.Format(CultureInfo.InvariantCulture, "n={0} median={1:F2} ms p95={2:F2} ms max={3:F2} ms", Count, Median, P95, Max);
        }

        public static Summary Summarize(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return new Summary();
            double median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
            return new Summary
            {
                Count = sorted.Count,
                Median = median,
                P95 = sorted[System.Math.Min(sorted.Count - 1, (int)System.Math.Ceiling(0.95 * sorted.Count) - 1)],
                Max = sorted[sorted.Count - 1],
            };
        }

        // Samples only frames that bring a new input event per action, so a held key counts once
        // (its press), not once per held frame.
        public sealed class LatencySampler
        {
            readonly Dictionary<string, double> m_LastEvent = new Dictionary<string, double>();
            readonly Dictionary<string, List<double>> m_Samples = new Dictionary<string, List<double>>();

            public bool Offer(string action, double eventTime, double now)
            {
                if (eventTime <= 0 || now < eventTime) return false;
                if (m_LastEvent.TryGetValue(action, out var last) && last == eventTime) return false;
                m_LastEvent[action] = eventTime;
                if (!m_Samples.TryGetValue(action, out var list)) m_Samples[action] = list = new List<double>();
                list.Add((now - eventTime) * 1000.0);
                return true;
            }

            public IEnumerable<string> RawCsv() => new[] { "action,latency_ms" }.Concat(
                m_Samples.SelectMany(kv => kv.Value.Select(v => kv.Key + "," + v.ToString("R", CultureInfo.InvariantCulture))));

            public IEnumerable<KeyValuePair<string, Summary>> Summaries() =>
                m_Samples.OrderBy(kv => kv.Key).Select(kv => new KeyValuePair<string, Summary>(kv.Key, Summarize(kv.Value)));

            public void Clear() { m_LastEvent.Clear(); m_Samples.Clear(); }
        }

        static readonly List<double> s_Frames = new List<double>();
        static float s_FrameEnd;
        static string s_FrameLabel = "";
        static LatencySampler s_Latency;
        static Runner s_Runner;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlay()
        {
            s_Frames.Clear();
            s_FrameEnd = 0;
            s_Latency = null;
            s_Runner = null;
        }

        public static bool LatencyActive => s_Latency != null;

        [ApiEndpoint("chris.perf.frametime",
            "Measurement: records the app frame interval for the given seconds and logs median/p95/max (optional label)")]
        public static string RecordFrameTime(string args)
        {
            var parts = (args ?? "").Split(',');
            int seconds = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 60;
            s_FrameLabel = parts.Length > 1 ? parts[1].Trim() : "";
            s_Frames.Clear();
            s_FrameEnd = Time.unscaledTime + seconds;
            EnsureRunner();
            string started = $"CHRIS perf frametime started: {seconds} s, label '{s_FrameLabel}'";
            Debug.Log(started);
            return started;
        }

        [ApiEndpoint("chris.perf.latency",
            "Measurement: 1 starts keyboard/mouse-to-action latency sampling, 0 stops and logs it")]
        public static string Latency(int on)
        {
            if (on != 0)
            {
                s_Latency = new LatencySampler();
                Debug.Log("CHRIS perf latency started");
                return "CHRIS perf latency started";
            }
            if (s_Latency == null) return "CHRIS perf latency was not running";
            if (s_FrameEnd > 0)
            {
                const string busy = "CHRIS perf latency not stopped: a frame-time window is running; stop latency after it ends";
                Debug.LogWarning(busy);
                return busy;
            }
            var lines = s_Latency.Summaries().Select(kv => $"CHRIS perf latency (device-update timing estimate) {kv.Key}: {kv.Value}").ToList();
            if (lines.Count == 0) lines.Add("CHRIS perf latency: no samples");
            string raw = WriteRaw(RawDirectory, "latency", "", s_Latency.RawCsv());
            if (raw != null) lines.Add("CHRIS perf latency raw samples: " + raw);
            foreach (var line in lines) Debug.Log(line);
            s_Latency = null;
            return string.Join("\n", lines);
        }

        // Called by the mapping host in the frame it applies an action; eventTime is the device's
        // Input System lastUpdateTime (same clock as InputState.currentTime).
        internal static void MarkAction(string action, double eventTime) =>
            s_Latency?.Offer(action, eventTime, UnityEngine.InputSystem.LowLevel.InputState.currentTime);

        static void EnsureRunner()
        {
            if (s_Runner != null) return;
            var go = new GameObject("CHRIS perf probe") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            s_Runner = go.AddComponent<Runner>();
        }

        // Raw samples for independent recalculation; failure to write never affects the app.
        static string RawDirectory => System.IO.Path.Combine(Application.persistentDataPath, "CHRIS", "perf");

        // Each capture gets a unique file: timestamp to the millisecond plus a short random capture ID,
        // opened with FileMode.CreateNew so an existing file is never overwritten (a clash retries with a
        // new ID). Synchronous by design; see the class comment for when it may run.
        internal static string WriteRaw(string dir, string kind, string label, IEnumerable<string> rows)
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                string safeLabel = string.IsNullOrEmpty(label) ? "" : "-" + string.Concat(label.Where(char.IsLetterOrDigit));
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    string id = System.Guid.NewGuid().ToString("N").Substring(0, 8);
                    string path = System.IO.Path.Combine(dir, $"{kind}{safeLabel}-{System.DateTime.Now:yyyyMMdd-HHmmss-fff}-{id}.csv");
                    try
                    {
                        using (var stream = new System.IO.FileStream(path, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write))
                        using (var writer = new System.IO.StreamWriter(stream))
                            foreach (var row in rows) writer.WriteLine(row);
                        return path;
                    }
                    catch (System.IO.IOException) when (System.IO.File.Exists(path)) { }
                }
                throw new System.IO.IOException("no unique raw-sample file name after 5 attempts");
            }
            catch (System.Exception error) { Debug.LogWarning("CHRIS perf could not write raw samples: " + error.Message); return null; }
        }

        static float RefreshRate()
        {
            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            foreach (var display in displays)
                if (display.running && display.TryGetDisplayRefreshRate(out var rate) && rate > 0) return rate;
            return 0;
        }

        static void FinishFrameWindow()
        {
            var summary = Summarize(s_Frames);
            float rate = RefreshRate();
            int missed = rate > 0 ? s_Frames.Count(ms => ms > 1.5 * 1000.0 / rate) : -1;
            string raw = WriteRaw(RawDirectory, "frametime", s_FrameLabel,
                new[] { "frame_interval_ms" }.Concat(s_Frames.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "CHRIS perf frametime '{0}': {1}; refresh {2:F1} Hz; app frame intervals over 1.5x refresh period: {3} (compositor misses not observed); build {4}; raw {5}",
                s_FrameLabel, summary, rate, missed, App.Config != null ? App.Config.m_BuildStamp : "?", raw ?? "not written"));
            s_FrameEnd = 0;
        }

        sealed class Runner : MonoBehaviour
        {
            void Update()
            {
                if (s_FrameEnd <= 0) return;
                s_Frames.Add(Time.unscaledDeltaTime * 1000.0);
                if (Time.unscaledTime >= s_FrameEnd) FinishFrameWindow();
            }
        }
    }
}
