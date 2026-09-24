using EpubReview.Core.Model;

namespace EpubReview.Core.Import;

public abstract record ImportResult
{
    public sealed record Success(EpubMaterialSet Material) : ImportResult;

    public sealed record FormatCorrupt(
        string Reason,
        SourceLocation? Location,
        IReadOnlyList<Diagnostic> Diagnostics,
        long? ByteOffset) : ImportResult;

    public sealed record BudgetExhausted(
        string LimitName,
        long LimitValue,
        UnitRef LimitUnit,
        long ObservedValue,
        SourceLocation? Location,
        IReadOnlyList<Diagnostic> Diagnostics) : ImportResult;

    public sealed record SemanticUnresolvable(
        EpubMaterialSet Material,
        IReadOnlyList<Diagnostic> Diagnostics,
        IReadOnlyList<string> UnresolvedObjects) : ImportResult;
}
