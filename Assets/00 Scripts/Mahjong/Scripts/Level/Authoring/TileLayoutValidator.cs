using System;
using System.Collections.Generic;
using UnityEngine;

namespace MahjongOut3D.LevelSystem
{
    /// <summary>
    /// Validation severity used by the manual layout authoring tool.
    /// </summary>
    public enum TileLayoutValidationSeverity
    {
        Warning = 0,
        Error = 1,
    }

    /// <summary>
    /// Describes one authoring validation result.
    /// </summary>
    [Serializable]
    public sealed class TileLayoutValidationMessage
    {
        public TileLayoutValidationSeverity Severity { get; }
        public string Message { get; }

        public TileLayoutValidationMessage(TileLayoutValidationSeverity severity, string message)
        {
            Severity = severity;
            Message = message;
        }
    }

    /// <summary>
    /// Performs editor-independent validation for manually authored tile placements.
    /// </summary>
    public static class TileLayoutValidator
    {
        public static List<TileLayoutValidationMessage> Validate(TileLayoutAuthoring layout, Vector3 tileSize)
        {
            List<TileLayoutValidationMessage> messages = new List<TileLayoutValidationMessage>();
            if (layout == null)
            {
                messages.Add(new TileLayoutValidationMessage(TileLayoutValidationSeverity.Error, "Manual layout asset is missing."));
                return messages;
            }

            HashSet<string> stableIds = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<int, int> matchCounts = new Dictionary<int, int>();
            List<TileAuthoringEntry> entries = layout.Entries;
            for (int index = 0; index < entries.Count; index++)
            {
                TileAuthoringEntry entry = entries[index];
                if (entry == null)
                {
                    messages.Add(new TileLayoutValidationMessage(TileLayoutValidationSeverity.Error, $"Entry {index} is null."));
                    continue;
                }

                if (!stableIds.Add(entry.StableId))
                {
                    messages.Add(new TileLayoutValidationMessage(TileLayoutValidationSeverity.Error, $"Entry {index} has a duplicate stable id."));
                }

                if (!TileSnapMath.IsFinite(entry.ResolvedPosition) || !TileSnapMath.IsFinite(entry.ResolvedEulerAngles))
                {
                    messages.Add(new TileLayoutValidationMessage(TileLayoutValidationSeverity.Error, $"Entry {index} has a non-finite pose."));
                }

                matchCounts.TryGetValue(entry.MatchId, out int count);
                matchCounts[entry.MatchId] = count + 1;
            }

            foreach (KeyValuePair<int, int> pair in matchCounts)
            {
                if (pair.Value % 2 != 0)
                {
                    messages.Add(new TileLayoutValidationMessage(TileLayoutValidationSeverity.Error, $"Match ID {pair.Key} has {pair.Value} tile(s); pairs must be even."));
                }
            }

            Vector3 safeSize = new Vector3(Mathf.Max(0.01f, Mathf.Abs(tileSize.x)), Mathf.Max(0.01f, Mathf.Abs(tileSize.y)), Mathf.Max(0.01f, Mathf.Abs(tileSize.z)));
            for (int firstIndex = 0; firstIndex < entries.Count; firstIndex++)
            {
                TileAuthoringEntry first = entries[firstIndex];
                if (first == null)
                {
                    continue;
                }

                Bounds firstBounds = GetWorldLikeBounds(first, safeSize);
                for (int secondIndex = firstIndex + 1; secondIndex < entries.Count; secondIndex++)
                {
                    TileAuthoringEntry second = entries[secondIndex];
                    if (second == null)
                    {
                        continue;
                    }

                    Bounds secondBounds = GetWorldLikeBounds(second, safeSize);
                    if (firstBounds.Intersects(secondBounds))
                    {
                        messages.Add(new TileLayoutValidationMessage(TileLayoutValidationSeverity.Warning, $"Entries {firstIndex} and {secondIndex} have overlapping axis-aligned bounds."));
                    }
                }
            }

            return messages;
        }

        private static Bounds GetWorldLikeBounds(TileAuthoringEntry entry, Vector3 size)
        {
            Quaternion rotation = Quaternion.Euler(entry.ResolvedEulerAngles);
            Vector3 extents = GetRotatedExtents(size * 0.5f, rotation);
            return new Bounds(entry.ResolvedPosition, extents * 2f);
        }

        private static Vector3 GetRotatedExtents(Vector3 halfSize, Quaternion rotation)
        {
            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;
            Vector3 forward = rotation * Vector3.forward;
            return new Vector3(
                Mathf.Abs(right.x) * halfSize.x + Mathf.Abs(up.x) * halfSize.y + Mathf.Abs(forward.x) * halfSize.z,
                Mathf.Abs(right.y) * halfSize.x + Mathf.Abs(up.y) * halfSize.y + Mathf.Abs(forward.y) * halfSize.z,
                Mathf.Abs(right.z) * halfSize.x + Mathf.Abs(up.z) * halfSize.y + Mathf.Abs(forward.z) * halfSize.z);
        }
    }
}
