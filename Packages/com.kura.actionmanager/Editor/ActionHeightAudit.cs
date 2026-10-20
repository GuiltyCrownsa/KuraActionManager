using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;

namespace Kura.ActionInstaller
{
    public static class ActionHeightAudit
    {
        static void Copy(Transform source, Transform parent)
        {
            var target = new GameObject(source.name).transform;
            target.SetParent(parent, false);
            target.localPosition = source.localPosition;
            target.localRotation = source.localRotation;
            target.localScale = source.localScale;
            foreach (Transform child in source) Copy(child, target);
        }
        public static string Run(GameObject source, AnimationClip clip)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var go = new GameObject("HeightAudit");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            var profile = ScriptableObject.CreateInstance<ActionInstallProfile>();
            profile.parameterPrefix = "HeightAudit";
            profile.height = true;
            profile.sync = profile.music = profile.headLock = profile.speed = profile.mirror = false;
            profile.groups[0].actions.Add(new ActionEntry { name = "Pose", animation = clip });
            string folder = null;
            var graph = default(PlayableGraph);
            try
            {
                foreach (Transform child in source.transform) Copy(child, go.transform);
                var animator = go.AddComponent<Animator>(); animator.avatar = source.GetComponent<Animator>().avatar;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                go.AddComponent<VRCAvatarDescriptor>();
                var root = ActionInstallerBuilder.Install(profile, go, null);
                var controller = (AnimatorController)root.GetComponents<ModularAvatarMergeAnimator>().Single(m => m.layerType == VRCAvatarDescriptor.AnimLayerType.Action).animator;
                folder = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(controller)).Replace('\\', '/');
                var state = controller.layers[0].stateMachine.states.Single(s => s.state.name == "1 Pose").state;
                state.speed = 0;
                graph = PlayableGraph.Create("HeightAudit");
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "body", animator).SetSourcePlayable(playable);
                graph.Play();
                playable.SetInteger("HeightAudit/Active", 1);
                var values = new float[3];
                for (int i = 0; i < 3; i++)
                {
                    playable.SetFloat("HeightAudit/Height", i * .5f);
                    for (int frame = 0; frame < 5; frame++) graph.Evaluate(.02f);
                    values[i] = animator.GetBoneTransform(HumanBodyBones.Hips).position.y;
                }
                if (!(values[0] < values[1] - .05f && values[1] < values[2] - .05f)) throw new Exception("Height does not ascend: " + string.Join(",", values.Select(v => v.ToString()).ToArray()));
                if (go.GetComponentsInChildren<RectTransform>(true).Length != 0) throw new Exception("RectTransform unexpectedly present");
                playable.SetFloat("HeightAudit/Height", .5f); graph.Evaluate(.02f);
                if (Mathf.Abs(animator.GetBoneTransform(HumanBodyBones.Hips).position.y - values[1]) > .001f) throw new Exception("Center changed");
                return "PASS ordinary Transform rig; hips at low/center/high=" + string.Join(",", values.Select(v => v.ToString("F4")).ToArray());
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                UnityEngine.Object.DestroyImmediate(profile);
                if (folder != null && folder.StartsWith("Assets/KuraActionInstaller/Generated/HeightAudit_")) AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
