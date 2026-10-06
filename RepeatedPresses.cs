using System;
using System.Collections.Generic;

namespace AutoExpedition;

/// <summary>
/// Presses the same search over the same cold site several times, so a configuration is judged on a
/// distribution rather than on whichever press somebody happened to look at.
///
/// **Repeated presses are not repeated samples, which is the whole reason this exists.** A solve
/// inherits the previous plan as a floor and the site's best chain off disk, so pressing the action
/// key three times measures one search and two continuations of it - on the site this was written
/// for, 9,506 then 9,506 then 9,506, every repeat adding nothing. Reading that as three samples is
/// how four wrong diagnoses survived a session.
///
/// So each repeat starts from the same state as the first: the plan and the best chain on file are
/// cleared between them, and the markers and the routed ground are kept. That is the same fairness
/// the bake-off uses between strategies and for the same measured reason - each solve floods ground
/// and keeps it, so a run late in the order inherits the routing its predecessors paid for, worth
/// sixty five points on one measurement.
///
/// The numbers land in <see cref="PressHistory"/>, which the dump prints as a distribution. This
/// class only decides when to press.
/// </summary>
internal sealed class RepeatedPresses
{
    /// <summary>How many are left to start, including the one running.</summary>
    private int _left;

    /// <summary>How many the batch asked for, for the readout.</summary>
    private int _asked;

    /// <summary>Whether a press is in flight and the next one is waiting for it.</summary>
    private bool _inFlight;

    /// <summary>Whether the first press of this batch is still to start. See Begin.</summary>
    private bool _fresh;

    /// <summary>The plain score a press stops at, or nought. See DebugSettings.RepeatStopAtScore.</summary>
    private double _target;

    /// <summary>When the press in flight started, and how long each press that reached the target took.</summary>
    private DateTime _pressStarted;

    private readonly List<double> _reachedAfter = new();

    /// <summary>The plain score a press of this batch stops at, or nought for none.</summary>
    public double Target => _target;

    /// <summary>When the press in flight started. A chain published before then is not this press's own.</summary>
    public DateTime PressStarted => _pressStarted;

    /// <summary>Whether anything is being measured right now.</summary>
    public bool Running => _left > 0 || _inFlight;

    /// <summary>What the batch is doing, for the dump and the status line.</summary>
    public string Said { get; private set; } = "not run";

    /// <summary>
    /// Starts a batch. The caller has already cleared the site and is waiting for the sweep.
    ///
    /// **The history is emptied at the first press rather than here, because of the casualty.** The
    /// clear that precedes this cancels whatever search was in flight, and a cancelled search still
    /// returns and files itself - measured, a press of 12 rounds over 8 workers in 3.8s with the pool's
    /// best, median and worst all 7,575, landing in the batch after the history had been emptied. It is
    /// dropped by emptying one moment later instead. Presses made by hand afterwards join the batch and
    /// are told apart by their draw and their timestamp.
    /// </summary>
    public void Begin(int howMany, int from = 0, double target = 0d)
    {
        _asked = Math.Max(1, howMany);
        _left = _asked;
        _inFlight = false;
        _fresh = true;
        _target = Math.Max(0d, target);
        _reachedAfter.Clear();

        // **Every batch walks the same draws, so two batches are a matched pair.**
        //
        // A press is deterministic given its draw - every seed in the search is a constant plus the
        // worker's number plus this - so batch A's third press and batch B's third press differ only in
        // the configuration. That is common random numbers, and without it the draw luck lands in the
        // comparison instead of cancelling out of it: measured, two ten press batches over DIFFERENT
        // draws returned medians of 9,779 and 9,332 inside a spread of 2,600, which resolves nothing
        // about a change worth a few hundred points. See PlanEnvironment.Draw.
        Planning.Draws = Math.Max(0, from);

        Said = $"about to press {_asked} time(s) over this site" +
               (from > 0 ? $", from draw {from + 1}" : "");
    }

    /// <summary>
    /// Called every tick while a batch is live and no search is in flight.
    ///
    /// <paramref name="press"/> clears what a cold start has to clear, starts one solve, and answers
    /// whether a search is actually in flight afterwards. It is the caller's because the clearing needs
    /// half the plugin's objects and this class holds none of them.
    ///
    /// **A refused solve has to stop the batch rather than be counted.** Planning.Start can decline -
    /// no blast radius read, no explosives in hand, nothing in the site - and it declines instantly, so
    /// a batch that treated every press as started would run all of them out in three frames and file
    /// nothing. See Planning.Searching, which is what the answer reads.
    /// </summary>
    public void Tick(Func<bool> press)
    {
        // The press that was in flight has finished - PressHistory has it by now, because Solving
        // files a press as the search returns and before the plan is handed back.
        if (_inFlight)
        {
            _inFlight = false;

            if (_left <= 0)
            {
                Said = $"finished {_asked} press(es){Reaching()} - see the distribution in the dump";

                return;
            }
        }

        if (_left <= 0)
            return;

        _left--;
        _inFlight = true;

        // Now, so the search cancelled by the clear at the key press is not counted as one of these.
        if (_fresh)
        {
            _fresh = false;

            PressHistory.Forget();
        }

        Said = $"press {_asked - _left} of {_asked}{Reaching()}";
        _pressStarted = DateTime.UtcNow;

        if (press())
            return;

        _left = 0;
        _inFlight = false;
        Said = $"stopped at press {_asked - _left} of {_asked} - the solve was refused";
    }

    /// <summary>Notes that the press in flight reached the target, which the caller then stops.</summary>
    public void Reached() =>
        _reachedAfter.Add((DateTime.UtcNow - _pressStarted).TotalSeconds);

    /// <summary>How the presses so far did against the target, or nothing without one.</summary>
    private string Reaching() =>
        _target <= 0d
            ? ""
            : $"; {_reachedAfter.Count} reached {_target:N0}" +
              (_reachedAfter.Count == 0
                  ? ""
                  : $", after {string.Join(", ", _reachedAfter.ConvertAll(x => x.ToString("0.0") + "s"))}");

    /// <summary>Stops a batch without finishing it, for an area change or a placement run.</summary>
    public void Abandon(string why)
    {
        if (!Running)
            return;

        _left = 0;
        _inFlight = false;

        Said = $"stopped after {_asked - _left} press(es) - {why}";
    }
}
