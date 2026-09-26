using System;
using System.Collections.Generic;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// How far from a thing a blast still catches it, per art.
///
/// **Almost everything in a dig site is the smallest an entity can be, and two things are eight
/// times bigger.** That floor is Detonator.MinimumExtent, 2.25 grid, and it is a stated number
/// rather than an average: an ordinary marker takes it exactly, and this file exists for the
/// handful of objects that do not.
///
/// The crossings corroborate it and did not produce it. 11,777 of them - the moment each object lit
/// up and the moment it went dark as the placement indicator swept across its edge - put every
/// marker art between 2.16 and 2.26, which brackets 2.25 and is the tool's own resolution rather
/// than a difference between the arts. The eggs at 17.3 are the real exception.
///
/// **Every number in this file is GRID. The Size cell in the reference table is WORLD.** Grid
/// times 250/23 is world, so an ordinary marker is 2.25 here and 24.5 there. The two are a few
/// files apart and the same magnitude, which is the one thing the table is supposed not to allow -
/// reading a figure out of the table below and typing it into the Size column is a ten-fold error
/// and looks like a plausible number the whole way. The live readout prints both, each labelled,
/// until this is settled one way. See Wrt.Row.Size and Boundary.Lines.
///
/// | art | crossings say (grid) | lit / unlit samples |
/// | --- | --- | --- |
/// | monstermarker, _02, _03 | 2.19 - 2.20 | 3825 / 3461 |
/// | elitemarker, _02, _03 | 2.19 - 2.21 | 880 / 737 |
/// | chestmarker* | 2.16 - 2.22 | 771 / 665 |
/// | expeditionremnant01 | 2.20 | 720 / 557 |
/// | reefclam | 2.30 | 7 / 5 |
/// | wastessireneggs07, _08, _10 | 17.22 | 42 / 44 |
/// | sanctuarycap_01, laircap_01 | 17.31 | 29 / 27 (sanctuarycap only) |
/// | heathhenge01, _03, _04 | 2.16 | 15 / 8 |
///
/// **Render.Bounds does not predict this and must not be used for it - and the note below was not
/// enough to stop it being used anyway.** The Heath runic henge has bounds of 210.3 world, larger
/// than the siren eggs at 193.5, and it was put in this table at 18.8 grid on exactly that
/// reasoning: the big objects had tracked their bounds closely, so this one should too. Measured a
/// few minutes later off 23 crossings, it is **2.16** - an ordinary signpost's extent on an object
/// the size of a house. The entry is gone and the henge takes the default with every other marker.
///
/// The rule has no exceptions worth inferring from. Two objects agreeing with their bounds does not
/// make a rule; it makes two objects. Measure it or leave it at the default, which is what the
/// default is for.
///
/// **Tried again on the X and Y components alone, and it fails there too.** The henge entry above
/// records one number, so the obvious rescue is that 210.3 was its HEIGHT and that the footprint
/// might still predict the extent - a runic henge being exactly the shape whose Z dwarfs its base.
/// It does not. Read off one site, in grid:
///
/// | art | bounds X and Y | extent measured from crossings |
/// | --- | --- | --- |
/// | monstermarker, chestmarker_signpost, tundravaalremnant | 3.80 | 2.25 |
/// | elitemarker_02 | 4.55 | 2.25 |
/// | expeditionremnant01 | 6.80 | 2.25 |
/// | invisible_sentinel_holder | 4.80 | 2.25 |
/// | buriedarmourerstrongbox01, buriedlargestrongbox | 4.80 | unknown |
///
/// Five arts spanning 3.80 to 6.80 all measure 2.25, so the footprint does not track the extent
/// either. The sentry is the one that ends it: its footprint is 4.80, the same as a strongbox's to
/// the decimal, and its extent is 2.25 over 239 crossings at 99.2% agreement. Whatever a
/// strongbox's extent is, its bounds do not say.
///
/// **Positioned.Size states the footprint, and it separates the cases nothing else could.** It is
/// an int packing two sixteen bit halves - every ordinary marker reads 0x00010001, one cell by one,
/// and every one of them measures the 2.25 floor; a buried strongbox reads 0x00020002 and measures
/// about 4.28. That is the first stated field to tell a strongbox from a marker at all: Render
/// .Bounds cannot, since a sentry and a strongbox both read 4.80 and the sentry measures 2.25, and
/// inherent_explosion_radius reads nought on both.
///
/// **Two points are not a rule, and this table exists because of the last time that was forgotten.**
/// A footprint of one goes with 2.25 and a footprint of two with about 4.28, which fits several
/// arithmetics and none of them is evidence yet. What would settle it is the objects whose extents
/// are already known and are nothing like the floor - the siren eggs at 17.22, the sub-area caps at
/// 17.25 and the barrels at 5.2 - so the footprint is recorded beside every crossing rather than
/// turned into a formula. See Boundary, whose csv carries it now.
///
/// **Nor does the game state it anywhere else that has been found.** inherent_explosion_radius is
/// the only attribute that looked like it might, and it reads 0 on every strongbox - it is what
/// this object's OWN explosion reaches, sixty on a barrel and nought here, exactly as Target.Sets
/// says. See Dump's bounds table, which is where both of these were read.
///
/// **Render.Bounds does not predict this and must not be used for it.** A monster marker's bounds
/// are 41.3 world where its extent is 23.8; a remnant's are 73.9 where its extent is 23.9; a clam's
/// are 63 where its extent is 25.0. The egg happens to agree with its bounds and that is a
/// coincidence of one object, not a rule. What the game appears to test against is the physical
/// hull, and a signpost's hull is a signpost however tall the model is.
///
/// **The extent is a property of the object, not of the blast**, which is worth saying because the
/// two are only ever seen added together. The eggs were bracketed at 17.2 on a site whose blast
/// radius read 28.75 and again at 17.4 on one reading 34.75 - a six grid unit difference in the
/// blast that moved the extent by two tenths. So these carry across maps and map modifiers, and a
/// number measured once does not need re-measuring when the radius changes.
///
/// The blast radius is the other half of the catch distance and is read from the game separately
/// (see <see cref="Blast"/>), so these are absolute: a marker is caught at blast + 2.25, an egg at
/// blast + 17.3, which on this map is 31.0 against 46.1. Modelling the egg at a marker's size had
/// the planner believing it had to place fifteen grid closer than it does.
/// </summary>
internal static class Extents
{
    /// <summary>
    /// The arts that are not the ordinary size, and what they measure instead.
    ///
    /// Matched on a fragment rather than the whole name because each is a family - WastesSirenEggs07
    /// through 10 are the same nest in different poses and measured the same within a hundredth of a
    /// grid unit, so naming them one by one would only invite a variant nobody has seen yet to be
    /// treated as a signpost.
    /// </summary>
    private static readonly (string Art, float Grid)[] Big =
    {
        // 17.22 grid over 86 crossings, across arts 07, 08 and 10. The prefix has already earned
        // itself: 08 turned up after the table was written and was caught by it.
        ("wastessireneggs", 17.3f),

        // The sub-area entrance caps: 17.31 grid over 56 crossings on sanctuarycap, which is the
        // eggs' number to within a tenth of a grid unit. It went in on a shared render bound and a
        // look in game before there was any evidence, and the evidence agreed.
        //
        // **Two arts, one object.** The Peninsula tileset caps its sub-area with LairCap where the
        // Grand site used SanctuaryCap - same ExpeditionSubareaEntrance metadata, same
        // ExpeditionCavernEntrance icon, same 193.5 render bounds - so the classifier had it right
        // and this table did not, because this one is keyed on art. A cap wearing a new art would
        // have been drawn and planned at a marker's size, which is a sixth of what it is.
        //
        // Keyed on art because that is what the crossing tool measures, and the alternative - the
        // metadata - is what the classifier already uses, so the two would agree until a tileset
        // gave one object two metadata names. There is no key that cannot be surprised; the answer
        // is that a new tileset needs a look, which is what this one got.
        ("sanctuarycap", 17.3f),
        ("laircap", 17.3f),

        // Krutog's cavern and the volcanic tileset's, found the same way the Peninsula's was - by
        // being wrong in game. Same ExpeditionSubareaEntrance metadata, same ExpeditionCavernEntrance
        // icon, same 193.5 render bounds as the other two.
        ("caverncap", 17.3f),
        ("volcanocap", 17.3f),

        // The Prairie's two, added on the same standard as the two above: same
        // ExpeditionSubareaEntrance metadata, same ExpeditionCavernEntrance icon, same 193.5 render
        // bounds, both read off one dump of Grazed Prairie. Neither was in this table, so both were
        // answering to the kind rule below - which gave the right number and gave it for the wrong
        // reason, and stopped giving it at all once a discovered row started outranking the kind.
        //
        // expedition_subareacaps is the tileset's shared cap asset rather than a named one, so it is
        // the entry most likely to turn up again under another area's name.
        ("catacombcap", 17.3f),
        ("expedition_subareacaps", 17.3f),
    };

    /// <summary>
    /// The families that are big whatever art they turn up wearing.
    ///
    /// **Four arts for one object is enough to say the key was wrong.** SanctuaryCap, LairCap,
    /// CavernCap and VolcanoCap are the same sub-area entrance in four tilesets - identical metadata,
    /// identical minimap icon, identical 193.5 render bounds - and each new one was discovered by the
    /// planner treating it as a signpost, which is a sixth of its real size, and being caught at it.
    /// The table above says a new tileset needs a look; that is true and it is not a plan, because
    /// the look only happens after a dig site has already been planned wrongly.
    ///
    /// So the art table keeps its measured numbers and the KIND is the fallback underneath it. The
    /// classifier works off metadata, which is what actually identifies one of these, so a fifth
    /// tileset is caught at its right size the first time it is seen rather than the second.
    ///
    /// Only for families where every member really is the same size. Both of these are: the eggs
    /// measured 17.22 over 86 crossings across three arts, the caps 17.31 over 56.
    /// </summary>
    private static readonly (TargetKind Kind, float Grid)[] Family =
    {
        (TargetKind.Hatch, 17.3f),
        (TargetKind.Entrance, 17.3f),
    };

    /// <summary>
    /// The catch extent for a marker's art, in grid units, or the ordinary size when it has none.
    ///
    /// The table above is the whole of it. See Forget for why nothing is added to it while the game
    /// is running.
    /// </summary>
    public static float Of(string art, float ordinary)
    {
        if (!string.IsNullOrEmpty(art))
        {
            foreach (var (name, grid) in Big)
            {
                if (art.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return grid;
            }
        }

        return ordinary;
    }

    /// <summary>
    /// The extent a footprint is known to go with, in WORLD units, or null where none is.
    ///
    /// **Positioned.Size states a footprint and the game states nothing else about size.** It packs
    /// two sixteen bit halves - 0x00010001 is one cell by one - and it is the only field that tells
    /// a strongbox from a marker: Render.Bounds reads 4.80 on a sentry that measures the floor and
    /// on a strongbox that measures twice it, and inherent_explosion_radius reads nought on both.
    ///
    /// **A lookup of things measured, not a formula fitted to them.** Two footprints have an
    /// answer and both were measured off crossings, not derived: one by one over some thirty
    /// thousand of them, two by two over a few hundred. A rule through two points fits several
    /// arithmetics that disagree the moment they are extrapolated, and this file exists because a
    /// size was once inferred that way from Render.Bounds and was wrong about the very next object.
    /// So a footprint nobody has measured gets no answer, which leaves it at the ordinary size
    /// exactly as before.
    ///
    /// Add a row when a footprint is measured, not when one is met. The barrels, the siren eggs and
    /// the sub-area caps are the ones worth doing next: their extents are already known and are
    /// nothing like the floor, so they say at once whether a rule exists here at all.
    /// </summary>
    /// <summary>
    /// Every size this file already has a measured answer for, in world units, one per line.
    ///
    /// **For the hover on the Size cell, which is a world figure with nothing to compare it to.** The
    /// cell takes world units while every number in this file is grid, so somebody typing into it is
    /// converting in their head from figures they have to remember - and the note at the top of this
    /// file says a ten-fold error is exactly what that invites. The sizes the plugin already knows are
    /// the ones worth offering, since typing one of them is agreeing with a measurement rather than
    /// guessing.
    ///
    /// **Read out of the tables rather than written out again.** The numbers here are Big, Family,
    /// FromFootprint and the floor, so the hover cannot drift from what the planner uses - which is
    /// the whole failure this table was built to end. The art fragments are printed as they are keyed,
    /// so the line also says what a value is matched on.
    /// </summary>
    public static string MeasuredSizes()
    {
        var text = new StringBuilder();

        text.AppendLine("Sizes with a measured answer, in world units - the unit this cell takes:");
        text.AppendLine();
        text.AppendLine(Line(Detonator.MinimumExtent * Detonator.GridToWorld,
            "the ordinary size, and the floor - every 1x1 footprint measures it"));

        // 0x00020002 is a two by two footprint. See FromFootprint, which is keyed on the packed int.
        if (FromFootprint((2 << 16) | 2) is { } two)
            text.AppendLine(Line(two, "a buried strongbox - a 2x2 footprint"));

        // Grouped by the figure rather than listed per art, because five arts share one answer and
        // five identical lines would read as five separate measurements.
        var arts = new Dictionary<float, List<string>>();

        foreach (var (art, grid) in Big)
        {
            if (!arts.TryGetValue(grid, out var named))
                arts[grid] = named = new List<string>();

            named.Add(art);
        }

        foreach (var (grid, named) in arts)
            text.AppendLine(Line(grid * Detonator.GridToWorld, "art " + string.Join(", ", named)));

        var kinds = new Dictionary<float, List<string>>();

        foreach (var (kind, grid) in Family)
        {
            if (!kinds.TryGetValue(grid, out var named))
                kinds[grid] = named = new List<string>();

            named.Add(kind.ToString());
        }

        foreach (var (grid, named) in kinds)
        {
            text.AppendLine(Line(grid * Detonator.GridToWorld,
                string.Join(" and ", named) + " where the art is not one of the above"));
        }

        text.AppendLine();
        text.AppendLine("A barrel measures 5.2 grid, which is " +
                        (5.2f * Detonator.GridToWorld).ToString("0.00") +
                        " world. It is recorded from crossings and is in no table, so nothing uses " +
                        "it - it is here because it is a known answer, not because it is in force.");

        return text.ToString().TrimEnd();
    }

    private static string Line(float world, string what) =>
        $"  {world,7:0.00}   {what}";

    public static float? FromFootprint(int packed)
    {
        var across = packed >> 16;
        var down = packed & 0xFFFF;

        // Square only. Nothing measured so far is anything else, and an oblong would be a different
        // question - which of its sides the game tests against - rather than a gap in this table.
        if (across != down)
            return null;

        return across switch
        {
            // Measured over 44,835 crossings: every marker art fits the 2.25 grid floor, which is
            // what Detonator.MinimumExtent states. Not written to a row, since it IS the default.
            1 => null,

            // Measured on buried strongboxes, agreed range 46.1 to 47.0 world. The low end, because
            // an extent too small costs a wasted explosive and one too large costs the object.
            2 => 46.5f,

            _ => null,
        };
    }

    /// <summary>
    /// The catch extent for a target, in grid units. The one to call where a Target is in hand.
    ///
    /// **The object's own row first, then the measurements.** That order used to be the other way
    /// round, on the reasoning that a number taken from 11,777 crossings beats anything anybody
    /// typed - which is true of the DEFAULT and was applied to the correction as well. The effect
    /// was that the Size column on a row did nothing whenever the art was one of the measured ones,
    /// and the only way to disagree with a measurement was a second row in an art: namespace of its
    /// own. Two rows for one object, and the one you could see and edit was not the one that
    /// answered - which is how "caverncap" came to sit in the table beside "Sub-area entrance:
    /// caverncap_01".
    ///
    /// So the measurements are the default and the row is the correction, which is what every other
    /// number in this table already works like. Sized returns null rather than the default, so the
    /// two cases can be told apart at all. See Unknowns.Sized.
    ///
    /// Then the measured art, then the kind - which catches a sub-area cap wearing an art nobody has
    /// seen before - then the ordinary marker size, which is what every measured art came out at.
    ///
    /// The key is cached on the target once it has been filed, so this costs a dictionary lookup.
    /// </summary>
    public static float Of(Target target)
    {
        if (target == null)
            return Unknowns.Size / Detonator.GridToWorld;

        var key = Safe.Read(() => Unknowns.Key(target), "");

        // **The row naming THIS object, ahead of the row naming its whole kind.**
        //
        // RowIdOfTarget answers with kind:<Kind> for anything the scan can name and reaches for a
        // found: row only when the kind is Unknown, so an object's own discovered row stops being
        // read the moment a classifier learns to name it - and a size typed into that row does
        // nothing, while the row stays in the table looking like it should.
        //
        // Measured on prairieexplodablegate01.ao. Its own row says size 24.46 world, 2.25 grid, which
        // is the ordinary marker footprint and what its 3x3 Positioned.Size says it should be. It
        // classifies as Entrance, and kind:Entrance says 188.04 world - 17.30 grid, the sub-area cap
        // extent, correct for a cavern mouth and nearly eight times too wide for a gate. The kind row
        // answered because it was asked first, so the chain was planned on a catch radius the gate
        // does not have and placed short of it.
        //
        // **This is precedence, not a lookup that failed.** The first version of this fix added the
        // discovered read below the kind read, which changed nothing at all: the kind row has a size,
        // so it returned before the specific row was ever consulted. A row about one object is more
        // specific than a row about every object of a kind, and more specific wins - the same order
        // the comment above states for the measurements, one layer further in.
        var discovered = Safe.Read(key, static k => Wrt.Of(Wrt.Id.Found(k))?.Size, null);

        if (discovered is > 0f)
            return discovered.Value / Detonator.GridToWorld;

        // **Then the row the table shows for this object's kind.** A size typed into the Size column
        // is written to that row - see Catalogue's site rows - and this read it off the discovered row
        // instead, so an edit stored fine, was never read, and the cell refilled itself from the
        // old answer a frame later. One rule for which row answers, in one place, so the cell that
        // is editable and the cell that is read cannot come apart again. See Weighing.RowIdOfTarget.
        var own = Safe.Read(() => Wrt.Of(Weighing.RowIdOfTarget(target))?.Size, null);

        if (own is > 0f)
            return own.Value / Detonator.GridToWorld;

        // The discovered row as well, since that is where sizes went before and a file written then
        // is still somebody's answer.
        var said = Safe.Read(() => Unknowns.Sized(key), null);

        if (said.HasValue)
            return said.Value / Detonator.GridToWorld;

        var art = target.Art;

        if (!string.IsNullOrEmpty(art))
        {
            foreach (var (name, grid) in Big)
            {
                if (art.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return grid;
            }
        }

        // **The kind rule is skipped for an object that has a row of its own.**
        //
        // Asked for: an object the plugin has filed is one somebody is expected to price, and having
        // it silently inherit a whole kind's extent makes the row in front of them not the answer.
        // It also produced a genuine surprise - the sub-area caps carry a discovered size of 24.5,
        // which is Unknowns.Size, the ordinary default written out as though it were a measurement -
        // so those rows and kind:Entrance disagreed by a factor of eight with nothing saying which
        // was in force.
        //
        // **The accepted cost, stated plainly:** an Entrance or Hatch wearing an art the table above
        // does not name, and already filed, now measures the ordinary size until somebody sets it.
        // That is an under-estimate, which places explosives closer than they need to be - wasteful
        // and not a miss - where the kind rule's failure mode when wrong is the object not being
        // caught at all. See Big, which is where a measured art belongs.
        var filed = key.Length > 0 && Wrt.Of(Wrt.Id.Found(key)) != null;

        if (!filed)
        {
            foreach (var (family, grid) in Family)
            {
                if (family == target.Kind)
                    return grid;
            }
        }

        return Unknowns.Extent(key) / Detonator.GridToWorld;
    }

    /// <summary>
    /// The same, with the kind to fall back on when the art is one nobody has measured. See Family.
    /// </summary>
    public static float Of(TargetKind kind, string art, float ordinary)
    {
        if (!string.IsNullOrEmpty(art))
        {
            foreach (var (name, grid) in Big)
            {
                if (art.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return grid;
            }
        }

        foreach (var (family, grid) in Family)
        {
            if (family == kind)
                return grid;
        }

        return ordinary;
    }

    /// <summary>
    /// Nothing is learned at runtime, deliberately.
    ///
    /// **A marker that changes size while you watch is a permanent source of doubt.** The crossing
    /// tool measures the catch extent per art and it used to feed the answer straight back in, so a
    /// ring would grow or shrink mid-session - and when a measurement went wrong, as one did by
    /// pinning a floor above its own ceiling, there was no way to tell a real correction from a bad
    /// frame. Every ring on screen became a question.
    ///
    /// So the table above is the only source. It is measured the same way, from the same crossings,
    /// and it changes in a commit where the number can be looked at beside its evidence rather than
    /// halfway through a dig. The measuring still runs and still writes marker_extent.csv and its
    /// summary; what it no longer does is act.
    /// </summary>
    public static void Forget()
    {
    }
}
