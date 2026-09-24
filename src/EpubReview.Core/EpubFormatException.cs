namespace EpubReview.Core;

public sealed class EpubFormatException : Exception
{
    public required Diagnostic Diagnostic { get; init; }
    public EpubFormatException(Diagnostic diagnostic) : base(diagnostic.Message) => Diagnostic = diagnostic;
}
