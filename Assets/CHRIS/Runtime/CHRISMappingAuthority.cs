// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TiltBrush
{
    // Session-local authority for the fixed native mapping file. The HTTP gateway calls this only
    // on Unity's main thread; the pending document stays in memory until a neutral frame.
    internal sealed class CHRISMappingAuthority
    {
        const int MaxReceipts = 256;
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        static readonly string[] ApplyFields = {
            "request_id", "expected_session_id", "expected_revision", "expected_active_digest",
            "proposed_digest", "mapping_json"
        };

        readonly string m_Path;
        readonly HashSet<string> m_RequestIds = new HashSet<string>(StringComparer.Ordinal);
        readonly string m_Session = Guid.NewGuid().ToString("N");
        long m_Revision;
        string m_State = "none";
        // Fingerprint (SHA-256 of the raw file bytes) of the saved file as last observed by a load,
        // a missing-file check, a rejected reload or a persisted activation. A reviewed apply may only
        // replace the file while it still matches. Not part of Status(): the Python wire contract has
        // no field for it, and it is a file fingerprint, not a mapping digest.
        string m_ObservedFileDigest;
        bool m_SavedFileInvalid;
        CHRISInputMapping m_ActiveMapping, m_PendingMapping;
        string m_ActiveJson, m_ActiveDigest, m_ActiveRequestId;
        string m_PendingJson, m_PendingDigest, m_PendingRequestId, m_PendingFileDigest;
        bool m_PendingNeedsPersist;
        string m_LastRequestId, m_LastRequestResult;

        internal CHRISMappingAuthority(string path) { m_Path = System.IO.Path.GetFullPath(path); }
        internal string FilePath => m_Path;
        // Headset-only: the saved file exists but failed validation, so a reviewed apply replaces it.
        internal bool SavedFileInvalid => m_SavedFileInvalid;
        internal long Revision => m_Revision;
        internal bool HasPending => m_PendingMapping != null;
        // Successful writes of an approved mapping; readers of the saved file invalidate caches on it.
        internal int PersistCount { get; private set; }

        // Any change to the observed saved file moves the revision, so an approval reviewed against
        // the earlier file (including a rejected one) is stale. Status fields are unchanged.
        void ObserveFile(string fileDigest)
        {
            if (fileDigest == m_ObservedFileDigest) return;
            m_ObservedFileDigest = fileDigest;
            m_Revision++;
        }
        static JToken Nullable(string value) => value == null ? JValue.CreateNull() : new JValue(value);

        internal JObject Status() => new JObject {
            ["session_id"] = m_Session,
            ["revision"] = m_Revision,
            ["state"] = m_State,
            ["active_digest"] = Nullable(m_ActiveDigest),
            ["active_mapping_json"] = Nullable(m_ActiveJson),
            ["active_request_id"] = Nullable(m_ActiveRequestId),
            ["pending_digest"] = Nullable(m_PendingDigest),
            ["pending_mapping_json"] = Nullable(m_PendingJson),
            ["pending_request_id"] = Nullable(m_PendingRequestId),
            ["last_request_id"] = Nullable(m_LastRequestId),
            ["last_request_result"] = Nullable(m_LastRequestResult)
        };

        internal JObject DiscardPending()
        {
            if (m_PendingMapping != null) CancelPending("user_discarded");
            return Status();
        }

        static JObject Reply(JObject status, string result, string reason)
        {
            status["result"] = result;
            status["reason"] = Nullable(reason);
            return status;
        }

        JObject Reject(string requestId, string reason)
        {
            if (requestId != null)
            {
                m_LastRequestId = requestId;
                m_LastRequestResult = "rejected";
            }
            return Reply(Status(), "rejected", reason);
        }

        internal JObject Apply(JObject request)
        {
            if (request == null || request.Properties().Count() != ApplyFields.Length ||
                request.Properties().Any(p => !ApplyFields.Contains(p.Name)) ||
                ApplyFields.Any(name => request[name] == null))
                return Reject(null, "invalid_envelope");

            string requestId = request["request_id"].Type == JTokenType.String ? (string)request["request_id"] : null;
            if (requestId == null || !Regex.IsMatch(requestId, @"\A[A-Za-z0-9_-]{1,80}\z") ||
                request["expected_session_id"].Type != JTokenType.String ||
                request["expected_revision"].Type != JTokenType.Integer ||
                (request["expected_active_digest"].Type != JTokenType.Null && request["expected_active_digest"].Type != JTokenType.String) ||
                request["proposed_digest"].Type != JTokenType.String ||
                request["mapping_json"].Type != JTokenType.String)
                return Reject(requestId, "invalid_envelope");
            if (m_RequestIds.Contains(requestId)) return Reply(Status(), "rejected", "duplicate_request");
            if (m_RequestIds.Count >= MaxReceipts) return Reject(requestId, "ledger_full");
            m_RequestIds.Add(requestId);

            if ((string)request["expected_session_id"] != m_Session ||
                (long)request["expected_revision"] != m_Revision ||
                (string)request["expected_active_digest"] != m_ActiveDigest)
                return Reject(requestId, "stale_status");
            if (m_PendingMapping != null) return Reject(requestId, "pending_mapping");

            string json = (string)request["mapping_json"];
            byte[] bytes;
            try { bytes = StrictUtf8.GetBytes(json); }
            catch (EncoderFallbackException) { return Reject(requestId, "invalid_encoding"); }
            string digest = CHRISCommandGateway.Hash(json);
            if ((string)request["proposed_digest"] != digest) return Reject(requestId, "digest_mismatch");

            CHRISInputMapping mapping;
            try { mapping = CHRISInputMapping.Parse(bytes); }
            catch (CHRISMappingException error) { return Reject(requestId, error.Code); }

            string fileDigest;
            try { fileDigest = CurrentFileDigest(); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { return Reject(requestId, "file_unavailable"); }
            if (fileDigest != m_ObservedFileDigest) return Reject(requestId, "file_changed");

            Offer(mapping, json, digest, requestId, fileDigest, true);
            m_LastRequestId = requestId;
            m_LastRequestResult = "pending_neutral";
            return Reply(Status(), "pending_neutral", null);
        }

        internal void OfferLoaded(CHRISInputMapping mapping, byte[] raw)
        {
            string json = StrictUtf8.GetString(raw);
            string digest = CHRISCommandGateway.Hash(json);
            string fileDigest = RawDigest(raw);
            ObserveFile(fileDigest);
            m_SavedFileInvalid = false;
            if (m_PendingRequestId != null)
            {
                m_LastRequestId = m_PendingRequestId;
                m_LastRequestResult = "cancelled";
            }
            if (m_ActiveJson == json && m_PendingMapping == null) return;
            Offer(mapping, json, digest, null, fileDigest, false);
        }

        internal void NoteFileMissing()
        {
            ObserveFile(null);
            m_SavedFileInvalid = false;
        }

        // A readable saved file failed validation (bad JSON or UTF-8, schema, too large). Record its
        // exact bytes so an explicitly reviewed apply can replace it; if the file changes again before
        // that, the apply is rejected as file_changed. The file itself is never touched here.
        internal void NoteFileRejected()
        {
            string fileDigest;
            try { fileDigest = CurrentFileDigest(); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return; }
            ObserveFile(fileDigest);
            m_SavedFileInvalid = fileDigest != null;
        }

        void Offer(CHRISInputMapping mapping, string json, string digest, string requestId,
            string fileDigest, bool persist)
        {
            m_PendingMapping = mapping;
            m_PendingJson = json;
            m_PendingDigest = digest;
            m_PendingRequestId = requestId;
            m_PendingFileDigest = fileDigest;
            m_PendingNeedsPersist = persist;
            m_State = "pending_neutral";
            m_Revision++;
        }

        internal void TryActivate(ICHRISInputState input, bool focused, bool stroke, CHRISInputRemap remap)
        {
            if (m_PendingMapping == null || input.StopPressedThisFrame ||
                !remap.IsNeutralFor(m_PendingMapping, input, focused, stroke)) return;
            try
            {
                if (CurrentFileDigest() != m_PendingFileDigest)
                {
                    CancelPending("file_changed");
                    return;
                }
                if (m_PendingNeedsPersist)
                {
                    byte[] bytes = StrictUtf8.GetBytes(m_PendingJson);
                    AtomicWrite(bytes);
                    // CompleteActivation advances the revision for this activation.
                    m_ObservedFileDigest = RawDigest(bytes);
                    m_SavedFileInvalid = false;
                    PersistCount++;
                }
                remap.Offer(m_PendingMapping);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is CHRISMappingException || error is DecoderFallbackException)
            {
                CancelPending("persist_failed");
            }
        }

        internal void CompleteActivation(CHRISInputRemap remap)
        {
            if (m_PendingMapping == null || !ReferenceEquals(remap.Active, m_PendingMapping)) return;
            m_ActiveMapping = m_PendingMapping;
            m_ActiveJson = m_PendingJson;
            m_ActiveDigest = m_PendingDigest;
            m_ActiveRequestId = m_PendingRequestId;
            if (m_PendingRequestId != null)
            {
                m_LastRequestId = m_PendingRequestId;
                m_LastRequestResult = "active";
            }
            ClearPending();
            m_State = "active";
            m_Revision++;
        }

        internal void Stop()
        {
            if (m_PendingRequestId != null)
            {
                m_LastRequestId = m_PendingRequestId;
                m_LastRequestResult = "cancelled";
            }
            ClearPending();
            m_ActiveMapping = null;
            m_ActiveJson = m_ActiveDigest = m_ActiveRequestId = null;
            m_State = "stopped";
            m_Revision++;
        }

        void CancelPending(string reason)
        {
            if (m_PendingRequestId != null)
            {
                m_LastRequestId = m_PendingRequestId;
                m_LastRequestResult = "cancelled";
            }
            ClearPending();
            m_State = m_ActiveMapping == null ? "none" : "active";
            m_Revision++;
            UnityEngine.Debug.LogWarning("CHRIS mapping activation cancelled: " + reason);
        }

        void ClearPending()
        {
            m_PendingMapping = null;
            m_PendingJson = m_PendingDigest = m_PendingRequestId = m_PendingFileDigest = null;
            m_PendingNeedsPersist = false;
        }

        // Streams the whole file, so oversized or undecodable files still get a fingerprint.
        string CurrentFileDigest()
        {
            try
            {
                using (var stream = new FileStream(m_Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return Hex(sha.ComputeHash(stream));
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        static string RawDigest(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        static string Hex(byte[] hash) => string.Concat(hash.Select(b => b.ToString("x2")));

        void AtomicWrite(byte[] bytes)
        {
            string directory = System.IO.Path.GetDirectoryName(m_Path);
            Directory.CreateDirectory(directory);
            string temporary = m_Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(m_Path)) File.Replace(temporary, m_Path, null);
                else File.Move(temporary, m_Path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
