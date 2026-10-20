using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;

namespace Kura.ActionInstaller
{
    public static class ActionManagerAudit
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        public static string Run(Avatar avatar, AnimationClip animation, Texture2D icon)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var go = new GameObject("GroupAudit");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            go.AddComponent<Animator>().avatar = avatar;
            go.AddComponent<VRCAvatarDescriptor>();
            var p = ScriptableObject.CreateInstance<ActionInstallProfile>();
            p.parameterPrefix = "AuditGroups"; p.height = p.headLock = p.music = false;
            p.groups.Clear();
            for (int g = 0; g < 9; g++)
            {
                var group = new ActionGroup { name = "Group" + g, allowSync = g % 2 == 1 };
                group.actions.Add(new ActionEntry { name = "Action" + (g + 1), animation = animation, thumbnail = icon });
                p.groups.Add(group);
            }
            string path = null;
            try
            {
                var root = ActionInstallerBuilder.Install(p, go, null);
                var merges = root.GetComponents<ModularAvatarMergeAnimator>();
                path = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(merges[0].animator)).Replace('\\', '/');
                ActionInstallerAudit.CheckGenerated(root, p);
                var main = root.GetComponent<ModularAvatarMenuInstaller>().menuToAppend.controls[0].subMenu;
                int groupCount = 0, pageIndex = 0, settingsCount = 0;
                while (main != null)
                {
                    Check(main.controls.Count <= 8, "总菜单分页超限");
                    settingsCount += main.controls.Count(c => c.name == "设置");
                    if (pageIndex++ == 0) Check(main.controls[main.controls.Count - (main.controls.Last().name == "→" ? 2 : 1)].name == "设置", "首页设置位置错误");
                    else Check(main.controls.All(c => c.name != "设置"), "设置重复出现在后续页");
                    foreach (var entry in main.controls.Where(c => c.name != "→" && c.name != "设置"))
                    {
                        groupCount++;
                        Check(entry.subMenu.controls.All(c => c.name != "设置"), "动作页仍有设置");
                        Check(entry.subMenu.controls[0].icon == icon, "缩略图未写入菜单");
                    }
                    var next = main.controls.FirstOrDefault(c => c.name == "→");
                    main = next == null ? null : next.subMenu;
                }
                Check(groupCount == 9, "分组丢失");
                Check(settingsCount == 1, "设置入口不唯一");
                var resolver = (AnimatorController)merges.First(m => m.animator.name == "动作选择").animator;
                var transitions = resolver.layers[0].stateMachine.anyStateTransitions;
                var parameters = root.GetComponent<ModularAvatarParameters>().parameters;
                Check(parameters.Single(x => x.nameOrPrefix.EndsWith("/Selected")).localOnly, "菜单选择仍占联网预算");
                Check(!parameters.Single(x => x.nameOrPrefix.EndsWith("/Active")).localOnly, "播放动作未联网");
                Check(parameters.Single(x => x.nameOrPrefix.EndsWith("/Transmit")).syncType == ParameterSyncType.Bool, "发起标记不是 Bool");
                var sender = (AnimatorController)merges.First(m => m.animator.name == "Sync_FX").animator;
                for (int active = 0; active <= 9; active++)
                    for (int origin = 0; origin <= 1; origin++)
                    {
                        var destinations = sender.layers[0].stateMachine.anyStateTransitions.Where(t => t.conditions.All(condition =>
                            condition.parameter.EndsWith("/Active") ? condition.mode == AnimatorConditionMode.Equals && condition.threshold == active :
                            condition.parameter.EndsWith("/Transmit") && (condition.mode == AnimatorConditionMode.If ? origin == 1 : origin == 0)))
                            .Select(t => t.destinationState.name).Distinct().ToArray();
                        Check(destinations.Length == 1 && destinations[0] == (origin == 0 ? "0" : active.ToString()), "跟随转发隔离错误");
                    }
                for (int id = 1; id <= 9; id++)
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path + "/SyncSend" + id + ".anim");
                    var bindings = AnimationUtility.GetCurveBindings(clip);
                    int encoded = 0;
                    for (int bit = 1; bit <= 8; bit++)
                    {
                        var binding = bindings.Single(b => b.path == "Contacts/SD" + bit + "S");
                        if (AnimationUtility.GetEditorCurve(clip, binding).Evaluate(0) > .5f) encoded |= 1 << (bit - 1);
                    }
                    bool allowed = id % 2 == 0;
                    Check(encoded == (allowed ? id : 0), "发送编码错误：" + id);
                    var receive = transitions.Count(t => t.destinationState.name == id + " Action" + id && t.conditions.Any(c => c.parameter == "Kurasync1"));
                    Check(receive == (allowed ? 1 : 0), "分组接收限制错误：" + id);
                }
                var report = System.IO.File.ReadAllText(path + "/同步ID对照表.txt");
                for (int id = 1; id <= 9; id++) Check(report.Contains("\tAction" + id + "\t") == (id % 2 == 0), "ID 对照表不一致");
                return "PASS: 9 groups paginated; one settings entry on main first page before Next; no child settings; icons and sync checks pass.";
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                UnityEngine.Object.DestroyImmediate(p);
                if (path != null && path.StartsWith("Assets/KuraActionInstaller/Generated/AuditGroups")) AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
