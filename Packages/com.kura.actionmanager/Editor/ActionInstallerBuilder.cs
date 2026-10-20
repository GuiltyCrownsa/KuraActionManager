using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Kura.ActionInstaller
{
    public sealed partial class ActionInstallerBuilder
    {
        ActionInstallProfile profile;
        List<ActionEntry> entries;
        List<bool> syncActions;
        string folder, prefix;
        GameObject root;
        string P(string name) { return prefix + "/" + name; }
        public static int SyncId(ActionEntry entry, int index) { return entry.importedSyncId == 0 ? index : entry.importedSyncId; }
        public static string SyncParameter(ActionInstallProfile p, int bit) { return p.syncPrefix + bit + (p.legacySyncNames ? bit.ToString() : ""); }

        public static string Validate(ActionInstallProfile p, GameObject avatar, Transform heightRoot)
        {
            if (p == null) return "请先新建或选择配置。";
            var overrideNames = new System.Collections.Generic.HashSet<string>();
            foreach (var item in p.parameterOverrides)
            {
                if (string.IsNullOrWhiteSpace(item.parameter) || item.parameter != item.parameter.Trim()) return "临时参数名称不能为空或带有首尾空格。";
                if (!overrideNames.Add(item.parameter)) return "临时参数重复：" + item.parameter;
                if (item.parameter.StartsWith(p.parameterPrefix + "/", StringComparison.Ordinal)) return "不能临时修改动作管理器自身参数：" + item.parameter;
                if (float.IsNaN(item.actionValue) || float.IsInfinity(item.actionValue)) return "临时参数值无效：" + item.parameter;
            }
            if (avatar == null || avatar.GetComponent<VRCAvatarDescriptor>() == null) return "请拖入含 Avatar Descriptor 的角色根物件。";
            var animator = avatar.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return "角色需要有效的 Humanoid Animator。";
            if (!Regex.IsMatch(p.parameterPrefix ?? "", @"^[A-Za-z][A-Za-z0-9_]*$")) return "安装标识只能包含英文字母、数字和下划线，并以字母开头。";
            if (p.sync && (!Regex.IsMatch(p.syncPrefix ?? "", @"^[A-Za-z][A-Za-z0-9_]*$"))) return "同步前缀只能包含英文字母、数字和下划线，并以字母开头。";
            if (string.IsNullOrWhiteSpace(p.menuName)) return "总菜单名称不能为空。";
            if (p.groups == null || p.groups.Count == 0) return "至少建立一个动作分组。";
            int count = 0;
            foreach (var group in p.groups)
            {
                if (string.IsNullOrWhiteSpace(group.name)) return "分组名称不能为空。";
                if (group.capacity < 1 || group.actions.Count > group.capacity) return "分组「" + group.name + "」超过容量，请提高容量或移出动作。";
                foreach (var e in group.actions)
                {
                    if (e.animation == null || string.IsNullOrWhiteSpace(e.name)) return "分组「" + group.name + "」有未填写的动作或名称。";
                    if (e.animation.legacy) return "动作「" + e.name + "」是 Legacy 动画，需先转换为 Humanoid 动画。";
                    if (e.baseSpeed < 0 || float.IsNaN(e.baseSpeed) || float.IsInfinity(e.baseSpeed)) return "动作「" + e.name + "」的原始速度无效。";
                    foreach (var part in e.continuations)
                        if (part.animation == null || part.animation.legacy || part.exitTime < 0 || part.duration < 0 || part.speed < 0 ||
                            float.IsNaN(part.exitTime + part.duration + part.offset + part.speed) || float.IsInfinity(part.exitTime + part.duration + part.offset + part.speed))
                            return "动作「" + e.name + "」的后续动画或切换参数无效。";
                    count++;
                }
            }
            if (count < 1 || count > 255) return "每套安装支持 1–255 个动作（8 路同步编码）。";
            if (p.height && (p.heightRange <= 0 || float.IsNaN(p.heightRange) || float.IsInfinity(p.heightRange))) return "高度范围需要是大于 0 的有限数值。";
            var matches = avatar.transform.Cast<Transform>().Where(t => t.name == "KuraActions_" + p.parameterPrefix).ToArray();
            if (matches.Length > 1) return "发现多个同名安装，请先整理重复物件。";
            if (matches.Length == 1 && !matches[0].GetComponents<ModularAvatarMergeAnimator>().Any(m => m.animator != null && AssetDatabase.GetAssetPath(m.animator).StartsWith("Assets/KuraActionInstaller/Generated/", StringComparison.Ordinal)))
                return "同名物件不是可识别的动作管理器安装，不能自动覆盖。";
            return null;
        }

        public static GameObject Install(ActionInstallProfile p, GameObject avatar, Transform heightRoot)
        {
            string error = Validate(p, avatar, heightRoot);
            if (error != null) throw new InvalidOperationException(error);
            if (EditorApplication.isPlaying) throw new InvalidOperationException("请退出播放模式后安装或更新。");
            var old = avatar.transform.Find("KuraActions_" + p.parameterPrefix);
            string oldName = old == null ? null : old.name;
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(old == null ? "安装动作管理器" : "更新动作管理器");
            var b = new ActionInstallerBuilder { profile = p, entries = p.groups.SelectMany(g => g.actions).ToList(), prefix = p.parameterPrefix };
            b.syncActions = p.groups.SelectMany(g => g.actions.Select(e => g.allowSync && e.importedSyncId >= 0)).ToList();
            string output = KuraPaths.Ensure("Generated");
            b.folder = AssetDatabase.GenerateUniqueAssetPath(output + "/" + p.parameterPrefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AssetDatabase.CreateFolder(output, System.IO.Path.GetFileName(b.folder));
            b.root = new GameObject("KuraActions_" + p.parameterPrefix);
            Undo.RegisterCreatedObjectUndo(b.root, "安装动作菜单");
            b.root.transform.SetParent(avatar.transform, false);
            try
            {
                if (old != null)
                {
                    Undo.RecordObject(old.gameObject, "暂存旧动作安装");
                    old.name = oldName + "_Previous_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    b.root.transform.localPosition = old.localPosition;
                    b.root.transform.localRotation = old.localRotation;
                    b.root.transform.localScale = old.localScale;
                    b.root.transform.SetSiblingIndex(old.GetSiblingIndex());
                    b.root.SetActive(old.gameObject.activeSelf);
                }
                b.PrepareObjects(avatar);
                b.Parameters();
                b.Menus();
                if (p.sync) b.Contacts();
                b.Resolver();
                b.ParameterOverrides();
                b.Body();
                b.ObjectEffects();
                if (p.music) b.Music();
                if (p.sync) b.Senders();
                var syncReport = new System.Text.StringBuilder("同步前缀：" + p.syncPrefix + "\nID\t分组\t动作\t编码\n");
                int syncId = 0;
                foreach (var group in p.groups)
                    foreach (var entry in group.actions)
                    {
                        syncId++;
                        if (p.sync && group.allowSync && entry.importedSyncId >= 0) syncReport.AppendLine(SyncId(entry, syncId) + "\t" + group.name + "\t" + entry.name + "\t" + Convert.ToString(SyncId(entry, syncId), 2).PadLeft(8, '0'));
                    }
                System.IO.File.WriteAllText(b.folder + "/同步ID对照表.txt", syncReport.ToString());
                AssetDatabase.ImportAsset(b.folder + "/同步ID对照表.txt");
                foreach (string guid in AssetDatabase.FindAssets("", new[] { b.folder }))
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)))
                        if (asset != null) EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                // Commit only after all generation and asset writes succeed.
                if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
                Undo.CollapseUndoOperations(undoGroup);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.scene);
                Selection.activeGameObject = b.root;
                return b.root;
            }
            catch
            {
                Undo.DestroyObjectImmediate(b.root);
                if (old != null) old.name = oldName;
                // Only this installation's newly created output folder is rolled back.
                AssetDatabase.DeleteAsset(b.folder);
                throw;
            }
        }

        void Parameters()
        {
            var ma = root.AddComponent<ModularAvatarParameters>();
            ma.parameters.AddRange(RequiredParameters(profile));
        }

        // The preview and generated MA component use the same parameter definitions.
        public static List<ParameterConfig> RequiredParameters(ActionInstallProfile p)
        {
            var result = new List<ParameterConfig>();
            string prefix = p.parameterPrefix + "/";
            AddParameter(result, prefix + "Selected", ParameterSyncType.Int, 0, false, true);
            AddParameter(result, prefix + "Active", ParameterSyncType.Int);
            if (p.music) AddParameter(result, prefix + "Music", ParameterSyncType.Bool, 1, true);
            if (p.headLock) AddParameter(result, prefix + "HeadLock", ParameterSyncType.Bool, 1, true);
            if (p.height) AddParameter(result, prefix + "Height", ParameterSyncType.Float, .5f);
            if (p.mirror) AddParameter(result, prefix + "Mirror", ParameterSyncType.Bool);
            if (p.speed) AddParameter(result, prefix + "Speed", ParameterSyncType.Float, .5f);
            if (p.sync)
            {
                AddParameter(result, prefix + "Transmit", ParameterSyncType.Bool);
                AddParameter(result, prefix + "Sync", ParameterSyncType.Bool, 0, true);
                for (int i = 1; i <= 8; i++) AddParameter(result, SyncParameter(p, i), ParameterSyncType.Bool, 0, false, true);
            }
            return result;
        }

        static void AddParameter(List<ParameterConfig> parameters, string name, ParameterSyncType type, float value = 0, bool saved = false, bool local = false)
        {
            parameters.Add(new ParameterConfig { nameOrPrefix = name, syncType = type, defaultValue = value,
                hasExplicitDefaultValue = true, saved = saved, localOnly = local });
        }

        void Menus()
        {
            var settings = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            settings.name = "设置";
            if (profile.music) settings.controls.Add(ActionMenus.Toggle("音乐", P("Music")));
            if (profile.height) settings.controls.Add(ActionMenus.Radial("动作高度", P("Height")));
            if (profile.sync) settings.controls.Add(ActionMenus.Toggle("同步舞蹈", P("Sync")));
            if (profile.headLock) settings.controls.Add(ActionMenus.Toggle("头部固定", P("HeadLock")));
            if (profile.mirror) settings.controls.Add(ActionMenus.Toggle("动作镜像", P("Mirror")));
            if (profile.speed) settings.controls.Add(ActionMenus.Radial("动作速度（50%=原速）", P("Speed")));
            AssetDatabase.CreateAsset(settings, folder + "/Settings.asset");
            var groups = new List<VRCExpressionsMenu.Control>();
            int id = 0, groupIndex = 0;
            foreach (var group in profile.groups)
            {
                var items = group.actions.Select(e => { var control = ActionMenus.Toggle(e.name, P("Selected"), ++id); control.icon = e.thumbnail; return control; }).ToList();
                groups.Add(ActionMenus.Sub(group.name, ActionMenus.Pages(folder, "Group" + (++groupIndex), items)));
            }
            var top = ActionMenus.Pages(folder, "Main", groups, settings, true);
            var install = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            install.name = profile.menuName;
            install.controls.Add(ActionMenus.Sub(profile.menuName, top));
            AssetDatabase.CreateAsset(install, folder + "/InstallMenu.asset");
            root.AddComponent<ModularAvatarMenuInstaller>().menuToAppend = install;
        }

        AnimatorController Controller(string name, VRCAvatarDescriptor.AnimLayerType type, bool absolute = false)
        {
            var c = AnimatorController.CreateAnimatorControllerAtPath(folder + "/" + name + ".controller");
            c.layers = new AnimatorControllerLayer[0];
            Param(c, P("Active"), AnimatorControllerParameterType.Int);
            var ma = root.AddComponent<ModularAvatarMergeAnimator>();
            ma.animator = c;
            ma.layerType = type;
            ma.pathMode = absolute ? MergeAnimatorPathMode.Absolute : MergeAnimatorPathMode.Relative;
            ma.matchAvatarWriteDefaults = true;
            ma.deleteAttachedAnimator = true;
            return c;
        }

        static void Param(AnimatorController c, string name, AnimatorControllerParameterType type, float value = 0)
        {
            if (c.parameters.Any(p => p.name == name)) return;
            c.AddParameter(new AnimatorControllerParameter { name = name, type = type,
                defaultBool = value != 0, defaultFloat = value, defaultInt = (int)value });
        }

        static AnimatorStateMachine Layer(AnimatorController c, string name)
        {
            c.AddLayer(name);
            var ls = c.layers;
            ls[ls.Length - 1].defaultWeight = 1;
            c.layers = ls;
            return ls[ls.Length - 1].stateMachine;
        }

        static AnimatorState State(AnimatorStateMachine sm, string name, Motion clip = null)
        {
            var s = sm.AddState(name);
            s.motion = clip;
            s.writeDefaultValues = false;
            return s;
        }

        static AnimatorStateTransition Any(AnimatorStateMachine sm, AnimatorState s)
        {
            var t = sm.AddAnyStateTransition(s);
            t.hasExitTime = false;
            t.duration = 0;
            t.canTransitionToSelf = false;
            return t;
        }
        static void Eq(AnimatorStateTransition t, string p, int value) { t.AddCondition(AnimatorConditionMode.Equals, value, p); }
        static void Bool(AnimatorStateTransition t, string p, bool value) { t.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0, p); }

        AnimationClip Clip(string name)
        {
            var clip = new AnimationClip { name = name, frameRate = 60 };
            AssetDatabase.CreateAsset(clip, folder + "/" + name + ".anim");
            return clip;
        }
        static void Curve(AnimationClip clip, string path, Type type, string property, float value)
        {
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 1f / 60, value));
            EditorUtility.SetDirty(clip);
        }
        void Driver(AnimatorState state, int value)
        {
            var d = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            d.localOnly = true;
            d.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = P("Active"), type = VRC_AvatarParameterDriver.ChangeType.Set, value = value });
        }

        void Resolver()
        {
            var c = Controller("动作选择", VRCAvatarDescriptor.AnimLayerType.FX);
            Param(c, "IsLocal", AnimatorControllerParameterType.Bool);
            Param(c, P("Selected"), AnimatorControllerParameterType.Int);
            if (profile.sync)
            {
                Param(c, P("Sync"), AnimatorControllerParameterType.Bool);
                for (int i = 1; i <= 8; i++) Param(c, SyncParameter(profile, i), AnimatorControllerParameterType.Bool);
            }
            var sm = Layer(c, "选择与同步接收");
            if (profile.sync)
            {
                Param(c, P("Transmit"), AnimatorControllerParameterType.Bool);
                var origin = Layer(c, "主动发起标记");
                for (int enabled = 0; enabled <= 1; enabled++)
                {
                    var s = State(origin, enabled == 0 ? "跟随或停止" : "主动播放");
                    var driver = s.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                    driver.localOnly = true;
                    driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = P("Transmit"), type = VRC_AvatarParameterDriver.ChangeType.Set, value = enabled });
                    var t = Any(origin, s);
                    Bool(t, "IsLocal", true);
                    if (enabled == 0) Eq(t, P("Selected"), 0);
                    else t.AddCondition(AnimatorConditionMode.Greater, 0, P("Selected"));
                }
            }
            var idle = State(sm, "停止");
            Driver(idle, 0);
            for (int i = 1; i <= entries.Count; i++)
            {
                var state = State(sm, i + " " + entries[i - 1].name);
                Driver(state, i);
                var manual = Any(sm, state);
                Bool(manual, "IsLocal", true);
                Eq(manual, P("Selected"), i);
                if (profile.sync && syncActions[i - 1])
                {
                    var follow = Any(sm, state);
                    Bool(follow, "IsLocal", true);
                    Eq(follow, P("Selected"), 0);
                    Bool(follow, P("Sync"), true);
                    Bits(follow, SyncId(entries[i - 1], i));
                }
            }
            var stop = Any(sm, idle);
            Bool(stop, "IsLocal", true);
            Eq(stop, P("Selected"), 0);
            if (profile.sync)
            {
                Bool(stop, P("Sync"), false);
                // Unassigned codes must clear the previous action too.
                for (int code = 0; code <= 255; code++)
                {
                    if (entries.Where((e, index) => syncActions[index] && SyncId(e, index + 1) == code).Any()) continue;
                    var invalid = Any(sm, idle);
                    Bool(invalid, "IsLocal", true);
                    Eq(invalid, P("Selected"), 0);
                    Bool(invalid, P("Sync"), true);
                    Bits(invalid, code);
                }
            }
        }
        void Bits(AnimatorStateTransition t, int value)
        {
            for (int b = 0; b < 8; b++) Bool(t, SyncParameter(profile, b + 1), (value & (1 << b)) != 0);
        }

        static void Tracking(AnimatorState s, bool playing, bool includeHead)
        {
            var b = s.AddStateMachineBehaviour<VRCAnimatorTrackingControl>();
            var t = playing ? VRC_AnimatorTrackingControl.TrackingType.Animation : VRC_AnimatorTrackingControl.TrackingType.Tracking;
            b.trackingLeftHand = b.trackingRightHand = b.trackingHip = b.trackingLeftFoot = b.trackingRightFoot = t;
            b.trackingLeftFingers = b.trackingRightFingers = t;
            if (includeHead) b.trackingHead = t;
        }
        static void Weight(AnimatorState s, float weight)
        {
            var b = s.AddStateMachineBehaviour<VRCPlayableLayerControl>();
            b.layer = VRC_PlayableLayerControl.BlendableLayer.Action;
            b.goalWeight = weight;
            b.blendDuration = .15f;
        }
        void Body()
        {
            var c = Controller("动画", VRCAvatarDescriptor.AnimLayerType.Action, true);
            if (profile.height) Param(c, P("Height"), AnimatorControllerParameterType.Float, .5f);
            if (profile.mirror) Param(c, P("Mirror"), AnimatorControllerParameterType.Bool);
            if (profile.speed) Param(c, P("Speed"), AnimatorControllerParameterType.Float, .5f);
            var sm = Layer(c, "角色动作");
            var idle = State(sm, "停止");
            Tracking(idle, false, !profile.headLock);
            Weight(idle, 0);
            Param(c, P("BodyCurrent"), AnimatorControllerParameterType.Int);
            RememberAction(idle, "BodyCurrent", 0);
            EnterAction(sm, idle, "BodyCurrent", 0);
            for (int i = 1; i <= entries.Count; i++)
            {
                var entry = entries[i - 1];
                var s = State(sm, i + " " + entry.name, HeightMotion(BodyClip(entry.animation, "Body" + i, entry.loopMode), "HeightBody" + i));
                if (profile.mirror) { s.mirrorParameterActive = true; s.mirrorParameter = P("Mirror"); }
                if (profile.speed) { s.speed = 2; s.speedParameterActive = true; s.speedParameter = P("Speed"); }
                s.speed = entries[i - 1].baseSpeed * (profile.speed ? 2 : 1);
                Tracking(s, true, false);
                Weight(s, 1);
                RememberAction(s, "BodyCurrent", i);
                EnterAction(sm, s, "BodyCurrent", i);
                var previous = s;
                int phase = 0;
                foreach (var part in entry.continuations)
                {
                    var next = State(sm, i + " " + entry.name + " / " + (++phase), HeightMotion(BodyClip(part.animation, "Body" + i + "_" + phase, part.loopMode), "HeightBody" + i + "_" + phase));
                    ConfigureSpeed(next, part.speed);
                    if (profile.mirror) { next.mirrorParameterActive = true; next.mirrorParameter = P("Mirror"); }
                    ContinueAction(previous, next, part, i);
                    previous = next;
                }
            }
            if (!profile.headLock) return;
            Param(c, P("HeadLock"), AnimatorControllerParameterType.Bool, 1);
            var heads = Layer(c, "头部固定");
            var mask = new AvatarMask { name = "仅控制跟踪状态" };
            for (int part = 0; part < (int)AvatarMaskBodyPart.LastBodyPart; part++) mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)part, false);
            mask.transformCount = 0;
            AssetDatabase.CreateAsset(mask, folder + "/TrackingOnly.mask");
            var layers = c.layers;
            layers[layers.Length - 1].avatarMask = mask;
            c.layers = layers;
            var free = State(heads, "自由头部");
            free.AddStateMachineBehaviour<VRCAnimatorTrackingControl>().trackingHead = VRC_AnimatorTrackingControl.TrackingType.Tracking;
            var locked = State(heads, "固定头部");
            locked.AddStateMachineBehaviour<VRCAnimatorTrackingControl>().trackingHead = VRC_AnimatorTrackingControl.TrackingType.Animation;
            Eq(Any(heads, free), P("Active"), 0);
            Bool(Any(heads, free), P("HeadLock"), false);
            var t = Any(heads, locked);
            t.AddCondition(AnimatorConditionMode.Greater, 0, P("Active"));
            Bool(t, P("HeadLock"), true);
        }

        void Music()
        {
            var parent = new GameObject("Music");
            parent.transform.SetParent(root.transform, false);
            var audio = parent.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.volume = Mathf.Clamp01(profile.volume);
            audio.spatialBlend = 1;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = 1;
            audio.maxDistance = 1.8f;
            audio.dopplerLevel = 0;
            var c = Controller("动画 1", VRCAvatarDescriptor.AnimLayerType.FX);
            if (profile.speed) Param(c, P("Speed"), AnimatorControllerParameterType.Float, .5f);
            var playback = Layer(c, "动作配乐播放");
            for (int i = 0; i <= entries.Count; i++)
            {
                var state = State(playback, i == 0 ? "停止" : i + " " + entries[i - 1].name);
                var pitch = Clip("MusicSpeed" + i);
                float baseSpeed = i == 0 ? 1 : entries[i - 1].baseSpeed;
                AnimationUtility.SetEditorCurve(pitch, EditorCurveBinding.FloatCurve("Music", typeof(AudioSource), "m_Pitch"),
                    profile.speed ? AnimationCurve.Linear(0, 0, 1, 2 * baseSpeed) : AnimationCurve.Constant(0, 1, baseSpeed));
                state.motion = pitch;
                if (profile.speed) { state.timeParameterActive = true; state.timeParameter = P("Speed"); }
                PlayAudio(state, i == 0 ? null : entries[i - 1]);
                Eq(Any(playback, state), P("Active"), i);
            }
            Param(c, P("Music"), AnimatorControllerParameterType.Bool, 1);
            var volume = Layer(c, "音乐开关");
            for (int enabled = 1; enabled >= 0; enabled--)
            {
                var clip = Clip("MusicVolume" + enabled);
                Curve(clip, "Music", typeof(AudioSource), "m_Volume", enabled == 1 ? profile.volume : 0);
                var s = State(volume, enabled == 1 ? "开" : "关", clip);
                Bool(Any(volume, s), P("Music"), enabled == 1);
            }
        }

        void PlayAudio(AnimatorState state, ActionEntry entry)
        {
            if (entry != null && entry.music != null)
            {
                var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(entry.music)) as AudioImporter;
                // SDK requires background loading for Decompress On Load clips in state behaviours.
                if (importer != null && !importer.loadInBackground)
                {
                    importer.loadInBackground = true;
                    importer.SaveAndReimport();
                }
            }
            var play = state.AddStateMachineBehaviour<VRCAnimatorPlayAudio>();
            play.SourcePath = root.name + "/Music";
            play.PlaybackOrder = VRC_AnimatorPlayAudio.Order.Roundabout;
            play.Clips = entry != null && entry.music != null ? new[] { entry.music } : new AudioClip[0];
            play.ClipsApplySettings = VRC_AnimatorPlayAudio.ApplySettings.ApplyIfStopped;
            play.Volume = Vector2.one;
            // The separate FX music switch owns volume, including while a new action starts.
            play.VolumeApplySettings = VRC_AnimatorPlayAudio.ApplySettings.NeverApply;
            play.Pitch = Vector2.one;
            // Animator owns pitch so starting a track never resets the speed control.
            play.PitchApplySettings = VRC_AnimatorPlayAudio.ApplySettings.NeverApply;
            play.Loop = entry != null && entry.loopMusic;
            play.LoopApplySettings = VRC_AnimatorPlayAudio.ApplySettings.ApplyIfStopped;
            play.StopOnEnter = true;
            play.PlayOnEnter = entry != null && entry.music != null;
            play.StopOnExit = true;
            play.PlayOnExit = false;
            play.DelayInSeconds = 0;
        }

        void Contacts()
        {
            var parent = new GameObject("Contacts");
            parent.transform.SetParent(root.transform, false);
            for (int i = 1; i <= 8; i++)
            {
                var s = new GameObject("SD" + i + "S");
                s.transform.SetParent(parent.transform, false);
                var sender = s.AddComponent<VRCContactSender>();
                sender.radius = 100;
                sender.collisionTags = new List<string> { SyncParameter(profile, i) };
                s.SetActive(false);
                var r = new GameObject("SD" + i + "R");
                r.transform.SetParent(parent.transform, false);
                var receiver = r.AddComponent<VRCContactReceiver>();
                receiver.radius = .5f;
                receiver.allowSelf = false;
                receiver.allowOthers = true;
                receiver.parameter = SyncParameter(profile, i);
                receiver.collisionTags = new List<string> { SyncParameter(profile, i) };
            }
            parent.SetActive(false);
        }

        void Senders()
        {
            var c = Controller("Sync_FX", VRCAvatarDescriptor.AnimLayerType.FX);
            // Only Active and the one-bit origin marker need network synchronization.
            // Remote copies transmit manually selected dances, never received dances.
            Param(c, P("Transmit"), AnimatorControllerParameterType.Bool);
            Param(c, P("Sync"), AnimatorControllerParameterType.Bool);
            Param(c, "IsLocal", AnimatorControllerParameterType.Bool);
            var sm = Layer(c, "同步发送");
            for (int action = 0; action <= entries.Count; action++)
            {
                var clip = Clip("SyncSend" + action);
                int transmitted = action > 0 && syncActions[action - 1] ? SyncId(entries[action - 1], action) : 0;
                for (int bit = 0; bit < 8; bit++) Curve(clip, "Contacts/SD" + (bit + 1) + "S", typeof(GameObject), "m_IsActive", (transmitted & (1 << bit)) != 0 ? 1 : 0);
                var s = State(sm, action.ToString(), clip);
                var t = Any(sm, s);
                Eq(t, P("Active"), action);
                if (action > 0) Bool(t, P("Transmit"), true);
                else Bool(Any(sm, s), P("Transmit"), false);
            }
            var contacts = Layer(c, "同步开关");
            for (int enabled = 0; enabled <= 1; enabled++)
            {
                var clip = Clip("SyncEnabled" + enabled);
                Curve(clip, "Contacts", typeof(GameObject), "m_IsActive", enabled);
                var s = State(contacts, enabled == 0 ? "关" : "开", clip);
                Bool(Any(contacts, s), P("Sync"), enabled == 1);
            }
        }
    }
}
