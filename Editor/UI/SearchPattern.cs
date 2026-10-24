using System;
using System.Text.RegularExpressions;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// What a search field asks for, as the Mixer console's search does: a piece of text found anywhere (any case unless asked),
    /// or a .NET regular expression. An invalid expression matches everything and says why in <see cref="Error"/>, so a
    /// list never empties while one is being typed. An expression that takes too long on one value (catastrophic
    /// backtracking, often while it is being typed) matches nothing more from then on and says so in
    /// <see cref="TooSlow"/>: a list of thousands of names costs one timeout, not one per name.
    /// </summary>
    public sealed class SearchPattern
    {
        public static readonly SearchPattern Empty = new SearchPattern(string.Empty, false, false);

        public SearchPattern(string text, bool regex, bool matchCase)
        {
            IsRegex = regex;
            MatchCase = matchCase;
            // A plain search ignores the spaces around it; an expression keeps them.
            Text = regex ? text ?? string.Empty : (text ?? string.Empty).Trim();
            if (!regex || Text.Length == 0) return;
            try
            {
                var options = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                Regex = new Regex(Text, options, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException exception)
            {
                // "parsing \"(abc\" - Not enough )'s." → "Not enough )'s."
                string message = exception.Message;
                int dash = message.LastIndexOf(" - ", StringComparison.Ordinal);
                Error = dash > 0 ? message.Substring(dash + 3) : message;
            }
        }

        public string Text { get; }
        public bool IsRegex { get; }
        public bool MatchCase { get; }
        /// <summary>The compiled expression, or null (plain text, or invalid).</summary>
        public Regex Regex { get; }
        /// <summary>Why the expression is invalid, or null.</summary>
        public string Error { get; }
        /// <summary>The expression took longer than its timeout on a value: it matches nothing from then on.</summary>
        public bool TooSlow => tooSlow;
        public const string TooSlowMessage = "This expression takes too long on some names: simplify it (nested repeats such as (a+)+ are the usual cause).";

        private volatile bool tooSlow;

        public bool IsEmpty => Text.Length == 0 || Error != null;
        public StringComparison Comparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        public bool SameAs(string text, bool regex, bool matchCase) =>
            IsRegex == regex && MatchCase == matchCase && Text == (regex ? text ?? string.Empty : (text ?? string.Empty).Trim());

        public bool Matches(string value)
        {
            if (IsEmpty) return true;
            if (string.IsNullOrEmpty(value)) return false;
            if (Regex == null) return value.IndexOf(Text, Comparison) >= 0;
            if (tooSlow) return false;
            try { return Regex.IsMatch(value); }
            catch (RegexMatchTimeoutException) { tooSlow = true; return false; }
        }
    }
}
