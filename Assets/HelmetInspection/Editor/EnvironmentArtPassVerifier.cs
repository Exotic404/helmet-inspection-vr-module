using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HelmetInspection.Editor
{
    /// <summary>
    /// Focused acceptance checks for the environment pass, followed by all existing
    /// gameplay, Quest, collision, reset, and exterior-hole regression checks.
    /// </summary>
    public static class EnvironmentArtPassVerifier
    {
        const string Root = "Assets/HelmetInspection";
        const string Generated = Root + "/EnvironmentArt/Generated";

        [MenuItem("Helmet Inspection/Legacy Generators/Environment Art Pass/Verify Visual Pass and Gameplay")]
        public static void VerifyAll()
        {
            VerifyVisualPass();
            HelmetProjectValidator.ValidateProject();
            ExteriorHoleAccessibilityVerifier.Verify();
            ChildSafeInteractionVerifier.Verify();
            VerifyVisualPass();
            Debug.Log("[EnvironmentArtPassVerification] PASS: upgraded lab visuals and all existing " +
                      "Quest/gameplay, child-safe interaction, reset, collision, and six-hole checks passed.");
        }

        static void VerifyVisualPass()
        {
            EditorSceneManager.OpenScene(EnvironmentArtPassInstaller.ScenePath, OpenSceneMode.Single);
            var artRoot = GameObject.Find(EnvironmentArtPassInstaller.ArtRootName);
            Require(artRoot != null, "The generated environment art root is missing.");
            Require(artRoot.transform.parent != null &&
                    artRoot.transform.parent.name == "ENVIRONMENT - QA Metrology Lab",
                "The visual pass is not grouped below the existing environment root.");
            Require(artRoot.GetComponentsInChildren<Collider>(true).Length == 0,
                "Visual dressing must not introduce gameplay colliders.");
            Require(artRoot.GetComponentsInChildren<MonoBehaviour>(true)
                    .All(item => item is TMPro.TMP_Text),
                "Visual dressing contains a runtime behavior other than display-only text.");

            var artFilters = artRoot.GetComponentsInChildren<MeshFilter>(true);
            var triangles = artFilters.Where(item => item.sharedMesh != null)
                .Sum(item => item.sharedMesh.triangles.Length / 3);
            Require(triangles > 0 && triangles <= 5000,
                $"Visual dressing triangle budget is invalid ({triangles} / 5000).");
            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include);
            Require(renderers.Length <= 150, $"Renderer budget exceeded ({renderers.Length} / 150).");
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
            Require(lights.Length <= 6, $"Light budget exceeded ({lights.Length} / 6).");
            Require(lights.Count(item => item.enabled && item.gameObject.activeInHierarchy) <= 6,
                "More than six lights are active.");

            var probes = artRoot.GetComponentsInChildren<ReflectionProbe>(true);
            Require(probes.Length == 1 && probes[0].mode == UnityEngine.Rendering.ReflectionProbeMode.Custom &&
                    probes[0].customBakedTexture is Cubemap cubemap && cubemap.width >= 64,
                "The single low-cost custom chrome reflection probe is missing.");

            foreach (var asset in new[]
                     {
                         "Materials/M_UpperWallWarm.mat", "Materials/M_WainscotPanel.mat",
                         "Materials/M_AccentWallCharcoal.mat", "Materials/M_PolishedMicroCement.mat",
                         "Materials/M_AcousticCeiling.mat", "Materials/M_QADashboard.mat",
                         "Textures/T_MicroCement.asset", "Textures/T_RubberDimple.asset"
                     })
                Require(AssetDatabase.LoadMainAssetAtPath(Generated + "/" + asset) != null,
                    "Required art asset is missing: " + asset);

            foreach (var shellName in new[] { "Floor", "Back Wall", "Front Wall", "Left Wall", "Right Wall", "Ceiling" })
                Require(GameObject.Find(shellName)?.GetComponent<BoxCollider>() != null,
                    shellName + " lost its original solid collider.");

            var defects = UnityEngine.Object.FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include);
            Require(defects.Length == 10 && defects.Count(item => item.IsHole) == 6,
                "The authoritative 10-defect set (six holes and four dents) changed.");
            Require(defects.All(item => item.GetComponent<SphereCollider>() is { isTrigger: true }),
                "A defect trigger collider changed.");
            Require(UnityEngine.Object.FindObjectsByType<TrainingStartButton>(FindObjectsInactive.Include).Length == 1 &&
                    UnityEngine.Object.FindObjectsByType<TrainingResetButton>(FindObjectsInactive.Include).Length == 1,
                "BEGIN or RESTART control is missing.");

            var helmetMaterial = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Materials/M_Helmet_Triplanar.mat");
            var helmetShaderPath = helmetMaterial != null
                ? AssetDatabase.GetAssetPath(helmetMaterial.shader)
                : string.Empty;
            Require(helmetMaterial != null &&
                    helmetShaderPath == Root + "/Shaders/TriplanarHelmet.shadergraph" &&
                    helmetMaterial.GetTexture("_Grid_Texture") != null,
                "The two helmets are no longer driven by the UV-independent triplanar material.");
            foreach (var helmetName in new[] { "A1 - REFERENCE HELMET", "A2 - DEFECTIVE SCAN" })
            {
                var helmet = GameObject.Find(helmetName);
                Require(helmet != null && helmet.GetComponentsInChildren<MeshRenderer>(true)
                        .Any(item => item.sharedMaterial == helmetMaterial),
                    helmetName + " lost the shared chrome helmet material.");
            }

            var graphText = File.ReadAllText(Root + "/Shaders/TriplanarHelmet.shadergraph");
            Require(graphText.Contains("0.9900000095367432") && graphText.Contains("0.949999988079071"),
                "Chrome metallic/smoothness values are not present in the helmet Shader Graph.");
            Require(GameObject.Find("Wall Monitor Screen")?.GetComponent<Renderer>()?.sharedMaterial?.name ==
                    "M_QADashboard" ||
                    GameObject.Find("Center Screen - QA Status Dashboard")?.GetComponent<Renderer>()?
                        .sharedMaterial?.mainTexture != null,
                "No authored QA dashboard display is assigned.");
        }

        static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
