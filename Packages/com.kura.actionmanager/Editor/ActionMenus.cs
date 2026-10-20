using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Kura.ActionInstaller
{
    public static class ActionMenus
    {
        public static VRCExpressionsMenu.Control Sub(string name, VRCExpressionsMenu menu)
        {
            return new VRCExpressionsMenu.Control { name = name, type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                parameter = new VRCExpressionsMenu.Control.Parameter(), subMenu = menu };
        }

        public static VRCExpressionsMenu.Control Toggle(string name, string parameter, float value = 1)
        {
            return new VRCExpressionsMenu.Control { name = name, type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter }, value = value };
        }

        public static VRCExpressionsMenu.Control Radial(string name, string parameter)
        {
            return new VRCExpressionsMenu.Control { name = name, type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                parameter = new VRCExpressionsMenu.Control.Parameter(), subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = parameter } } };
        }

        // Every content page shares the settings submenu, immediately before Next.
        public static VRCExpressionsMenu Pages(string folder, string stem, List<VRCExpressionsMenu.Control> entries,
            VRCExpressionsMenu settings = null, bool settingsFirstPageOnly = false)
        {
            VRCExpressionsMenu first = null, previous = null;
            int offset = 0, page = 0;
            do
            {
                var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                menu.name = stem + "_" + (++page);
                AssetDatabase.CreateAsset(menu, folder + "/" + menu.name + ".asset");
                if (first == null) first = menu;
                if (previous != null) { previous.controls.Add(Sub("→", menu)); EditorUtility.SetDirty(previous); }
                int reserved = settings != null && (!settingsFirstPageOnly || page == 1) ? 1 : 0;
                int remaining = entries.Count - offset;
                int take = Math.Min(remaining, remaining > 8 - reserved ? 7 - reserved : 8 - reserved);
                menu.controls.AddRange(entries.GetRange(offset, take));
                offset += take;
                if (reserved != 0) menu.controls.Add(Sub("设置", settings));
                EditorUtility.SetDirty(menu);
                previous = menu;
            } while (offset < entries.Count);
            return first;
        }
    }
}
