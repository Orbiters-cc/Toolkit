using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Orbiters.Toolkit.Armature
{
    public enum BodySide { None, Left, Right }

    /// <summary>
    /// Bone naming conventions of VRChat bases and clothing: Unity/VRChat, Mixamo, Blender/Rigify, VRoid, Rexouium,
    /// 3ds Max Biped and Unreal. Names are read as words ("upper_arm.L" = upper, arm, L; "LeftUpLeg" = Left, Up, Leg),
    /// so props named after a body part ("ArmBand.L", "HeadAccessory", "Hand_Target") are never taken for that part.
    /// </summary>
    public static class BoneNames
    {
        private readonly struct Token
        {
            public readonly int Start, Length;
            public readonly string Text;
            public Token(int start, int length, string text) { Start = start; Length = length; Text = text; }
        }

        // Blender numbers duplicated names "Hips.001": a copy of the bone, except along finger chains ("Thumb.L.001").
        private static readonly Regex DuplicateSuffix = new Regex(@"\.(\d{3})$");
        // "Head_end", "Head.end", "HeadTop_End" or "HeadEnd", but not a word ending in "end" ("Bend").
        private static readonly Regex EndSuffix = new Regex(@"(?i:(^|[\s._-])end(\.\d+)?)$|End(\.\d+)?$");
        // Mixamo without namespace: LeftUpLeg (thigh) > LeftLeg (shin); elsewhere "Left leg" / "Leg_L" is the thigh.
        private static readonly Regex MixamoLeg = new Regex(@"(^|[^A-Za-z])(Left|Right)Leg$");
        private static readonly HashSet<string> RigPrefixes = new HashSet<string> { "j", "adj", "bip", "def", "mixamorig", "cc", "valve", "biped" };
        private static readonly HashSet<string> FingerWords = new HashSet<string> { "thumb", "index", "middle", "ring", "little", "pinky", "pinkie" };
        private static readonly HashSet<string> FingerFiller = new HashSet<string> { "f", "hand", "finger", "fingers" };
        private static readonly string[] Segments = { "proximal", "intermediate", "distal" };

        private static readonly Dictionary<string, HumanBodyBones> Center = new Dictionary<string, HumanBodyBones>
        {
            ["hips"] = HumanBodyBones.Hips, ["hip"] = HumanBodyBones.Hips, ["pelvis"] = HumanBodyBones.Hips,
            ["spine"] = HumanBodyBones.Spine, ["waist"] = HumanBodyBones.Spine,
            ["chest"] = HumanBodyBones.Chest, ["torso"] = HumanBodyBones.Chest, ["ribcage"] = HumanBodyBones.Chest,
            ["upperchest"] = HumanBodyBones.UpperChest, ["chestup"] = HumanBodyBones.UpperChest, ["upchest"] = HumanBodyBones.UpperChest, ["chestupper"] = HumanBodyBones.UpperChest,
            ["neck"] = HumanBodyBones.Neck, ["head"] = HumanBodyBones.Head,
            ["jaw"] = HumanBodyBones.Jaw, ["mandible"] = HumanBodyBones.Jaw,
        };

        // Left bone of each sided part; the right one is the next enum value except for fingers (handled apart).
        private static readonly Dictionary<string, HumanBodyBones> Sided = new Dictionary<string, HumanBodyBones>
        {
            ["shoulder"] = HumanBodyBones.LeftShoulder, ["clavicle"] = HumanBodyBones.LeftShoulder,
            ["upperarm"] = HumanBodyBones.LeftUpperArm, ["arm"] = HumanBodyBones.LeftUpperArm, ["uparm"] = HumanBodyBones.LeftUpperArm, ["armupper"] = HumanBodyBones.LeftUpperArm,
            ["lowerarm"] = HumanBodyBones.LeftLowerArm, ["forearm"] = HumanBodyBones.LeftLowerArm, ["elbow"] = HumanBodyBones.LeftLowerArm, ["lowarm"] = HumanBodyBones.LeftLowerArm, ["armlower"] = HumanBodyBones.LeftLowerArm,
            ["hand"] = HumanBodyBones.LeftHand, ["wrist"] = HumanBodyBones.LeftHand,
            ["upperleg"] = HumanBodyBones.LeftUpperLeg, ["upleg"] = HumanBodyBones.LeftUpperLeg, ["thigh"] = HumanBodyBones.LeftUpperLeg, ["leg"] = HumanBodyBones.LeftUpperLeg, ["legupper"] = HumanBodyBones.LeftUpperLeg,
            ["lowerleg"] = HumanBodyBones.LeftLowerLeg, ["calf"] = HumanBodyBones.LeftLowerLeg, ["shin"] = HumanBodyBones.LeftLowerLeg, ["knee"] = HumanBodyBones.LeftLowerLeg, ["lowleg"] = HumanBodyBones.LeftLowerLeg, ["leglower"] = HumanBodyBones.LeftLowerLeg,
            ["foot"] = HumanBodyBones.LeftFoot, ["ankle"] = HumanBodyBones.LeftFoot,
            ["toe"] = HumanBodyBones.LeftToes, ["toes"] = HumanBodyBones.LeftToes, ["toebase"] = HumanBodyBones.LeftToes, ["ball"] = HumanBodyBones.LeftToes,
            ["eye"] = HumanBodyBones.LeftEye, ["faceeye"] = HumanBodyBones.LeftEye,
        };

        /// <summary>Drops a "namespace:" prefix, lowercases and drops ' ', '_', '.' and '-': "mixamorig:Left_Arm" = "leftarm".</summary>
        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            int start = NameStart(name);
            var builder = new StringBuilder(name.Length - start);
            for (int i = start; i < name.Length; i++)
            {
                char c = name[i];
                if (c != ' ' && c != '_' && c != '.' && c != '-') builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }

        /// <summary>The side a name carries as a word of its own: "Left arm", "upper_arm.L", "J_Bip_R_Hand", "EarRT_L".</summary>
        public static BodySide Side(string name)
        {
            if (string.IsNullOrEmpty(name)) return BodySide.None;
            bool left = false, right = false;
            foreach (var token in Tokenize(name, NameStart(name)))
            {
                left |= IsLeft(token.Text);
                right |= IsRight(token.Text);
            }
            return left == right ? BodySide.None : left ? BodySide.Left : BodySide.Right;
        }

        /// <summary>The name of the opposite side's bone, keeping case and separators ("Left arm" = "Right arm",
        /// "upper_arm.L" = "upper_arm.R", "mixamorig:LeftHand" = "mixamorig:RightHand"). Null when the name has no side.</summary>
        public static string Mirror(string name)
        {
            if (Side(name) == BodySide.None) return null;
            var builder = new StringBuilder(name.Length + 1);
            int position = 0;
            foreach (var token in Tokenize(name, NameStart(name)))
            {
                if (!IsLeft(token.Text) && !IsRight(token.Text)) continue;
                builder.Append(name, position, token.Start - position);
                builder.Append(Swap(name.Substring(token.Start, token.Length)));
                position = token.Start + token.Length;
            }
            return builder.Append(name, position, name.Length - position).ToString();
        }

        /// <summary>
        /// The humanoid bone a name describes: body, limbs, fingers (proximal/intermediate/distal, 1/2/3 or .01/.02/.03),
        /// eyes and jaw. Every word must belong to the bone's name, so "_end" leaves, Blender copies ("Hips.001"), props
        /// ("ArmBand.L", "Chest Pin") and helpers ("Planti_Left_Knee", "Fthr_UpArm1_L") are not humanoid.
        /// </summary>
        public static bool TryInferHumanoid(string name, out HumanBodyBones bone)
        {
            bone = HumanBodyBones.LastBone;
            if (string.IsNullOrEmpty(name)) return false;
            var body = name.Substring(NameStart(name));
            int copy = 0;
            var duplicate = DuplicateSuffix.Match(body);
            if (duplicate.Success)
            {
                copy = int.Parse(duplicate.Groups[1].Value);
                body = body.Substring(0, duplicate.Index);
            }

            var tokens = Tokenize(body, 0);
            int first = 0;
            bool prefixed = false;
            while (first < tokens.Count)
            {
                var text = tokens[first].Text;
                // J_Bip_C_Hips (VRoid), CC_Base_Hip (Character Creator), Bip01_L_Thigh (Biped), DEF-thigh.L (Rigify).
                if (RigPrefixes.Contains(text) || prefixed && (text == "c" || text == "base" || IsNumber(text))) { prefixed = true; first++; }
                else break;
            }

            bool left = false, right = false;
            string number = null;
            var words = new List<string>();
            for (int i = first; i < tokens.Count; i++)
            {
                var text = tokens[i].Text;
                if (IsLeft(text)) left = true;
                else if (IsRight(text)) right = true;
                else if (IsNumber(text))
                {
                    if (number != null) return false;
                    number = text;
                }
                else words.Add(text);
            }
            if (left && right || words.Count == 0) return false;
            var side = left ? BodySide.Left : right ? BodySide.Right : BodySide.None;

            string finger = null;
            int segment = 0;
            bool otherWords = false;
            foreach (var word in words)
            {
                int named = Array.IndexOf(Segments, word);
                if (FingerWords.Contains(word))
                {
                    if (finger != null) return false;
                    finger = word;
                }
                else if (named >= 0)
                {
                    if (segment != 0) return false;
                    segment = named + 1;
                }
                else if (!FingerFiller.Contains(word)) otherWords = true;
            }
            if (finger != null)
            {
                // "ToeIndex1_L" and "EarRing_L" name a finger word among others: not a finger.
                if (otherWords || side == BodySide.None) return false;
                if (number != null)
                {
                    if (segment != 0) return false;
                    segment = int.Parse(number);
                }
                if (copy > 0)
                {
                    if (segment != 0) return false;
                    segment = 1 + copy;
                }
                if (segment == 0) segment = 1;
                if (segment > 3) return false;
                bone = Finger(finger, side, segment);
                return true;
            }

            if (copy > 0) return false;
            var key = string.Concat(words);
            if (number != null)
            {
                if (side != BodySide.None) return false;
                int value = int.Parse(number);
                // Unreal pads (spine_01 spine, _02 chest, _03 upper chest); Mixamo and Biped follow a plain Spine (Spine1 chest, Spine2 upper chest).
                if (key == "spine" && number.Length > 1 && value >= 1 && value <= 3)
                    bone = value == 1 ? HumanBodyBones.Spine : value == 2 ? HumanBodyBones.Chest : HumanBodyBones.UpperChest;
                else if (key == "spine" && number.Length == 1 && (value == 1 || value == 2))
                    bone = value == 1 ? HumanBodyBones.Chest : HumanBodyBones.UpperChest;
                else if (key == "neck" && value == 1) bone = HumanBodyBones.Neck;
                else return false;
                return true;
            }
            if (Center.TryGetValue(key, out var center))
            {
                if (side != BodySide.None) return false;
                bone = center;
                return true;
            }
            if (!Sided.TryGetValue(key, out var sided) || side == BodySide.None) return false;
            if (sided == HumanBodyBones.LeftUpperLeg && key == "leg" && MixamoLeg.IsMatch(body)) sided = HumanBodyBones.LeftLowerLeg;
            bone = side == BodySide.Left ? sided : RightOf(sided);
            return true;
        }

        /// <summary>A node that holds a skeleton rather than being a bone: "Armature", "Armature.006", "Scalf Armature",
        /// "Skeleton", "Rig", "Root".</summary>
        public static bool IsArmatureContainer(string name)
        {
            var key = Normalize(name).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            return key == "armature" || key == "skeleton" || key == "rig" || key == "root" ||
                   key.StartsWith("armature", StringComparison.Ordinal) || key.EndsWith("armature", StringComparison.Ordinal);
        }

        /// <summary>A leaf that only marks where the previous bone ends: "Head_end", "FoxEar_Top.L_end", "HeadTop_End".</summary>
        public static bool IsEnd(string name) => !string.IsNullOrEmpty(name) && EndSuffix.IsMatch(name);

        private static HumanBodyBones RightOf(HumanBodyBones left)
        {
            switch (left)
            {
                case HumanBodyBones.LeftShoulder: return HumanBodyBones.RightShoulder;
                case HumanBodyBones.LeftUpperArm: return HumanBodyBones.RightUpperArm;
                case HumanBodyBones.LeftLowerArm: return HumanBodyBones.RightLowerArm;
                case HumanBodyBones.LeftHand: return HumanBodyBones.RightHand;
                case HumanBodyBones.LeftUpperLeg: return HumanBodyBones.RightUpperLeg;
                case HumanBodyBones.LeftLowerLeg: return HumanBodyBones.RightLowerLeg;
                case HumanBodyBones.LeftFoot: return HumanBodyBones.RightFoot;
                case HumanBodyBones.LeftToes: return HumanBodyBones.RightToes;
                case HumanBodyBones.LeftEye: return HumanBodyBones.RightEye;
                default: throw new ArgumentOutOfRangeException(nameof(left));
            }
        }

        private static HumanBodyBones Finger(string finger, BodySide side, int segment)
        {
            int index = finger == "thumb" ? 0 : finger == "index" ? 1 : finger == "middle" ? 2 : finger == "ring" ? 3 : 4;
            var first = side == BodySide.Left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal;
            return first + index * 3 + (segment - 1);
        }

        private static bool IsLeft(string word) => word == "l" || word == "left";
        private static bool IsRight(string word) => word == "r" || word == "right";

        private static bool IsNumber(string word)
        {
            foreach (var c in word) if (c < '0' || c > '9') return false;
            return word.Length > 0;
        }

        private static string Swap(string side)
        {
            var lower = side.ToLowerInvariant();
            var other = lower == "left" ? "right" : lower == "right" ? "left" : lower == "l" ? "r" : "l";
            if (side == side.ToUpperInvariant()) return other.ToUpperInvariant();
            return char.IsUpper(side[0]) ? char.ToUpperInvariant(other[0]) + other.Substring(1) : other;
        }

        private static int NameStart(string name) => name.LastIndexOf(':') + 1;

        // Words of a name: split at separators, lower-to-upper case changes ("LeftArm"), the last capital of a capital
        // run ("LArm", "EarRT") and letter-digit changes ("Spine1").
        private static List<Token> Tokenize(string name, int start)
        {
            var tokens = new List<Token>();
            int i = start;
            while (i < name.Length)
            {
                if (!char.IsLetterOrDigit(name[i])) { i++; continue; }
                int begin = i++;
                while (i < name.Length && char.IsLetterOrDigit(name[i]) && !Boundary(name, i)) i++;
                tokens.Add(new Token(begin, i - begin, name.Substring(begin, i - begin).ToLowerInvariant()));
            }
            return tokens;
        }

        private static bool Boundary(string name, int i)
        {
            char previous = name[i - 1], current = name[i];
            if (char.IsDigit(previous) != char.IsDigit(current)) return true;
            if (char.IsLower(previous) && char.IsUpper(current)) return true;
            return char.IsUpper(previous) && char.IsUpper(current) && i + 1 < name.Length && char.IsLower(name[i + 1]);
        }
    }
}
