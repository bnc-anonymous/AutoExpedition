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
/// chests as they become clickable. One item at a time: with a party member in the area, in list order, and within a
/// line those in pickup reach first, then by NinjaPricer the dearest, those within Priority threshold for loot matched by
/// the same line of each other taken nearest first; alone, the nearest. See TopFirstNow and BestInLootOrder. An item in reach plans the rest of its line in reach and the line's nearest
/// out of reach, and the cursor glides through them; see the plan in Tick. The cursor is taken, the player's mouse ignored - and by default blocked - for Mouse block time, the move
/// made at Instant loot shortest move and Instant loot move time per root pixel, and one click sent. The cursor is taken from a placement or tablet run that holds it
/// (InstantLootPriority). Each item is tried once, by entity id, whether the click took or not,
/// for as long as the area lasts. After a click on an item in pickup reach the next is clicked at once, the pickup
/// checked in the background (InReachPickups); after one that needs a walk, from under other labels, or with the
/// inventory open, the next waits until its label has gone.
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

        /// <summary>
        /// An item whose label is under other labels: the ground labels taken off the screen with Toggle Highlighting, and
        /// the cursor on its way to the item itself, where hovering shows its label and a click picks it up. See
        /// StepUnderLabels.
        /// </summary>
        UnderLabels,
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

    /// <summary>
    /// For the dump: the last idle look that saw a listed item, with the time, and what it did with it - taken up, or
    /// why not. Written only when something listed was in view, so it survives the item being gone by the time of the
    /// dump. The sound plays on seeing an item; every reason here is one where it plays and nothing is clicked.
    /// </summary>
    private static string _lastListedDecision = "no listed item seen yet";

    private static void RecordListedDecision(string decision) =>
        _lastListedDecision = $"{DateTime.Now:HH:mm:ss.fff} {decision}";

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
        PendingConfirmations.Clear();
        InReachPickups.Clear();
        ClickTargets.Clear();
        PricesOfItems.Clear();
        UnpricedUntil.Clear();
        FactsOfItems.Clear();
        GearReadingsOfItems.Clear();
        FirstLooks.Clear();
        RanksOfItems.Clear();
        Finish();
    }

    /// <summary>
    /// The lines of a list in order, trimmed, blank ones dropped, by name ignoring case to their position: the line's
    /// number among the non-blank lines from 0, so the dump's "line N" is the N-th line in the editor. Parsed again only
    /// when the text changes.
    ///
    /// **A line starting with # is a comment**, counted for the numbering and otherwise ignored. **Commas split a line
    /// of names, not a line of conditions**: lists were once comma-separated, and splitting every line at its commas cut
    /// CLASS:Helmet,Boots or DEFENCE:ES,AR+ES into pieces, none a whole line. The names of one comma-separated line share
    /// its rank.
    /// </summary>
    private static Dictionary<string, int> RanksOf(TextNode list)
    {
        var text = list?.Value ?? "";

        if (!ParsedLists.TryGetValue(list ?? new TextNode(), out var parsed) || parsed.Text != text)
        {
            var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var number = 0;

            foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var rank = number++;

                if (line.StartsWith('#'))
                    continue;

                if (IsConditionLine(line))
                {
                    ranks.TryAdd(line, rank);
                    continue;
                }

                foreach (var name in line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    ranks.TryAdd(name, rank);
            }

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
    ///
    /// **For gear** (2026-10-20): CLASS takes several classes separated by commas, any of which will do. GEAR:Armour or
    /// GEAR:Weapon, or both separated by a comma, is an item with the game's Armour or Weapon component. ILVL:&lt;n&gt; is
    /// the item level at least (Mods.ItemLevel); LEVEL:&lt;n&gt; the base's level at least (BaseItemType.DropLevel), the
    /// requirement a normal base shows - not checked against the game's tooltip yet, and mods can raise a magic or rare
    /// item's requirement above it; SOCKETS:&lt;n&gt; the sockets at least (Sockets.NumberOfSockets). DEFENCE: the base's
    /// defences exactly, as AR, EV and ES joined by + for a hybrid, NONE for none, several separated by commas, any of
    /// which will do: AR where its Armour component's armour is above 0, and so on. Words before the first condition word
    /// name the item, its base type or unique name ignoring case. The gear words come from a price survey of normal
    /// bases (2026-10-20): what made one worth picking up was the exceptional socket, item level 82, energy shield, and
    /// for weapons the base itself. See GearReadingsOfItem.
    /// </summary>
    private sealed record ConditionLine(int Rank, string Line, string Name, string[] ItemClasses, string MetadataStart,
        string ArtName, double Amount, string Unit, string[] GearKinds, int? MinItemLevel, int? MinLevel, int? MinSockets,
        string[] Defences, string Unreadable);

    /// <summary>
    /// What a gear condition reads of an item, once: whether it has the Armour or the Weapon component, its item level,
    /// its base's level, its sockets and its defences as DEFENCE writes them. Null fields could not be read. See
    /// ConditionLine.
    /// </summary>
    private sealed record GearReadings(bool Armour, bool Weapon, int? ItemLevel, int? BaseLevel, int? Sockets, string Defences);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, GearReadings> GearReadingsOfItems = new();

    /// <summary>An item's gear readings, read once and kept by entity id for the area, or null with no item. See GearReadings.</summary>
    private static GearReadings GearReadingsOfItem(GameController gc, Entity onGround)
    {
        var id = Safe.Read(onGround, static e => e.Id, 0u);

        if (id != 0u && GearReadingsOfItems.TryGetValue(id, out var known))
            return known;

        var item = Safe.Read(onGround, static e => e.GetComponent<WorldItem>()?.ItemEntity, null);

        if (item == null)
            return null;

        var armour = Safe.Read(item, static e => e.GetComponent<Armour>(), null);
        var sockets = Safe.Read(item, static e => e.GetComponent<Sockets>(), null);
        var mods = Safe.Read(item, static e => e.GetComponent<Mods>(), null);
        var type = FactsOfItem(gc, onGround)?.Type;
        var parts = new List<string>();

        if (Safe.Read(armour, static a => a.ArmourScore, 0) > 0)
            parts.Add("AR");

        if (Safe.Read(armour, static a => a.EvasionScore, 0) > 0)
            parts.Add("EV");

        if (Safe.Read(armour, static a => a.EnergyShieldScore, 0) > 0)
            parts.Add("ES");

        var read = new GearReadings(
            armour != null,
            Safe.Read(item, static e => e.HasComponent<Weapon>(), false),
            mods == null ? null : Safe.Read(mods, static m => (int?)m.ItemLevel, null),
            type == null ? null : Safe.Read(() => (int?)type.DropLevel, null),
            sockets == null ? 0 : Safe.Read(sockets, static s => (int?)s.NumberOfSockets, null),
            parts.Count == 0 ? "NONE" : string.Join("+", parts));

        // Kept once its base type has read, as an item on the ground does not change.
        if (id != 0u && type != null)
            GearReadingsOfItems[id] = read;

        return read;
    }

    /// <summary>A DEFENCE value as GearReadings writes one: AR, EV and ES in that order joined by +, or NONE; null if unreadable.</summary>
    private static string DefenceOfWord(string word)
    {
        var parts = word.ToUpperInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1 && parts[0] == "NONE")
            return "NONE";

        if (parts.Length == 0 || parts.Any(p => p is not ("AR" or "EV" or "ES")) || parts.Distinct().Count() != parts.Length)
            return null;

        return string.Join("+", new[] { "AR", "EV", "ES" }.Where(parts.Contains));
    }

    /// <summary>The prices, set by the plugin. See ConditionLine.</summary>
    internal static Valuation Valuation { get; set; }

    /// <summary>NinjaPricer prices of items on the ground this area, in exalts for the stack, by entity id. See PriceOfItem.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, double> PricesOfItems = new();

    /// <summary>The words a condition line is made of. See ConditionLine.</summary>
    private static readonly string[] ConditionWords = ["CLASS:", "METADATA:", "ART:", "MIN:", "GEAR:", "ILVL:", "LEVEL:", "SOCKETS:", "DEFENCE:"];

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

            string metadataStart = null, artName = null, unit = null, unreadable = null;
            string[] itemClasses = null, gearKinds = null, defences = null;
            int? minItemLevel = null, minLevel = null, minSockets = null;
            var amount = 0d;
            var nameWords = new List<string>();
            var conditionSeen = false;

            int? Whole(string word, string prefix)
            {
                if (int.TryParse(word[prefix.Length..], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                    return value;

                unreadable ??= $"{word} is not a whole number";
                return null;
            }

            foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var isCondition = ConditionWords.Any(c => word.StartsWith(c, StringComparison.OrdinalIgnoreCase));

                // The item's name: the words before the first condition word. See ConditionLine.
                if (!isCondition && !conditionSeen)
                {
                    nameWords.Add(word);
                    continue;
                }

                conditionSeen = true;

                if (word.StartsWith("CLASS:", StringComparison.OrdinalIgnoreCase))
                    itemClasses = word["CLASS:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
                else if (word.StartsWith("GEAR:", StringComparison.OrdinalIgnoreCase))
                {
                    gearKinds = word["GEAR:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);

                    if (gearKinds.Length == 0 || gearKinds.Any(k => !k.Equals("Armour", StringComparison.OrdinalIgnoreCase) &&
                                                                   !k.Equals("Weapon", StringComparison.OrdinalIgnoreCase)))
                        unreadable ??= $"{word} is not GEAR:Armour, GEAR:Weapon or both";
                }
                else if (word.StartsWith("ILVL:", StringComparison.OrdinalIgnoreCase))
                    minItemLevel = Whole(word, "ILVL:");
                else if (word.StartsWith("LEVEL:", StringComparison.OrdinalIgnoreCase))
                    minLevel = Whole(word, "LEVEL:");
                else if (word.StartsWith("SOCKETS:", StringComparison.OrdinalIgnoreCase))
                    minSockets = Whole(word, "SOCKETS:");
                else if (word.StartsWith("DEFENCE:", StringComparison.OrdinalIgnoreCase))
                {
                    var read = word["DEFENCE:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(DefenceOfWord).ToArray();

                    if (read.Length == 0 || read.Any(d => d == null))
                        unreadable ??= $"{word} is not AR, EV, ES or NONE, joined by + or separated by commas";
                    else
                        defences = read;
                }
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
                    unreadable ??= $"{word} comes after a condition and is not one of {string.Join(", ", ConditionWords)}";
            }

            lines.Add(new ConditionLine(rank, line, nameWords.Count == 0 ? null : string.Join(" ", nameWords), itemClasses,
                metadataStart, artName, amount, unit, gearKinds, minItemLevel, minLevel, minSockets, defences, unreadable));
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

        if (line.Name != null)
        {
            var (baseName, unique) = NamesOfItem(onGround);

            if (!string.Equals(baseName, line.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(unique, line.Name, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (line.ItemClasses != null &&
            !line.ItemClasses.Contains(ItemClassOfItem(gc, onGround), StringComparer.OrdinalIgnoreCase))
            return false;

        if (line.GearKinds != null || line.MinItemLevel != null || line.MinLevel != null || line.MinSockets != null ||
            line.Defences != null)
        {
            if (GearReadingsOfItem(gc, onGround) is not { } gear)
                return false;

            if (line.GearKinds != null &&
                !line.GearKinds.Any(k => k.Equals("Armour", StringComparison.OrdinalIgnoreCase) ? gear.Armour : gear.Weapon))
                return false;

            if (line.MinItemLevel is { } itemLevel && !(gear.ItemLevel >= itemLevel))
                return false;

            if (line.MinLevel is { } level && !(gear.BaseLevel >= level))
                return false;

            if (line.MinSockets is { } sockets && !(gear.Sockets >= sockets))
                return false;

            if (line.Defences != null && !line.Defences.Contains(gear.Defences))
                return false;
        }

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
    /// 1.4 to 9.2 (dump 2026-10-11 15:37:16). Since, the character has stopped as far as 24.0 grid from an item it walked
    /// to, and 23.3 to 23.9 in all ten walks judged in the dump of 2026-10-19 14:15:21; the game stops it once the item
    /// is in reach, so the reach is at least that. 24 rather than 23, which took items 23 to 24 grid away as walks:
    /// waited for and never planned. Each pickup after a click is measured into the dump; see ObservePickups.
    /// </summary>
    internal const float PickupReachGrid = 24f;

    /// <summary>The walk to an item that far away: none within PickupReachGrid, otherwise the distance past it.</summary>
    internal static float WalkToItem(float away) => Math.Max(0f, away - PickupReachGrid);

    /// <summary>
    /// Where a label now at label on screen is expected once the character has walked into pickup reach of walkTo, a grid
    /// position; null where no walk is needed, a position cannot be read, or the point falls within Placement.Margin of
    /// the window's edge or outside it. Such a job cannot be clicked from the walk's end, and a point clamped to the edge
    /// sent the cursor towards an item the walk had taken off screen, the run then ending with it unclickable (dump
    /// 2026-10-19 14:19:40, 35.034). The
    /// camera follows the character, so the screen moves by the screen point of where the character stands now less that
    /// of where the walk ends, both projected with the camera as it is now. The walk is taken to end PickupReachGrid short
    /// of walkTo in a straight line, so a walk round an obstacle ends elsewhere and the estimate is off by the difference.
    /// See AutolootSettings.MoveCursorAheadDuringWalks.
    /// </summary>
    internal static Vector2? LabelAfterWalk(GameController gc, Vector2 walkTo, Vector2 label)
    {
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var camera = Safe.Read(gc, static g => g.IngameState.Camera, null);
        var window = Safe.Read(gc, static g => g.Window.GetWindowRectangle(), default);
        var away = Vector2.Distance(player, walkTo);

        if (player == Vector2.Zero || walkTo == Vector2.Zero || label == Vector2.Zero || camera == null ||
            window.Width <= 2f * Placement.Margin || window.Height <= 2f * Placement.Margin || away <= PickupReachGrid)
            return null;

        var end = WalkEnd(player, walkTo);
        var standsWorld = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(player), Vector3.Zero);
        var endWorld = Safe.Read(() => gc.IngameState.Data.ToWorldWithTerrainHeight(end), Vector3.Zero);

        if (standsWorld == Vector3.Zero || endWorld == Vector3.Zero)
            return null;

        var stands = Safe.Read((camera, standsWorld), static x => x.camera.WorldToScreen(x.standsWorld), Vector2.Zero);
        var ends = Safe.Read((camera, endWorld), static x => x.camera.WorldToScreen(x.endWorld), Vector2.Zero);

        if (stands == Vector2.Zero || ends == Vector2.Zero)
            return null;

        var at = label + stands - ends;

        return at.X > Placement.Margin && at.Y > Placement.Margin &&
               at.X < window.Width - Placement.Margin && at.Y < window.Height - Placement.Margin
            ? at
            : null;
    }

    /// <summary>
    /// Where a walk from player to an item at walkTo is taken to end, in grid: PickupReachGrid short of it in a straight
    /// line, or where the player stands when it is already in reach. See LabelAfterWalk.
    /// </summary>
    internal static Vector2 WalkEnd(Vector2 player, Vector2 walkTo)
    {
        var away = Vector2.Distance(player, walkTo);

        return away > PickupReachGrid ? walkTo + (player - walkTo) / away * PickupReachGrid : player;
    }

    /// <summary>
    /// A move ahead during a walk, waiting to be judged at its run's next click, by run: the job it was for, the point
    /// the cursor was sent to, the cursor's point at the walk's click, the item walked to, where the walk was taken to
    /// end (LabelAfterWalk's straight line), and when. See NoteClickAfterWalkAhead.
    /// </summary>
    private static readonly Dictionary<string, (uint NextId, string NextName, Vector2 Guessed, Vector2 ClickedAt, Vector2 WalkTo,
        Vector2 WalkEndGuessed, DateTime At)> WalkAheadGuesses = new();

    /// <summary>
    /// How long after the walk's click a move ahead is judged at the next click; later, it is counted as not judged. Two
    /// of ten were judged about 3s after, one with the character stopped 98 grid from the item walked to - something
    /// came between - while the rest came 190 to 700ms after (dump 2026-10-19 14:15:21). Chosen.
    /// </summary>
    private static readonly TimeSpan WalkAheadJudgedWithin = TimeSpan.FromSeconds(1.5);

    /// <summary>Moves ahead whose next click came after WalkAheadJudgedWithin, not judged. See DescribeWalkAhead.</summary>
    private static int _walkAheadNotJudged;

    /// <summary>
    /// Kept when a run has moved the cursor ahead of a walk, to be judged at its next click. Run names the run, "action
    /// key" or "instant loot"; clickedAt is the cursor's point at the walk's click, read before the move ahead. See
    /// AutolootSettings.MoveCursorAheadDuringWalks.
    /// </summary>
    internal static void NoteWalkAheadGuess(GameController gc, string run, uint nextId, string nextName, Vector2 guessed,
        Vector2 clickedAt, Vector2 walkTo)
    {
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var end = player == Vector2.Zero ? player : WalkEnd(player, walkTo);

        lock (WalkAheadGuesses)
            WalkAheadGuesses[run] = (nextId, nextName, guessed, clickedAt, walkTo, end, DateTime.UtcNow);
    }

    /// <summary>
    /// At a run's next click after a move ahead, before the cursor moves to it: how good the guess was, kept for the dump.
    /// Whether it was the job guessed; how far the point aimed at is from the point guessed; how far the cursor still is
    /// from it, against how far it would be without the move ahead, from the walk's click; and where the character
    /// stopped, against the item walked to and the walk's guessed end. Nothing without a guess in hand. See
    /// WalkAheadSaid.
    /// </summary>
    internal static void NoteClickAfterWalkAhead(GameController gc, string run, uint id, string name, Vector2 aimedAt)
    {
        (uint NextId, string NextName, Vector2 Guessed, Vector2 ClickedAt, Vector2 WalkTo, Vector2 WalkEndGuessed, DateTime At) guess;

        lock (WalkAheadGuesses)
        {
            if (!WalkAheadGuesses.Remove(run, out guess))
                return;
        }

        var since = DateTime.UtcNow - guess.At;

        if (since > WalkAheadJudgedWithin)
        {
            _walkAheadNotJudged++;
            return;
        }

        var cursor = CursorOnScreen(gc);
        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var same = id == guess.NextId;
        var off = Vector2.Distance(guess.Guessed, aimedAt);
        var left = Vector2.Distance(cursor, aimedAt);
        var without = Vector2.Distance(guess.ClickedAt, aimedAt);
        var fromItem = player == Vector2.Zero ? -1f : Vector2.Distance(player, guess.WalkTo);
        var fromEnd = player == Vector2.Zero ? -1f : Vector2.Distance(player, guess.WalkEndGuessed);

        lock (WalkAheadResults)
        {
            WalkAheadResults.Add((same, off, left, without, fromItem, fromEnd,
                $"{DateTime.Now:HH:mm:ss.fff} {run}: " +
                (same ? $"{name}, as guessed" : $"guessed {guess.NextName}, chose {name}") +
                $", {since.TotalMilliseconds:0}ms after the walk's click; guessed ({guess.Guessed.X:0},{guess.Guessed.Y:0}), " +
                $"aimed at ({aimedAt.X:0},{aimedAt.Y:0}), off {off:0}px; the cursor {left:0}px from it, " +
                $"{without:0}px without the move ahead; the character stopped {fromItem:0.0} grid from the item walked to, " +
                $"{fromEnd:0.0} grid from the walk's guessed end"));

            if (WalkAheadResults.Count > WalkAheadResultsKept)
                WalkAheadResults.RemoveAt(0);
        }
    }

    /// <summary>Each move ahead judged at the next click, newest last. See NoteClickAfterWalkAhead.</summary>
    private static readonly List<(bool Same, float Off, float Left, float Without, float FromItem, float FromEnd, string Said)>
        WalkAheadResults = new();

    /// <summary>How many judged moves ahead are kept for the dump. Chosen.</summary>
    private const int WalkAheadResultsKept = 60;

    /// <summary>
    /// Each move ahead during a walk judged at its run's next click, newest last, with medians over those of the job
    /// guessed: the guess's error, the cursor's distance left with and without it, and where the character stopped. For
    /// the dump. See NoteClickAfterWalkAhead.
    /// </summary>
    internal static void DescribeWalkAhead(StringBuilder b)
    {
        List<(bool Same, float Off, float Left, float Without, float FromItem, float FromEnd, string Said)> results;

        lock (WalkAheadResults)
            results = WalkAheadResults.ToList();

        b.AppendLine($"  cursor moved ahead during walks, judged at the next click, newest last ({results.Count}; " +
                     $"{_walkAheadNotJudged} not judged, their next click over {WalkAheadJudgedWithin.TotalSeconds:0.#}s after " +
                     "the walk's click):" + (results.Count == 0 ? " none since the plugin loaded" : ""));

        if (results.Count == 0)
            return;

        var same = results.Where(r => r.Same).ToList();

        static string Median(IEnumerable<float> values, string format)
        {
            var sorted = values.Where(v => v >= 0f).OrderBy(v => v).ToList();

            return sorted.Count == 0 ? "-" : sorted[sorted.Count / 2].ToString(format);
        }

        b.AppendLine($"    the job guessed in {same.Count} of {results.Count}; over those, medians: guess off " +
                     $"{Median(same.Select(r => r.Off), "0")}px, the cursor {Median(same.Select(r => r.Left), "0")}px from the " +
                     $"job against {Median(same.Select(r => r.Without), "0")}px without the move ahead; the character stopped " +
                     $"{Median(same.Select(r => r.FromItem), "0.0")} grid from the item walked to (taken as {PickupReachGrid:0}), " +
                     $"{Median(same.Select(r => r.FromEnd), "0.0")} grid from the walk's guessed end");

        foreach (var r in results)
            b.AppendLine("    " + r.Said);
    }

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
        PlayersInParty = (members?.Count ?? 0) + 1;
        PlayersInArea = Math.Max(here, others) + 1;
        _partySaid = $"area \"{area}\"; party panel: {here} member(s) here, {elsewhere} elsewhere (by the portal icon); " +
                     $"{others} other player(s) in the entity list; party in area: {_partyInArea}; players in party {PlayersInParty}, in area {PlayersInArea}";

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

    /// <summary>The party panel's members and the player, wherever they are; 1 alone. Set by PartyInArea.</summary>
    internal static volatile int PlayersInParty = 1;

    /// <summary>
    /// The players in this area, the player included: the more of the party panel's members here (no portal icon) and
    /// the other players in the entity list. Set by PartyInArea.
    /// </summary>
    internal static volatile int PlayersInArea = 1;

    private static DateTime _partyReadAt = DateTime.MinValue;
    private static bool _partyInArea;
    private static string _partySaid = "not read yet";

    /// <summary>
    /// Whether both lists are taken top first now - higher lines, then those in pickup reach, then the dearer by more
    /// than Priority threshold for loot matched by the same line, then the nearer (see BestInLootOrder) - rather than
    /// nearest first. Top first with another party member in this area, racing for the same loot; nearest first alone,
    /// where nothing can be lost and the shortest path round is best. Was a dropdown per list, removed 2026-10-11 as
    /// this was the only choice worth making. See BestInLootOrder.
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

    /// <summary>What the loot order weighs of one candidate: its list line, price in exalts, walk in grid and how far its label is from the cursor in pixels. See BestInLootOrder.</summary>
    internal readonly record struct LootOrderFacts(int Rank, double Price, float Away, float FromCursor);

    /// <summary>
    /// The candidate the loot order takes, by index, or -1 with none; for both the action key run and instant loot.
    ///
    /// Top first: the highest list line. On it, those within pickup reach, which all walk 0 (WalkToItem), before those
    /// out of it. Of those, the ones priced within bandExalts of the dearest; and of them, in reach the label nearest the
    /// cursor, out of reach the shortest walk. So with a band of 0.25 divine, items in reach at 2, 1.5, 1.4, 1.3 and 1
    /// divine go 2 first, then 1.5, 1.4 and 1.3 nearest the cursor first, then 1. The band is measured from the dearest
    /// left at each look, so once 1.5 is taken an item at 1.2 would join 1.4 and 1.3. A higher line still wins whatever
    /// the walk: a Mirror of Kalandra out of reach is walked to before a Divine Orb at the feet.
    ///
    /// Nearest first: the shortest walk, then the label nearest the cursor.
    ///
    /// Price came before the walk in the action key run, one candidate against the next: with a party member in the area
    /// it walked to a 10ex item past others of the same line worth 2 to 5ex within reach (the player's account; the dump
    /// of 2026-10-19 13:39:43 reads top first, party in area True). See AutolootSettings.SameLinePriorityThresholdDivines.
    /// </summary>
    internal static int BestInLootOrder(IReadOnlyList<LootOrderFacts> candidates, bool topFirst, double bandExalts)
    {
        var best = -1;

        if (!topFirst)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];

                if (best < 0 || c.Away < candidates[best].Away ||
                    c.Away == candidates[best].Away && c.FromCursor < candidates[best].FromCursor)
                    best = i;
            }

            return best;
        }

        if (candidates.Count == 0)
            return -1;

        var rank = candidates.Min(c => c.Rank);
        var inReach = candidates.Any(c => c.Rank == rank && c.Away <= 0f);
        var dearest = candidates.Where(c => c.Rank == rank && c.Away <= 0f == inReach).Max(c => c.Price);

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];

            if (c.Rank != rank || c.Away <= 0f != inReach || c.Price < dearest - bandExalts)
                continue;

            if (best < 0 ||
                (inReach
                    ? c.FromCursor < candidates[best].FromCursor
                    : c.Away < candidates[best].Away ||
                      c.Away == candidates[best].Away && c.FromCursor < candidates[best].FromCursor))
                best = i;
        }

        return best;
    }

    /// <summary>
    /// Whether a candidate loses to one already found whatever its price and label: top first, a lower line, or out of
    /// reach on a line with one in reach; nearest first, a longer walk. So its clickable point, the dear part of a look,
    /// need not be worked out. See BestInLootOrder.
    /// </summary>
    internal static bool LosesInLootOrder(bool topFirst, int rank, float away, IReadOnlyList<LootOrderFacts> found)
    {
        foreach (var f in found)
        {
            if (topFirst ? f.Rank < rank || f.Rank == rank && f.Away <= 0f && away > 0f : f.Away < away)
                return true;
        }

        return false;
    }

    /// <summary>The price band of the loot order in exalts: Priority threshold for loot matched by the same line at the divine price in use, 0 before one is read. See BestInLootOrder.</summary>
    internal static double LootOrderBandExalts(AutoExpeditionSettings settings) =>
        settings.Automation.Autoloot.SameLinePriorityThresholdDivines.Value * Math.Max(0d, Valuation.DivineInExalts);

    /// <summary>The cursor's position on screen, or the screen's corner when it cannot be read. See BestInLootOrder.</summary>
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
            var fresh = NoteLook(gc, onGround, id, ranked);

            lock (kept)
                kept[id] = (ranked.Rank, ranked.Name,
                    whole ? DateTime.MaxValue : DateTime.UtcNow + (fresh ? RankKeptUnmatchedWhileFresh : RankKeptUnmatched));
        }

        return ranked;
    }

    /// <summary>
    /// How long after an item is first looked at its rank, unmatched, is kept for only RankKeptUnmatchedWhileFresh. A stack
    /// a party member dropped right after one that went at once waited about a second before its click, the first
    /// waiting none (the player's account, 2026-10-20 18:06): suspected, its first look came before its facts or price
    /// read, and the "no match" was kept for RankKeptUnmatched. ListedItemTimings records what the first look read.
    /// </summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(2);

    /// <summary>How long a rank that matched nothing is kept while the item is fresh. See FreshFor. Chosen.</summary>
    private static readonly TimeSpan RankKeptUnmatchedWhileFresh = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Each item's first look, by entity id: when, whether its metadata had read, and its price then; dropped once it has
    /// matched a line and been noted in ListedItemTimings. Cleared on an area change.
    /// </summary>
    private static readonly Dictionary<uint, (DateTime At, bool MetadataRead, double Price)> FirstLooks = new();

    /// <summary>For each listed item, its first look and how long after it the item first matched a line, newest last, for the dump.</summary>
    private static readonly List<string> ListedItemTimings = new();

    /// <summary>
    /// Notes an item's first look, and how soon after it the item first matched a line. True while the item is within
    /// FreshFor of its first look. See FreshFor.
    /// </summary>
    private static bool NoteLook(GameController gc, Entity onGround, uint id, (int Rank, string Name) ranked)
    {
        lock (FirstLooks)
        {
            if (!FirstLooks.TryGetValue(id, out var first))
            {
                if (FirstLooks.Count > 2000)
                    FirstLooks.Clear();

                first = (DateTime.UtcNow, FactsOfItem(null, onGround)?.Metadata is { Length: > 0 }, PriceOfItem(gc, onGround));
                FirstLooks[id] = first;
            }

            if (ranked.Rank >= 0 && first.At != DateTime.MinValue)
            {
                var arrived = Ground.ArrivalOfItem(id);

                ListedItemTimings.Add($"{first.At.ToLocalTime():HH:mm:ss.fff} {ranked.Name} (id {id}): " +
                                      (arrived is { } a ? $"arrived {(first.At - a).TotalMilliseconds:0}ms before " : "arrival not seen; ") +
                                      "its first look, with its " +
                                      $"metadata {(first.MetadataRead ? "read" : "not read")} and priced {first.Price:0.#}ex; " +
                                      $"matched a line {(DateTime.UtcNow - first.At).TotalMilliseconds:0}ms later");

                if (ListedItemTimings.Count > 20)
                    ListedItemTimings.RemoveAt(0);

                FirstLooks[id] = (DateTime.MinValue, first.MetadataRead, first.Price);
            }

            return first.At != DateTime.MinValue && DateTime.UtcNow - first.At < FreshFor;
        }
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
            if (AllocatedToSomeoneElse(gc, item))
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
    ///
    /// **Not that flag alone either.** A remnant's reward allocated to the local player read allocated to someone else
    /// True, permanently True, to player 3429014651, which the player said was their own (dump 2026-10-16 00:51:09). So an
    /// item whose WorldItem.AllocatedToPlayer equals the local player's own allocation id (OwnLootId) is the player's own,
    /// whatever the flag says. With no id readable, the flag decides.
    /// </summary>
    internal static bool AllocatedToSomeoneElse(GameController gc, Entity item)
    {
        var world = Safe.Read(item, static e => e.GetComponent<WorldItem>(), null);

        if (world == null || !Safe.Read(world, static w => w.AllocatedToSomeoneElse, false))
            return false;

        var own = OwnLootId(gc);

        return own == 0u || Safe.Read(world, static w => w.AllocatedToPlayer, 0u) != own;
    }

    /// <summary>
    /// The id the game allocates the local player's loot to, as WorldItem.AllocatedToPlayer holds it: ExileCore2's
    /// Player.AllocatedLootId, an int holding the same bits as the uint. 0 when unreadable. A wrong reading after a patch
    /// matches no item's allocation and leaves the game's flag to decide. See AllocatedToSomeoneElse.
    ///
    /// Until ExileCore2's 2026-10-16 build that property read too few bytes (123 for 3429014651), and the id was read raw
    /// at Player+0x1D0, which agreed with the fixed property (dump 2026-10-16 04:14:17). The id differs between sessions:
    /// 3429014651 and 2039392453 on the same day.
    /// </summary>
    internal static uint OwnLootId(GameController gc) =>
        Safe.Read(gc, static g => g.Player?.GetComponent<Player>() is { } player ? unchecked((uint)player.AllocatedLootId) : 0u,
            0u);

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
                         (AllocatedToSomeoneElse(gc, item) ? " - LEFT ALONE" : "");
        }
    }

    /// <summary>
    /// For the dump: where the allocation id of an item in view allocated to a player appears in memory about the local
    /// player - the Player component, the player entity, ServerData and its PlayerInformation, and 1KB behind each pointer
    /// the Player component and the entity hold. Reads only. Written when Player.AllocatedLootId read 123 where the
    /// player's own items read allocated to 3429014651 (dump 2026-10-16 00:56:10); with the player's own item in view, an
    /// offset listed here is where the id sits. See AllocatedToSomeoneElse.
    /// </summary>
    private static string AllocationIdFound(GameController gc)
    {
        var wanted = new HashSet<uint>();

        foreach (var (_, item) in Ground.VisibleItems(gc))
        {
            var id = Safe.Read(item, static e => e.GetComponent<WorldItem>()?.AllocatedToPlayer ?? 0u, 0u);

            if (id != 0u)
                wanted.Add(id);
        }

        if (wanted.Count == 0)
            return "no item in view is allocated to anybody";

        var memory = gc.Memory;
        var player = gc.Player;
        var blocks = new List<(string Name, long Address, int Size)>
        {
            ("Player component", Safe.Read(player, static e => e.GetComponent<Player>()?.Address ?? 0L, 0L), 0x1000),
            ("player entity", Safe.Read(player, static e => e.Address, 0L), 0x1000),
            ("ServerData", Safe.Read(gc, static g => g.IngameState.ServerData.Address, 0L), 0x20000),
            ("ServerData.PlayerInformation", Safe.Read(gc, static g => g.IngameState.ServerData.PlayerInformation.Address, 0L), 0x1000),
        };

        var found = new List<string>();

        void Search(string name, long address, int size)
        {
            if (address == 0L)
                return;

            var bytes = Safe.Read(() => memory.ReadBytes(address, size), null);

            if (bytes == null)
                return;

            for (var at = 0; at + 4 <= bytes.Length; at += 4)
            {
                var value = BitConverter.ToUInt32(bytes, at);

                if (wanted.Contains(value))
                    found.Add($"{name}+0x{at:X} = {value}");
            }
        }

        foreach (var (name, address, size) in blocks)
            Search(name, address, size);

        // One pointer deep from the two small blocks: the id may sit in a structure they point to.
        foreach (var (name, address, _) in blocks.Take(2))
        {
            if (address == 0L)
                continue;

            var bytes = Safe.Read(() => memory.ReadBytes(address, 0x1000), null);

            for (var at = 0; bytes != null && at + 8 <= bytes.Length; at += 8)
            {
                var pointer = BitConverter.ToInt64(bytes, at);

                if (pointer is > 0x10000L and < 0x7FFFFFFFFFFFL)
                    Search($"[{name}+0x{at:X}]", pointer, 0x400);
            }
        }

        return $"looking for {string.Join(", ", wanted)}: " +
               (found.Count == 0 ? "nowhere searched" : string.Join("; ", found.Distinct().Take(40)));
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

        // Every Tick, whatever the phase, while a click's response is being timed. See ClickResponse.
        ObserveClickResponse(gc);
        ObserveClickTargets(gc);

        // Every Tick, whatever the phase: the pickups in reach the run did not wait for. See InReachPickups.
        StepInReachPickups(gc, settings);

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
        ConfirmInBackground(gc, settings);

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

            case Phase.UnderLabels:
                StepUnderLabels(gc);

                return;

            case Phase.WaitingForLabel:
                // Labels just put back read as gone until the key lands, which would take the pickup as done before a
                // walk to it: until one of the labels that were over it reads shown again. See LabelsBack.
                if (!LabelsBack())
                    return;

                // The item on the cursor, the inventory being open, is the pickup done: its label can linger a moment.
                if (Done(gc) || PickedUpOntoCursor(gc) != null)
                {
                    EndKeyBlock();

                    // The label gone is not the item carried: a party member's pickup takes it the same way. See
                    // ConfirmPickup.
                    _deadline = DateTime.UtcNow + InventoryShowsWithin;

                    if (PickedUpOntoCursor(gc) != null)
                    {
                        _phase = Phase.ConfirmingPickup;
                        ConfirmPickup(gc, settings);

                        return;
                    }

                    // **Confirmed in the background, not waited on.** Waiting held the run for InventoryShowsWithin
                    // whenever the item went to somebody else, since the count then never rises, and every other listed
                    // item in view waited out the second. See ConfirmInBackground.
                    PendingConfirmations.Add((_itemName, _targetMetadata, _carriedAtClick, _deadline));
                    _phase = Phase.Idle;
                    _scannedAt = DateTime.MinValue;
                    ConfirmInBackground(gc, settings);

                    return;
                }

                if (DateTime.UtcNow >= _deadline)
                {
                    EndKeyBlock();
                    _phase = Phase.Idle;
                    _lastOutcome += "; still on the ground when the pickup gave up";

                    return;
                }

                // The walk's start, measured for WalkStartsMeasured and ClickFailed.
                var moving = MovingNow(gc);

                if (_walkExpected && !_walkSeen && moving)
                {
                    _walkSeen = true;
                    WalkStartsMeasured.Add((int)(DateTime.UtcNow - _clickedAt).TotalMilliseconds);

                    if (WalkStartsMeasured.Count > 20)
                        WalkStartsMeasured.RemoveAt(0);
                }

                // **A click that failed**, with the label still there and the character standing: no walk begun
                // ClickFailsAfterMs after the button went down, for an item out of reach; no pickup by then, for one in
                // reach; and for a walk that has ended, no pickup that long after the character stopped, as arriving
                // starts the same pickup an item in reach does. With a retry left (Instant loot retries) it is clicked
                // again, under the key block, so the retry's walk is not cut short. With none, the keyboard is let go and
                // the item given up, so the run goes on to the rest in view rather than waiting out LabelGoneWithin
                // with the player's keys held: a fixed 400ms after the click let the keyboard go before, and nothing
                // else did.
                if (!moving && ClickFailed(gc) is { Length: > 0 } failed)
                {
                    if (RetriesOfItems.GetValueOrDefault(_itemId) < settings.Automation.Autoloot.InstantLootRetries)
                    {
                        RetriesOfItems[_itemId] = RetriesOfItems.GetValueOrDefault(_itemId) + 1;
                        TriedItemIds.Remove(_itemId);
                        _lastOutcome = $"{_itemName}: {failed} - clicked again";
                    }
                    else
                    {
                        EndKeyBlock();
                        _lastOutcome += $"; {failed}, with no retry left - given up, and the keyboard let go";
                    }

                    _scannedAt = DateTime.MinValue;
                    _phase = Phase.Idle;
                }

                return;
        }

        if (!settings.Automation.Enable.Value || !autoloot.EnableInstantAutoloot)
        {
            LetGoWhenNoPickupStarts();
            return;
        }

        if (_input is not { Available: true })
        {
            if (listed.Count > 0)
                RecordListedDecision($"{listed[0].Name}: not clicked - ExileInput2 is not available");

            LetGoWhenNoPickupStarts();
            return;
        }

        // Alone there is no race for an item: it is left for the action key. See DisableInstantAutolootWithoutParty.
        if (autoloot.DisableInstantAutolootWithoutParty.Value && !PartyInArea(gc))
        {
            LetGoWhenNoPickupStarts();
            return;
        }

        // Nothing on the ground is clicked with the inventory open and an item on the cursor: the click would carry the
        // held item off. See PickedUpOntoCursor.
        if (InventoryPanelOpen(gc) && Tablets.ItemOnCursor(gc, true) != null)
        {
            if (listed.Count > 0)
                RecordListedDecision($"{listed[0].Name}: not clicked - the inventory is open with an item on the cursor");

            LetGoWhenNoPickupStarts();
            return;
        }

        var player = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        var covered = Panels.Covered(gc);
        var topFirst = TopFirstNow(gc);
        var cursor = CursorOnScreen(gc);
        (Entity Of, int[] Path, uint Id, string Name, Vector2 At, int Rank, float Away, double Price, float FromCursor)? best = null;

        // Items already in the plan being worked out, passed over as if tried. See PlanInstantLootAhead.
        var setAside = new HashSet<uint>();

        // The best listed item whose label is under other labels and whose own place on screen is clear: taken only when
        // nothing else can be clicked. See StepUnderLabels.
        var underLabelsAllowed = settings.Automation.On(settings.Automation.Unhide);
        (Entity Of, int[] Path, uint Id, string Name, Vector2 At, int Rank, float Away, double Price, float FromCursor)? underLabels = null;

        // Why listed items were passed over, for the dump. See RecordListedDecision.
        int alreadyTried = 0, waitingAfterCovered = 0, withoutRoom = 0, notClickable = 0;

        // Everything clickable and everything from under labels at this look, which the loot order chooses among as a
        // whole, as its price band is measured from the dearest. See BestInLootOrder.
        var band = LootOrderBandExalts(settings);
        var clickable = new List<(Entity Of, int[] Path, uint Id, string Name, Vector2 At, int Rank, float Away, double Price, float FromCursor)>();
        var clickableFacts = new List<LootOrderFacts>();
        var fromUnder = new List<(Entity Of, int[] Path, uint Id, string Name, Vector2 At, int Rank, float Away, double Price, float FromCursor)>();
        var fromUnderFacts = new List<LootOrderFacts>();

        void Consider(Entity of, int[] path, int rank, string name)
        {
            var id = Safe.Read(of, static e => e.Id, 0u);

            if (setAside.Contains(id))
                return;

            if (id == 0u || TriedItemIds.Contains(id))
            {
                alreadyTried++;
                return;
            }

            if (CoveredUntil.TryGetValue(id, out var lookAgain) && DateTime.UtcNow < lookAgain)
            {
                waitingAfterCovered++;
                return;
            }

            // Not one with no room in the inventory, slots kept for it included. Not tried, so it is taken up once there
            // is room. See InventorySpace.
            if (path.Length == 0 && !InventorySpace.Fits(gc, of, 0))
            {
                withoutRoom++;
                return;
            }

            var away = Vector2.Distance(player, Safe.Read(of, static e => e.GridPos, Vector2.Zero));
            var price = path.Length == 0 ? PriceOfItem(gc, of) : 0d;

            // An item within reach needs no walk. See PickupReachGrid.
            if (path.Length == 0)
                away = WalkToItem(away);

            // One that cannot win whatever its price and label is not looked at further: the clear-spot search is the
            // dear part.
            if (LosesInLootOrder(topFirst, rank, away, clickableFacts))
                return;

            var rect = Placement.Button(gc, of, path).Rect;
            var at = Placement.Exposed(gc, covered, rect, id).At;

            // Not clickable now - off screen, behind a panel or under other labels. Not tried, so it is taken up when
            // it can be - or, an item whose label is on screen under other labels, from under them. See StepUnderLabels.
            if (at == Vector2.Zero)
            {
                notClickable++;

                if (underLabelsAllowed && path.Length == 0 && rect.Width > 0f && Placement.OnScreen(gc, rect.Center) &&
                    Safe.Read(of, static e => e.HasComponent<WorldItem>(), false) &&
                    Placement.ScreenOfItem(gc, of) is var lies && lies != Vector2.Zero && Placement.OnScreen(gc, lies) &&
                    !Panels.Covers(covered, lies) && !Panels.Blocked(gc, lies, true))
                {
                    fromUnder.Add((of, path, id, name, lies, rank, away, price, Vector2.Distance(cursor, lies)));
                    fromUnderFacts.Add(new LootOrderFacts(rank, price, away, Vector2.Distance(cursor, lies)));
                }

                return;
            }

            var fromCursor = Vector2.Distance(cursor, at);

            clickable.Add((of, path, id, name, at, rank, away, price, fromCursor));
            clickableFacts.Add(new LootOrderFacts(rank, price, away, fromCursor));
        }

        // ENTITY lines: shattering and chests, ranked by their line; without the line, not done. See RemnantsLine.
        var remnantsRank = RankOfLine(autoloot.InstantLootItems, RemnantsLine);
        var chestsRank = RankOfLine(autoloot.InstantLootItems, ExcavatedChestsLine);

        // Everything in view weighed with the cursor at cursorAt: the best clickable as best, the best from under labels
        // as underLabels. Asked again with the cursor on each planned point to work out the plan. See the plan below.
        void ChooseWithCursorAt(Vector2 cursorAt)
        {
            cursor = cursorAt;
            clickable.Clear();
            clickableFacts.Clear();
            fromUnder.Clear();
            fromUnderFacts.Clear();

            foreach (var (item, _, rank, name) in listed)
                Consider(item, [], rank, name);

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

            var chosenIndex = BestInLootOrder(clickableFacts, topFirst, band);
            var underIndex = BestInLootOrder(fromUnderFacts, topFirst, band);

            best = chosenIndex < 0 ? null : clickable[chosenIndex];
            underLabels = underIndex < 0 ? null : fromUnder[underIndex];
        }

        ChooseWithCursorAt(cursor);

        var fromUnderLabels = best == null && underLabels != null;

        if ((best ?? underLabels) is not { } chosen)
        {
            if (listed.Count > 0)
                RecordListedDecision($"{listed[0].Name}: not clicked - {listed.Count} listed in view, {alreadyTried} already " +
                                     $"tried, {waitingAfterCovered} waiting after being covered, {withoutRoom} without inventory " +
                                     $"room, {notClickable} off screen, behind a panel or under other labels" +
                                     (underLabelsAllowed ? "" : " (\"Use the Toggle Highlighting key when buttons are obscured\" is off, so none is taken from under labels)"));

            LetGoWhenNoPickupStarts();
            return;
        }

        // **Clear in the interface tree, not only of the panels the plugin knows**, before anything is taken: the spot was
        // judged against the fixed furniture and named panels alone, so a Divine Orb under the buff bar was moved to,
        // clicked on the bar, and the player's input blocked for nothing (Stagnant Basin, 2026-10-11 17:26:13). The walk
        // placement makes before its click, about 0.74ms; a covered item waits CoveredWaitMs and is looked at again,
        // untried. See Panels.Blocked.
        if (!fromUnderLabels && Panels.Blocked(gc, chosen.At, true))
        {
            CoveredUntil[chosen.Id] = DateTime.UtcNow + CoveredWait;
            // What covered it, as the dump's cursor line says it, since "the interface" covers chat, a panel left open
            // and the buff bar alike. A second walk, paid only on this path and at most once per CoveredWait.
            _lastOutcome = $"{chosen.Name}: under a piece of the interface - not clicked, looked at again shortly; " +
                           $"at ({chosen.At.X:0},{chosen.At.Y:0}) {Panels.Why(gc, chosen.At)}";
            RecordListedDecision(_lastOutcome);
            LetGoWhenNoPickupStarts();
            return;
        }

        // An ExileInput2 without TakeWithPriority falls back to an ordinary Take, and waits for the cursor to be free.
        if (!(_input.TakeWithPriority(InstantLootPriority) || _input.Take()))
        {
            // **A refusal can be a stop, and a stop is cleared only by Release.** ExileInput2 stops a caller whose claim
            // finds the game out of focus or typing into chat, and refuses every later claim from it until it releases.
            // The idle look releases nothing, as it holds nothing, so one claim made while chat was open refused every
            // listed item for the rest of the area. Released here, the next look claims again. With no stop, the cursor
            // is held by another caller at the same or a higher priority.
            var stopped = _input.Stopped();

            if (stopped.Length > 0)
                _input.Release();

            RecordListedDecision(stopped.Length > 0
                ? $"{chosen.Name}: not clicked - ExileInput2 had stopped instant loot ({stopped}); released, to claim again on the next look"
                : $"{chosen.Name}: not clicked - ExileInput2 refused the cursor, held by another caller");
            LetGoWhenNoPickupStarts();
            return;
        }

        RecordListedDecision($"{chosen.Name}: taken up");

        _itemId = chosen.Id;
        _pickupFromUnderLabels = fromUnderLabels;
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
        _input.SetShortestMove(autoloot.InstantLootShortestMoveMs.Value);
        _input.SetMoveTimePerRootPixel(autoloot.InstantLootMoveTimePerRootPixelMs.Value);

        // **From under other labels: the labels off the screen first**, the move once they read gone. See StepUnderLabels.
        if (fromUnderLabels)
        {
            _labelsOver.Clear();
            _labelsOver.AddRange(Placement.LabelElementsOver(gc, chosen.Of));
            _underLabelsAt = chosen.At;
            _underLabelsMoved = false;
            _notShownSince = null;

            if (!HideLabels(settings))
            {
                TriedItemIds.Remove(chosen.Id);
                CoveredUntil[chosen.Id] = DateTime.UtcNow + CoveredWait;
                _lastOutcome = $"{_itemName}: its label is under other labels, and the tap of Toggle Highlighting was refused";
                Finish();
                return;
            }

            _deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ms) + Placement.HidesWithin;
            _phase = Phase.UnderLabels;
            _clickArmed = false;
            return;
        }

        // **The items after this one, planned with it**, when this is not already the next of the plan in hand: the rest
        // of its line within pickup reach, then its line's nearest out of reach. The cursor glides through them, and is
        // kept between their clicks, since handing it back drops the plan. Each click still waits for the last label to
        // go. Worked out by choosing again with each planned item set aside and the cursor on the one before, as it will
        // be when the run chooses, so the plan is the run's own order.
        _nextAfterWalk = null;

        if (_instantPlanned.Count > 0 && _instantPlanned[0].Id == chosen.Id && _instantPlanned[0].At == chosen.At)
        {
            _instantPlanned.RemoveAt(0);
            SayInstantPlan($"{chosen.Name}: followed the plan, {_instantPlanned.Count} still planned after it");
        }
        else
        {
            var dropped = _instantPlanned.Count > 0
                ? $"the plan's next point was ({_instantPlanned[0].At.X:0},{_instantPlanned[0].At.Y:0}), this is ({chosen.At.X:0},{chosen.At.Y:0}) - "
                : "";

            _instantPlanned.Clear();

            var isItem = chosen.Path.Length == 0 && Safe.Read(chosen.Of, static e => e.HasComponent<WorldItem>(), false);

            if (!isItem)
                SayInstantPlan($"{chosen.Name}: {dropped}not planned - not an item");
            else if (chosen.Away > 0f)
            {
                // The item the run would choose once this one is picked up, for the cursor to go ahead to during the
                // walk. See MoveCursorAheadOfWalk.
                // Chosen with the character where the walk is taken to end, as the run will choose once there: from where
                // it stands the walks differ, and another item of the same name was chosen (dump 2026-10-19 14:15:21).
                // The cursor on this item's label, which the screen's move leaves where it is against the others.
                if (autoloot.MoveCursorAheadDuringWalks.Value)
                {
                    var stands = player;

                    player = WalkEnd(player, Safe.Read(chosen.Of, static e => e.GridPos, Vector2.Zero));
                    setAside.Add(chosen.Id);
                    ChooseWithCursorAt(chosen.At);
                    player = stands;

                    if (best is { } next)
                        _nextAfterWalk = (next.Id, next.Name, next.At);
                }

                SayInstantPlan($"{chosen.Name}: {dropped}not planned - it needs a walk, so it is clicked and its pickup waited for alone");
            }
            else if (InventoryPanelOpen(gc))
                SayInstantPlan($"{chosen.Name}: {dropped}not planned - the inventory is open, so each item is put down from the cursor");
            else
            {
                var points = new List<Vector2> { chosen.At };
                var ids = new List<uint> { chosen.Id };
                var names = new List<string> { chosen.Name };
                var ended = $"planned up to {InstantPlannedMost}, the most one plan holds";

                setAside.Add(chosen.Id);

                while (points.Count < InstantPlannedMost)
                {
                    ChooseWithCursorAt(points[^1]);

                    if (best is not { } next)
                    {
                        ended = "nothing more to click from here";
                        break;
                    }

                    // Top first, a plan keeps to one line: a higher line arriving is chosen at the next look anyway.
                    if (topFirst && next.Rank != chosen.Rank)
                    {
                        ended = $"{next.Name} is on another line of the list";
                        break;
                    }

                    if (next.Path.Length != 0 || !Safe.Read(next.Of, static e => e.HasComponent<WorldItem>(), false))
                    {
                        ended = $"{next.Name} is not an item";
                        break;
                    }

                    setAside.Add(next.Id);
                    points.Add(next.At);
                    ids.Add(next.Id);
                    names.Add(next.Name);

                    // The first needing a walk ends it: the walk moves every label with the camera.
                    if (next.Away > 0f)
                    {
                        ended = $"{next.Name} needs a walk";
                        break;
                    }
                }

                if (points.Count < 2)
                    SayInstantPlan($"{chosen.Name}: {dropped}not planned - only one item ({ended})");
                else
                {
                    var shortest = autoloot.InstantLootShortestMoveMs.Value;
                    var planned = _input.PlanClicks(points.Select(p => new RectangleF(p.X - 3f, p.Y - 3f, 6f, 6f)).ToArray(),
                        points.ToArray(), new bool[points.Count], points.Select(_ => Array.Empty<Keys>()).ToArray(),
                        points.Select(_ => shortest).ToArray(), settings.Automation.ShowPlannedAutomationAutoloot.Value);

                    if (planned)
                    {
                        for (var i = 1; i < points.Count; i++)
                            _instantPlanned.Add((ids[i], points[i]));
                    }

                    SayInstantPlan(planned
                        ? $"{chosen.Name}: {dropped}planned {points.Count} - {string.Join(", ", names)}; ended: {ended}"
                        : $"{chosen.Name}: {dropped}not planned - ExileInput2 refused the plan of {points.Count}");
                }
            }
        }

        // How good a move ahead of the last walk was, before the cursor leaves where it put it. See MoveCursorAheadOfWalk.
        NoteClickAfterWalkAhead(gc, "instant loot", chosen.Id, chosen.Name, chosen.At);

        // **Where the cursor is, when the game says it is on this item already**: the cursor was moved across the label it
        // was on (the player's account, 2026-10-20). The game's label element names the ground item under the cursor by
        // the id aimed at - 9 of 10 clicks read so (dump 18:28:17) - and with nothing in the interface over that point,
        // the click goes there. Not with a plan in hand or being followed: a planned click is matched by its point.
        var aim = chosen.At;

        if (_instantPlanned.Count == 0 && !_cursorKeptForPlan && Placement.ItemOnHoverId(gc) == chosen.Id &&
            CursorOnScreen(gc) is var cursorNow && !Panels.Blocked(gc, cursorNow, true))
            aim = cursorNow;

        _aimedWhereCursorWas = aim != chosen.At;

        // To the point itself, which is what a planned move is matched against. See PlanClicks.
        if (!_input.MoveTo(aim))
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
        _armedAt = aim;
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

    /// <summary>Whether this run took the ground labels off the screen and owes the game the key back, and which key.</summary>
    private static bool _labelsHidden;

    private static Keys _labelsHiddenWith = Keys.Z;

    private static DateTime _labelsHiddenAt = DateTime.MinValue;

    private static DateTime _labelsShownAt = DateTime.MinValue;

    /// <summary>The labels over the item's label when they were taken down: their going and coming back says the key landed.</summary>
    private static readonly List<ExileCore2.PoEMemory.Element> _labelsOver = [];

    /// <summary>Where on screen the item being picked up from under other labels lies, and whether the cursor was sent.</summary>
    private static Vector2 _underLabelsAt;

    private static bool _underLabelsMoved;

    /// <summary>When the arrived cursor was first read as not showing the item's label. See Placement.UnderGrace.</summary>
    private static DateTime? _notShownSince;

    /// <summary>What the last pickup from under other labels did, for the dump.</summary>
    internal static string UnderLabelsSaid { get; private set; } = "has not tried";

    private static bool HideLabels(AutoExpeditionSettings settings)
    {
        if (_labelsHidden)
            return true;

        var key = Safe.Read(() => settings.HighlightKey.Value.Key, Keys.Z);

        if (!_input.Tap(key))
        {
            UnderLabelsSaid = $"{_itemName}: the tap of {key} was refused";
            return false;
        }

        _labelsHidden = true;
        _labelsHiddenWith = key;
        _labelsHiddenAt = DateTime.UtcNow;
        UnderLabelsSaid = $"{_itemName}: its label was under {_labelsOver.Count} other label(s), tapped {key} to hide the labels";

        return true;
    }

    private static void ShowLabels()
    {
        if (!_labelsHidden)
            return;

        _labelsHidden = false;
        _labelsShownAt = DateTime.UtcNow;
        UnderLabelsSaid += _input.Tap(_labelsHiddenWith)
            ? $"; tapped {_labelsHiddenWith} to put them back"
            : $"; the tap of {_labelsHiddenWith} to put them back WAS REFUSED - the labels may still be hidden";
    }

    /// <summary>Whether the labels that were over the item read hidden, or Placement.HidesWithin has passed.</summary>
    private static bool LabelsGone() =>
        DateTime.UtcNow - _labelsHiddenAt >= Placement.HidesWithin ||
        _labelsOver.Count > 0 && _labelsOver.All(e => !Safe.Read(e, static x => x.IsVisible, false));

    /// <summary>Whether a label that was over the item reads shown again, or Placement.HidesWithin has passed since it was put back.</summary>
    private static bool LabelsBack() =>
        DateTime.UtcNow - _labelsShownAt >= Placement.HidesWithin ||
        _labelsOver.Any(e => Safe.Read(e, static x => x.IsVisible, false));

    /// <summary>
    /// A pickup from under other labels, as the action key run makes it (see Placement.UnderLabelsWord): the labels off
    /// the screen, the cursor to the item where it lies once the labels over it read gone, the click once the game says
    /// the cursor is on the item, and the labels back at once. Offered only when no listed item can be clicked as it is.
    /// The click is never armed on arrival: it waits for the game's word. Not shown its label, the item is let go untried
    /// and looked at again after CoveredWait.
    /// </summary>
    private static void StepUnderLabels(GameController gc)
    {
        if (_input.WasStopped())
        {
            _lastOutcome = $"{_itemName}: stopped ({_input.Stopped()}) before the click, from under other labels";
            Finish();
            return;
        }

        if (DateTime.UtcNow >= _deadline)
        {
            _lastOutcome = $"{_itemName}: the cursor did not reach it from under other labels in time - not clicked";
            UnderLabelsSaid += "; ran out of time";
            Finish();
            return;
        }

        if (!_underLabelsMoved)
        {
            if (!LabelsGone())
                return;

            var waited = (DateTime.UtcNow - _labelsHiddenAt).TotalMilliseconds;

            UnderLabelsSaid += _labelsOver.Count == 0
                ? $"; no label over it could be read, so waited the {Placement.HidesWithin.TotalMilliseconds:0}ms ceiling"
                : waited >= Placement.HidesWithin.TotalMilliseconds
                    ? $"; the label(s) over it still read shown at the {Placement.HidesWithin.TotalMilliseconds:0}ms ceiling"
                    : $"; the label(s) over it read hidden after {waited:0}ms";

            // Where it lies now, the camera having had a moment to move.
            if (Placement.ScreenOfItem(gc, _target) is var lies && lies != Vector2.Zero && Placement.OnScreen(gc, lies))
                _underLabelsAt = lies;

            if (!_input.MoveTo(new RectangleF(_underLabelsAt.X - 2f, _underLabelsAt.Y - 2f, 4f, 4f)))
            {
                _lastOutcome = $"{_itemName}: the move was refused";
                Finish();
                return;
            }

            _underLabelsMoved = true;
            return;
        }

        if (!_input.Arrived())
            return;

        if (!Placement.HoverShowsLabelOf(gc, _target))
        {
            if ((_notShownSince ??= DateTime.UtcNow) is var since && DateTime.UtcNow - since < Placement.UnderGrace)
                return;

            TriedItemIds.Remove(_itemId);
            CoveredUntil[_itemId] = DateTime.UtcNow + CoveredWait;
            _lastOutcome = $"{_itemName}: the cursor on it did not show its label - not clicked, looked at again shortly";
            UnderLabelsSaid += $"; the cursor on it did not show its label (the ground labels say item {Placement.ItemOnHoverId(gc)} " +
                               $"is under it, against {_itemId}) - not clicked";
            Finish();
            return;
        }

        var clicked = _input.Click();

        UnderLabelsSaid += clicked ? "; the cursor on it showed its label, clicked it" : "; the click was refused";
        ShowLabels();
        AfterClick(gc, clicked);
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
                _lastOutcome = $"{_itemName}: under a piece of the interface at the click - not clicked, input let go; " +
                               $"{Panels.Why(gc, CursorOnScreen(gc))}";
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
            // The labels read often for a while, so another item landing meanwhile is seen at once. See Ground.FastRefreshForMs.
            Ground.FastRefreshNow();
            _pickupNote = ($"Instant autoloot: {_itemName}", DateTime.UtcNow);
            _clickedAt = DateTime.UtcNow;

            // When the button went down, from ExileInput2 where it answers with a press this last second, else now. See
            // ClickFailsAfterMs.
            var down = _input.LastPressDownAt();
            var since = down > 0 ? System.Diagnostics.Stopwatch.GetElapsedTime(down).TotalMilliseconds : -1d;

            _clickPressedFromExileInput2 = since is >= 0d and < 1000d;
            _clickPressedAt = _clickPressedFromExileInput2 ? down : System.Diagnostics.Stopwatch.GetTimestamp();
            StartClickResponse(gc);
            NoteClickTarget(gc);

            if (_targetPath.Length == 0)
                NoteClicked(gc, _target);

            // What is carried of it now, for ConfirmPickup to compare against.
            _targetMetadata = Safe.Read(_target, static e => e.GetComponent<WorldItem>()?.ItemEntity?.Metadata, "") ?? "";
            _carriedAtClick = CarriedOfMetadata(gc, _targetMetadata);

            // Out of reach, the character walks to it. See ClickFailed.
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

        var aheadOfWalk = clicked && _walkExpected && MoveCursorAheadOfWalk(gc);

        Finish(keepKeysBlocked: clicked, keepCursor: clicked && (_instantPlanned.Count > 0 || aheadOfWalk));

        if (!clicked)
            return;

        // **An item in reach is not waited for.** Its label took 389 to 432ms to go at a ping of 13 to 15, and the run
        // waited that out before every next click, about 600ms an item through a plan (dump 2026-10-18 23:05:51). The
        // game takes the pickup on the button going down, so the next item is clicked at once and this one is checked
        // in the background. Waited for still: a walk, which a click elsewhere would cut short; an item from under other
        // labels, put back over it; and with the inventory open, the item going onto the cursor to be put down first.
        if (!_walkExpected && !_pickupFromUnderLabels && _targetPath.Length == 0 && !InventoryPanelOpen(gc) &&
            Safe.Read(_target, static e => e.HasComponent<WorldItem>(), false))
        {
            InReachPickups.Add((_itemId, _target, _itemName, _clickPressedAt, _targetMetadata, _carriedAtClick));
            EndKeyBlock();
            _scannedAt = DateTime.MinValue;

            return;
        }

        _deadline = DateTime.UtcNow + LabelGoneWithin;
        _phase = Phase.WaitingForLabel;
    }

    /// <summary>
    /// After a click starting a walk: the cursor to where the item chosen to follow it (_nextAfterWalk) is expected once
    /// the walk has ended, so the move after the walk is short. True when the move was sent, and the cursor is then kept
    /// through the walk, as the plan's is; the next look lets it go when it starts no pickup. Moving the mouse stops it in
    /// ExileInput2, as at any other time. See AutolootSettings.MoveCursorAheadDuringWalks and LabelAfterWalk.
    /// </summary>
    private static bool MoveCursorAheadOfWalk(GameController gc)
    {
        if (_nextAfterWalk is not { } next)
            return false;

        _nextAfterWalk = null;

        var walkTo = Safe.Read(_target, static e => e.GridPos, Vector2.Zero);

        if (LabelAfterWalk(gc, walkTo, next.At) is not { } ahead)
        {
            SayInstantPlan($"{_itemName}: needs a walk; cursor not moved ahead of it for {next.Name} - expected off screen after " +
                           "the walk, or the walk's end could not be projected");
            return false;
        }

        var clickedAt = CursorOnScreen(gc);
        var moved = _input.MoveTo(ahead);

        if (moved)
            NoteWalkAheadGuess(gc, "instant loot", next.Id, next.Name, ahead, clickedAt, walkTo);

        SayInstantPlan(moved
            ? $"{_itemName}: needs a walk; cursor moved ahead to ({ahead.X:0},{ahead.Y:0}) for {next.Name}, now at ({next.At.X:0},{next.At.Y:0})"
            : $"{_itemName}: needs a walk; the move ahead for {next.Name} was refused");

        return moved;
    }

    /// <summary>The item chosen to follow one that needs a walk, and its label's point when chosen. See MoveCursorAheadOfWalk.</summary>
    private static (uint Id, string Name, Vector2 At)? _nextAfterWalk;

    /// <summary>
    /// For each pickup in reach not waited for, how long after the button went down its label was seen gone, in ms, and
    /// the ping then, the last InReachPickupTimesKept, for ClickPickupMarginMs. Seen each Tick, so up to a Tick late.
    /// </summary>
    private static readonly List<(int Ms, int Ping)> InReachPickupTimes = new();

    private const int InReachPickupTimesKept = 40;

    /// <summary>Whether the pickup in hand was started from under other labels. See StepUnderLabels.</summary>
    private static bool _pickupFromUnderLabels;

    /// <summary>
    /// Items in reach clicked and not waited for, oldest first: entity id, entity, name, when the button went down (a
    /// Stopwatch timestamp), metadata and what was carried of it at the click. See StepInReachPickups.
    /// </summary>
    private static readonly List<(uint Id, Entity Of, string Name, long PressedAt, string Metadata, int CarriedAtClick)>
        InReachPickups = new();

    /// <summary>
    /// Each pickup in reach the run did not wait for, every Tick: its label gone, confirmed against the inventory in the
    /// background; still there ClickFailsAfterMs after its click, failed - clicked again as Instant loot retries allows,
    /// by putting it back among the untried, or given up.
    /// </summary>
    private static void StepInReachPickups(GameController gc, AutoExpeditionSettings settings)
    {
        for (var i = InReachPickups.Count - 1; i >= 0; i--)
        {
            var pickup = InReachPickups[i];

            if (Placement.Button(gc, pickup.Of, []).Rect.Width <= 0f)
            {
                // The timing a click's response would have given, which closes when the next click starts. See
                // InReachPickupTimes.
                InReachPickupTimes.Add(((int)System.Diagnostics.Stopwatch.GetElapsedTime(pickup.PressedAt).TotalMilliseconds,
                    Safe.Read(gc, static g => g.IngameState.ServerData.Latency, -1)));

                if (InReachPickupTimes.Count > InReachPickupTimesKept)
                    InReachPickupTimes.RemoveAt(0);

                InReachPickups.RemoveAt(i);
                PendingConfirmations.Add((pickup.Name, pickup.Metadata, pickup.CarriedAtClick, DateTime.UtcNow + InventoryShowsWithin));
                continue;
            }

            var since = System.Diagnostics.Stopwatch.GetElapsedTime(pickup.PressedAt).TotalMilliseconds;

            if (since < ClickFailsAfterMs(gc, false))
                continue;

            InReachPickups.RemoveAt(i);

            if (RetriesOfItems.GetValueOrDefault(pickup.Id) < settings.Automation.Autoloot.InstantLootRetries)
            {
                RetriesOfItems[pickup.Id] = RetriesOfItems.GetValueOrDefault(pickup.Id) + 1;
                TriedItemIds.Remove(pickup.Id);
                _scannedAt = DateTime.MinValue;
                _lastOutcome += $"; {pickup.Name}: not picked up {since:0}ms after the click - to be clicked again";
            }
            else
                _lastOutcome += $"; {pickup.Name}: not picked up {since:0}ms after the click, with no retry left - given up";
        }
    }

    /// <summary>
    /// How long the player's keys are blocked from an instant click: long enough for the walk to an item at the edge of
    /// the screen, and ended as soon as the item's label goes. See AutolootSettings.BlockInputForInstantLoot.
    /// </summary>
    private const int KeyBlockMs = 2000;

    /// <summary>When the last instant click's button went down, a Stopwatch timestamp. See AfterClick.</summary>
    private static long _clickPressedAt;

    /// <summary>Whether _clickPressedAt came from ExileInput2 rather than this plugin's frame after the click.</summary>
    private static bool _clickPressedFromExileInput2;

    /// <summary>
    /// Why the click being waited on has failed, or "" while it has not: the character is standing, and ClickFailsAfterMs
    /// has passed since the button went down with no walk begun (an item out of reach) or no pickup (an item in reach),
    /// or since the character stopped after a walk with no pickup. The caller has already found the label there.
    /// </summary>
    private static string ClickFailed(GameController gc)
    {
        var sincePress = System.Diagnostics.Stopwatch.GetElapsedTime(_clickPressedAt).TotalMilliseconds;

        if (_walkExpected && !_walkSeen)
            return sincePress >= ClickFailsAfterMs(gc, true) ? $"no walk began {sincePress:0}ms after the click" : "";

        if (!_walkExpected)
            return sincePress >= ClickFailsAfterMs(gc, false) ? $"not picked up {sincePress:0}ms after the click" : "";

        var sinceStopped = (DateTime.UtcNow - _movedAt).TotalMilliseconds;

        return sinceStopped >= ClickFailsAfterMs(gc, false) ? $"not picked up {sinceStopped:0}ms after the walk ended" : "";
    }

    /// <summary>
    /// How long a click may take to show it worked before it counts as failed: the player's ping (ServerData.Latency, as
    /// Placement.ChoreMissedAfter reads it), plus ClickWalkStartMarginMs for a walk to begin or ClickPickupMarginMs for an
    /// item in reach to be picked up; ClickUnknownPingMs stands in for a ping that cannot be read.
    /// </summary>
    private static double ClickFailsAfterMs(GameController gc, bool walk)
    {
        var ping = Safe.Read(gc, static g => g.IngameState.ServerData.Latency, -1);

        return (ping is > 0 and < 2000 ? ping : ClickUnknownPingMs) + (walk ? ClickWalkStartMarginMs : ClickPickupMarginMs);
    }

    /// <summary>
    /// The margin over the ping for a walk to an item out of reach to begin. Measured: of 7 clicks that walked, the
    /// position first moved 125 to 203ms after the button went down, 109 to 185ms over the ping (14 to 26), each figure in
    /// steps of 15.6ms (dump 2026-10-18 22:48:08). 200 sits just over the slowest. One map; 81 earlier walk starts, timed
    /// from the frame after the click by the half-grid move, reached 261ms at the 99th percentile, so this may be tight.
    /// See ClickResponses for more.
    /// </summary>
    private const int ClickWalkStartMarginMs = 200;

    /// <summary>
    /// The margin over the ping for an item in reach to be picked up. Measured: of 11 pickups in reach, the label went 63
    /// to 375ms after the button went down, 48 to 360ms over the ping (15 to 50), each figure in steps of 15.6ms (dump
    /// 2026-10-18 22:48:08). The 250ms the retry waited before was under 8 of them. 380, just over that slowest, was under
    /// most pickups on the next map: 9 in reach went 389 to 432ms after the button, 374 to 419ms over pings of 13 to 15,
    /// on the Stopwatch clock (dump 2026-10-18 23:05:51), and 7 were clicked again. 480 sits 60 over the slowest of both.
    /// See ClickResponses for more.
    /// </summary>
    private const int ClickPickupMarginMs = 480;

    /// <summary>The ping assumed when ServerData.Latency cannot be read. Chosen.</summary>
    private const int ClickUnknownPingMs = 100;

    /// <summary>Whether this pickup asked for the player's keys blocked, so it lets them go when done.</summary>
    private static bool _keysBlocked;


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

    /// <summary>
    /// **What follows an instant click, and how soon, timed from the button going down.** For setting how soon a click
    /// that started nothing counts as failed: a walk is now seen by a change of position of half a grid (MovingNow),
    /// first seen 16 to 137ms after the click at a ping of 5 to 6 (dump 2026-10-18 22:03:41), and a faster sign would let
    /// a failed click be retried sooner. Each sign is the first Tick on which it differs from its reading at the click:
    /// the Pathfinding component's TargetMovePos, WantMoveToPosition, IsMoving and PathingNodes count, the actor's
    /// isMoving or Moving action, its animation, the position moved 0.1 and 0.5 grid, and the label gone. Timed from
    /// _clickPressedAt on the Stopwatch clock. The first 20, on Environment.TickCount64, moved in steps of 15.6ms; none of
    /// the Pathfinding or actor signs came before the position's own change (dump 2026-10-18 22:48:08). Kept for
    /// ClickResponseFor, the last ClickResponsesKept, for the dump. Reads only.
    /// </summary>
    private static readonly string[] ClickResponseSigns =
        ["path target", "path wants", "path moving", "path nodes", "actor moving", "animation", "moved 0.1", "moved 0.5", "label gone"];

    /// <summary>How long after the button went down a click's response is timed. Chosen.</summary>
    private static readonly long ClickResponseFor = 1500;

    private const int ClickResponsesKept = 20;

    private static readonly List<string> ClickResponses = new();

    private static bool _responseOpen;
    private static long _responseFrom;
    private static bool _responseFromExileInput2;
    private static string[] _responseBaseline;
    private static int[] _responseFirstMs;
    private static Vector2 _responseStartGrid;
    private static string _responseItem = "";

    /// <summary>
    /// The wall clock at the click, which a row is stamped with. Stamped at its close instead, a row read as the click
    /// going out 1.5s after it was decided on - ClickResponseFor, or until the next click - where it had gone within
    /// 10ms (dump 2026-10-20 18:06:20).
    /// </summary>
    private static DateTime _responseClickedAt;
    private static int _responsePing;
    private static float _responseAway;

    /// <summary>
    /// The readings ClickResponseSigns compares, as text so any type compares alike: the first six of them, in order.
    /// </summary>
    private static string[] ClickResponseReadings(GameController gc)
    {
        var player = Safe.Read(gc, static g => g.Player, null);
        var path = Safe.Read(player, static e => e.GetComponent<Pathfinding>(), null);
        var actor = Safe.Read(player, static e => e.GetComponent<Actor>(), null);

        return
        [
            Safe.Read(path, static p => p.TargetMovePos.ToString(), "?"),
            Safe.Read(path, static p => p.WantMoveToPosition.ToString(), "?"),
            Safe.Read(path, static p => p.IsMoving.ToString(), "?"),
            Safe.Read(path, static p => (object)p.PathingNodes is System.Collections.ICollection c ? c.Count.ToString() : "?", "?"),
            Safe.Read(actor, static a => (a.isMoving || a.Action.HasFlag(ActionFlags.Moving)).ToString(), "?"),
            Safe.Read(actor, static a => a.Animation.ToString(), "?"),
        ];
    }

    /// <summary>
    /// Each instant click, followed for ClickResponseFor whatever is clicked after it: the item aimed at, the ground item
    /// the game's label element says the cursor was on at the click, and which of the items visible then went, and when.
    /// ClickResponses closes a click's row at the next click, so a label going after it was not seen. Kept for a stack of
    /// ten dropped one every half second, eight taken and two left after their retries, which the rows could not explain:
    /// a click cancelling the pickup before it, or landing on a newer stack's label, were both suspected (dump 2026-10-20
    /// 18:21:50). Reads only.
    /// </summary>
    private static readonly List<(DateTime At, long PressedAt, uint TargetId, string Name, uint HoveredId,
        Dictionary<uint, string> Visible, List<string> Gone)> ClickTargets = new();

    /// <summary>Whether the click in hand went where the cursor already was, the game naming the item under it. See Tick.</summary>
    private static bool _aimedWhereCursorWas;

    /// <summary>The finished ClickTargets, newest last, for the dump.</summary>
    private static readonly List<string> ClickTargetsSaid = new();

    private static void NoteClickTarget(GameController gc)
    {
        var visible = new Dictionary<uint, string>();

        foreach (var (_, onGround) in Ground.VisibleItems(gc))
        {
            var id = Safe.Read(onGround, static e => e.Id, 0u);

            if (id != 0u)
                visible[id] = NamesOfItem(onGround).BaseName;
        }

        ClickTargets.Add((DateTime.Now, _clickPressedAt, _itemId, _itemName + (_aimedWhereCursorWas ? " (clicked where the cursor was)" : ""),
            Placement.ItemOnHoverId(gc), visible, new List<string>()));
    }

    private static void ObserveClickTargets(GameController gc)
    {
        if (ClickTargets.Count == 0)
            return;

        var now = new HashSet<uint>();

        foreach (var (_, onGround) in Ground.VisibleItems(gc))
            now.Add(Safe.Read(onGround, static e => e.Id, 0u));

        for (var i = ClickTargets.Count - 1; i >= 0; i--)
        {
            var click = ClickTargets[i];
            var ms = (int)System.Diagnostics.Stopwatch.GetElapsedTime(click.PressedAt).TotalMilliseconds;

            foreach (var (id, name) in click.Visible.ToList())
            {
                if (now.Contains(id))
                    continue;

                click.Gone.Add($"{(id == click.TargetId ? "the one aimed at" : $"{name} {id}")} {ms}ms");
                click.Visible.Remove(id);
            }

            if (ms < ClickResponseFor)
                continue;

            ClickTargets.RemoveAt(i);
            ClickTargetsSaid.Add($"{click.At:HH:mm:ss.fff} {click.Name} {click.TargetId}, the game's hovered item {click.HoveredId}; " +
                                 $"gone within {ClickResponseFor}ms: {(click.Gone.Count == 0 ? "nothing" : string.Join(", ", click.Gone))}");

            if (ClickTargetsSaid.Count > ClickResponsesKept)
                ClickTargetsSaid.RemoveAt(0);
        }
    }

    /// <summary>Begins timing what follows this click, closing the last one first. See ClickResponseSigns.</summary>
    private static void StartClickResponse(GameController gc)
    {
        if (_responseOpen)
            CloseClickResponse();

        _responseFromExileInput2 = _clickPressedFromExileInput2;
        _responseFrom = _clickPressedAt;
        _responseBaseline = ClickResponseReadings(gc);
        _responseFirstMs = Enumerable.Repeat(-1, ClickResponseSigns.Length).ToArray();
        _responseStartGrid = Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero);
        _responseItem = _itemName;
        _responseClickedAt = DateTime.Now;
        _responsePing = Safe.Read(gc, static g => g.IngameState.ServerData.Latency, -1);
        _responseAway = Vector2.Distance(_responseStartGrid, Safe.Read(_target, static e => e.GridPos, Vector2.Zero));
        _responseOpen = true;

        // A sign already changed by the time this frame read it is timed now. See ObserveClickResponse.
        ObserveClickResponse(gc);
    }

    /// <summary>Notes each sign the first time it differs from its reading at the click. See ClickResponseSigns.</summary>
    private static void ObserveClickResponse(GameController gc)
    {
        if (!_responseOpen)
            return;

        var ms = (int)System.Diagnostics.Stopwatch.GetElapsedTime(_responseFrom).TotalMilliseconds;
        var now = ClickResponseReadings(gc);

        for (var i = 0; i < now.Length; i++)
        {
            if (_responseFirstMs[i] < 0 && now[i] != _responseBaseline[i])
                _responseFirstMs[i] = ms;
        }

        var moved = Vector2.Distance(_responseStartGrid, Safe.Read(gc, static g => g.Player.GridPos, Vector2.Zero));

        if (_responseFirstMs[6] < 0 && moved > 0.1f)
            _responseFirstMs[6] = ms;

        if (_responseFirstMs[7] < 0 && moved > 0.5f)
            _responseFirstMs[7] = ms;

        if (_responseFirstMs[8] < 0 && Done(gc))
            _responseFirstMs[8] = ms;

        if (ms >= ClickResponseFor || _responseFirstMs.All(x => x >= 0))
            CloseClickResponse();
    }

    private static void CloseClickResponse()
    {
        _responseOpen = false;

        var signs = string.Join(", ", ClickResponseSigns.Select((name, i) =>
            $"{name} {(_responseFirstMs[i] < 0 ? "-" : _responseFirstMs[i] + "ms")}"));

        ClickResponses.Add($"{_responseClickedAt:HH:mm:ss.fff} {_responseItem}, {_responseAway:0.#} grid " +
                           $"({(_responseAway > PickupReachGrid ? "walk expected" : "in reach")}), ping {_responsePing}, from " +
                           $"{(_responseFromExileInput2 ? "the button going down" : "this plugin's frame after the click")}: {signs}; " +
                           $"at the click: {string.Join(" | ", _responseBaseline)}");

        if (ClickResponses.Count > ClickResponsesKept)
            ClickResponses.RemoveAt(0);
    }

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
    /// Pickups whose label has gone, still to be confirmed against the inventory while the run looks at other items,
    /// oldest first. See ConfirmInBackground.
    /// </summary>
    private static readonly List<(string Name, string Metadata, int CarriedAtClick, DateTime Until)> PendingConfirmations = new();

    /// <summary>
    /// ConfirmPickup for each pickup whose label has gone, run on every Tick whatever the run is doing: success once more
    /// of the item is carried than at its click, failure when Until passes without it. Two of the same metadata in flight
    /// compare against their own counts at their clicks, so one arriving can read as both: a failure beside a success of
    /// the same item can be sounded as a success. See AfterClick.
    /// </summary>
    private static void ConfirmInBackground(GameController gc, AutoExpeditionSettings settings)
    {
        for (var i = 0; i < PendingConfirmations.Count; i++)
        {
            var pending = PendingConfirmations[i];
            var carried = CarriedOfMetadata(gc, pending.Metadata);

            if (pending.CarriedAtClick < 0 || carried < 0)
            {
                PendingConfirmations.RemoveAt(i--);
                continue;
            }

            if (carried > pending.CarriedAtClick)
            {
                PendingConfirmations.RemoveAt(i--);
                _lastOutcome += $"; {pending.Name} picked up";
                SoundForResult(settings, true);
                continue;
            }

            if (DateTime.UtcNow < pending.Until)
                continue;

            PendingConfirmations.RemoveAt(i--);
            _lastOutcome += $"; {pending.Name}: the label went but no more was carried after " +
                            $"{InventoryShowsWithin.TotalMilliseconds:0}ms - taken by someone else, or no room";
            SoundForResult(settings, false);
        }
    }

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
            // A stop refuses every claim until released. See the claim in Tick.
            if (_input.Stopped().Length > 0)
                _input.Release();

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

    /// <summary>
    /// What a look that starts no pickup lets go of. A key block kept from a click for its retry: the retry keeps the
    /// block so its walk is not cut short (see InstantLootRetries), and with the item gone off screen - a dodge roll away
    /// from it - nothing was clicked and the player's keys stayed blocked for the rest of KeyBlockMs with the run idle.
    /// And the cursor kept for a plan whose next item is no longer to be clicked. See Finish.
    /// </summary>
    private static void LetGoWhenNoPickupStarts()
    {
        if (_keysBlocked)
        {
            EndKeyBlock();
            _lastOutcome += "; nothing more to click, so the keyboard was let go";
        }

        if (_cursorKeptForPlan)
            Finish();
    }

    /// <summary>
    /// The instant loot plan in hand: the items after the one being picked up, by entity id and the point planned for
    /// each, in order. The next look follows it while it chooses the next of them at that point; anything else drops it.
    /// See the plan in Tick.
    /// </summary>
    private static readonly List<(uint Id, Vector2 At)> _instantPlanned = new();

    /// <summary>Whether the cursor was kept after a click, for the plan's next one or moved ahead of a walk. See Finish.</summary>
    private static bool _cursorKeptForPlan;

    /// <summary>The most clicks one instant loot plan holds, as the action key run's do. Chosen.</summary>
    private const int InstantPlannedMost = 12;

    /// <summary>What each instant loot pickup did about the plan, newest last, kept across areas. See SayInstantPlan.</summary>
    private static readonly List<string> InstantPlans = new();

    /// <summary>How many instant loot plan decisions are kept for the dump. Chosen.</summary>
    private const int InstantPlansKept = 40;

    private static void SayInstantPlan(string said)
    {
        InstantPlans.Add($"{DateTime.Now:HH:mm:ss.fff} {said}");

        if (InstantPlans.Count > InstantPlansKept)
            InstantPlans.RemoveAt(0);
    }

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

    /// <summary>
    /// Puts this caller's move speeds back, lets go of the cursor and goes idle. With keepCursor, after a click with a
    /// plan still in hand, the cursor and speeds are kept for the plan's next click, as handing the cursor back drops the
    /// plan. See _instantPlanned.
    /// </summary>
    private static void Finish(bool keepKeysBlocked = false, bool keepCursor = false)
    {
        _phase = Phase.Idle;

        if (keepCursor && _input != null)
        {
            _cursorKeptForPlan = true;

            if (!keepKeysBlocked)
                EndKeyBlock();

            return;
        }

        _cursorKeptForPlan = false;
        _instantPlanned.Clear();

        if (_input == null)
            return;

        // Labels taken down for a pickup are put back on every way out. See StepUnderLabels.
        ShowLabels();

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
                     $"{settings.Automation.Autoloot.MouseBlockMs.Value}ms, instant move {settings.Automation.Autoloot.InstantLootShortestMoveMs.Value}ms + " +
                     $"{settings.Automation.Autoloot.InstantLootMoveTimePerRootPixelMs.Value}ms per root pixel; action key move " +
                     $"{settings.Automation.Autoloot.AutolootShortestMoveMs.Value}ms + " +
                     $"{settings.Automation.Autoloot.AutolootMoveTimePerRootPixelMs.Value}ms per root pixel");
        b.AppendLine($"  instant loot run: {_phase}, {TriedItemIds.Count} item(s) tried this area, " +
                     $"{RetriesOfItems.Values.Sum()} retry click(s), keys blocked {_keysBlocked}, last: {_lastOutcome}");
        b.AppendLine($"  instant loot from under other labels, last: {UnderLabelsSaid}");
        b.AppendLine($"  instant loot, the last look with a listed item in view: {_lastListedDecision}");

        lock (FirstLooks)
        {
            b.AppendLine($"  listed items, from the first look at each to its first match, newest last ({ListedItemTimings.Count}):");

            foreach (var line in ListedItemTimings)
                b.AppendLine("    " + line);
        }
        b.AppendLine($"  instant loot plan in hand: {(_instantPlanned.Count == 0 ? "none" : string.Join(" ", _instantPlanned.Select(p => $"({p.At.X:0},{p.At.Y:0})")))}, " +
                     $"cursor kept for it {_cursorKeptForPlan}; pickups in reach not waited for, still in flight " +
                     $"{InReachPickups.Count}, confirmations pending {PendingConfirmations.Count}; plan decisions, newest last " +
                     $"({InstantPlans.Count}):");

        foreach (var said in InstantPlans)
            b.AppendLine($"    {said}");

        var planEnds = _input?.PlanEndsSaid() ?? [];

        b.AppendLine($"  ExileInput2's plans for instant loot, how each ended, newest last ({planEnds.Length}):" +
                     (planEnds.Length == 0 ? " none, or an ExileInput2 too old to say" : ""));

        foreach (var said in planEnds)
            b.AppendLine($"    {said}");

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
                     $"; a click counts as failed after the ping + {ClickWalkStartMarginMs}ms without one");
        b.AppendLine($"  what followed each instant click, stamped with the click's time, and how soon, newest last ({ClickResponses.Count}; - is not seen " +
                     $"within {ClickResponseFor}ms; the readings at the click are path target | path wants | path moving | " +
                     "path nodes | actor moving | animation):");

        foreach (var said in ClickResponses)
            b.AppendLine($"    {said}");

        b.AppendLine($"  each instant click followed for {ClickResponseFor}ms whatever came after it - the item aimed at, the item " +
                     $"the game said the cursor was on, and which items in view went - newest last ({ClickTargetsSaid.Count}):");

        foreach (var said in ClickTargetsSaid)
            b.AppendLine($"    {said}");

        b.AppendLine($"  pickups in reach not waited for, label gone after the button went down, ms (ping), newest last " +
                     $"({InReachPickupTimes.Count}; failed at the ping + {ClickPickupMarginMs}ms): " +
                     (InReachPickupTimes.Count == 0 ? "none yet" : string.Join(", ", InReachPickupTimes.Select(t => $"{t.Ms} ({t.Ping})"))));
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
        b.AppendLine($"  items in view, who may pick each up (ExileCore2's label and WorldItem); the local player's loot id " +
                     $"{OwnLootId(gc)} (Player.AllocatedLootId):");

        foreach (var line in AllocationSaid(gc))
            b.AppendLine($"    {line}");

        b.AppendLine($"  where an item's allocation id appears in the local player's memory: {AllocationIdFound(gc)}");

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
                    : $"  {listName} list line {line.Rank + 1}: {line.Line} = name {line.Name ?? "any"}, class " +
                      $"{(line.ItemClasses == null ? "any" : string.Join(" or ", line.ItemClasses))}, metadata from " +
                      $"{line.MetadataStart ?? "any"}, art {line.ArtName ?? "any"}, at least {(line.Unit == null ? "any price" : ExaltsOfMin(line) is { } least ? $"{least:0.##}ex" : $"? ({line.Unit} rate not known yet)")}" +
                      (line.GearKinds == null ? "" : $", gear {string.Join(" or ", line.GearKinds)}") +
                      (line.MinItemLevel == null ? "" : $", item level at least {line.MinItemLevel}") +
                      (line.MinLevel == null ? "" : $", base level at least {line.MinLevel}") +
                      (line.MinSockets == null ? "" : $", sockets at least {line.MinSockets}") +
                      (line.Defences == null ? "" : $", defences {string.Join(" or ", line.Defences)}"));
            }
        }

        foreach (var list in new[] { settings.Automation.Autoloot.InstantLootItems, settings.Automation.Autoloot.ItemsOnActionKey })
        {
            foreach (var (item, label, rank, name) in ListedOnGround(gc, list))
            {
                var id = Safe.Read(item, static e => e.Id, 0u);
                var rect = Safe.Read(label, static l => l.Label?.GetClientRect() ?? default, default);

                // What the runs take as over its label, so a label clicked while hidden, or refused while clear, can be
                // read against what the player saw. See Placement.CoveringRectsOfLabel.
                var over = rect.Width > 0f ? Placement.LabelsOver(gc, rect, id) : [];

                b.AppendLine($"    on the ground: \"{name}\" (line {rank + 1} of the {(list == settings.Automation.Autoloot.InstantLootItems ? "instant" : "action key")} list), " +
                             $"id {id}{(TriedItemIds.Contains(id) ? ", tried" : "")}, label ({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}), " +
                             (over.Count == 0 ? "nothing taken as over it" : "taken as over it: " + string.Join("; ", over)));
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
