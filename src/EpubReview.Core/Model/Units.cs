namespace EpubReview.Core.Model;

public sealed record UnitRef(string Dimension, string Symbol, string? Basis = null)
{
    public static readonly UnitRef Bytes = new("bytes", "B", "uint8 octets of the original ZIP entry");
    public static readonly UnitRef Ordinal = new("ordinal", "#", "0-based position in the ZIP central directory");
    public static readonly UnitRef ClockMs = new("time", "ms", "SMIL clipBegin/clipEnd normalized to milliseconds; origin=clip start");
    public static readonly UnitRef ClockRaw = new("time", "raw", "unparsed SMIL clock value, kept at original location with diagnosis");
    public static readonly UnitRef Pixel = new("length", "px", "SVG user coordinate unit on a 96dpi canvas");
}

public sealed record Scalar(double Value, UnitRef Unit, string? Raw = null, string? DiagnosisCode = null);

public sealed record CoordinateBasis(
    string Origin,
    string XAxis,
    string YAxis,
    UnitRef Unit,
    string ReferenceFrame);
