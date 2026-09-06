using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// The roster card: everything the project already knows about a player,
    /// rendered as one screen.
    ///
    /// WHY THIS EXISTS AT ALL. Every number on this card was already being
    /// written to disk and read by nobody. Reward_Settings carries a provenance
    /// block - trainingSteps, trainingRunId, trainedOn, evalWinRate,
    /// evalEpisodes - that scripts/update-model.ps1 and scripts/evaluate.ps1
    /// stamp automatically after every run, so it is a career record that keeps
    /// itself current. Until now the menu surfaced two lines of it on a slot
    /// card and discarded the rest.
    ///
    /// WHY THE ATTRIBUTE BARS ARE RATIOS AND NOT SCORES. A player's style in
    /// this project is literally its reward mix (docs/players.md: personality
    /// lives in the terminal rewards and trait scales, while the locomotion
    /// mechanics are shared and must match code). Those values have no natural
    /// ceiling, so an absolute bar would need magic constants that drift the
    /// moment somebody retunes an asset. Each bar is therefore the profile's
    /// value as a share of the best value ON THIS ROSTER, which is
    /// self-normalising: it needs no constants, it survives retuning, and it
    /// answers the question a player actually asks - not "how good is 1.4" but
    /// "who is the best finisher here".
    ///
    /// The consequence worth knowing: bars are comparative, so whoever leads an
    /// attribute always reads 100. COVER and CONTROL are zero for everyone
    /// except KIM and NICK respectively, and that is not a rendering fault -
    /// those are single-owner signature traits, which is exactly what the card
    /// should show.
    ///
    /// Presentation only. A pure static builder with no MonoBehaviour and no
    /// scene dependency, so the arithmetic below is directly unit-testable.
    /// </summary>
    public static class Agent_PlayerCard
    {
        /// <summary>Body mass (kg) of the STANDARD reference physique.</summary>
        const float REFERENCE_MASS = 75f;

        /// <summary>Below this, a roster maximum counts as unspent.</summary>
        const float EPSILON = 0.0000001f;

        /// <summary>One rendered attribute row: a name, a 0-1 bar, a readout.</summary>
        public readonly struct Attribute
        {
            public readonly string Label;

            /// <summary>Share of the roster's best value for this attribute, 0-1.</summary>
            public readonly float Share;

            /// <summary>The underlying asset value, kept for tests and tooltips.</summary>
            public readonly float Raw;

            public Attribute(string label, float share, float raw)
            {
                Label = label;
                Share = share;
                Raw = raw;
            }
        }

        /// <summary>Number of attribute rows <see cref="Attributes"/> produces.</summary>
        public const int ATTRIBUTE_COUNT = 7;

        /// <summary>
        /// The seven attributes, in display order, for one profile measured
        /// against the roster it sits in.
        ///
        /// Each is a straight read of one serialized field - no blending, no
        /// invented composite - so a designer who changes an asset can predict
        /// exactly which bar moves. A null or empty roster degrades to measuring
        /// the profile against itself, which renders every spent bar full
        /// rather than throwing or dividing by zero.
        /// </summary>
        public static Attribute[] Attributes(Reward_Settings profile, Reward_Settings[] roster)
        {
            if (profile == null) return Array.Empty<Attribute>();

            // Roster maxima. Conceding is stored negative and read as a
            // magnitude: a bigger fear of conceding is more defensive
            // discipline, not less of it.
            float bestFinishing = Mathf.Abs(profile.goalScorer);
            float bestVision = Mathf.Abs(profile.assist);
            float bestDiscipline = Mathf.Abs(profile.goalConceded);
            float bestShooting = Mathf.Abs(profile.ballToGoalVelocityScale);
            float bestControl = Mathf.Abs(profile.possessionScale);
            float bestCover = Mathf.Abs(profile.defensivePositionScale);
            float bestPower = Mathf.Abs(profile.bodyMass);

            if (roster != null)
            {
                for (int rosterIndex = 0; rosterIndex < roster.Length; rosterIndex++)
                {
                    Reward_Settings other = roster[rosterIndex];
                    if (other == null) continue;
                    bestFinishing = Mathf.Max(bestFinishing, Mathf.Abs(other.goalScorer));
                    bestVision = Mathf.Max(bestVision, Mathf.Abs(other.assist));
                    bestDiscipline = Mathf.Max(bestDiscipline, Mathf.Abs(other.goalConceded));
                    bestShooting = Mathf.Max(bestShooting, Mathf.Abs(other.ballToGoalVelocityScale));
                    bestControl = Mathf.Max(bestControl, Mathf.Abs(other.possessionScale));
                    bestCover = Mathf.Max(bestCover, Mathf.Abs(other.defensivePositionScale));
                    bestPower = Mathf.Max(bestPower, Mathf.Abs(other.bodyMass));
                }
            }

            return new[]
            {
                Row("FINISHING", profile.goalScorer, bestFinishing),
                Row("VISION", profile.assist, bestVision),
                Row("DISCIPLINE", profile.goalConceded, bestDiscipline),
                Row("SHOOTING", profile.ballToGoalVelocityScale, bestShooting),
                Row("CONTROL", profile.possessionScale, bestControl),
                Row("COVER", profile.defensivePositionScale, bestCover),
                Row("POWER", profile.bodyMass, bestPower),
            };
        }

        static Attribute Row(string label, float value, float best)
        {
            float magnitude = Mathf.Abs(value);
            // best <= 0 means nobody on the roster spends anything here, so the
            // honest bar is empty rather than full.
            float share = best > EPSILON ? Mathf.Clamp01(magnitude / best) : 0f;
            return new Attribute(label, share, magnitude);
        }

        // -- Text ------------------------------------------------------------

        /// <summary>What actually drives this body once a match starts.</summary>
        public static string DriverText(Reward_Settings profile, Reward_Settings ruleBot)
        {
            if (profile == null) return "-";
            if (ReferenceEquals(profile, ruleBot)) return "RULE-BASED BOT";
            return profile.brainModel != null ? "TRAINED AI" : "UNTRAINED - PLAYS AS BOT";
        }

        /// <summary>
        /// The measured record, or an explicit statement that there is not one.
        /// evalWinRate defaults to -1 precisely so "never graded" stays
        /// distinguishable from "graded 0%", and the card must not collapse
        /// that distinction into a confident-looking zero.
        /// </summary>
        public static string RecordText(Reward_Settings profile, Reward_Settings ruleBot)
        {
            if (profile == null) return "-";
            if (ReferenceEquals(profile, ruleBot)) return "BENCHMARK";
            if (profile.evalWinRate < 0f) return "UNGRADED";
            return $"{profile.evalWinRate * 100f:0.#}%";
        }

        /// <summary>Sample size behind <see cref="RecordText"/>, as a caption.</summary>
        public static string RecordCaption(Reward_Settings profile, Reward_Settings ruleBot)
        {
            if (profile == null) return string.Empty;
            if (ReferenceEquals(profile, ruleBot)) return "the opponent every brain is measured against";
            if (profile.evalWinRate < 0f) return "no evaluation has been run on this brain";
            return $"wins vs BOT over {profile.evalEpisodes} episodes";
        }

        public static string StepsText(Reward_Settings profile, Reward_Settings ruleBot)
        {
            if (profile == null) return "-";
            if (ReferenceEquals(profile, ruleBot)) return "SCRIPTED";
            if (profile.trainingSteps <= 0) return "NONE";
            return FormatSteps(profile.trainingSteps);
        }

        public static string FormatSteps(int steps)
        {
            if (steps >= 1_000_000) return $"{steps / 1_000_000f:0.#}M";
            if (steps >= 1_000) return $"{steps / 1_000f:0}k";
            return steps.ToString();
        }

        /// <summary>
        /// Physique in the terms the traction model actually uses. Drive force
        /// scales with mass, so every physique shares one top speed and mass
        /// buys momentum rather than pace. Calling a heavy build "slow" would
        /// be precisely wrong here, so the caption says what mass really does.
        /// </summary>
        public static string PhysiqueCaption(Reward_Settings profile)
        {
            if (profile == null) return string.Empty;
            float ratio = profile.bodyMass / REFERENCE_MASS;
            if (Mathf.Abs(ratio - 1f) < 0.02f) return "reference build - the shared top speed";
            int percent = Mathf.RoundToInt(Mathf.Abs(ratio - 1f) * 100f);
            string direction = ratio > 1f ? "more" : "less";
            return $"{percent}% {direction} momentum, same top speed";
        }

        /// <summary>Where the deployed brain came from, or why there is not one.</summary>
        public static string ProvenanceText(Reward_Settings profile, Reward_Settings ruleBot)
        {
            if (profile == null) return string.Empty;
            if (ReferenceEquals(profile, ruleBot))
            {
                return "Agent_HeuristicBot - hand-written, never trained";
            }
            if (profile.brainModel == null)
            {
                return "no brain deployed - falls back to the scripted bot";
            }

            string run = string.IsNullOrEmpty(profile.trainingRunId) ? "unknown run" : profile.trainingRunId;
            string date = string.IsNullOrEmpty(profile.trainedOn) ? "date unrecorded" : profile.trainedOn;
            return $"{run} - deployed {date}";
        }

        // -- UI --------------------------------------------------------------

        /// <summary>
        /// Builds the card body for one profile. The caller owns the scrim, the
        /// navigation and the close affordance, so the same builder serves the
        /// menu today and a match-day squad screen later without dragging
        /// either one's chrome along with it.
        /// </summary>
        public static VisualElement Build(Reward_Settings profile, Reward_Settings[] roster,
            Reward_Settings ruleBot)
        {
            var card = new VisualElement();
            card.AddToClassList("card");

            if (profile == null)
            {
                var missing = new Label("no profile");
                missing.AddToClassList("text-muted");
                card.Add(missing);
                return card;
            }

            // Colour band: the same tint the body wears on the pitch, so the
            // card and the player on screen read as the same person.
            var band = new VisualElement();
            band.AddToClassList("card__band");
            band.style.backgroundColor = profile.playerColor;
            card.Add(band);

            var name = new Label(profile.playerName);
            name.AddToClassList("card__name");
            card.Add(name);

            var driver = new Label(DriverText(profile, ruleBot));
            driver.AddToClassList("card__driver");
            driver.style.color = profile.playerColor;
            card.Add(driver);

            card.Add(BuildTiles(profile, ruleBot));

            if (!string.IsNullOrEmpty(profile.personalityNotes))
            {
                var notes = new Label(profile.personalityNotes);
                notes.AddToClassList("card__note");
                card.Add(notes);
            }

            var heading = new Label("ATTRIBUTES - RELATIVE TO ROSTER");
            heading.AddToClassList("card__heading");
            card.Add(heading);

            Attribute[] attributes = Attributes(profile, roster);
            for (int attributeIndex = 0; attributeIndex < attributes.Length; attributeIndex++)
            {
                card.Add(BuildAttributeRow(attributes[attributeIndex], profile.playerColor));
            }

            var provenance = new Label(ProvenanceText(profile, ruleBot));
            provenance.AddToClassList("card__prov");
            card.Add(provenance);

            return card;
        }

        static VisualElement BuildTiles(Reward_Settings profile, Reward_Settings ruleBot)
        {
            var row = new VisualElement();
            row.AddToClassList("card__tiles");
            row.Add(Tile(RecordText(profile, ruleBot), "RECORD", RecordCaption(profile, ruleBot)));
            row.Add(Tile(StepsText(profile, ruleBot), "TRAINED", "trainer steps behind this brain"));
            row.Add(Tile($"{profile.bodyMass:0} kg", "BUILD", PhysiqueCaption(profile)));
            return row;
        }

        static VisualElement Tile(string value, string label, string caption)
        {
            var tile = new VisualElement();
            tile.AddToClassList("card__tile");

            var valueLabel = new Label(value);
            valueLabel.AddToClassList("card__tile-value");
            tile.Add(valueLabel);

            var nameLabel = new Label(label);
            nameLabel.AddToClassList("card__tile-label");
            tile.Add(nameLabel);

            // The caption is the honesty channel: it carries the sample size
            // behind a win rate and the real meaning behind a mass. A bare
            // "25.7%" with no n is exactly the kind of number this project's
            // own notes warn against reading as signal.
            var captionLabel = new Label(caption);
            captionLabel.AddToClassList("card__tile-caption");
            tile.Add(captionLabel);

            return tile;
        }

        static VisualElement BuildAttributeRow(Attribute attribute, Color fillColor)
        {
            var row = new VisualElement();
            row.AddToClassList("attr");

            var label = new Label(attribute.Label);
            label.AddToClassList("attr__label");
            row.Add(label);

            var track = new VisualElement();
            track.AddToClassList("attr__track");

            var fill = new VisualElement();
            fill.AddToClassList("attr__fill");
            fill.style.backgroundColor = fillColor;
            // Percent width so the bar follows its track when the panel is
            // re-padded for a safe area, rather than being pinned to a
            // reference-space pixel width that is wrong on a notched device.
            //
            // Start at zero and set the real value a frame later, so the USS
            // width transition has something to animate FROM. A card is rebuilt
            // from scratch on every page, so its bars are new elements each
            // time; without this they would simply appear at their final width
            // and the transition on .attr__fill would never once run. Same
            // one-frame idiom as Agent_UIStyle.PlayEntrance.
            float targetPercent = attribute.Share * 100f;
            fill.style.width = new StyleLength(new Length(0f, LengthUnit.Percent));
            fill.schedule
                .Execute(() => fill.style.width =
                    new StyleLength(new Length(targetPercent, LengthUnit.Percent)))
                .ExecuteLater(16);
            track.Add(fill);
            row.Add(track);

            // An unspent trait reads as a dash rather than "0". Zero here means
            // "this player does not do that at all", which an absence conveys
            // more clearly than a number does.
            var value = new Label(attribute.Share <= 0f ? "-" : $"{attribute.Share * 100f:0}");
            value.AddToClassList("attr__value");
            row.Add(value);

            return row;
        }
    }
}
