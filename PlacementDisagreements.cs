using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// Every time the client did something with a placement that the model did not predict.
///
/// Two shapes, both faults in the same thing. The game put the explosive on a cell other than the
/// one the aim was computed to land on; or the game refused the cell outright, showing the
/// indicator red at a spot the plan had already decided was placeable.
///
/// **The run stops on either rather than replanning.** A re-solve moves the cursor on to a
/// different pair, so the disagreement goes unlooked-at and the model keeps the fault. Stopping is
/// what makes the evidence still be on screen when somebody comes to look.
///
/// What this holds that the per-frame readings do not: those record the pairs the client produced,
/// cursor to landing, and they are plentiful. None of them knows which cell the PLAN wanted or what
/// the run aimed at to get there, and without those a reading is just another pair rather than a
/// failure.
/// </summary>
internal sealed class PlacementDisagreements
{
    public static readonly PlacementDisagreements Here = new();

    /// <param name="Link">Which link of the chain this was, counting from one.</param>
    /// <param name="Spot">The cell the plan chose.</param>
    /// <param name="Aim">The cell the cursor was sent to in order to land on Spot.</param>
    /// <param name="From">The explosive the game routes from, which is the chain's head.</param>
    /// <param name="Landing">Where the placement indicator stood when the disagreement was read.</param>
    /// <param name="Reach">The placement range at the time, in grid units.</param>
    /// <param name="AimedPastSpot">Whether the aim was a point past the spot rather than the spot.</param>
    /// <param name="Refused">
    /// Whether the game showed the indicator red, rather than accepting the placement somewhere
    /// other than the spot. The two have different causes and want reading apart.
    /// </param>
    internal readonly record struct Disagreement(int Link, Vector2 Spot, Vector2 Aim, Vector2 From,
        Vector2 Landing, float Reach, bool AimedPastSpot, bool Refused);

    private readonly List<Disagreement> _seen = [];
    private uint _area;

    /// <summary>The game accepted a placement, on a cell other than the one the aim was computed for.</summary>
    public void NoteWrongSpot(int link, Vector2 spot, Vector2 aim, Vector2 from, Vector2 landing,
        float reach) =>
        Add(link, spot, aim, from, landing, reach, false);

    /// <summary>The game showed the indicator red at a spot the plan had decided was placeable.</summary>
    public void NoteRefusal(int link, Vector2 spot, Vector2 aim, Vector2 from, Vector2 landing,
        float reach) =>
        Add(link, spot, aim, from, landing, reach, true);

    private void Add(int link, Vector2 spot, Vector2 aim, Vector2 from, Vector2 landing, float reach,
        bool refused)
    {
        lock (_seen)
        {
            // Bounded, because a site that disagrees once usually disagrees on every press, and a
            // list nobody trims is a list that grows for as long as the game is open. The oldest go
            // first: the interesting failure is the one that just happened.
            if (_seen.Count >= Kept)
                _seen.RemoveAt(0);

            _seen.Add(new Disagreement(link, spot, aim, from, landing, reach,
                Cell(aim) != Cell(spot), refused));
        }
    }

    /// <summary>
    /// The disagreements, worst first, with the numbers needed to reproduce one by hand.
    ///
    /// Sorted by how far the landing was from the spot rather than by when it happened, because a
    /// whole-cell disagreement and a sixty-grid one have different causes and the second is the one
    /// worth opening first. A refusal sorts by the same distance, which is usually nought - the
    /// indicator is standing on the spot and the game is simply saying no.
    /// </summary>
    public string Describe()
    {
        List<Disagreement> seen;

        lock (_seen)
            seen = [.. _seen];

        if (seen.Count == 0)
            return "the client agreed with the model on every placement attempted";

        var text = new StringBuilder();

        text.Append(seen.Count).Append(seen.Count == 1 ? " placement" : " placements")
            .AppendLine(" the model did not predict");
        text.AppendLine("  link  spot         aim          from         landing      off   reach  verdict");

        foreach (var one in seen.OrderByDescending(d => Vector2.Distance(d.Landing, d.Spot)))
            text.Append("  ")
                .Append(one.Link.ToString(CultureInfo.InvariantCulture).PadLeft(4))
                .Append("  ").Append(At(one.Spot))
                .Append(' ').Append(At(one.Aim))
                .Append(' ').Append(At(one.From))
                .Append(' ').Append(At(one.Landing))
                .Append(' ').Append(Vector2.Distance(one.Landing, one.Spot)
                    .ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5))
                .Append(' ').Append(one.Reach.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(6))
                .Append("  ").Append(one.Refused ? "refused" : "accepted elsewhere")
                .AppendLine(one.AimedPastSpot ? ", aimed past the spot" : ", aimed at the spot");

        // What separates a model that is slightly wrong from one answering a different question.
        // An aim past the spot is the routing model choosing a relocation, so a miss is in the cut
        // or the endpoint refinement; an aim straight at the spot that still misses is the client
        // moving a request the model believed it would take. A refusal is neither - it is the
        // placeable test being wrong about a cell, which is usually ground nothing has an entity
        // for.
        var refused = seen.Count(d => d.Refused);
        var past = seen.Count(d => !d.Refused && d.AimedPastSpot);

        text.Append("  ").Append(refused).Append(" refused, ")
            .Append(past).Append(" accepted elsewhere after aiming past the spot, ")
            .Append(seen.Count - refused - past)
            .AppendLine(" accepted elsewhere after aiming at it");

        return text.ToString();
    }

    /// <summary>
    /// Dropped on leaving the area, because every cell reference here is area-local and one carried
    /// across says the model failed at a coordinate that now means somewhere else.
    /// </summary>
    public void AreaChange(uint areaHash)
    {
        if (areaHash == _area)
            return;

        _area = areaHash;

        lock (_seen)
            _seen.Clear();
    }

    /// <summary>How many disagreements are kept before the oldest is dropped.</summary>
    private const int Kept = 200;

    private static string At(Vector2 at) =>
        $"({at.X,5:0},{at.Y,5:0})";

    private static (int X, int Y) Cell(Vector2 at) =>
        ((int)MathF.Round(at.X), (int)MathF.Round(at.Y));
}
