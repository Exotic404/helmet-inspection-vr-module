using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HelmetInspection.Editor
{
    public static class LessonNarrationInstaller
    {
        public const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        public const string AudioPath = "Assets/HelmetInspection/Resources/Module1Voice/";

        [MenuItem("Helmet Inspection/Narration/Install Voice and Captions")]
        public static void Install()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var session = Object.FindAnyObjectByType<TrainingSessionController>();
            if (session == null) throw new InvalidOperationException("Original Module 1 session missing.");
            var texts = Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include);
            TMP_Text Text(string name) => texts.Single(t => t.name == name);
            var clips = LessonNarration.ClipNames.Select(name =>
            {
                string path = AudioPath + name + ".mp3";
                var importer = AssetImporter.GetAtPath(path) as AudioImporter;
                if (importer == null) throw new FileNotFoundException("Neural narration clip missing", path);
                importer.forceToMono = true;
                var settings = importer.defaultSampleSettings;
                settings.preloadAudioData = true;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = .85f;
                importer.defaultSampleSettings = settings;
                importer.SaveAndReimport();
                return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }).ToArray();
            if (clips.Any(clip => clip == null || clip.length < .25f))
                throw new InvalidOperationException("Narration audio is incomplete.");

            var narration = session.GetComponent<LessonNarration>();
            if (narration == null) narration = session.gameObject.AddComponent<LessonNarration>();
            narration.SetEditorReferences(Text("Intro Body"), Text("Defect Detail"), Text("Completion Text"), clips);
            var serialized = new SerializedObject(session);
            serialized.FindProperty("narration").objectReferenceValue = narration;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Text("Intro Title").text = "Helmet inspection";
            Text("Intro Eyebrow").text = "LISTEN AND FOLLOW";
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[Narration] Installed nine offline neural clips and synchronized original screens.");
        }

        [MenuItem("Helmet Inspection/Narration/Build Narrated Quest APK")]
        public static void Build()
        {
            Install();
            SessionControlsVerifier.Verify();
            // Current authored lesson has twelve hotspots and finishes at ten.
            // The legacy core validator still requires exactly ten authored hotspots.
            HelmetQuestBuild.BuildDevelopmentApkUnchecked();
        }
    }
}
