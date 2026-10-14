using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.PoEMemory.Models;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Nodes;
using RectangleF = ExileCore2.Shared.RectangleF;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Windows.Forms;

namespace AutoExpedition;

/// <summary>
/// Picking up listed items from the ground. Two lists, one name a line (AutolootSettings): items picked up instantly,
/// with no key, and items the post-expedition pass picks up with the rest of its jobs (Placement.Chore). This class
/// holds the matching, the instant loot run and the autoloot line's listed-item half.
///
/// An item matches a line when the line is its base type's name ("Mirror of Kalandra") or, for a unique, its unique
/// name, ignoring case. Nothing partial: an instant loot entry clicks without being asked, so a loose one would click on
/// things nobody meant.
///
/// **Instant loot.** Its list may hold the action key list's ENTITY lines too, which shatter spent remnants and open
/// chests as they become clickable. One item at a time: with a party member in the area, in list order, the most
/// expensive within a line by NinjaPricer and then the nearest; alone, the nearest. See TopFirstNow. The cursor is taken, the player's mouse ignored - and by default blocked - for Mouse block time, the move
/// made fast or as a teleport, and one click sent. The cursor is taken from a placement or tablet run that holds it
/// (InstantLootPriority). Each item is tried once, by entity id, whether the click took or not,
/// for as long as the area lasts. The next item waits until the last one's label has gone, so a click is never sent
/// at a label about to move as the one before it is picked up.
/// </summary>
internal static class Autoloot
{
    /// <summary>The ExileInput2 caller name for the instant loot run. Its own, so it never shares a pointer with placement.</summary>
    private const string Caller = "AutoExpedition.Autoloot";

    /// <summary>
    /// The priority the instant loot run takes the cursor at, over every ordinary Take (0): a placement or tablet run holding
    /// it is stopped with TakenOver and says so, and is started again by pressing its key. See ExileInput2.TakeWithPriority.
    /// </summary>
    private const int InstantLootPriority = 1;

    /// <summary>Move Time Per Root Pixel for the instant loot move, in place of ExileInput2's own (6 by default): about 60ms over 1000 pixels.</summary>
    private const int FastMoveTimePerRootPixel = 2;

    /// <summary>How long a clicked item's label may stay before the next item is tried anyway: a walk to it, or a pickup that failed.</summary>
    private static readonly TimeSpan LabelGoneWithin = TimeSpan.FromSeconds(5);

    private static ExileInput2Client _input;

    /// <summary>How often the instant run looks at the ground when idle: about once a rendered frame. See Tick.</summary>
    private static readonly TimeSpan ScanEvery = TimeSpan.FromMilliseconds(15);

    private static DateTime _scannedAt = DateTime.MinValue;

    private enum Phase
    {
        Idle,
        Moving,
        WaitingForLabel,
        ConfirmingPickup,

        /// <summary>The item picked up went onto the cursor; the cursor is taking it to a free place in the inventory.</summary>
        PuttingDown,
    }

    private static Phase _phase = Phase.Idle;
    private static uint _itemId;

    /// <summary>Whether the game reported something else under the cursor during the move in hand. See StepMoving.</summary>
    private static bool _overSomethingElse;

    /// <summary>What the run clicked last - an item, a remnant or a chest - and the path to its button. See Done.</summary>
    private static Entity _target;

    private static int[] _targetPath = [];
    private static string _itemName = "";
    private static DateTime _deadline;

    /// <summary>Entity ids of items the instant loot run has tried this area, clicked or given up on. Never tried twice.</summary>
    private static readonly HashSet<uint> TriedItemIds = new();

    /// <summary>What the instant loot run last did, for the dump.</summary>
    private static string _lastOutcome = "nothing yet";

    public static void Initialise(GameController gc, string pluginDirectory)
    {
        _input = new ExileInput2Client(gc, Caller);
        _pluginDirectory = pluginDirectory ?? "";
    }

    /// <summary>Where the plugin's assembly is, with its own sounds in sounds/ beside it. See SoundFile.</summary>
    private static string _pluginDirectory = "";

    /// <summary>Entity ids of instant loot items the sound has been played for this area. See SoundForNewItems.</summary>
    private static readonly HashSet<uint> SoundedItemIds = new();

    /// <summary>
    /// The HUD's Sounds folder: beside ExileCore2.dll, which ships alert, attention, beast, bomb, corrupted, danger,
    /// elemental, physical, treasure and volatile (ExileCore2 260907). Played from here by full path rather than through
    /// ExileCore2's SoundController, whose base folder for a name was not confirmed.
    /// </summary>
    private static string SoundsFolder() =>
        Path.Combine(Path.GetDirectoryName(typeof(GameController).Assembly.Location) ?? "", "Sounds");

    /// <summary>
    /// The plugin's own sounds folder, copied beside the assembly by the build: autoloot.wav, 16-bit PCM, made from the
    /// player's autoloot.mp3 at twice its volume (2026-10-11), since SoundPlayer plays PCM .wav only.
    /// </summary>
    private static string PluginSoundsFolder() => Path.Combine(_pluginDirectory, "sounds");

    /// <summary>
    /// The sounds to choose from, by name without the extension: the plugin's own first, then the HUD's, each sorted, a
    /// name in both listed once (the plugin's is played). Read from the folders' .wav files.
    /// </summary>
    public static List<string> SoundNames()
    {
        var names = new List<string>();

        foreach (var folder in new[] { PluginSoundsFolder(), SoundsFolder() })
        {
            try
            {
                names.AddRange(Directory.GetFiles(folder, "*.wav")
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Where(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase)));
            }
            catch (Exception)
            {
                // No such folder: nothing from it.
            }
        }

        return names;
    }

    /// <summary>The file a sound name plays: the plugin's own of that name, else the HUD's, else null.</summary>
    private static string SoundFile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (var folder in new[] { PluginSoundsFolder(), SoundsFolder() })
        {
            var file = Path.Combine(folder, $"{name}.wav");

            if (File.Exists(file))
                return file;
        }

        return null;
    }

    /// <summary>
    /// Plays a sound by name at a volume in per cent, without waiting for it. Nothing when there is no such file. See
    /// SoundFile and SoundAtVolume.
    /// </summary>
    public static void PlaySound(string name, int volumePercent)
    {
        if (SoundFile(name) is not { } file || volumePercent <= 0)
            return;

        try
        {
            var bytes = SoundAtVolume(file, volumePercent);

            // Through the wave output, so a sound plays over the last rather than cutting it off. See WaveOutSound.
            if (WaveOutSound.Play(bytes))
                return;

            // The player is kept until the next sound: one dropped while its stream plays could be collected mid-sound.
            _player?.Stop();
            _player = new System.Media.SoundPlayer(new MemoryStream(bytes));
            _player.Play();
        }
        catch (Exception)
        {
            // A sound that will not play is not worth stopping anything for.
        }
    }

    private static System.Media.SoundPlayer _player;

    /// <summary>The last file scaled, kept so a sound played again is not read and scaled again.</summary>
    private static (string File, int Volume, byte[] Bytes) _scaled;

    /// <summary>
    /// A .wav file's bytes with its samples scaled to a volume in per cent, clipped at the sample's limits. SoundPlayer
    /// has no volume of its own. 16-bit and 8-bit PCM are scaled - every sound shipped, the plugin's and the HUD's, is
    /// 16-bit PCM (read 2026-10-11) - and any other format is returned as it is.
    /// </summary>
    private static byte[] SoundAtVolume(string file, int volumePercent)
    {
        if (_scaled.File == file && _scaled.Volume == volumePercent)
            return _scaled.Bytes;

        var bytes = File.ReadAllBytes(file);

        if (volumePercent != 100 && bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" &&
            Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE")
        {
            var factor = volumePercent / 100f;
            var format = 0;
            var bits = 0;

            for (var at = 12; at + 8 <= bytes.Length;)
            {
                var id = Encoding.ASCII.GetString(bytes, at, 4);
                var size = BitConverter.ToInt32(bytes, at + 4);
                var start = at + 8;

                if (size < 0 || start + size > bytes.Length)
                    break;

                if (id == "fmt " && size >= 16)
                {
                    format = BitConverter.ToUInt16(bytes, start);
                    bits = BitConverter.ToUInt16(bytes, start + 14);
                }
                else if (id == "data" && format == 1 && bits == 16)
                {
                    for (var i = start; i + 1 < start + size; i += 2)
                    {
                        var sample = (int)Math.Round(BitConverter.ToInt16(bytes, i) * factor);
                        var clipped = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);

                        bytes[i] = (byte)(clipped & 0xFF);
                        bytes[i + 1] = (byte)((clipped >> 8) & 0xFF);
                    }
                }
                else if (id == "data" && format == 1 && bits == 8)
                {
                    // 8-bit PCM is unsigned, silence at 128.
                    for (var i = start; i < start + size; i++)
                        bytes[i] = (byte)Math.Clamp((int)Math.Round((bytes[i] - 128) * factor) + 128, 0, 255);
                }

                at = start + size + (size & 1);
            }
        }

        _scaled = (file, volumePercent, bytes);

        return bytes;
    }

    /// <summary>
    /// Plays Instant loot sound once for each instant loot item not yet sounded this area, one sound however many arrive
    /// together. See AutolootSettings.PlayInstantLootSound.
    /// </summary>
    private static void SoundForNewItems(GameController gc, AutoExpeditionSettings settings,
        List<(Entity Item, LabelOnGround Label, int Rank, string Name)> listed)
    {
        if (!settings.Automation.Enable.Value || !settings.Automation.Autoloot.EnableInstantAutoloot ||
            !settings.Automation.Autoloot.PlayInstantLootSound)
            return;

        // Silent whenever instant loot is, alone with Disable instant autoloot when no party members are in the area on. See
        // DisableInstantAutolootWithoutParty.
        if (settings.Automation.Autoloot.DisableInstantAutolootWithoutParty.Value && !PartyInArea(gc))
            return;

        var fresh = false;

        foreach (var (item, _, _, _) in listed)
            fresh |= SoundedItemIds.Add(Safe.Read(item, static e => e.Id, 0u));

        if (fresh)
            PlaySound(settings.Automation.Autoloot.InstantLootSound.Value, settings.Automation.Autoloot.InstantLootSoundVolume.Value);
    }

    /// <summary>A new area: no item here has been tried or sounded, and anything in flight is dropped.</summary>
    public static void AreaChange()
    {
        TriedItemIds.Clear();
        RetriesOfItems.Clear();
        CoveredUntil.Clear();
        SoundedItemIds.Clear();
        PricesOfItems.Clear();
        UnpricedUntil.Clear();
        FactsOfItems.Clear();
        RanksOfItems.Clear();
        Finish();
    }

    /// <summary>
    /// The lines of a list in order, trimmed, blank ones dropped, by name ignoring case to their position. Parsed again
    /// only when the text changes.
    /// </summary>
    private static Dictionary<string, int> RanksOf(TextNode list)
    {
        var text = list?.Value ?? "";

        if (!ParsedLists.TryGetValue(list ?? new TextNode(), out var parsed) || parsed.Text != text)
        {
            var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in text.Split(new[] { '\n', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                ranks.TryAdd(line, ranks.Count);

            parsed = (text, ranks);

            if (list != null)
                ParsedLists[list] = parsed;
        }

        return parsed.Ranks;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<TextNode, (string Text, Dictionary<string, int> Ranks)> ParsedLists = new();

    /// <summary>
    /// A line of the action key list standing for the post-expedition pass's shattering of spent remnants, so the list's
    /// order can put it before or after items. "ENTITY:" because no item the game labels is named so.
    /// </summary>
    public const string RemnantsLine = "ENTITY:Remnants";

    /// <summary>A line of the action key list standing for opening excavated chests. See RemnantsLine.</summary>
    public const string ExcavatedChestsLine = "ENTITY:ExcavatedChests";

    /// <summary>
    /// A list line of conditions, all of which an item must meet, separated by spaces: CLASS:&lt;item class&gt;, the
    /// game's BaseItemType.ClassName, whole and ignoring case; METADATA:&lt;path start&gt;, the start of the item's
    /// metadata path, ignoring case; MIN:&lt;amount&gt;&lt;d|c|ex&gt;, NinjaPricer's price for the item and its stack
    /// at least that (see PriceOfItem), a bare number being exalts. A MIN may stand alone, taking any item of any kind
    /// priced that high; NinjaPricer prices no magic or rare gear, so a MIN never takes one. ART:&lt;art name&gt;, the
    /// item's art file name without its folder or extension, whole and ignoring case: a unique's art names that unique
    /// whether identified or not, where its unique name reads blank until it is (a Whisper of the Brotherhood read both
    /// ways, dumps of 2026-10-11 19:22:54 and 19:23:03). In d or c a MIN meets nothing while that rate is unknown,
    /// rather than reading 1d as 1ex. A line
    /// with none of these words is an item's name. Classes read in dumps of 2026-10-11 15:23:50: StackableCurrency
    /// (orbs, fluxes, Verisium, alloys, expedition artifacts), Omen, Expedition2Logbooks, UncutSkillGemStackable,
    /// UncutReservationGemStackable.
    /// </summary>
    private sealed record ConditionLine(int Rank, string Line, string ItemClass, string MetadataStart, string ArtName, double Amount,
        string Unit, string Unreadable);

    /// <summary>The prices, set by the plugin. See ConditionLine.</summary>
    internal static Valuation Valuation { get; set; }

    /// <summary>NinjaPricer prices of items on the ground this area, in exalts for the stack, by entity id. See PriceOfItem.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, double> PricesOfItems = new();

    /// <summary>The words a condition line is made of. See ConditionLine.</summary>
    private static readonly string[] ConditionWords = ["CLASS:", "METADATA:", "ART:", "MIN:"];

    /// <summary>Whether a list line is a condition line rather than a name: any of its words starts with a condition word.</summary>
    private static bool IsConditionLine(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(word => ConditionWords.Any(c => word.StartsWith(c, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A list's condition lines, read from its ranks, each with why it cannot be used if it cannot; read once per ranks,
    /// which RanksOf makes once per list text. See ConditionLine.
    /// </summary>
    private static List<ConditionLine> ConditionLinesOf(Dictionary<string, int> ranks)
    {
        if (ConditionLinesOfRanks.TryGetValue(ranks, out var read))
            return read;

        read = ReadConditionLines(ranks);
        ConditionLinesOfRanks.AddOrUpdate(ranks, read);

        return read;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dictionary<string, int>, List<ConditionLine>>
        ConditionLinesOfRanks = new();

    private static List<ConditionLine> ReadConditionLines(Dictionary<string, int> ranks)
    {
        var lines = new List<ConditionLine>();

        foreach (var (line, rank) in ranks)
        {
            if (!IsConditionLine(line))
                continue;

            string itemClass = null, metadataStart = null, artName = null, unit = null, unreadable = null;
            var amount = 0d;

            foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.StartsWith("CLASS:", StringComparison.OrdinalIgnoreCase))
                    itemClass = word["CLASS:".Length..];
                else if (word.StartsWith("METADATA:", StringComparison.OrdinalIgnoreCase))
                    metadataStart = word["METADATA:".Length..];
                else if (word.StartsWith("ART:", StringComparison.OrdinalIgnoreCase))
                    artName = word["ART:".Length..];
                else if (word.StartsWith("MIN:", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(word["MIN:".Length..],
                        @"^(\d+(?:\.\d+)?)(d|c|ex)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (!match.Success || !double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out amount))
                        unreadable ??= $"{word} is not a number with d, c or ex";

                    unit = match.Groups[2].Value.ToLowerInvariant() switch
                    {
                        "d" => "Divine",
                        "c" => "Chaos",
                        _ => "Exalted",
                    };
                }
                else
                    unreadable ??= $"{word} is not CLASS:, METADATA:, ART: or MIN:";
            }

            lines.Add(new ConditionLine(rank, line, itemClass, metadataStart, artName, amount, unit, unreadable));
        }

        return lines;
    }

    /// <summary>A MIN's amount in exalts now, or null while its unit's rate is unknown. See ConditionLine.</summary>
    private static double? ExaltsOfMin(ConditionLine line) =>
        line.Unit == null ? 0d : Valuation != null && Valuation.Converts(line.Unit) ? line.Amount * Valuation.PerExalt(line.Unit) : null;

    /// <summary>Whether an item on the ground meets every condition of a line. See ConditionLine.</summary>
    private static bool MeetsConditions(GameController gc, Entity onGround, ConditionLine line)
    {
        if (line.Unreadable != null)
            return false;

        if (line.ItemClass != null &&
            !string.Equals(ItemClassOfItem(gc, onGround), line.ItemClass, StringComparison.OrdinalIgnoreCase))
            return false;

        if (line.MetadataStart != null &&
            !MetadataOfItem(onGround).StartsWith(line.MetadataStart, StringComparison.OrdinalIgnoreCase))
            return false;

        if (line.ArtName != null &&
            !string.Equals(FactsOfItem(null, onGround)?.ArtName ?? "", line.ArtName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (line.Unit == null)
            return true;

        var price = PriceOfItem(gc, onGround);

        return ExaltsOfMin(line) is { } least && price > 0d && price >= least;
    }

    /// <summary>The game's item class of an item on the ground, from its base type, or "" when it cannot be read.</summary>
    private static string ItemClassOfItem(GameController gc, Entity onGround) => FactsOfItem(gc, onGround)?.ItemClass ?? "";

    /// <summary>The metadata path of an item on the ground - the item's, not the ground entity's - or "".</summary>
    private static string MetadataOfItem(Entity onGround) => FactsOfItem(null, onGround)?.Metadata ?? "";

    /// <summary>
    /// What NinjaPricer says an item on the ground is worth, in exalts for its whole stack: Valuation.ExaltsOfItem, which
    /// prices uniques by name and tablets by rarity. 0 when it cannot say, as for magic and rare gear. Kept for the area once above 0, as an item on the ground does not change.
    /// </summary>
    private static double PriceOfItem(GameController gc, Entity onGround)
    {
        var id = Safe.Read(onGround, static e => e.Id, 0u);

        if (PricesOfItems.TryGetValue(id, out var known))
            return known;

        // No price yet - NinjaPricer still loading, or none for this type - is asked again every few seconds rather than
        // on every pass.
        if (UnpricedUntil.TryGetValue(id, out var until) && DateTime.UtcNow < until)
            return 0d;

        var item = Safe.Read(onGround, static e => e.GetComponent<WorldItem>()?.ItemEntity, null);
        var price = Valuation?.ExaltsOfItem(item) ?? 0d;

        if (id != 0u)
        {
            if (price > 0d)
                PricesOfItems[id] = price;
            else
                UnpricedUntil[id] = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        }

        return price;
    }

    /// <summary>When an item NinjaPricer had no price for is next asked about, by entity id. See PriceOfItem.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, DateTime> UnpricedUntil = new();

    /// <summary>
    /// What NinjaPricer says an item on the ground is worth, in exalts for the stack, 0 when it cannot say: for ordering
    /// items that share a list line, most expensive first. See PriceOfItem.
    /// </summary>
    internal static double ExaltsOfItem(GameController gc, Entity onGround) => PriceOfItem(gc, onGround);

    /// <summary>
    /// How near the player an item may be and be picked up without walking, in grid: an item that close counts as at the
    /// player's feet when ordering, so nearer ones do not win over it on distance alone. Measured: of 19 pickups after a
    /// click, the three that walked first ended 23.2, 23.5 and 23.8 grid from the item, and the other 16 were clicked from
    /// 1.4 to 9.2 (dump 2026-10-11 15:37:16). 23 sits just under the walked ones. Each pickup after a click is measured
    /// into the dump; see ObservePickups.
    /// </summary>
    internal const float PickupReachGrid = 23f;

    /// <summary>The walk to an item that far away: none within PickupReachGrid, otherwise the distance past it.</summary>
    internal static float WalkToItem(float away) => Math.Max(0f, away - PickupReachGrid);

    /// <summary>
    /// Whether another party member is in this area, by either test: a party portrait without the portal icon, or
    /// another player in the entity list. Read every second at most; both shown in the dump.
    ///
    /// A portrait holds the portal icon (PartyPortalIcon.dds, not drawn until hovered) while its member is in another
    /// area and not while they are in this one: present in two dumps with the member elsewhere and absent in one with
    /// them here (2026-10-11 15:13:14, 15:14:26 and 15:14:33). The member's zone name, which looked like the answer,
    /// reads blank either way on this build, as does the name. The entity list finds a member near the player; the
    /// portrait finds one anywhere in the area.
    /// </summary>
    internal static bool PartyInArea(GameController gc)
    {
        if (DateTime.UtcNow - _partyReadAt < TimeSpan.FromSeconds(1))
            return _partyInArea;

        _partyReadAt = DateTime.UtcNow;

        var area = Safe.Read(gc, static g => g.Area.CurrentArea.DisplayName, "") ?? "";
        var members = Safe.Read(gc, static g => g.IngameState.IngameUi.PartyElement.PlayerElements, null);
        var here = 0;
        var elsewhere = 0;

        foreach (var member in members ?? [])
        {
            if (HoldsPortalIcon(member))
                elsewhere++;
            else
                here++;
        }

        var players = Safe.Read(gc, static g => g.EntityListWrapper.ValidEntitiesByType.TryGetValue(
            ExileCore2.Shared.Enums.EntityType.Player, out var of) ? of.Count : 0, 0);
        var others = Math.Max(0, players - 1);

        _partyInArea = here > 0 || others > 0;
        _partySaid = $"area \"{area}\"; party panel: {here} member(s) here, {elsewhere} elsewhere (by the portal icon); " +
                     $"{others} other player(s) in the entity list; party in area: {_partyInArea}";

        return _partyInArea;
    }

    /// <summary>Whether a party portrait holds the portal icon, a few levels down: its member is in another area. See PartyInArea.</summary>
    private static bool HoldsPortalIcon(ExileCore2.PoEMemory.Element portrait)
    {
        var stack = new Stack<(ExileCore2.PoEMemory.Element, int)>();

        stack.Push((portrait, 0));

        while (stack.Count > 0)
        {
            var (at, depth) = stack.Pop();

            if ((Safe.Read(at, static e => e.TextureName, "") ?? "").Contains("PartyPortalIcon", StringComparison.OrdinalIgnoreCase))
                return true;

            if (depth < 4)
                foreach (var child in Safe.Read(at, static e => e.Children, null) ?? [])
                    stack.Push((child, depth + 1));
        }

        return false;
    }

    private static DateTime _partyReadAt = DateTime.MinValue;
    private static bool _partyInArea;
    private static string _partySaid = "not read yet";

    /// <summary>
    /// Whether both lists are taken top first now - higher lines, then the more expensive, then the nearer - rather than
    /// nearest first. Top first with another party member in this area, racing for the same loot; nearest first alone,
    /// where nothing can be lost and the shortest path round is best. Was a dropdown per list, removed 2026-10-11 as
    /// this was the only choice worth making. See Beats.
    /// </summary>
    internal static bool TopFirstNow(GameController gc) => PartyInArea(gc);

    /// <summary>Items clicked and still on the ground, by id, with where each was last seen. See ObservePickups.</summary>
    private static readonly Dictionary<uint, (Entity Item, Vector2 Grid, DateTime Clicked)> ClickedItems = new();

    /// <summary>The last pickups measured: how far the player was from the item when its label went, in grid.</summary>
    private static readonly List<(string Name, float Away, DateTime At)> PickupsMeasured = new();

    /// <summary>An item has been clicked to pick up, by the instant run or the post-expedition pass. See ObservePickups.</summary>
    internal static void NoteClicked(GameController gc, Entity item)
    {
        var id = Safe.Read(item, static e => e.Id, 0u);

        if (id != 0u)
            ClickedItems[id] = (item, Safe.Read(item, static e => e.GridPos, Vector2.Zero), DateTime.UtcNow);

        var metadata = Safe.Read(item, static e => e.GetComponent<WorldItem>()?.ItemEntity?.Metadata, null);

        if (!string.IsNullOrEmpty(metadata))
            ClickedMetadata[metadata] = DateTime.UtcNow;
    }

    /// <summary>When an item of each metadata was last clicked on the ground, by either list. See PickedUpOntoCursor.</summary>
    private static readonly Dictionary<string, DateTime> ClickedMetadata = new(StringComparer.Ordinal);

    /// <summary>How long after its click an item held on the cursor is taken to be the one clicked. Chosen.</summary>
    private static readonly TimeSpan HeldAfterClickWithin = TimeSpan.FromSeconds(10);

    /// <summary>Whether the inventory panel is showing.</summary>
    internal static bool InventoryPanelOpen(GameController gc) =>
        Safe.Read(gc, static g => g.IngameState.IngameUi.InventoryPanel.IsVisible, false);

    /// <summary>
    /// An item a pickup put on the cursor rather than in the inventory, or null. With the inventory panel open, clicking
    /// an item on the ground picks it up onto the cursor (seen in game, 2026-10-14), and the next click would carry it
    /// off. Only an item of the metadata of one clicked within HeldAfterClickWithin counts, so an item the player picked
    /// up by hand is left alone.
    /// </summary>
    internal static Entity PickedUpOntoCursor(GameController gc)
    {
        if (ClickedMetadata.Count == 0 || !InventoryPanelOpen(gc))
            return null;

        var held = Tablets.ItemOnCursor(gc, true);
        var metadata = Safe.Read(held, static e => e.Metadata, null);

        return metadata != null && ClickedMetadata.TryGetValue(metadata, out var at) && DateTime.UtcNow - at <= HeldAfterClickWithin
            ? held
            : null;
    }

    /// <summary>
    /// Where in the inventory an item held on the cursor goes down, on screen: for a stackable one, a carried stack of
    /// the same item with room for all of it, else a free place its size; null when the panel is not showing or neither
    /// is there. Never a stack it would only top up, which would leave the rest held and need a second place. See
    /// InventorySpace.StackWithRoomOnScreen and FreePlaceOnScreen.
    /// </summary>
    internal static RectangleF? PutDownPlace(GameController gc, Entity held)
    {
        if (Safe.Read(held, static e => e.GetComponent<Stack>()?.Info?.MaxStackSize ?? 0, 0) > 1 &&
            InventorySpace.StackWithRoomOnScreen(gc, Safe.Read(held, static e => e.Metadata, null),
                Math.Max(1, Safe.Read(held, static e => e.GetComponent<Stack>()?.Size ?? 1, 1))) is { } stack)
            return stack;

        var wide = Math.Max(1, Safe.Read(held, static e => e.GetComponent<Base>()?.ItemCellsSizeX ?? 1, 1));
        var high = Math.Max(1, Safe.Read(held, static e => e.GetComponent<Base>()?.ItemCellsSizeY ?? 1, 1));

        return InventorySpace.FreePlaceOnScreen(gc, wide, high);
    }

    /// <summary>A held item's name for messages: its base name, or "the item".</summary>
    internal static string HeldName(Entity held) =>
        Safe.Read(held, static e => e.GetComponent<Base>()?.Name, null) is { Length: > 0 } name ? name : "the item";

    /// <summary>
    /// Measures pickup reach: for each clicked item, where it was last seen, and when its label goes within 10 seconds of
    /// the click, how far the player then stands from it. The last 20 go in the dump. See PickupReachGrid.
    /// </summary>
    public static void ObservePickups(GameController gc)
    {
        if (ClickedItems.Count == 0)
            return;

        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        foreach (var (id, clicked) in ClickedItems.ToList())
        {
            if (DateTime.UtcNow - clicked.Clicked > TimeSpan.FromSeconds(10))
            {
                ClickedItems.Remove(id);
                continue;
            }

            if (Placement.Button(gc, clicked.Item, []).Rect.Width > 0f)
            {
                ClickedItems[id] = clicked with { Grid = Safe.Read(clicked.Item, static e => e.GridPos, clicked.Grid) };
                continue;
            }

            ClickedItems.Remove(id);

            if (player == Vector2.Zero || clicked.Grid == Vector2.Zero)
                continue;

            PickupsMeasured.Add((NamesOfItem(clicked.Item).BaseName, Vector2.Distance(player, clicked.Grid), DateTime.UtcNow));

            if (PickupsMeasured.Count > 20)
                PickupsMeasured.RemoveAt(0);
        }
    }

    /// <summary>
    /// Whether a candidate beats the best so far. Top first: a higher line, then on one line the more expensive, then the
    /// shorter walk, then the label nearer the cursor. Nearest first: the shorter walk, then the label nearer the cursor -
    /// so among items within pickup reach, which all walk 0 (WalkToItem), the cursor's nearest goes first, and only when
    /// none is in reach does distance decide. Shared by the instant run and the post-expedition pass.
    /// </summary>
    internal static bool Beats(bool topFirst, int rank, double price, float away, float fromCursor,
        int bestRank, double bestPrice, float bestAway, float bestFromCursor) =>
        topFirst && rank != bestRank ? rank < bestRank :
        topFirst && price != bestPrice ? price > bestPrice :
        away != bestAway ? away < bestAway :
        fromCursor < bestFromCursor;

    /// <summary>The cursor's position on screen, or the screen's corner when it cannot be read. See Beats.</summary>
    internal static Vector2 CursorOnScreen(GameController gc) =>
        Safe.Read(gc, static g => new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), Vector2.Zero);

    /// <summary>The position of a line in a list, ignoring case, or int.MaxValue when the list has no such line.</summary>
    public static int RankOfLine(TextNode list, string line) => RanksOf(list).TryGetValue(line, out var rank) ? rank : int.MaxValue;

    /// <summary>
    /// The base type's name of an item on the ground, and its unique name when it is a unique, else "". An uncut gem's
    /// level is part of its base name - "Uncut Spirit Gem (Level 19)" - read in the dump of 2026-10-11 15:23:50.
    /// </summary>
    private static (string BaseName, string UniqueName) NamesOfItem(Entity onGround) =>
        FactsOfItem(null, onGround) is { } facts ? (facts.BaseName, facts.UniqueName) : ("", "");

    /// <summary>
    /// What is read once about an item on the ground and kept for the area, since none of it changes while the item lies
    /// there: its names, class, metadata, base type and art. Read again each call until its metadata reads. See
    /// FactsOfItem.
    /// </summary>
    private sealed record ItemFacts(string BaseName, string UniqueName, string ItemClass, string Metadata,
        BaseItemType Type, string ArtName);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ItemFacts> FactsOfItems = new();

    /// <summary>
    /// An item's art file name without its folder or extension - "WhisperOfTheBrotherhood" for
    /// Art/2DItems/Rings/Uniques/WhisperOfTheBrotherhood.dds - from the item itself, not its ground entity; "" when it
    /// cannot be read. See ConditionLine.
    /// </summary>
    internal static string ArtNameOfItem(Entity item) =>
        Path.GetFileNameWithoutExtension(Safe.Read(item, static e => e.GetComponent<RenderItem>()?.ResourcePath, "") ?? "");

    /// <summary>An item's facts, read once and kept by entity id for the area, or null with no item. See ItemFacts.</summary>
    private static ItemFacts FactsOfItem(GameController gc, Entity onGround)
    {
        var id = Safe.Read(onGround, static e => e.Id, 0u);

        if (id != 0u && FactsOfItems.TryGetValue(id, out var known) && (known.Type != null || gc == null))
            return known;

        var item = Safe.Read(onGround, static e => e.GetComponent<WorldItem>()?.ItemEntity, null);

        if (item == null)
            return null;

        var metadata = Safe.Read(item, static e => e.Metadata, "") ?? "";
        var mods = Safe.Read(item, static e => e.GetComponent<Mods>(), null);
        var type = gc == null || metadata.Length == 0 ? null : Safe.Read(() => gc.Files.BaseItemTypes.Translate(metadata), null);
        var facts = new ItemFacts(
            Safe.Read(item, static e => e.GetComponent<Base>()?.Name, "") ?? "",
            Safe.Read(mods, static m => m.ItemRarity == ItemRarity.Unique ? m.UniqueName : "", "") ?? "",
            Safe.Read(() => type?.ClassName, "") ?? "",
            metadata,
            type,
            ArtNameOfItem(item));

        if (id != 0u && metadata.Length > 0)
            FactsOfItems[id] = facts;

        return facts;
    }

    /// <summary>
    /// The highest list line an item matches, or -1 when it matches none, with what to call it: by its unique or base
    /// name, or by a condition line it meets. See ConditionLine.
    /// </summary>
    private static (int Rank, string Name) RankOfItem(GameController gc, Entity onGround, Dictionary<string, int> ranks)
    {
        if (ranks.Count == 0)
            return (-1, "");

        // **Kept, as an item on the ground does not change.** The visible items are looked at every 10ms, and each look
        // worked every item's rank out again against every line: the listing was about 0.3ms and 220KB a frame each in
        // Tick and Render while looting (dump 2026-10-12 00:49:50). A rank is kept for the area against the same parsed
        // list; no match, or a match before the item's facts read, only for RankKeptUnmatched, as a price or a late read
        // can still change it.
        var id = Safe.Read(onGround, static e => e.Id, 0u);
        var kept = RanksOfItems.GetOrCreateValue(ranks);

        lock (kept)
        {
            if (id != 0u && kept.TryGetValue(id, out var known) && DateTime.UtcNow < known.Until)
                return (known.Rank, known.Name);
        }

        var ranked = RankOfItemUnkept(gc, onGround, ranks);

        if (id != 0u)
        {
            var whole = ranked.Rank >= 0 && FactsOfItem(null, onGround)?.Metadata is { Length: > 0 };

            lock (kept)
                kept[id] = (ranked.Rank, ranked.Name, whole ? DateTime.MaxValue : DateTime.UtcNow + RankKeptUnmatched);
        }

        return ranked;
    }

    /// <summary>Each item's rank by entity id, for each parsed list. See RankOfItem.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dictionary<string, int>,
        Dictionary<uint, (int Rank, string Name, DateTime Until)>> RanksOfItems = new();

    /// <summary>How long a rank that matched nothing is kept before it is worked out again. Chosen.</summary>
    private static readonly TimeSpan RankKeptUnmatched = TimeSpan.FromSeconds(1);

    private static (int Rank, string Name) RankOfItemUnkept(GameController gc, Entity onGround, Dictionary<string, int> ranks)
    {

        var (baseName, unique) = NamesOfItem(onGround);
        var best = (Rank: -1, Name: "");

        foreach (var name in new[] { unique, baseName })
        {
            if (name.Length > 0 && ranks.TryGetValue(name, out var rank) && (best.Rank < 0 || rank < best.Rank))
                best = (rank, name);
        }

        foreach (var line in ConditionLinesOf(ranks))
        {
            if ((best.Rank < 0 || line.Rank < best.Rank) && MeetsConditions(gc, onGround, line))
                best = (line.Rank, baseName);
        }

        return best;
    }

    /// <summary>Every visible ground label whose item is on this list, with its rank and the name it matched.</summary>
    internal static List<(Entity Item, LabelOnGround Label, int Rank, string Name)> ListedOnGround(GameController gc, TextNode list)
    {
        // Once per read of the labels for each list: the instant run, the line and the dump all ask in one frame.
        Ground.Labels(gc);

        if (list != null && Ground.OnOwnerThread && ListedThisRead.TryGetValue(list, out var cached) &&
            cached.Read == Ground.ViewNumber && cached.Text == list.Value)
            return cached.Found;

        var found = ReadListedOnGround(gc, list);

        if (list != null && Ground.OnOwnerThread)
            ListedThisRead[list] = (Ground.ViewNumber, list.Value, found);

        return found;
    }

    /// <summary>Each list's matches for the shared look at the labels, Tick and Render alike. See Ground.ViewNumber.</summary>
    private static readonly Dictionary<TextNode, (long Read, string Text, List<(Entity Item, LabelOnGround Label, int Rank, string Name)> Found)>
        ListedThisRead = new();

    private static List<(Entity Item, LabelOnGround Label, int Rank, string Name)> ReadListedOnGround(GameController gc, TextNode list)
    {
        var found = new List<(Entity, LabelOnGround, int, string)>();
        var ranks = RanksOf(list);

        if (ranks.Count == 0)
            return found;

        // The visible items, found once per read for every list. See Ground.VisibleItems. Timed apart from the ranking,
        // to tell the two apart in "where a frame goes".
        IEnumerable<(LabelOnGround, Entity)> visible;

        using (Spent.On("Autoloot/VisibleItems"))
            visible = Ground.VisibleItems(gc);

        using var ranking = Spent.On("Autoloot/Ranking");

        foreach (var (label, item) in visible)
        {
            // Not one another player has, which cannot be picked up: clicked, the character walked to it for nothing.
            // See AllocatedToSomeoneElse.
            if (AllocatedToSomeoneElse(label, item))
                continue;

            var (rank, name) = RankOfItem(gc, item, ranks);

            if (rank >= 0)
                found.Add((item, label, rank, name));
        }

        return found;
    }

    /// <summary>
    /// Whether an item on the ground is held for another player and cannot be picked up, by ExileCore2's
    /// WorldItem.AllocatedToSomeoneElse. Some items drop for one player, and some party modes allocate every drop
    /// (permanent allocation); the game draws such a label faded and the click does nothing.
    ///
    /// **Not the label's CanPickUp**, which this asked as well and which reads True on such an item: a Twilight Reliquary
    /// Key bound to another player read allocated to someone else True, permanently True, to player 3919706517, can pick
    /// up True, against False and 0 on two free items beside it (dump 2026-10-12 16:08:52). The dump lists every reading
    /// for the items in view. See AllocationSaid.
    /// </summary>
    internal static bool AllocatedToSomeoneElse(LabelOnGround label, Entity item) =>
        Safe.Read(item, static e => e.GetComponent<WorldItem>()?.AllocatedToSomeoneElse ?? false, false);

    /// <summary>
    /// For the dump: each item in view, up to AllocationShown, with what ExileCore2 reads of who may pick it up. See
    /// AllocatedToSomeoneElse.
    /// </summary>
    private static IEnumerable<string> AllocationSaid(GameController gc)
    {
        foreach (var (label, item) in Ground.VisibleItems(gc).Take(AllocationShown))
        {
            var world = Safe.Read(item, static e => e.GetComponent<WorldItem>(), null);
            var name = NamesOfItem(item).BaseName;

            yield return $"{(name.Length > 0 ? name : "?")}: can pick up {Safe.Read(label, static l => l.CanPickUp, false)}, " +
                         $"time for pick-up {Safe.Read(label, static l => l.MaxTimeForPickUp.ToString(), "?")}, " +
                         $"allocated to someone else {Safe.Read(world, static w => w.AllocatedToSomeoneElse, false)}, " +
                         $"permanently {Safe.Read(world, static w => w.IsPermanentlyAllocated, false)}, " +
                         $"to player {Safe.Read(world, static w => w.AllocatedToPlayer.ToString(), "?")}, " +
                         $"to other time {Safe.Read(world, static w => w.AllocatedToOtherTime.ToString(), "?")}" +
                         (AllocatedToSomeoneElse(label, item) ? " - LEFT ALONE" : "");
        }
    }

    /// <summary>How many items in view the dump reads allocation for.</summary>
    private const int AllocationShown = 12;

    /// <summary>
    /// Whether the last click has done its work: the item's label or the remnant's shatter button gone, or the chest
    /// open - what the post-expedition pass waits for as well. See Placement's Shattered step.
    /// </summary>
    private static bool Done(GameController gc) =>
        _target == null ||
        Safe.Read(_target, static e => e.GetComponent<Chest>()?.IsOpened ?? false, false) ||
        Placement.Button(gc, _target, _targetPath).Rect.Width <= 0f;

    /// <summary>
    /// Advances the instant loot run by one tick. Starts nothing with Enable automation or Enable Instant Autoloot off, with the list empty, or while
    /// another caller holds the cursor.
    /// </summary>
    public static void Tick(GameController gc, AutoExpeditionSettings settings, Scan scan)
    {
        var autoloot = settings.Automation.Autoloot;

        // **A look at the ground every ScanEvery, not every Tick.** Tick ran about eleven times a rendered frame, each
        // reading every ground label - 498 in the list, 32 on screen - for 18ms and 15MB a call (dump 2026-10-11
        // 15:58:49). A move or a wait in hand still steps every Tick.
        if (_phase == Phase.Idle && DateTime.UtcNow - _scannedAt < ScanEvery)
            return;

        if (_phase == Phase.Idle)
            _scannedAt = DateTime.UtcNow;

        List<(Entity Item, LabelOnGround Label, int Rank, string Name)> listed;

        using (Spent.On("Tick/Autoloot/Listed"))
            listed = _phase == Phase.Idle ? ListedOnGround(gc, autoloot.InstantLootItems) : [];

        SoundForNewItems(gc, settings, listed);

        // The action key pressed during the block: ExileInput2 has already ended the key and mouse blocks, and the
        // pickup stops here, before its click if it had not been sent. See AutolootSettings.ActionKeyEndsInputBlock.
        if (_phase != Phase.Idle && _keysBlocked && _input?.KeyBlockCancelled() == true)
        {
            _keysBlocked = false;
            _lastOutcome = $"{_itemName}: cancelled by the action key";
            Finish();

            return;
        }

        switch (_phase)
        {
            case Phase.Moving:
                StepMoving(gc);
                return;

            case Phase.ConfirmingPickup:
                ConfirmPickup(gc, settings);

                return;

            case Phase.PuttingDown:
                StepPuttingDown(gc, settings);

                return;

            case Phase.WaitingForLabel:
                // The item on the cursor, the inventory being open, is the pickup done: its label can linger a moment.
                if (Done(gc) || PickedUpOntoCursor(gc) != null)
                {
                    EndKeyBlock();

                    // The label gone is not the item carried: a party member's pickup takes it the same way. See
                    // ConfirmPickup.
                    _deadline = DateTime.UtcNow + InventoryShowsWithin;
                    _phase = Phase.ConfirmingPickup;
                    ConfirmPickup(gc, settings);

                    return;
                }

                if (DateTime.UtcNow >= _deadline)
                {
                    EndKeyBlock();
                    _phase = Phase.Idle;
                    _lastOutcome += "; still on the ground when the pickup gave up";

                    return;
                }

                // The walk's start, measured for WalkStartsWithin.
                var moving = MovingNow(gc);

                if (_walkExpected && !_walkSeen && moving)
                {
                    _walkSeen = true;
                    WalkStartsMeasured.Add((int)(DateTime.UtcNow - _clickedAt).TotalMilliseconds);

                    if (WalkStartsMeasured.Count > 20)
                        WalkStartsMeasured.RemoveAt(0);
                }

                // **A walk expected and not begun lets the keyboard go.** An item out of pickup reach is walked to; a
                // character still standing WalkStartsWithin after the click is not going to, and the block would only hold
                // the player still for nothing. See KeyBlockMs.
                if (_keysBlocked && _walkExpected && !_walkSeen && DateTime.UtcNow - _clickedAt >= WalkStartsWithin && !moving)
                {
                    EndKeyBlock();
                    _lastOutcome += "; no walk began, so the keyboard was let go";
                }
                // **A walk cut short, clicked again**, as Instant loot retries allows: the label still there and the
                // character standing - which a click needs a moment to change, so not straight after it. Kept under the
                // key block, so the retry's walk is not cut short the same way. See InstantLootRetries.
                else if (DateTime.UtcNow - _clickedAt >= RetryAfterStandingMs && !moving &&
                         RetriesOfItems.GetValueOrDefault(_itemId) < settings.Automation.Autoloot.InstantLootRetries)
                {
                    RetriesOfItems[_itemId] = RetriesOfItems.GetValueOrDefault(_itemId) + 1;
                    TriedItemIds.Remove(_itemId);
                    _lastOutcome = $"{_itemName}: still there with the character standing - clicked again";
                    _scannedAt = DateTime.MinValue;
                    _phase = Phase.Idle;
                }

                return;
        }

        if (!settings.Automation.Enable.Value || !autoloot.EnableInstantAutoloot || _input is not { Available: true })
            return;

        // Alone there is no race for an item: it is left for the action key. See DisableInstantAutolootWithoutParty.
        if (autoloot.DisableInstantAutolootWithoutParty.Value && !PartyInArea(gc))
            return;

        // Nothing on the ground is clicked with the inventory open and an item on the cursor: the click would carry the
        // held item off. See PickedUpOntoCursor.
        if (InventoryPanelOpen(gc) && Tablets.ItemOnCursor(gc, true) != null)
            return;

        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var covered = Panels.Covered(gc);
        var topFirst = TopFirstNow(gc);
        var cursor = CursorOnScreen(gc);
        (Entity Of, int[] Path, uint Id, string Name, Vector2 At, int Rank, float Away, double Price, float FromCursor)? best = null;

        void Consider(Entity of, int[] path, int rank, string name)
        {
            var id = Safe.Read(of, static e => e.Id, 0u);

            if (id == 0u || TriedItemIds.Contains(id) ||
                CoveredUntil.TryGetValue(id, out var lookAgain) && DateTime.UtcNow < lookAgain)
                return;

            // Not one with no room in the inventory, slots kept for it included. Not tried, so it is taken up once there
            // is room. See InventorySpace.
            if (path.Length == 0 && !InventorySpace.Fits(gc, of, 0))
                return;

            var away = Vector2.Distance(player, Safe.Read(of, static e => e.GridPos, Vector2.Zero));
            var price = path.Length == 0 ? PriceOfItem(gc, of) : 0d;

            // An item within reach needs no walk. See PickupReachGrid.
            if (path.Length == 0)
                away = WalkToItem(away);

            // One that cannot win even at the cursor is not looked at further: the clear-spot search is the dear part.
            if (best is { } leader &&
                !Beats(topFirst, rank, price, away, 0f, leader.Rank, leader.Price, leader.Away, leader.FromCursor))
                return;

            var rect = Placement.Button(gc, of, path).Rect;
            var at = Placement.Exposed(gc, covered, rect, id).At;

            // Not clickable now - off screen, behind a panel or under other labels. Not tried, so it is taken up when
            // it can be.
            if (at == Vector2.Zero)
                return;

            var fromCursor = Vector2.Distance(cursor, at);

            // See Beats.
            if (best is { } held &&
                !Beats(topFirst, rank, price, away, fromCursor, held.Rank, held.Price, held.Away, held.FromCursor))
                return;

            best = (of, path, id, name, at, rank, away, price, fromCursor);
        }

        foreach (var (item, _, rank, name) in listed)
            Consider(item, [], rank, name);

        // ENTITY lines: shattering and chests, ranked by their line; without the line, not done. See RemnantsLine.
        var remnantsRank = RankOfLine(autoloot.InstantLootItems, RemnantsLine);
        var chestsRank = RankOfLine(autoloot.InstantLootItems, ExcavatedChestsLine);

        if (remnantsRank != int.MaxValue && scan != null)
        {
            foreach (var target in scan.Targets)
            {
                if (target.Shatterable && target.Entity != null)
                    Consider(target.Entity, Placement.ShatterPath, remnantsRank, "Spent remnant");
            }
        }

        if (chestsRank != int.MaxValue)
        {
            foreach (var chest in Placement.ExcavatedChestsToOpen(gc))
                Consider(chest, [], chestsRank, "Excavated chest");
        }

        if (best is not { } chosen)
            return;

        // **Clear in the interface tree, not only of the panels the plugin knows**, before anything is taken: the spot was
        // judged against the fixed furniture and named panels alone, so a Divine Orb under the buff bar was moved to,
        // clicked on the bar, and the player's input blocked for nothing (Stagnant Basin, 2026-10-11 17:26:13). The walk
        // placement makes before its click, about 0.74ms; a covered item waits CoveredWaitMs and is looked at again,
        // untried. See Panels.Blocked.
        if (Panels.Blocked(gc, chosen.At, true))
        {
            CoveredUntil[chosen.Id] = DateTime.UtcNow + CoveredWait;
            _lastOutcome = $"{chosen.Name}: under a piece of the interface - not clicked, looked at again shortly";
            return;
        }

        // An ExileInput2 without TakeWithPriority falls back to an ordinary Take, and waits for the cursor to be free.
        if (!(_input.TakeWithPriority(InstantLootPriority) || _input.Take()))
            return;

        _itemId = chosen.Id;
        _itemName = chosen.Name;
        _target = chosen.Of;
        _targetPath = chosen.Path;
        TriedItemIds.Add(chosen.Id);

        var ms = Math.Clamp(autoloot.MouseBlockMs.Value, 125, 1000);

        // Blocked only where there is a race: with a party member in the area, unless asked always. See
        // AutolootSettings.BlockInputOnlyWithParty.
        var blockInput = autoloot.BlockInputForInstantLoot.Value && (!autoloot.BlockInputOnlyWithParty.Value || PartyInArea(gc));

        _input.IgnorePlayerMouse(ms, blockInput);

        // The keyboard too, from the pickup on, so a walk on WASD stops before the click. See BlockInputForInstantLoot.
        _restoreHeldKeys = autoloot.RestoreHeldKeysAfterInstantLoot.Value;
        _cancelKey = autoloot.ActionKeyEndsInputBlock.Value ? Safe.Read(() => settings.ActionHotkey.Value.Key, Keys.None) : Keys.None;

        if (blockInput)
            _keysBlocked = _input.BlockPlayerKeys(KeyBlockMs, _restoreHeldKeys, _cancelKey);
        _input.SetShortestMove(0);
        _input.SetMoveTimePerRootPixel(autoloot.TeleportForInstantLoot.Value ? 0 : FastMoveTimePerRootPixel);

        if (!_input.MoveTo(new RectangleF(chosen.At.X - 3f, chosen.At.Y - 3f, 6f, 6f)))
        {
            _lastOutcome = $"{_itemName}: the move was refused";
            Finish();
            return;
        }

        _deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ms);
        _overSomethingElse = false;
        _phase = Phase.Moving;

        // The click armed to go out on arrival from ExileInput2's input thread. See StepMoving and
        // AutolootSettings.ClickInstantLootWithoutWaiting.
        _armedAt = chosen.At;
        _clickArmed = autoloot.ClickInstantLootWithoutWaiting.Value && _input.ClickOnArrival();
    }

    /// <summary>Whether this pickup's click is armed in ExileInput2 to go out on arrival, and where it aims. See StepMoving.</summary>
    private static bool _clickArmed;

    private static Vector2 _armedAt;

    /// <summary>
    /// **While the cursor travels with the click armed**: the checks the click would otherwise make on arrival, made
    /// now, since the click goes out the moment the cursor gets there. The item gone, nothing is clicked. Its label slid
    /// off the point aimed at, the move goes to where it is now, still armed. The point under a piece of the interface,
    /// the click is disarmed and the arrival checks below decide. True when this step is done with.
    ///
    /// A disarm that comes too late - the click already out - is taken as the click it was.
    /// </summary>
    private static bool StepArmed(GameController gc)
    {
        if (_input.ClickedOnArrival())
        {
            _clickArmed = false;
            AfterClick(gc, true);
            return true;
        }

        var rect = Placement.Button(gc, _target, _targetPath).Rect;

        if (rect.Width <= 0f)
        {
            if (Disarm(gc))
                return true;

            _lastOutcome = $"{_itemName}: gone before the click - not clicked";
            Finish();
            return true;
        }

        if (!(_armedAt.X >= rect.Left && _armedAt.X <= rect.Right && _armedAt.Y >= rect.Top && _armedAt.Y <= rect.Bottom))
        {
            var spot = Placement.Exposed(gc, Panels.Covered(gc), rect, _itemId).At;

            if (spot != Vector2.Zero && !Panels.Blocked(gc, spot, true) &&
                _input.MoveTo(new RectangleF(spot.X - 3f, spot.Y - 3f, 6f, 6f)))
            {
                _armedAt = spot;
                return true;
            }

            return Disarm(gc);
        }

        if (Panels.Blocked(gc, _armedAt, true))
            return Disarm(gc);

        return false;
    }

    /// <summary>
    /// Takes the armed click back, for the arrival checks to decide. True when it had already gone out, which is then
    /// taken as the click. See StepArmed.
    /// </summary>
    private static bool Disarm(GameController gc)
    {
        _clickArmed = false;

        if (_input.CancelClickOnArrival() || !_input.ClickedOnArrival())
            return false;

        AfterClick(gc, true);
        return true;
    }

    private static void StepMoving(GameController gc)
    {
        if (_input.WasStopped())
        {
            _lastOutcome = $"{_itemName}: stopped ({_input.Stopped()}) before the click";
            Finish();
            return;
        }

        if (_clickArmed && StepArmed(gc))
            return;

        if (_input.Arrived())
        {
            // Arrived with the click still armed - its last click's button not yet up, say - it is taken back and made
            // here, after the checks. See StepArmed.
            if (_clickArmed && Disarm(gc))
                return;

            // **Where the label is now, not where it was.** With the character moving, the camera follows and the label
            // slides across the screen during the move, so the cursor can arrive where it was and click beside it - and
            // an item is tried once. Off it, the cursor is sent on to where it is now, within the same deadline.
            var rect = Placement.Button(gc, _target, _targetPath).Rect;
            var cursor = CursorOnScreen(gc);

            if (rect.Width > 0f &&
                !(cursor.X >= rect.Left && cursor.X <= rect.Right && cursor.Y >= rect.Top && cursor.Y <= rect.Bottom))
            {
                var spot = Placement.Exposed(gc, Panels.Covered(gc), rect, _itemId).At;

                if (spot != Vector2.Zero && _input.MoveTo(new RectangleF(spot.X - 3f, spot.Y - 3f, 6f, 6f)))
                    return;
            }

            // **The game's word on what is under the cursor, last.** Everything above is this plugin's geometry - the
            // label's rectangle, the other labels and panels cut out of it - and a click on something it does not know
            // about would spend the item's one try. On something else, the cursor goes to a clear spot again, within
            // the same deadline; when the game names nothing, the geometry stands, as in the post-expedition pass. See
            // Placement.UnderCursor.
            if (!Placement.UnderCursor(gc, _target, _targetPath))
            {
                var spot = Placement.Exposed(gc, Panels.Covered(gc), Placement.Button(gc, _target, _targetPath).Rect, _itemId).At;

                _overSomethingElse = true;

                if (spot != Vector2.Zero && _input.MoveTo(new RectangleF(spot.X - 3f, spot.Y - 3f, 6f, 6f)))
                    return;
            }

            // The tree again at the cursor, the moment before the click: the interface moves too. Covered, nothing is
            // clicked and the input is let go at once; the item waits CoveredWait, untried.
            if (Panels.Blocked(gc, CursorOnScreen(gc), true))
            {
                TriedItemIds.Remove(_itemId);
                CoveredUntil[_itemId] = DateTime.UtcNow + CoveredWait;
                _lastOutcome = $"{_itemName}: under a piece of the interface at the click - not clicked, input let go";
                Finish();
                return;
            }

            AfterClick(gc, _input.Click());
            return;
        }

        if (DateTime.UtcNow >= _deadline)
        {
            _lastOutcome = _overSomethingElse
                ? $"{_itemName}: the game kept reporting something else under the cursor - not clicked"
                : $"{_itemName}: the cursor did not arrive within the mouse block time";
            Finish();
        }
    }

    /// <summary>What follows the click, made here or armed and made on arrival: the note, the pickup to confirm, the walk.</summary>
    private static void AfterClick(GameController gc, bool clicked)
    {
        _lastOutcome = clicked ? $"{_itemName}: clicked" : $"{_itemName}: the click was refused";

        if (clicked)
        {
            _pickupNote = ($"Instant autoloot: {_itemName}", DateTime.UtcNow);
            _clickedAt = DateTime.UtcNow;

            if (_targetPath.Length == 0)
                NoteClicked(gc, _target);

            // What is carried of it now, for ConfirmPickup to compare against.
            _targetMetadata = Safe.Read(_target, static e => e.GetComponent<WorldItem>()?.ItemEntity?.Metadata, "") ?? "";
            _carriedAtClick = CarriedOfMetadata(gc, _targetMetadata);

            // Out of reach, the character walks to it. See WalkStartsWithin.
            var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
            var item = Safe.Read(_target, static e => e.GridPos, Vector2.Zero);

            _walkSeen = false;
            _movedAt = DateTime.MinValue;
            _lastPosition = player;
            _walkExpected = player != Vector2.Zero && item != Vector2.Zero &&
                            Vector2.Distance(player, item) > PickupReachGrid;

            // The key block runs on from the click, for the walk to the item; it ends when the label goes. See
            // KeyBlockMs.
            if (_keysBlocked)
                _input.BlockPlayerKeys(KeyBlockMs, _restoreHeldKeys, _cancelKey);
        }

        Finish(keepKeysBlocked: clicked);

        if (clicked)
        {
            _deadline = DateTime.UtcNow + LabelGoneWithin;
            _phase = Phase.WaitingForLabel;
        }
    }

    /// <summary>
    /// How long the player's keys are blocked from an instant click: long enough for the walk to an item at the edge of
    /// the screen, and ended as soon as the item's label goes. See AutolootSettings.BlockInputForInstantLoot.
    /// </summary>
    private const int KeyBlockMs = 2000;

    /// <summary>How soon after a click a standing character counts as having stopped, rather than not yet started. See Tick.</summary>
    private static readonly TimeSpan RetryAfterStandingMs = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether this pickup asked for the player's keys blocked, so it lets them go when done.</summary>
    private static bool _keysBlocked;

    /// <summary>
    /// How soon after a click on an item out of reach the character is walking, or the key block ends. 125ms let the
    /// keyboard go before a walk to a Divine Orb began, and the player, still holding W, walked away from it (2026-10-11
    /// 17:31:39). 400 is a guess at comfortably more than the click's round trip, until the walk starts measured in the
    /// dump say otherwise. See WalkStartsMeasured.
    /// </summary>
    private static readonly TimeSpan WalkStartsWithin = TimeSpan.FromMilliseconds(400);

    /// <summary>How long after each instant click out of reach the character was first seen walking, in ms, the last 20.</summary>
    private static readonly List<int> WalkStartsMeasured = new();

    /// <summary>Whether the last click was on an item out of pickup reach, which the character walks to.</summary>
    private static bool _walkExpected;

    /// <summary>Whether the walk after the last click has been seen to start. See WalkStartsMeasured.</summary>
    private static bool _walkSeen;

    /// <summary>AutolootSettings.RestoreHeldKeysAfterInstantLoot, read when the run's key block began.</summary>
    private static bool _restoreHeldKeys = true;

    /// <summary>
    /// The action key while AutolootSettings.ActionKeyEndsInputBlock is on, Keys.None otherwise, read when the run's
    /// key block began.
    /// </summary>
    private static Keys _cancelKey = Keys.None;

    /// <summary>
    /// Whether the character is moving: its grid position changed within the last MovedWithin, or the actor says so.
    /// **By position**, because the actor's isMoving and Moving action did not show a click's walk to an item: the
    /// keyboard was let go as "no walk began" while the character walked to a Divine Orb, and the player, holding D,
    /// walked away from it (2026-10-11 17:31). Asked every Tick while a pickup waits, so a change of a grid is seen.
    /// </summary>
    private static bool MovingNow(GameController gc)
    {
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        if (player != Vector2.Zero && Vector2.Distance(player, _lastPosition) > 0.5f)
        {
            _lastPosition = player;
            _movedAt = DateTime.UtcNow;
        }

        return DateTime.UtcNow - _movedAt < MovedWithin || Placement.Walking(gc);
    }

    /// <summary>How recently the position must have changed to count as moving. See MovingNow.</summary>
    private static readonly TimeSpan MovedWithin = TimeSpan.FromMilliseconds(150);

    private static Vector2 _lastPosition;
    private static DateTime _movedAt = DateTime.MinValue;

    /// <summary>How long an item found under a piece of the interface is left before it is looked at again.</summary>
    private static readonly TimeSpan CoveredWait = TimeSpan.FromMilliseconds(250);

    /// <summary>Items found under a piece of the interface, by entity id, and when they may be looked at again.</summary>
    private static readonly Dictionary<uint, DateTime> CoveredUntil = new();

    /// <summary>
    /// How long after the clicked label goes the inventory may take to show the item before the pickup counts as
    /// failed. 1000ms is a guess at comfortably more than the server's reply, not a measured figure.
    /// </summary>
    private static readonly TimeSpan InventoryShowsWithin = TimeSpan.FromMilliseconds(1000);

    /// <summary>The clicked ground item's own metadata, the base it is carried as; empty for a chest or remnant.</summary>
    private static string _targetMetadata = "";

    /// <summary>CarriedOfMetadata for _targetMetadata at the click; -1 when it could not be read.</summary>
    private static int _carriedAtClick = -1;

    /// <summary>
    /// How many of this base the player carries in the main inventory, stacks summed, from the game's own record of it
    /// (ServerData.PlayerInventories, MainInventory1) so it holds with the inventory panel closed; -1 when it cannot be
    /// read.
    /// </summary>
    private static int CarriedOfMetadata(GameController gc, string metadata)
    {
        if (metadata.Length == 0)
            return -1;

        var holders = Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInventories, null);
        var main = holders?.FirstOrDefault(h => Safe.Read(h, static x => x.TypeId, InventoryNameE.None) == InventoryNameE.MainInventory1);
        var items = Safe.Read(main, static m => m.Inventory.Items, null);

        if (items == null)
            return -1;

        return items
            .Where(e => string.Equals(Safe.Read(e, static x => x.Metadata, ""), metadata, StringComparison.OrdinalIgnoreCase))
            .Sum(e => Math.Max(1, Safe.Read(e, static x => x.GetComponent<Stack>()?.Size ?? 1, 1)));
    }

    /// <summary>
    /// After the clicked label went: success once more of the item is carried than at the click, failure when
    /// InventoryShowsWithin passes without it. A chest, a remnant or an unreadable inventory has nothing to compare and
    /// plays nothing. See AutolootSettings.PlayInstantLootResultSound.
    /// </summary>
    private static void ConfirmPickup(GameController gc, AutoExpeditionSettings settings)
    {
        if (PickedUpOntoCursor(gc) is { } held)
        {
            StartPuttingDown(gc, held);

            return;
        }

        var carried = CarriedOfMetadata(gc, _targetMetadata);

        if (_carriedAtClick < 0 || carried < 0)
        {
            _phase = Phase.Idle;

            return;
        }

        if (carried > _carriedAtClick)
        {
            _phase = Phase.Idle;
            _lastOutcome += "; picked up";
            SoundForResult(settings, true);

            return;
        }

        if (DateTime.UtcNow < _deadline)
            return;

        _phase = Phase.Idle;
        _lastOutcome += $"; the label went but no more was carried after {InventoryShowsWithin.TotalMilliseconds:0}ms - taken by someone else, or no room";
        SoundForResult(settings, false);
    }

    /// <summary>
    /// Takes the cursor and moves it to a free place in the inventory for an item the pickup put on the cursor, the
    /// inventory panel being open. With no free place, or the cursor not to be had before the pickup's deadline, the
    /// item stays held and the outcome says so. See PickedUpOntoCursor.
    /// </summary>
    private static void StartPuttingDown(GameController gc, Entity held)
    {
        if (PutDownPlace(gc, held) is not { } place)
        {
            _phase = Phase.Idle;
            _lastOutcome += $"; {HeldName(held)} went onto the cursor and the inventory has no free place to put it down";

            return;
        }

        if (!(_input.TakeWithPriority(InstantLootPriority) || _input.Take()))
        {
            if (DateTime.UtcNow >= _deadline)
            {
                _phase = Phase.Idle;
                _lastOutcome += $"; {HeldName(held)} went onto the cursor and the cursor could not be taken to put it down";
            }

            return;
        }

        MoveToPutDown(held, place);
    }

    /// <summary>Sends the cursor to the place a held item goes down, and waits for it there. See StepPuttingDown.</summary>
    private static void MoveToPutDown(Entity held, RectangleF place)
    {
        if (!_input.MoveTo(place))
        {
            _lastOutcome += $"; {HeldName(held)} went onto the cursor and the move to put it down was refused";
            Finish();

            return;
        }

        _putDownClicked = false;
        _deadline = DateTime.UtcNow + PutDownWithin;
        _phase = Phase.PuttingDown;
    }

    /// <summary>
    /// Clicks the held item down once the cursor is over its place, then confirms the pickup as ConfirmPickup does.
    /// Gives up after PutDownWithin.
    /// </summary>
    private static void StepPuttingDown(GameController gc, AutoExpeditionSettings settings)
    {
        if (PickedUpOntoCursor(gc) == null)
        {
            Finish();
            _lastOutcome += "; put down from the cursor into the inventory";
            _deadline = DateTime.UtcNow + InventoryShowsWithin;
            _phase = Phase.ConfirmingPickup;

            return;
        }

        if (DateTime.UtcNow >= _deadline)
        {
            Finish();
            _lastOutcome += $"; still held on the cursor {PutDownWithin.TotalMilliseconds:0}ms after the move to put it down";
            SoundForResult(settings, false);

            return;
        }

        if (!_putDownClicked && _input.Arrived())
            _putDownClicked = _input.Click();
    }

    /// <summary>Whether the click putting the held item down has gone out. See StepPuttingDown.</summary>
    private static bool _putDownClicked;

    /// <summary>How long the move and click putting a held item down may take. Chosen.</summary>
    private static readonly TimeSpan PutDownWithin = TimeSpan.FromSeconds(2);

    /// <summary>Plays Instant loot success or failure sound, at Instant loot sound volume. See PlayInstantLootResultSound.</summary>
    private static void SoundForResult(AutoExpeditionSettings settings, bool pickedUp)
    {
        var autoloot = settings.Automation.Autoloot;

        if (!autoloot.PlayInstantLootResultSound)
            return;

        PlaySound(pickedUp ? autoloot.InstantLootSuccessSound.Value : autoloot.InstantLootFailureSound.Value,
            autoloot.InstantLootSoundVolume.Value);
    }

    /// <summary>When the last instant click went out. See Tick.</summary>
    private static DateTime _clickedAt = DateTime.MinValue;

    /// <summary>Times each item has been clicked again, by entity id, for the area. See AutolootSettings.InstantLootRetries.</summary>
    private static readonly Dictionary<uint, int> RetriesOfItems = new();

    /// <summary>Ends the key block this pickup asked for, if any.</summary>
    private static void EndKeyBlock()
    {
        if (!_keysBlocked)
            return;

        _keysBlocked = false;
        _input?.BlockPlayerKeys(0, _restoreHeldKeys, Keys.None);
    }

    /// <summary>"Instant autoloot: name" for the last instant click, and when it was sent. See DrawPickupNote.</summary>
    private static (string Text, DateTime At) _pickupNote = ("", DateTime.MinValue);

    /// <summary>How long the instant autoloot note stays under the cursor.</summary>
    private static readonly TimeSpan PickupNoteFor = TimeSpan.FromSeconds(4);

    /// <summary>
    /// "Instant autoloot: name" under the cursor for PickupNoteFor after an instant click, placed, backed and faded as
    /// CursorWarning's words are, in white: it reports something done, not a warning.
    /// </summary>
    public static void DrawPickupNote(Graphics graphics, GameController gc, AutoExpeditionSettings settings)
    {
        if (!settings.Automation.Autoloot.ShowInstantLootMessage)
            return;

        var age = (DateTime.UtcNow - _pickupNote.At).TotalSeconds;

        if (_pickupNote.Text.Length == 0 || age >= PickupNoteFor.TotalSeconds)
            return;

        var cursor = Safe.Read(gc, static g => new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), Vector2.Zero);
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);

        if (cursor == Vector2.Zero || window.Width <= 0f)
            return;

        var size = graphics.MeasureText(_pickupNote.Text);
        var at = new Vector2(
            Math.Clamp(cursor.X + 16f, 0f, Math.Max(0f, window.Width - size.X - 4f)),
            Math.Clamp(cursor.Y + 44f, 0f, Math.Max(0f, window.Height - size.Y - 4f)));
        var alpha = (int)(255 * Math.Clamp(PickupNoteFor.TotalSeconds - age, 0d, 1d));

        graphics.DrawTextWithBackground(_pickupNote.Text, at, System.Drawing.Color.FromArgb(alpha, System.Drawing.Color.White),
            System.Drawing.Color.FromArgb(alpha * 200 / 255, System.Drawing.Color.Black));
    }

    /// <summary>Puts this caller's move speeds back, lets go of the cursor and goes idle.</summary>
    private static void Finish(bool keepKeysBlocked = false)
    {
        _phase = Phase.Idle;

        if (_input == null)
            return;

        if (!keepKeysBlocked)
            EndKeyBlock();

        _input.SetShortestMove(-1);
        _input.SetMoveTimePerRootPixel(-1);
        _input.Release();
    }

    /// <summary>
    /// The nearest visible item on either list, for the autoloot line, or null. In world coordinates and grid, with the
    /// distance from the player in grid.
    /// </summary>
    internal static (Vector3 World, Vector2 Grid, float Away)? NearestListedItem(GameController gc, AutoExpeditionSettings settings)
    {
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

        if (player == Vector2.Zero)
            return null;

        (Vector3, Vector2, float)? nearest = null;
        var closest = float.MaxValue;

        foreach (var list in new[] { settings.Automation.Autoloot.InstantLootItems, settings.Automation.Autoloot.ItemsOnActionKey })
        {
            foreach (var (item, _, _, _) in ListedOnGround(gc, list))
            {
                var grid = Safe.Read(item, static e => e.GridPos, Vector2.Zero);
                var world = Safe.Read(item, static e => e.Pos, Vector3.Zero);
                var away = Vector2.Distance(player, grid);

                if (grid == Vector2.Zero || world == Vector3.Zero || away >= closest)
                    continue;

                closest = away;
                nearest = (world, grid, away);
            }
        }

        return nearest;
    }

    /// <summary>
    /// The autoloot line to the nearest listed item, where the expedition loot line is not drawn - away from a dig site,
    /// or before it is looted. Overlay.Loot draws it there, offering listed items with the rest. See
    /// AutolootSettings.LineToListedItems.
    /// </summary>
    public static void DrawLine(Graphics graphics, GameController gc, AutoExpeditionSettings settings, List<RectangleF> covered)
    {
        if (!settings.Automation.PostExpedition.Line || !settings.Automation.Autoloot.LineToListedItems)
            return;

        if (NearestListedItem(gc, settings) is not { } nearest)
            return;

        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var player = Safe.Read(gc, static g => g.Player.Pos, Vector3.Zero);

        if (camera == null || player == Vector3.Zero)
            return;

        var from = Safe.Read((camera, player), static x => x.camera.WorldToScreen(x.player), Vector2.Zero);
        var to = Safe.Read((camera, nearest.World), static x => x.camera.WorldToScreen(x.World), Vector2.Zero);

        if (from == Vector2.Zero || to == Vector2.Zero || Panels.Covers(covered, from) || Panels.Covers(covered, to))
            return;

        graphics.DrawLine(from, to, LineThickness(settings), settings.Automation.Autoloot.LineColour.Value);
    }

    /// <summary>The autoloot line's thickness in pixels. See AutolootSettings.LineThickness.</summary>
    internal static float LineThickness(AutoExpeditionSettings settings) => Math.Clamp(settings.Automation.Autoloot.LineThickness.Value, 1, 3);

    /// <summary>The instant loot run's state and the lists as parsed, for the F6 dump.</summary>
    public static void Describe(StringBuilder b, GameController gc, AutoExpeditionSettings settings)
    {
        b.AppendLine("=== autoloot ===");
        b.AppendLine($"  instant list: {RanksOf(settings.Automation.Autoloot.InstantLootItems).Count} name(s), on the action key: " +
                     $"{RanksOf(settings.Automation.Autoloot.ItemsOnActionKey).Count} name(s); block mouse {settings.Automation.Autoloot.BlockInputForInstantLoot.Value}, " +
                     $"{settings.Automation.Autoloot.MouseBlockMs.Value}ms, teleport {settings.Automation.Autoloot.TeleportForInstantLoot.Value}");
        b.AppendLine($"  instant loot run: {_phase}, {TriedItemIds.Count} item(s) tried this area, " +
                     $"{RetriesOfItems.Values.Sum()} retry click(s), keys blocked {_keysBlocked}, last: {_lastOutcome}");

        var labels = Ground.Labels(gc);
        var visible = labels?.Count(l => Safe.Read(l, static x => x.IsVisible, false)) ?? 0;

        b.AppendLine($"  label list reads: {Ground.ReadsOnChange} on an item arriving or going ({Ground.ChangesSeen} such events), " +
                     $"{Ground.ReadsOnRefresh} on the half-second refresh");
        b.AppendLine($"  ground labels: {labels?.Count ?? 0} in the list, {visible} visible; item facts kept {FactsOfItems.Count}, " +
                     $"prices kept {PricesOfItems.Count}, unpriced waiting {UnpricedUntil.Count}");

        PartyInArea(gc);
        b.AppendLine($"  order of both lists: {(TopFirstNow(gc) ? "top first" : "nearest first")} now; {_partySaid}");
        // Each party portrait with its display flags, to find what greys a member out who is in another area: the zone
        // name is not that. See PartyInArea.
        var members = Safe.Read(gc, static g => g.IngameState.IngameUi.PartyElement.PlayerElements, null);

        foreach (var member in members ?? [])
        {
            var rect = Safe.Read(member, static m => m.GetClientRectCache, default);

            b.AppendLine($"    party member \"{Safe.Read(member, static m => m.PlayerName, "")}\", zone \"{Safe.Read(member, static m => m.ZoneName, "")}\", " +
                         $"teleport button visible {Safe.Read(member, static m => m.TeleportButton?.IsVisible ?? false, false)}, portal icon {HoldsPortalIcon(member)}, " +
                         $"({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}):");
            b.Append(Tablets.LitFlagsSaid(member, "      ", 4));
        }

        b.AppendLine($"  walks after an instant click began after (ms): " +
                     (WalkStartsMeasured.Count == 0 ? "none measured yet" : string.Join(", ", WalkStartsMeasured)) +
                     $"; the keyboard is let go after {WalkStartsWithin.TotalMilliseconds:0}ms without one");
        b.AppendLine($"  {Ground.VisibleCountsSaid(gc)}");
        b.AppendLine($"  inventory space: {InventorySpace.Said(gc)}; slots kept for instant loot now: " +
                     $"{InventorySpace.KeptForInstantLoot(gc, settings)}; listed items left for want of room at the last look: " +
                     (Placement.NoRoomFor.Count == 0 ? "none" : string.Join(", ", Placement.NoRoomFor)));
        var onCursor = Tablets.ItemOnCursor(gc, true);
        b.AppendLine($"  inventory panel open {InventoryPanelOpen(gc)}; on the cursor: " +
                     (onCursor == null ? "nothing" : $"{HeldName(onCursor)} ({Safe.Read(onCursor, static e => e.Metadata, "?")})") +
                     $"; taken for a pickup to put down: {PickedUpOntoCursor(gc) != null}; items clicked by metadata, last " +
                     $"{HeldAfterClickWithin.TotalSeconds:0}s: " +
                     string.Join(", ", ClickedMetadata.Where(x => DateTime.UtcNow - x.Value <= HeldAfterClickWithin)
                         .Select(x => x.Key[(x.Key.LastIndexOf('/') + 1)..])));
        b.AppendLine("  items in view, who may pick each up (ExileCore2's label and WorldItem):");

        foreach (var line in AllocationSaid(gc))
            b.AppendLine($"    {line}");

        b.AppendLine($"  pickup reach used: {PickupReachGrid:0} grid; measured after clicks: " +
                     (PickupsMeasured.Count == 0
                         ? "none yet"
                         : string.Join(", ", PickupsMeasured.Select(x => $"{x.Name} {x.Away:0.#}"))));

        // Each condition line as it is read now, and why any cannot be used, so a typo is not silently a line that never
        // matches. See ConditionLine.
        foreach (var (listName, list) in new[] { ("instant", settings.Automation.Autoloot.InstantLootItems), ("action key", settings.Automation.Autoloot.ItemsOnActionKey) })
        {
            foreach (var line in ConditionLinesOf(RanksOf(list)))
            {
                b.AppendLine(line.Unreadable != null
                    ? $"  {listName} list line {line.Rank + 1}: {line.Line} - not used: {line.Unreadable}"
                    : $"  {listName} list line {line.Rank + 1}: {line.Line} = class {line.ItemClass ?? "any"}, metadata from " +
                      $"{line.MetadataStart ?? "any"}, art {line.ArtName ?? "any"}, at least {(line.Unit == null ? "any price" : ExaltsOfMin(line) is { } least ? $"{least:0.##}ex" : $"? ({line.Unit} rate not known yet)")}");
            }
        }

        foreach (var list in new[] { settings.Automation.Autoloot.InstantLootItems, settings.Automation.Autoloot.ItemsOnActionKey })
        {
            foreach (var (item, _, rank, name) in ListedOnGround(gc, list))
            {
                var id = Safe.Read(item, static e => e.Id, 0u);

                b.AppendLine($"    on the ground: \"{name}\" (line {rank + 1} of the {(list == settings.Automation.Autoloot.InstantLootItems ? "instant" : "action key")} list), " +
                             $"id {id}{(TriedItemIds.Contains(id) ? ", tried" : "")}");
            }
        }

        // Every item on screen with the names it can be listed by, so a line that never matches can be checked against
        // what the game says. See ConditionLine.
        var shown = 0;

        foreach (var label in Ground.Labels(gc) ?? [])
        {
            if (shown >= 60 || !Safe.Read(label, static l => l.IsVisible, false))
                continue;

            var item = Safe.Read(label, static l => l.ItemOnGround, null);

            if (item == null || !Safe.Read(item, static e => e.HasComponent<WorldItem>(), false))
                continue;

            var (baseName, unique) = NamesOfItem(item);

            var itemGrid = Safe.Read(item, static e => e.GridPos, Vector2.Zero);
            var playerGrid = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);

            b.AppendLine($"    item on screen at ({itemGrid.X:0},{itemGrid.Y:0}), {Vector2.Distance(itemGrid, playerGrid):0.#} grid from the player: " +
                         $"base \"{baseName}\", unique \"{unique}\", class \"{ItemClassOfItem(gc, item)}\", " +
                         $"metadata \"{MetadataOfItem(item)}\", NinjaPricer {PriceOfItem(gc, item):0.##}ex");

            // The inner item's art, rarity and whether it is identified: whether an unidentified unique can be told
            // apart by its art, which is what an exact-unique line would need.
            var inner = Safe.Read(item, static e => e.GetComponent<WorldItem>()?.ItemEntity, null);
            var mods = Safe.Read(inner, static e => e.GetComponent<Mods>(), null);

            b.AppendLine($"      art \"{Safe.Read(inner, static e => e.GetComponent<RenderItem>()?.ResourcePath, "") ?? ""}\", " +
                         $"rarity {Safe.Read(mods, static m => m.ItemRarity.ToString(), "?")}, " +
                         $"identified {Safe.Read(mods, static m => m.Identified.ToString(), "?")}, " +
                         $"unique name read \"{Safe.Read(mods, static m => m.UniqueName, "") ?? ""}\"");

            // **An item that reads blank cannot match any line.** A Tier 2 Expedition Tablet's label showed on screen
            // while its ground entity read no name, class or metadata, at grid (0,0) (dump 2026-10-12 00:35:49). What the
            // label and its entity say, and whether the game's entity list holds that id, say which of them is wrong.
            if (MetadataOfItem(item).Length == 0)
            {
                var id = Safe.Read(item, static e => e.Id, 0u);
                var listed = Safe.Read(gc, g => g.Entities.FirstOrDefault(e => e.Id == id), null);

                b.AppendLine($"      READS BLANK: label text \"{Safe.Read(label, static l => l.Label?.Text, "") ?? ""}\" at " +
                             $"{Safe.Read(label, static l => l.Label?.GetClientRectCache.ToString(), "?")}, ground entity address " +
                             $"{Safe.Read(item, static e => e.Address, 0L):X}, id {id}, valid {Safe.Read(item, static e => e.IsValid, false)}, path " +
                             $"\"{Safe.Read(item, static e => e.Path, "") ?? ""}\", inner item address {Safe.Read(inner, static e => e.Address, 0L):X}; " +
                             (listed == null
                                 ? "no entity with that id in the entity list"
                                 : $"the entity list's one with that id: path \"{Safe.Read(listed, static e => e.Path, "") ?? ""}\", grid " +
                                   $"{Safe.Read(listed, static e => e.GridPos, Vector2.Zero)}, address {Safe.Read(listed, static e => e.Address, 0L):X}"));
            }

            shown++;
        }

        b.AppendLine();
    }
}
