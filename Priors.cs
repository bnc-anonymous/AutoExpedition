namespace AutoExpedition;

/// <summary>
/// What to assume a newly discovered object is worth, before anybody has looked at it.
///
/// **A nominal one is the safe answer and a wasteful one.** Everything the plugin has never met is
/// filed at Unknowns.Default - enough to exist on screen, far too little to bend a chain - which is
/// right when nothing at all is known about the thing. It is not right when something is: a marker
/// the classifier placed as a sub-area entrance is a sub-area entrance whether or not this
/// particular tileset's art has been seen before, and there is no reason to make somebody price
/// each new one from scratch when the last four were all worth about the same.
///
/// So this is the shipped guess per kind, and the one place to add the next one. The pattern is
/// always the same: the plugin knows something general about a thing and nothing specific, so it
/// starts from the general and lets anybody who knows better say so.
///
/// **A guess that is written down beats a guess that is not.** The alternative to this file is the
/// old one - a single "Sub-area entrance" weight standing in for every entrance in the game, which
/// looked like a decision and was an average of things nobody had compared. One row per entrance
/// with a sensible starting number says the same thing honestly and can be disagreed with per
/// tileset.
///
/// Room to grow, in the order it will probably be wanted: the generic monster metadata that comes up
/// every league and is worth a normal monster; chests whose art is new but whose minimap icon is not;
/// anything whose name the game states plainly. None of those are here yet, and adding one means
/// adding a case below rather than a code change anywhere else.
/// </summary>
internal static class Priors
{
    /// <summary>
    /// What a sub-area entrance is worth until somebody says otherwise.
    ///
    /// The number the single generic entrance weight shipped at, kept so that replacing one setting
    /// with a row per tileset changes nothing about how a site plans on the day it lands.
    /// </summary>
    public const float Entrance = 20f;

    /// <summary>
    /// The starting weight for a kind, or nothing where the plugin has no opinion.
    ///
    /// Nothing is the ordinary answer. A guess is only worth making where the classifier has placed
    /// the object into a family whose members are alike - which is true of entrances, and is not
    /// true of the relics and scenery that make up most of what gets discovered.
    /// </summary>
    public static float? Weight(TargetKind kind) => kind switch
    {
        TargetKind.Entrance => Entrance,
        _ => null,
    };

    /// <summary>
    /// What to call a discovered object of this kind, in front of whatever names the one.
    ///
    /// **So a row reads "Sub-area entrance: volcanocap" rather than "volcanocap".** A key is spelt
    /// from metadata and art, and art is a file name - it identifies the object without saying what
    /// it is. The kind is the half that says what it is, and it is known at the moment the thing is
    /// filed even when nothing else about it is.
    /// </summary>
    public static string Called(TargetKind kind) => kind switch
    {
        TargetKind.Entrance => "Sub-area entrance",
        TargetKind.Hatch => "Siren egg",
        TargetKind.Relic => "Relic",
        TargetKind.Scenery => "Scenery",
        TargetKind.Strongbox => "Strongbox",
        TargetKind.Caged => "Encounter",
        TargetKind.Monolith => "Monolith",
        TargetKind.Chest => "Chest",
        TargetKind.Monster or TargetKind.Elite => "Monster",
        TargetKind.Sentry => "Verisium Sentry",
        _ => "",
    };

    /// <summary>
    /// What to call a row, from its kind if that was recorded and from its key if it was not.
    ///
    /// **The kind is the better answer and is often missing.** It is written down when an object is
    /// filed, so anything filed before that existed has none - and an EFFECT never will, because an
    /// effect is a modifier a relic grants rather than a thing standing in the dig site.
    ///
    /// So the key answers when the kind cannot. A key begins with the metadata path, which is the
    /// game's own filing, and an effect's key is the modifier id - both say plainly what sort of
    /// thing they are if you read the right part. That is a string match and it will be wrong about
    /// something eventually; it is a label rather than a weight, so being wrong costs a word.
    /// </summary>
    public static string Called(string key, TargetKind kind)
    {
        var known = Called(kind);

        if (known.Length > 0)
            return known;

        if (string.IsNullOrEmpty(key))
            return "";

        // The metadata, or the whole id when this is an effect rather than an object.
        var at = key.IndexOf('|');
        var meta = at < 0 ? key : key[..at];

        bool Has(string word) => meta.Contains(word, System.StringComparison.OrdinalIgnoreCase);

        // Ordered most specific first. A hidden encounter chest is a chest, so chests are asked
        // about before encounters; a relic's modifier id and the relic itself both say Relic.
        return Has("ExpeditionRelic") ? "Relic"
            : Has("Chest") ? "Chest"
            : Has("Destructable") || Has("Destructible") ? "Destructible"
            : Has("SubareaEntrance") || Has("KaruiGate") || Has("BossCave") ? "Sub-area entrance"
            : Has("Encounter") ? "Encounter"
            : Has("Sentinel") ? "Verisium Sentry"
            : Has("/Monsters/") ? "Monster"

            // **And nothing, where the only thing left to say is "Expedition".**
            //
            // That was the last branch here, and it matched almost everything: every object in a dig
            // site sits under Leagues/Expedition somewhere in its path, so the fallback that was
            // meant for the odd unclassifiable thing labelled most of the list. "Expedition:
            // devoureregg_01" in a plugin about expeditions says precisely as much as
            // "devoureregg_01" does, while taking up the space a real classification would occupy
            // and reading as though one had been made.
            //
            // A prefix is a claim about what something IS. Where there is no claim to make, the name
            // stands on its own and the Kind column carries what little is known.
            : "";
    }
}
