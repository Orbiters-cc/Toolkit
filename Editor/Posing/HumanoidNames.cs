using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Posing
{
    /// <summary>Reads the humanoid role and body side from common bone naming conventions (Blender, Mixamo, Unity, VRChat bases).</summary>
    public static class HumanoidNames
    {
        public enum BodySide { None, Left, Right }

        private static readonly Regex LeftTag = new Regex(@"(^|[._\s\-])l($|[._\s\-\d])|left", RegexOptions.IgnoreCase);
        private static readonly Regex RightTag = new Regex(@"(^|[._\s\-])r($|[._\s\-\d])|right", RegexOptions.IgnoreCase);
        private static readonly Regex Words = new Regex(@"[^A-Za-z]+|(?<=[a-z])(?=[A-Z])");

        public static BodySide Side(string name)
        {
            if (string.IsNullOrEmpty(name)) return BodySide.None;
            bool left = LeftTag.IsMatch(name), right = RightTag.IsMatch(name);
            return left == right ? BodySide.None : left ? BodySide.Left : BodySide.Right;
        }

        /// <summary>Hips, spine, chest, neck, head, and the sided limbs and eyes; fingers are left to exact names.</summary>
        public static bool TryInfer(string name, out HumanBodyBones bone)
        {
            bone = HumanBodyBones.LastBone;
            if (string.IsNullOrEmpty(name)) return false;
            var colon = name.LastIndexOf(':');
            if (colon >= 0) name = name.Substring(colon + 1);
            var side = Side(name);
            var words = string.Concat(Words.Split(name).Select(w => w.ToLowerInvariant()).Where(w => w.Length > 0 && w != "l" && w != "r" && w != "left" && w != "right"));
            bool Has(params string[] parts) => parts.Any(words.Contains);
            HumanBodyBones? central =
                Has("upperchest") ? HumanBodyBones.UpperChest :
                Has("chest") ? HumanBodyBones.Chest :
                Has("spine") ? HumanBodyBones.Spine :
                Has("hips", "pelvis") || words == "hip" ? HumanBodyBones.Hips :
                Has("neck") ? HumanBodyBones.Neck :
                Has("head") ? HumanBodyBones.Head : (HumanBodyBones?)null;
            if (central.HasValue && side == BodySide.None) { bone = central.Value; return true; }
            if (side == BodySide.None) return false;
            bool left = side == BodySide.Left;
            HumanBodyBones? sided =
                Has("shoulder", "clavicle") ? (left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder) :
                Has("forearm", "lowerarm", "elbow") ? (left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm) :
                Has("upperarm", "arm") ? (left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm) :
                Has("hand", "wrist") ? (left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand) :
                Has("toe") ? (left ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes) :
                Has("foot", "ankle") ? (left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot) :
                Has("calf", "shin", "knee", "lowerleg") ? (left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg) :
                Has("thigh", "upperleg", "upleg", "leg") ? (left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg) :
                Has("eye") ? (left ? HumanBodyBones.LeftEye : HumanBodyBones.RightEye) : (HumanBodyBones?)null;
            if (!sided.HasValue) return false;
            bone = sided.Value;
            return true;
        }
    }
}
