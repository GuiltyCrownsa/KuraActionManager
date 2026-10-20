using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
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
                clone.transform.position = Vector3.zero;
                clone.transform.rotation = Quaternion.identity;
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
                preview.ambientColor = new Color(.65f, .65f, .65f);
                preview.lights[0].intensity = 1.1f;
                preview.lights[0].transform.rotation = Quaternion.Euler(35, 155, 0);
                preview.lights[1].intensity = .8f;
                preview.lights[1].transform.rotation = Quaternion.Euler(320, 25, 0);
                preview.BeginPreview(new Rect(0, 0, 256, 256), GUIStyle.none);
                preview.Render(true);
                var rendered = (RenderTexture)preview.EndPreview();
                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = rendered;
                    result = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                    result.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                    result.Apply();
                }
                finally { RenderTexture.active = previous; }
                var centered = CenterVisiblePixels(result);
                Object.DestroyImmediate(result);
                return centered;
            }
            finally { if (graph.IsValid()) graph.Destroy(); preview.Cleanup(); }
        }

        // Skinned mesh bounds include invisible vertices and accessories. Frame
        // the pixels actually rendered instead, so asymmetric poses stay centered.
        public static Texture2D CenterVisiblePixels(Texture2D source)
        {
            var pixels = source.GetPixels32();
            int minX = source.width, minY = source.height, maxX = -1, maxY = -1;
            for (int y = 0; y < source.height; y++)
                for (int x = 0; x < source.width; x++)
                    if (pixels[y * source.width + x].a > 8)
                    { minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x); minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y); }
            var result = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            var output = new Color[256 * 256];
            if (maxX >= 0)
            {
                float extent = Mathf.Max(maxX - minX + 1, maxY - minY + 1) / .88f;
                float centerX = (minX + maxX + 1) * .5f, centerY = (minY + maxY + 1) * .5f;
                for (int y = 0; y < 256; y++)
                    for (int x = 0; x < 256; x++)
                    {
                        float sx = centerX + ((x + .5f) / 256 - .5f) * extent;
                        float sy = centerY + ((y + .5f) / 256 - .5f) * extent;
                        if (sx >= 0 && sy >= 0 && sx < source.width && sy < source.height)
                            output[y * 256 + x] = source.GetPixelBilinear(sx / source.width, sy / source.height);
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
