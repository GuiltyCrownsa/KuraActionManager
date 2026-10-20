using System;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Kura.ActionInstaller
{
    public sealed class ActionInstallerWindow : EditorWindow
    {
        [SerializeField] ActionInstallProfile profile;
        [SerializeField] GameObject avatar;
        [SerializeField] Transform heightRoot;
        SerializedObject serialized;
        ReorderableList groups, actions;
        int selectedGroup;
        int moveTarget;
        Vector2 scroll;
        Vector2 groupScroll;
        [SerializeField] float groupPanelWidth = 280;
        string message;
        [SerializeField] int page;

        [MenuItem("王冠Kura/动作管理器")]
        public static void Open() { GetWindow<ActionInstallerWindow>("动作管理器"); }

        void OnEnable() { titleContent = new GUIContent("动作管理器"); minSize = new Vector2(720, 500); Undo.undoRedoPerformed += Rebuild;
            if (avatar != null) { var remembered = ActionModelMemory.Recall(avatar); if (remembered != null) profile = remembered; else if (profile != null) ActionModelMemory.Remember(avatar, profile); else profile = ActionModelMemory.GetOrCreate(avatar); }
            Rebuild(); }
        void OnDisable() { Undo.undoRedoPerformed -= Rebuild; ActionModelMemory.Remember(avatar, profile); }

        void SelectProfile(ActionInstallProfile next)
        {
            if (serialized != null) serialized.ApplyModifiedProperties();
            profile = next; selectedGroup = 0;
            ActionModelMemory.Remember(avatar, profile);
            Rebuild();
        }
        void SelectAvatar(GameObject next)
        {
            if (serialized != null) serialized.ApplyModifiedProperties();
            ActionModelMemory.Remember(avatar, profile);
            avatar = next; heightRoot = FindHeightRoot(avatar);
            if (avatar != null) SelectProfile(ActionModelMemory.GetOrCreate(avatar));
        }

        void Rebuild()
        {
            if (profile == null) { serialized = null; groups = actions = null; return; }
            serialized = new SerializedObject(profile);
            groups = new ReorderableList(serialized, serialized.FindProperty("groups"), true, true, true, true);
            groups.drawHeaderCallback = r => EditorGUI.LabelField(r, "大菜单分组（拖动排序，＋新建）");
            groups.elementHeight = 45;
            groups.drawElementCallback = (r, i, active, focused) =>
            {
                var g = groups.serializedProperty.GetArrayElementAtIndex(i);
                r.height = EditorGUIUtility.singleLineHeight;
                EditorGUI.PropertyField(new Rect(r.x, r.y + 1, r.width - 145, r.height), g.FindPropertyRelative("name"), GUIContent.none);
                EditorGUI.LabelField(new Rect(r.xMax - 135, r.y + 1, 65, r.height), "容量");
                EditorGUI.PropertyField(new Rect(r.xMax - 90, r.y + 1, 85, r.height), g.FindPropertyRelative("capacity"), GUIContent.none);
                var sync = g.FindPropertyRelative("allowSync");
                sync.boolValue = EditorGUI.ToggleLeft(new Rect(r.x, r.y + 23, 150, r.height), "允许此分组动作同步", sync.boolValue);
                EditorGUI.LabelField(new Rect(r.x + 160, r.y + 23, r.width - 160, r.height), g.FindPropertyRelative("actions").arraySize + " 个动作", EditorStyles.miniLabel);
            };
            groups.onAddCallback = list =>
            {
                serialized.ApplyModifiedProperties();
                Undo.RecordObject(profile, "新增动作分组");
                profile.groups.Add(new ActionGroup { name = "新建分组" });
                EditorUtility.SetDirty(profile);
                selectedGroup = profile.groups.Count - 1;
                Rebuild();
            };
            groups.onRemoveCallback = list =>
            {
                serialized.ApplyModifiedProperties();
                if (list.index < 0) return;
                Undo.RecordObject(profile, "删除动作分组");
                profile.groups.RemoveAt(list.index);
                EditorUtility.SetDirty(profile);
                selectedGroup = Math.Max(0, list.index - 1);
                Rebuild();
            };
            groups.onSelectCallback = list => { selectedGroup = list.index; scroll = Vector2.zero; BuildActions(); };
            groups.onReorderCallback = list => { selectedGroup = list.index; serialized.ApplyModifiedProperties(); BuildActions(); };
            selectedGroup = Mathf.Clamp(selectedGroup, 0, profile.groups.Count - 1);
            groups.index = selectedGroup;
            BuildActions();
        }
        public void RefreshProfile() { Rebuild(); Repaint(); }

        void BuildActions()
        {
            if (serialized == null || selectedGroup < 0 || selectedGroup >= serialized.FindProperty("groups").arraySize) { actions = null; return; }
            var data = serialized.FindProperty("groups").GetArrayElementAtIndex(selectedGroup).FindPropertyRelative("actions");
            actions = new ReorderableList(serialized, data, true, true, true, true);
            actions.elementHeightCallback = i => 220 + EditorGUI.GetPropertyHeight(data.GetArrayElementAtIndex(i).FindPropertyRelative("continuations"), true);
            actions.drawHeaderCallback = r => EditorGUI.LabelField(r, "当前分组动作（拖动排序）");
            actions.drawElementCallback = (r, i, active, focused) =>
            {
                var entry = data.GetArrayElementAtIndex(i);
                r.y += 2;
                float x = r.x + 105, w = r.width - 105;
                var icon = entry.FindPropertyRelative("thumbnail");
                icon.objectReferenceValue = EditorGUI.ObjectField(new Rect(r.x, r.y, 92, 92), icon.objectReferenceValue, typeof(Texture2D), false);
                var auto = entry.FindPropertyRelative("autoThumbnail");
                auto.boolValue = EditorGUI.ToggleLeft(new Rect(r.x, r.y + 96, 100, 18), "自动拍摄", auto.boolValue);
                using (new EditorGUI.DisabledScope(avatar == null || entry.FindPropertyRelative("animation").objectReferenceValue == null))
                    if (GUI.Button(new Rect(r.x, r.y + 118, 92, 20), "拍摄缩略图"))
                    {
                        serialized.ApplyModifiedProperties();
                        QueueCapture(new[] { profile.groups[selectedGroup].actions[i] }, false, true);
                    }
                EditorGUI.LabelField(new Rect(x, r.y, 28, 18), (i + 1).ToString());
                EditorGUI.PropertyField(new Rect(x + 30, r.y, w - 30, 18), entry.FindPropertyRelative("name"), GUIContent.none);
                EditorGUI.PropertyField(new Rect(x, r.y + 26, w, 18), entry.FindPropertyRelative("animation"), new GUIContent("动作"));
                if (profile.music)
                {
                    EditorGUI.PropertyField(new Rect(x, r.y + 52, w - 100, 18), entry.FindPropertyRelative("music"), new GUIContent("配乐（可选）"));
                    var loop = entry.FindPropertyRelative("loopMusic");
                    loop.boolValue = EditorGUI.ToggleLeft(new Rect(r.xMax - 95, r.y + 52, 95, 18), "循环", loop.boolValue);
                }
                EditorGUI.PropertyField(new Rect(x, r.y + 80, w, 18), entry.FindPropertyRelative("thumbnailTime"), new GUIContent("拍摄时间（秒）"));
                int syncId = profile.groups.Take(selectedGroup).Sum(g => g.actions.Count) + i + 1;
                int importedId = entry.FindPropertyRelative("importedSyncId").intValue;
                if (importedId != 0) syncId = importedId;
                bool participates = serialized.FindProperty("sync").boolValue && serialized.FindProperty("groups").GetArrayElementAtIndex(selectedGroup).FindPropertyRelative("allowSync").boolValue;
                EditorGUI.LabelField(new Rect(x, r.y + 108, w, 20), participates && syncId > 0 ? "同步 ID：" + syncId + "   编码：" + Convert.ToString(syncId, 2).PadLeft(8, '0') : "同步 ID：—（不参与同步）", EditorStyles.boldLabel);
                EditorGUI.PropertyField(new Rect(x, r.y + 140, w, 18), entry.FindPropertyRelative("objectAnimation"), new GUIContent("附加物件开关动画"));
                int phases = entry.FindPropertyRelative("continuations").arraySize;
                if (phases > 0) EditorGUI.LabelField(new Rect(r.x, r.y + 143, 100, 18), "后续动画：" + phases + " 段", EditorStyles.miniLabel);
                EditorGUI.PropertyField(new Rect(r.x, r.y + 168, r.width * .48f, 18), entry.FindPropertyRelative("baseSpeed"), new GUIContent("首段速度"));
                var firstLoop = entry.FindPropertyRelative("loopMode");
                firstLoop.enumValueIndex = EditorGUI.Popup(new Rect(r.x + r.width * .5f, r.y + 168, r.width * .5f, 18), "首段循环", firstLoop.enumValueIndex, new[] { "沿用动画", "循环", "单次" });
                var parts = entry.FindPropertyRelative("continuations");
                if (GUI.Button(new Rect(r.x, r.y + 191, r.width, 20), "＋添加后续动画（入场 → 循环）"))
                {
                    int n = parts.arraySize++; var part = parts.GetArrayElementAtIndex(n);
                    part.FindPropertyRelative("animation").objectReferenceValue = null;
                    part.FindPropertyRelative("exitTime").floatValue = 1;
                    part.FindPropertyRelative("duration").floatValue = .1f;
                    part.FindPropertyRelative("fixedDuration").boolValue = true;
                    part.FindPropertyRelative("offset").floatValue = 0;
                    part.FindPropertyRelative("speed").floatValue = 1;
                    part.FindPropertyRelative("loopMode").enumValueIndex = 1;
                    parts.isExpanded = true;
                }
                EditorGUI.PropertyField(new Rect(r.x, r.y + 216, r.width, EditorGUI.GetPropertyHeight(parts, true)), parts, new GUIContent("后续动画（按顺序播放）"), true);
            };
            actions.onAddCallback = list =>
            {
                serialized.ApplyModifiedProperties();
                Undo.RecordObject(profile, "新增动作");
                profile.groups[selectedGroup].actions.Add(new ActionEntry());
                EditorUtility.SetDirty(profile);
                Rebuild();
            };
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("动作管理器", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            var next = (ActionInstallProfile)EditorGUILayout.ObjectField("配置", profile, typeof(ActionInstallProfile), false);
            if (next != profile) SelectProfile(next);
            if (GUILayout.Button("新建配置", GUILayout.Width(90)))
            {
                string path = EditorUtility.SaveFilePanelInProject("保存动作配置", "动作配置", "asset", "选择配置保存位置");
                if (!string.IsNullOrEmpty(path))
                {
                    var created = CreateInstance<ActionInstallProfile>();
                    AssetDatabase.CreateAsset(created, path);
                    SelectProfile(created);
                }
            }
            EditorGUILayout.EndHorizontal();
            var nextAvatar = (GameObject)EditorGUILayout.ObjectField("安装到角色", avatar, typeof(GameObject), true);
            if (nextAvatar != avatar) SelectAvatar(nextAvatar);
            EditorGUILayout.LabelField("模型与配置自动关联；新模型自动使用独立的新配置。", EditorStyles.miniLabel);
            if (serialized == null) return;
            serialized.Update();
            EditorGUILayout.Space(5);
            page = Mathf.Clamp(page, 0, 2);
            int nextPage = GUILayout.Toolbar(page, new[] { "动作编排", "缩略图", "设置" }, GUILayout.Height(30));
            if (nextPage != page) { page = nextPage; scroll = Vector2.zero; GUI.FocusControl(null); }
            EditorGUILayout.Space(8);
            if (page != 0) scroll = EditorGUILayout.BeginScrollView(scroll);
            if (page == 2)
            {
            EditorGUILayout.LabelField("菜单与安装", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serialized.FindProperty("menuName"), new GUIContent("总菜单名称"));
            EditorGUILayout.PropertyField(serialized.FindProperty("parameterPrefix"), new GUIContent("安装标识"));
            EditorGUILayout.HelpBox("设置入口放在总菜单第一页末尾、翻页按钮之前。安装标识用于区分不同安装，修改配置后需重新生成。", MessageType.None);
            }
            if (page == 2)
            {
            EditorGUILayout.LabelField("动作期间临时修改参数", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("填写其他插件的实际 Animator 参数名。动作值：Bool 关闭为 0、开启为 1；若参数含义是“禁用眨眼”，应填 1。进入动作时记录原值并设置动作值，停止后恢复；连续切换动作不重复记录。请勿填写 VRC 内置参数。", MessageType.None);
            var overrides = serialized.FindProperty("parameterOverrides");
            for (int i = 0; i < overrides.arraySize; i++)
            {
                var item = overrides.GetArrayElementAtIndex(i);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(item.FindPropertyRelative("parameter"), new GUIContent("参数名"));
                EditorGUILayout.PropertyField(item.FindPropertyRelative("actionValue"), new GUIContent("动作值"));
                if (GUILayout.Button("删除", GUILayout.Width(45))) { overrides.DeleteArrayElementAtIndex(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("＋添加临时参数"))
            {
                int i = overrides.arraySize++; var item = overrides.GetArrayElementAtIndex(i);
                item.FindPropertyRelative("parameter").stringValue = "";
                item.FindPropertyRelative("actionValue").floatValue = 0;
            }
            EditorGUILayout.PropertyField(serialized.FindProperty("objectSwitches"), new GUIContent("自动处理动画中的物件开关"));
            if (serialized.FindProperty("objectSwitches").boolValue)
            {
                EditorGUILayout.PropertyField(serialized.FindProperty("objectPrefab"), new GUIContent("随安装复制的物件 Prefab"));
                EditorGUILayout.PropertyField(serialized.FindProperty("objectSourcePrefix"), new GUIContent("动画原物件根路径"));
                EditorGUILayout.HelpBox("自动读取动作和附加动画中的物件显隐曲线。路径先匹配复制的物件，再匹配角色下同路径物件；停止或切换动作时恢复安装时状态。未匹配路径会写入生成目录的“未匹配物件.txt”。", MessageType.None);
            }
            }
            if (page == 2)
            {
            EditorGUILayout.LabelField("配乐与动作控制", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serialized.FindProperty("music"), new GUIContent("动作配乐"));
            if (serialized.FindProperty("music").boolValue)
                EditorGUILayout.Slider(serialized.FindProperty("volume"), 0, 1, new GUIContent("音乐音量"));
            EditorGUILayout.PropertyField(serialized.FindProperty("height"), new GUIContent("高度调整"));
            if (serialized.FindProperty("height").boolValue)
            {
                EditorGUILayout.PropertyField(serialized.FindProperty("heightRange"), new GUIContent("上下调整范围"));
                EditorGUILayout.HelpBox("高度直接应用于 Humanoid 动作，50% 为原高度，无需给 Armature 添加 Rect Transform。", MessageType.None);
            }
            EditorGUILayout.PropertyField(serialized.FindProperty("headLock"), new GUIContent("头部固定"));
            EditorGUILayout.PropertyField(serialized.FindProperty("mirror"), new GUIContent("动作镜像"));
            EditorGUILayout.PropertyField(serialized.FindProperty("speed"), new GUIContent("动画速度调整"));
            if (serialized.FindProperty("mirror").boolValue || serialized.FindProperty("speed").boolValue)
                EditorGUILayout.HelpBox("总菜单的设置入口中控制。镜像适用于 Humanoid 动作；速度 0–2 倍，旋钮 50% 为原速、0% 为暂停。配乐随动作速度变化（音调也会随之变化）；舞蹈跟随者需自行保持相同镜像和速度设置。", MessageType.None);
            EditorGUILayout.PropertyField(serialized.FindProperty("sync"), new GUIContent("同步舞蹈"));
            if (serialized.FindProperty("sync").boolValue)
            {
                EditorGUILayout.PropertyField(serialized.FindProperty("syncPrefix"), new GUIContent("同步前缀"));
                EditorGUILayout.PropertyField(serialized.FindProperty("legacySyncNames"), new GUIContent("兼容旧同步命名（11–88）"));
                EditorGUILayout.HelpBox("双方需使用相同前缀和动作顺序。Kurasync 会生成 Kurasync1–Kurasync8；仅允许同步的分组参与发送和接收。", MessageType.None);
            }
            serialized.ApplyModifiedProperties();
            }
            if (page == 2)
            {
                serialized.ApplyModifiedProperties();
                DrawParameterPreview();
                EditorGUILayout.Space(12);
                EditorGUILayout.LabelField("by：王冠Kura");
                EditorGUILayout.LabelField("问题反馈群：993713826");
                EditorGUILayout.LabelField("版本：v1.0.0");
            }
            if (page == 0)
            {
            float panelWidth = Mathf.Clamp(groupPanelWidth, 220, Mathf.Max(220, position.width - 460));
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(panelWidth), GUILayout.ExpandHeight(true));
            EditorGUILayout.LabelField("菜单分组", EditorStyles.boldLabel);
            if (GUILayout.Button("从文件夹批量导入")) { serialized.ApplyModifiedProperties(); ActionBatchImportWindow.Open(profile); }
            groupScroll = EditorGUILayout.BeginScrollView(groupScroll);
            groups.DoLayoutList();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            DrawPanelSplitter();
            EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(440, position.width - panelWidth - 12)), GUILayout.ExpandHeight(true));
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (actions != null)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
                GUILayout.Label(profile.groups[selectedGroup].name, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(avatar == null))
                    if (GUILayout.Button("拍摄本组缩略图", EditorStyles.toolbarButton))
                    {
                        serialized.ApplyModifiedProperties();
                        QueueCapture(profile.groups[selectedGroup].actions.ToArray(), false);
                    }
                EditorGUILayout.EndHorizontal();
                actions.DoLayoutList();
                if (profile.groups.Count > 1)
                {
                    EditorGUILayout.BeginHorizontal();
                    moveTarget = EditorGUILayout.Popup("将选中动作移至", Mathf.Clamp(moveTarget, 0, profile.groups.Count - 1), profile.groups.Select(g => g.name).ToArray());
                    using (new EditorGUI.DisabledScope(actions.index < 0 || moveTarget == selectedGroup))
                    {
                        if (GUILayout.Button("移动", GUILayout.Width(60)))
                        {
                            serialized.ApplyModifiedProperties();
                            Undo.RecordObject(profile, "跨分组移动动作");
                            var from = profile.groups[selectedGroup].actions;
                            profile.groups[moveTarget].actions.Add(from[actions.index]);
                            from.RemoveAt(actions.index);
                            EditorUtility.SetDirty(profile);
                            Rebuild();
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
                var drop = GUILayoutUtility.GetRect(0, 48, GUILayout.ExpandWidth(true));
                GUI.Box(drop, "拖入动作或 FBX 添加到本组；拖入文件夹打开批量分组导入");
                HandleDrop(drop);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            }
            if (page == 1) DrawThumbnailPage();
            if (page != 0) EditorGUILayout.EndScrollView();
            serialized.ApplyModifiedProperties();
            int count = profile.groups.Sum(g => g.actions.Count);
            EditorGUILayout.LabelField("共 " + profile.groups.Count + " 个分组 / " + count + " 个动作");
            string error = ActionInstallerBuilder.Validate(profile, avatar, heightRoot);
            bool updating = avatar != null && avatar.transform.Find("KuraActions_" + profile.parameterPrefix) != null;
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);
            else if (updating) EditorGUILayout.HelpBox("已找到现有安装，可直接更新。按当前配置重建，安装内的手动修改不会保留；更新失败保留旧安装，成功后可用撤销恢复。", MessageType.Info);
            using (new EditorGUI.DisabledScope(error != null))
            {
                if (GUILayout.Button(updating ? "更新并覆盖现有安装" : "生成并安装到角色", GUILayout.Height(34)))
                {
                    try
                    {
                        ActionThumbnails.Generate(profile, avatar, profile.groups.SelectMany(g => g.actions), true);
                        ActionInstallerBuilder.Install(profile, avatar, heightRoot);
                        message = updating ? "更新完成。可通过 Unity 撤销恢复旧安装。请保存场景。" : "安装完成。生成物件已选中；可通过 Unity 撤销移除本次安装。请保存场景。";
                    }
                    catch (Exception e) { Debug.LogException(e); message = "安装失败：" + e.Message; }
                }
            }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        }

        void DrawPanelSplitter()
        {
            var rect = GUILayoutUtility.GetRect(6, 6, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(false));
            EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(.16f, .16f, .16f) : new Color(.65f, .65f, .65f));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
            int control = GUIUtility.GetControlID("KuraActionPanelSplitter".GetHashCode(), FocusType.Passive, rect);
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                GUIUtility.hotControl = control;
                groupPanelWidth = Mathf.Clamp(groupPanelWidth, 220, Mathf.Max(220, position.width - 460));
                if (e.clickCount == 2) { groupPanelWidth = 280; GUIUtility.hotControl = 0; }
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && GUIUtility.hotControl == control)
            {
                groupPanelWidth = Mathf.Clamp(groupPanelWidth + e.delta.x, 220, Mathf.Max(220, position.width - 460));
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseUp && GUIUtility.hotControl == control)
            { GUIUtility.hotControl = 0; e.Use(); }
        }

        void DrawThumbnailPage()
        {
            EditorGUILayout.LabelField("透明背景 · 人物自动居中", EditorStyles.boldLabel);
            if (profile.groups.Count == 0) { EditorGUILayout.HelpBox("先在动作编排页建立分组。", MessageType.Info); return; }
            int next = EditorGUILayout.Popup("当前分组", selectedGroup, profile.groups.Select(g => g.name).ToArray());
            if (next != selectedGroup) { selectedGroup = next; groups.index = next; BuildActions(); }
            var items = profile.groups[selectedGroup].actions;
            using (new EditorGUI.DisabledScope(avatar == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("拍摄本组", GUILayout.Height(26))) QueueCapture(items.ToArray(), false);
                if (GUILayout.Button("补齐全部缺失缩略图", GUILayout.Height(26))) QueueCapture(profile.groups.SelectMany(g => g.actions).ToArray(), true);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.LabelField("批量拍摄遵循“自动拍摄”勾选；单张重拍可覆盖当前图标。", EditorStyles.wordWrappedMiniLabel);
            int columns = Mathf.Max(1, Mathf.FloorToInt((position.width - 35) / 126));
            for (int i = 0; i < items.Count; i++)
            {
                if (i % columns == 0) EditorGUILayout.BeginHorizontal();
                var item = items[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(118));
                var rect = GUILayoutUtility.GetRect(106, 106, GUILayout.ExpandWidth(false));
                if (item.thumbnail != null) EditorGUI.DrawTextureTransparent(rect, item.thumbnail, ScaleMode.ScaleToFit);
                else GUI.Box(rect, "暂无缩略图");
                GUILayout.Label(item.name, EditorStyles.miniLabel, GUILayout.Width(106));
                using (new EditorGUI.DisabledScope(avatar == null || item.animation == null))
                    if (GUILayout.Button("重拍", GUILayout.Width(106))) QueueCapture(new[] { item }, false, true);
                EditorGUILayout.EndVertical();
                if (i % columns == columns - 1 || i == items.Count - 1) { GUILayout.FlexibleSpace(); EditorGUILayout.EndHorizontal(); }
            }
        }

        void DrawParameterPreview()
        {
            var parameters = ActionInstallerBuilder.RequiredParameters(profile);
            int bits = parameters.Where(p => !p.localOnly).Sum(p => p.syncType == nadena.dev.modular_avatar.core.ParameterSyncType.Bool ? 1 : 8);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("本插件参数占用：" + bits + " bit", EditorStyles.boldLabel);
        }

        void HandleDrop(Rect rect)
        {
            var ev = Event.current;
            if (!rect.Contains(ev.mousePosition) || (ev.type != EventType.DragUpdated && ev.type != EventType.DragPerform)) return;
            var directory = DragAndDrop.objectReferences.FirstOrDefault(o => AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(o))) as DefaultAsset;
            if (directory != null)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (ev.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); serialized.ApplyModifiedProperties(); ActionBatchImportWindow.Open(profile, directory); }
                ev.Use(); return;
            }
            var clips = DragAndDrop.objectReferences.SelectMany(o => o is AnimationClip ? new[] { (AnimationClip)o } :
                AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(o)).OfType<AnimationClip>())
                .Where(c => !c.name.StartsWith("__preview__")).Distinct().ToArray();
            if (clips.Length == 0) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (ev.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                serialized.ApplyModifiedProperties();
                Undo.RecordObject(profile, "批量添加动作");
                var group = profile.groups[selectedGroup];
                foreach (var clip in clips) group.actions.Add(new ActionEntry { name = clip.name, animation = clip });
                EditorUtility.SetDirty(profile);
                Rebuild();
                if (avatar != null) QueueCapture(group.actions.ToArray(), true);
            }
            ev.Use();
        }

        void QueueCapture(ActionEntry[] entries, bool missingOnly, bool manual = false)
        {
            var currentProfile = profile;
            var currentAvatar = avatar;
            EditorApplication.delayCall += () =>
            {
                if (this == null || currentProfile == null || currentAvatar == null) return;
                try
                {
                    ActionThumbnails.Generate(currentProfile, currentAvatar, entries, missingOnly, !manual);
                    message = "缩略图已生成，安装时将作为动作菜单图标。";
                }
                catch (Exception e) { message = "拍摄失败：" + e.Message; Debug.LogException(e); }
                Rebuild(); Repaint();
            };
        }

        public static Transform FindHeightRoot(GameObject go)
        {
            if (go == null) return null;
            var animator = go.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return null;
            var t = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (t == null) return null;
            while (t.parent != null && t.parent != go.transform) t = t.parent;
            return t;
        }
    }
}
