using System;
using System.Collections.Generic;
using RemappingTime.Remote;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RemappingTime.EditorTools
{
    /// <summary>
    /// Builds the demo scene from scratch: <b>Remapping Time → Create Demo Scene</b>.
    ///
    /// Generating the scene in code (rather than shipping a hand-edited .unity file) keeps the setup
    /// reviewable and reproducible, and survives Meta SDK upgrades that change prefab internals.
    /// The resulting scene contains:
    /// <list type="bullet">
    /// <item>an OVRCameraRig with passthrough (underlay), per-eye cameras, simultaneous hands + controllers,
    /// and floor-level tracking;</item>
    /// <item>right/left hand skeletons (invisible; used only for fingertip tracking);</item>
    /// <item>the "Remapping Time" object with capture, tracking, input, engine, view, zone, gallery, HUD and
    /// web bridge, all wired up.</item>
    /// </list>
    /// Batch mode: <c>Unity -batchmode -projectPath . -executeMethod RemappingTime.EditorTools.DemoSceneBuilder.BuildFromCommandLine -quit</c>
    /// </summary>
    public static class DemoSceneBuilder
    {
        public const string ScenePath = "Assets/RemappingTime/Scenes/RemappingTimeDemo.unity";

        /// <summary>
        /// Bump whenever the builder wires something new. It is written into the root object's name, so
        /// <see cref="DemoBuild"/> can tell that a scene came from an older builder and regenerate it.
        /// </summary>
        public const int SceneVersion = 6;
        public static string RootName => $"Remapping Time (scene v{SceneVersion})";
        private const string Root = "Assets/RemappingTime";

        [MenuItem("Remapping Time/Create Demo Scene", priority = 0)]
        public static void CreateDemoScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Build();
            EditorUtility.DisplayDialog("Remapping Time", $"Demo scene created at\n{ScenePath}\nand added to Build Settings.", "OK");
        }

        public static void BuildFromCommandLine()
        {
            try
            {
                Build();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        public static void Build()
        {
            EnsureLayer("LeftEye");
            EnsureLayer("RightEye");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ── Camera rig ────────────────────────────────────────────────────
            GameObject rigGo = (GameObject)PrefabUtility.InstantiatePrefab(FindPrefab("OVRCameraRig"));
            rigGo.name = "OVRCameraRig";
            var rig = rigGo.GetComponent<OVRCameraRig>();
            rig.usePerEyeCameras = true;

            OVRManager manager = rigGo.GetComponent<OVRManager>();
            if (manager == null) manager = rigGo.AddComponent<OVRManager>();
            manager.isInsightPassthroughEnabled = true;
            manager.launchSimultaneousHandsControllersOnStartup = true;
            SetInt(manager, "_trackingOriginType", (int)OVRManager.TrackingOrigin.FloorLevel);

            // Passthrough is drawn beneath the eye buffer; the remapped canvas composites over it by alpha.
            var passthrough = rigGo.AddComponent<OVRPassthroughLayer>();
#pragma warning disable CS0618 // Underlay is the only supported mode going forward; set it explicitly for older SDKs.
            passthrough.overlayType = OVROverlay.OverlayType.Underlay;
#pragma warning restore CS0618

            var eyeSetup = rigGo.AddComponent<EyeLayerSetup>();
            SetObject(eyeSetup, "rig", rig);

            foreach (Camera cam in rigGo.GetComponentsInChildren<Camera>(true))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
            }

            Transform trackingSpace = rigGo.transform.Find("TrackingSpace");
            Transform centerEye = rigGo.transform.Find("TrackingSpace/CenterEyeAnchor");
            if (trackingSpace == null || centerEye == null)
                throw new Exception("OVRCameraRig prefab layout changed: TrackingSpace/CenterEyeAnchor not found.");

            // ── Hands (tracking only, no visuals) ─────────────────────────────
            GameObject handPrefab = FindPrefab("OVRHandPrefab");
            (OVRHand rightHand, OVRSkeleton rightSkeleton) = CreateHand(handPrefab, trackingSpace, right: true);
            (OVRHand leftHand, OVRSkeleton leftSkeleton) = CreateHand(handPrefab, trackingSpace, right: false);

            // ── Remapping Time ────────────────────────────────────────────────
            Shader frameShader = LoadShader("ReprojectedFrame");
            Shader uiShader = LoadShader("UnlitTransparent");
            Shader waterShader = LoadShader("WaterVolume");

            var root = new GameObject(RootName);

            var capture = root.AddComponent<StereoPassthroughCapture>();
            SetObject(capture, "trackingSpace", trackingSpace);

            var tracking = root.AddComponent<TrackedPoint>();
            SetObject(tracking, "rig", rig);
            SetObject(tracking, "rightHand", rightHand);
            SetObject(tracking, "rightSkeleton", rightSkeleton);
            SetObject(tracking, "leftHand", leftHand);
            SetObject(tracking, "leftSkeleton", leftSkeleton);
            SetObject(tracking, "markerShader", uiShader);

            var input = root.AddComponent<InputHub>();
            var engine = root.AddComponent<TimeRemapEngine>();

            var view = Child(root, "Remapped View").AddComponent<ReprojectedFrameView>();
            SetObject(view, "capture", capture);
            SetObject(view, "tracking", tracking);
            SetObject(view, "head", centerEye);
            SetObject(view, "frameShader", frameShader);

            var zone = Child(root, "Zone").AddComponent<ZoneVolume>();
            SetObject(zone, "shader", waterShader);
            SetObject(zone, "capture", capture);

            var gallery = Child(root, "Timeline Gallery").AddComponent<TimelineGallery>();
            SetObject(gallery, "capture", capture);
            SetObject(gallery, "frameShader", frameShader);

            var hud = Child(root, "HUD").AddComponent<HeadsetHud>();
            SetObject(hud, "engine", engine);
            SetObject(hud, "head", centerEye);
            SetObject(hud, "uiShader", uiShader);

            // Depth API: the manager produces depth maps; the probe reports on the web page whether they arrive.
            GameObject depthGo = Child(root, "Depth (probe)");
            var depthManager = depthGo.AddComponent<Meta.XR.EnvironmentDepth.EnvironmentDepthManager>();
            var raycaster = depthGo.AddComponent<Meta.XR.EnvironmentRaycastManager>();
            var probe = depthGo.AddComponent<DepthProbe>();
            SetObject(probe, "engine", engine);
            SetObject(probe, "depthManager", depthManager);
            SetObject(probe, "raycaster", raycaster);
            SetObject(probe, "head", centerEye);

            var bridge = root.AddComponent<RemoteControlBridge>();
            SetObject(bridge, "engine", engine);
            SetObject(bridge, "remotePage", AssetDatabase.LoadAssetAtPath<TextAsset>($"{Root}/Resources/RemappingTime/remote.html"));
            SetObject(bridge, "operatorPage", AssetDatabase.LoadAssetAtPath<TextAsset>($"{Root}/Resources/RemappingTime/operator.html"));

            SetObject(engine, "capture", capture);
            SetObject(engine, "tracking", tracking);
            SetObject(engine, "input", input);
            SetObject(engine, "view", view);
            SetObject(engine, "zone", zone);
            SetObject(engine, "gallery", gallery);
            SetObject(engine, "head", centerEye);

            // ── Save and register ─────────────────────────────────────────────
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScenePath));
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new Exception($"Could not save {ScenePath}");

            var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(ScenePath, true) };
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
                if (s.path != ScenePath) scenes.Add(s);
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log($"[RemappingTime] Demo scene written to {ScenePath}.");
        }

        private static (OVRHand, OVRSkeleton) CreateHand(GameObject prefab, Transform parent, bool right)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = right ? "Right Hand (tracking)" : "Left Hand (tracking)";
            int side = right ? 1 : 0; // OVRHand.Hand / OVRSkeleton.SkeletonType / OVRMesh.MeshType: HandLeft = 0, HandRight = 1

            var hand = go.GetComponent<OVRHand>();
            var skeleton = go.GetComponent<OVRSkeleton>();
            if (hand == null || skeleton == null) throw new Exception("OVRHandPrefab is missing OVRHand/OVRSkeleton.");
            SetInt(hand, "HandType", side);
            SetInt(skeleton, "_skeletonType", side);
            // The skeleton positions itself from the tracked root pose, relative to its parent (TrackingSpace).
            SetBool(skeleton, "_updateRootPose", true);
            var mesh = go.GetComponent<OVRMesh>();
            if (mesh != null) SetInt(mesh, "_meshType", side);

            // We only need joint positions, so hide the hand visuals. (Seeing a real-time virtual hand on top
            // of remapped passthrough would give the effect away; turn the marker on in Tracking settings
            // if you want to show the real-time position.)
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb is OVRMeshRenderer || mb is OVRSkeletonRenderer) mb.enabled = false;

            return (hand, skeleton);
        }

        private static GameObject Child(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static GameObject FindPrefab(string name)
        {
            foreach (string guid in AssetDatabase.FindAssets($"{name} t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith($"/Prefabs/{name}.prefab", StringComparison.Ordinal))
                    return AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            throw new Exception($"Prefab '{name}' not found. Is the Meta XR Core SDK installed?");
        }

        private static Shader LoadShader(string name)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>($"{Root}/Shaders/{name}.shader");
            if (shader == null) throw new Exception($"Shader {name} not found under {Root}/Shaders.");
            return shader;
        }

        private static void EnsureLayer(string layerName)
        {
            if (LayerMask.NameToLayer(layerName) >= 0) return;
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            for (int i = 8; i < layers.arraySize; i++)
            {
                SerializedProperty slot = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(slot.stringValue))
                {
                    slot.stringValue = layerName;
                    tagManager.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log($"[RemappingTime] Added layer '{layerName}' at index {i}.");
                    return;
                }
            }
            throw new Exception($"No free layer slot for '{layerName}'.");
        }

        // ── SerializedObject helpers (also reach private [SerializeField] fields) ──

        private static SerializedProperty Prop(SerializedObject so, UnityEngine.Object target, string name)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p == null) throw new Exception($"{target.GetType().Name} has no serialized field '{name}'.");
            return p;
        }

        private static void SetObject(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var so = new SerializedObject(target);
            Prop(so, target, field).objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetInt(UnityEngine.Object target, string field, int value)
        {
            var so = new SerializedObject(target);
            Prop(so, target, field).intValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetBool(UnityEngine.Object target, string field, bool value)
        {
            var so = new SerializedObject(target);
            Prop(so, target, field).boolValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
