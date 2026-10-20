using UnityEditor;
using UnityEngine;

namespace Kura.ActionInstaller
{
    [CustomPropertyDrawer(typeof(ActionContinuation))]
    public sealed class ActionContinuationDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) { return 158; }
        public override void OnGUI(Rect rect, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(rect, label, property);
            rect.height = 18;
            EditorGUI.PropertyField(rect, property.FindPropertyRelative("animation"), new GUIContent("后续动画")); rect.y += 22;
            EditorGUI.PropertyField(rect, property.FindPropertyRelative("exitTime"), new GUIContent("上一段播放进度", "1 = 上一段播放完一次；0.95 = 播放到 95% 时开始过渡。")); rect.y += 22;
            var fixedTime = property.FindPropertyRelative("fixedDuration");
            EditorGUI.PropertyField(rect, property.FindPropertyRelative("duration"), new GUIContent(fixedTime.boolValue ? "过渡时长（秒）" : "过渡时长（比例）")); rect.y += 22;
            EditorGUI.PropertyField(rect, fixedTime, new GUIContent("过渡使用秒数")); rect.y += 22;
            EditorGUI.PropertyField(rect, property.FindPropertyRelative("offset"), new GUIContent("本段起始进度", "0 = 从头播放；0.5 = 从本段中间开始。")); rect.y += 22;
            EditorGUI.PropertyField(rect, property.FindPropertyRelative("speed"), new GUIContent("本段速度")); rect.y += 22;
            var loop = property.FindPropertyRelative("loopMode");
            loop.enumValueIndex = EditorGUI.Popup(rect, "本段循环", loop.enumValueIndex, new[] { "沿用动画", "循环", "单次" });
            EditorGUI.EndProperty();
        }
    }
}
