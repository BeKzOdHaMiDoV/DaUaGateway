using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DaUaGateway.Config
{
    /// <summary>Маски вида "Line1.*", "*.Temp?" (без учёта регистра).</summary>
    public static class Wildcard
    {
        public static bool IsMatch(string text, string mask)
        {
            if (string.IsNullOrEmpty(mask)) return false;
            if (mask == "*") return true;
            var pattern = "^" + Regex.Escape(mask).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return Regex.IsMatch(text ?? "", pattern, RegexOptions.IgnoreCase);
        }

        public static bool MatchesAny(string text, IEnumerable<string> masks) =>
            masks != null && masks.Any(m => IsMatch(text, m));

        /// <summary>Тег проходит, если попадает в include и не попадает в exclude.</summary>
        public static bool Passes(string text, IList<string> include, IList<string> exclude) =>
            (include == null || include.Count == 0 || MatchesAny(text, include)) && !MatchesAny(text, exclude);
    }
}
