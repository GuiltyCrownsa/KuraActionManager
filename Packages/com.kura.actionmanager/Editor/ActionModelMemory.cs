using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Kura.ActionInstaller
{
    // Editor-only, project-local associations: never add components to avatars.
    [FilePath("ProjectSettings/KuraActionModelMemory.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class ActionModelMemory : ScriptableSingleton<ActionModelMemory>
    {
        [Serializable] class Entry { public string model; public string profile; }
        [SerializeField] List<Entry> entries = new List<Entry>();
        static readonly Dictionary<GameObject, ActionInstallProfile> session = new Dictionary<GameObject, ActionInstallProfile>();

        [InitializeOnLoadMethod]
        static void Initialize()
        {
            EditorSceneManager.sceneSaved -= SceneSaved;
            EditorSceneManager.sceneSaved += SceneSaved;
        }
        static void SceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (var pair in session.ToArray())
                if (pair.Key != null && pair.Key.scene == scene) Remember(pair.Key, pair.Value);
        }
        static string Key(GameObject model)
        {
            if (model == null || (!EditorUtility.IsPersistent(model) && string.IsNullOrEmpty(model.scene.path))) return null;
            return GlobalObjectId.GetGlobalObjectIdSlow(model).ToString();
        }
        public static ActionInstallProfile Recall(GameObject model)
        {
            if (model == null) return null;
            ActionInstallProfile cached;
            if (session.TryGetValue(model, out cached) && cached != null) return cached;
            var key = Key(model);
            var entry = key == null ? null : instance.entries.FirstOrDefault(e => e.model == key);
            return entry == null ? null : AssetDatabase.LoadAssetAtPath<ActionInstallProfile>(AssetDatabase.GUIDToAssetPath(entry.profile));
        }
        public static void Remember(GameObject model, ActionInstallProfile profile)
        {
            if (model == null || profile == null) return;
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(profile));
            if (string.IsNullOrEmpty(guid)) return;
            session[model] = profile;
            var key = Key(model);
            if (key == null) return;
            var entry = instance.entries.FirstOrDefault(e => e.model == key);
            if (entry != null && entry.profile == guid) return;
            if (entry == null) { entry = new Entry { model = key }; instance.entries.Add(entry); }
            entry.profile = guid;
            instance.Save(true);
        }
        public static ActionInstallProfile GetOrCreate(GameObject model)
        {
            if (model == null) return null;
            var existing = Recall(model);
            if (existing != null) return existing;
            string folder = KuraPaths.Ensure("Profiles");
            string name = model.name;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var profile = ScriptableObject.CreateInstance<ActionInstallProfile>();
            AssetDatabase.CreateAsset(profile, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + "_动作配置.asset"));
            Remember(model, profile);
            return profile;
        }
    }
}
