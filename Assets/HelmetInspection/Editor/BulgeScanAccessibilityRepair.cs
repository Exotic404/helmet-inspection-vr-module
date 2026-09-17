using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HelmetInspection.Editor
{
    /// <summary>Corrects D09 acquisition only; measured markers and geometry stay authored.</summary>
    public static class BulgeScanAccessibilityRepair
    {
        const string DefectId = "A2-D09";
        const string MeshPath = "Assets/HelmetInspection/Models/Generated/A2_defective_scan.asset";

        public static void Apply()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play Mode before configuring the bulge scanner normal.");
            var target = FindTarget();
            var body = target.GetComponentInParent<Rigidbody>();
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            if (body == null || mesh == null)
                throw new InvalidOperationException("D09 helmet body or original mesh was not found.");
            var visual = body.GetComponentsInChildren<MeshFilter>(true).Single(item => item.sharedMesh == mesh);
            var outward = (target.MeasuredCenter - visual.transform.TransformPoint(mesh.bounds.center)).normalized;
            if (outward.sqrMagnitude < .99f)
                throw new InvalidOperationException("D09 does not have a valid exterior-facing direction.");

            // The nearest mesh vertex normal is almost perpendicular to this right-side
            // bulge: +X incidence is only 0.090 (< the scanner's 0.1 front-face limit).
            // Use the shell's exterior direction for acquisition, not one triangle on a
            // strongly curved bulge. Keep its measured point/radius and green rim intact.
            var position = target.transform.localPosition;
            var rotation = target.transform.localRotation;
            var collider = EditorJsonUtility.ToJson(target.GetComponent<SphereCollider>());
            target.SetEditorScannerNormal(outward);
            EditorUtility.SetDirty(target);
            if (PrefabUtility.IsPartOfPrefabInstance(target))
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            if (target.transform.localPosition != position || target.transform.localRotation != rotation ||
                EditorJsonUtility.ToJson(target.GetComponent<SphereCollider>()) != collider)
                throw new InvalidOperationException("D09 measured geometry changed unexpectedly.");
            Debug.Log($"[BulgeScan] D09 scanner-facing normal corrected to {outward:F4}. " +
                      "Measured location, marker orientation, radius and all other defects are unchanged.");
        }

        internal static DefectHotspot FindTarget()
        {
            var session = Object.FindFirstObjectByType<TrainingSessionController>(FindObjectsInactive.Include);
            if (session == null || session.DefectSet == null)
                throw new InvalidOperationException("The authored training session is not loaded.");
            return Object.FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Single(item => item.DefectIndex >= 0 && item.DefectIndex < session.DefectSet.Defects.Count &&
                                session.DefectSet.Defects[item.DefectIndex].id == DefectId);
        }
    }
}
