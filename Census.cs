using ExileCore2;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// Every remnant ever seen, written down, so the odds can be measured instead of assumed.
///
/// The question it exists to answer: a Liquid Verisium replaces a remnant with a different one, so
/// what is a roll worth? That is a question about the distribution of remnants the game generates,
/// and the game will not say - Expedition2RunesWeight is named for the weights and carries none.
/// Nothing short of counting them answers it.
///
/// **It records rather than assumes, and that distinction is the whole design.** The obvious shortcut
/// is to take the remnants on a map as a sample of what a roll returns. That is probably true and it
/// is not established, so each observation is tagged with whether the remnant was generated with the
/// map or is the result of a roll - the "is_rerolled" state says which. Two populations, counted
/// separately, and whether they match becomes a thing to look at rather than a premise. If they do,
/// they pool and the sample doubles for free.
///
/// Better still, a remnant seen before and after that flag changes is a matched pair: the same
/// position, the layout it had and the layout it became. That is a direct sample of the conditional
/// distribution, which needs no assumption at all.
///
/// A roll DOES change the socket count - confirmed in game - which is why the socket column is a
/// variable to be measured here rather than context. It is also why a roll can change the route and
/// not just the reward: sockets are in the planner's objective, so a remnant that rolls into more
/// of them is worth more to the chain, and one off the path can be rolled onto it.
///
/// **Identities are recorded, not prices.** A value in exalts banked today is a wrong number in a
/// fortnight; the reward names keep, and NinjaPricer can price them whenever the question is asked.
/// The area level goes in too, because recipes are level-gated and pooling a level 65 map with a
/// level 82 one mixes two distributions.
/// </summary>
internal sealed class Census
{
    /// <summary>What has already been written, so a remnant is recorded once per state it is in.</summary>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    private string _path;
    private int _rows;

    /// <summary>How many remnants this session has added to the file.</summary>
    public int Rows => _rows;

    /// <summary>
    /// Forgets what has been recorded, but only on a genuinely different instance.
    ///
    /// **It used to clear on every area change, and that made the file count dwell time.** Walking
    /// to town and back is an area change, and so is a portal in and out - each one emptied the set
    /// and every remnant on the map was written down again. The file holds 4,626 rows over 627
    /// distinct observations because of it, so any count taken from it straight is weighted by how
    /// long somebody stood in each map rather than by what the game generated.
    ///
    /// Guarded on the hash, like Refused and Insisted. The set survives a trip out and back, and a
    /// row is written once per remnant per state, which is what it always said it did.
    /// </summary>
    public void AreaChange(uint areaHash)
    {
        if (areaHash != _area)
        {
            _seen.Clear();

            // Emptied, so it has to be filled again before the next decision - otherwise leaving a
            // site and coming back writes every remnant in it a second time. See Remembering.
            _remembered = false;
        }

        _area = areaHash;
    }

    private uint _area;

    /// <summary>
    /// Writes down any remnant not already recorded in its current layout.
    ///
    /// **Keyed on the layout, not on the rolled flag, and that is a correction.** It used to be
    /// position plus the flag, on the reasoning that a roll flips the flag and so writes a second
    /// row - the matched pair the whole file exists to collect. It does write a second row, and for
    /// 143 of the first 159 pairs that row held the layout the remnant had BEFORE the roll: same
    /// fixed rune, same slot, same sockets, same option list. The flag flips on the click; the new
    /// layout lands a sweep or so later, and by then the key was already in the set, so the real
    /// remnant could never be recorded. Ninety seven of those rows were the only row that remnant
    /// ever got.
    ///
    /// A layout key fixes it without any timing guess: the stale row still gets written, the real
    /// one arrives behind it, and both are in the file. Reading it means taking the LAST row for a
    /// cell in a given state - a first-sweep row is superseded rather than trusted.
    ///
    /// A roll keeping the same socket count is ordinary and not evidence of this fault; the fixed
    /// rune being identical is, because rerolling the runes is the entire point of the orb.
    /// </summary>
    public void Observe(BaseSettingsPlugin<AutoExpeditionSettings> plugin, GameController gc,
        Scan scan, Valuation valuation)
    {
        var area = Safe.Read(() => gc.Area.CurrentArea.Name, "?") ?? "?";
        var hash = Safe.Read(() => gc.Area.CurrentArea.Hash, 0u);
        var level = Safe.Read(() => gc.IngameState.Data.CurrentAreaLevel, 0);

        // **The map's floor on rune slots, because it makes the socket counts in this file not a
        // sample of anything.**
        //
        // An atlas passive grants "All Verisium Remnants have at least X rune slots" and the map
        // carries it as a visible stat, so it is readable in the map rather than only on the tree:
        // MapExpedition2RemnantsHaveAtLeastXSlots, seen at 5 and reported as reaching 7. Every
        // remnant in such a map is at or above that floor by construction.
        //
        // Without the column those rows are indistinguishable from natural ones, and a few of these
        // maps would pull the whole socket distribution upwards - which is the distribution the
        // reroll shares are drawn from. See Rolls.Pins and NOTES on reading this file: the floor is
        // what a reader filters on before counting sockets at all.
        //
        // Nought where no such modifier is present, which is the ordinary case, and nought is also
        // what an unreadable stat gives - a distinction that does not matter here, since both mean
        // "no floor stated".
        var floor = Rolls.MapSlotFloor(gc);

        // **The other modifiers that change how remnants are made, as the map states them.** "+# Verisium Remnant with
        // at least # Rune slots" adds a remnant of seven, eight or nine slots on purpose, which no socket count in this
        // file should be read as a draw of; the Power rune one does the same for a rune; the lucky rolls change the draw
        // itself in a way nothing here has measured. Written as the raw values, since what each number means is not
        // settled, so a reader can filter on any of them.
        var remnantWithAtLeastSlots = Rolls.MapStat(gc, GameStat.MapLogbookHasAtLeast1Expedition2RemnantWithAtLeastXSlots);
        var remnantWithPowerRune = Rolls.MapStat(gc, GameStat.MapLogbookHasAtLeast1Expedition2RemnantWithAPowerRune);
        var luckyRolls = Rolls.MapStat(gc, GameStat.MapExpedition2RemnantGenerationHasXLuckyRolls);
        var luckyRollChance = Rolls.MapStat(gc, GameStat.MapExpedition2RemnantGenerationHasAdditionalLuckyRollChancePct);

        // **Whether the site is a Grand Expedition**, since its socket counts are a different population: seven sockets
        // come up there and almost never anywhere else. It was told from the map name until 2026-10-06. See
        // Rolls.SocketsFor and Detonator.Grand.
        var grand = Detonator.Grand(gc);

        // **Read the file back BEFORE deciding what is new, which is the whole of the dedupe bug.**
        //
        // _seen is what stops a remnant being written twice, and it was seeded by Remember - called
        // from Append, which runs after this loop has already decided every row. So the first sighting
        // of a session compared each remnant against an EMPTY set, wrote the lot, and only then read
        // the file. Four plugin reloads in one map therefore produced four copies of every remnant:
        // measured at 67 rows over 28 distinct keys on one site, and 32 over 21 on another.
        //
        // Costs one read of the file per generation and nothing afterwards. See Remembering.
        Remembering(plugin);

        var rows = new List<string>();

        foreach (var target in scan.Targets)
        {
            // Only a loaded remnant can be read at all, and only one that has been priced has had
            // its options worked out. An unpriced one is not evidence of anything yet.
            if (target.Kind != TargetKind.Remnant || !target.Live || target.Spread.Options == 0)
                continue;

            var rolled = target.Rerolled;
            var layout = $"{target.Sockets}/{target.FixedRune}/{target.FixedSlot}/" +
                         $"{Slots(target)}/{Carried(target)}/{target.Spread.Options}";
            var key = $"{hash}:{(int)target.Grid.X},{(int)target.Grid.Y}:{(rolled ? 1 : 0)}:{layout}";

            if (!_seen.Add(key))
                continue;

            // Read here rather than in the row, so a failure leaves a blank figure instead of
            // dropping the observation. Both are nought when the valuation cannot answer, which a
            // reader can tell from a real nought: a remnant with no reachable recipe would not have
            // passed the Options test above.
            // The pin the game admits that matches what this remnant actually has, which is the
            // route from a rune's id to the row RecipesForShape wants. Matching rather than looking
            // up, because the admitted list is the one place that pairing is worked out.
            var admitted = Safe.Read(() => valuation.FixedRunesPossibleAt(target.Sockets), null)
                           ?? new List<(Expedition2Rune Rune, int Slot)>();
            var pins = admitted.Count;
            var reachable = 0;

            foreach (var (rune, slot) in admitted)
            {
                if (slot != target.FixedSlot ||
                    !string.Equals(Safe.Read(() => rune.Id, null), target.FixedRune,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                reachable = Safe.Read(() => valuation.RecipesForShape(target.Sockets, rune, slot).Count, 0);

                break;
            }

            rows.Add(string.Join(",",
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                Clean(area),
                level.ToString(CultureInfo.InvariantCulture),
                hash.ToString(CultureInfo.InvariantCulture),
                ((int)target.Grid.X).ToString(CultureInfo.InvariantCulture),
                ((int)target.Grid.Y).ToString(CultureInfo.InvariantCulture),
                rolled ? "rerolled" : "fresh",
                target.Sockets.ToString(CultureInfo.InvariantCulture),
                Clean(target.FixedRune),
                target.FixedSlot.ToString(CultureInfo.InvariantCulture),
                Clean(Slots(target)),
                target.Spread.Options.ToString(CultureInfo.InvariantCulture),
                Clean(Rewards(target)),

                // **Last, not beside propagatingSlots where it belongs.** Adding a column in the
                // middle of a file that is appended to for months means every row written before
                // today parses one field short and silently shifts - rewards read out of the
                // options column, and nothing says so. On the end, an old row is simply missing its
                // last field, which every reader handles.
                Clean(Carried(target)),

                // **The identity this row was deduplicated on, written down.**
                //
                // A row is not a remnant: _seen is per session, so walking back into a site writes
                // every remnant in it again, and one remnant has produced 163 rows differing only
                // in `when`. Analysing the file therefore begins by deduplicating, and every reader
                // has to guess which columns are identity. Guessing wrong is not a rounding error -
                // deduplicating on every column but `when` counts one remnant several times when
                // its reward string changed with activation, and deduplicating on too few merges
                // genuinely different readings.
                //
                // So the key is a column. Group by it and each group is one remnant in one state.
                Clean(key),

                // **What state the client was in when this was read**, because two of the columns
                // above depend on it and nothing said so. An activated remnant reports no rewards
                // and a cached count of nought, so rows for one remnant differ in `options` and
                // `rewards` for a reason that is about the reading rather than the remnant.
                target.State("activated").ToString(CultureInfo.InvariantCulture),
                Clean(target.Chose),

                // **How many recipes the constraint tables admit, and how many pins the level does.**
                //
                // Both were needed to check this file and neither was in it: confirming that every
                // reachable recipe is offered meant pairing rows against a dump, and measuring how
                // often each pin comes up meant reading the admitted set out of a dump taken at the
                // same area level. The pair set is LEVEL specific - a quarter of the rows in this
                // file are from other levels - so a reading is only comparable with the admitted
                // count that was in force when it was taken. Written per row, the file answers both
                // on its own.
                reachable.ToString(CultureInfo.InvariantCulture),
                pins.ToString(CultureInfo.InvariantCulture),

                // The map's floor on rune slots, last because columns are only ever appended. See
                // where it is read: a row from a map with a floor is not a sample of the natural
                // socket distribution and has to be filtered out before counting.
                floor.ToString(CultureInfo.InvariantCulture),

                // The other generation modifiers, appended after it. See where they are read.
                remnantWithAtLeastSlots.ToString(CultureInfo.InvariantCulture),
                remnantWithPowerRune.ToString(CultureInfo.InvariantCulture),
                luckyRolls.ToString(CultureInfo.InvariantCulture),
                luckyRollChance.ToString(CultureInfo.InvariantCulture),
                grand ? "1" : "0"));
        }

        if (rows.Count > 0)
            Append(plugin, rows);
    }

    /// <summary>
    /// The propagating slot indices, so a rune's reach can be recovered later.
    ///
    /// **Zero based, like fixedSlot, and the game displays them one based.** A remnant recorded as
    /// propagating "1 2" shows slots two and three lit in the interface. The two columns agree with
    /// each other, which is the property worth keeping; agreeing with the screen instead would mean
    /// the file and the code disagreed.
    /// </summary>
    private static string Slots(Target target)
    {
        if (target.Passing is not { Count: > 0 })
            return "";

        var text = new StringBuilder();

        foreach (var slot in target.Passing)
        {
            if (text.Length > 0)
                text.Append(' ');

            text.Append(slot.Slot);
        }

        return text.ToString();
    }

    /// <summary>
    /// What is actually sitting in each propagating slot, as "slot=rune" per slot.
    ///
    /// **The slot indices alone do not answer the question this file exists for.** A roll returning
    /// a propagating slot is worth almost nothing to know; a roll returning Opulent in one is the
    /// difference between a remnant to build the chain around and a dud, and a slot number cannot
    /// tell those apart. The runes were always known - Passes carries the candidates for each slot -
    /// and simply were not written down.
    ///
    /// Several candidates separated by "/" where the combination has not been picked yet, because a
    /// slot only says "whatever ends up here carries forward". One candidate means the choice is
    /// already made, which is always true of the slot holding the fixed rune. See Valuation.Passing.
    ///
    /// **This is a POSSIBILITY SET and must never be counted as a frequency.** It is a union over
    /// every recipe still reachable, so a slot can list up to 32 runes - measured: 242 slots listed
    /// one, 113 two, 7 all thirty-two. Only `fixedRune` is an observation of what the game chose.
    /// Counting these as draws once produced a per-rune distribution that looked plausible and was
    /// meaningless. See NOTES 8z.
    /// </summary>
    private static string Carried(Target target)
    {
        if (target.Passing is not { Count: > 0 })
            return "";

        var text = new StringBuilder();

        foreach (var slot in target.Passing)
        {
            if (text.Length > 0)
                text.Append(' ');

            text.Append(slot.Slot).Append('=')
                .Append(slot.Runes is { Count: > 0 } ? string.Join("/", slot.Runes) : "?");
        }

        return text.ToString();
    }

    /// <summary>
    /// Every reward this remnant could have become, by name, richest first.
    ///
    /// Names rather than values, so the row can be re-priced against whatever the league thinks
    /// today. The count is recorded separately because a name list can be truncated by a patch
    /// renaming something and a count cannot.
    /// </summary>
    private static string Rewards(Target target)
    {
        var text = new StringBuilder();

        foreach (var reward in target.Rewards)
        {
            if (text.Length > 0)
                text.Append(" | ");

            text.Append(reward.Name);
        }

        return text.ToString();
    }

    /// <summary>The columns this version writes, in order. One place, so the two uses agree.</summary>
    private const string Columns =
        "when,area,level,areaHash,x,y,origin,sockets,fixedRune,fixedSlot," +
        "propagatingSlots,options,rewards,passedRunes," +

        // Appended, never inserted, so Upgrade can bring an existing file forward and its old rows
        // stay valid while simply stopping short. See Upgrade.
        "dedupe,activated,selected,reachable,admittedPins," +
        "mapSlotFloor," +
        "mapRemnantWithAtLeastSlots,mapRemnantWithPowerRune,mapLuckyRolls,mapLuckyRollChancePct," +
        "grand";

    /// <summary>
    /// Brings an existing file up to the current header, once, leaving its rows alone.
    ///
    /// Only when the header has actually changed, and only ever by adding columns to the end - so
    /// the rows underneath stay valid and merely stop short. A file whose header already matches is
    /// read and not written.
    /// </summary>
    private static void Headed(string path, string columns)
    {
        if (!File.Exists(path))
            return;

        string[] lines;

        try
        {
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return;
        }

        if (lines.Length == 0 || string.Equals(lines[0], columns, StringComparison.Ordinal))
            return;

        // Refuses to touch a file whose header is not a prefix of this one, because that is not a
        // column being added - it is a different file, or one written by a version that moved
        // something, and rewriting its header would mislabel every row in it.
        if (!columns.StartsWith(lines[0], StringComparison.Ordinal))
            return;

        lines[0] = columns;
        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// Takes the identities already in the file into the seen set, once, when the path is first used.
    ///
    /// **Because the set is per session and a reload starts a new one.** The file holds one row per
    /// remnant per state by design, and that design only held within a session: unloading the plugin,
    /// or restarting the game, emptied the set and every remnant walked past again was written down
    /// again. Measured before this: 11,397 rows carrying 1,666 distinct readings, a sevenfold
    /// inflation, and the cost is not the disk - it is that every count taken from the file has to
    /// begin by deduplicating it, and getting that wrong changes conclusions rather than rounding
    /// them.
    ///
    /// **Reads the dedupe column rather than rebuilding the key from the others**, so the file and
    /// this cannot disagree about what identity means. A row written before that column existed has
    /// no field there and is skipped, which costs one duplicate per such remnant and never a wrong
    /// skip.
    ///
    /// Failure is not fatal and not silent: an unreadable file leaves the set empty, which writes
    /// duplicates exactly as before rather than losing an observation.
    /// </summary>
    private void Remember(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var header = Columns.Split(',');
            var at = System.Array.IndexOf(header, "dedupe");

            if (at < 0)
                return;

            var lines = File.ReadAllLines(path);

            // The file's own header, not this version's, because an older file has fewer columns and
            // the dedupe field sits at a different index - or at none, in which case there is
            // nothing to take.
            if (lines.Length < 2)
                return;

            var had = lines[0].Split(',');
            var was = System.Array.IndexOf(had, "dedupe");

            if (was < 0)
                return;

            for (var i = 1; i < lines.Length; i++)
            {
                var fields = lines[i].Split(',');

                if (fields.Length > was && fields[was].Length > 0)
                    _seen.Add(fields[was]);
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(
                $"[AutoExpedition] Could not read the remnant census back, so it may write rows it " +
                $"already has: {ex.Message}", 5f);
        }
    }

    private static string Clean(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace(',', ';').Replace('\n', ' ').Trim();

    /// <summary>
    /// Fills _seen from the file, once per generation, before anything is compared against it.
    ///
    /// **Resolves the path without writing to it**, so asking what is already recorded cannot create
    /// the file or rewrite its header - Append still owns both of those.
    ///
    /// A generation ends when AreaChange empties the set. Re-seeding then matters because the set is
    /// cleared on entering a different area and Remember only ever ran once a session, so returning to
    /// a site already in the file wrote all of it again.
    /// </summary>
    private void Remembering(BaseSettingsPlugin<AutoExpeditionSettings> plugin)
    {
        if (_remembered)
            return;

        _remembered = true;

        var at = _path ?? Safe.Read(
            () => Path.Combine(plugin.ConfigDirectory, "dumps", "remnants.csv"), null);

        if (at != null)
            Remember(at);
    }

    /// <summary>Whether _seen has been filled from the file for this generation. See Remembering.</summary>
    private bool _remembered;

    private void Append(BaseSettingsPlugin<AutoExpeditionSettings> plugin, List<string> rows)
    {
        try
        {
            if (_path == null)
            {
                var directory = Path.Combine(plugin.ConfigDirectory, "dumps");
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "remnants.csv");

                // **An existing file keeps its own header, which is how a column goes unnoticed.**
                // The header is written once, when the file is created, and every session after
                // that appends under it - so a row with a new field on the end sits beneath a
                // header that does not name it, and a reader either drops the field or refuses the
                // row. Rewriting the header is the whole fix: the old rows are short by one field,
                // which reads as empty, and nothing is lost or moved.
                Headed(_path, Columns);

                if (!File.Exists(_path))
                {
                    File.WriteAllText(_path, Columns + "\n");
                }

                // Remembering has already filled _seen for this generation, before anything was
                // compared against it - which is the point. Reading the file again here would be a
                // second path doing one job, and the one that used to run too late.
            }

            File.AppendAllLines(_path, rows);
            _rows += rows.Count;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[AutoExpedition] Could not write the remnant census: {ex.Message}", 5f);
        }
    }
}
