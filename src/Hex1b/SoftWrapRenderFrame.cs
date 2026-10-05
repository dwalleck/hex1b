using Hex1b.Kgp;

namespace Hex1b;

/// <summary>A complete app frame, including cursor restoration and desired graphics.</summary>
internal sealed record SoftWrapRenderFrame(
    int Width,
    int Height,
    string Prefix,
    string Body,
    string Suffix,
    IReadOnlyList<KgpFragment> Graphics)
{
    internal const string SynchronizedBegin = "\x1b[?2026h";
    internal string Text => string.Concat(Prefix, Body, Suffix);
}
