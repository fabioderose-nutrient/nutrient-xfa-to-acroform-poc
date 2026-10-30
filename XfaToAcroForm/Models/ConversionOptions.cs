namespace XfaToAcroForm.Models;

/// <summary>Options that control a conversion. Every property has a default.</summary>
public sealed class ConversionOptions
{
    /// <summary>Copy the values of the XFA data packet into the new fields. Default: true.</summary>
    public bool PreserveValues { get; set; } = true;

    /// <summary>
    /// Static XFA forms already contain AcroForm fields: by default the converter only removes
    /// the XFA packets and keeps them. Set to true to delete those fields and recreate them
    /// from the XFA template.
    /// </summary>
    public bool Rebuild { get; set; }

    /// <summary>
    /// Dynamic XFA forms carry a placeholder page ("Please wait..."). By default the converter
    /// clears it and draws the static text of the template. Set to true to keep the page as is.
    /// </summary>
    public bool KeepPageContent { get; set; }

    /// <summary>
    /// Optional flattened rendering of the form (for example the form printed to PDF from an
    /// XFA viewer). When set, the fields are added on top of that PDF instead.
    /// </summary>
    public string? BackgroundPath { get; set; }

    /// <summary>Print every field found. Default: false.</summary>
    public bool Verbose { get; set; }
}
