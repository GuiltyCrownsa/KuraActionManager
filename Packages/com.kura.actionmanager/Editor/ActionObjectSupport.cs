using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Kura.ActionInstaller
{
    public sealed partial class ActionInstallerBuilder
    {
        Transform avatarRoot, objects;
        readonly Dictionary<string, float> objectDefaults = new Dictionary<string, float>();
        readonly HashSet<string> missingObjects = new HashSet<string>();

        void PrepareObjects(GameObject avatar)
        {
            avatarRoot = avatar.transform;
            if (profile.objectPrefab != null && profile.objectSwitches)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(profile.objectPrefab, root.transform);
                instance.name = "Objects";
                objects = instance.transform;
            }
        }
        public static bool IsObjectSwitch(EditorCurveBinding b) { return b.type == typeof(GameObject) && b.propertyName == "m_IsActive"; }
        string ResolveObject(string path)
        {
            string relative = path;
            string prefix = (profile.objectSourcePrefix ?? "").Trim('/');
            bool oldPrefix = prefix.Length > 0 && path.StartsWith(prefix + "/", StringComparison.Ordinal);
            if (oldPrefix) relative = path.Substring(prefix.Length + 1);
            Transform target = objects == null || relative.Length == 0 ? null : objects.Find(relative);
            // Never fall back to an old installation for an explicitly migrated prop.
            if (target == null && !oldPrefix && path.Length > 0) target = avatarRoot.Find(path);
            if (target == null || target == avatarRoot || target == root.transform || root.transform.IsChildOf(target))
            {
                missingObjects.Add(path); return null;
            }
            string resolved = AnimationUtility.CalculateTransformPath(target, avatarRoot);
            if (!objectDefaults.ContainsKey(resolved)) objectDefaults[resolved] = target.gameObject.activeSelf ? 1 : 0;
            return resolved;
        }
        AnimationClip BodyClip(AnimationClip source, string name, ActionLoopMode loopMode = ActionLoopMode.FollowClip)
        {
            if (source == null) return null;
            var switches = AnimationUtility.GetCurveBindings(source).Where(IsObjectSwitch).ToArray();
            var prefix = (profile.objectSourcePrefix ?? "").Trim('/') + "/";
            bool remap = objects != null && AnimationUtility.GetCurveBindings(source).Any(b => b.path.StartsWith(prefix, StringComparison.Ordinal));
            if (switches.Length == 0 && !remap && loopMode == ActionLoopMode.FollowClip) return source;
            var copy = UnityEngine.Object.Instantiate(source); copy.name = name;
            foreach (var binding in AnimationUtility.GetCurveBindings(copy))
            {
                if (IsObjectSwitch(binding)) { AnimationUtility.SetEditorCurve(copy, binding, null); continue; }
                if (objects != null && binding.path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var target = objects.Find(binding.path.Substring(prefix.Length));
                    var curve = AnimationUtility.GetEditorCurve(copy, binding);
                    AnimationUtility.SetEditorCurve(copy, binding, null);
                    if (target != null)
                    {
                        var mapped = binding; mapped.path = AnimationUtility.CalculateTransformPath(target, avatarRoot);
                        AnimationUtility.SetEditorCurve(copy, mapped, curve);
                    }
                }
            }
            ApplyLoop(copy, loopMode);
            AssetDatabase.CreateAsset(copy, folder + "/" + name + ".anim"); return copy;
        }
        static void ApplyLoop(AnimationClip clip, ActionLoopMode mode)
        {
            if (mode == ActionLoopMode.FollowClip) return;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = mode == ActionLoopMode.Loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
        }
        void RememberAction(AnimatorState state, string parameter, int value)
        {
            var d = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            // Controller-only parameters are not expression/network parameters; each client tracks its own state.
            d.localOnly = false;
            d.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = P(parameter), type = VRC_AvatarParameterDriver.ChangeType.Set, value = value });
        }
        void EnterAction(AnimatorStateMachine sm, AnimatorState state, string parameter, int id)
        {
            var t = Any(sm, state); Eq(t, P("Active"), id);
            t.AddCondition(AnimatorConditionMode.NotEqual, id, P(parameter));
        }
        void ConfigureSpeed(AnimatorState state, float speed)
        {
            state.speed = speed * (profile.speed ? 2 : 1);
            if (profile.speed) { state.speedParameterActive = true; state.speedParameter = P("Speed"); }
        }
        void ContinueAction(AnimatorState previous, AnimatorState next, ActionContinuation part, int id)
        {
            var t = previous.AddTransition(next); t.hasExitTime = true; t.exitTime = part.exitTime;
            t.duration = part.duration; t.hasFixedDuration = part.fixedDuration; t.offset = part.offset;
            t.interruptionSource = TransitionInterruptionSource.SourceThenDestination;
            Eq(t, P("Active"), id);
        }
        Dictionary<string, AnimationCurve> SwitchCurves(params AnimationClip[] clips)
        {
            var result = new Dictionary<string, AnimationCurve>();
            foreach (var source in clips.Where(c => c != null))
                foreach (var b in AnimationUtility.GetCurveBindings(source).Where(IsObjectSwitch))
                {
                    var path = ResolveObject(b.path);
                    if (path != null) result[path] = AnimationUtility.GetEditorCurve(source, b);
                }
            return result;
        }
        AnimationClip EffectClip(string name, AnimationClip body, Dictionary<string, AnimationCurve> curves, Dictionary<string, float> carry)
        {
            var clip = Clip(name); float length = body == null ? 1f / 60 : Mathf.Max(body.length, 1f / 60);
            foreach (var baseline in objectDefaults)
            {
                AnimationCurve curve;
                if (!curves.TryGetValue(baseline.Key, out curve)) curve = AnimationCurve.Constant(0, length, carry.ContainsKey(baseline.Key) ? carry[baseline.Key] : baseline.Value);
                curve = new AnimationCurve(curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
                if (curve.length > 0 && curve.keys[curve.length - 1].time < length) curve.AddKey(new Keyframe(length, curve.Evaluate(length), float.PositiveInfinity, float.PositiveInfinity));
                // m_IsActive must use Unity's float binding. DiscreteCurve samples in
                // the editor but does not drive GameObject activation in a playable.
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(baseline.Key, typeof(GameObject), "m_IsActive"), curve);
                carry[baseline.Key] = curve.Evaluate(length);
            }
            if (body != null)
            {
                var settings = AnimationUtility.GetAnimationClipSettings(body);
                settings.startTime = 0; settings.stopTime = length;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }
            return clip;
        }
        void ObjectEffects()
        {
            if (!profile.objectSwitches) return;
            var phases = entries.Select(e => new[] { SwitchCurves(e.objectAnimation, e.animation) }
                .Concat(e.continuations.Select(p => SwitchCurves(p.animation))).ToArray()).ToArray();
            if (missingObjects.Count > 0)
                System.IO.File.WriteAllText(folder + "/未匹配物件.txt", "以下动画物件路径不存在，未生成这些开关：\n" + string.Join("\n", missingObjects.OrderBy(x => x).ToArray()));
            if (objectDefaults.Count == 0) return;
            var c = Controller("物件开关", VRCAvatarDescriptor.AnimLayerType.FX, true);
            Param(c, P("ObjectCurrent"), AnimatorControllerParameterType.Int);
            if (profile.speed) Param(c, P("Speed"), AnimatorControllerParameterType.Float, .5f);
            var sm = Layer(c, "动作物件开关");
            var idle = State(sm, "停止", EffectClip("ObjectsReset", null, new Dictionary<string, AnimationCurve>(), new Dictionary<string, float>()));
            RememberAction(idle, "ObjectCurrent", 0);
            // All actions without prop curves share the reset state. A single
            // AND transition excludes only the action IDs which own prop curves.
            var affected = Enumerable.Range(0, entries.Count).Where(i => phases[i].Any(p => p.Count > 0)).ToArray();
            var reset = Any(sm, idle);
            foreach (int i in affected) reset.AddCondition(AnimatorConditionMode.NotEqual, i + 1, P("Active"));
            for (int i = 0; i < entries.Count; i++)
            {
                if (!affected.Contains(i)) continue;
                var entry = entries[i]; var carry = new Dictionary<string, float>();
                var first = State(sm, (i + 1) + " " + entry.name, EffectClip("Objects" + (i + 1), entry.animation, phases[i][0], carry));
                ApplyLoop((AnimationClip)first.motion, entry.loopMode);
                ConfigureSpeed(first, entry.baseSpeed); RememberAction(first, "ObjectCurrent", i + 1); EnterAction(sm, first, "ObjectCurrent", i + 1);
                var previous = first;
                for (int phase = 0; phase < entry.continuations.Count; phase++)
                {
                    var part = entry.continuations[phase];
                    var next = State(sm, (i + 1) + " / " + (phase + 1), EffectClip("Objects" + (i + 1) + "_" + (phase + 1), part.animation, phases[i][phase + 1], carry));
                    ApplyLoop((AnimationClip)next.motion, part.loopMode);
                    ConfigureSpeed(next, part.speed); ContinueAction(previous, next, part, i + 1); previous = next;
                }
            }
        }
    }
}
