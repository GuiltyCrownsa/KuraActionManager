using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;

namespace Kura.ActionInstaller
{
    public static class LegacyObjectMigration
    {
        public static string Complete()
        {
            const string path = "Assets/KuraActionInstaller/原动作插件导入配置.asset";
            var p = AssetDatabase.LoadAssetAtPath<ActionInstallProfile>(path);
            if (p == null) throw new InvalidOperationException("请先导入原动作配置。");
            var source = Resources.FindObjectsOfTypeAll<ModularAvatarMenuInstaller>()
                .Where(m => m.gameObject.scene.IsValid() && m.transform.name == "动作")
                .Select(m => m.gameObject)
                .FirstOrDefault();
            if (source == null) throw new InvalidOperationException("场景里找不到原动作物件「动作」。");
            KuraPaths.EnsureRoot();
            string output = AssetDatabase.GenerateUniqueAssetPath(KuraPaths.Root + "/ImportedObjects");
            AssetDatabase.CreateFolder(KuraPaths.Root, System.IO.Path.GetFileName(output));
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(source);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, scene);
                clone.name = "原动作附属物件";
                clone.transform.position = Vector3.zero; clone.transform.rotation = Quaternion.identity; clone.transform.localScale = source.transform.localScale;
                // Clone together to preserve cross-child constraint and bone references.
                foreach (var component in clone.GetComponents<Component>()) if (!(component is Transform)) UnityEngine.Object.DestroyImmediate(component);
                foreach (string child in new[] { "Contacts", "1 (10)" })
                {
                    var t = clone.transform.Find(child); if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
                }
                // Refuse prefab references back into the old scene/avatar.
                foreach (var c in clone.GetComponentsInChildren<Component>(true).Where(c => c != null))
                {
                    var so = new SerializedObject(c); var property = so.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var component = property.objectReferenceValue as Component;
                        var go = property.objectReferenceValue as GameObject;
                        var target = component != null ? component.transform : go != null ? go.transform : null;
                        if (target != null && target.gameObject.scene.IsValid() && !target.IsChildOf(clone.transform)) throw new InvalidOperationException("物件引用了旧角色：" + c.name + "/" + property.propertyPath);
                    }
                }
                // The source FX turns these props off at rest; the scene snapshot
                // can have them enabled while editing and is not the idle state.
                foreach (var propPath in new[] { "Mic1", "SlenderWheels (Place Outside of Armature)", "Wall", "nadenade (1)/GameObject/nadenade", "AFK_Crystal/mdl_AFKCrystal" })
                {
                    var prop = clone.transform.Find(propPath);
                    if (prop != null) prop.gameObject.SetActive(false);
                }
                var prefab = PrefabUtility.SaveAsPrefabAsset(clone, output + "/原动作附属物件.prefab");
                Undo.RecordObject(p, "迁移动作附属物件和分段动画");
                p.objectPrefab = prefab; p.objectSourcePrefix = "动作"; p.objectSwitches = true;
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }

            var states = AssetDatabase.LoadAllAssetsAtPath("Assets/动作/动画.controller").OfType<AnimatorState>().ToArray();
            var map = states.SelectMany(s => s.transitions).Where(t => t.destinationState != null && t.conditions.Any(c => c.parameter == "dongz" && c.mode == AnimatorConditionMode.Equals))
                .GroupBy(t => (int)t.conditions.First(c => c.parameter == "dongz").threshold).ToDictionary(g => g.Key, g => g.First().destinationState);
            var sync = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/动作/SyncDances/Controllers/Sync_FX.controller");
            var send = sync.layers[1].stateMachine.states.SelectMany(s => s.state.transitions)
                .Where(t => t.destinationState != null && t.conditions.Any(c => c.parameter == "dongz" && c.mode == AnimatorConditionMode.Equals))
                .GroupBy(t => (int)t.conditions.First(c => c.parameter == "dongz").threshold).ToDictionary(g => g.Key, g => g.First().destinationState.motion as AnimationClip);
            var effects = sync.layers[3].stateMachine.states.SelectMany(s => s.state.transitions)
                .Where(t => t.destinationState != null && t.conditions.Any(c => c.parameter == "w1" && c.mode == AnimatorConditionMode.Equals))
                .GroupBy(t => (int)t.conditions.First(c => c.parameter == "w1").threshold).ToDictionary(g => g.Key, g => g.First().destinationState.motion as AnimationClip);
            int chainCount = 0, effectCount = 0;
            var notes = new StringBuilder("附属物件迁移完成\nPrefab：" + AssetDatabase.GetAssetPath(p.objectPrefab) + "\n");
            foreach (string line in System.IO.File.ReadAllLines(path.Replace(".asset", "_核对表.txt")))
            {
                var columns = line.Split('\t'); int id;
                if (columns.Length < 6 || !int.TryParse(columns[0], out id)) continue;
                var group = p.groups.Single(g => g.name == columns[1]);
                var entry = group.actions.Single(e => e.name == columns[2]);
                var original = map[id];
                if (entry.animation != original.motion) throw new InvalidOperationException("导入配置已改动，无法自动映射：" + id);
                entry.continuations.Clear();
                var visited = new HashSet<AnimatorState> { original }; var current = original;
                while (true)
                {
                    var exits = current.transitions.Where(t => t.hasExitTime && t.conditions.Length == 0 && t.destinationState != null && t.destinationState.motion is AnimationClip).ToArray();
                    if (exits.Length == 0) break;
                    if (exits.Length != 1 || !visited.Add(exits[0].destinationState)) throw new InvalidOperationException("不支持分支或循环状态链：" + id);
                    var t = exits[0]; current = t.destinationState;
                    entry.continuations.Add(new ActionContinuation { animation = (AnimationClip)current.motion, speed = current.speed, exitTime = t.exitTime, duration = t.duration, fixedDuration = t.hasFixedDuration, offset = t.offset });
                }
                if (entry.continuations.Count > 0) { chainCount++; notes.AppendLine("分段动作 " + id + "：" + original.name + " -> " + string.Join(" -> ", entry.continuations.Select(x => x.animation.name).ToArray())); }
                var clips = new List<AnimationClip>(); AnimationClip clip;
                if (send.TryGetValue(id, out clip) && clip != null) clips.Add(clip);
                foreach (var driver in original.behaviours.OfType<VRCAvatarParameterDriver>())
                    foreach (var parameter in driver.parameters.Where(x => x.name == "w1"))
                        if (effects.TryGetValue((int)parameter.value, out clip) && clip != null) clips.Add(clip);
                var objectClip = new AnimationClip { name = "动作" + id + "_物件开关" };
                int curves = 0;
                foreach (var item in clips)
                    foreach (var binding in AnimationUtility.GetCurveBindings(item).Where(ActionInstallerBuilder.IsObjectSwitch))
                    {
                        if (binding.path.StartsWith("Contacts/")) continue;
                        if (p.objectPrefab.transform.Find(binding.path) == null) { notes.AppendLine("原插件残留无目标路径，跳过：" + id + " / " + binding.path); continue; }
                        AnimationUtility.SetEditorCurve(objectClip, binding, AnimationUtility.GetEditorCurve(item, binding)); curves++;
                    }
                if (curves > 0) { AssetDatabase.CreateAsset(objectClip, output + "/Object" + id + ".anim"); entry.objectAnimation = objectClip; effectCount++; }
                else { UnityEngine.Object.DestroyImmediate(objectClip); entry.objectAnimation = null; }
            }
            EditorUtility.SetDirty(p); AssetDatabase.SaveAssets();
            notes.AppendLine("完成：5 组物件，" + chainCount + " 个分段动作，" + effectCount + " 个附加物件动画。原动作中的显隐曲线由生成器自动提取。");
            string report = output + "/迁移记录.txt"; System.IO.File.WriteAllText(report, notes.ToString()); AssetDatabase.ImportAsset(report);
            var window = EditorWindow.GetWindow<ActionInstallerWindow>("动作管理器"); window.Repaint();
            return notes.ToString();
        }
    }
}
