// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Febucci.TextAnimatorForUnity.Parsing
{
    /// <summary>
    /// Shared rules for Unity rich text tags recognized by Text Animator integrations.
    /// </summary>
    public static class UnityRichTextTagUtility
    {
        static readonly HashSet<string> reservedTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a",
            "align",
            "allcaps",
            "alpha",
            "b",
            "br",
            "color",
            "cspace",
            "font",
            "font-weight",
            "gradient",
            "i",
            "indent",
            "line-height",
            "line-indent",
            "link",
            "lowercase",
            "margin",
            "margin-left",
            "margin-right",
            "mark",
            "material",
            "mspace",
            "nobr",
            "noparse",
            "page",
            "pos",
            "rotate",
            "s",
            "size",
            "smallcaps",
            "space",
            "sprite",
            "style",
            "sub",
            "sup",
            "u",
            "uppercase",
            "voffset",
            "width"
        };

        public static bool IsReservedTagName(string tagId)
        {
            if (string.IsNullOrWhiteSpace(tagId))
                return false;

            return reservedTagNames.Contains(tagId.Trim());
        }

        public static string GetReservedTagWarning(string tagId)
        {
            return $"Tag ID '{tagId}' is reserved by Unity rich text. Choose a different effect tag; reserved tags are ignored by Text Animator effects.";
        }

        public static string NormalizeTagId(string tagId)
        {
            if (string.IsNullOrEmpty(tagId))
                return tagId;

            StringBuilder builder = null;

            for (int i = 0; i < tagId.Length; i++)
            {
                char character = tagId[i];
                if (!char.IsWhiteSpace(character))
                {
                    builder?.Append(character);
                    continue;
                }

                builder ??= new StringBuilder(tagId.Length).Append(tagId, 0, i);
                builder.Append('_');
            }

            return builder?.ToString() ?? tagId;
        }

        public static bool MatchesTagOpening(string fullTag, string tagOpening)
        {
            if (string.IsNullOrEmpty(fullTag) || string.IsNullOrEmpty(tagOpening))
                return false;

            if (!fullTag.StartsWith(tagOpening, true, CultureInfo.InvariantCulture))
                return false;

            if (!RequiresBoundary(tagOpening))
                return true;

            if (fullTag.Length == tagOpening.Length)
                return true;

            return IsTagBoundary(fullTag[tagOpening.Length]);
        }

        static bool RequiresBoundary(string tagOpening)
        {
            char lastChar = tagOpening[tagOpening.Length - 1];
            return char.IsLetterOrDigit(lastChar) || lastChar == '-';
        }

        static bool IsTagBoundary(char character)
        {
            return character == '>'
                   || character == '='
                   || character == '/'
                   || char.IsWhiteSpace(character);
        }
    }
}
