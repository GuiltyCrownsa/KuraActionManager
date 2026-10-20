using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Kura.ActionInstaller
{
    public sealed class ActionBatchImportWindow : EditorWindow
    {
        [SerializeField] ActionInstallProfile profile;
        [SerializeField] DefaultAsset folder;
        [SerializeField] bool recursive = true;
        [SerializeField] int perGroup = 15, startNumber = 1, naming, sorting;
        [SerializeField] string prefix = "动作", groupName = "批量动作";
        [SerializeField] bool allowSync;
        AnimationClip[] clips = new AnimationClip[0];
        Vector2 scroll;
        string status;
        public static void Open(ActionInstallProfile target, DefaultAsset directory = null)
        {
            var window = GetWindow<ActionBatchImportWindow>("批量导入动作");
            window.profile = target;
            if (directory != null) window.folder = directory;
            window.minSize = new Vector2(520, 480);
            window.Scan(); window.Show(); window.Focus();
        }
        public static int NaturalCompare(string left, string right)
        {
            var a = Regex.Split(left ?? "", "([0-9]+)");
            var b = Regex.Split(right ?? "", "([0-9]+)");
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                int result;
                if (i % 2 == 1)
                {
                    string x = a[i].TrimStart('0'), y = b[i].TrimStart('0');
                    result = x.Length.CompareTo(y.Length);
                    if (result == 0) result = string.CompareOrdinal(x, y);
                }
                else result = string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
                if (result != 0) return result;
            }
            return a.Length.CompareTo(b.Length);
        }
        public static AnimationClip[] Discover(string directory, bool includeChildren, int order)
        {
            if (!AssetDatabase.IsValidFolder(directory)) return new AnimationClip[0];
            var paths = AssetDatabase.FindAssets("", new[] { directory }).Select(AssetDatabase.GUIDToAssetPath).Distinct()
                .Where(p => includeChildren || System.IO.Path.GetDirectoryName(p).Replace('\\', '/') == directory);
            var result = new List<AnimationClip>();
            foreach (var path in paths)
            {
                if (!path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) && !(AssetImporter.GetAtPath(path) is ModelImporter)) continue;
                result.AddRange(AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c => !c.legacy && !c.name.StartsWith("__preview__", StringComparison.Ordinal)));
            }
            var comparer = Comparer<string>.Create(NaturalCompare);
            return result.Distinct().OrderBy(c => order == 0 ? AssetDatabase.GetAssetPath(c) : c.name, comparer)
                .ThenBy(c => order == 0 ? c.name : AssetDatabase.GetAssetPath(c), comparer)
                .ThenBy(c => AssetDatabase.GetAssetPath(c) + "/" + c.name, StringComparer.Ordinal).ToArray();
        }
        void Scan()
        {
            clips = Discover(AssetDatabase.GetAssetPath(folder), recursive, sorting);
            status = null;
        }
        public static string ActionName(AnimationClip clip, int mode, string text, long number)
        {
            switch (mode)
            {
                case 1: return text + number;
                case 2: return number + text;
                case 3: return clip.name + "_" + number;
                default: return clip.name;
            }
        }
        public static void Import(ActionInstallProfile target, AnimationClip[] source, int capacity, int mode, string text, int first, string groupLabel, bool sync)
        {
            if (target == null || source == null || source.Length == 0) throw new InvalidOperationException("请先选择包含动作的资源文件夹。");
            if (capacity < 1 || capacity > 255) throw new InvalidOperationException("每组动作数量需为 1–255。");
            if (target.groups.Sum(g => g.actions.Count) + source.Length > 255) throw new InvalidOperationException("当前配置最多容纳 255 个动作，请减少导入数量或新建配置。");
            if (first < 0 || string.IsNullOrWhiteSpace(groupLabel) || ((mode == 1 || mode == 2) && string.IsNullOrWhiteSpace(text))) throw new InvalidOperationException("请填写分组名、名称前缀和有效起始编号。");
            if (source.Any(c => c == null || c.legacy)) throw new InvalidOperationException("动作列表包含无效或 Legacy 动画，请重新扫描。");
            var added = new List<ActionGroup>();
            var usedNames = new HashSet<string>(target.groups.Select(g => g.name));
            for (int offset = 0; offset < source.Length; offset += capacity)
            {
                int count = Math.Min(capacity, source.Length - offset);
                string label = groupLabel + " " + ((long)first + offset) + "–" + ((long)first + offset + count - 1);
                string unique = label; int suffix = 2;
                while (!usedNames.Add(unique)) unique = label + " (" + suffix++ + ")";
                var group = new ActionGroup { name = unique, capacity = capacity, allowSync = sync };
                for (int i = offset; i < offset + count; i++) group.actions.Add(new ActionEntry { animation = source[i], name = ActionName(source[i], mode, text, (long)first + i) });
                added.Add(group);
            }
            Undo.RecordObject(target, "文件夹批量导入动作");
            target.groups.AddRange(added);
            EditorUtility.SetDirty(target); AssetDatabase.SaveAssets();
        }
        void OnGUI()
        {
            profile = (ActionInstallProfile)EditorGUILayout.ObjectField("导入到配置", profile, typeof(ActionInstallProfile), false);
            EditorGUI.BeginChangeCheck();
            folder = (DefaultAsset)EditorGUILayout.ObjectField("动作资源文件夹", folder, typeof(DefaultAsset), false);
            recursive = EditorGUILayout.Toggle("包含子文件夹", recursive);
            sorting = EditorGUILayout.Popup("导入顺序", sorting, new[] { "按文件路径自然排序（1、2、10）", "按动作名称自然排序（1、2、10）" });
            if (EditorGUI.EndChangeCheck()) Scan();
            if (GUILayout.Button("重新扫描文件夹")) Scan();
            EditorGUILayout.Space();
            perGroup = EditorGUILayout.IntSlider("每个子菜单动作数", perGroup, 1, 255);
            groupName = EditorGUILayout.TextField("子菜单名称", groupName);
            allowSync = EditorGUILayout.Toggle("新分组允许同步", allowSync);
            naming = EditorGUILayout.Popup("动作命名方式", naming, new[] { "保留原动作名称", "自定义名称＋数字", "数字＋自定义名称", "原动作名称＋数字" });
            if (naming == 1 || naming == 2) prefix = EditorGUILayout.TextField("自定义名称", prefix);
            startNumber = EditorGUILayout.IntField("起始数字", startNumber);
            EditorGUILayout.HelpBox("按下方顺序导入并自动新建分组，满组后继续新建。每组超过 8 个菜单项时，安装时自动分页。名称按输入文字原样拼接。", MessageType.None);
            EditorGUILayout.LabelField("识别到 " + clips.Length + " 个动作，将新建 " + (clips.Length + perGroup - 1) / perGroup + " 个分组", EditorStyles.boldLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (int i = 0; i < clips.Length; i++)
                EditorGUILayout.LabelField(new GUIContent((i + 1) + ". " + ActionName(clips[i], naming, prefix, (long)startNumber + i)), new GUIContent(clips[i].name, AssetDatabase.GetAssetPath(clips[i])));
            EditorGUILayout.EndScrollView();
            int existing = profile == null ? 0 : profile.groups.Sum(g => g.actions.Count);
            if (existing + clips.Length > 255) EditorGUILayout.HelpBox("现有 " + existing + " ＋导入 " + clips.Length + " 超过配置上限 255，请新建配置或减少文件夹内动作。", MessageType.Warning);
            using (new EditorGUI.DisabledScope(profile == null || clips.Length == 0 || existing + clips.Length > 255 || startNumber < 0))
                if (GUILayout.Button("按预览顺序导入", GUILayout.Height(32)))
                {
                    try
                    {
                        Import(profile, clips, perGroup, naming, prefix, startNumber, groupName, allowSync);
                        status = "导入完成，可用 Unity 撤销恢复。请回到动作管理器检查并更新安装。";
                        foreach (var window in Resources.FindObjectsOfTypeAll<ActionInstallerWindow>()) window.RefreshProfile();
                    }
                    catch (Exception e) { status = e.Message; }
                }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        }
    }
}
