using ExileCore2;
using ExileCore2.PoEMemory.Components;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// Objects a blast sets off that the plugin has never been taught, and what you have decided they
/// are worth.
///
/// **Every new tileset brings objects nobody has catalogued, and until now each one was a code
/// change.** Unexpected already finds them - it looks for the glow_epk state rather than for a
/// metadata prefix, which is the game stating that a blast acts on a thing - but finding them only
/// ever produced a red line and an invitation to go and read the source. A Grand Expedition can
/// introduce half a dozen at once.
///
/// So what was a code change becomes a slider. Anything found is written down here by its metadata,
/// given a nominal weight, and listed in Weights - Unknown entities, where it can be priced by
/// whoever has just opened one and seen what came out.
///
/// **In a file of its own rather than in the settings**, which is the point of it. The settings file
/// is one blob per plugin; this is a list that grows as the game does, is legible, is hand-editable,
/// and can be handed to somebody else by copying one file. When enough of it settles down, the
/// entries become defaults in Weighing and the file empties itself of them.
///
/// The weight ships at one, not nought. Nought would keep an unknown object out of the plan, which
/// is the safe default and the wrong one: a marker worth nothing is a marker the planner will not
/// draw, will not route past, and will never prompt anybody to look at. One is enough to exist and
/// far too little to bend a chain, so a site full of undecided objects plans as it did before while
/// every one of them is on screen asking to be read.
/// </summary>
internal static class Unknowns
{
    /// <summary>What one undecided object is worth until somebody decides. See the class summary.</summary>
    /// <summary>
    /// Which spelling of the key this file was written under.
    ///
    /// **A row only means anything under the key that produced it**, and this key has been
    /// respelled three times: the mods moved out of it onto rows of their own, ground labels
    /// started being matched by entity rather than by cell, and the digits in a label stopped
    /// counting. Each of those re-files every object, and the rows written under the old
    /// spelling do not go anywhere - they sit beside the new ones, looking like duplicates of
    /// things already priced, which is what they were reported as all three times.
    ///
    /// So this tracks the key rather than the columns. A bump costs the weights set under a
    /// spelling that no longer exists and saves a list nobody can trust. Bump it whenever Key
    /// changes what it reads or how it writes it.
    /// </summary>
    private const int Keyed = 2;

    public const float Default = 1f;

    /// <summary>
    /// What an object is assumed to be, in world units, until somebody says otherwise.
    ///
    /// Detonator.MinimumExtent, converted: the smallest an entity can be, which is what an ordinary
    /// marker is. Not an average of readings - 11,777 recorded crossings agree with it, putting
    /// every marker art between 2.16 and 2.26 grid, and that spread is the crossing tool's own
    /// resolution rather than a difference between the arts.
    ///
    /// World units because that is what a row's Size cell holds and what this is compared against.
    ///
    /// The two things it is wrong about are in Extents and never reach this: the siren eggs and the
    /// sub-area entrance caps, both 17.3 grid. Both were found by a plan being built against a
    /// marker's size and being visibly wrong about it, which is what the per-row slider below is
    /// for - so the next one can be answered in the menu instead of in a commit.
    /// </summary>
    public const float Size = Detonator.MinimumExtent * Detonator.GridToWorld;

    /// <summary>Where the file lives. Set once by the plugin; empty turns remembering off.</summary>
    public static string Home { get; set; } = "";

    /// <summary>What has been decided about one kind of object.</summary>
    internal sealed class Priced
    {
        /// <summary>What catching it is worth.</summary>
        public float Weight = Default;

        /// <summary>
        /// What share it passes on, and to WHAT - "rare_monster=50, excavated_chest=20".
        ///
        /// **A bare percentage could only ever mean one thing, and the game means several.** The
        /// number used to multiply one accumulator, the monsters unearthed downstream, so an effect
        /// reading "increased Quantity of Items Found in Excavated Chests" had nowhere to go: there
        /// was no number you could type that made it mean what it says. A scope names the tags it
        /// reaches, and the search builds an accumulator for each tag some effect actually names.
        ///
        /// Empty means undecided and contributes nothing. That is deliberate and it is why the table
        /// draws it red: an effect that quietly multiplied everything because nobody said otherwise
        /// is worth several times what it should be, and it would never announce itself.
        ///
        /// See Tags for the vocabulary and Tags.Scope for what this accepts.
        /// </summary>
        public string Scope = "";

        /// <summary>
        /// What it used to pass on, before a share had to say what it was a share OF.
        /// </summary>
        /// <remarks>
        /// Kept so a file written before scopes existed can be read, and read correctly: a bare
        /// percentage meant the monsters unearthed downstream, because that is the accumulator it
        /// multiplied, so it migrates to "monster=n" exactly. Nothing writes it any more.
        /// </remarks>
        public float Carries;

        /// <summary>
        /// What the classifier placed this as, recorded when it was filed.
        ///
        /// **Known at filing even for an object nothing else is known about**, and worth keeping for
        /// two things a key cannot do: naming the row for what it is rather than for the art file
        /// that identifies it, and deriving its tags when there is no longer one standing in the
        /// dig site to derive them from.
        /// </summary>
        public TargetKind Kind = TargetKind.Unknown;

        /// <summary>
        /// What this thing IS, when the plugin has guessed wrong or cannot guess.
        ///
        /// **Tags are derived, and for a discovered object there is nothing to derive from.** An
        /// object the classifier could not place has no kind and no tier, so Tags.Of gives it
        /// "unknown" and nothing a scope can aim at - which means a modifier reaching excavated
        /// chests cannot be made to reach the chest the plugin failed to recognise. Whoever opened
        /// one and saw what came out is the only one who can say, so this is where they say it.
        ///
        /// Empty means derive, which is what everything the plugin recognises does. Comma separated,
        /// checked against the vocabulary the same way a scope is.
        /// </summary>
        public string Marks = "";

        /// <summary>
        /// What the game calls the object this effect was seen on, or empty.
        ///
        /// **An effect row has no object in it and needs one to read properly.** It is keyed under a
        /// modifier id alone, so the most it can say for itself is "Item Quantity Chest" - and the
        /// same family of modifiers comes off different things, the Vaal Relic granting that one
        /// while Devourer Tail comes off a Dormant Burrower. The table can work this out from
        /// whatever is standing in the dig site, and does, but then the name is right where you are
        /// standing and wrong everywhere else.
        ///
        /// So it is written down the moment the effect is filed, when the object that granted it is
        /// in hand. Only set on an effect row; an object names itself from its own label.
        ///
        /// First granter wins and later ones are not overwritten, because the alternative is a name
        /// that changes depending on which site you last walked through - and where two things grant
        /// the same modifier, either answer is as true as the other.
        /// </summary>
        public string Granter = "";

        /// <summary>
        /// How far from it a blast still catches it, in world units. See Unknowns.Size.
        ///
        /// Its own row rather than one number for every unknown, because the thing this exists to
        /// answer is a single object being the wrong size. A Grand Expedition can introduce half a
        /// dozen objects at once and one of them be a nest eight times a signpost; a global figure
        /// either leaves that one wrong or makes the other five wrong to fix it.
        /// </summary>
        public float Extent = Size;

        /// <summary>
        /// Whether taking a second one is worth anything.
        ///
        /// **Some effects are a switch, not a quantity.** "Runic monsters are duplicated" is
        /// enormous - it doubles everything the rest of the chain unearths - and taking two of them
        /// does not double it again. An objective that adds them up will bend a whole chain towards
        /// collecting four of something that pays once, and score it as four times the truth.
        ///
        /// On by default, because most things do add up and a wrong "does not stack" quietly
        /// undervalues a real effect.
        /// </summary>
        public bool Stacks = true;

        /// <summary>
        /// Whether a person has actually set these, as against them being the shipped guess.
        ///
        /// It is what keeps the red line pointing at the thing: an object still sitting at its
        /// nominal weight has not been looked at, and should go on saying so however many times you
        /// walk past it. Touching either slider is the act of having looked.
        /// </summary>
        public bool Set;

        /// <summary>The last time this was seen in a map, so the file can be read as a history.</summary>
        public DateTime Seen = DateTime.UtcNow;

        /// <summary>
        /// When it was first discovered, which is what orders the list.
        ///
        /// Newest at the top, because a row that has just appeared is the one asking a question -
        /// you walked past something the plugin cannot name and want to price it. Sorted by name
        /// instead, a new find lands halfway down a list of things already dealt with and has to be
        /// hunted for.
        /// </summary>
        public DateTime First = DateTime.UtcNow;
    }

    /// <summary>Everything ever found, for the settings tab to draw. Ordered so it does not jump.</summary>
    public static IEnumerable<KeyValuePair<string, Priced>> All
    {
        get
        {
            // **Read off the reference table, which is the only store now.** Every discovered object
            // is a found: row in your layer; this walks them rather than a dictionary of its own.
            var keys = Wrt.Standing.Select(x => x.Key)
                .Concat(Wrt.Yours.Select(x => x.Key))
                .Where(x => x.StartsWith(Prefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Select(x => x[Prefix.Length..])
                .ToList();

            // Newest first, and by key within the same moment so the order is stable rather than
            // whatever the dictionary felt like. See Wrt.Row.First.
            keys.Sort((a, b) =>
            {
                var when = (Wrt.Of(Wrt.Id.Found(b))?.First ?? DateTime.MinValue)
                    .CompareTo(Wrt.Of(Wrt.Id.Found(a))?.First ?? DateTime.MinValue);

                return when != 0 ? when : StringComparer.Ordinal.Compare(a, b);
            });

            foreach (var key in keys)
            {
                var seen = Viewed(key);

                if (seen != null)
                    yield return new KeyValuePair<string, Priced>(key, seen);
            }
        }
    }

    public static int Count => Wrt.Standing.Select(x => x.Key)
        .Concat(Wrt.Yours.Select(x => x.Key))
        .Where(x => x.StartsWith(Prefix, StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .Count();

    /// <summary>The id prefix a discovered object's row wears. One definition. See Wrt.Id.Found.</summary>
    private static readonly string Prefix = Wrt.Id.Found("");

    /// <summary>How many are still sitting at the shipped guess, for the tab's heading.</summary>
    public static int Undecided
    {
        get
        {
            // The rows whose Status reads "unset": discovered, and nobody has priced them yet.
            return Wrt.Standing.Select(x => x.Key)
                .Concat(Wrt.Yours.Select(x => x.Key))
                .Where(x => x.StartsWith(Prefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Count(x => Wrt.Of(x) is { Set: false });
        }
    }

    /// <summary>
    /// Writes down that this object exists, if it is not already known.
    ///
    /// Called from the scan for anything carrying the state and matching no rule, so the list fills
    /// itself as you play rather than needing anybody to go looking.
    /// </summary>
    public static void Found(string metadata) => Found(metadata, TargetKind.Unknown);

    /// <summary>
    /// The same, starting from what the plugin already knows about this kind of thing.
    ///
    /// **A nominal one is the safe answer and often a wasteful one.** A marker the classifier placed
    /// as a sub-area entrance is one whether or not this tileset's art has been seen before, and
    /// asking somebody to price each new one from scratch when the last four were alike is work the
    /// plugin could have saved them. See Priors.
    ///
    /// **A prior seeds the weight and no longer decides it.** The sentence here used to say the
    /// opposite, and gave the reason: a red label on every entrance would be asking a question the
    /// plugin had already answered. True while one generic weight stood for every sub-area entrance;
    /// false now that each cap art has its own row, which exists precisely so a lair and a vein can
    /// differ. A red label on a cap nobody has priced is the question, not noise.
    /// </summary>
    public static void Found(string metadata, TargetKind kind, int footprint = 0)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return;

        // **Filed as an ordinary table row, because there is only one table now.**
        //
        // This wrote to unknown.json, a second store with its own schema and its own copy of Weight,
        // Carries, Scope, Stacks, Set and the rest. A discovered object's weight therefore lived
        // somewhere the reference table could not show or edit - twenty of thirty-eight entries in a
        // live file carried a weight the table did not have, including a superunique boss priced at
        // sixty by hand. "The only place anything ever pulls weights from" had a second place.
        //
        // A row now, in your layer, with Set saying whether anybody has decided it. The prior counts
        // as decided exactly as it did before: a kind the plugin already answers for is not asking a
        // question. See Wrt.Row.Set and Priors.Weight.
        var id = Wrt.Id.Found(metadata);
        var already = Wrt.Of(id);
        var now = DateTime.UtcNow;

        // **A row that exists is one that has been filed, whatever its cells say.**
        //
        // This asked whether the row had a WEIGHT, so a row whose weight had been cleared by hand
        // read as never filed and was seeded again on the next sweep - the prior back in the cell
        // within a second of deleting it, which is the third of three ways a cleared number used to
        // undo itself. The seed is for an object nobody has met; the row's existence is what says
        // somebody has.
        if (already != null)
        {
            Wrt.Set(id, r =>
            {
                r.Seen = now;

                // Filed before the kind was recorded, or filed off an effect that has no kind.
                if (already.Kind == null && kind != TargetKind.Unknown)
                    r.Kind = kind.ToString();
            });

            return;
        }

        var prior = Priors.Weight(kind);

        Wrt.Set(id, r =>
        {
            r.Kind = kind == TargetKind.Unknown ? null : kind.ToString();
            // **A prior is a starting number, not a decision.** It used to count as decided, on the
            // reasoning that a kind the plugin already answers for is not asking a question - which
            // was true while one generic weight stood for every sub-area entrance in the game. It is
            // not true now that each art gets its own row: the whole point of the split is that a cap
            // nobody has met should ASK, and a prior marking it settled is precisely the silent
            // catch-all the split removes.
            //
            // So the weight still starts at the prior - nothing collapses to one on first sighting -
            // and Set stays false until somebody says otherwise. Seeded and undecided, which is the
            // state the Status column exists to show. See Priors.Weight and Weighing.Entrance.
            r.Weight = prior ?? Default;
            r.Set = false;
            r.First = now;
            r.Seen = now;

            // **The size too, where the game has stated a footprint we have measured.**
            //
            // A discovered row asks for a weight because nobody can know what a thing is worth
            // without deciding. Its SIZE is not like that - the game states a footprint and two of
            // them have been measured against it - so filling it in is handing over a fact rather
            // than guessing on somebody's behalf. Left blank for a footprint nobody has measured,
            // which leaves the object at the ordinary size exactly as before.
            //
            // Written on the row the table shows for this object, which is the row Extents reads.
            // Those were two different rows until recently and a size typed into the column went
            // somewhere nothing looked. See Weighing.RowIdOfTarget.
            if (Extents.FromFootprint(footprint) is { } sized)
                r.Size = sized;
        });
    }

    /// <summary>
    /// How far from this object a blast still catches it, in world units.
    ///
    /// Anything with no row here is something the plugin recognises, and recognised objects are
    /// either in the measured table in Extents or are an ordinary marker. So the fallback is the
    /// ordinary size, which is what every measured art came out at.
    /// </summary>
    /// <summary>
    /// The size somebody has typed on this object's row, or null where nobody has.
    ///
    /// **Separate from Extent because "unset" and "the default" had to stop being the same answer.**
    /// Extent falls through to the ordinary marker size, so a caller cannot tell a row that says
    /// nothing from one that agrees with the default - which is why the measured art table had to be
    /// consulted ahead of the row, and therefore why the row's Size column did nothing at all for
    /// every art the table names. See Extents.Of.
    /// </summary>
    public static float? Sized(string key)
    {
        var said = Wrt.Of(Wrt.Id.Found(key))?.Size;

        return said is > 0f ? said : null;
    }

    public static float Extent(string key) =>
        Wrt.Of(Wrt.Id.Found(key))?.Size
        ?? Size;

    /// <summary>
    /// Files an object again under the name that has just arrived, and drops the nameless row.
    ///
    /// **A ground label turns up when you walk near it, and filing waits on a clock.** Register
    /// holds off until Settled - two seconds after the target was born - on the reasoning that
    /// everything naming it has arrived by then. That is true of metadata, art, icon and states,
    /// which come with the entity. It is not true of the label: the game draws it when the player
    /// is close enough, so a relic first seen across the dig site is filed nameless two seconds
    /// later, and Filed then locks that key so the real name can never correct it. Seen in a live
    /// file: one Vaal Relic under four keys, one of them with no name at all.
    ///
    /// **The target is the handle, not the entity.** An entity id would do for this and is wrong
    /// for the case that matters - entities unload as you walk a Grand site, which is exactly why
    /// the scan keys what it remembers on the grid cell. The target outlives its entity, and Filed
    /// hangs off the target, so the thing that was filed is the thing that re-files itself.
    ///
    /// The old row goes only when nobody has touched it and nothing else is still filed under it.
    /// A row somebody has priced is a decision, and a decision is not swept up by a bookkeeping
    /// correction - it stays, and the object simply stops pointing at it.
    /// </summary>
    public static void Refile(Target target, string was, IEnumerable<Target> others)
    {
        if (target == null || string.IsNullOrEmpty(was))
            return;

        // Key returns Filed when it is set - that is what makes it stable - so it has to be let go
        // of before the question can be asked again.
        target.Filed = "";

        var now = Key(target);

        if (now.Length == 0 || string.Equals(now, was, StringComparison.Ordinal))
        {
            target.Filed = was;

            return;
        }

        target.Filed = now;
        Found(now);

        // What it grants may only have become readable with the label, so those get another look -
        // and this is the moment a name usually becomes available to hand them, since a refile is
        // precisely a label having arrived. See Granted.
        foreach (var effect in Effects(target))
        {
            Found(effect);
            Granted(effect, target);
        }

        if (Wrt.Of(Wrt.Id.Found(was)) is not { Set: false })
            return;

        foreach (var other in others ?? Array.Empty<Target>())
        {
            if (!ReferenceEquals(other, target) && string.Equals(other?.Filed, was, StringComparison.Ordinal))
                return;
        }

        // **The reference table's row too, not just an entry beside it.** This used to clear a
        // dictionary and leave the row in the file, which is why one object could end up with two
        // rows for good - see Wrt.Labelled, which had to clean up after it.
        Wrt.Forget(Wrt.Id.Found(was));
    }

    /// <summary>The row an object is filed under, or nothing when the plugin recognises it.</summary>
    public static Priced Row(string key) => Viewed(key);

    /// <summary>
    /// A table row seen through the old Priced shape, for the readers that still ask for one.
    ///
    /// **Built rather than stored.** The discovery store used to hold these objects and write them to
    /// unknown.json; the reference table holds the same facts now, in its own row, and this is the
    /// adapter that keeps the handful of callers wanting a Priced working while they are moved over.
    /// It owns nothing - change the row and the next call sees it.
    /// </summary>
    private static Priced Viewed(string key)
    {
        var row = key == null ? null : Wrt.Of(Wrt.Id.Found(key));

        if (row == null)
            return null;

        return new Priced
        {
            Weight = row.Weight ?? Default,
            Marks = row.Tags ?? "",
            Granter = row.Granter ?? "",
            Extent = row.Size ?? Size,
            Stacks = row.Stacks ?? true,
            Set = row.Set ?? true,
            Kind = Enum.TryParse<TargetKind>(row.Kind, out var kind) ? kind : TargetKind.Unknown,
            Seen = row.Seen ?? DateTime.UtcNow,
            First = row.First ?? DateTime.UtcNow,
        };
    }

    /// <summary>What the classifier placed an object as when it was filed. See Priced.Kind.</summary>
    public static TargetKind KindOf(string key) =>
        Enum.TryParse<TargetKind>(Wrt.Of(Wrt.Id.Found(key))?.Kind, out var kind)
            ? kind
            : TargetKind.Unknown;

    /// <summary>What somebody has said this object is, or empty to derive it. See Priced.Marks.</summary>
    public static string MarksOf(string key) =>
        Wrt.Of(Wrt.Id.Found(key))?.Tags
        ?? "";

    /// <summary>
    /// What this object is worth. The default for anything unheard of.
    ///
    /// It used to hand back what the object passes on as well, from the retired Carries cell. What
    /// a row propagates is its effect now, and effects are read through TableGrammar.EffectsOfRow
    /// by everything that needs them.
    /// </summary>
    /// <summary>
    /// What this object is worth, or nothing where its row states no weight.
    ///
    /// **Nought rather than Default, because the default is delivered by the seed now.** Register
    /// writes the prior for the object's kind into the row the moment it is filed, so a row with no
    /// weight is no longer "an object nobody has met" - it is one whose weight somebody deleted.
    /// Answering one for that put a number back that had just been taken out.
    ///
    /// The reason the default is one and not nought still holds and is unchanged: an unknown object
    /// worth nothing is one the planner will not draw, will not route past and will never prompt
    /// anybody to look at. That is the seed's job. See Register and Priors.Weight.
    /// </summary>
    public static float Of(string metadata) =>
        Wrt.Of(Wrt.Id.Found(metadata))?.Weight ?? 0f;

    /// <summary>Whether a second one of these is worth anything. See Priced.Stacks.</summary>
    public static bool Stacking(string key) =>
        Wrt.Of(Wrt.Id.Found(key))?.Stacks
        ?? true;

    /// <summary>
    /// Whether this is something the plugin has filed as unread and nobody has priced.
    ///
    /// **Both halves matter.** Absent from the table means the plugin recognises it - a goblin relic
    /// is not unread, it is known and priced in code - so a plain "has anybody set this" test would
    /// have put a red label under every recognised thing in the game. Present and unset is the only
    /// state that is asking a question.
    /// </summary>
    public static bool Unread(string key) =>
        key != null && Wrt.Of(Wrt.Id.Found(key)) is { Set: false };

    /// <summary>
    /// Whether anything about this object is still waiting to be priced - itself or what it grants.
    ///
    /// **An object is not dealt with until all of it is.** A relic is its own row plus a row for
    /// each upside it carries, and asking only about the object meant setting one weight took the
    /// red line away while two of the three things it grants were still at their default. The mark
    /// on the ground says "there is work here", so it has to survive until there is none.
    /// </summary>
    public static bool Unread(Target target)
    {
        if (target == null)
            return false;

        // **Worked out once per table revision, because the overlay asks it of every marker every
        // frame.** Everything below builds the key this object is filed under, looks it up, and
        // does the same for each thing it grants - a string built from eight fields, hashed twice,
        // per marker per frame.
        //
        // **Two halves to the key, because the revision only covers half the question.** What the
        // TABLE says cannot change without the revision moving: an edit to a row moves it, and so
        // does filing an object for the first time, since filing writes a row. What the OBJECT says
        // can: the key is built from its metadata, mods, art, icon, states, words, render and
        // blessing, and those arrive over several frames as the entity streams in. An object read
        // early has a different key from the same object read a second later, and nothing about
        // that touches the revision.
        //
        // So the answer is aged out as well. Ten seconds is far longer than streaming takes and far
        // shorter than a session, which makes it a backstop rather than a cache policy - and it
        // costs one clock read per marker where the alternative is a string build. An object that
        // has settled needs none of this, since Key returns its filed id from then on and cannot
        // move; the age is kept for all of them anyway rather than adding a second rule about
        // which ones it applies to.
        var now = Environment.TickCount64;

        if (target.UnreadUnder == Wrt.Revision && now - target.UnreadAt < Ageing)
            return target.UnreadAnswer;

        target.UnreadUnder = Wrt.Revision;
        target.UnreadAt = now;

        return target.UnreadAnswer = Asking(target);
    }

    /// <summary>How long an unread verdict may stand before it is worked out again, in ms.</summary>
    private const long Ageing = 10_000L;

    /// <summary>The question itself, behind the memo above. See Unread.</summary>
    private static bool Asking(Target target)
    {
        if (Unread(Key(target)))
            return true;

        foreach (var effect in Effects(target))
        {
            if (Unread(effect))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Records whether somebody has agreed this row's weight, which is what stops it asking.
    ///
    /// **This is the only thing that clears a red line, and for a long time nothing called it.** A
    /// discovered object is seeded with the prior for its kind and Set false - a starting number
    /// nobody has agreed - and Unread turns that into a mark on the ground. With no caller the mark
    /// could not be taken away by any gesture in the plugin: the rows that read as answered today
    /// were answered by the one-time unknown.json import, which no longer runs.
    ///
    /// Takes the row id, which is what every caller holds, rather than the bare metadata - the
    /// prefix belongs to Wrt.Id and this file need not know it twice. Unread is the reader.
    /// </summary>
    public static void AnsweredByHand(string id, bool answered)
    {
        if (!string.IsNullOrEmpty(id))
            Wrt.Set(id, r => r.Set = answered);
    }

    /// <summary>Forgets one entry entirely, so a thing filed by mistake can be dropped.</summary>
    /// <summary>
    /// Empties the list. It fills itself again from whatever the next dig site sets off.
    ///
    /// The same button the must-avoid mods panel has, for the same reason: a list keyed on seven
    /// fields accumulates near-duplicates when the game changes an object slightly, and the cheapest
    /// answer to a list you no longer trust is to throw it away and walk a site.
    /// </summary>
    public static void ForgetAll()
    {
        foreach (var key in new List<string>(Wrt.Yours.Select(x => x.Key)
                     .Concat(Wrt.Standing.Select(x => x.Key))
                     .Where(x => x.StartsWith(Prefix, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal)))
            Wrt.Forget(key);
    }

    /// <summary>Marks the list as changed, for an edit that is not somebody pricing something.</summary>
    /// <summary>Does nothing. The reference table tracks its own changes. See Keep.</summary>
    public static void Touched()
    {
    }

    public static void Forget(string metadata)
    {
        if (metadata != null)
            Wrt.Forget(Wrt.Id.Found(metadata));
    }

    /// <summary>
    /// Writes the file when something has changed, on the same unhurried rhythm the other stores
    /// use: a reload gives no warning, and waiting for an area change would lose exactly the
    /// sessions that needed it.
    /// </summary>
    /// <summary>
    /// Writes nothing. Discovered objects are rows in the reference table and Wrt writes those.
    ///
    /// Kept as a no-op rather than deleted because it is called on the plugin's own rhythm from
    /// several places, and a store that has moved should stop writing quietly rather than make every
    /// caller learn that it has. See Load, which imports the old file once and retires it.
    /// </summary>
    /// <summary>What the one-time import of unknown.json did, row by row. See Load and Dump.</summary>
    public static readonly List<string> Imported = new();

    public static void Keep()
    {
    }


    /// <summary>
    /// Reads the file back.
    ///
    /// Parsed by hand rather than through a serialiser, because the shape is four fields and the
    /// plugin has no JSON dependency of its own - and a file somebody has edited by hand should
    /// survive a stray comma rather than take the plugin down with it.
    /// </summary>
    public static void Load()
    {
        if (Home.Length == 0)
            return;

        var path = Path.Combine(Folder(), "unknown.json");

        if (!File.Exists(path))
            return;

        try
        {
            var lines = File.ReadAllLines(path);
            var keyed = 0;
            var imported = 0;

            foreach (var line in lines)
            {
                var said = Between(line, "\"keyed\": ", ",");

                if (said.Trim().Length > 0 && int.TryParse(said.Trim(), out var wrote))
                {
                    keyed = wrote;

                    break;
                }
            }

            // Written under a key that no longer means anything. See Keyed.
            //
            // **Except where it can be translated, which is the whole of this bump.** Version two
            // took the blessing out of the key, so every version one row is the same row with one
            // trailing field to clear - and throwing away somebody's priced list to make a
            // mechanical edit would be a poor trade. Anything older than that predates the fields
            // and is still discarded.
            if (keyed != Keyed && keyed != 1)
                return;

            var migrate = keyed == 1;

            foreach (var line in lines)
            {
                var meta = Between(line, "\"metadata\": \"", "\"");

                if (string.IsNullOrWhiteSpace(meta))
                    continue;

                if (migrate)
                    meta = Unblessed(meta);

                var row = new Priced
                {
                    Weight = Number(Between(line, "\"weight\": ", ","), Default),
                    Carries = Number(Between(line, "\"carries\": ", ","), 0f),

                    // Absent in a file written before this existed, and absent means the ordinary
                    // size - which is what everything in such a file was planned against.
                    Extent = Number(Between(line, "\"extent\": ", ","), Size),

                    Scope = Between(line, "\"scope\": \"", "\""),
                    Marks = Between(line, "\"marks\": \"", "\""),

                    // Absent in a file written before this existed, and absent means the table works
                    // it out from the dig site as it did then. See Priced.Granter.
                    Granter = Between(line, "\"granter\": \"", "\""),
                    Kind = Enum.TryParse<TargetKind>(Between(line, "\"kind\": \"", "\""), out var was)
                        ? was
                        : TargetKind.Unknown,
                    Set = Between(line, "\"set\": ", ",").Trim() == "true",

                    // Absent in a file written before this existed, and absent means stacking -
                    // which is what everything did then.
                    Stacks = Between(line, "\"stacks\": ", ",").Trim() != "false",
                    Seen = DateTime.TryParse(Between(line, "\"seen\": \"", "\""),
                        CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when)
                        ? when
                        : DateTime.UtcNow,

                    // A file written before this existed has no first, and everything in it is
                    // equally old - which is exactly what falling back to seen says.
                    First = DateTime.TryParse(Between(line, "\"first\": \"", "\""),
                        CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var made)
                        ? made
                        : when,
                };

                // **A bare carry from before scopes meant the monsters unearthed downstream**,
                // because that is the accumulator it multiplied. So it translates exactly, and
                // nothing anybody tuned stops working the day scopes arrive. Blanking it instead
                // would have been the honest-looking choice and the wrong one: a carry that silently
                // becomes nought is a plan that changes for a reason nobody can see.
                if (row.Scope.Length == 0 && row.Carries > 0f)
                {
                    row.Scope = "monster=" +
                        row.Carries.ToString("0.###", CultureInfo.InvariantCulture);
                }

                // **Imported into the reference table, which is where it lives now.** This used to
                // be the second store's own dictionary; every entry becomes an ordinary found: row in
                // your layer, keeping Set so "discovered and nobody has priced it" survives the move.
                // A row you have already edited wins - the import fills gaps, it does not overwrite.
                var into = Wrt.Id.Found(meta);

                // **Your layer, not the merged row.** Wrt.Of folds the shipped file in, so asking it
                // "has this a weight already?" answers yes for every row the plugin ships a default
                // for - and the import then skips the number somebody tuned by hand in favour of a
                // default they were overriding. A shipped value is not an edit. Only a row in your
                // own file counts as one, which is what this reads.
                var standing = Wrt.Yours.FirstOrDefault(x =>
                    string.Equals(x.Key, into, StringComparison.Ordinal)).Value;

                // **A row that exists is one this import has already served, and it is left alone.**
                //
                // Each cell used to be gap-filled on its own - "fill it if it is empty" - which reads
                // an empty cell as one nobody has answered. A cleared cell IS empty, so clearing one
                // and reloading put the old value straight back, out of a legacy file the table no
                // longer shows. Reported as being unable to delete a size: the store took the null,
                // and this handed it back on the next load.
                //
                // Row-level is the right test and is what the paragraph above already claims - "a row
                // you have already edited wins". At this point in startup nothing has been sighted
                // yet, so Mine holds only what the reference table's own file carried; a row here means
                // this import has already run for that object, cleared cells and all.
                //
                // The observations are still gap-filled, because they are not answers - see Wrt.Set,
                // which leaves Seen and First out of its "did an answer move" test for the same reason.
                Wrt.Set(into, r =>
                {
                    if (standing == null)
                    {
                        r.Weight = row.Weight;

                        if (row.Marks.Length > 0)
                            r.Tags = row.Marks;

                        if (row.Extent > 0f)
                            r.Size = row.Extent;

                        if (!row.Stacks)
                            r.Stacks = false;

                        if (row.Granter.Length > 0)
                            r.Granter = row.Granter;

                        if (row.Kind != TargetKind.Unknown)
                            r.Kind = row.Kind.ToString();
                    }

                    r.Set ??= row.Set;
                    r.First ??= row.First;
                    r.Seen ??= row.Seen;
                });

                imported++;

                // **Says what it did, per row, because three rows came through without their weight
                // and four attempts to reason out why were all wrong.** An import runs once, at load,
                // where nothing can be watched - so it writes down its own decisions and the dump
                // prints them. See Dump and Imported.
                var after = Wrt.Yours.FirstOrDefault(x =>
                    string.Equals(x.Key, into, StringComparison.Ordinal)).Value;

                Imported.Add(
                    $"{(row.Weight == (after?.Weight ?? float.MinValue) ? "took" : "LOST")} " +
                    $"weight {row.Weight:0.##} (yours said " +
                    $"{(standing?.Weight?.ToString("0.##") ?? "nothing")}, row now says " +
                    $"{(after?.Weight?.ToString("0.##") ?? "nothing")})  {meta}");
            }

            // The rows that were four are now one, so the file should say so rather than keep
            // writing the old spelling until something else happens to change.
            if (migrate)

            // **The file is retired, not deleted.** Everything in it is a row in the reference table
            // now, so reading it again would re-import rows somebody may since have edited or
            // forgotten. Renamed rather than removed because an import is a one-way door and this is
            // the only copy of numbers somebody tuned by hand - twenty weights in a live file, a
            // superunique boss among them. If the import got something wrong, it is still here.
            Wrt.Keep();

            var done = path + ".imported";

            if (File.Exists(done))
                File.Delete(done);

            File.Move(path, done);

            DebugWindow.LogMsg(
                $"[AutoExpedition] moved {imported} discovered entities out of unknown.json and into " +
                "the weight reference table, where they are ordinary rows with a Status of unset " +
                $"until you price them. The old file is kept as {Path.GetFileName(done)}.", 15f);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not read the unknown entities: {ex.Message}", 5f);
        }
    }

    /// <summary>
    /// What to file a target under: everything about it that names the KIND rather than the one.
    ///
    /// **One field was not enough, and which one was not enough differed by family.** A relic's
    /// metadata names the scenery it stands in while its mod names what it does, so two relics can
    /// share a path and grant different things. An encased monster is the opposite: eighty of them
    /// share one path, one art and one icon, and the only thing that separates a white zealot from a
    /// rare is the entity standing inside it. Keying on the path alone priced all eighty the same;
    /// keying on the mod alone would have left everything that has no mods sharing a row.
    ///
    /// So the key is the combination, and each part earns its place:
    ///
    /// - metadata, the model;
    /// - art, which separates variants the path does not;
    /// - minimap icon, the game's own classification, and what separates a bright chest from a dull
    ///   one wearing identical art;
    /// - the mod that names an effect, which is what the recognised relics are matched on;
    /// - the state names it carries, sorted - be_free is an encased monster, sockets is a remnant;
    /// - the words on its ground label, which is the game saying in English what the thing is;
    /// - the render name, which is occasionally a real description.
    ///
    /// Rarity is deliberately NOT here. It varies from one instance to the next rather than
    /// describing a kind, so including it would file a Magic strongbox and a Rare one as two
    /// different objects to be priced separately. The same goes for the mods an object carries and
    /// for the enchanted lines on its label - both say what this ONE rolled, both are priced on
    /// rows of their own, and both were splitting a relic into a row per roll. See Key.
    /// </summary>
    public static string Key(Target target)
    {
        if (target == null)
            return "";

        // Once filed, always that key. See Target.Filed.
        if (target.Filed.Length > 0)
            return target.Filed;

        return Key(target.Meta, target.Mods, target.Art, target.Icon,
            target.StateNames(), target.Words, target.Rendered, target.Blessing);
    }

    /// <summary>
    /// Files a target under a key worked out once, when everything that names it has arrived.
    ///
    /// Returns the key either way, so a caller that only wants the weight is answered immediately
    /// while the row itself waits for a name worth filing under. See Target.Born.
    /// </summary>
    public static string Register(Target target)
    {
        var key = Key(target);

        if (target == null || key.Length == 0 || target.Filed.Length > 0)
            return key;

        if (!target.Settled || target.StateNames().Length == 0)
            return key;

        target.Filed = key;
        Found(key, target.Kind,
            Safe.Read(() => target.Entity?.GetComponent<Positioned>()?.Size ?? 0, 0));

        // The things it grants, each on its own row. A relic is its own row plus these - and each of
        // them is told what granted it, which is knowable here and nowhere else. See Priced.Granter.
        foreach (var effect in Effects(target))
        {
            Found(effect);
            Granted(effect, target);
        }

        return key;
    }

    /// <summary>
    /// The part of a key that says WHICH OBJECT, ignoring everything a later reading might add.
    ///
    /// Metadata and art. Words, blessings and the mod list are things the same object can be filed
    /// with or without depending on how far away it was when it was first seen - which is exactly
    /// what makes them useless for recognising it again, and exactly why this exists: to match a row
    /// on file against a live object standing in the dig site. See Name's called.
    /// </summary>
    public static string Shape(string key)
    {
        var parts = Parts(key);

        return parts.Length < 3 ? "" : parts[0] + "|" + parts[2];
    }

    /// <summary>
    /// Records what granted an effect, if the granter has a name and the row has none.
    ///
    /// **Both halves of that guard matter.** An object filed before its label loaded has no name to
    /// give, and writing an empty one would mark the question answered; a row that already names its
    /// granter keeps it, because the first answer is as good as any later one and a name that moves
    /// about between sites is worse than either.
    /// </summary>
    private static void Granted(string effect, Target target)
    {
        var words = Safe.Read(() => target.Words ?? "", "");

        if (words.Length == 0 || Viewed(effect) is not { } priced ||
            priced.Granter.Length > 0)
            return;

        priced.Granter = words;
    }

    public static string Key(string metadata, string mods, string art, string icon,
        string states, string words, string render, string blessing = "")
    {
        // **The mods are not part of the object's key any more; each one is priced on its own.**
        //
        // Keying on the combination was right about one thing and wrong about the consequence: what
        // a relic grants is what it is worth, but a relic can grant three things at once and the
        // mixes multiply. Five upsides make thirty one possible relics, each needing a weight set by
        // hand, and pricing one teaches you nothing about the next.
        //
        // What actually has a value is the upside. So the object gets a row for what it is, each
        // upside gets a row for what it does, and a relic is worth its own row plus the rows of the
        // things it grants. Price "runic monsters duplicated" once and every relic carrying it is
        // priced. See Effects.
        var upside = "";

        // **And nor is what the label says it grants, for exactly the same reason.**
        //
        // The blessing is the enchanted line off the ground label - "20% increased Pack size" -
        // which is the mod above said in English. Dropping the mods and keeping this dropped
        // nothing: one Vaal Relic became four rows, one per roll, in the same commit that removed
        // the combinatorial split. Seen in a live file: the same tundravaalremnant art, the same
        // icon, the same ten states, filed four times over because the labels read Pack size,
        // Runic Monsters are Duplicated, increased number of Magic Monsters, and nothing yet.
        //
        // It is the same mistake the note below makes about rarity: it varies from one instance to
        // the next rather than describing a kind. What it grants is priced on the upside's own row,
        // where pricing it once prices every relic carrying it.
        //
        // Kept as a parameter rather than removed, because the caller reads it for the label and
        // the dump, and a signature that no longer asks for it would take that away too.
        var granted = "";

        // **And nor is the ground label, which is the third field dropped for one reason.**
        //
        // A label streams in later than the metadata, art, icon and states do - later even than
        // Settled waits - so an object gets filed once before it is readable and again after, and
        // the two keys differ in this field alone. Refile exists to reconcile that and cannot
        // finish the job: it keeps the older row whenever somebody has priced it, which is exactly
        // the row worth keeping, and it clears the entry from this table without touching the
        // reference table's row. So the duplicate survives in the file for good.
        //
        // Measured before removing it: across every object row shipped, this field is non-empty
        // exactly ONCE - and that once is one half of a duplicated Vaal relic. It distinguishes
        // nothing and has duplicated two objects. The same judgement as the mods and the blessing
        // above: what varies from one sighting to the next does not describe a kind.
        //
        // Still read, still shown, still the row's NAME - see Catalogue and Priors.Called. It is
        // only its identity it has stopped being.
        var said = "";

        return string.Join("|", metadata ?? "", upside, art ?? "", icon ?? "", states ?? "",
            said, render ?? "", granted);
    }

    /// <summary>
    /// A label with its numbers taken out, so a count does not make a new kind of thing.
    ///
    /// "guarded by 9 packs of monsters" and "guarded by 8 packs of monsters" are one object and one
    /// row. Every run of digits becomes a hash, whitespace collapses, and what is left is the
    /// sentence the game uses for that kind of thing.
    /// </summary>
    public static string Plain(string words)
    {
        if (string.IsNullOrWhiteSpace(words))
            return "";

        var text = new StringBuilder(words.Length);
        var digit = false;
        var space = false;

        foreach (var c in words)
        {
            if (char.IsDigit(c))
            {
                if (!digit)
                    text.Append('#');

                digit = true;
                space = false;

                continue;
            }

            digit = false;

            if (char.IsWhiteSpace(c))
            {
                if (!space && text.Length > 0)
                    text.Append(' ');

                space = true;

                continue;
            }

            space = false;
            text.Append(c);
        }

        return text.ToString().Trim();
    }

    /// <summary>The parts of a key, for the (i) beside each row. See Key.</summary>
    public static string[] Parts(string key) => (key ?? "").Split('|');

    /// <summary>
    /// What to call a row in the settings, shortest first.
    ///
    /// The key is seven fields joined together and unreadable on purpose - it exists to be unique,
    /// not to be read. The label is the best name the object has: what the game calls it, failing
    /// that its art, failing that the tail of its path.
    /// </summary>
    /// <param name="called">
    /// What the game calls it, when the caller has a live one to ask and the key does not say.
    ///
    /// **A key is spelt once, from whatever had arrived at the time.** An object first seen from
    /// further away than the game draws its label is filed with an empty words field and keeps it -
    /// Scan.Refile exists to correct that and only fires while a live target and its label are both
    /// in hand, which is not the case for a row read back from the file weeks later. So the row goes
    /// on reading "devourerbodysegment" while the thing itself is labelled "Dormant Burrower" two
    /// paces away.
    ///
    /// Passed in rather than re-keyed. Re-keying is how a row changes identity, and the identity is
    /// what every edit, weight and tag is filed against - the key has been respelled three times in
    /// this plugin's life and each one left rows stranded beside their replacements. A display name
    /// can be improved without any of that being at stake.
    /// </param>
    public static string Name(string key, string called = "")
    {
        var parts = Parts(key);

        // An effect is filed under its mod id alone, with none of an object's fields, so there is
        // nothing to say about it but what it is - and what grants it, where the caller knows.
        //
        // **Which object that is cannot be read off the key and is not a constant.** An effect row
        // is a modifier, and the same family of modifiers is handed out by different things: the
        // Vaal Relic grants Item Quantity Chest and Pack Size, while Devourer Tail comes off a
        // Dormant Burrower. Both are ExpeditionRelicUpside ids, so the id says "relic" and nothing
        // more - which is why this reads "Relic: Item Quantity Chest" with no caller to ask.
        if (parts.Length == 1)
            return called.Length > 0
                ? $"{called}: {Effect(parts[0])}"
                : Effect(parts[0]);

        string At(int i) => i < parts.Length ? parts[i] : "";

        // **The effect leads, because the effect is what makes two rows two rows.**
        //
        // Five Vaal relics share a model, an art, an icon and a state list and differ only in what
        // they grant - so a name taken from any of those reads "tundravaalremnant.ao" five times
        // over and the list looks broken rather than detailed. What the game calls the difference
        // goes first, and the thing it belongs to follows it in the way a surname follows a name.
        var effect = Effect(At(7).Length > 0 ? At(7) : At(1));

        // The key's own words first - a name filed with the row is the row's name. What a live one
        // is called comes next, ahead of the art file, because it is the same kind of answer the
        // words field holds and the art file is a last resort. See called.
        var thing = At(5).Length > 0 ? At(5)
            : called.Length > 0 ? called
            : At(6).Length > 0 ? At(6)
            : At(2).Length > 0 ? Model(At(2))
            : Short(At(0));

        // **What it is, then what it does, in that order and with a plain colon.**
        //
        // The game names these on their ground label - "Vaal Relic" - and that is the word a player
        // has in their head when they walk up to one, so it leads. The effect follows it because the
        // effect is what makes two of them two rows. An ordinary colon rather than anything
        // decorative: this is read on a dark background at a glance, and a symbol nobody can name is
        // a symbol nobody can search for either.
        if (effect.Length > 0)
            return thing.Length > 0 ? $"{thing}: {effect}" : effect;

        // **A name the game shares between several objects needs what tells them apart beside it.**
        //
        // Three Vaal zealots - daggers, knifestick and spear - all label as "Blood Zealot", so three
        // correctly separate rows read as one row printed three times and the list looks broken. The
        // model is what actually differs, and it is the only thing that does.
        //
        // Only when it adds something. Where the name came from the model in the first place, saying
        // it twice is worse than saying it once.
        // **Never the live label here, which is the one place it would do harm.**
        //
        // This branch exists to print what DIFFERS between rows the game names alike - three Vaal
        // zealots all label "Blood Zealot" and are told apart only by their model. A live label is
        // the shared half by construction, so using it would set model equal to thing, the test
        // below would collapse the pair, and the three rows would read as one row printed three
        // times. Exactly the fault this branch was written to fix. See called.
        var model = At(2).Length > 0 ? Model(At(2)) : Short(At(0));

        return model.Length > 0 && !string.Equals(model, thing, StringComparison.OrdinalIgnoreCase)
            ? $"{thing}: {model}"
            : thing;
    }

    /// <summary>
    /// Every upside a target grants, as its own key. See Key.
    ///
    /// Sorted, because the mods come back in whatever order memory holds them and a list that
    /// reorders itself between frames is a list nobody can read.
    /// </summary>
    public static List<string> Effects(Target target)
    {
        var found = new List<string>();

        foreach (var piece in (target?.Mods ?? "").Split(',', ';', ' '))
        {
            var token = piece.Trim();

            if (token.Contains("ExpeditionRelicUpside", StringComparison.OrdinalIgnoreCase) &&
                !found.Contains(token))
                found.Add(token);
        }

        found.Sort(StringComparer.Ordinal);

        return found;
    }

    /// <summary>
    /// The same upsides in the order the game lists them, for reading rather than for keying.
    ///
    /// **Sorted is right for a key and wrong for a label.** Effects sorts so that two relics
    /// granting the same three things are one row however memory happened to return them; but on
    /// the ground the line wants to read in the order the relic itself reads, so that what is on
    /// screen and what is on the object line up while looking from one to the other.
    ///
    /// Only the order differs. Anything that decides a weight or a row still goes through Effects.
    /// </summary>
    public static List<string> Listed(Target target)
    {
        var found = new List<string>();

        foreach (var piece in (target?.Mods ?? "").Split(',', ';', ' '))
        {
            var token = piece.Trim();

            if (token.Contains("ExpeditionRelicUpside", StringComparison.OrdinalIgnoreCase) &&
                !found.Contains(token))
                found.Add(token);
        }

        return found;
    }

    /// <summary>
    /// A mod id as something a person can read: the boilerplate off the front, the words apart.
    ///
    /// ExpeditionRelicUpsideElitesDuplicated is the game naming the difference between one relic and
    /// the next, and it is perfectly good information behind a prefix that every one of them shares.
    /// Text the game wrote for a player - an enchantment line - is left exactly as it is.
    /// </summary>
    public static string Effect(string mod)
    {
        if (string.IsNullOrWhiteSpace(mod))
            return "";

        if (mod.Contains(' '))
            return mod;

        // Several at once, each tidied and listed. See Key.
        if (mod.Contains('+'))
        {
            var all = mod.Split('+');

            for (var i = 0; i < all.Length; i++)
                all[i] = Effect(all[i]);

            return string.Join(", ", all);
        }

        foreach (var prefix in new[]
                 {
                     "ExpeditionRelicUpsideSpecial", "ExpeditionRelicUpside",
                     // The drawbacks read the same way the gifts do - "Immune Fire Damage",
                     // not "Downside Immune Fire Damage". Which family it belongs to is said
                     // by where it is listed, not by repeating it in every row. See MustAvoidMods.
                     "ExpeditionRelicDownside", "ExpeditionRelic",
                 })
        {
            if (mod.StartsWith(prefix, StringComparison.Ordinal) && mod.Length > prefix.Length)
            {
                mod = mod[prefix.Length..];

                break;
            }
        }

        var text = new StringBuilder(mod.Length + 8);

        for (var i = 0; i < mod.Length; i++)
        {
            if (i > 0 && char.IsUpper(mod[i]) && !char.IsUpper(mod[i - 1]))
                text.Append(' ');

            text.Append(mod[i]);
        }

        return text.ToString();
    }

    /// <summary>
    /// What to call a whole object on screen: what it is, then everything it grants.
    ///
    /// **The object and its effects are separate rows, and separate rows do not correlate
    /// themselves.** Pricing by upside is right - one weight for "runic monsters duplicated" prices
    /// every relic that carries it - but it leaves the ground saying "tundravaalremnant" while the
    /// settings say "Item Quantity Monster", with nothing to connect the two. So the ground says
    /// both: the object names itself and lists what it grants, and every name in that list is a row
    /// you can go and set.
    /// </summary>
    public static string Describe(Target target)
    {
        var name = Name(Key(target));
        var effects = Listed(target);

        if (effects.Count == 0)
            return name;

        for (var i = 0; i < effects.Count; i++)
            effects[i] = Effect(effects[i]);

        var granted = string.Join(", ", effects);

        return name.Length > 0 ? $"{name}: {granted}" : granted;
    }

    /// <summary>Every field of a key, one per line, for a tooltip. See Name.</summary>
    public static string Spelled(string key)
    {
        var parts = Parts(key);
        var names = new[]
        {
            "metadata", "effect mod", "art", "minimap icon", "states", "label", "render name",
            "grants",
        };
        var text = new StringBuilder();

        for (var i = 0; i < names.Length; i++)
        {
            text.Append(names[i]).Append(": ")
                .AppendLine(i < parts.Length && parts[i].Length > 0 ? parts[i] : "-");
        }

        return text.ToString();
    }

    /// <summary>A model's file name without its path or its extension, which nobody needs to read.</summary>
    private static string Model(string art)
    {
        var name = Short(art);

        return name.EndsWith(".ao", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }

    /// <summary>The short name, which is all the settings tab has room for.</summary>
    public static string Short(string metadata)
    {
        if (string.IsNullOrEmpty(metadata))
            return "?";

        var cut = metadata.LastIndexOf('/');

        return cut >= 0 && cut < metadata.Length - 1 ? metadata[(cut + 1)..] : metadata;
    }

    /// <summary>
    /// A version one key with its blessing cleared, which is a version two key.
    ///
    /// Only touches a key that has the eight fields; an effect is filed under its own id with no
    /// separators at all, and those mean the same thing in both versions.
    /// </summary>
    private static string Unblessed(string key)
    {
        var parts = key.Split('|');

        if (parts.Length != 8)
            return key;

        parts[7] = "";

        return string.Join("|", parts);
    }

    /// <summary>
    /// Which of two rows that have just become one row to keep.
    ///
    /// A decision somebody made beats a default, and between two decisions the larger weight wins -
    /// a relic priced for what it granted was priced for the best thing it granted, and the row
    /// they collapse into is the object rather than the roll. The earliest sighting is kept either
    /// way, because that is what orders the list.
    /// </summary>
    private static Priced Richer(Priced a, Priced b)
    {
        var keep = a.Set != b.Set
            ? a.Set ? a : b
            : b.Weight > a.Weight ? b : a;

        keep.First = a.First < b.First ? a.First : b.First;
        keep.Seen = a.Seen > b.Seen ? a.Seen : b.Seen;

        return keep;
    }

    /// <summary>
    /// A hand-typed value made safe to write into this file's very simple JSON.
    ///
    /// The scope is free text and the writer builds strings by hand, so a quote or a backslash in it
    /// would produce a file the reader cannot parse - and the reader is deliberately forgiving
    /// rather than strict, so it would not complain, it would silently lose rows.
    /// </summary>
    private static string Safely(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("\\", "").Replace("\"", "");

    private static string Between(string line, string after, string before)
    {
        var from = line.IndexOf(after, StringComparison.Ordinal);

        if (from < 0)
            return "";

        from += after.Length;

        var to = line.IndexOf(before, from, StringComparison.Ordinal);

        return to < 0 ? line[from..] : line[from..to];
    }

    private static float Number(string text, float fallback) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static string Folder()
    {
        Directory.CreateDirectory(Home);

        return Home;
    }
}
