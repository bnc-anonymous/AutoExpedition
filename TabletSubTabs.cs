using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExileCore2;
using Newtonsoft.Json;

namespace AutoExpedition;

/// <summary>
/// What each of the fragment tab's Expedition sub-tabs held when last seen, kept on disk, so a run does not go through
/// all six every time. A sub-tab's record is updated whenever it is the one showing - a run's visit or the player's own.
///
/// **Sub-tabs fill in order**: tablets go into sub-tab 2 only once 1 is full (96 tablets), into 3 once 2 is, and so on,
/// by the player's account (2026-10-09). So past the first sub-tab that is not full, one that was empty is likely empty
/// still. A run visits a sub-tab only when it is worth it - see SubTabWorthVisiting - and goes through all six at most
/// once every FullScanEvery.
/// </summary>
internal static partial class Tablets
{
    /// <summary>One sub-tab when last seen: tablets, junk among them, tablets needing a crafting currency, and when.</summary>
    internal sealed class SubTabRecord
    {
        public int Tablets { get; set; }
        public int Junk { get; set; }
        public int Craftable { get; set; }

        /// <summary>Unidentified tablets; null in a record saved before it was counted, which is visited in case.</summary>
        public int? Unidentified { get; set; }
        public DateTime When { get; set; }

        /// <summary>
        /// Every tablet the sub-tab held, where and what: null in a record saved before contents were kept. See
        /// RememberedContents.
        /// </summary>
        public List<RememberedTablet> Contents { get; set; }
    }

    /// <summary>One tablet of a sub-tab as last seen: its rectangle on screen, its uses, its fingerprint and its worth.</summary>
    internal sealed class RememberedTablet
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public int Uses { get; set; }
        public string Fingerprint { get; set; } = "";

        /// <summary>Its worth in exalts for ordering a withdraw, when it had one. See WorthForWithdraw.</summary>
        public double? Worth { get; set; }

        /// <summary>Whether it was junk when seen; null in a record saved before this was kept. See PlannedSubTabsAfter.</summary>
        public bool? Junk { get; set; }

        /// <summary>Whether it was unidentified at full uses when seen; null in a record saved before this was kept.</summary>
        public bool? Unidentified { get; set; }

        public ExileCore2.Shared.RectangleF Rect => new(X, Y, Width, Height);
    }

    /// <summary>
    /// How long a sub-tab's remembered contents are taken as what it holds, without a visit. The stash changes only with
    /// a sub-tab showing - a deposit by hand shows the sub-tab it lands in - and a sub-tab showing is recorded, so what was
    /// seen stays true until it is seen again; the limit is for what this cannot see. Withdraw went through every sub-tab
    /// on every press, though each had just been seen (2026-10-10). Chosen.
    /// </summary>
    private static readonly TimeSpan ContentsTrustedFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A sub-tab's tablets as last seen, when that was within ContentsTrustedFor and the record kept them; else null,
    /// and the sub-tab is to be visited. The sub-tab showing is never answered from memory: it is read as it is.
    ///
    /// **An empty sub-tab is empty however long ago it was seen, while the records add up to the Expedition Tablets
    /// button**, and so is one never seen: every tablet the button counts is in another. The tidy after a crafting run
    /// went through sub-tabs 2 to 6, each recorded as empty, with the button and the records both at 16 (dump 2026-10-11
    /// 23:58:47). A sub-tab holding tablets keeps the time limit: where in it each one sits is not covered by the count.
    /// </summary>
    private static List<RememberedTablet> RememberedContents(GameController gc, int number)
    {
        if (SelectedSubTab(gc) == number)
            return null;

        var recorded = Book.SubTabs.TryGetValue(number, out var record) ? record : null;

        if ((recorded == null || recorded.Tablets == 0) && RecordsAccountForAll(gc) == true)
            return [];

        return recorded?.Contents is { } contents && DateTime.UtcNow - recorded.When < ContentsTrustedFor ? contents : null;
    }

    /// <summary>The records, and when all six were last gone through, as saved.</summary>
    internal sealed class SubTabBook
    {
        public Dictionary<int, SubTabRecord> SubTabs { get; set; } = [];
        public DateTime LastFullScan { get; set; }
    }

    /// <summary>How often a run goes through all six sub-tabs whatever the records say.</summary>
    private static readonly TimeSpan FullScanEvery = TimeSpan.FromHours(8);

    /// <summary>How many tablets fill a sub-tab: 96 read on a full one (2026-10-09 16:23:34).</summary>
    private const int SubTabHolds = 96;

    /// <summary>Where the records are kept. Set by the plugin from its config directory.</summary>
    internal static string SubTabFile { get; set; } = "";

    private static SubTabBook _subTabBook;

    private static bool _subTabBookDirty;

    private static SubTabBook Book
    {
        get
        {
            if (_subTabBook != null)
                return _subTabBook;

            try
            {
                if (SubTabFile.Length > 0 && File.Exists(SubTabFile))
                    _subTabBook = JsonConvert.DeserializeObject<SubTabBook>(File.ReadAllText(SubTabFile));
            }
            catch (Exception)
            {
                // An unreadable book is started afresh: the worst it costs is a full scan.
            }

            return _subTabBook ??= new SubTabBook();
        }
    }

    /// <summary>Writes the records when they have changed. Called on each read of the screen.</summary>
    private static void SaveSubTabBook(bool force = false)
    {
        // At most every SubTabBookSavedEvery: the records carry each sub-tab's contents, and during a run they change on
        // nearly every read. Written whatever the time as the plugin unloads. See FlushSubTabBook.
        if (!_subTabBookDirty || SubTabFile.Length == 0 || !force && DateTime.UtcNow - _subTabBookSavedAt < SubTabBookSavedEvery)
            return;

        _subTabBookSavedAt = DateTime.UtcNow;

        try
        {
            var writing = SubTabFile + ".tmp";

            File.WriteAllText(writing, JsonConvert.SerializeObject(Book, Formatting.Indented));
            File.Move(writing, SubTabFile, true);
            _subTabBookDirty = false;
        }
        catch (Exception)
        {
            // Tried again on the next read.
        }
    }

    /// <summary>
    /// Records the sub-tab the last read was taken from, when the fragment tab's Expedition tablets are up. Called on each
    /// read, two frames after it; nothing is recorded when another sub-tab is showing by then. See _subTabAtRead.
    /// </summary>
    private static void NoteSubTab(GameController gc, TabletRerollingSettings settings)
    {
        if (_stashRect.Width <= 0f || _subTabAtRead is not (> 0 and var number) || SelectedSubTab(gc) != number)
            return;

        // Only the Expedition tablets' sub-tabs: the same numbered sub-tabs show every tablet type. Known by what the
        // sub-tab shows, or, for an empty one, by a run having just brought the stash to them itself - any run that
        // navigates there. Withdraw and the tidy were left out, so a sub-tab they emptied kept its old contents for
        // ContentsTrustedFor, and the next Withdraw went back and forth to it for tablets no longer there (2026-10-10).
        // From the read's own walk, not a second one. See _stashTabItemPathsAtRead.
        var shown = _stashTabItemPathsAtRead;
        var expedition = shown.Count > 0
            ? shown.All(x => x.StartsWith(ExpeditionTabletPath, StringComparison.OrdinalIgnoreCase))
            : _reforgingHasCursor && (_run is TabletRun.FullAuto or TabletRun.Craft or TabletRun.WithdrawValuable or TabletRun.Tidy) &&
              _navigateStep >= 2;

        if (!expedition)
            return;

        var tablets = StashTablets();
        using var counting = Spent.On("Tablets/NoteSubTab/Counts");
        var record = new SubTabRecord
        {
            Tablets = tablets.Count,
            Junk = tablets.Count(t => IsJunk(gc, t, settings)),
            Craftable = tablets.Count(t => t.Uses == FullUses && (t.Rarity == ExileCore2.Shared.Enums.ItemRarity.Normal || t.Identified) &&
                                           CraftingActions.Contains(ActionOf(t, settings))),
            Unidentified = tablets.Count(t => !t.Identified && t.Uses == FullUses),
            When = DateTime.UtcNow,
        };

        // What it holds and where, compared each read: a change of place or of tablet, not only of count, is a new record.
        var contents = tablets.Select(t => (t.Rect, Fingerprint: FingerprintOf(t), Tablet: t)).ToList();

        bool Same(List<RememberedTablet> had) =>
            had != null && had.Count == contents.Count &&
            had.Zip(contents).All(x => MathF.Abs(x.First.X - x.Second.Rect.X) < 1f && MathF.Abs(x.First.Y - x.Second.Rect.Y) < 1f &&
                                       x.First.Fingerprint == x.Second.Fingerprint);

        if (Book.SubTabs.TryGetValue(number, out var before) && before.Tablets == record.Tablets && before.Junk == record.Junk &&
            before.Craftable == record.Craftable && before.Unidentified == record.Unidentified && Same(before.Contents) &&
            before.Contents.All(x => x.Junk != null) &&
            record.When - before.When < TimeSpan.FromMinutes(1))
            return;

        // Worked out only as the record is written: a search's key for each tablet, not each read.
        record.Contents = contents.Select(x => new RememberedTablet
        {
            X = x.Rect.X, Y = x.Rect.Y, Width = x.Rect.Width, Height = x.Rect.Height, Uses = x.Tablet.Uses,
            Fingerprint = x.Fingerprint, Worth = WorthForWithdraw(gc, x.Tablet, settings),
            Junk = IsJunk(gc, x.Tablet, settings), Unidentified = !x.Tablet.Identified && x.Tablet.Uses == FullUses,
        }).ToList();

        Book.SubTabs[number] = record;
        _subTabBookDirty = true;
    }

    private static DateTime _subTabBookSavedAt;

    private static readonly TimeSpan SubTabBookSavedEvery = TimeSpan.FromSeconds(2);

    /// <summary>Writes the sub-tab records now, if changed. Called as the plugin unloads.</summary>
    internal static void FlushSubTabBook() => SaveSubTabBook(force: true);

    /// <summary>Whether all six sub-tabs are due to be gone through: none has been for FullScanEvery.</summary>
    private static bool FullScanDue => DateTime.UtcNow - Book.LastFullScan > FullScanEvery;

    /// <summary>A run has gone through all six: the next full scan is FullScanEvery away.</summary>
    private static void FullScanDone()
    {
        Book.LastFullScan = DateTime.UtcNow;
        _subTabBookDirty = true;
    }

    /// <summary>The lowest sub-tab not full when last seen, or one never seen: where new tablets go.</summary>
    private static int FirstNotFull() =>
        Enumerable.Range(1, 6).FirstOrDefault(n => !Book.SubTabs.TryGetValue(n, out var r) || r.Tablets < SubTabHolds, 6);

    /// <summary>What a run is looking in a sub-tab for.</summary>
    private enum SubTabPurpose
    {
        Junk,
        Craftable,
        Unidentified,
        Room,
    }

    /// <summary>
    /// Whether a run should visit this sub-tab: always when a full scan is due or it has never been seen or not for
    /// FullScanEvery; else when it held what the run is looking for - junk, tablets needing a currency, room for a
    /// deposit - or it is the first not full, where new tablets land. A sub-tab past that one, empty when last seen, is
    /// passed by.
    /// </summary>
    private static bool SubTabWorthVisiting(GameController gc, int number, SubTabPurpose purpose)
    {
        // **The Expedition Tablets button's count says whether the records are whole.** Adding up to it, a sub-tab never
        // seen holds none and an old record is still right, so neither is visited and no full scan is needed; not adding
        // up, every sub-tab is visited until they do. Unreadable, the records are judged by age as before. Records right
        // in total but not in which sub-tab holds what - tablets moved between sub-tabs with the plugin not running - are
        // not caught.
        var accounted = RecordsAccountForAll(gc);

        if (accounted == false)
            return true;

        if (accounted != true && FullScanDue)
            return true;

        if (!Book.SubTabs.TryGetValue(number, out var record))
            return accounted != true || purpose == SubTabPurpose.Room && number == FirstNotFull();

        if (accounted != true && DateTime.UtcNow - record.When > FullScanEvery)
            return true;

        return purpose switch
        {
            SubTabPurpose.Junk => record.Junk > 0 || number == FirstNotFull(),
            SubTabPurpose.Craftable => record.Craftable > 0 || number == FirstNotFull(),
            SubTabPurpose.Unidentified => record.Unidentified is null or > 0 || number == FirstNotFull(),
            _ => record.Tablets < SubTabHolds,
        };
    }

    /// <summary>
    /// The count on the fragment tab's Expedition Tablets button - the run clicks it to show Expedition tablets, content
    /// child 2, 2 - or null when the stash does not show it or no number can be read on it. It counts every Expedition
    /// tablet in the six sub-tabs: 46 with 44, 1 and 1 in sub-tabs 1, 2 and 3, by the player's account (2026-10-11).
    /// </summary>
    private static int? ExpeditionTabletsTotal(GameController gc)
    {
        var button = FragmentTabContent(gc) is { } content ? Safe.Read(content, static c => c.Children[2].Children[2], null) : null;

        if (button == null)
            return null;

        foreach (var text in TextsUnder(button))
        {
            if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var total))
                return total;
        }

        return null;
    }

    /// <summary>The tablets the six sub-tab records hold between them.</summary>
    private static int RecordedTabletsTotal() => Enumerable.Range(1, 6).Sum(n => Book.SubTabs.TryGetValue(n, out var r) ? r.Tablets : 0);

    /// <summary>
    /// Whether the sub-tab records hold every Expedition tablet the button counts: true when they add up to it, false
    /// when they have not for MismatchHeldFor, null when the button cannot be read or a mismatch is newer than that. A
    /// tablet withdrawn lowers the button at once and its sub-tab's record a read later, so a moment's mismatch is not
    /// taken as stale records. See SubTabWorthVisiting.
    /// </summary>
    private static bool? RecordsAccountForAll(GameController gc)
    {
        if (ExpeditionTabletsTotal(gc) is not { } total)
            return null;

        if (RecordedTabletsTotal() == total)
        {
            _recordsMismatchSince = null;
            return true;
        }

        _recordsMismatchSince ??= DateTime.UtcNow;
        return DateTime.UtcNow - _recordsMismatchSince >= MismatchHeldFor ? false : null;
    }

    private static DateTime? _recordsMismatchSince;

    /// <summary>How long the records and the button must disagree before the records are taken as stale. Chosen.</summary>
    private static readonly TimeSpan MismatchHeldFor = TimeSpan.FromSeconds(1);

    /// <summary>The button's count against the records', for the dump.</summary>
    private static string TabletsTotalSaid(GameController gc) =>
        $"Expedition Tablets button: {ExpeditionTabletsTotal(gc)?.ToString() ?? "unread"}; sub-tab records add up to " +
        $"{RecordedTabletsTotal()} ({string.Join(", ", Enumerable.Range(1, 6).Select(n => Book.SubTabs.TryGetValue(n, out var r) ? $"{n}: {r.Tablets}" : $"{n}: never seen"))})";
}
