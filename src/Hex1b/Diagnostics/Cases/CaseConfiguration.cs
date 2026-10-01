using Hex1b.Reflow;
using Hex1b.Sixel;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Maps the model configuration a case records (manifest format 2) to and from the terminal's types:
/// the reflow strategy's name, the capabilities field by field, and the graphics limits.
/// </summary>
internal static class CaseConfiguration
{
    private static readonly (string Id, ITerminalReflowProvider Strategy)[] Strategies =
    [
        ("none", NoReflowStrategy.Instance),
        ("alacritty", AlacrittyReflowStrategy.Instance),
        ("foot", FootReflowStrategy.Instance),
        ("ghostty", GhosttyReflowStrategy.Instance),
        ("iterm2", ITerm2ReflowStrategy.Instance),
        ("kitty", KittyReflowStrategy.Instance),
        ("vte", VteReflowStrategy.Instance),
        ("wezterm", WezTermReflowStrategy.Instance),
        ("windows-terminal", WindowsTerminalReflowStrategy.Instance),
        ("xterm", XtermReflowStrategy.Instance),
    ];

    /// <summary>
    /// The presentation's reflow strategy by name. The built-in strategies are stateless, so the type
    /// identifies the behavior; automatic detection is named by what it detected. A presentation that
    /// reflows through a strategy this build cannot name is <c>custom:</c> and the type.
    /// </summary>
    internal static string ReflowStrategyId(IHex1bTerminalPresentationAdapter presentation) => presentation switch
    {
        IReflowStrategySource source => StrategyId(source.ReflowStrategy),
        ITerminalReflowProvider provider => $"custom:{provider.GetType().FullName}",
        _ => "none",
    };

    private static string StrategyId(ITerminalReflowProvider strategy)
    {
        if (strategy is AutoReflowStrategy auto)
            strategy = auto.DetectedStrategy;
        foreach (var (id, known) in Strategies)
        {
            if (known.GetType() == strategy.GetType())
                return id;
        }
        return $"custom:{strategy.GetType().FullName}";
    }

    /// <summary>The reflow strategy ids a re-applied model can be rebuilt with.</summary>
    internal static IEnumerable<string> StrategyIds => Strategies.Select(s => s.Id);

    /// <summary>The strategy a recorded name denotes, or null when this build cannot rebuild it.</summary>
    internal static ITerminalReflowProvider? CreateReflowStrategy(string id)
    {
        foreach (var (known, strategy) in Strategies)
        {
            if (known == id)
                return strategy;
        }
        return null;
    }

    /// <summary>The most retained rows a re-applied model is built with; a recorded capacity above it is refused.</summary>
    internal const int MaxScrollbackCapacity = 1_000_000;

    // Format 2 fields a model cannot be rebuilt without. Absent nullable fields (scrollbackCapacity,
    // customMarkerLimit, sixelCellMetrics) mean none, as they are written only when set.
    internal static readonly IReadOnlyList<string> RequiredFields =
        ["width", "height", "commandMarkHistoryCapacity", "escapeSequenceTimeoutMs", "reflowEnabled", "reflowStrategy", "capabilities", "graphics"];

    internal static readonly IReadOnlyList<string> RequiredCapabilities =
    [
        "supportsDeltaProtocol", "supportsSixel", "sixelSupport", "supportsMouse", "supportsTrueColor", "supports256Colors",
        "supportsAlternateScreen", "handlesAlternateScreenNatively", "supportsBracketedPaste", "supportsKgp",
        "supportsRetroactiveVariationSelectors", "cellPixelWidth", "actualCellPixelWidth", "cellPixelHeight", "defaultForeground",
        "defaultBackground", "supportsStyledUnderlines", "supportsUnderlineColor",
    ];

    private static readonly string[] RequiredGraphics =
    [
        "maximumRetainedInputBytesPerImage", "maximumRasterPixelsPerImage", "maximumRasterOperationsPerImage", "maximumImagesPerScreen",
        "maximumPlacementsPerScreen", "maximumHistoryPlacements", "maximumRetainedLogicalPixelsPerScreen", "maximumRetainedBytesPerScreen",
    ];

    /// <summary>The optional configuration fields (format 2), written only when set.</summary>
    internal static readonly IReadOnlyList<string> OptionalFields = ["scrollbackCapacity", "customMarkerLimit", "presentation", "workload"];

    /// <summary>The optional capability fields: the cell metrics.</summary>
    internal static readonly IReadOnlyList<string> OptionalCapabilities = ["sixelCellMetrics"];

    /// <summary>
    /// Why a format 2 configuration's fields cannot rebuild a model, naming the field: one of the <paramref name="required"/>
    /// missing from the raw manifest, one this build does not know, a value no model could be built with, or a reflow
    /// strategy this build cannot rebuild. Null when they can. The capabilities are <see cref="CapabilitiesProblem"/>'s.
    /// </summary>
    internal static string? FieldProblem(System.Text.Json.Nodes.JsonObject raw, DiagnosticCaseModelConfiguration configuration, IReadOnlyList<string> required)
    {
        foreach (var field in required)
        {
            if (raw[field] is null)
                return $"configuration.{field}: missing";
        }
        foreach (var field in RequiredGraphics)
        {
            if (raw["graphics"]?[field] is null)
                return $"graphics.{field}: missing";
        }
        if (configuration.Unknown is { Count: > 0 } unknown)
            return $"configuration.{unknown.Keys.Order(StringComparer.Ordinal).First()}: unknown field";
        if (configuration.Graphics?.Unknown is { Count: > 0 } unknownGraphics)
            return $"graphics.{unknownGraphics.Keys.Order(StringComparer.Ordinal).First()}: unknown field";
        if (configuration.Width is < 1 or > 10_000)
            return $"configuration.width: {configuration.Width} is not 1 to 10,000";
        if (configuration.Height is < 1 or > 10_000)
            return $"configuration.height: {configuration.Height} is not 1 to 10,000";
        if (configuration.ScrollbackCapacity is < 1 or > MaxScrollbackCapacity)
            return $"configuration.scrollbackCapacity: {configuration.ScrollbackCapacity} is not 1 to {MaxScrollbackCapacity:N0}";
        if (configuration.CommandMarkHistoryCapacity < 0)
            return $"configuration.commandMarkHistoryCapacity: {configuration.CommandMarkHistoryCapacity} is negative";
        if (configuration.CustomMarkerLimit is < 0)
            return $"configuration.customMarkerLimit: {configuration.CustomMarkerLimit} is negative";
        if (!double.IsFinite(configuration.EscapeSequenceTimeoutMs) || configuration.EscapeSequenceTimeoutMs is < 0 or > 86_400_000)
            return $"configuration.escapeSequenceTimeoutMs: {configuration.EscapeSequenceTimeoutMs} is not 0 to 86,400,000";
        if (CreateReflowStrategy(configuration.ReflowStrategy) is null)
            return $"reflowStrategy: '{configuration.ReflowStrategy}' is not a strategy this build can rebuild.";
        return null;
    }

    /// <summary>
    /// Why recorded capabilities cannot rebuild a model, naming the field: one missing from the raw manifest, one
    /// this build does not know, or an unknown value. Null, with the rebuilt capabilities, when they can.
    /// </summary>
    internal static string? CapabilitiesProblem(System.Text.Json.Nodes.JsonObject? raw, DiagnosticCaseCapabilities? recorded, IReadOnlyList<string> required,
        out TerminalCapabilities? capabilities)
    {
        capabilities = null;
        // A guard for a caller that did not run FieldProblem first; the reapplier and the start never reach it, as
        // "capabilities" is a required field and a non-object value fails the typed read before either.
        if (raw is null || recorded is null)
            return "configuration.capabilities: missing";
        foreach (var field in required)
        {
            if (raw[field] is null)
                return $"capabilities.{field}: missing";
        }
        if (raw["sixelCellMetrics"] is System.Text.Json.Nodes.JsonObject metrics)
        {
            foreach (var field in new[] { "width", "height", "source", "reliability" })
            {
                if (metrics[field] is null)
                    return $"capabilities.sixelCellMetrics.{field}: missing";
            }
        }
        capabilities = TerminalCapabilities(recorded, out var problem);
        return problem;
    }

    /// <summary>
    /// Why a recorded configuration cannot rebuild a model, or null: its fields and reflow strategy
    /// (<see cref="FieldProblem"/>), then its capabilities (<see cref="CapabilitiesProblem"/>). The reapplier
    /// judges each as its own compatibility check; a live start that this names is never complete.
    /// </summary>
    internal static string? RebuildProblem(System.Text.Json.Nodes.JsonObject raw, DiagnosticCaseModelConfiguration configuration) =>
        FieldProblem(raw, configuration, RequiredFields)
        ?? CapabilitiesProblem(raw["capabilities"] as System.Text.Json.Nodes.JsonObject, configuration.Capabilities, RequiredCapabilities, out _);

    internal static DiagnosticCaseCapabilities Capabilities(TerminalCapabilities capabilities) => new()
    {
        SupportsDeltaProtocol = capabilities.SupportsDeltaProtocol,
        SupportsSixel = capabilities.SupportsSixel,
        SixelSupport = capabilities.SixelSupport.ToString(),
        SixelCellMetrics = capabilities.SixelCellMetrics is { } metrics
            ? new DiagnosticCaseSixelCellMetrics
            {
                Width = metrics.Width,
                Height = metrics.Height,
                Source = metrics.Source.ToString(),
                Reliability = metrics.Reliability.ToString(),
            }
            : null,
        SupportsMouse = capabilities.SupportsMouse,
        SupportsTrueColor = capabilities.SupportsTrueColor,
        Supports256Colors = capabilities.Supports256Colors,
        SupportsAlternateScreen = capabilities.SupportsAlternateScreen,
        HandlesAlternateScreenNatively = capabilities.HandlesAlternateScreenNatively,
        SupportsBracketedPaste = capabilities.SupportsBracketedPaste,
        SupportsKgp = capabilities.SupportsKgp,
        SupportsRetroactiveVariationSelectors = capabilities.SupportsRetroactiveVariationSelectors,
        CellPixelWidth = capabilities.CellPixelWidth,
        ActualCellPixelWidth = capabilities.ActualCellPixelWidth,
        CellPixelHeight = capabilities.CellPixelHeight,
        DefaultForeground = capabilities.DefaultForeground,
        DefaultBackground = capabilities.DefaultBackground,
        SupportsStyledUnderlines = capabilities.SupportsStyledUnderlines,
        SupportsUnderlineColor = capabilities.SupportsUnderlineColor,
    };

    /// <summary>
    /// Rebuilds recorded capabilities; null with the offending field named when a field or value is
    /// unknown to this build.
    /// </summary>
    internal static TerminalCapabilities? TerminalCapabilities(DiagnosticCaseCapabilities recorded, out string? problem)
    {
        problem = null;
        if (recorded.Unknown is { Count: > 0 } unknown)
        {
            problem = $"capabilities.{unknown.Keys.Order(StringComparer.Ordinal).First()}: unknown field";
            return null;
        }
        if (!TryParseName<SixelPresentationSupport>(recorded.SixelSupport, out var sixelSupport))
        {
            problem = $"capabilities.sixelSupport: unknown value '{recorded.SixelSupport}'";
            return null;
        }

        SixelCellMetrics? metrics = null;
        if (recorded.SixelCellMetrics is { } m)
        {
            if (m.Unknown is { Count: > 0 } unknownMetric)
            {
                problem = $"capabilities.sixelCellMetrics.{unknownMetric.Keys.Order(StringComparer.Ordinal).First()}: unknown field";
                return null;
            }
            if (!TryParseName<SixelCellMetricsSource>(m.Source, out var source))
            {
                problem = $"capabilities.sixelCellMetrics.source: unknown value '{m.Source}'";
                return null;
            }
            if (!TryParseName<SixelCellMetricsReliability>(m.Reliability, out var reliability))
            {
                problem = $"capabilities.sixelCellMetrics.reliability: unknown value '{m.Reliability}'";
                return null;
            }
            metrics = new SixelCellMetrics(m.Width, m.Height, source, reliability);
        }

        return new TerminalCapabilities
        {
            SupportsDeltaProtocol = recorded.SupportsDeltaProtocol,
            SupportsSixel = recorded.SupportsSixel,
            SixelSupport = sixelSupport,
            SixelCellMetrics = metrics,
            SupportsMouse = recorded.SupportsMouse,
            SupportsTrueColor = recorded.SupportsTrueColor,
            Supports256Colors = recorded.Supports256Colors,
            SupportsAlternateScreen = recorded.SupportsAlternateScreen,
            HandlesAlternateScreenNatively = recorded.HandlesAlternateScreenNatively,
            SupportsBracketedPaste = recorded.SupportsBracketedPaste,
            SupportsKgp = recorded.SupportsKgp,
            SupportsRetroactiveVariationSelectors = recorded.SupportsRetroactiveVariationSelectors,
            CellPixelWidth = recorded.CellPixelWidth,
            ActualCellPixelWidth = recorded.ActualCellPixelWidth,
            CellPixelHeight = recorded.CellPixelHeight,
            DefaultForeground = recorded.DefaultForeground,
            DefaultBackground = recorded.DefaultBackground,
            SupportsStyledUnderlines = recorded.SupportsStyledUnderlines,
            SupportsUnderlineColor = recorded.SupportsUnderlineColor,
        };
    }

    private static bool TryParseName<TEnum>(string name, out TEnum value) where TEnum : struct, Enum =>
        Enum.TryParse(name, ignoreCase: false, out value) && Enum.IsDefined(value) && !int.TryParse(name, out _);

    internal static DiagnosticCaseGraphicsLimits Graphics(Hex1bTerminalGraphicsOptions graphics) => new()
    {
        MaximumRetainedInputBytesPerImage = graphics.MaximumRetainedInputBytesPerImage,
        MaximumRasterPixelsPerImage = graphics.MaximumRasterPixelsPerImage,
        MaximumRasterOperationsPerImage = graphics.MaximumRasterOperationsPerImage,
        MaximumImagesPerScreen = graphics.MaximumImagesPerScreen,
        MaximumPlacementsPerScreen = graphics.MaximumPlacementsPerScreen,
        MaximumHistoryPlacements = graphics.MaximumHistoryPlacements,
        MaximumRetainedLogicalPixelsPerScreen = graphics.MaximumRetainedLogicalPixelsPerScreen,
        MaximumRetainedBytesPerScreen = graphics.MaximumRetainedBytesPerScreen,
    };
}
