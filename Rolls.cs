using System.Linq;
using System.Collections.Generic;
using System;

namespace AutoExpedition;

/// <summary>
/// What a Liquid Verisium returns, measured rather than assumed.
///
/// **A roll replaces the whole remnant, so pricing one is a question about the population the game
/// generates - and nothing in the client answers it.** Expedition2RunesWeight is named for the
/// weights and exposes none. The only way to the number is to count remnants, which is what Census
/// has been doing; this file is what came back.
///
/// Taken from 476 deduplicated fresh remnants at area levels 79-81, and checked against 44 rolled
/// remnants whose combination had locked. The dedup matters: the file is appended across sessions and holds
/// several rows per remnant, so a count taken from it straight is weighted by how long somebody
/// stood in each map rather than by what the game made.
///
/// **The fresh population stands in for what a roll returns, and that substitution was tested
/// rather than assumed.** A permutation test over the 73 clean post-roll observations against the
/// 476 fresh ones gives p = 0.95 on rune identity and p = 0.67 on socket count: no difference
/// detectable. Honest about its own power, that test would only have caught a rune whose rate had
/// roughly quintupled - so this is "nothing contradicts it" rather than "proved". Fifty more clean
/// rolls would tighten it considerably.
///
/// **Two known substitutions, both flagged where they are used:**
///
/// - The rune table is the FIXED rune's frequency, which is a different draw from what lands in a
///   propagating slot. Only 14 observations of the latter exist. The shapes should be similar and
///   that is a guess; Census now records passedRunes, so it becomes measurable within a session or
///   two.
/// - Opulent appeared nowhere in 643 observations, which left it either unreachable as a placed
///   rune or under half a percent. **One has now been seen**, on a Craggy Peninsula remnant,
///   propagating at its full +40% - so it is the second, and listing it at zero was the one
///   reading the sample could not distinguish and the one that mattered.
///
///   At zero a roll was never advised in hope of one, and Opulent is worth ten times any other
///   propagating rune: +40% against +4%. That is the whole of what this correction changes.
///
///   Listed at 0.002, which is one sighting against the 643 that found none - the same order as
///   the rarest rune already in the table, and inside the half a percent the null bounded it by.
///   It is one observation and should be read as such; the number moves the moment Census has a
///   second. See Wrt rune:opulent.
///
/// Every number here is provisional by construction. They are written down so that the advice can
/// be re-derived when the sample grows, rather than tuned by feel.
/// </summary>
internal static class Rolls
{
    /// <summary>
    /// How many sockets a remnant comes up with. Shares, summing to one.
    ///
    /// **Seven, eight and nine are listed rather than folded into six, which is a change.** They are
    /// three observations each out of the 476 and were collapsed on the reasoning that a
    /// distribution built to be sampled from should not carry a tail nobody has seen enough of to
    /// believe. That reasoning is sound about confidence and wrong about consequence: folding them
    /// into six does not make the tail uncertain, it makes it absent, and everything downstream
    /// then behaves as though remnants larger than six do not exist.
    ///
    /// They are where the largest rewards are. Mirror of Kalandra and Hinekora's Lock are nine rune
    /// recipes and so need nine sockets; Krillson's Bay Key needs ten. A reroll advisor that cannot
    /// produce a nine socket remnant cannot price the possibility of one, and that is the tail that
    /// matters rather than a rounding detail.
    ///
    /// Each is 3/476 = 0.0063, and six gives up the 9/476 it was carrying for them: 0.177 - 0.019.
    /// Ten is still absent because none has been seen at all, which is a different statement from
    /// seen rarely - see the Krillson's Bay Key note in NOTES 8z.
    ///
    /// **Three observations is three observations.** These shares are the least reliable numbers in
    /// this file and should be read as "rare, and how rare is not settled".
    /// </summary>
    public static readonly (int Sockets, float Share)[] Sockets =
    {
        (3, 0.221f),
        (4, 0.362f),
        (5, 0.240f),
        (6, 0.158f),
        (7, 0.0063f),
        (8, 0.0063f),
        (9, 0.0063f),
    };

    /// <summary>
    /// The chance of a remnant carrying two propagating slots rather than one, as a fraction.
    ///
    /// **The game states this and the player chooses it, so it is a setting rather than a number
    /// here.** The atlas node "Double or Nothing" decides how Verisium remnants add runic modifiers
    /// to future remnants - which is what this plugin calls a propagating rune - and it offers three
    /// choices. One is "25% chance to add an additional Runic Modifier". A constant cannot be right
    /// for a player who took a different one.
    ///
    /// **It was 0.283, sampled rather than stated**, with the sample flat across socket counts -
    /// 65% one slot at three sockets, 76% at six. That is within noise of the stated 25% for a
    /// sample that size, and eight remnants on one site later came out at exactly 2 of 8. A figure
    /// the game prints beats one counted off a handful of remnants, so the default is the printed
    /// one and the sample is corroboration.
    ///
    /// Asked rather than read, because nothing states the answer: the node shows as allocated in
    /// IngameUi.AtlasTreePanel.Passives, and its only stat is a marker saying it opens a chooser.
    /// See Debug.DoubleOrNothingDouble.
    /// </summary>
    public static float TwoSlots(AutoExpeditionSettings settings) =>
        Safe.Read(() => settings.Debug.DoubleOrNothingDouble.Value, true) ? Stated : 0f;

    /// <summary>The chance the node prints for its "Double" choice.</summary>
    private const float Stated = 0.25f;

    /// <summary>
    /// How often a fresh socket comes up as each rune, read off the table.
    ///
    /// **It was an array here, and that made the list of runes that exist a second store.** Twenty-nine
    /// entries against thirty-four rune rows in a live table: the two could disagree, and nothing would
    /// have said which was right. A rune a patch adds had to be typed in twice.
    ///
    /// Still measured rather than read from the game, which is settled rather than open -
    /// Expedition2RunesWeight is named for the weights and exposes none. What moved is where the
    /// measurement lives: a share column on the rune's own row, editable, beside the effect it belongs
    /// to. See Wrt.Row.Share.
    ///
    /// Normalised on use rather than here, because the shares are a measurement and will not sum to
    /// exactly one - Rolling.Enumerated divides by the weight it accumulated for that reason, and
    /// anything else added later should do the same.
    /// </summary>
    public static (string Rune, float Share)[] Runes
    {
        get
        {
            if (_runes != null && _at == Wrt.Revision)
                return _runes;

            var found = new List<(string, float)>(34);

            foreach (var (id, _) in Wrt.Standing.Concat(Wrt.Yours))
            {
                if (!id.StartsWith("rune:", StringComparison.Ordinal))
                    continue;

                var share = Wrt.Of(id)?.Share;

                if (share is > 0f)
                    found.Add((id[5..], share.Value));
            }

            _at = Wrt.Revision;

            return _runes = found.ToArray();
        }
    }

    private static (string Rune, float Share)[] _runes;
    private static int _at = -1;


    /// <summary>
    /// What the reward on the far side of a roll is worth, in exalts.
    ///
    /// **A roll LOCKS the combination, which is the whole reason this is not the fresh table.** A
    /// fresh remnant is worth the best of the options you get to pick between; a rolled one is worth
    /// the single thing it landed on, and nothing can move it. Comparing the two directly would be
    /// comparing an upper bound with an outcome.
    ///
    /// **A RANDOM option from a fresh remnant, which is the quantity a roll actually lands on.**
    /// Not the best one: a fresh remnant is worth the best of what it offers because you get to
    /// pick, and a rolled one is worth the single thing it locked. Substituting best-for-locked
    /// would price a roll at 93 exalts instead of 65, and the error is in the direction that makes
    /// the advisor keenest.
    ///
    /// Two thousand and seventy options across 454 fresh remnants, each remnant weighted once
    /// however many options it has - a roll draws a remnant and then a combination, so pooling the
    /// options raw would over-count the remnants that offer many. Written as a hundred equally
    /// spaced quantiles, so a uniform draw reproduces the distribution.
    ///
    /// **It disagrees with the forty four directly observed locked rolls, and the disagreement is
    /// the thing to watch.** Those measured mean 27.3, median 14.6, highest 177. This measures mean
    /// 64.9, median 9.4, highest 885. The medians are compatible - forty four samples put a wide
    /// interval on a median - but the means are not, because this carries a tail the observations
    /// never touched. If the two populations were the same, seeing nothing above 177 in 44 draws is
    /// a 1.6% event.
    ///
    /// So one of two things is true: forty four rolls were unlucky, or the game does not lock
    /// combinations uniformly and steers away from the richest. Nothing here can tell them apart,
    /// and the choice matters - this table makes a roll look worth about 2.4x what the direct
    /// observations say. Taken anyway because 44 is too thin to price anything and this rests on a
    /// sample 47 times larger; the observations below are kept beside it so the question stays open
    /// rather than being quietly settled.
    ///
    /// Thirty two remnants were dropped as unpriceable, mostly generic uniques. That biases these
    /// numbers DOWNWARD to the extent that the hand-written price list under-rates them.
    /// </summary>
    public static readonly float[] Reward =
    {
        0.09f, 0.09f, 1.00f, 1.00f, 1.00f, 1.00f, 1.27f, 1.27f,
        1.65f, 1.97f, 1.97f, 2.00f, 2.00f, 2.00f, 2.00f, 2.14f,
        2.24f, 2.86f, 2.93f, 3.00f, 3.00f, 3.12f, 3.12f, 3.33f,
        3.43f, 3.43f, 3.74f, 3.80f, 3.81f, 4.00f, 4.32f, 4.32f,
        4.82f, 5.15f, 5.38f, 5.90f, 5.90f, 6.15f, 6.42f, 6.42f,
        6.58f, 6.82f, 6.82f, 7.23f, 8.12f, 8.12f, 8.71f, 8.71f,
        8.71f, 9.43f, 9.62f, 10.29f, 10.29f, 11.73f, 11.73f, 12.23f,
        12.23f, 12.33f, 12.85f, 12.92f, 13.07f, 13.64f, 13.82f, 14.76f,
        16.58f, 17.37f, 17.37f, 17.37f, 19.88f, 21.82f, 23.73f, 27.10f,
        27.10f, 27.24f, 28.33f, 28.68f, 29.29f, 36.98f, 40.99f, 44.04f,
        46.64f, 46.64f, 49.78f, 54.19f, 93.27f, 93.27f, 93.67f, 120.44f,
        139.91f, 176.95f, 285.75f, 294.87f, 294.87f, 294.87f, 294.87f, 414.56f,
        442.48f, 659.29f, 857.26f, 884.96f,
    };

    /// <summary>
    /// The forty four rolls actually watched, kept as the check on the table above.
    ///
    /// Nothing reads these. They are here so that the disagreement described above can be re-tested
    /// as the sample grows rather than being taken on trust: if a few hundred more observed rolls
    /// keep landing under 180, the fresh table is wrong about the tail and this is what replaces it.
    /// </summary>
    public static readonly float[] Observed =
    {
        1.00f, 1.00f, 3.00f, 3.38f, 4.10f, 4.60f, 5.15f, 5.39f,
        5.65f, 5.90f, 5.90f, 6.42f, 6.71f, 8.12f, 8.12f, 9.62f,
        10.14f, 10.29f, 11.73f, 12.23f, 13.07f, 13.07f, 16.14f, 16.73f,
        17.04f, 17.37f, 19.53f, 23.18f, 23.18f, 27.24f, 27.24f, 28.33f,
        37.39f, 43.76f, 44.02f, 44.02f, 44.04f, 44.28f, 46.64f, 49.78f,
        66.46f, 93.27f, 138.19f, 176.95f,
    };

    /// <summary>
    /// The average of the table above, in exalts.
    ///
    /// The directly observed average is 27.3. The gap is the tail, and which of the two is right is
    /// the open question described on Reward.
    /// </summary>
    public const double Average = 64.9;

    /// <summary>Draws a socket count.</summary>
    public static int Socketed(Random random) => Pick(random, Sockets);

    /// <summary>Draws how many propagating slots, which is one or two and never three.</summary>
    public static int Slots(Random random, AutoExpeditionSettings settings) =>
        random.NextDouble() < TwoSlots(settings) ? 2 : 1;

    /// <summary>Draws a rune name.</summary>
    public static string Rune(Random random)
    {
        var roll = random.NextDouble();
        var seen = 0d;

        foreach (var (rune, share) in Runes)
        {
            seen += share;

            if (roll <= seen)
                return rune;
        }

        return Runes[^1].Rune;
    }

    /// <summary>Draws a locked reward, in exalts. See Reward for what it is drawn from.</summary>
    public static double Paid(Random random) => Reward[random.Next(Reward.Length)];

    private static int Pick(Random random, (int Value, float Share)[] from)
    {
        var roll = random.NextDouble();
        var seen = 0d;

        foreach (var (value, share) in from)
        {
            seen += share;

            if (roll <= seen)
                return value;
        }

        return from[^1].Value;
    }

    /// <summary>
    /// How often each (socket count, rune, slot) has been the pinned one, counted rather than shared.
    ///
    /// **A global share per rune is structurally wrong, and Power is the proof.** Its share over all
    /// fresh readings is 1.75%, while seven of the ten pins a nine socket remnant admits are Power -
    /// and it is admissible at no three socket remnant and no seven socket one at all. The rate is
    /// conditional on the socket count and a marginal number cannot express that. Weighting a nine
    /// socket shape at 1.75% is worst exactly where it costs most, because nine rune recipes are
    /// where Mirror of Kalandra and Hinekora's Lock are. See NOTES 8z.
    ///
    /// **Counts rather than fractions, so there is one description of the fact.** The share follows
    /// from the count and the admitted set, and the admitted set depends on the area level, so a
    /// stored fraction would be a second answer that goes stale the moment the level changes. See
    /// ShareOfPin.
    ///
    /// Measured at area level 79, fresh remnants only, deduplicated on every column of
    /// remnants.csv but `when` - a row is not a remnant, see NOTES 8z. Rerolled remnants are a
    /// different draw and are not pooled in.
    ///
    /// **Nothing here is a constraint.** Which pins are possible comes from the game's own table,
    /// through Valuation.FixedRunesPossibleAt, and is exact. This only says how often.
    /// </summary>
    public static readonly (int Sockets, string Rune, int Slot, int Seen)[] Pins =
    {
        // 3 sockets - 177 readings
        (3, "rune:adaptive", 0, 42), (3, "rune:adaptive", 1, 15), (3, "rune:arcane", 1, 13),
        (3, "rune:bloodletting", 2, 24), (3, "rune:electrocuting", 2, 12),
        (3, "rune:momentum", 0, 13), (3, "rune:prismatic", 0, 13), (3, "rune:tidal", 2, 30),
        (3, "rune:ward", 1, 15),

        // 4 sockets - 333 readings
        (4, "rune:adaptive", 0, 23), (4, "rune:arcane", 0, 28), (4, "rune:cyclonic", 3, 20),
        (4, "rune:earth", 0, 8), (4, "rune:gasp", 0, 18), (4, "rune:moon", 0, 15),
        (4, "rune:moon", 1, 18), (4, "rune:oath", 0, 10), (4, "rune:oath", 1, 16),
        (4, "rune:power", 2, 1), (4, "rune:prismatic", 0, 20), (4, "rune:rebirth", 1, 24),
        (4, "rune:sky", 0, 14), (4, "rune:soul", 2, 4), (4, "rune:tidal", 2, 45),
        (4, "rune:toxic", 0, 26), (4, "rune:ward", 1, 19), (4, "rune:wisdom", 3, 24),

        // 5 sockets - 226 readings
        (5, "rune:adaptive", 0, 17), (5, "rune:arcane", 1, 10), (5, "rune:arcane", 3, 5),
        (5, "rune:bloodletting", 1, 8), (5, "rune:bloodletting", 3, 1), (5, "rune:bond", 1, 1),
        (5, "rune:bond", 4, 21), (5, "rune:celestial", 0, 11), (5, "rune:cold", 1, 13),
        (5, "rune:cyclonic", 0, 8), (5, "rune:death", 2, 4), (5, "rune:electrocuting", 2, 7),
        (5, "rune:fire", 1, 12), (5, "rune:power", 2, 8), (5, "rune:prismatic", 0, 3),
        (5, "rune:rage", 1, 3), (5, "rune:rebirth", 1, 13), (5, "rune:sky", 1, 7),
        (5, "rune:sky", 2, 16), (5, "rune:stone", 3, 19), (5, "rune:tidal", 1, 7),
        (5, "rune:tidal", 4, 9), (5, "rune:toxic", 1, 4), (5, "rune:ward", 2, 19),

        // 6 sockets - 171 readings
        (6, "rune:adaptive", 0, 1), (6, "rune:arcane", 2, 2), (6, "rune:bond", 2, 9),
        (6, "rune:celestial", 0, 3), (6, "rune:cold", 0, 18), (6, "rune:cyclonic", 0, 7),
        (6, "rune:cyclonic", 2, 5), (6, "rune:fire", 0, 15), (6, "rune:lightning", 0, 5),
        (6, "rune:momentum", 0, 1), (6, "rune:moon", 3, 16), (6, "rune:oath", 0, 15),
        (6, "rune:power", 2, 1), (6, "rune:power", 3, 1), (6, "rune:power", 4, 1),
        (6, "rune:prismatic", 2, 12), (6, "rune:rebirth", 1, 13), (6, "rune:sky", 1, 12),
        (6, "rune:sky", 2, 5), (6, "rune:time", 0, 2), (6, "rune:time", 5, 3),
        (6, "rune:toxic", 0, 3), (6, "rune:vision", 1, 7), (6, "rune:ward", 3, 14),

        // 7 and 9 sockets - 1 and 6 readings. Kept because they are observations, and they barely
        // move the answer: smoothed against twelve and ten admitted pairs they are nearly uniform.
        (7, "rune:cyclonic", 0, 1),
        (9, "rune:celestial", 0, 1), (9, "rune:cyclonic", 0, 1), (9, "rune:power", 2, 1),
        (9, "rune:power", 3, 3),
    };

    /// <summary>
    /// How likely this pin is on a remnant of this many sockets, as a fraction.
    ///
    /// **Smoothed, so a pin the census has not caught yet is rare rather than impossible.** Power at
    /// slot 5 on six sockets is admitted by the game and appeared in none of 171 readings; stored as
    /// a flat nought it would make the enumeration refuse a shape the game allows. One imagined
    /// observation per admitted pin is the standard correction and it costs almost nothing where the
    /// counts are large: at four sockets the busiest pin moves from 13.5% to 13.4%.
    ///
    /// **It also makes a thin socket count fall back to uniform on its own.** With no readings at
    /// all every pin is (0+1)/(0+k), which is uniform over the admitted set - the least-assumption
    /// reading NOTES 8z settles on - and one reading tilts it by one part in k. So eight sockets
    /// needs no special case and seven and nine need no apology.
    /// </summary>
    /// <param name="admitted">
    /// How many pins the game admits at this socket count, from Valuation.FixedRunesPossibleAt.
    ///
    /// Passed in rather than counted here because it is a property of the AREA LEVEL and this table
    /// is not: the census was gathered at level 79 and the same code runs at 78 and 81, where the
    /// admitted set differs. A pin admitted now and absent from the table simply scores the
    /// smoothing weight, which is the right answer for something never seen.
    /// </param>
    public static float ShareOfPin(int sockets, string rune, int slot, int admitted)
    {
        if (admitted <= 0)
            return 0f;

        var seen = 0;
        var total = 0;

        foreach (var (had, named, at, count) in Pins)
        {
            if (had != sockets)
                continue;

            total += count;

            if (at == slot && string.Equals(named, rune, StringComparison.OrdinalIgnoreCase))
                seen = count;
        }

        return (seen + 1f) / (total + admitted);
    }
}
