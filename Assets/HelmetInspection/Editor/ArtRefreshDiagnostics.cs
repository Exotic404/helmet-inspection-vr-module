using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace HelmetInspection.Editor
{
    public static class ArtRefreshDiagnostics
    {
        public static void Audit()
        {
            EditorSceneManager.OpenScene(EnvironmentArtPassInstaller.ScenePath);
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                Debug.Log($"[ArtAudit] ROOT {root.name} p={root.transform.position:F3} s={root.transform.lossyScale:F3}");
            foreach(var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
                if (!(r is LineRenderer)) Debug.Log($"[ArtAudit] R {PathOf(r.transform)} enabled={r.enabled} p={r.transform.position:F3} bounds={r.bounds} mat={r.sharedMaterial?.name} shader={r.sharedMaterial?.shader?.name}");
            foreach(var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
                Debug.Log($"[ArtAudit] LIGHT {l.name} p={l.transform.position} type={l.type} intensity={l.intensity} color={l.color} active={l.enabled}");
            foreach(var t in UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include))
                Debug.Log($"[ArtAudit] TEXT {t.name} pos={t.transform.position} size={t.fontSize} scale={t.transform.lossyScale} rect={t.rectTransform.rect} text={t.text}");
            foreach(var c in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include))
                Debug.Log($"[ArtAudit] COLLIDER {PathOf(c.transform)} bounds={c.bounds} enabled={c.enabled}");
        }
        static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent)+"/"+t.name;
    }
}
