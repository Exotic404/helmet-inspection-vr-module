using UnityEngine;

namespace HelmetInspection
{
    public sealed class QuestRuntimeSettings : MonoBehaviour
    {
        [SerializeField, Range(72, 120)] int targetFrameRate = 90;

        void Awake()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = targetFrameRate;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }
    }
}
