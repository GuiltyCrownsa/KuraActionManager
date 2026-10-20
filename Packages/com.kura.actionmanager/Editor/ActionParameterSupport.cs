using UnityEditor.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Kura.ActionInstaller
{
    public sealed partial class ActionInstallerBuilder
    {
        void ParameterOverrides()
        {
            if (profile.parameterOverrides.Count == 0) return;
            var controller = Controller("动作临时参数", VRCAvatarDescriptor.AnimLayerType.FX);
            var sm = Layer(controller, "动作期间临时参数");
            var idle = State(sm, "等待动作");
            var capture = State(sm, "记录原值");
            var active = State(sm, "动作期间");
            var restore = State(sm, "恢复原值");
            sm.defaultState = idle;
            var save = capture.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            var apply = active.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            var reset = restore.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            // Each client preserves its own pre-action value. These buffers are
            // Animator-only; no expression parameters or network bits are added.
            save.localOnly = apply.localOnly = reset.localOnly = false;
            for (int i = 0; i < profile.parameterOverrides.Count; i++)
            {
                var item = profile.parameterOverrides[i];
                var buffer = P("SavedExternal" + i);
                Param(controller, buffer, UnityEngine.AnimatorControllerParameterType.Float);
                save.parameters.Add(new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Copy, source = item.parameter, name = buffer });
                apply.parameters.Add(new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = item.parameter, value = item.actionValue });
                reset.parameters.Add(new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Copy, source = buffer, name = item.parameter });
            }
            // Explicit transitions avoid capturing the disabled value again when
            // switching directly between two actions or entering a loop segment.
            var enter = idle.AddTransition(capture); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.Greater, 0, P("Active"));
            var start = capture.AddTransition(active); start.hasExitTime = false; start.duration = 0; start.AddCondition(AnimatorConditionMode.Greater, -1, P("Active"));
            var stop = active.AddTransition(restore); stop.hasExitTime = false; stop.duration = 0; Eq(stop, P("Active"), 0);
            var finish = restore.AddTransition(idle); finish.hasExitTime = false; finish.duration = 0; finish.AddCondition(AnimatorConditionMode.Greater, -1, P("Active"));
        }
    }
}
