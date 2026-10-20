using System;
using System.Text.RegularExpressions;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// What a search field asks for, as the Logger's search does: a piece of text found anywhere (any case unless asked),
    /// or a .NET regular expression. An invalid expression matches everything and says why in <see cref="Error"/>, so a
    /// list never empties while one is being typed. Immutable.
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

        public bool IsEmpty => Text.Length == 0 || Error != null;
        public StringComparison Comparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        public bool SameAs(string text, bool regex, bool matchCase) =>
            IsRegex == regex && MatchCase == matchCase && Text == (regex ? text ?? string.Empty : (text ?? string.Empty).Trim());

        public bool Matches(string value)
        {
            if (IsEmpty) return true;
            if (string.IsNullOrEmpty(value)) return false;
            if (Regex == null) return value.IndexOf(Text, Comparison) >= 0;
            try { return Regex.IsMatch(value); }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }
}
