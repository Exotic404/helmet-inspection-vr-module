using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HelmetInspection
{
    // Offline, sentence-sized voice clips. Captions and speech always advance together.
    public sealed class LessonNarration : MonoBehaviour
    {
        public static readonly string[] ClipNames =
            { "welcome", "begin", "scanner", "scan", "target", "logged", "complete", "restart", "reset" };
        public static readonly string[] Captions =
        {
            "Welcome. Compare the reference helmet, A one, with the scan, A two.",
            "Point at the green start button and squeeze the grip to begin.",
            "Use the scanner on your left wrist, or pick it up with either hand.",
            "Bring the glowing tip close to a defect on A two, and press the trigger.",
            "Find ten defects. Rotate the helmet to check every side.",
            "Defect logged.",
            "Inspection complete. A2 was put on hold.",
            "Press the red restart button to try again.",
            "Session reset."
        };

        [SerializeField] TMP_Text instructionText;
        [SerializeField] TMP_Text detailText;
        [SerializeField] TMP_Text completionText;
        [SerializeField] AudioClip[] clips = Array.Empty<AudioClip>();
        AudioSource m_Guide;
        AudioSource m_Feedback;
        Coroutine m_Sequence;
        Coroutine m_LogRoutine;
        int m_PendingLogs;
        string m_GuideCaption = Captions[0];

        public bool IsLogging => m_LogRoutine != null;
        public string CurrentCaption { get; private set; } = Captions[0];
        public string CurrentCue { get; private set; } = "welcome";
        public int LoggedCueCount { get; private set; }
        public IReadOnlyList<AudioClip> Clips => clips;

        void Awake()
        {
            m_Guide = CreateSource();
            m_Feedback = CreateSource();
        }

        AudioSource CreateSource()
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = .85f;
            source.pitch = 1f;
            source.bypassEffects = true;
            source.bypassListenerEffects = true;
            source.bypassReverbZones = true;
            return source;
        }

        public void PlayWelcome() => StartSequence(new[] { 0, 1 }, .6f);
        public void PlayInspection() => StartSequence(new[] { 2, 3, 4 });
        public void PlayCompletion() => StartSequence(new[] { 6, 7 });

        public void ResetGuide()
        {
            StopAllCoroutines();
            m_Guide.Stop();
            m_Feedback.Stop();
            m_Sequence = m_LogRoutine = null;
            m_PendingLogs = LoggedCueCount = 0;
            StartSequence(new[] { 8, 1 });
        }

        public void DefectLogged()
        {
            ++m_PendingLogs;
            if (m_LogRoutine == null)
                m_LogRoutine = StartCoroutine(PlayLoggedFeedback());
        }

        void StartSequence(int[] indices, float delay = 0f)
        {
            if (m_Sequence != null) StopCoroutine(m_Sequence);
            m_Guide.Stop();
            m_Sequence = StartCoroutine(PlaySequence(indices, delay));
        }

        IEnumerator PlaySequence(int[] indices, float delay)
        {
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
            foreach (int index in indices)
            {
                while (IsLogging) yield return null;
                m_GuideCaption = Captions[index];
                CurrentCue = ClipNames[index];
                ShowCaption(m_GuideCaption);
                m_Guide.clip = clips[index];
                m_Guide.Play();
                do { yield return null; } while (m_Guide.isPlaying || IsLogging);
            }
            m_Sequence = null;
        }

        IEnumerator PlayLoggedFeedback()
        {
            bool resumeGuide = m_Guide.isPlaying;
            m_Guide.Pause();
            string previousCue = CurrentCue;
            while (m_PendingLogs > 0)
            {
                --m_PendingLogs;
                ++LoggedCueCount;
                CurrentCue = "logged";
                ShowCaption(Captions[5]);
                m_Feedback.clip = clips[5];
                m_Feedback.Play();
                do { yield return null; } while (m_Feedback.isPlaying);
            }
            CurrentCue = previousCue;
            ShowCaption(m_GuideCaption);
            if (resumeGuide) m_Guide.UnPause();
            m_LogRoutine = null;
        }

        void ShowCaption(string caption)
        {
            CurrentCaption = caption;
            if (instructionText != null) instructionText.text = caption;
            if (detailText != null) detailText.text = caption;
            if (completionText != null) completionText.text = caption;
        }

        void OnDisable()
        {
            StopAllCoroutines();
            if (m_Guide != null) m_Guide.Stop();
            if (m_Feedback != null) m_Feedback.Stop();
            m_Sequence = m_LogRoutine = null;
            m_PendingLogs = 0;
        }

#if UNITY_EDITOR
        public void SetEditorReferences(TMP_Text instruction, TMP_Text detail, TMP_Text completion, AudioClip[] voiceClips)
        {
            instructionText = instruction;
            detailText = detail;
            completionText = completion;
            clips = voiceClips;
            ShowCaption(Captions[0]);
        }
#endif
    }
}
