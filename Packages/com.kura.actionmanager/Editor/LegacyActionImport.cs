using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Kura.ActionInstaller
{
    public static class LegacyActionImport
    {
        public static string LastPath;
        static IEnumerable<VRCExpressionsMenu.Control> Leaves(VRCExpressionsMenu menu, HashSet<VRCExpressionsMenu> seen)
        {
            if (menu == null || !seen.Add(menu)) yield break;
            foreach (var c in menu.controls)
            {
                if (c.parameter != null && c.parameter.name == "dongz") yield return c;
                else if (c.subMenu != null)
                    foreach (var child in Leaves(c.subMenu, seen)) yield return child;
            }
        }
        public static string Import()
        {
            var states = AssetDatabase.LoadAllAssetsAtPath("Assets/动作/动画.controller").OfType<AnimatorState>().ToArray();
            var transitions = states.SelectMany(s => s.transitions).Where(t => t.destinationState != null).ToArray();
            var map = transitions.Where(t => t.conditions.Any(c => c.parameter == "dongz" && c.mode == AnimatorConditionMode.Equals))
                .GroupBy(t => (int)t.conditions.First(c => c.parameter == "dongz").threshold).ToDictionary(g => g.Key, g => g.First().destinationState);
            var sync = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/动作/SyncDances/Controllers/Sync_FX.controller");
            var codes = new Dictionary<int, int>();
            foreach (var t in sync.layers[1].stateMachine.states.SelectMany(s => s.state.transitions))
            {
                var condition = t.conditions.FirstOrDefault(c => c.parameter == "dongz" && c.mode == AnimatorConditionMode.Equals);
                if (condition.parameter == null || t.destinationState == null) continue;
                var clip = t.destinationState.motion as AnimationClip;
                if (clip == null) continue;
                int code = 0;
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    for (int bit = 1; bit <= 8; bit++)
                        if (b.path == "Contacts/SD" + bit + "S" && AnimationUtility.GetEditorCurve(clip, b).Evaluate(0) > .5f) code |= 1 << (bit - 1);
                codes[(int)condition.threshold] = code;
            }
            var p = ScriptableObject.CreateInstance<ActionInstallProfile>();
            p.name = "原动作插件导入配置"; p.parameterPrefix = "KuraImportedActions";
            p.menuName = "一些拍照用的动作"; p.syncPrefix = "SyncDance"; p.legacySyncNames = true;
            p.mirror = p.speed = false; p.volume = .133f; p.heightRange = 1;
            p.groups.Clear();
            var report = new StringBuilder("原插件导入核对表\n原控制器：Assets/动作/动画.controller\n同步协议：SyncDance11–SyncDance88，保留原发送编号\n\n原ID\t分组\t动作名称\t同步ID\t动画\t音频\n");
            var warnings = new List<string>();
            var seenIds = new HashSet<int>();
            var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>("Assets/动作/dz.asset");
            var seenPages = new HashSet<VRCExpressionsMenu>();
            while (menu != null && seenPages.Add(menu))
            {
                foreach (var c in menu.controls.Where(c => c.subMenu != null && c.name != "→" && c.name != "设置"))
                {
                    var group = new ActionGroup { name = c.name };
                    foreach (var item in Leaves(c.subMenu, new HashSet<VRCExpressionsMenu>()))
                    {
                        int id = (int)item.value;
                        if (!seenIds.Add(id)) { warnings.Add("重复菜单 ID：" + id + " / " + item.name); continue; }
                        AnimatorState state;
                        if (!map.TryGetValue(id, out state) || !(state.motion is AnimationClip)) { warnings.Add("未导入空动作：" + id + " / " + item.name); continue; }
                        var audio = state.behaviours.OfType<VRCAnimatorPlayAudio>().FirstOrDefault();
                        int code; bool enabled = codes.TryGetValue(id, out code) && code > 0;
                        var entry = new ActionEntry { name = item.name, animation = (AnimationClip)state.motion, thumbnail = item.icon,
                            autoThumbnail = false, baseSpeed = state.speed, importedSyncId = enabled ? code : -1,
                            music = audio == null || audio.Clips.Length == 0 ? null : audio.Clips[0], loopMusic = audio == null || audio.Loop };
                        group.actions.Add(entry); group.allowSync |= enabled;
                        report.AppendLine(id + "\t" + group.name + "\t" + entry.name + "\t" + entry.importedSyncId + "\t" + AssetDatabase.GetAssetPath(entry.animation) + "\t" + AssetDatabase.GetAssetPath(entry.music));
                        if (state.transitions.Any(t => t.hasExitTime && t.destinationState != null && t.destinationState.motion != null)) warnings.Add("有后续动画状态：" + id + " / " + entry.name);
                    }
                    group.capacity = Math.Max(15, group.actions.Count);
                    if (group.actions.Count > 0) p.groups.Add(group);
                }
                var next = menu.controls.FirstOrDefault(c => c.name == "→");
                menu = next == null ? null : next.subMenu;
            }
            var unlisted = map.Where(x => x.Value.motion is AnimationClip && !seenIds.Contains(x.Key)).ToArray();
            foreach (var x in unlisted) warnings.Add("控制器中有动作但原菜单未引用：" + x.Key + " / " + x.Value.name);
            report.AppendLine("\n待核对事项：\n" + string.Join("\n", warnings.ToArray()));
            report.AppendLine("附属物件：原 FX 中还控制 SlenderWheels、Mic1、Wall、nadenade、AFK_Crystal，未在配置中替代为普通动作。\n设置：高度范围 ±1、头部固定、配乐开启、同步默认关闭；镜像和变速未额外启用。音量取原场景 AudioSource 的 0.133。\n未修改旧配置、原插件和场景安装。");
            LastPath = AssetDatabase.GenerateUniqueAssetPath("Assets/KuraActionInstaller/原动作插件导入配置.asset");
            AssetDatabase.CreateAsset(p, LastPath);
            string reportPath = LastPath.Replace(".asset", "_核对表.txt");
            System.IO.File.WriteAllText(reportPath, report.ToString()); AssetDatabase.ImportAsset(reportPath);
            AssetDatabase.SaveAssets();
            var window = EditorWindow.GetWindow<ActionInstallerWindow>("动作管理器");
            var serialized = new SerializedObject(window); serialized.FindProperty("profile").objectReferenceValue = p; serialized.ApplyModifiedProperties();
            typeof(ActionInstallerWindow).GetMethod("Rebuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, null);
            window.Repaint(); Selection.activeObject = p;
            return LastPath + "\n分组=" + p.groups.Count + " 动作=" + p.groups.Sum(g => g.actions.Count) + " 配乐=" + p.groups.Sum(g => g.actions.Count(e => e.music != null)) + " 同步=" + p.groups.Sum(g => g.actions.Count(e => e.importedSyncId > 0)) + "\n" + string.Join("\n", warnings.ToArray());
        }
    }
}
