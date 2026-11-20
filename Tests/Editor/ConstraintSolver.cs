using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Jobs;
using UnityEngine;
using VRC.Dynamics;

namespace Orbiters.Toolkit.Editor.Tests
{
    /// <summary>
    /// Runs the VRChat SDK's own constraint solver on objects of a preview scene (it only picks up the open scenes by
    /// itself), one update per <see cref="Solve"/>.
    /// </summary>
    internal sealed class ConstraintSolver
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type Manager = typeof(VRCConstraintBase).Assembly.GetType("VRC.Dynamics.VRCConstraintManager");
        private readonly List<VRCConstraintBase> constraints = new List<VRCConstraintBase>();

        public void Track(GameObject root)
        {
            foreach (var constraint in root.GetComponentsInChildren<VRCConstraintBase>(true))
            {
                constraints.Add(constraint);
                Manager.GetMethod("RegisterConstraint", Static).Invoke(null, new object[] { constraint });
            }
        }

        public void Solve()
        {
            // The SDK unregisters a constraint whose object is turned off; turned on again, it is back (as in a scene).
            foreach (var constraint in constraints)
                if (constraint.isActiveAndEnabled && !(bool)Manager.GetMethod("IsConstraintRegistered", Static).Invoke(null, new object[] { constraint }))
                    Manager.GetMethod("RegisterConstraint", Static).Invoke(null, new object[] { constraint });
            Manager.GetMethod("Sdk_ManuallyRefreshGroups", Static).Invoke(null, new object[] { constraints.Where(c => c.isActiveAndEnabled).ToArray() });
            Manager.GetMethod("UpdateConstraints", Static).Invoke(null, null);
            var schedule = Manager.GetMethod("ScheduleExecutionJobs", Static);
            var stage = schedule.GetParameters()[0].ParameterType;
            foreach (string name in new[] { "PrePhysBone", "PostPhysBone", "PostLocalAvatarProcess" })
            {
                // SDK 3.10.6 added a "resolve at once" flag between the stage and the job to wait for.
                var arguments = schedule.GetParameters().Select(p => p.ParameterType == stage ? Enum.Parse(stage, name)
                    : p.ParameterType == typeof(bool) ? true : (object)default(JobHandle)).ToArray();
                ((JobHandle)schedule.Invoke(null, arguments)).Complete();
            }
            Manager.GetMethod("PostUpdateConstraints", Static).Invoke(null, null);
        }

        public void Release()
        {
            foreach (var constraint in constraints)
                if (constraint != null) Manager.GetMethod("UnregisterConstraint", Static).Invoke(null, new object[] { constraint });
            constraints.Clear();
        }
    }
}
