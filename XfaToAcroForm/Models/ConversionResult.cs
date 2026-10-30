namespace XfaToAcroForm.Models;

/// <summary>Outcome of one conversion.</summary>
public sealed class ConversionResult
{
    public bool Success { get; init; }
    public string InputPath { get; init; } = "";
    public string OutputPath { get; init; } = "";

    /// <summary>"dynamic", "static" or "none" (no XFA found).</summary>
    public string FormKind { get; init; } = "";

    public int FieldsFound { get; init; }
    public int FieldsCreated { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<XfaFieldInfo> Fields { get; init; } = Array.Empty<XfaFieldInfo>();

    public override string ToString() =>
        Success
            ? $"OK   {FormKind} XFA form, {FieldsCreated}/{FieldsFound} fields, {Elapsed.TotalSeconds:F2}s -> {OutputPath}"
            : $"FAIL {ErrorMessage}";
}
