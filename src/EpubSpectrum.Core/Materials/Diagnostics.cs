namespace EpubSpectrum.Core.Materials;

public enum Severity { Info, Warning, Error }

/// <summary>带位置、类别与稳定代码的诊断。空值/默认值绝不伪装成功。</summary>
public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    SourceLocation? Location = null,
    IReadOnlyDictionary<string, string>? Data = null)
{
    public override string ToString()
        => $"{Code} [{Severity}] {Message}" + (Location is null ? "" : " @ " + Location.Format());
}

/// <summary>三类必须互相区分的结果：预算耗尽 / 格式损坏 / 语义无解。</summary>
public enum OutcomeKind { Complete, BudgetExhausted, FormatCorrupt, SemanticallyUnresolvable }
