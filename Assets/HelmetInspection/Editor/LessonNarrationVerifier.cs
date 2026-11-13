using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace HelmetInspection.Editor
{
    /// <summary>Exercises the real offline voice clips and session lifecycle without saving scene changes.
    /// Batch entry point: RunPlayMode, without -quit or -noaudio.</summary>
    public static class LessonNarrationVerifier
    {
        const string PendingKey = "HelmetInspection.NarrationVerifier.Pending";
        const string HashKey = "HelmetInspection.NarrationVerifier.SceneHash";
        const string ReportPath = "Builds/NarrationValidation.txt";
        static readonly List<string> Results = new List<string>();
        static IEnumerator s_Run;
        static double s_Deadline;
        static int s_LastFrame = -1;
        static TMP_Text[] s_Captions;

        [MenuItem("Helmet Inspection/Narration/Verify Live Narration")]
        public static void RunPlayMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Start narration verification outside Play Mode.");
            for (int i = 0; i < SceneManager.sceneCount; ++i)
                if (SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("Save open scene edits before narration verification.");
            EditorSceneManager.OpenScene(LessonNarrationInstaller.ScenePath, OpenSceneMode.Single);
            SessionState.SetString(HashKey, HashScene());
            SessionState.SetBool(PendingKey, true);
            Resume();
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        static void Resume()
        {
            if (!SessionState.GetBool(PendingKey, false)) return;
            Results.Clear();
            s_Run = null;
            s_LastFrame = -1;
            s_Deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > s_Deadline)
                    throw new TimeoutException("Narration verification exceeded its deadline.");
                if (!EditorApplication.isPlaying || Time.timeSinceLevelLoad < .85f ||
                    s_LastFrame == Time.frameCount) return;
                s_LastFrame = Time.frameCount;
                if (s_Run == null)
                {
                    s_Deadline = EditorApplication.timeSinceStartup + 40;
                    s_Run = VerifyLiveScene();
                }
                if (!s_Run.MoveNext()) Finish(null);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                Finish(error);
            }
        }

        static IEnumerator VerifyLiveScene()
        {
            var session = Object.FindObjectsByType<TrainingSessionController>(FindObjectsInactive.Include).Single();
            var narration = Object.FindObjectsByType<LessonNarration>(FindObjectsInactive.Include).Single();
            var hotspots = Object.FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include)
                .OrderBy(item => item.DefectIndex).ToArray();
            var texts = Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include);
            s_Captions = new[] { "Intro Body", "Defect Detail", "Completion Text" }
                .Select(name => texts.Single(item => item.name == name)).ToArray();
            var sessionData = new SerializedObject(session);
            var intro = (GameObject)sessionData.FindProperty("startPanel").objectReferenceValue;
            var completion = (GameObject)sessionData.FindProperty("completionPanel").objectReferenceValue;
            var art = GameObject.Find(ArtRefreshInstaller.ArtName);
            Require(art != null && art.activeInHierarchy, "Original Metrology Studio artwork remains active.");
            Require(hotspots.Length == 12 && session.AvailableDefectCount == 12 && session.TargetCount == 10 &&
                    hotspots.Select(item => item.DefectIndex).SequenceEqual(Enumerable.Range(0, 12)),
                "Original twelve independent defects and ten-finding target are retained.");
            Require(sessionData.FindProperty("narration").objectReferenceValue == narration,
                "The original session references the installed narration component.");
            var narrationData = new SerializedObject(narration);
            var fields = new[] { "instructionText", "detailText", "completionText" };
            Require(fields.Select((name, index) => narrationData.FindProperty(name).objectReferenceValue == s_Captions[index])
                    .All(value => value), "All three original screen text fields are bound to the voice captions.");
            Require(narration.Clips.Count == 9 && narration.Clips.Select((clip, index) => clip != null &&
                    clip.name == LessonNarration.ClipNames[index] && clip.samples > 1000 && clip.length > .25f &&
                    clip.loadState == AudioDataLoadState.Loaded && AssetDatabase.Contains(clip)).All(value => value),
                "Nine correctly ordered offline narration clips are loaded from project assets.");
            Require(narration.Clips[5].length > .7f && narration.Clips[5].length < 2.5f,
                "The real logged clip is short and spans the legacy 0.55-second completion delay.");
            Require(!session.IsStarted && !session.IsComplete && session.FoundCount == 0 &&
                    hotspots.All(item => !item.CanInspect) && intro.activeInHierarchy && !completion.activeSelf,
                "Boot retains START gating and hides completion.");
            CheckCaption(narration, "welcome");
            Require(Playing(narration, narration.Clips[0]) != null,
                "Welcome narration actually plays through an audible AudioSource.");
            Require(!session.RegisterDefect(0, hotspots[0]) && narration.LoggedCueCount == 0 &&
                    narration.CurrentCue == "welcome" && !narration.IsLogging,
                "Scanning before START is rejected without spoken confirmation.");

            session.BeginTraining();
            CheckCaption(narration, "scanner");
            Require(intro.activeInHierarchy && s_Captions.Take(2).All(text => text.enabled && text.gameObject.activeInHierarchy),
                "Both instruction screens stay visible after START.");
            var wait = Wait(.2f);
            while (wait.MoveNext()) yield return null;
            var guide = Playing(narration, narration.Clips[2]);
            Require(guide != null, "START plays the scanner instruction using the real voice clip.");
            float instructionTime = guide.time;
            Require(session.RegisterDefect(0, hotspots[0]) && session.FoundCount == 1 &&
                    narration.LoggedCueCount == 1 && narration.IsLogging,
                "One accepted finding starts exactly one spoken logged confirmation.");
            CheckCaption(narration, "logged");
            Require(narration.CurrentCaption == "Defect logged." && Playing(narration, narration.Clips[5]) != null &&
                    !guide.isPlaying, "Scanning says only 'Defect logged.' and pauses the instruction voice.");
            Require(!session.RegisterDefect(0, hotspots[0]) && !session.RegisterDefect(-1, null) &&
                    !session.RegisterDefect(99, null) && narration.LoggedCueCount == 1,
                "Duplicate and invalid findings add no spoken confirmations.");
            var feedback = WaitForFeedback(narration);
            while (feedback.MoveNext()) yield return null;
            CheckCaption(narration, "scanner");
            Require(narration.LoggedCueCount == 1 && guide.isPlaying,
                "Instruction audio and matching captions resume after the confirmation.");
            wait = Wait(.12f);
            while (wait.MoveNext()) yield return null;
            Require(guide.time > instructionTime, "The interrupted instruction resumes from its playback position.");

            // Real accepted scans with real audio: no synthetic clips, accelerated pitch, or private state writes.
            for (int i = 1; i < 9; ++i)
            {
                Require(session.RegisterDefect(i, hotspots[i]) && narration.LoggedCueCount == i + 1,
                    "Finding " + (i + 1) + " produces one confirmation.");
                feedback = WaitForFeedback(narration);
                while (feedback.MoveNext()) yield return null;
            }
            Require(session.FoundCount == 9 && !session.IsComplete, "Nine findings do not complete the original lesson.");
            Require(session.RegisterDefect(9, hotspots[9]) && session.IsComplete && session.FoundCount == 10 &&
                    narration.LoggedCueCount == 10 && !completion.activeSelf,
                "The tenth finding locks scanning while final spoken feedback is still playing.");
            wait = Wait(.6f);
            while (wait.MoveNext()) yield return null;
            CheckCaption(narration, "logged");
            Require(narration.IsLogging && !completion.activeSelf && Playing(narration, narration.Clips[5]) != null,
                "Final 'Defect logged' survives the old completion delay without interruption or overlap.");
            feedback = WaitForFeedback(narration);
            while (feedback.MoveNext()) yield return null;
            float until = Time.realtimeSinceStartup + 1f;
            while (narration.CurrentCue != "complete" && Time.realtimeSinceStartup < until) yield return null;
            CheckCaption(narration, "complete");
            Require(completion.activeInHierarchy && Playing(narration, narration.Clips[6]) != null &&
                    narration.LoggedCueCount == 10 && hotspots.All(item => !item.CanInspect),
                "Completion narration and all screen captions follow the final confirmation.");
            Require(!session.RegisterDefect(10, hotspots[10]) && narration.LoggedCueCount == 10,
                "A scan after completion stays silent and does not add an eleventh finding.");

            session.ResetTraining();
            CheckCaption(narration, "reset");
            Require(!session.IsStarted && !session.IsComplete && session.FoundCount == 0 && !narration.IsLogging &&
                    narration.LoggedCueCount == 0 && !completion.activeSelf &&
                    Playing(narration, narration.Clips[6]) == null && Playing(narration, narration.Clips[8]) != null,
                "RESTART interrupts completion speech and resets the session, captions, and confirmation counter.");

            // Exercise cancellation while many confirmations and a delayed completion are pending.
            session.BeginTraining();
            for (int i = 0; i < 10; ++i)
                if (!session.RegisterDefect(i, hotspots[i]))
                    throw new InvalidOperationException("The reset attempt could not register finding " + i + ".");
            Require(session.IsComplete && narration.IsLogging && !completion.activeSelf,
                "A second attempt reaches completion with queued feedback before the completion delay.");
            session.ResetTraining();
            CheckCaption(narration, "reset");
            Require(!narration.IsLogging && narration.LoggedCueCount == 0 &&
                    Playing(narration, narration.Clips[5]) == null,
                "RESTART stops active scan speech and discards queued confirmations.");
            wait = Wait(1f);
            while (wait.MoveNext()) yield return null;
            Require(!session.IsStarted && !session.IsComplete && session.FoundCount == 0 && !completion.activeSelf &&
                    !narration.IsLogging && narration.LoggedCueCount == 0 &&
                    (narration.CurrentCue == "reset" || narration.CurrentCue == "begin"),
                "No stale completion or scan speech returns after the cancelled attempt.");
            CheckCaption(narration, narration.CurrentCue);
            Require(HashScene() == SessionState.GetString(HashKey, string.Empty),
                "Play Mode validation leaves the saved training scene byte-identical.");
        }

        static IEnumerator WaitForFeedback(LessonNarration narration)
        {
            float until = Time.realtimeSinceStartup + narration.Clips[5].length + 1f;
            while (narration.IsLogging)
            {
                CheckCaption(narration, "logged");
                if (narration.GetComponents<AudioSource>().Count(source => source.isPlaying) > 1)
                    throw new InvalidOperationException("Narration voices overlap during scan feedback.");
                if (Time.realtimeSinceStartup > until)
                    throw new TimeoutException("Logged feedback did not end after its real audio clip.");
                yield return null;
            }
        }

        static void CheckCaption(LessonNarration narration, string cue)
        {
            int index = Array.IndexOf(LessonNarration.ClipNames, cue);
            if (index < 0 || narration.CurrentCue != cue || narration.CurrentCaption != LessonNarration.Captions[index] ||
                s_Captions.Any(text => text.text != narration.CurrentCaption))
                throw new InvalidOperationException("Voice cue and original screen captions disagree: expected " + cue +
                                                    ", actual " + narration.CurrentCue + ".");
        }

        static AudioSource Playing(LessonNarration narration, AudioClip clip) => narration.GetComponents<AudioSource>()
            .SingleOrDefault(source => source.clip == clip && source.isPlaying && source.enabled && !source.mute &&
                                       source.volume > .1f && Mathf.Approximately(source.pitch, 1f));

        static IEnumerator Wait(float duration)
        {
            float until = Time.realtimeSinceStartup + duration;
            while (Time.realtimeSinceStartup < until) yield return null;
        }

        static void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            Results.Add("PASS  " + description);
        }

        static string HashScene()
        {
            using var stream = File.OpenRead(LessonNarrationInstaller.ScenePath);
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(stream));
        }

        static void Finish(Exception error)
        {
            SessionState.SetBool(PendingKey, false);
            EditorApplication.update -= Tick;
            (s_Run as IDisposable)?.Dispose();
            s_Run = null;
            if (error != null) Results.Add("FAIL  " + error.Message);
            Directory.CreateDirectory("Builds");
            File.WriteAllLines(ReportPath, new[]
            {
                "MODULE 1 NARRATION PLAY MODE VALIDATION",
                "Result: " + (error == null ? "PASS" : "FAIL"),
                "Unity: " + Application.unityVersion,
                "Real imported voice clips; normal playback speed; original twelve-defect scene.",
                "Headset listening quality and comfort still require user review."
            }.Concat(Results));
            Debug.Log("[NarrationValidation] " + (error == null ? "PASS" : "FAIL") + ": " + Results.Count +
                      " checks; see " + ReportPath);
            if (Application.isBatchMode) EditorApplication.Exit(error == null ? 0 : 1);
            else EditorApplication.ExitPlaymode();
        }
    }
}
