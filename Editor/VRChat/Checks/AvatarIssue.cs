namespace Orbiters.Toolkit.Editor.VRChat.Checks
{
    public enum IssueLevel { Note, Warning, Problem }

    /// <summary>Something on a built avatar that doesn't work in VRChat, or works worse than it could.</summary>
    public sealed class AvatarIssue
    {
        public readonly IssueLevel Level;
        /// <summary>One line: what is wrong, naming the item.</summary>
        public readonly string Title;
        /// <summary>What it does in game, and how to fix it.</summary>
        public readonly string Detail;
        /// <summary>Where it is: a menu path, a layer, an object.</summary>
        public readonly string Where;

        /// <summary>A button's label for <see cref="Fix"/>, e.g. "Fix".</summary>
        public readonly string FixLabel;
        /// <summary>Fixes it in the user's avatar or assets (not the checked copy) and says what it did; null when there is no fix.</summary>
        public readonly System.Func<string> Fix;

        public AvatarIssue(IssueLevel level, string title, string detail, string where = null)
        {
            Level = level; Title = title; Detail = detail; Where = where;
        }

        public AvatarIssue(IssueLevel level, string title, string detail, string where, string fixLabel, System.Func<string> fix) : this(level, title, detail, where)
        {
            FixLabel = fixLabel; Fix = fix;
        }

        public override string ToString() => Level + ": " + Title + (Where != null ? " (" + Where + ")" : "");
    }
}
