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
    /// </summary>
    private static List<RememberedTablet> RememberedContents(GameController gc, int number) =>
        SelectedSubTab(gc) != number && Book.SubTabs.TryGetValue(number, out var record) && record.Contents is { } contents &&
        DateTime.UtcNow - record.When < ContentsTrustedFor
            ? contents
            : null;

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

    /// <summary>Records the sub-tab showing, when the fragment tab's Expedition tablets are up. Called on each read.</summary>
    private static void NoteSubTab(GameController gc, TabletRerollingSettings settings)
    {
        if (_stashRect.Width <= 0f || SelectedSubTab(gc) is not (> 0 and var number))
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
            record.When - before.When < TimeSpan.FromMinutes(1))
            return;

        // Worked out only as the record is written: a search's key for each tablet, not each read.
        record.Contents = contents.Select(x => new RememberedTablet
        {
            X = x.Rect.X, Y = x.Rect.Y, Width = x.Rect.Width, Height = x.Rect.Height, Uses = x.Tablet.Uses,
            Fingerprint = x.Fingerprint, Worth = WorthForWithdraw(gc, x.Tablet, settings),
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
    private static bool SubTabWorthVisiting(int number, SubTabPurpose purpose)
    {
        if (FullScanDue || !Book.SubTabs.TryGetValue(number, out var record) || DateTime.UtcNow - record.When > FullScanEvery)
            return true;

        return purpose switch
        {
            SubTabPurpose.Junk => record.Junk > 0 || number == FirstNotFull(),
            SubTabPurpose.Craftable => record.Craftable > 0 || number == FirstNotFull(),
            SubTabPurpose.Unidentified => record.Unidentified is null or > 0 || number == FirstNotFull(),
            _ => record.Tablets < SubTabHolds,
        };
    }
}
