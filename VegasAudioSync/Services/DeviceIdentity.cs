using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VegasAudioSync.Services
{
    /// <summary>
    /// Single source of truth for the device/source key attached to each event.
    /// It only sees paths (no metadata probe), so it must be deterministic and
    /// never throw: a folder-leaf fallback is always available.
    /// </summary>
    public static class DeviceIdentity
    {
        private sealed class Identity
        {
            public string Key;
            public string Display;
            public string Evidence;
            public int Confidence;
        }

        // Leading project token, e.g. "03_wesele_" in "03_wesele_Sony A7 III 001.MP4".
        private static readonly Regex ProjectToken =
            new Regex(@"^\d{1,4}_[^_]+_", RegexOptions.CultureInvariant);

        // Trailing counter, e.g. " 001", "_0001", "-12" or "C0001" -> "C".
        private static readonly Regex TrailingCounter =
            new Regex(@"[ _\-]?\d{2,}$", RegexOptions.CultureInvariant);

        // Camera-vendor model tokens (GH also covers GH010001 style names).
        private static readonly Regex CameraToken = new Regex(
            @"(?:^|[^a-z0-9])(sony|canon|nikon|panasonic|lumix|dji|gopro|insta360|hero|a7|a9|fx[0-9]+|gh[0-9]*)(?=[^a-z0-9]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex OryginalToken = new Regex(
            @"(?:^|[ _\-]+)(?:oryginal|original|raw)(?=[ _\-]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex MultiWhitespace = new Regex(@"\s{2,}", RegexOptions.CultureInvariant);
        private static readonly Regex MultiUnderscore = new Regex(@"_{2,}", RegexOptions.CultureInvariant);

        /// <summary>
        /// Stable, normalized key used for SourceKey (device grouping).
        /// Returns null when there is no model/prefix/folder evidence: an
        /// "unknown" key would merge unrelated devices into one source group.
        /// </summary>
        public static string Key(string mediaPath, string mediaFolder)
        {
            try { return Build(mediaPath, mediaFolder).Key; }
            catch { return null; }
        }

        /// <summary>Human readable device name for logs and diagnostics.</summary>
        public static string DisplayName(string mediaPath, string mediaFolder)
        {
            try { return Build(mediaPath, mediaFolder).Display; }
            catch { return "Unknown"; }
        }

        /// <summary>Short explanation of how the key was derived.</summary>
        public static string Evidence(string mediaPath, string mediaFolder)
        {
            try { return Build(mediaPath, mediaFolder).Evidence; }
            catch { return "folder leaf fallback"; }
        }

        /// <summary>2 = high (camera token), 1 = medium (prefix/folder variant), 0 = low.</summary>
        public static int Confidence(string mediaPath, string mediaFolder)
        {
            try { return Build(mediaPath, mediaFolder).Confidence; }
            catch { return 0; }
        }

        private static Identity Build(string mediaPath, string mediaFolder)
        {
            string leaf = CleanLeaf(FolderLeaf(mediaFolder));

            string fileName = null;
            try { fileName = Path.GetFileName(mediaPath); }
            catch { fileName = null; }

            string baseName = null;
            try { baseName = string.IsNullOrEmpty(fileName) ? null : Path.GetFileNameWithoutExtension(fileName); }
            catch { baseName = fileName; }

            bool fromFile = !string.IsNullOrEmpty(baseName);
            if (string.IsNullOrEmpty(baseName)) baseName = leaf;
            if (string.IsNullOrEmpty(baseName))
            {
                return new Identity
                {
                    Key = null,
                    Display = "Unknown",
                    Evidence = "no filename or folder evidence",
                    Confidence = 0
                };
            }

            if (!fromFile)
            {
                // No usable filename: the folder leaf itself is the only identity.
                string folderDisplay = CleanLeaf(baseName) ?? "Unknown";
                string leafKey = NormalizeVariant(folderDisplay);
                string folderHash = ParentHash(mediaFolder);
                if (!string.IsNullOrEmpty(folderHash))
                    leafKey = (leafKey ?? "folder") + "|" + folderHash;

                return new Identity
                {
                    Key = leafKey,
                    Display = folderDisplay,
                    Evidence = "folder leaf fallback",
                    Confidence = 0
                };
            }

            string display = CleanCore(baseName);
            if (string.IsNullOrEmpty(display)) display = CleanLeaf(baseName);
            if (string.IsNullOrEmpty(display))
            {
                return new Identity
                {
                    Key = null,
                    Display = "Unknown",
                    Evidence = "no usable filename or folder evidence",
                    Confidence = 0
                };
            }

            // Detect camera tokens on the raw name too, before any digit stripping
            // could turn "Insta360_001" into a generic prefix like "insta".
            bool camera = HasCameraToken(display) || HasCameraToken(leaf)
                || HasCameraToken(baseName) || HasCameraToken(fileName);
            bool hasPrefix = false;

            string displayBase;
            string keyBase;

            if (camera)
            {
                displayBase = display;
                keyBase = NormalizeWords(display);
            }
            else
            {
                string prefix = LeadingLetters(display);
                if (!string.IsNullOrEmpty(prefix))
                {
                    hasPrefix = true;
                    displayBase = prefix;
                    keyBase = prefix.ToLowerInvariant();
                }
                else
                {
                    displayBase = display;
                    keyBase = NormalizeWords(display);
                }
            }

            if (string.IsNullOrEmpty(keyBase)) keyBase = NormalizeWords(leaf);
            if (string.IsNullOrEmpty(keyBase) || !ContainsLetter(keyBase))
            {
                // Digits/punctuation only and no usable folder: do not invent a key
                // that would merge unrelated devices.
                return new Identity
                {
                    Key = null,
                    Display = display,
                    Evidence = "no model/prefix/folder evidence",
                    Confidence = 0
                };
            }

            string variantKey = null;
            string variantDisplay = null;

            if (!string.IsNullOrEmpty(leaf))
            {
                string suffix = ExtractSuffix(displayBase, leaf);
                if (!string.IsNullOrEmpty(suffix))
                {
                    variantDisplay = suffix;
                    variantKey = NormalizeVariant(suffix);
                }
                else if (!string.Equals(NormalizeWords(leaf), NormalizeWords(displayBase), StringComparison.OrdinalIgnoreCase))
                {
                    variantDisplay = leaf;
                    variantKey = NormalizeVariant(leaf);
                }

                if (!string.IsNullOrEmpty(variantKey)
                    && string.Equals(NormalizeWords(variantKey), NormalizeWords(keyBase), StringComparison.OrdinalIgnoreCase))
                {
                    variantKey = null;
                    variantDisplay = null;
                }
            }

            string key = string.IsNullOrEmpty(variantKey) ? keyBase : keyBase + "|" + variantKey;

            // Prefix/folder evidence is not unique on its own: two cards can both
            // contain "...\card1\audio" and "...\card2\audio". Add a stable short
            // hash of the full parent directory so those never collide. High
            // confidence camera models are allowed to merge across folders.
            if (!camera)
            {
                string parentHash = ParentHash(mediaFolder);
                if (!string.IsNullOrEmpty(parentHash)) key = key + "|" + parentHash;
            }

            string name = displayBase;
            if (!camera && !string.IsNullOrEmpty(variantDisplay)
                && !string.Equals(NormalizeWords(variantDisplay), NormalizeWords(displayBase), StringComparison.OrdinalIgnoreCase))
            {
                name = displayBase + " (" + variantDisplay + ")";
            }

            string evidence;
            if (camera) evidence = "camera model token in filename";
            else if (hasPrefix) evidence = "filename prefix + folder variant";
            else evidence = "folder leaf fallback";
            if (!string.IsNullOrEmpty(leaf)) evidence += "; folder='" + leaf + "'";

            return new Identity
            {
                Key = key,
                Display = name,
                Evidence = evidence,
                Confidence = camera ? 2 : (hasPrefix ? 1 : 0)
            };
        }

        private static string CleanCore(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            string value = name.Trim().Trim('!', ' ', '\t');
            value = ProjectToken.Replace(value, "");

            for (int i = 0; i < 4; i++)
            {
                string next = OryginalToken.Replace(value, "");
                if (next == value) break;
                value = next;
            }

            for (int i = 0; i < 4; i++)
            {
                string next = TrailingCounter.Replace(value, "");
                if (next == value) break;

                // Never strip digits that are part of a camera/model token:
                // "Insta360_001" -> "Insta360" but not "Insta".
                if (HasCameraToken(value) && !HasCameraToken(next)) break;

                value = next;
            }

            value = MultiWhitespace.Replace(value, " ");
            value = value.Trim(' ', '_', '-', '.', '!');
            return value;
        }

        private static string CleanLeaf(string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return null;
            string value = leaf.Trim().Trim('!', ' ', '\t', '\\', '/');
            value = MultiWhitespace.Replace(value, " ");
            value = value.Trim(' ', '_', '-', '.', '!');
            return value.Length == 0 ? null : value;
        }

        private static string FolderLeaf(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try
            {
                string trimmed = folder.TrimEnd('\\', '/', ' ');
                if (trimmed.Length == 0) return folder;
                string leaf = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Existing heuristic: letters (and underscores) before the first digit,
        /// e.g. C0001 -> C, GH010001 -> GH, DJI_0001 -> DJI, Tascam_wesele_... -> Tascam_wesele.
        /// </summary>
        private static string LeadingLetters(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            int end = 0;
            bool hasLetter = false;
            while (end < value.Length)
            {
                char c = value[end];
                if (c == '_')
                {
                    end++;
                    continue;
                }
                if (!char.IsLetter(c)) break;
                hasLetter = true;
                end++;
            }

            if (!hasLetter) return null;
            string prefix = value.Substring(0, end).TrimEnd('_');
            return prefix.Length == 0 ? null : prefix;
        }

        private static bool HasCameraToken(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            try { return CameraToken.IsMatch(value); }
            catch { return false; }
        }

        /// <summary>
        /// Returns the part of the folder leaf that follows the device name,
        /// e.g. base "Tascam_wesele" + leaf "Tascam_wesele_audio1" -> "audio1".
        /// </summary>
        private static string ExtractSuffix(string baseDisplay, string leaf)
        {
            if (string.IsNullOrEmpty(baseDisplay) || string.IsNullOrEmpty(leaf)) return null;

            string[] tokens = baseDisplay.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return null;

            string pattern = "^" + string.Join(
                "[ _.\\-]+",
                tokens.Select(Regex.Escape).ToArray()) + "[ _.\\-]+(.+)$";

            try
            {
                Match match = Regex.Match(leaf, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) return null;
                string suffix = match.Groups[1].Value.Trim(' ', '_', '-', '.', '!');
                return suffix.Length == 0 ? null : suffix;
            }
            catch
            {
                return null;
            }
        }

        private static bool ContainsLetter(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
            {
                if (char.IsLetter(c)) return true;
            }
            return false;
        }

        private static string NormalizeWords(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string normalized = value.ToLowerInvariant().Replace('_', ' ');
            normalized = MultiWhitespace.Replace(normalized, " ");
            normalized = normalized.Trim(' ', '_', '-', '.', '!');
            return normalized.Length == 0 ? null : normalized;
        }

        private static string NormalizeVariant(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string normalized = value.ToLowerInvariant().Trim();
            normalized = MultiWhitespace.Replace(normalized, " ");
            normalized = normalized.Replace(' ', '_');
            normalized = MultiUnderscore.Replace(normalized, "_");
            normalized = normalized.Trim(' ', '_', '-', '.', '!');
            return normalized.Length == 0 ? null : normalized;
        }

        /// <summary>
        /// Stable 8-hex hash of the full normalized parent directory. Used to keep
        /// prefix/folder keys unique across cards that share a folder leaf.
        /// </summary>
        private static string ParentHash(string mediaFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(mediaFolder)) return null;

                string value;
                try { value = Path.GetFullPath(mediaFolder); }
                catch { value = mediaFolder; }

                value = value.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
                if (value.Length == 0) return null;

                uint hash = 2166136261;
                foreach (char c in value)
                {
                    hash ^= c;
                    hash *= 16777619;
                }

                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }
    }
}
