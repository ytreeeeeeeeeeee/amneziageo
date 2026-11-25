namespace AmneziaGeo.Routing;

/// <summary>Where a destination's verdict was learned.</summary>
public enum RouteDecisionSource
{
    Default,
    Address,
    Domain,
    Dns,
    Application,
}

/// <summary>A verdict with the source that takes precedence for this connection.</summary>
public readonly record struct RouteDecision(RouteVerdict Verdict, RouteDecisionSource Source)
{
    /// <summary>
    /// An effective IP block wins first. A connection's name wins over DNS hints and the application.
    /// An ambiguous DNS hint is absent. The application then wins over non-blocking range rules.
    /// </summary>
    public static RouteDecision Resolve(RouteVerdict address, RouteVerdict domain, RouteVerdict inferred,
        RouteVerdict application)
    {
        if (address == RouteVerdict.Block)
        {
            return new(address, RouteDecisionSource.Address);
        }

        if (domain != RouteVerdict.None)
        {
            return new(domain, RouteDecisionSource.Domain);
        }

        if (inferred != RouteVerdict.None)
        {
            return new(inferred, RouteDecisionSource.Dns);
        }

        if (application != RouteVerdict.None)
        {
            return new(application, RouteDecisionSource.Application);
        }

        return new(address, address == RouteVerdict.None ? RouteDecisionSource.Default : RouteDecisionSource.Address);
    }

    /// <summary>DNS may stand in for a connection name only while all known answers agree.</summary>
    public static RouteVerdict Infer(IEnumerable<RouteVerdict> answers, out bool conflict)
    {
        conflict = false;
        using var values = answers.GetEnumerator();
        if (!values.MoveNext())
        {
            return RouteVerdict.None;
        }

        var first = values.Current;
        while (values.MoveNext())
        {
            if (values.Current != first)
            {
                conflict = true;
                return RouteVerdict.None;
            }
        }

        return first;
    }
}
