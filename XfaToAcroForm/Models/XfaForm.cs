namespace XfaToAcroForm.Models;

/// <summary>AcroForm field types the converter creates.</summary>
public enum XfaFieldType
{
    Text,
    CheckBox,
    RadioButton,
    ComboBox,
    ListBox,
    Signature,
    PushButton
}

/// <summary>
/// One XFA field, with its widget box already resolved to page coordinates
/// (points, origin at the top-left corner of the page).
/// </summary>
public sealed class XfaFieldInfo
{
    /// <summary>Field name in the template (last segment).</summary>
    public string Name { get; set; } = "";

    /// <summary>Dotted path of named subforms, for example "form1.ClaimForm.Employee.FullName".</summary>
    public string FullName { get; set; } = "";

    /// <summary>The XFA ui element, for example textEdit, numericEdit or choiceList.</summary>
    public string XfaUi { get; set; } = "";

    public XfaFieldType FieldType { get; set; } = XfaFieldType.Text;

    public int PageNumber { get; set; } = 1;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    public string? Caption { get; set; }
    public string? ToolTip { get; set; }

    /// <summary>Value from the XFA data packet, or the template default.</summary>
    public string? Value { get; set; }

    /// <summary>Displayed items of a drop-down or list box.</summary>
    public List<string> Options { get; set; } = new();

    /// <summary>For check boxes and radio buttons: the value that means "on".</summary>
    public string? OnValue { get; set; }

    /// <summary>For radio buttons: the name of the exclusion group.</summary>
    public string? RadioGroupName { get; set; }

    public bool IsChecked { get; set; }
    public bool ReadOnly { get; set; }
    public bool Required { get; set; }
    public bool MultiLine { get; set; }
    public int MaxChars { get; set; }
    public float FontSize { get; set; } = 10f;

    public override string ToString() =>
        $"[{FieldType,-11}] {FullName} ({XfaUi}) page {PageNumber} at ({X:F0},{Y:F0}) {Width:F0}x{Height:F0}" +
        (string.IsNullOrEmpty(Value) ? "" : $" = \"{Value}\"");
}

/// <summary>Static text of the form: draw elements and field captions.</summary>
public sealed class XfaText
{
    public int PageNumber { get; set; } = 1;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public string Text { get; set; } = "";
    public float FontSize { get; set; } = 9f;
    public bool Bold { get; set; }

    /// <summary>True to centre the text vertically in its box (captions), false to align it at the top.</summary>
    public bool CenterVertically { get; set; }
}

/// <summary>Everything the converter needs from the XFA template and data packets.</summary>
public sealed class XfaForm
{
    public List<XfaFieldInfo> Fields { get; } = new();
    public List<XfaText> Texts { get; } = new();
    public List<string> Warnings { get; } = new();
    public int PageCount { get; set; } = 1;
    public float PageWidth { get; set; } = 612f;
    public float PageHeight { get; set; } = 792f;
}
