using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering;
using UnityEngine.Playables;
using nadena.dev.modular_avatar.core;
using Object = UnityEngine.Object;

namespace Kura.ActionInstaller
{
    public static class ActionThumbnails
    {
        public static string LastPoseCheck;
        // Copy only render data and transforms. Avatar scripts, contacts and audio never
        // execute in the preview, and the source avatar's pose remains untouched.
        static Transform CopyTransforms(Transform source, Transform parent, Dictionary<Transform, Transform> map)
        {
            var go = new GameObject(source.name);
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = source.localPosition;
            t.localRotation = source.localRotation;
            t.localScale = source.localScale;
            go.SetActive(source.gameObject.activeSelf);
            map[source] = t;
            foreach (Transform child in source) CopyTransforms(child, t, map);
            return t;
        }

        // PreviewRenderUtility has its own two directional lights and knows nothing
        // about the scene, which is why the old thumbs looked darker than the lit
        // scene. Mirror the scene's lights into the preview instead, at their real
        // world positions, and keep the scene ambient. Only fall back to a neutral
        // studio when the scene has no lights at all.
        static void ApplySceneLighting(PreviewRenderUtility preview)
        {
            Color ambient;
            switch (RenderSettings.ambientMode)
            {
                case AmbientMode.Trilight:
                    ambient = (RenderSettings.ambientSkyColor + RenderSettings.ambientEquatorColor + RenderSettings.ambientGroundColor) / 3f;
                    break;
                case AmbientMode.Flat:
                    ambient = RenderSettings.ambientLight;
                    break;
                default:
                    ambient = RenderSettings.ambientSkyColor;
                    break;
            }
            ambient *= Mathf.Max(RenderSettings.ambientIntensity, 1f);
            const float ambientFloor = .42f;
            preview.ambientColor = new Color(
                Mathf.Max(ambient.r, ambientFloor),
                Mathf.Max(ambient.g, ambientFloor),
                Mathf.Max(ambient.b, ambientFloor));

            int copied = 0;
            foreach (var source in Object.FindObjectsOfType<Light>())
            {
                if (source == null || !source.enabled || !source.gameObject.activeInHierarchy) continue;
                if (source.type == LightType.Area) continue;
                if (copied >= 8) break;
                var go = new GameObject("KuraPreviewLight");
                go.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                var light = go.AddComponent<Light>();
                EditorUtility.CopySerialized(source, light);
                light.color = Color.Lerp(light.color, Color.white, .35f);
                preview.AddSingleGO(go);
                copied++;
            }

            preview.lights[0].intensity = 0f;
            preview.lights[1].intensity = 0f;
            if (copied > 0)
            {
                // A single scene light gives a very hard, one-sided read on a transparent
                // icon; add a weak front fill without changing the key direction.
                if (copied == 1)
                {
                    preview.lights[1].color = Color.white;
                    preview.lights[1].intensity = .35f;
                    preview.lights[1].transform.rotation = Quaternion.Euler(330, 20, 0);
                }
                return;
            }

            preview.lights[0].color = Color.white;
            preview.lights[0].intensity = 1.4f;
            preview.lights[0].transform.rotation = Quaternion.Euler(35, 155, 0);
            preview.lights[1].color = Color.white;
            preview.lights[1].intensity = .65f;
            preview.lights[1].transform.rotation = Quaternion.Euler(330, 20, 0);
        }

        public static Texture2D Capture(GameObject avatar, AnimationClip clip, float time)
        {
            if (avatar == null || clip == null) throw new InvalidOperationException("请先选择角色和动作。");
            var sourceAnimator = avatar.GetComponent<Animator>();
            if (sourceAnimator == null || !sourceAnimator.isHuman) throw new InvalidOperationException("缩略图拍摄需要 Humanoid 角色。");
            var preview = new PreviewRenderUtility();
            GameObject clone = null;
            Texture2D result = null;
            var graph = default(PlayableGraph);
            try
            {
                var map = new Dictionary<Transform, Transform>();
                clone = CopyTransforms(avatar.transform, null, map).gameObject;
                preview.AddSingleGO(clone);
                // Keep the avatar where it stands so the scene's lights hit it from
                // the same angles as in the editor view.
                clone.transform.SetPositionAndRotation(avatar.transform.position, avatar.transform.rotation);
                clone.transform.localScale = avatar.transform.lossyScale;
                clone.SetActive(true);
                foreach (var src in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var dst = map[src.transform].gameObject.AddComponent<SkinnedMeshRenderer>();
                    EditorUtility.CopySerialized(src, dst);
                    dst.bones = src.bones.Select(b => b != null && map.ContainsKey(b) ? map[b] : null).ToArray();
                    dst.rootBone = src.rootBone != null && map.ContainsKey(src.rootBone) ? map[src.rootBone] : null;
                    dst.probeAnchor = null;
                    dst.updateWhenOffscreen = true;
                }
                foreach (var src in avatar.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var filter = src.GetComponent<MeshFilter>();
                    if (filter == null) continue;
                    map[src.transform].gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    var dst = map[src.transform].gameObject.AddComponent<MeshRenderer>();
                    EditorUtility.CopySerialized(src, dst);
                    dst.probeAnchor = null;
                }
                var animator = clone.AddComponent<Animator>();
                animator.avatar = sourceAnimator.avatar;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                var followers = new List<Action>();
                foreach (var merge in avatar.GetComponentsInChildren<ModularAvatarMergeArmature>(true))
                {
                    if (!merge.enabled) continue;
                    var pairs = merge.GetBonesMapping();
                    if (pairs == null) continue;
                    foreach (var pair in pairs.OrderBy(pair => AnimationUtility.CalculateTransformPath(pair.Item2, avatar.transform).Count(c => c == '/')))
                    {
                        if (!map.ContainsKey(pair.Item1) || !map.ContainsKey(pair.Item2)) continue;
                        var baseBone = map[pair.Item1]; var clothingBone = map[pair.Item2];
                        var inverseBase = baseBone.worldToLocalMatrix;
                        var originalPosition = clothingBone.position;
                        var rotationOffset = Quaternion.Inverse(baseBone.rotation) * clothingBone.rotation;
                        followers.Add(() =>
                        {
                            clothingBone.position = baseBone.localToWorldMatrix.MultiplyPoint3x4(inverseBase.MultiplyPoint3x4(originalPosition));
                            clothingBone.rotation = baseBone.rotation * rotationOffset;
                        });
                    }
                }
                foreach (var proxy in avatar.GetComponentsInChildren<ModularAvatarBoneProxy>(true))
                {
                    if (!proxy.enabled || proxy.target == null || !map.ContainsKey(proxy.target)) continue;
                    var t = map[proxy.transform];
                    t.SetParent(map[proxy.target], true);
                    if (proxy.attachmentMode == BoneProxyAttachmentMode.AsChildAtRoot || proxy.attachmentMode == BoneProxyAttachmentMode.Unset)
                    { t.localPosition = Vector3.zero; t.localRotation = Quaternion.identity; }
                    else if (proxy.attachmentMode == BoneProxyAttachmentMode.AsChildKeepPosition) t.localRotation = Quaternion.identity;
                    else if (proxy.attachmentMode == BoneProxyAttachmentMode.AsChildKeepRotation) t.localPosition = Vector3.zero;
                }
                var arm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                var armBefore = arm != null ? arm.localRotation : Quaternion.identity;
                graph = PlayableGraph.Create("KuraThumbnailPose");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(false);
                var output = AnimationPlayableOutput.Create(graph, "Pose", animator);
                output.SetSourcePlayable(playable);
                playable.SetTime(Mathf.Clamp(time, 0, clip.length));
                graph.Play();
                graph.Evaluate(1f / 60);
                playable.SetTime(Mathf.Clamp(time, 0, clip.length));
                graph.Evaluate(0);
                foreach (var follow in followers) follow();
                LastPoseCheck = "initialized=" + animator.isInitialized + ", arm=" + (arm == null ? "missing" : arm.name) + ", rotationChange=" + (arm == null ? 0 : Quaternion.Angle(armBefore, arm.localRotation));
                var bounds = new Bounds();
                bool hasBounds = false;
                foreach (var renderer in clone.GetComponentsInChildren<Renderer>())
                {
                    if (!renderer.enabled) continue;
                    Bounds current;
                    var skin = renderer as SkinnedMeshRenderer;
                    if (skin != null && skin.sharedMesh != null)
                    {
                        var mesh = new Mesh();
                        try
                        {
                            skin.BakeMesh(mesh);
                            var vertices = mesh.vertices;
                            if (vertices.Length == 0) continue;
                            current = new Bounds(skin.transform.TransformPoint(vertices[0]), Vector3.zero);
                            foreach (var vertex in vertices) current.Encapsulate(skin.transform.TransformPoint(vertex));
                        }
                        finally { Object.DestroyImmediate(mesh); }
                    }
                    else current = renderer.bounds;
                    if (!hasBounds) { bounds = current; hasBounds = true; } else bounds.Encapsulate(current);
                }
                if (!hasBounds) throw new InvalidOperationException("角色没有可见的网格可供拍摄。");
                float size = Mathf.Max(.1f, Mathf.Max(bounds.size.x, bounds.size.y)) * .58f;
                var camera = preview.camera;
                camera.orthographic = true;
                camera.orthographicSize = size;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                float distance = bounds.size.magnitude + 2;
                camera.transform.position = bounds.center + Vector3.forward * distance;
                camera.transform.LookAt(bounds.center);
                camera.nearClipPlane = .01f;
                camera.farClipPlane = distance * 3;
                ApplySceneLighting(preview);
                const int outputSize = 256;
                int renderSize = outputSize * 2;
                preview.BeginPreview(new Rect(0, 0, renderSize, renderSize), GUIStyle.none);
                preview.Render(true);
                var rendered = (RenderTexture)preview.EndPreview();
                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = rendered;
                    int width = Mathf.Max(outputSize, rendered.width);
                    int height = Mathf.Max(outputSize, rendered.height);
                    result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    result.Apply();
                }
                finally { RenderTexture.active = previous; }
                var centered = CenterVisiblePixels(result, outputSize);
                BleedEdges(centered);
                Object.DestroyImmediate(result);
                return centered;
            }
            finally { if (graph.IsValid()) graph.Destroy(); preview.Cleanup(); }
        }

        // Transparent pixels default to black RGB; Unity scales the icon at draw time
        // and the filter then pulls that black into the silhouette, which reads as a
        // dark, smudged edge. Spread the nearby colour a few pixels outward instead.
        static void BleedEdges(Texture2D texture, int passes = 2)
        {
            int width = texture.width, height = texture.height;
            var colors = texture.GetPixels();
            var valid = new bool[colors.Length];
            for (int i = 0; i < colors.Length; i++) valid[i] = colors[i].a > 0f;
            for (int pass = 0; pass < passes; pass++)
            {
                var nextColors = (Color[])colors.Clone();
                var nextValid = (bool[])valid.Clone();
                bool changed = false;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * width + x;
                        if (valid[i]) continue;
                        Color sum = Color.clear;
                        int count = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                                int j = ny * width + nx;
                                if (!valid[j]) continue;
                                sum += colors[j];
                                count++;
                            }
                        if (count == 0) continue;
                        nextColors[i] = new Color(sum.r / count, sum.g / count, sum.b / count, 0f);
                        nextValid[i] = true;
                        changed = true;
                    }
                colors = nextColors;
                valid = nextValid;
                if (!changed) break;
            }
            texture.SetPixels(colors);
            texture.Apply();
        }

        // Skinned mesh bounds include invisible vertices and accessories. Frame
        // the pixels actually rendered instead, so asymmetric poses stay centered.
        public static Texture2D CenterVisiblePixels(Texture2D source)
        {
            return CenterVisiblePixels(source, 256);
        }

        // `source` is rendered at 2x and averaged down here, which removes the jaggies
        // without making the saved PNG any bigger. Samples are alpha-premultiplied so
        // transparent edge pixels do not darken the outline.
        public static Texture2D CenterVisiblePixels(Texture2D source, int size)
        {
            var pixels = source.GetPixels32();
            int minX = source.width, minY = source.height, maxX = -1, maxY = -1;
            for (int y = 0; y < source.height; y++)
                for (int x = 0; x < source.width; x++)
                    if (pixels[y * source.width + x].a > 8)
                    { minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x); minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y); }
            var result = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var output = new Color[size * size];
            if (maxX >= 0)
            {
                float extent = Mathf.Max(maxX - minX + 1, maxY - minY + 1) / .88f;
                float centerX = (minX + maxX + 1) * .5f, centerY = (minY + maxY + 1) * .5f;
                float step = extent / size;
                float[] offsets = { -.25f, .25f };
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float sx = centerX + ((x + .5f) / size - .5f) * extent;
                        float sy = centerY + ((y + .5f) / size - .5f) * extent;
                        Color sum = Color.clear;
                        int count = 0;
                        for (int oy = 0; oy < 2; oy++)
                            for (int ox = 0; ox < 2; ox++)
                            {
                                float px = sx + offsets[ox] * step, py = sy + offsets[oy] * step;
                                if (px < 0 || py < 0 || px >= source.width || py >= source.height) continue;
                                var c = source.GetPixelBilinear(px / source.width, py / source.height);
                                sum += new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a);
                                count++;
                            }
                        if (count == 0) continue;
                        sum /= count;
                        output[y * size + x] = sum.a > 0.0001f
                            ? new Color(sum.r / sum.a, sum.g / sum.a, sum.b / sum.a, sum.a)
                            : Color.clear;
                    }
            }
            result.SetPixels(output); result.Apply(); return result;
        }

        public static void Generate(ActionInstallProfile profile, GameObject avatar, IEnumerable<ActionEntry> entries, bool missingOnly, bool respectAuto = true)
        {
            var selected = entries.Where(e => (!respectAuto || e.autoThumbnail) && e.animation != null && (!missingOnly || e.thumbnail == null)).ToArray();
            if (selected.Length == 0) return;
            if (avatar == null) throw new InvalidOperationException("请先拖入要拍摄的角色。");
            string folder = KuraPaths.Ensure("Thumbnails");
            Undo.RecordObject(profile, "拍摄动作缩略图");
            try
            {
                for (int i = 0; i < selected.Length; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("动作管理器 · 拍摄缩略图", selected[i].name, (float)i / selected.Length)) break;
                    var entry = selected[i];
                    var texture = Capture(avatar, entry.animation, entry.thumbnailTime);
                    string path = folder + "/" + Guid.NewGuid().ToString("N") + ".png";
                    try { File.WriteAllBytes(path, texture.EncodeToPNG()); }
                    finally { Object.DestroyImmediate(texture); }
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    importer.textureType = TextureImporterType.Default;
                    importer.maxTextureSize = 256;
                    importer.mipmapEnabled = false;
                    importer.alphaSource = TextureImporterAlphaSource.FromInput;
                    importer.alphaIsTransparency = true;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.SaveAndReimport();
                    entry.thumbnail = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    EditorUtility.SetDirty(profile);
                }
                AssetDatabase.SaveAssets();
            }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
