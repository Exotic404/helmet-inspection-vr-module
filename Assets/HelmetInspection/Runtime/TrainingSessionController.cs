using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR;

namespace HelmetInspection
{
    public sealed class TrainingSessionController : MonoBehaviour
    {
        const int RequiredFindings = 10;

        [SerializeField] DefectSet defectSet;
        [SerializeField] Transform inspectedHelmet;
        [SerializeField] TMP_Text progressText;
        [SerializeField] TMP_Text detailText;
        [SerializeField] TMP_Text objectiveText;
        [SerializeField] GameObject startPanel;
        [SerializeField] GameObject completionPanel;
        [SerializeField] List<DefectHotspot> hotspots = new List<DefectHotspot>();

        readonly HashSet<int> m_Found = new HashSet<int>();
        AudioSource m_AudioSource;
        AudioClip m_ConfirmationChime;
        bool m_Started;
        bool m_Complete;
        int m_SessionGeneration;

        public bool IsStarted => m_Started;
        public bool IsComplete => m_Complete;
        public int FoundCount => m_Found.Count;
        public int AvailableDefectCount => defectSet != null ? defectSet.Defects.Count : hotspots.Count;
        public int TargetCount => Mathf.Min(RequiredFindings, AvailableDefectCount);
        public DefectSet DefectSet => defectSet;

        void Awake()
        {
            // Recover automatically from manually duplicated/repositioned hotspot objects.
            // The saved list is still maintained by the authoring synchronizer, but using
            // the live scene set here prevents an omitted list entry from becoming an
            // unscannable defect on device.
            var sceneHotspots = FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (sceneHotspots.Length > 0)
            {
                System.Array.Sort(sceneHotspots, (left, right) => left.DefectIndex.CompareTo(right.DefectIndex));
                hotspots = new List<DefectHotspot>(sceneHotspots);
            }

            m_AudioSource = gameObject.AddComponent<AudioSource>();
            m_AudioSource.spatialBlend = 0f;
            m_ConfirmationChime = BuildConfirmationChime();
            if (completionPanel != null)
                completionPanel.SetActive(false);
            SetHotspotsEnabled(false);
            UpdateProgress();
            if (detailText != null)
                detailText.text = "Press START.\nThen inspect A2 with the scanner.\nCompare it against reference helmet A1.";
        }

        public void BeginTraining()
        {
            if (m_Started || TargetCount <= 0)
                return;

            m_Started = true;
            if (startPanel != null)
                startPanel.SetActive(false);
            SetHotspotsEnabled(true);
            if (objectiveText != null)
                objectiveText.text = $"ACTIVE INSPECTION  /  FIND ANY {TargetCount} OF {AvailableDefectCount} A2 DEVIATIONS";
            if (detailText != null)
                detailText.text = "Aim INS-01 at A2.\nMove its glowing tip close to the shell.\nPress the trigger to confirm a finding.";
            PulseHaptics(XRNode.LeftHand, 0.25f, 0.08f);
            PulseHaptics(XRNode.RightHand, 0.25f, 0.08f);
        }

        public bool RegisterDefect(int index, DefectHotspot hotspot)
        {
            if (!m_Started || m_Complete || defectSet == null || index < 0 || index >= defectSet.Defects.Count)
                return false;

            if (!m_Found.Add(index))
            {
                ShowDetail(defectSet.Defects[index], true);
                return false;
            }

            if (hotspot != null)
                hotspot.MarkFound();
            m_AudioSource.PlayOneShot(m_ConfirmationChime, 0.65f);
            PulseHaptics(XRNode.LeftHand, 0.45f, 0.12f);
            PulseHaptics(XRNode.RightHand, 0.25f, 0.08f);
            ShowDetail(defectSet.Defects[index], false);
            UpdateProgress();

            if (m_Found.Count >= TargetCount)
            {
                // Finish immediately, before the short confirmation delay. Otherwise
                // a second trigger or another hand could accept an eleventh finding.
                m_Complete = true;
                SetHotspotsEnabled(false);
                StartCoroutine(CompleteAfterFeedback(m_SessionGeneration));
            }
            return true;
        }

        public void ResetTraining()
        {
            ++m_SessionGeneration;
            StopAllCoroutines();
            m_Started = false;
            m_Complete = false;

            // RESTART is a complete physical reset. Cancel active grabs and restore
            // every movable training prop before clearing the inspection state.
            foreach (var recovery in FindObjectsByType<HelmetOutOfBoundsRecovery>(FindObjectsInactive.Include))
                if (recovery != null)
                    recovery.ResetToHomeNow();
            foreach (var scanner in FindObjectsByType<InspectionScanner>(FindObjectsInactive.Include))
                if (scanner != null)
                    scanner.ResetPlacement();

            m_Found.Clear();
            foreach (var hotspot in hotspots)
                if (hotspot != null)
                    hotspot.ResetState();
            if (completionPanel != null)
                completionPanel.SetActive(false);
            if (startPanel != null)
                startPanel.SetActive(true);
            if (objectiveText != null)
                objectiveText.text = "MODULE 1  /  HELMET DEFECT INSPECTION";
            if (detailText != null)
                detailText.text = "Press START.\nThen inspect A2 with the scanner.\nCompare it against reference helmet A1.";
            SetHotspotsEnabled(false);
            UpdateProgress();
        }

        void SetHotspotsEnabled(bool value)
        {
            foreach (var hotspot in hotspots)
                if (hotspot != null)
                    hotspot.SetInspectionEnabled(value);
        }

        void ShowDetail(DefectRecord defect, bool alreadyFound)
        {
            if (detailText == null)
                return;

            var state = alreadyFound ? "ALREADY LOGGED" : "DEFECT CONFIRMED";
            var measurement = defect.IsHole
                ? $"Opening radius: {defect.markerRadius * 1000f:0.0} mm"
                : $"Deviation: {defect.deviationMillimeters:0.0} mm";
            detailText.text =
                $"{state}  /  {defect.id}\n" +
                $"{defect.title}\n" +
                $"{measurement}\n" +
                $"Severity: {defect.severity}\n\n" +
                $"{defect.inspectionNote}\n\n" +
                $"REQUIRED ACTION\n{defect.correctiveAction}";
        }

        void UpdateProgress()
        {
            if (progressText == null)
                return;
            progressText.text = $"QA FINDINGS  {m_Found.Count:00} / {TargetCount:00}";
        }

        IEnumerator CompleteAfterFeedback(int sessionGeneration)
        {
            yield return new WaitForSeconds(0.55f);
            // A pending completion from a previous attempt must never overwrite the
            // UI after RESET, even if this routine is resumed by another caller.
            if (sessionGeneration != m_SessionGeneration || !m_Started || !m_Complete)
                yield break;
            if (completionPanel != null)
                completionPanel.SetActive(true);
            if (objectiveText != null)
                objectiveText.text = $"INSPECTION COMPLETE  /  {TargetCount} OF {AvailableDefectCount} DEVIATIONS DOCUMENTED";
            if (detailText != null)
                detailText.text = $"Inspection target reached: {TargetCount} findings.\nA2 is on quality hold.\nReview the logged defects.\nPress RESET to repeat the exercise.";
            PulseHaptics(XRNode.LeftHand, 0.7f, 0.18f);
            PulseHaptics(XRNode.RightHand, 0.7f, 0.18f);
        }

        static void PulseHaptics(XRNode node, float amplitude, float duration)
        {
            var devices = new List<InputDevice>();
            InputDevices.GetDevicesAtXRNode(node, devices);
            foreach (var device in devices)
                if (device.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse)
                    device.SendHapticImpulse(0u, Mathf.Clamp01(amplitude), duration);
        }

        static AudioClip BuildConfirmationChime()
        {
            const int sampleRate = 44100;
            const float duration = 0.22f;
            var samples = Mathf.CeilToInt(sampleRate * duration);
            var data = new float[samples];
            for (var i = 0; i < samples; ++i)
            {
                var t = i / (float)sampleRate;
                var envelope = Mathf.Exp(-11f * t);
                data[i] = envelope * (Mathf.Sin(2f * Mathf.PI * 880f * t) * 0.32f + Mathf.Sin(2f * Mathf.PI * 1320f * t) * 0.16f);
            }
            var clip = AudioClip.Create("QA Confirmation Chime", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

#if UNITY_EDITOR
        public void SetEditorReferences(DefectSet set, Transform helmet, TMP_Text progress, TMP_Text detail,
            TMP_Text objective, GameObject intro, GameObject completion, List<DefectHotspot> sceneHotspots)
        {
            defectSet = set;
            inspectedHelmet = helmet;
            progressText = progress;
            detailText = detail;
            objectiveText = objective;
            startPanel = intro;
            completionPanel = completion;
            hotspots = sceneHotspots;
        }
#endif
    }
}
