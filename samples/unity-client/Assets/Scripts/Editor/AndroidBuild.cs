using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// Builds the Android APK. <c>Unity -batchmode -buildTarget Android -executeMethod Game.Editor.AndroidBuild.Build</c>.
    /// Environment: GAME_SERVER_URL (baked in as the default server), ANDROID_OUT (output .apk path).
    /// </summary>
    public static class AndroidBuild
    {
        private const string DefaultOut = "Builds/Android/TankArena.apk";
        private const string ServerUrlAsset = "Assets/Resources/server-url.txt";

        [MenuItem("Game/Build Android APK")]
        public static void Build()
        {
            var code = 1;
            try
            {
                var report = BuildApk();
                var s = report.summary;
                Debug.Log($"[AndroidBuild] {s.result} size={s.totalSize / (1024 * 1024)} MB time={s.totalTime:mm\\:ss} errors={s.totalErrors} warnings={s.totalWarnings} -> {s.outputPath}");
                code = s.result == BuildResult.Succeeded ? 0 : 1;
            }
            catch (Exception ex)
            {
                Debug.LogError("[AndroidBuild] FAILED " + ex);
            }

            if (Application.isBatchMode)
                EditorApplication.Exit(code);
        }

        private static BuildReport BuildApk()
        {
            var url = Environment.GetEnvironmentVariable("GAME_SERVER_URL");
            var output = Environment.GetEnvironmentVariable("ANDROID_OUT");
            if (string.IsNullOrEmpty(output))
                output = DefaultOut;

            GameBuild.CreateScenes();
            BakeServerUrl(url);
            ApplySettings();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };

            return BuildPipeline.BuildPlayer(options);
        }

        /// <summary>The phone cannot reach "localhost", so the build carries the address of the PC that runs the server.</summary>
        private static void BakeServerUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            Directory.CreateDirectory("Assets/Resources");
            File.WriteAllText(ServerUrlAsset, url.Trim());
            AssetDatabase.Refresh();
        }

        private static void ApplySettings()
        {
            var android = NamedBuildTarget.Android;

            PlayerSettings.companyName = "RelicBit Studio";
            PlayerSettings.productName = "Tank Arena";
            PlayerSettings.SetApplicationIdentifier(android, "com.relicbitstudio.tankarena");
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.Android.bundleVersionCode = 1;

            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            EditorUserBuildSettings.buildAppBundle = false;

            // Optimized Frame Pacing pulls androidx.games:games-frame-pacing from Google's Maven, which some networks cannot reach.
            // The game does not need it, and turning it off keeps the build free of that download.
            PlayerSettings.Android.optimizedFramePacing = false;

            // The dev server speaks plain http/ws; Android blocks that unless it is allowed.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

            // The UI is built for portrait and also fits landscape, so let the phone rotate.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            // Newtonsoft fills the SDK's model classes through reflection, which code stripping would break.
            PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Disabled);
            AssetDatabase.SaveAssets();
        }
    }
}
