// Copyright 2009 The Noda Time Authors. All rights reserved.
// Use of this source code is governed by the Apache License 2.0,
// as found in the LICENSE.txt file.

using System;
using System.Collections.Generic;
using NodaTime.TimeZones;
using NodaTime.Utility;

namespace NodaTime.TzdbCompiler.Tzdb
{
    /// <summary>
    /// Provides a means of programatically creating complex time zones. Currently internal, but we
    /// may want to make it public again eventually.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DateTimeZoneBuilder allows complex DateTimeZones to be constructed. Since creating a new
    /// DateTimeZone this way is a relatively expensive operation, built zones can be written to a
    /// file. Reading back the encoded data is a quick operation.
    /// </para>
    /// <para>
    /// DateTimeZoneBuilder itself is mutable and not thread-safe, but the DateTimeZone objects that
    /// it builds are thread-safe and immutable.
    /// </para>
    /// <para>
    /// It is intended that {@link NodaTime.TzdbCompiler} be used to read time zone data files,
    /// indirectly calling DateTimeZoneBuilder. The following complex example defines the
    /// America/Los_Angeles time zone, with all historical transitions:
    /// </para>
    /// <para>
    /// <example>
    ///     DateTimeZone America_Los_Angeles = new DateTimeZoneBuilder()
    ///         .AddCutover(-2147483648, 'w', 1, 1, 0, false, 0)
    ///         .SetStandardOffset(-28378000)
    ///         .SetFixedSavings("LMT", 0)
    ///         .AddCutover(1883, 'w', 11, 18, 0, false, 43200000)
    ///         .SetStandardOffset(-28800000)
    ///         .AddRecurringSavings("PDT", 3600000, 1918, 1919, 'w',  3, -1, 7, false, 7200000)
    ///         .AddRecurringSavings("PST",       0, 1918, 1919, 'w', 10, -1, 7, false, 7200000)
    ///         .AddRecurringSavings("PWT", 3600000, 1942, 1942, 'w',  2,  9, 0, false, 7200000)
    ///         .AddRecurringSavings("PPT", 3600000, 1945, 1945, 'u',  8, 14, 0, false, 82800000)
    ///         .AddRecurringSavings("PST",       0, 1945, 1945, 'w',  9, 30, 0, false, 7200000)
    ///         .AddRecurringSavings("PDT", 3600000, 1948, 1948, 'w',  3, 14, 0, false, 7200000)
    ///         .AddRecurringSavings("PST",       0, 1949, 1949, 'w',  1,  1, 0, false, 7200000)
    ///         .AddRecurringSavings("PDT", 3600000, 1950, 1966, 'w',  4, -1, 7, false, 7200000)
    ///         .AddRecurringSavings("PST",       0, 1950, 1961, 'w',  9, -1, 7, false, 7200000)
    ///         .AddRecurringSavings("PST",       0, 1962, 1966, 'w', 10, -1, 7, false, 7200000)
    ///         .AddRecurringSavings("PST",       0, 1967, 2147483647, 'w', 10, -1, 7, false, 7200000)
    ///         .AddRecurringSavings("PDT", 3600000, 1967, 1973, 'w', 4, -1,  7, false, 7200000)
    ///         .AddRecurringSavings("PDT", 3600000, 1974, 1974, 'w', 1,  6,  0, false, 7200000)
    ///         .AddRecurringSavings("PDT", 3600000, 1975, 1975, 'w', 2, 23,  0, false, 7200000)
    ///         .AddRecurringSavings("PDT", 3600000, 1976, 1986, 'w', 4, -1,  7, false, 7200000)
    ///         .AddRecurringSavings("PDT", 3600000, 1987, 2147483647, 'w', 4, 1, 7, true, 7200000)
    ///         .ToDateTimeZone("America/Los_Angeles");
    /// </example>
    /// </para>
    /// <para>
    /// Original name: DateTimeZoneBuilder.
    /// </para>
    /// </remarks>
    internal sealed class DateTimeZoneBuilder
    {
        private readonly IList<ZoneRuleSet> ruleSets;

        internal DateTimeZoneBuilder(List<ZoneRuleSet> ruleSets)
        {
            this.ruleSets = ruleSets;
        }
        
        /// <summary>
        /// Processes all the rules and builds a DateTimeZone.
        /// </summary>
        /// <param name="zoneId">Time zone ID to assign</param>
        public DateTimeZone ToDateTimeZone(String zoneId)
        {
            Preconditions.CheckNotNull(zoneId, "zoneId");

            var transitions = new List<ZoneTransition>();
            DateTimeZone tailZone = null;
            Instant instant = Instant.BeforeMinValue;

            // TODO: See whether PartialZoneIntervalMap would help to tidy this up.
            int ruleSetCount = ruleSets.Count;
            bool tailZoneSeamValid = false;
            for (int i = 0; i < ruleSetCount; i++)
            {
                var ruleSet = ruleSets[i];
                var transitionIterator = ruleSet.Iterator(instant);
                ZoneTransition nextTransition = transitionIterator.First();
                if (nextTransition == null)
                {
                    continue;
                }
                AddTransition(transitions, nextTransition);

                while ((nextTransition = transitionIterator.Next()) != null)
                {
                    if (AddTransition(transitions, nextTransition))
                    {
                        if (tailZone != null)
                        {
                            // Got the extra transition before DaylightSavingsTimeZone.
                            // This final transition has a valid start point and offset, but
                            // we don't know where it ends - which is fine, as the tail zone will
                            // take over.
                            tailZoneSeamValid = true;
                            break;
                        }
                    }
                    if (tailZone == null && i == ruleSetCount - 1)
                    {
                        tailZone = transitionIterator.BuildTailZone(zoneId);
                        // If tailZone is not null, don't break out of main loop until at least one
                        // more transition is calculated. This ensures a correct 'seam' to the
                        // DaylightSavingsTimeZone.
                    }
                }

                instant = ruleSet.GetUpperLimit(transitionIterator.Savings);
            }

            // Simple case where we don't have a trailing daylight saving zone.
            if (tailZone == null)
            {
                switch (transitions.Count)
                {
                    case 0:
                        return new FixedDateTimeZone(zoneId, Offset.Zero);
                    case 1:
                        return new FixedDateTimeZone(zoneId, transitions[0].WallOffset, transitions[0].Name);
                    default:
                        return CreatePrecalculatedDateTimeZone(zoneId, transitions, Instant.AfterMaxValue, null);
                }
            }

            // Sanity check
            if (!tailZoneSeamValid)
            {
                throw new InvalidOperationException("Invalid time zone data for id " + zoneId + "; no valid transition before tail zone");
            }

            // The final transition should not be used for a zone interval,
            // although it should have the same offset etc as the tail zone for its starting point.
            var lastTransition = transitions[transitions.Count - 1];
            var firstTailZoneInterval = tailZone.GetZoneInterval(lastTransition.Instant);
            if (lastTransition.StandardOffset != firstTailZoneInterval.StandardOffset ||
                lastTransition.WallOffset != firstTailZoneInterval.WallOffset ||
                lastTransition.Savings != firstTailZoneInterval.Savings ||
                lastTransition.Name != firstTailZoneInterval.Name)
            {
                throw new InvalidOperationException(
                    string.Format("Invalid seam to tail zone in time zone {0}; final transition {1} different to first tail zone interval {2}",
                                  zoneId, lastTransition, firstTailZoneInterval));
            }

            transitions.RemoveAt(transitions.Count - 1);
            return CreatePrecalculatedDateTimeZone(zoneId, transitions, lastTransition.Instant, tailZone);
        }

        private static DateTimeZone CreatePrecalculatedDateTimeZone(string id, IList<ZoneTransition> transitions,
            Instant tailZoneStart, DateTimeZone tailZone)
        {
            // Convert the transitions to intervals
            int size = transitions.Count;
            var intervals = new ZoneInterval[size];
            for (int i = 0; i < size; i++)
            {
                var transition = transitions[i];
                var endInstant = i == size - 1 ? tailZoneStart : transitions[i + 1].Instant;
                intervals[i] = new ZoneInterval(transition.Name, transition.Instant, endInstant, transition.WallOffset, transition.Savings);
            }
            return new PrecalculatedDateTimeZone(id, intervals, tailZone).MaybeCreateCachedZone();
        }

        /// <summary>
        /// Adds the given transition to the transition list if it represents a new transition.
        /// </summary>
        /// <param name="transitions">The list of <see cref="ZoneTransition"/> to add to.</param>
        /// <param name="transition">The transition to add.</param>
        /// <returns><c>true</c> if the transition was added.</returns>
        private static bool AddTransition(IList<ZoneTransition> transitions, ZoneTransition transition)
        {
            int transitionCount = transitions.Count;
            if (transitionCount == 0)
            {
                Preconditions.CheckArgument(!transition.Instant.IsValid,
                    nameof(transition), "First transition must be at the start of time");
                transitions.Add(transition);
                return true;
            }

            ZoneTransition lastTransition = transitions[transitionCount - 1];
            if (!transition.IsTransitionFrom(lastTransition))
            {
                return false;
            }

            // A transition after the "beginning of time" one will always be valid.
            if (lastTransition.Instant == Instant.BeforeMinValue)
            {
                transitions.Add(transition);
                return true;
            }

            Offset lastOffset = transitions.Count < 2 ? Offset.Zero : transitions[transitions.Count - 2].WallOffset;
            Offset newOffset = lastTransition.WallOffset;
            // If the local time just before the new transition is the same as the local time just
            // before the previous one, just replace the last transition with new one.
            // This code is taken from Joda Time, and is not terribly clear. It appears to occur when
            // a new rule set starts to apply, and the TransitionIterator effectively starts with the wrong rule.
            // It appears to be doing the right thing, but a full-scale refactor might be able to remove it...

            // Example: America/Juneau
            // Zone rules around the problematic transition:
            // 9:00  US Y% sT    1980 Oct 26  2:00
            // -8:00  US P% sT    1983 Oct 30  2:00
            // - 9:00  US Y% sT    1983 Nov 30

            // We have:
            // Previous but one: { PDT at 1983 - 04 - 24T10: 00:00Z - 08[+01]}
            // Previous:         { YDT at 1983 - 10 - 30T09: 00:00Z - 09[+01]}
            // Replaced by:      { YST at 1983 - 10 - 30T10: 00:00Z - 09[+00]}
            // The transition *to* YDT in October is clearly spurious, but that's what TransitionIterator gives us.
            LocalInstant lastLocalStart = lastTransition.Instant.Plus(lastOffset);
            LocalInstant newLocalStart = transition.Instant.Plus(newOffset);
            if (lastLocalStart == newLocalStart)
            {
                transitions.RemoveAt(transitionCount - 1);
                return AddTransition(transitions, transition);
            }
            transitions.Add(transition);
            return true;
        }
    }
}