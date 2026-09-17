using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;

namespace HelmetInspection.Editor
{
    public static class ComfortPresentationVerifier
    {
        public static void VerifyInPlayMode()
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Verify the initialized Play Mode scene.");
            ComfortPresentationInstaller.VerifyConfiguration();
            foreach (var overlay in UnityEngine.Object.FindObjectsByType<TunnelingVignetteController>(FindObjectsInactive.Include))
                foreach (var renderer in overlay.GetComponentsInChildren<Renderer>(true))
                    if (renderer.enabled && renderer.gameObject.activeInHierarchy)
                        throw new InvalidOperationException("Peripheral dimming mesh is still able to render.");
            Debug.Log("[ComfortPresentation] PASS: no runtime peripheral overlay can render, at rest or during locomotion.");
        }
    }
}
