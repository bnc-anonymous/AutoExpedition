namespace AutoExpedition;

/// <summary>
/// What the router can say about a pair of spots.
///
/// **"No" and "I have not looked" were the same answer, and that was the most expensive bug in the
/// planner.** Routing costs time, the allowance accrues as a solve runs, and a question asked before
/// there is budget for it came back false - which every caller read as "that link is impossible".
///
/// It cost a different thing each time and looked like a different bug each time: a second solve
/// beating the first, a chain of nineteen links where twenty were available, six of eight henges
/// apparently unreachable, and an exact solve that gave up and fell back to searching for a hundred
/// and twenty points. Each was fixed at the surface. This is the shared cause.
///
/// The distinction is small and it matters: a check deciding whether to PLACE must stay pessimistic,
/// because placing where you cannot confirm is how a chain ends up unplaceable. A builder deciding
/// whether a route EXISTS should wait and ask again, because concluding impossibility from an
/// unanswered question throws away chains that are perfectly legal.
/// </summary>
internal enum Certainty
{
    /// <summary>The ground has been walked and there is no way through inside the reach.</summary>
    No,

    /// <summary>The ground has been walked and there is.</summary>
    Yes,

    /// <summary>Nobody has looked. Not an answer, and never cached or treated as one.</summary>
    Unknown,
}
