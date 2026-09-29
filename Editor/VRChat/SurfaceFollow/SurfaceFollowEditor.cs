using System.Linq;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.VRChat.SurfaceFollow
{
    // The settings, then Check: which accessories will follow the body and which blendshapes move them, before uploading.
    [CustomEditor(typeof(OrbitersSurfaceFollow))]
    internal sealed class SurfaceFollowEditor : UnityEditor.Editor
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/VRChat/SurfaceFollow/surface-follow.uss";
        private VisualElement results;

        public override VisualElement CreateInspectorGUI()
        {
            var follow = (OrbitersSurfaceFollow)target;
            var root = new VisualElement();
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) root.styleSheets.Add(sheet);
            root.AddToClassList("orb-follow");

            var head = new VisualElement();
            head.AddToClassList("orb-follow__head");
            root.Add(head);
            var title = new Label("Follow body blendshapes");
            title.AddToClassList("orb-follow__title");
            head.Add(title);
            head.Add(new StageBadge(FeatureStage.Beta));
            var text = new Label("When a body blendshape changes the skin under an accessory (a piercing on a chest with “Muscles”), the accessory moves and tilts with it. Applied when the avatar is built for upload or Play Mode; the scene is not changed.");
            text.AddToClassList("orb-follow__text");
            root.Add(text);

            var scope = new PropertyField(serializedObject.FindProperty(nameof(OrbitersSurfaceFollow.scope)), "Follows");
            root.Add(scope);
            root.Add(new PropertyField(serializedObject.FindProperty(nameof(OrbitersSurfaceFollow.body)), "Body"));
            root.Add(new PropertyField(serializedObject.FindProperty(nameof(OrbitersSurfaceFollow.rotate)), "Tilt with the skin"));
            var small = new VisualElement();
            small.Add(new PropertyField(serializedObject.FindProperty(nameof(OrbitersSurfaceFollow.maximumSize)), "Largest accessory (m)"));
            small.Add(new PropertyField(serializedObject.FindProperty(nameof(OrbitersSurfaceFollow.maximumGap)), "Farthest from the skin (m)"));
            root.Add(small);
            void ShowScope() => small.style.display = follow.scope == OrbitersSurfaceFollow.Scope.SmallAccessories ? DisplayStyle.Flex : DisplayStyle.None;
            ShowScope();
            scope.RegisterValueChangeCallback(_ => ShowScope());

            var check = new Button(() => Check(follow)) { text = "Check", tooltip = "List what will follow the body, and which blendshapes move it. Changes nothing." };
            check.AddToClassList("orb-follow__check");
            check.RegisterCallback<PointerDownEvent>(_ => check.AddToClassList("orb-follow__check--pressed"), TrickleDown.TrickleDown);
            check.RegisterCallback<PointerUpEvent>(_ => check.RemoveFromClassList("orb-follow__check--pressed"), TrickleDown.TrickleDown);
            root.Add(check);
            results = new VisualElement();
            results.AddToClassList("orb-follow__results");
            root.Add(results);
            return root;
        }

        private void Check(OrbitersSurfaceFollow follow)
        {
            results.Clear();
            var body = SurfaceFollow.BodyOf(follow);
            if (body == null)
            {
                results.Add(Line("No body found: set the Body field to the avatar's main skinned mesh.", "orb-follow__line--warning"));
                return;
            }
            var plan = SurfaceFollow.Plan(follow);
            results.Add(Line("Body: " + body.name + " · " + body.sharedMesh.blendShapeCount + " blendshapes", "orb-follow__line--muted"));
            if (plan.Count == 0)
            {
                results.Add(Line(follow.scope == OrbitersSurfaceFollow.Scope.SmallAccessories ? "No small accessory sits on the skin." : "This object has no mesh to move.", "orb-follow__line--warning"));
                return;
            }
            int index = 0;
            foreach (var target in plan)
            {
                var card = new VisualElement();
                card.AddToClassList("orb-follow__card");
                card.AddToClassList("orb-follow__card--enter");
                int delay = 20 + Mathf.Min(index++, 10) * 30;
                card.schedule.Execute(() => card.RemoveFromClassList("orb-follow__card--enter")).StartingIn(delay);
                var name = new Label(target.Renderer.name);
                name.AddToClassList("orb-follow__card-title");
                name.RegisterCallback<PointerDownEvent>(_ => EditorGUIUtility.PingObject(target.Renderer));
                card.Add(name);
                if (target.Skipped != null)
                {
                    card.Add(Line("Stays as it is: " + target.Skipped + ".", "orb-follow__line--muted"));
                }
                else
                {
                    var shapes = target.Shapes.GroupBy(s => s.Name).Select(g => g.OrderByDescending(s => s.Move.magnitude).First()).OrderByDescending(s => s.Move.magnitude).ToList();
                    card.Add(Line(shapes.Count + " blendshape" + (shapes.Count == 1 ? "" : "s") + " move it · " + Distance(target.Gap) + " from the skin", "orb-follow__line--muted"));
                    foreach (var shape in shapes.Take(6))
                        card.Add(Line(shape.Name + ": " + Distance(shape.Move.magnitude) + (shape.TurnDegrees >= 0.5f ? ", " + shape.TurnDegrees.ToString("0") + "°" : ""), "orb-follow__line"));
                    if (shapes.Count > 6) card.Add(Line("and " + (shapes.Count - 6) + " more", "orb-follow__line--muted"));
                }
                results.Add(card);
            }
        }

        private static Label Line(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        private static string Distance(float metres)
        {
            return metres < 0.01f ? (metres * 1000f).ToString("0.#") + " mm" : (metres * 100f).ToString("0.#") + " cm";
        }
    }
}
