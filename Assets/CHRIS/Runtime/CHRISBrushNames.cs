// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine.Localization.Settings;

namespace TiltBrush
{
    // Catalog/localization work is done when the catalog changes, never for every context poll.
    internal sealed class CHRISBrushNames : IDisposable
    {
        BrushCatalog m_Catalog;
        IEnumerable<BrushDescriptor> m_Source;
        internal int BuildCount { get; private set; }
        JObject m_Names = new JObject();
        long m_Generation;
        bool m_Dirty = true, m_Disposed;

        public CHRISBrushNames() { LocalizationSettings.SelectedLocaleChanged += LocaleChanged; }
        void LocaleChanged(UnityEngine.Localization.Locale _) => Invalidate();
        void Invalidate() { m_Dirty = true; m_Generation++; }

        public JObject Snapshot(BrushCatalog catalog)
        {
            if (m_Catalog != catalog)
            {
                if (m_Catalog != null) m_Catalog.BrushCatalogChanged -= Invalidate;
                m_Catalog = catalog;
                if (catalog != null) catalog.BrushCatalogChanged += Invalidate;
                Invalidate();
            }
            if (m_Dirty || !ReferenceEquals(m_Source, catalog.AllBrushes))
            {
                m_Source = catalog.AllBrushes;
                BuildCount++;
                m_Dirty = false;
                long generation = ++m_Generation;
                m_Names = new JObject();
                foreach (var brush in m_Source.Where(b => !b.m_HiddenInGui).OrderBy(b => b.m_Guid.ToString()))
                {
                    string id = brush.m_Guid.ToString();
                    m_Names[id] = brush.m_DurableName;
                    LoadLocalizedName(brush, id, generation);
                }
            }
            return (JObject)m_Names.DeepClone();
        }

        void LoadLocalizedName(BrushDescriptor brush, string id, long generation)
        {
            if (brush.m_LocalizedDescription == null || brush.m_LocalizedDescription.IsEmpty) return;
            try
            {
                var operation = brush.m_LocalizedDescription.GetLocalizedStringAsync();
                if (operation.IsDone)
                {
                    if (!string.IsNullOrEmpty(operation.Result)) m_Names[id] = operation.Result;
                }
                else operation.Completed += completed =>
                {
                    if (!m_Disposed && generation == m_Generation && !string.IsNullOrEmpty(completed.Result))
                        m_Names[id] = completed.Result;
                };
            }
            catch (Exception)
            {
                // Match BrushDescriptor's durable-name fallback without blocking context capture.
            }
        }

        public void Dispose()
        {
            m_Disposed = true;
            if (m_Catalog != null) m_Catalog.BrushCatalogChanged -= Invalidate;
            LocalizationSettings.SelectedLocaleChanged -= LocaleChanged;
        }
    }
}
