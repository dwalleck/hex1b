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
