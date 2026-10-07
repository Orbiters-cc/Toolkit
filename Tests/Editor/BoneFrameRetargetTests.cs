using System.Collections.Generic;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Animations;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class BoneFrameRetargetTests
    {
        private const string Toe = "Armature/Ankle/Toe";
        // Rexouium's toe rests at (21.7, 180, 0) under its ankle; the re-exported model's toe at (0, 0, 0), same shape.
        private static readonly Quaternion AuthoredRest = Quaternion.Euler(21.7f, 180f, 0f);
        private static readonly Quaternion CurrentRest = Quaternion.identity;
        private readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Clean()
        {
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
        }

        [Test]
        public void ACurlWrittenForTheOriginalRollCurlsTheSameWayOnTheNewOne()
        {
            var ankle = Quaternion.Euler(0f, 30f, 0f);
            // Both models' toes point the same way: the new one is the original rolled about the toe itself.
            var roll = Quaternion.Inverse(AuthoredRest) * CurrentRest;
            var frames = new Dictionary<string, BoneFrameRetarget.Frame> { [Toe] = BoneFrameRetarget.Frame.Between(ankle, ankle * AuthoredRest, ankle, ankle * AuthoredRest * roll) };
            var curl = Quaternion.AngleAxis(35f, Vector3.right);
            var clip = Clip("localEulerAnglesRaw", AuthoredRest * curl);

            Assert.AreEqual(1, BoneFrameRetarget.Retarget(new[] { clip }, frames));

            var played = Read(clip, "localEulerAnglesRaw");
            // The same turn away from rest, measured in the model's space.
            var authoredTurn = (ankle * AuthoredRest * curl) * Quaternion.Inverse(ankle * AuthoredRest);
            var currentTurn = (ankle * played) * Quaternion.Inverse(ankle * AuthoredRest * roll);
            Assert.Less(Quaternion.Angle(authoredTurn, currentTurn), .1f);
            Assert.Less(Quaternion.Angle(Read(clip, "localEulerAnglesRaw", 0f), played), .1f, "every key is converted");
        }

        [Test]
        public void TheRestPoseOfTheOriginalBecomesTheRestOfTheNewModelInQuaternionCurvesToo()
        {
            var frames = new Dictionary<string, BoneFrameRetarget.Frame> { [Toe] = BoneFrameRetarget.Frame.Between(Quaternion.identity, AuthoredRest, Quaternion.identity, CurrentRest) };
            var clip = Clip("m_LocalRotation", AuthoredRest);

            BoneFrameRetarget.Retarget(new[] { clip }, frames);

            Assert.Less(Quaternion.Angle(Read(clip, "m_LocalRotation"), CurrentRest), .1f);
        }

        [Test]
        public void AClipAlreadyMadeForTheNewModelStays()
        {
            var frames = new Dictionary<string, BoneFrameRetarget.Frame> { [Toe] = BoneFrameRetarget.Frame.Between(Quaternion.identity, AuthoredRest, Quaternion.identity, CurrentRest) };
            var pose = CurrentRest * Quaternion.AngleAxis(20f, Vector3.right);
            var clip = Clip("localEulerAnglesRaw", pose);

            Assert.AreEqual(0, BoneFrameRetarget.Retarget(new[] { clip }, frames));
            Assert.Less(Quaternion.Angle(Read(clip, "localEulerAnglesRaw"), pose), .1f);
        }

        // A two-key pose, as blend trees use them.
        private AnimationClip Clip(string kind, Quaternion rotation)
        {
            var clip = new AnimationClip(); owned.Add(clip);
            if (kind == "m_LocalRotation")
                for (int i = 0; i < 4; i++)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(Toe, typeof(Transform), "m_LocalRotation." + "xyzw"[i]), AnimationCurve.Constant(0, 1, rotation[i]));
            else
            {
                var euler = rotation.eulerAngles;
                for (int i = 0; i < 3; i++)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(Toe, typeof(Transform), kind + "." + "xyz"[i]), AnimationCurve.Constant(0, 1, euler[i]));
            }
            return clip;
        }

        private static Quaternion Read(AnimationClip clip, string kind, float time = 1f)
        {
            float Value(string axis) => AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(Toe, typeof(Transform), kind + "." + axis)).Evaluate(time);
            return kind == "m_LocalRotation"
                ? new Quaternion(Value("x"), Value("y"), Value("z"), Value("w")).normalized
                : Quaternion.Euler(Value("x"), Value("y"), Value("z"));
        }
    }
}
