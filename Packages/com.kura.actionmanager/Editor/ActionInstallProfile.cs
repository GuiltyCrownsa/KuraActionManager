using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kura.ActionInstaller
{
    public enum ActionLoopMode { FollowClip, Loop, Once }
    [Serializable]
    public class ActionContinuation
    {
        public AnimationClip animation;
        public float exitTime = 1;
        public float duration;
        public bool fixedDuration = true;
        public float offset;
        public float speed = 1;
        public ActionLoopMode loopMode;
    }
    [Serializable]
    public class ActionEntry
    {
        public string name = "新动作";
        public AnimationClip animation;
        public AudioClip music;
        public bool loopMusic = true;
        public Texture2D thumbnail;
        public bool autoThumbnail = true;
        public float thumbnailTime = .5f;
        public float baseSpeed = 1;
        public ActionLoopMode loopMode;
        // 0 follows menu order; -1 disables synchronization; positive values retain imported wire IDs.
        public int importedSyncId;
        public AnimationClip objectAnimation;
        public List<ActionContinuation> continuations = new List<ActionContinuation>();
    }

    [Serializable]
    public class ActionGroup
    {
        public string name = "新建分组";
        public bool allowSync;
        public int capacity = 15;
        public List<ActionEntry> actions = new List<ActionEntry>();
    }

    [Serializable]
    public class ActionParameterOverride
    {
        public string parameter = "";
        public float actionValue;
    }

    public class ActionInstallProfile : ScriptableObject
    {
        public string menuName = "动作";
        public string parameterPrefix = "KuraActions";
        public bool height = true;
        public bool sync = true;
        public bool headLock = true;
        public bool music = true;
        public bool mirror = true;
        public bool speed = true;
        public string syncPrefix = "Kurasync";
        public bool legacySyncNames;
        public float heightRange = 1;
        public float volume = .5f;
        public bool objectSwitches = true;
        public GameObject objectPrefab;
        public string objectSourcePrefix = "";
        public List<ActionParameterOverride> parameterOverrides = new List<ActionParameterOverride>();
        public List<ActionGroup> groups = new List<ActionGroup> { new ActionGroup() };
    }
}
