using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Animations;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using Object = UnityEngine.Object;

namespace Kura.ActionInstaller
{
    // Explicitly invoked editor checks, never runs during scene loading or avatar upload.
    public static class ActionInstallerAudit
    {
        public static string LastBuildReport;
        public static string LastPlaybackReport;

        public static string CheckClipSamples(Avatar avatarAsset, AnimationClip animation, AudioClip music)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var go = new GameObject("KuraClipAudit");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            var animator = go.AddComponent<Animator>(); animator.avatar = avatarAsset;
            go.AddComponent<VRCAvatarDescriptor>();
            var h = new GameObject("Armature").transform;
            h.SetParent(go.transform, false); h.localPosition = new Vector3(0, .2f, 0);
            var p = ScriptableObject.CreateInstance<ActionInstallProfile>();
            p.parameterPrefix = "AuditSamples"; p.syncPrefix = "test"; p.groups[0].allowSync = true;
            p.groups[0].actions.Add(new ActionEntry { name = "动作", animation = animation, music = music });
            string path = null;
            try
            {
                var root = ActionInstallerBuilder.Install(p, go, h);
                path = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(root.GetComponent<ModularAvatarMergeAnimator>().animator)).Replace('\\', '/');
                // Samples run without an active Animator graph, so no second writer can
                // overwrite a sampled property. State transitions are checked separately.
                Object.DestroyImmediate(animator);
                var audio = root.GetComponentInChildren<AudioSource>();
                AssetDatabase.LoadAssetAtPath<AnimationClip>(path + "/MusicVolume0.anim").SampleAnimation(root, 0);
                Check(Mathf.Abs(audio.volume) < .0001f, "静音曲线采样失败");
                AssetDatabase.LoadAssetAtPath<AnimationClip>(path + "/MusicVolume1.anim").SampleAnimation(root, 0);
                Check(Mathf.Abs(audio.volume - p.volume) < .0001f, "音量恢复曲线采样失败");
                Check(root.GetComponents<ModularAvatarMergeAnimator>().All(m => m.animator.name != "动画 2"), "旧 FX 高度层仍存在");
                AssetDatabase.LoadAssetAtPath<AnimationClip>(path + "/SyncSend1.anim").SampleAnimation(root, 0);
                for (int bit = 0; bit < 8; bit++) Check(root.transform.Find("Contacts/SD" + (bit + 1) + "S").gameObject.activeSelf == (bit == 0), "发送位采样失败");
                return "PASS: actual clip samples: mute/unmute, no legacy FX height layer, all 8 sender activation bits.";
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(p);
                if (path != null && path.StartsWith("Assets/KuraActionInstaller/Generated/AuditSamples")) AssetDatabase.DeleteAsset(path);
            }
        }
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static string CheckPagination()
        {
            const string folder = KuraPaths.Root + "/__PaginationAudit";
            Check(!AssetDatabase.IsValidFolder(folder), "测试目录已存在，未覆盖。");
            KuraPaths.Ensure("__PaginationAudit");
            int cases = 0;
            try
            {
                foreach (int count in new[] { 0, 1, 6, 7, 8, 9, 13, 14, 15, 30, 64, 255 })
                {
                    var settings = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                    AssetDatabase.CreateAsset(settings, folder + "/Settings" + count + ".asset");
                    var entries = Enumerable.Range(1, count).Select(i => ActionMenus.Toggle("Item" + i, "Selected", i)).ToList();
                    var menu = ActionMenus.Pages(folder, "Case" + count, entries, settings);
                    var seen = new List<int>();
                    int pages = 0;
                    while (menu != null)
                    {
                        Check(++pages < 100, "分页出现循环");
                        Check(menu.controls.Count <= 8, "菜单超过 8 项");
                        var next = menu.controls.LastOrDefault(c => c.name == "→");
                        int settingsIndex = menu.controls.Count - (next == null ? 1 : 2);
                        Check(menu.controls[settingsIndex].subMenu == settings, "设置入口位置错误");
                        Check(menu.controls.Count(c => c.subMenu == settings) == 1, "设置入口重复");
                        foreach (var c in menu.controls.Where(c => c.type == VRCExpressionsMenu.Control.ControlType.Toggle)) seen.Add((int)c.value);
                        if (next != null) Check(menu.controls.Count == 8 && menu.controls.Last() == next, "下一页位置错误");
                        menu = next == null ? null : next.subMenu;
                    }
                    Check(seen.SequenceEqual(Enumerable.Range(1, count)), "动作丢失、重复或顺序错误");
                    cases++;
                }
                return "PASS: " + cases + " pagination boundary cases; settings on every page, <=8 controls, exact order preserved.";
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        public static string CheckGenerated(GameObject root, ActionInstallProfile p)
        {
            var ma = root.GetComponents<ModularAvatarMergeAnimator>();
            Check(ma.Length == 2 + (p.parameterOverrides.Count > 0 ? 1 : 0) + (p.music ? 1 : 0) + (p.sync ? 1 : 0) + (ma.Any(m => m.animator.name == "物件开关") ? 1 : 0), "控制器选装数量不符");
            Check(ma.Count(m => m.layerType == VRCAvatarDescriptor.AnimLayerType.Action) == 1, "Action 合并层不符");
            var names = root.GetComponent<ModularAvatarParameters>().parameters.Select(v => v.nameOrPrefix).ToArray();
            Check(names.Distinct().Count() == names.Length, "存在重复参数");
            var receivers = root.GetComponentsInChildren<VRCContactReceiver>(true);
            var senders = root.GetComponentsInChildren<VRCContactSender>(true);
            Check(receivers.Length == (p.sync ? 8 : 0) && senders.Length == (p.sync ? 8 : 0), "Contact 数量不符");
            for (int i = 1; i <= receivers.Length; i++)
            {
                string expected = ActionInstallerBuilder.SyncParameter(p, i);
                Check(receivers[i - 1].parameter == expected, "Receiver 参数未替换");
                Check(receivers[i - 1].collisionTags.SequenceEqual(new[] { expected }) && senders[i - 1].collisionTags.SequenceEqual(new[] { expected }), "Contact 标签未替换");
            }
            int count = p.groups.Sum(g => g.actions.Count);
            foreach (var m in ma)
            {
                var c = m.animator as AnimatorController;
                Check(c != null, "控制器丢失");
                var parameters = new HashSet<string>(c.parameters.Select(x => x.name));
                foreach (var layer in c.layers)
                {
                    var sm = layer.stateMachine;
                    Check(sm.defaultState != null, "控制器没有默认状态");
                    foreach (var t in sm.anyStateTransitions.Concat(sm.states.SelectMany(s => s.state.transitions)))
                        foreach (var condition in t.conditions) Check(parameters.Contains(condition.parameter), "条件参数未声明：" + condition.parameter);
                }
            }
            var body = (AnimatorController)ma.Single(m => m.layerType == VRCAvatarDescriptor.AnimLayerType.Action).animator;
            Check(body.layers[0].stateMachine.states.Length == count + 1 + p.groups.SelectMany(g => g.actions).Sum(e => e.continuations.Count), "角色动作数量不符");
            var audio = root.GetComponentsInChildren<AudioSource>(true);
            Check(audio.Length == (p.music ? 1 : 0), "共享音源数量错误");
            var bodyStates = p.music ? ((AnimatorController)ma.Single(m => m.animator.name == "动画 1").animator).layers[0].stateMachine.states : new ChildAnimatorState[0];
            var entries = p.groups.SelectMany(g => g.actions).ToArray();
            for (int i = 0; i < bodyStates.Length; i++)
            {
                var plays = bodyStates[i].state.behaviours.OfType<VRCAnimatorPlayAudio>().ToArray();
                Check(plays.Length == (p.music ? 1 : 0), "Play Audio 选装错误");
                if (!p.music) continue;
                var play = plays[0];
                var music = i == 0 ? null : entries[i - 1].music;
                Check(play.StopOnEnter && play.StopOnExit && !play.PlayOnExit && play.PlayOnEnter == (music != null), "Play Audio 播放/停止设置错误");
                Check(play.Clips.Length == (music == null ? 0 : 1) && (music == null || play.Clips[0] == music), "Play Audio 配乐不符");
                Check(root.transform.parent.Find(play.SourcePath) == audio[0].transform, "Play Audio 路径不符");
            }
            var queue = new Queue<VRCExpressionsMenu>();
            var visited = new HashSet<VRCExpressionsMenu>();
            queue.Enqueue(root.GetComponent<ModularAvatarMenuInstaller>().menuToAppend);
            while (queue.Count > 0)
            {
                var menu = queue.Dequeue();
                if (!visited.Add(menu)) continue;
                Check(menu.controls.Count <= 8, "生成菜单超出上限");
                foreach (var item in menu.controls)
                {
                    if (item.subMenu != null) queue.Enqueue(item.subMenu);
                    if (!string.IsNullOrEmpty(item.parameter.name)) Check(names.Contains(item.parameter.name), "菜单参数未注册");
                }
            }
            return "PASS: " + count + " actions, " + ma.Length + " controllers, " + visited.Count + " menus, optional features and contact names verified.";
        }

        public static string CheckOptions(Avatar avatarAsset, AnimationClip clip, AudioClip music)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var go = new GameObject("KuraInstallerAudit");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            go.AddComponent<Animator>().avatar = avatarAsset;
            go.AddComponent<VRCAvatarDescriptor>();
            var height = new GameObject("Armature");
            height.transform.SetParent(go.transform, false);
            var report = new List<string>();
            try
            {
                for (int mask = 0; mask < 16; mask++)
                {
                    var p = ScriptableObject.CreateInstance<ActionInstallProfile>();
                    p.parameterPrefix = "AuditOption" + mask;
                    p.syncPrefix = "test";
                    p.height = (mask & 1) != 0;
                    p.sync = (mask & 2) != 0;
                    p.headLock = (mask & 4) != 0;
                    p.music = (mask & 8) != 0;
                    p.groups.Clear();
                    int groups = mask == 15 ? 9 : 1;
                    for (int g = 0; g < groups; g++)
                    {
                        var group = new ActionGroup { name = "分组" + g, capacity = 30, allowSync = true };
                        for (int a = 0; a < (g == 0 && mask == 15 ? 16 : 2); a++)
                            group.actions.Add(new ActionEntry { name = "动作" + g + "-" + a, animation = clip, music = a % 2 == 0 ? music : null });
                        p.groups.Add(group);
                    }
                    GameObject root = null;
                    string path = null;
                    try
                    {
                        root = ActionInstallerBuilder.Install(p, go, height.transform);
                        path = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(root.GetComponent<ModularAvatarMergeAnimator>().animator)).Replace('\\', '/');
                        Check(path.StartsWith("Assets/KuraActionInstaller/Generated/AuditOption"), "测试路径越界");
                        report.Add(mask + ": " + CheckGenerated(root, p));
                    }
                    finally
                    {
                        if (root != null) Object.DestroyImmediate(root);
                        if (path != null && path.StartsWith("Assets/KuraActionInstaller/Generated/AuditOption")) AssetDatabase.DeleteAsset(path);
                        Object.DestroyImmediate(p);
                    }
                }
                return string.Join("\n", report);
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
        }

        static Transform CopyRig(Transform source, Transform parent)
        {
            var go = new GameObject(source.name, source is RectTransform ? typeof(RectTransform) : typeof(Transform));
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = source.localPosition;
            t.localRotation = source.localRotation;
            t.localScale = source.localScale;
            var rect = source as RectTransform;
            if (rect != null)
            {
                var dest = (RectTransform)t;
                dest.anchorMin = rect.anchorMin; dest.anchorMax = rect.anchorMax;
                dest.pivot = rect.pivot; dest.sizeDelta = rect.sizeDelta;
                dest.anchoredPosition3D = rect.anchoredPosition3D;
            }
            foreach (Transform child in source) CopyRig(child, t);
            return t;
        }

        public static string CheckBuild(GameObject source, AnimationClip animation, AudioClip music)
        {
            LastBuildReport = "RUNNING";
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var go = new GameObject("KuraBuildAudit");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            var p = ScriptableObject.CreateInstance<ActionInstallProfile>();
            string path = null;
            try
            {
                var h = CopyRig(ActionInstallerWindow.FindHeightRoot(source), go.transform);
                go.AddComponent<Animator>().avatar = source.GetComponent<Animator>().avatar;
                var descriptor = go.AddComponent<VRCAvatarDescriptor>();
                p.parameterPrefix = "AuditBuild";
                p.syncPrefix = "test";
                for (int i = 0; i < 9; i++) p.groups[0].actions.Add(new ActionEntry { name = "动作" + (i + 1), animation = animation, music = i % 2 == 0 ? music : null });
                var root = ActionInstallerBuilder.Install(p, go, h);
                path = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(root.GetComponent<ModularAvatarMergeAnimator>().animator)).Replace('\\', '/');
                string before = CheckGenerated(root, p);
                nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(go);
                Check(go.GetComponentsInChildren<ModularAvatarMergeAnimator>(true).Length == 0, "MA 未完成合并");
                Check(descriptor.expressionsMenu != null && descriptor.expressionParameters != null, "菜单或参数没有合并到 Avatar Descriptor");
                Check(descriptor.expressionsMenu.controls.Any(c => c.name == p.menuName), "总菜单入口未安装");
                var controllers = descriptor.baseAnimationLayers.Concat(descriptor.specialAnimationLayers).Select(l => l.animatorController).OfType<AnimatorController>().Distinct().ToArray();
                Check(controllers.Any(c => c.parameters.Any(v => v.name == "AuditBuild/Active")), "Active 参数未合并");
                var receivers = go.GetComponentsInChildren<VRCContactReceiver>(true);
                Check(receivers.Length == 8 && receivers.Select(r => r.parameter).OrderBy(s => s).SequenceEqual(Enumerable.Range(1, 8).Select(i => "test" + i)), "构建后同步参数不符");
                int bound = 0;
                foreach (var c in controllers)
                foreach (var clip in c.animationClips.Distinct())
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (!binding.path.Contains("KuraActions_AuditBuild")) continue;
                    Check(go.transform.Find(binding.path) != null, "构建后动画路径断开：" + binding.path);
                    bound++;
                }
                Check(bound > 20, "未找到音乐及同步路径 count=" + bound + " paths=" + string.Join(";", controllers.SelectMany(c => c.animationClips).SelectMany(c => AnimationUtility.GetCurveBindings(c)).Select(b => b.path).Distinct().Take(20)));
                var audioBehaviours = new List<VRCAnimatorPlayAudio>();
                Action<AnimatorStateMachine> collect = null;
                collect = sm =>
                {
                    foreach (var state in sm.states) audioBehaviours.AddRange(state.state.behaviours.OfType<VRCAnimatorPlayAudio>());
                    foreach (var child in sm.stateMachines) collect(child.stateMachine);
                };
                foreach (var controller in controllers) foreach (var layer in controller.layers) collect(layer.stateMachine);
                Check(audioBehaviours.Count == 10, "合并后 Play Audio 数量不符");
                foreach (var play in audioBehaviours) Check(go.transform.Find(play.SourcePath) != null && go.transform.Find(play.SourcePath).GetComponent<AudioSource>() != null, "合并后 Play Audio 路径无音源");
                LastBuildReport = before + "\nPASS: NDMF/MA build; menu and expression parameters installed; 8 custom receiver parameters retained; " + bound + " generated animation bindings resolve.";
                return LastBuildReport;
            }
            catch (Exception e) { LastBuildReport = "FAIL: " + e; throw; }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(p);
                if (path != null && path.StartsWith("Assets/KuraActionInstaller/Generated/AuditBuild")) AssetDatabase.DeleteAsset(path);
            }
        }

        static void Evaluate(PlayableGraph graph)
        {
            for (int i = 0; i < 4; i++) graph.Evaluate(1f / 60);
        }

        public static string CheckPlayback(GameObject source, AnimationClip animation, AudioClip music)
        {
            LastPlaybackReport = "RUNNING";
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var go = new GameObject("KuraPlaybackAudit");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            var p = ScriptableObject.CreateInstance<ActionInstallProfile>();
            string path = null;
            try
            {
                var h = CopyRig(ActionInstallerWindow.FindHeightRoot(source), go.transform);
                var animator = go.AddComponent<Animator>();
                animator.avatar = source.GetComponent<Animator>().avatar;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                go.AddComponent<VRCAvatarDescriptor>();
                p.parameterPrefix = "AuditPlayback";
                p.groups[0].allowSync = true;
                p.syncPrefix = "test";
                p.groups[0].actions.Add(new ActionEntry { name = "动作一", animation = animation, music = music });
                p.groups[0].actions.Add(new ActionEntry { name = "动作二", animation = animation });
                var root = ActionInstallerBuilder.Install(p, go, h);
                var merges = root.GetComponents<ModularAvatarMergeAnimator>();
                // Relative MA clips are authored against the module root. Give direct clip
                // sampling its own binding root instead of the ancestor avatar Animator.
                root.AddComponent<Animator>();
                path = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(merges[0].animator)).Replace('\\', '/');
                int checks = 0;
                foreach (var merge in merges)
                {
                    var graph = PlayableGraph.Create("KuraActionAudit");
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    try
                    {
                        var controller = (AnimatorController)merge.animator;
                        var playable = AnimatorControllerPlayable.Create(graph, controller);
                        var output = AnimationPlayableOutput.Create(graph, "Audit", animator);
                        output.SetSourcePlayable(playable);
                        graph.Play();
                        Evaluate(graph);
                        if (controller.name == "动作选择")
                        {
                            playable.SetBool("IsLocal", true);
                            playable.SetInteger("AuditPlayback/Selected", 2);
                            Evaluate(graph);
                            Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("2 动作二"), "手动选择状态错误"); checks++;
                            playable.SetInteger("AuditPlayback/Selected", 0);
                            playable.SetBool("AuditPlayback/Sync", true);
                            playable.SetBool("test1", true);
                            Evaluate(graph);
                            Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("1 动作一"), "Contact 编码接收错误"); checks++;
                            playable.SetBool("test8", true);
                            Evaluate(graph);
                            Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("停止"), "无效接收编码未停止"); checks++;
                        }
                        else if (controller.name == "动画")
                        {
                            playable.SetInteger("AuditPlayback/Active", 1);
                            playable.SetBool("AuditPlayback/HeadLock", true);
                            Evaluate(graph);
                            Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("1 动作一"), "身体动作未切换");
                            Check(playable.GetCurrentAnimatorStateInfo(1).shortNameHash == Animator.StringToHash("固定头部"), "固定头部未切换"); checks += 2;
                            playable.SetInteger("AuditPlayback/Active", 0);
                            playable.SetInteger("AuditPlayback/BodyCurrent", 1);
                            Evaluate(graph);
                            Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("停止"), "身体动作未停止");
                            Check(playable.GetCurrentAnimatorStateInfo(1).shortNameHash == Animator.StringToHash("自由头部"), "停止后头部未恢复"); checks += 2;
                        }
                        else if (controller.name == "动画 1")
                        {
                            playable.SetBool("AuditPlayback/Music", false);
                            Evaluate(graph);
                            Check(playable.GetCurrentAnimatorStateInfo(1).shortNameHash == Animator.StringToHash("关"), "音乐开关未切换"); checks++;
                            playable.SetBool("AuditPlayback/Music", true);
                            Evaluate(graph);
                            Check(playable.GetCurrentAnimatorStateInfo(1).shortNameHash == Animator.StringToHash("开"), "音乐开关未恢复"); checks++;
                        }
                        else if (controller.name == "动画 2")
                        {
                            playable.SetInteger("AuditPlayback/Active", 1);
                            playable.SetFloat("AuditPlayback/Height", .75f);
                            Evaluate(graph);
                            Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("动作高度"), "升降状态未切换"); checks++;
                            playable.SetInteger("AuditPlayback/Active", 0);
                            Evaluate(graph);
                            Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("原高度"), "高度未恢复默认状态"); checks++;
                        }
                        else if (controller.name == "Sync_FX")
                        {
                            playable.SetBool("IsLocal", false);
                            playable.SetInteger("AuditPlayback/Active", 2);
                            playable.SetBool("AuditPlayback/Transmit", true);
                            playable.SetBool("AuditPlayback/Sync", true);
                            Evaluate(graph);
                            Check(playable.GetCurrentAnimatorStateInfo(0).shortNameHash == Animator.StringToHash("2"), "远端发送状态未切换");
                            Check(playable.GetCurrentAnimatorStateInfo(1).shortNameHash == Animator.StringToHash("开"), "Contact 开关未切换"); checks += 2;
                        }
                    }
                    finally { graph.Destroy(); }
                }
                LastPlaybackReport = "PASS: " + checks + " evaluated Animator state transitions (manual, contact code, invalid code, stop, head lock, music, elevation, remote sender).";
                return LastPlaybackReport;
            }
            catch (Exception e) { LastPlaybackReport = "FAIL: " + e; throw; }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(p);
                if (path != null && path.StartsWith("Assets/KuraActionInstaller/Generated/AuditPlayback")) AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
