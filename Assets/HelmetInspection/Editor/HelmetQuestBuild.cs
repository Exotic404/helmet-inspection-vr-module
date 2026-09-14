using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HelmetInspection.Editor
{
    public static class HelmetQuestBuild
    {
        const string ApkPath = "Builds/HelmetInspectionModule1.apk";
        const string AabPath = "Builds/HelmetInspectionModule1.aab";

        [MenuItem("Helmet Inspection/Build Quest Development APK")]
        public static void BuildDevelopmentApk()
        {
            // A clean player build prevents stale Bee/Gradle scene data from surviving
            // interrupted Quest builds, which can otherwise crash Unity while loading level0.
            Build(ApkPath, false, BuildOptions.Development | BuildOptions.CleanBuildCache);
        }

        // Intentionally skips the module's strict exactly-ten-defects validator so a
        // manually authored experimental hotspot (for example D11) can be device-tested.
        [MenuItem("Helmet Inspection/Build Quest Development APK (Unchecked)")]
        public static void BuildDevelopmentApkUnchecked()
        {
            Build(ApkPath, false, BuildOptions.Development, false);
        }

        [MenuItem("Helmet Inspection/Build Quest Store AAB")]
        public static void BuildStoreAab()
        {
            Build(AabPath, true, BuildOptions.None);
        }

        static void Build(string outputPath, bool appBundle, BuildOptions options, bool validate = true)
        {
            RepairStaleSimulationTemp();
            if (validate)
                HelmetProjectValidator.ValidateProject();
            Directory.CreateDirectory("Builds");
            EditorUserBuildSettings.buildAppBundle = appBundle;
            var scenes = Array.FindAll(EditorBuildSettings.scenes, scene => scene.enabled);
            var paths = Array.ConvertAll(scenes, scene => scene.path);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = paths,
                locationPathName = outputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = options
            });
            var summary = report.summary;
            Debug.Log($"[HelmetBuild] {summary.result}: {summary.totalSize} bytes at {Path.GetFullPath(outputPath)}");
            if (summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Android build failed with {summary.totalErrors} errors.");
        }

        static void RepairStaleSimulationTemp()
        {
            const string tempRoot = "Assets/XR/Temp";
            var moves = new[]
            {
                (temp: tempRoot + "/XRSimulationPreferences.asset",
                 source: "Assets/XR/UserSimulationSettings/Resources/XRSimulationPreferences.asset"),
                (temp: tempRoot + "/XRSimulationRuntimeSettings.asset",
                 source: "Assets/XR/Resources/XRSimulationRuntimeSettings.asset")
            };

            foreach (var (temp, source) in moves)
            {
                if (AssetDatabase.LoadMainAssetAtPath(temp) == null)
                    continue;

                if (AssetDatabase.LoadMainAssetAtPath(source) == null)
                {
                    var error = AssetDatabase.MoveAsset(temp, source);
                    if (!string.IsNullOrEmpty(error))
                        throw new InvalidOperationException($"Failed to recover {temp}: {error}");
                }
                else if (!AssetDatabase.DeleteAsset(temp))
                {
                    throw new InvalidOperationException($"Failed to remove stale XR Simulation build asset: {temp}");
                }
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
