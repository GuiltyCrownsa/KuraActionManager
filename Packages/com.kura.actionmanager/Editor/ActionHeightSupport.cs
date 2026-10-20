using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Kura.ActionInstaller
{
    public sealed partial class ActionInstallerBuilder
    {
        Motion HeightMotion(AnimationClip source, string name)
        {
            if (!profile.height || source == null) return source;
            var tree = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = P("Height"), useAutomaticThresholds = false };
            AssetDatabase.CreateAsset(tree, folder + "/" + name + ".asset");
            for (int endpoint = 0; endpoint < 2; endpoint++)
            {
                var clip = Object.Instantiate(source);
                clip.name = name + (endpoint == 0 ? "_Down" : "_Up");
                float offset = (endpoint == 0 ? -1 : 1) * profile.heightRange;
                var motionY = AnimationUtility.GetCurveBindings(clip).Where(b => b.type == typeof(Animator) && b.propertyName == "MotionT.y").ToArray();
                if (motionY.Length > 0)
                {
                    foreach (var binding in motionY)
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        var keys = curve.keys;
                        for (int k = 0; k < keys.Length; k++) keys[k].value += offset;
                        curve.keys = keys;
                        AnimationUtility.SetEditorCurve(clip, binding, curve);
                    }
                }
                else
                {
                    var settings = AnimationUtility.GetAnimationClipSettings(clip);
                    settings.level -= offset;
                    AnimationUtility.SetAnimationClipSettings(clip, settings);
                }
                AssetDatabase.CreateAsset(clip, folder + "/" + clip.name + ".anim");
                tree.AddChild(clip, endpoint);
            }
            EditorUtility.SetDirty(tree);
            return tree;
        }
    }
}
