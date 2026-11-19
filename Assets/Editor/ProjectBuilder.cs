using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WebARDrawing;

namespace WebARDrawing.EditorTools
{
    /// <summary>One-click project setup + WebGL build. Run from the menu "WebAR" or in batch mode:
    /// Unity -batchmode -quit -projectPath . -executeMethod WebARDrawing.EditorTools.ProjectBuilder.BuildWebGL</summary>
    public static class ProjectBuilder
    {
        const string ScenePath = "Assets/Scenes/Main.unity";
        const string MatPath = "Assets/Materials/SpriteUnlit.mat";

        [MenuItem("WebAR/Create Scene")]
        public static void CreateScene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            Directory.CreateDirectory("Assets/Materials");

            var shader = Shader.Find("Sprites/Default");
            if (shader == null) throw new Exception("Sprites/Default shader not found");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, MatPath); }
            mat.shader = shader;
            EditorUtility.SetDirty(mat);
            AddAlwaysIncludedShader(shader);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGO = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGO.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 5; cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.04f, 0.14f, 0.26f);
            cam.transform.position = new Vector3(0, 0, -10);
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 50;

            var app = new GameObject("ARApp").AddComponent<ARApp>();
            app.spriteMaterial = mat; app.cam = cam;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
        }

        static void AddAlwaysIncludedShader(Shader shader)
        {
            var gs = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(gs);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            for (int i = 0; i < arr.arraySize; i++) if (arr.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
            arr.InsertArrayElementAtIndex(arr.arraySize);
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = shader;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void ConfigureSettings()
        {
            PlayerSettings.companyName = "WebARDrawing";
            PlayerSettings.productName = "Magic Colouring Aquarium";
            PlayerSettings.colorSpace = ColorSpace.Gamma;   // scan colours are shown 1:1 (no gamma shift)
            PlayerSettings.runInBackground = true;
            PlayerSettings.WebGL.template = "PROJECT:ARTemplate";
            // Brotli + JS decompression fallback: smaller download and works on hosts (e.g. GitHub Pages)
            // that cannot send the Content-Encoding header.
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.None;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.initialMemorySize = 64;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Medium);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.WebGL, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            PlayerSettings.defaultWebScreenWidth = 1280;
            PlayerSettings.defaultWebScreenHeight = 720;
        }

        [MenuItem("WebAR/Build WebGL")]
        public static void BuildWebGL()
        {
            ConfigureSettings();
            CreateScene();
            string outDir = "Build/WebGL";
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outDir,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(opts);
            Debug.Log("[WebAR] Build result: " + report.summary.result + " size=" + report.summary.totalSize);
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>Headless check of the scanning pipeline on a folder of test photos (writes debug PNGs).</summary>
        public static void RunScannerTest()
        {
            string dir = "Assets/StreamingAssets";
            string outDir = "Build/ScannerTest";
            Directory.CreateDirectory(outDir);
            int failures = 0;
            foreach (var file in Directory.GetFiles(dir, "test_photo_*.jpg"))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(File.ReadAllBytes(file));
                var px = tex.GetPixels32();
                var quad = new Vector2[4];
                bool ok = DrawingScanner.DetectPaper(px, tex.width, tex.height, 400, quad, out float cov);
                Debug.Log($"[ScannerTest] {Path.GetFileName(file)} {tex.width}x{tex.height} paper={ok} coverage={cov:0.00} quad={string.Join(",", quad)}");
                if (!ok) { failures++; continue; }
                DrawingScanner.PageSizeFor(quad, 720, out int pw, out int ph);
                var page = DrawingScanner.Rectify(px, tex.width, tex.height, quad, pw, ph);
                SavePng(DrawingScanner.MakeTexture(page, pw, ph), Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "_page.png"));
                var cut = DrawingScanner.Extract(page, pw, ph, out string err);
                if (cut == null) { Debug.Log("[ScannerTest] extract failed: " + err); failures++; continue; }
                Debug.Log($"[ScannerTest] cutout {cut.w}x{cut.h} rotated={cut.rotated} rect={cut.fishRect}");
                SavePng(DrawingScanner.MakeTexture(cut.pixels, cut.w, cut.h), Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "_cutout.png"));
            }
            Debug.Log("[ScannerTest] failures=" + failures);
            EditorApplication.Exit(failures == 0 ? 0 : 2);
        }

        static void SavePng(Texture2D t, string path) { File.WriteAllBytes(path, t.EncodeToPNG()); }
    }
}
