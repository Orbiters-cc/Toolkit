using System.Linq;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    [CustomEditor(typeof(OrbitersRefit))]
    internal sealed class OrbitersRefitEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var record = (OrbitersRefit)target;
            var root = new VisualElement();
            string by = string.IsNullOrEmpty(record.tool) ? "an Orbiters tool" : record.tool;
            string what = record.kind == OrbitersRefit.FitKind.Fitted ? "Refitted" : "Blendshapes added";
            string forBase = string.IsNullOrEmpty(record.baseName) ? "" : " for " + record.baseName;
            string state = record.Applied ? "" : "\nNot in use: the renderer uses another mesh now.";
            var shapes = record.shapes.Select(s => s.source == s.generated ? s.source : s.generated + " ← " + s.source).ToList();
            root.Add(new HelpBox(what + forBase + " by " + by + ". " + shapes.Count + " blendshape" + (shapes.Count == 1 ? "" : "s") +
                                 " follow the body when the avatar is built." + state, HelpBoxMessageType.Info));
            var body = new PropertyField(serializedObject.FindProperty(nameof(OrbitersRefit.body)));
            root.Add(body);
            if (shapes.Count > 0)
            {
                var list = new Foldout { text = "Blendshapes", value = false };
                foreach (string shape in shapes) list.Add(new Label(shape));
                root.Add(list);
            }
            var restore = new Button(() => RefitRecords.Remove(record)) { text = record.Applied ? "Restore the original mesh" : "Remove this record" };
            restore.style.marginTop = 6;
            root.Add(restore);
            return root;
        }
    }
}
