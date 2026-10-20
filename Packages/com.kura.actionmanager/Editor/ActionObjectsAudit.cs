using System;
using System.Collections.Generic;
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
    public static class ActionObjectsAudit
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        public static string Build(GameObject source)
        {
            var p = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<ActionInstallProfile>("Assets/KuraActionInstaller/原动作插件导入配置.asset"));
            p.parameterPrefix = "AuditObjectsBuild";
            foreach (var g in p.groups) g.actions = g.actions.Where(e => e.objectAnimation != null || e.continuations.Count > 0 || AnimationUtility.GetCurveBindings(e.animation).Any(ActionInstallerBuilder.IsObjectSwitch)).ToList();
            p.groups = p.groups.Where(g => g.actions.Count > 0).ToList();
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var go = new GameObject("ObjectsBuildAudit"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            string folder = null;
            try
            {
                var method = typeof(ActionInstallerAudit).GetMethod("CopyRig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var h = (Transform)method.Invoke(null, new object[] { ActionInstallerWindow.FindHeightRoot(source), go.transform });
                go.AddComponent<Animator>().avatar = source.GetComponent<Animator>().avatar;
                var descriptor = go.AddComponent<VRCAvatarDescriptor>();
                var root = ActionInstallerBuilder.Install(p, go, h);
                folder = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(root.GetComponent<ModularAvatarMergeAnimator>().animator)).Replace('\\', '/');
                ActionInstallerAudit.CheckGenerated(root, p);
                // The SDK normally unpacks the upload clone before NDMF runs.
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    if (t != null && PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject))
                        PrefabUtility.UnpackPrefabInstance(t.gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(go);
                Check(go.GetComponentsInChildren<ModularAvatarMergeAnimator>(true).Length == 0, "MA 控制器未完成合并");
                Check(go.GetComponentsInChildren<ModularAvatarBoneProxy>(true).Length == 0, "物件挂载未完成合并");
                var controllers = descriptor.baseAnimationLayers.Concat(descriptor.specialAnimationLayers).Select(l => l.animatorController).OfType<AnimatorController>().Distinct().ToArray();
                int switches = 0, audio = 0;
                foreach (var controller in controllers)
                {
                    foreach (var clip in controller.animationClips.Distinct())
                    {
                        if (!clip.name.StartsWith("Objects")) continue;
                        foreach (var b in AnimationUtility.GetCurveBindings(clip).Where(ActionInstallerBuilder.IsObjectSwitch))
                        {
                            Check(go.transform.Find(b.path) != null, "MA 合并后物件路径丢失：" + b.path); switches++;
                        }
                    }
                    Action<AnimatorStateMachine> visit = null;
                    visit = sm =>
                    {
                        foreach (var s in sm.states)
                            foreach (var play in s.state.behaviours.OfType<VRCAnimatorPlayAudio>())
                            {
                                var target = go.transform.Find(play.SourcePath);
                                Check(target != null && target.GetComponent<AudioSource>() != null, "MA 合并后配乐路径失效"); audio++;
                            }
                        foreach (var child in sm.stateMachines) visit(child.stateMachine);
                    };
                    foreach (var layer in controller.layers) visit(layer.stateMachine);
                }
                Check(switches > 20 && audio > 0, "合并后缺少物件或音频控制");
                Check(!descriptor.expressionParameters.parameters.Any(x => x.name.EndsWith("BodyCurrent") || x.name.EndsWith("ObjectCurrent")), "内部阶段参数占用了表情参数");
                return "PASS: MA/NDMF build; " + switches + " prop curve paths resolve after bone attachment; " + audio + " audio behaviours resolve; phase parameters add zero network bits.";
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); UnityEngine.Object.DestroyImmediate(p);
                if (folder != null && folder.StartsWith("Assets/KuraActionInstaller/Generated/AuditObjectsBuild_")) AssetDatabase.DeleteAsset(folder);
            }
        }
        public static string Run(GameObject source)
        {
            var original = AssetDatabase.LoadAssetAtPath<ActionInstallProfile>("Assets/KuraActionInstaller/原动作插件导入配置.asset");
            var p = UnityEngine.Object.Instantiate(original); p.parameterPrefix = "AuditObjects";
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var go = new GameObject("ObjectAudit");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            var animator = go.AddComponent<Animator>(); animator.avatar = source.GetComponent<Animator>().avatar;
            go.AddComponent<VRCAvatarDescriptor>();
            var h = new GameObject("Armature").transform; h.SetParent(go.transform, false);
            string folder = null;
            try
            {
                var root = ActionInstallerBuilder.Install(p, go, h);
                var merges = root.GetComponents<ModularAvatarMergeAnimator>();
                folder = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(merges[0].animator)).Replace('\\', '/');
                ActionInstallerAudit.CheckGenerated(root, p);
                var body = (AnimatorController)merges.Single(m => m.layerType == VRCAvatarDescriptor.AnimLayerType.Action).animator;
                var objects = (AnimatorController)merges.Single(m => m.animator.name == "物件开关").animator;
                var entries = p.groups.SelectMany(g => g.actions).ToArray();
                Check(root.transform.Find("Objects").childCount == 5, "物件组数量错误");
                Check(root.GetComponentsInChildren<AudioSource>(true).Length == 1, "旧音源重复复制");
                int switchChecks = 0, sequenceChecks = 0;
                var reset = AssetDatabase.LoadAssetAtPath<AnimationClip>(folder + "/ObjectsReset.anim");
                var defaults = AnimationUtility.GetCurveBindings(reset);
                Check(defaults.Length > 0, "缺少物件复原曲线");
                UnityEngine.Object.DestroyImmediate(animator);
                for (int i = 0; i < entries.Length; i++)
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(folder + "/Objects" + (i + 1) + ".anim") ?? reset;
                    Check(AnimationUtility.GetCurveBindings(clip).Length == defaults.Length, "切换动作未覆盖全部物件默认状态");
                    clip.SampleAnimation(go, 0);
                    foreach (var b in defaults)
                    {
                        var target = go.transform.Find(b.path);
                        Check(target != null && target.IsChildOf(root.transform), "物件曲线路径错误：" + b.path);
                        var value = AnimationUtility.GetEditorCurve(clip, b).Evaluate(0) > .5f;
                        Check(target.gameObject.activeSelf == value, "物件开关采样错误：" + b.path);
                        switchChecks++;
                    }
                    reset.SampleAnimation(go, 0);
                    foreach (var b in defaults) Check(go.transform.Find(b.path).gameObject.activeSelf == (AnimationUtility.GetEditorCurve(reset, b).Evaluate(0) > .5f), "停止动作后没有恢复物件");
                    var start = body.layers[0].stateMachine.states.Single(s => s.state.name == (i + 1) + " " + entries[i].name).state;
                    Check(!(start.motion is BlendTree ? ((BlendTree)start.motion).children.Select(c => (AnimationClip)c.motion) : new[] { (AnimationClip)start.motion }).Any(c => AnimationUtility.GetCurveBindings(c).Any(ActionInstallerBuilder.IsObjectSwitch)), "身体层仍在写物件开关");
                    if (entries[i].continuations.Count == 0) continue;
                    foreach (var controller in new[] { body, objects })
                    {
                        var initial = controller.layers[0].stateMachine.states.FirstOrDefault(s => s.state.name == (i + 1) + " " + entries[i].name).state;
                        if (initial == null && controller == objects) continue;
                        var part = entries[i].continuations[0]; var t = initial.transitions.Single();
                        Check(t.exitTime == part.exitTime && t.duration == part.duration && t.hasFixedDuration == part.fixedDuration, "分段切换时机不符");
                        Check(t.destinationState.motion != null, "循环段丢失");
                        var entryTransition = controller.layers[0].stateMachine.anyStateTransitions.Single(x => x.destinationState == initial);
                        Check(entryTransition.conditions.Any(c => c.mode == AnimatorConditionMode.NotEqual && c.parameter.EndsWith("Current")), "循环段会重新跳回入场");
                        sequenceChecks++;
                    }
                }
                // Evaluate the FX state machine, emulating the SDK's controller-local driver writes.
                var testAnimator = go.AddComponent<Animator>(); testAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var graph = PlayableGraph.Create("ObjectSequenceAudit"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                try
                {
                    var playable = AnimatorControllerPlayable.Create(graph, objects);
                    AnimationPlayableOutput.Create(graph, "Objects", testAnimator).SetSourcePlayable(playable); graph.Play();
                    // Sampling alone accepts incorrect discrete bindings. Assert the
                    // actual Animator output on the imported props as well.
                    int[] propIds = { 18, 27, 34, 35, 40 };
                    string[] propPaths = { "Mic1", "SlenderWheels (Place Outside of Armature)", "Wall", "nadenade (1)/GameObject/nadenade", "AFK_Crystal/mdl_AFKCrystal" };
                    for (int index = 0; index < propIds.Length; index++)
                    {
                        playable.SetInteger("AuditObjects/Active", propIds[index]);
                        for (int frame = 0; frame < 4; frame++) graph.Evaluate(.02f);
                        for (int other = 0; other < propPaths.Length; other++)
                            Check(root.transform.Find("Objects/" + propPaths[other]).gameObject.activeSelf == (other == index), "实际 Animator 开关失败：" + propPaths[other] + " / 动作 " + propIds[index]);
                        playable.SetInteger("AuditObjects/ObjectCurrent", propIds[index]);
                        playable.SetInteger("AuditObjects/Active", 0);
                        for (int frame = 0; frame < 4; frame++) graph.Evaluate(.02f);
                        foreach (var path in propPaths) Check(!root.transform.Find("Objects/" + path).gameObject.activeSelf, "实际 Animator 停止后物件未关闭：" + path);
                        playable.SetInteger("AuditObjects/ObjectCurrent", 0);
                    }
                    for (int i = 0; i < entries.Length; i++)
                    {
                        if (entries[i].continuations.Count == 0 || !objects.layers[0].stateMachine.states.Any(s => s.state.name == (i + 1) + " / 1")) continue;
                        playable.SetInteger("AuditObjects/Active", i + 1); playable.SetInteger("AuditObjects/ObjectCurrent", 0);
                        graph.Evaluate(.02f); graph.Evaluate(.02f);
                        playable.SetInteger("AuditObjects/ObjectCurrent", i + 1);
                        float seconds = entries[i].animation.length * entries[i].continuations[0].exitTime / entries[i].baseSpeed + entries[i].continuations[0].duration + .3f;
                        for (float time = 0; time < seconds; time += 1f / 30) graph.Evaluate(1f / 30);
                        Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash((i + 1) + " / 1"), "分段动画实际播放未进入循环段：" + (i + 1));
                        graph.Evaluate(.2f);
                        Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash((i + 1) + " / 1"), "循环段被重置");
                        playable.SetInteger("AuditObjects/Active", 0); graph.Evaluate(.02f); graph.Evaluate(.02f);
                        Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("停止"), "分段动作无法停止");
                        playable.SetInteger("AuditObjects/ObjectCurrent", 0);
                    }
                }
                finally { graph.Destroy(); }
                return "PASS: " + entries.Length + " actions; " + switchChecks + " switch samples plus reset checks; " + sequenceChecks + " body/FX phase timings; three evaluated loop transitions and stops; five prop groups; one audio source. Missing paths: " + (System.IO.File.Exists(folder + "/未匹配物件.txt") ? System.IO.File.ReadAllText(folder + "/未匹配物件.txt") : "none");
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                UnityEngine.Object.DestroyImmediate(p);
                if (folder != null && folder.StartsWith("Assets/KuraActionInstaller/Generated/AuditObjects_")) AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
