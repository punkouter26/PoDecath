using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// The Android build, as code rather than as a set of inspector fields somebody has to be told about.
    ///
    /// Everything here is a setting that has to be right for this particular game, and every one of them
    /// was previously either a template default or something you would only discover was wrong by looking
    /// at a phone. The application identifier is the clearest example: it shipped as
    /// <c>com.UnityTechnologies.com.unity.template.urpblank</c>, which is not merely untidy — it is the
    /// identifier of whatever other blank URP project was last installed on the device, so two of them
    /// cannot be on the same phone at once and one silently replaces the other.
    ///
    /// The settings are applied on every build rather than saved once into ProjectSettings.asset, because
    /// a build machine that has to be set up by hand before it can produce a correct build is a build
    /// machine that will one day produce an incorrect one. Running this is the whole configuration.
    ///
    /// From the command line:
    ///
    ///     Unity.exe -quit -batchmode -projectPath . \
    ///               -executeMethod PoDecath.EditorTools.AndroidBuild.BuildFromCommandLine \
    ///               -apkPath Build/Android/PoDecath.apk [-devBuild]
    /// </summary>
    public static class AndroidBuild
    {
        const string DefaultApk = "Build/Android/PoDecath.apk";
        const string ApplicationId = "com.podecath.game";
        const string Company = "PoDecath";

        // The menu builds bump the version themselves. From the command line that is the configure pass's
        // job, but there is no configure pass here — and an APK whose version code matches the last one is
        // an APK nobody can tell apart from it on the device.
        [MenuItem("PoDecath/Build Android APK", priority = 20)]
        public static void BuildFromMenu()
        {
            ConfigurePlayer(development: false, bumpVersion: true);
            BuildReport report = Build(DefaultApk, development: false);
            if (report != null && report.summary.result == BuildResult.Succeeded)
                EditorUtility.RevealInFinder(Path.GetFullPath(DefaultApk));
        }

        [MenuItem("PoDecath/Build Android APK (development)", priority = 21)]
        public static void BuildDevelopmentFromMenu()
        {
            ConfigurePlayer(development: true, bumpVersion: true);
            Build(DefaultApk, development: true);
        }

        /// <summary>
        /// The -executeMethod entry point. Exits the editor with a non-zero code on any failure, because a
        /// batchmode build that logs an error and exits 0 is a build the deploy script will happily go on
        /// to install — over the top of the last one that worked.
        /// </summary>
        public static void BuildFromCommandLine()
        {
            string apk = DefaultApk;
            bool development = false;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-apkPath" && i + 1 < args.Length) apk = args[i + 1];
                if (args[i] == "-devBuild") development = true;
            }

            BuildReport report = Build(apk, development);
            bool ok = report != null && report.summary.result == BuildResult.Succeeded;
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Applies the player settings and the version bump, then exits — the pass that has to happen in
        /// its own Unity invocation, before the one that builds.
        ///
        /// This is not tidiness. Several of these settings decide how the managed assemblies are
        /// *compiled*: the API compatibility level above all, and with it the scripting backend and the
        /// stripping level. Changing one of them dirties every assembly in the project, and Unity
        /// recompiles them on the next domain reload — which, in a batchmode run that goes straight from
        /// setting them to <see cref="BuildPipeline.BuildPlayer"/>, never comes. The build then serialises
        /// the scenes against the editor's old assemblies and hands the player the new ones, and the whole
        /// thing dies on
        ///
        ///     Error building player because script class layout is incompatible between the editor and
        ///     the player
        ///
        /// which names neither the setting that caused it nor the fact that a setting caused it at all.
        /// This project lost a full IL2CPP build to exactly that, having changed nothing but the API
        /// level. Applying the settings, exiting, and letting the next invocation start up already
        /// compiled against them makes the failure structurally impossible rather than merely unlikely.
        /// </summary>
        public static void ConfigureFromCommandLine()
        {
            try
            {
                bool development = Array.IndexOf(Environment.GetCommandLineArgs(), "-devBuild") >= 0;
                ConfigurePlayer(development, bumpVersion: true);
                Debug.Log($"[PoDecath] Player settings applied: {ApplicationId} "
                        + $"v{PlayerSettings.bundleVersion} (code {PlayerSettings.Android.bundleVersionCode}).");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PoDecath] Applying the player settings failed: {e}");
                EditorApplication.Exit(1);
            }
        }

        // ---------------------------------------------------------------- scenes

        /// <summary>
        /// Regenerates every scene that ships, then leaves the project ready to build.
        ///
        /// This exists because the scenes are generated assets - the house rule is to re-run the builders
        /// rather than hand-edit the YAML - and anything added to a builder is therefore not in any scene
        /// until they are re-run. Shipping an APK built from stale scenes is the exact failure this
        /// prevents: the code is all there, the build succeeds, and the phone shows the old game.
        ///
        /// The order matters. The two field builders each rebuild the setup menu when they finish, so the
        /// last one to run decides which events MAIN offers; long jump goes last because it is the builder
        /// that leaves the menu offering both.
        /// </summary>
        public static void RebuildScenesFromCommandLine()
        {
            try
            {
                Debug.Log("[PoDecath] Rebuilding every shipped scene.");
                PoDecathSceneBuilder.BuildAll();          // MainMenu, layers, physics, policy library
                RooftopSceneBuilder.Build();              // Rooftop      - the dev straight
                RooftopSceneBuilder.BuildLap();           // RooftopLap   - one runner, one loop
                RooftopSceneBuilder.BuildRace();          // RooftopRace  + MAIN
                RooftopSceneBuilder.BuildLongJump();      // RooftopLongJump + MAIN, offering both events
                AssetDatabase.SaveAssets();
                Debug.Log("[PoDecath] All scenes rebuilt.");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PoDecath] Scene rebuild failed: {e}");
                EditorApplication.Exit(1);
            }
        }

        // ---------------------------------------------------------------- the build

        public static BuildReport Build(string apkPath, bool development)
        {
            // Re-applied rather than assumed, so a build from the menu is configured too. It does not bump
            // the version: the deploy script's configure pass has already done that, and a build that
            // bumped again would put a different number in the manifest than the one that pass logged.
            ConfigurePlayer(development, bumpVersion: false);

            string[] scenes = EnabledScenes();
            if (scenes.Length == 0)
            {
                Debug.LogError("[PoDecath] No enabled scenes in the build settings; run PoDecath/Build Everything first.");
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(apkPath)));

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = apkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = development
                    ? BuildOptions.Development | BuildOptions.AllowDebugging | BuildOptions.ConnectWithProfiler
                    : BuildOptions.None,
            };

            Debug.Log($"[PoDecath] Building {(development ? "development" : "release")} APK -> {apkPath}\n"
                    + $"  scenes: {string.Join(", ", scenes)}\n"
                    + $"  id: {ApplicationId}  version: {PlayerSettings.bundleVersion} ({PlayerSettings.Android.bundleVersionCode})");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
                Debug.Log($"[PoDecath] Build succeeded: {summary.outputPath}, "
                        + $"{summary.totalSize / (1024f * 1024f):F1} MB in {summary.totalTime.TotalSeconds:F0} s.");
            else
                Debug.LogError($"[PoDecath] Build {summary.result}: {summary.totalErrors} error(s).");

            return report;
        }

        static string[] EnabledScenes()
        {
            var paths = new List<string>();
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
                if (s.enabled && !string.IsNullOrEmpty(s.path)) paths.Add(s.path);
            return paths.ToArray();
        }

        // ---------------------------------------------------------------- the settings

        /// <summary>
        /// Every player setting this game needs on Android, with the reason for each where the reason is
        /// not obvious. Grouped by what the setting is actually about rather than by which inspector tab
        /// Unity happens to put it on.
        /// </summary>
        static void ConfigurePlayer(bool development, bool bumpVersion)
        {
            var android = NamedBuildTarget.Android;

            // ---- identity
            //
            // The version code goes into the version string as well as into the manifest, so the number in
            // the bottom-right corner of every screenshot identifies exactly one build. A screenshot
            // labelled "v0.1.0" when there have been forty builds of 0.1.0 is not evidence of anything,
            // which was the entire point of putting a version in the frame.
            PlayerSettings.companyName = Company;
            PlayerSettings.productName = "PoDecath";
            PlayerSettings.SetApplicationIdentifier(android, ApplicationId);
            if (bumpVersion) PlayerSettings.Android.bundleVersionCode++;
            PlayerSettings.bundleVersion = $"0.1.{PlayerSettings.Android.bundleVersionCode}";

            // ---- orientation and the screen
            //
            // Portrait only, and drawing under the cutout rather than being letterboxed away from it. The
            // safe area is then handled per-screen by UiRoot, which pads the 'safe' element in by
            // Screen.safeArea - so the game gets the whole tall screen and the text still clears the
            // camera hole and the gesture bar.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.useAnimatedAutorotation = false;
            PlayerSettings.Android.renderOutsideSafeArea = true;
            PlayerSettings.Android.startInFullscreen = true;
            PlayerSettings.Android.androidIsGame = true;
            PlayerSettings.Android.fullscreenMode = FullScreenMode.FullScreenWindow;

            // ---- code generation
            //
            // IL2CPP ARM64 is the only combination Google Play accepts and the only one worth measuring
            // performance on. Stripping is deliberately Low rather than the Medium default: the Inference
            // Engine resolves parts of its backend by reflection, and a stripped ONNX backend fails at the
            // first inference on the device rather than at build time. The megabytes are not worth that
            // failure mode; revisit only with a device build to test it against.
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(android, Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Low);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // The API compatibility level is pinned to NET_Standard, and this line is load-bearing.
            //
            // Setting it to NET_Unity_4_8 also moves editorAssembliesCompatibilityLevel, which changes how
            // the *editor's* assemblies are compiled. glTFast's Burst jobs are unsafe structs holding raw
            // pointers, and under the two levels Unity disagrees with itself about whether those pointers
            // are serialized fields: the editor saw MemCopyJob as one long, the player saw it as one long
            // and two void*. The build then died with 148 of
            //
            //     Type '[glTFast]GLTFast.Jobs.MemCopyJob' has an extra field 'result' of type
            //     'System.Void*' in the player and thus can't be serialized
            //
            // under the heading "script class layout is incompatible between the editor and the player",
            // which reads like a stale-assembly problem and is not one - no amount of recompiling or
            // splitting the build into passes fixes it, because the two sides are compiled correctly and
            // to different rules. NET_Standard, the project default, is also the right answer for a mobile
            // build: it is the smaller surface. It is set rather than merely left alone so that a project
            // already carrying the bad value is corrected by running this, rather than staying broken until
            // somebody finds the setting by hand. Leave it unless something genuinely needs 4.8, and if it
            // ever does, expect to deal with glTFast.
            PlayerSettings.SetApiCompatibilityLevel(android, ApiCompatibilityLevel.NET_Standard);
            PlayerSettings.stripEngineCode = true;

            // ---- SDK levels
            //
            // 26 is the floor the project already had; Auto tracks whatever the installed build tools
            // support, which is what Play requires and what a device this new wants.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;

            // ---- graphics
            //
            // Vulkan first with GLES3 behind it. This project is a physics scene with a few hundred
            // batches, and Vulkan's lower driver overhead is worth more here than anywhere else in it;
            // GLES3 stays as the fallback for a device whose Vulkan driver is not trustworthy.
            //
            // optimizedFramePacing is the setting that matters most for how the game feels: without it a
            // 60 FPS target on a 120 Hz panel judders, because the frames are produced evenly and
            // presented unevenly. It costs nothing and it is off by default.
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
            {
                GraphicsDeviceType.Vulkan,
                GraphicsDeviceType.OpenGLES3,
            });
            PlayerSettings.Android.optimizedFramePacing = true;
            PlayerSettings.SetMobileMTRendering(android, true);
            PlayerSettings.gpuSkinning = true;
            PlayerSettings.Android.maxAspectRatio = 2.4f;   // a 20:9 phone, not letterboxed to 16:9

            // ---- what the game does not use
            //
            // The accelerometer is polled every frame at 60 Hz by default and this game never reads it.
            PlayerSettings.accelerometerFrequency = 0;
            PlayerSettings.muteOtherAudioSources = false;

            // ---- packaging
            //
            // An APK, not an app bundle: this goes onto a phone over adb, and adb cannot install an .aab.
            // The debug keystore is deliberate for the same reason - a signing key belongs in a release
            // pipeline, not in a script that runs after every training run.
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.androidCreateSymbols = development ? AndroidCreateSymbols.Debugging
                                                                       : AndroidCreateSymbols.Disabled;
            PlayerSettings.Android.useCustomKeystore = false;
            PlayerSettings.Android.buildApkPerCpuArchitecture = false;
            PlayerSettings.Android.preferredInstallLocation = AndroidPreferredInstallLocation.Auto;

            AssetDatabase.SaveAssets();
        }
    }
}
