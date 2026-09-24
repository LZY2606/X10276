namespace EpubReview.Core.Model;

public sealed record InputDigest(
    string Algorithm,
    string Value,
    long Length,
    UnitRef LengthUnit,
    IReadOnlyList<string> ComponentHashes)
{
    public static InputDigest Of(string value, long length, IReadOnlyList<string> components) =>
        new("sha256", value, length, UnitRef.Bytes, components);
}
