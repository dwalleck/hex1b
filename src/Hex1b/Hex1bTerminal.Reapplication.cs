namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    /// <summary>
    /// Applies one recorded output chunk to a detached model exactly as the output pump's raw path does:
    /// the chunk is framed, decoded and tokenized with the model's own continuation state, then applied as
    /// one model event. Only the case reapplier calls this, on a terminal whose pumps never start; nothing
    /// is written to a presentation and no filter or metric sees it.
    /// </summary>
    internal void ApplyRecordedOutput(ReadOnlySpan<byte> data)
    {
        var tokenization = TokenizeRawWorkloadOutput(data);
        ApplyTokens(tokenization.Tokens, tokenization.FramedDcs);
    }
}
