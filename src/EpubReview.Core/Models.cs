namespace EpubReview.Core;

/// <summary>三种必须区分开的失败形态 + 正常。</summary>
public static class Statuses
{
    public const string Ok = "ok";
    public const string BudgetExhausted = "budget-exhausted";
    public const string FormatCorrupt = "format-corrupt";
    public const string Unsolvable = "unsolvable";
}

/// <summary>带位置的诊断。位置格式: 容器内路径[:行号]。</summary>
public record Diagnostic(string Code, string Severity, string Message, string? Location, string? SubjectId);

/// <summary>不可变材料层: ZIP 条目字节 / container.xml / package 文档 / media type / fallback / fragment / 未识别 metadata 的原始表示。</summary>
public record Material(
    string Id,
    string Kind,        // zip-entry | container | package | overlay | nav | metadata
    string Name,        // 容器内路径
    string MediaType,   // 导入时保留的原始 media type
    byte[] Bytes,
    string Digest,
    string RawMetaJson  // 原始表示(未规范化), 例如未识别 metadata 的 raw XML
);

/// <summary>派生计算层: 记录输入摘要、参数版本与父级依赖。</summary>
public record Computation(
    string Id,
    string Kind,          // reading-order | reference-graph | nav-tree | fallback-chains | overlay
    string RuleVersion,
    string InputDigest,
    string ParamsJson,
    string Status,
    string ResultJson,
    string DiagnosticsJson,
    string ParentsJson    // 父级依赖: material id 列表
);

/// <summary>事件层: 导入/参数修订/源改动/接受/否定/调整/冲突。事件构成可回放的分支树。</summary>
public record ReviewEvent(
    string Id,
    long Seq,
    string? ParentId,
    string Kind,
    string PayloadJson,
    string CreatedAt
);
