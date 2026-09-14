using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HelmetInspection.Editor
{
    /// <summary>Protects the authored studio from obsolete scene generators.</summary>
    internal static class LegacyGenerationGuard
    {
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        const string StudioMarker = "ART - Metrology Studio";
        static int s_ScopeDepth;

        internal static bool TryBegin(string operation, out IDisposable scope)
        {
            scope = null;
            if (s_ScopeDepth == 0 && File.Exists(ScenePath) &&
                File.ReadAllText(ScenePath).IndexOf(StudioMarker, StringComparison.Ordinal) >= 0)
            {
                var message = operation + " is a legacy generator. It can replace authored scene work, " +
                              "materials, lighting, and defect data with an older generated layout. " +
                              "The saved scene currently contains the Metrology Studio.\n\n" +
                              "For normal editing, use Helmet Inspection > Metrology Studio. " +
                              "Make a backup before deliberately running this legacy generator.";
                if (Application.isBatchMode)
                    throw new InvalidOperationException("[LegacyGeneration] Refusing to overwrite the current " +
                                                        "Metrology Studio in batch mode. " + message);

                // Keep both the Enter/default result (0) and Escape/close result (1)
                // non-destructive. Only explicitly choosing the alternate button (2)
                // permits replacement; swapping DisplayDialog's labels would let Esc
                // accidentally approve the destructive action.
                var choice = EditorUtility.DisplayDialogComplex("Replace authored Metrology Studio?", message,
                    "Keep current studio", "Cancel", "Run legacy generator");
                if (choice != 2)
                    return false;
            }

            ++s_ScopeDepth;
            scope = new GenerationScope();
            return true;
        }

        sealed class GenerationScope : IDisposable
        {
            bool m_Disposed;

            public void Dispose()
            {
                if (m_Disposed)
                    return;
                m_Disposed = true;
                --s_ScopeDepth;
            }
        }
    }
}
