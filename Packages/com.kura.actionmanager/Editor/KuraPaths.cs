using UnityEditor;

namespace Kura.ActionInstaller
{
    // Where the installer keeps its generated assets and user data.
    // Kept outside the package folder: package folders are read-only at runtime.
    internal static class KuraPaths
    {
        public const string Root = "Assets/KuraActionInstaller";

        public static void EnsureRoot()
        {
            if (AssetDatabase.IsValidFolder(Root)) return;
            if (!AssetDatabase.IsValidFolder("Assets")) return;
            AssetDatabase.CreateFolder("Assets", "KuraActionInstaller");
        }

        public static string Ensure(string child)
        {
            EnsureRoot();
            string path = Root + "/" + child;
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(Root, child);
            return path;
        }
    }
}
