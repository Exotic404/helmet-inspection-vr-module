using System;
using System.Collections.Generic;
using UnityEngine;

namespace HelmetInspection
{
    public enum DefectCategory
    {
        LocalDeformation,
        ImpactDent,
        EdgeDistortion,
        CrownDepression,
        SurfaceBulge,
        MissingGeometryHole
    }

    public enum DefectSeverity
    {
        Advisory,
        Moderate,
        Critical
    }

    [Serializable]
    public sealed class DefectRecord
    {
        public string id;
        public string title;
        public DefectCategory category;
        public DefectSeverity severity;
        public Vector3 localPosition;
        public Vector3 localNormal;
        [Min(0.001f)] public float markerRadius = 0.012f;
        [Min(0f)] public float deviationMillimeters;
        [TextArea(2, 5)] public string inspectionNote;
        [TextArea(2, 5)] public string correctiveAction;
        public int sourceVertex;
        public int sourceClusterSize;

        public bool IsHole => category == DefectCategory.MissingGeometryHole;
    }

    [CreateAssetMenu(fileName = "DefectSet_A2", menuName = "Helmet Inspection/Defect Set")]
    public sealed class DefectSet : ScriptableObject
    {
        [SerializeField] string referenceModel = "A1_helmet.glb";
        [SerializeField] string inspectedModel = "A2_defective_scan.glb";
        [SerializeField] string sourceUnits = "millimeters converted to Unity meters at 0.001";
        [SerializeField] float candidateThresholdMillimeters = 2.0f;
        [SerializeField] List<DefectRecord> defects = new List<DefectRecord>();

        public string ReferenceModel => referenceModel;
        public string InspectedModel => inspectedModel;
        public string SourceUnits => sourceUnits;
        public float CandidateThresholdMillimeters => candidateThresholdMillimeters;
        public IReadOnlyList<DefectRecord> Defects => defects;

#if UNITY_EDITOR
        public void SetEditorData(float thresholdMillimeters, List<DefectRecord> curatedDefects)
        {
            candidateThresholdMillimeters = thresholdMillimeters;
            defects = curatedDefects ?? new List<DefectRecord>();
        }
#endif
    }
}
