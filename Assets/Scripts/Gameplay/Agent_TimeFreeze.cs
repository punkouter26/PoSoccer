using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Reference-counted owner of <see cref="Time.timeScale"/>.
    ///
    /// Four separate systems now want to stop the clock - the goal replay, the
    /// kickoff countdown, the halftime break and the end-of-match panel - and a
    /// goal that wins the match triggers three of them in the same frame. With
    /// each one writing timeScale directly, whichever finished first would
    /// un-freeze the game underneath the others: the end panel would appear over
    /// a pitch that had quietly resumed playing.
    ///
    /// Holders are tracked by identity, so the clock only runs again once the
    /// last holder releases. Acquire/Release are idempotent per holder.
    /// </summary>
    public static class Agent_TimeFreeze
    {
        static readonly HashSet<object> Holders = new();

        public static bool IsFrozen => Holders.Count > 0;

        public static int HolderCount => Holders.Count;

        /// <summary>
        /// Names every current holder. A leaked hold presents to the player as a
        /// hung game with nothing in the console, so the diagnostic has to name
        /// the culprit rather than just report that the clock is stopped.
        /// Used by Agent_Telemetry and by the flow tests' failure messages.
        /// </summary>
        public static string DescribeHolders()
        {
            if (Holders.Count == 0) return "none";
            var builder = new System.Text.StringBuilder();
            foreach (var holder in Holders)
            {
                if (builder.Length > 0) builder.Append(", ");
                builder.Append(holder is Object unityObject && unityObject != null
                    ? $"{holder.GetType().Name}({unityObject.name})"
                    : holder.GetType().Name);
            }
            return builder.ToString();
        }

        public static void Acquire(object holder)
        {
            if (holder == null || !Holders.Add(holder)) return;
            Time.timeScale = 0f;
        }

        public static void Release(object holder)
        {
            if (holder == null || !Holders.Remove(holder)) return;
            if (Holders.Count == 0) Time.timeScale = 1f;
        }

        /// <summary>
        /// Drops every holder and resumes the clock. Statics survive a scene load
        /// in a player build (no domain reload), so a REMATCH taken from a frozen
        /// end panel would otherwise load the new scene with a stale holder still
        /// registered and the game paused. Scene entry points call this.
        /// </summary>
        public static void ReleaseAll()
        {
            Holders.Clear();
            Time.timeScale = 1f;
        }
    }
}
