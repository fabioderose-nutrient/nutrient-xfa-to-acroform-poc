using GdPicture14;
using XfaToAcroForm.Models;

namespace XfaToAcroForm.Services;

/// <summary>
/// Writes AcroForm fields and the static text of the form into the open GdPicture document.
/// Coordinates are in points with the origin at the top-left corner, like XFA.
/// </summary>
public sealed class AcroFormWriter
{
    private readonly GdPicturePDF _pdf;
    private readonly ConversionOptions _options;
    private readonly List<string> _warnings = new();
    private readonly Dictionary<string, int> _nameCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int fieldId, string name)> _radioGroups = new(StringComparer.Ordinal);
    private string? _font;
    private string? _boldFont;

    public IReadOnlyList<string> Warnings => _warnings;

    public AcroFormWriter(GdPicturePDF pdf, ConversionOptions options)
    {
        _pdf = pdf;
        _options = options;
        _pdf.SetOrigin(PdfOrigin.PdfOriginTopLeft);
        _pdf.SetMeasurementUnit(PdfMeasurementUnit.PdfMeasurementUnitPoint);
    }

    /// <summary>Adds pages of the form's size until the document has at least <paramref name="pageCount"/> pages.</summary>
    public void EnsurePageCount(int pageCount, float width, float height)
    {
        while (_pdf.GetPageCount() < pageCount)
            _pdf.NewPage(width, height);
    }

    /// <summary>Removes the placeholder content of every page ("Please wait..." on dynamic forms).</summary>
    public void ClearPages()
    {
        for (int p = 1; p <= _pdf.GetPageCount(); p++)
        {
            _pdf.SelectPage(p);
            _pdf.SetPageContent("");
        }
    }

    /// <summary>Draws the static text of the form: draw elements and field captions.</summary>
    public void DrawTexts(IEnumerable<XfaText> texts)
    {
        _font ??= _pdf.AddStandardFont(PdfStandardFont.PdfStandardFontHelvetica);
        _boldFont ??= _pdf.AddStandardFont(PdfStandardFont.PdfStandardFontHelveticaBold);
        _pdf.SetFillColor(0, 0, 0);

        foreach (var t in texts)
        {
            if (t.PageNumber > _pdf.GetPageCount()) continue;
            _pdf.SelectPage(t.PageNumber);
            _pdf.SetTextSize(t.FontSize);
            string font = t.Bold ? _boldFont : _font;
            var lines = Wrap(font, t.FontSize, t.Text, t.Width);
            float lineHeight = t.FontSize * 1.2f;
            // DrawText takes the baseline position.
            float baseline = t.CenterVertically && lines.Count == 1
                ? t.Y + t.Height / 2f + t.FontSize * 0.35f
                : t.Y + t.FontSize;
            foreach (var line in lines)
            {
                _pdf.DrawText(font, t.X, baseline, line);
                baseline += lineHeight;
            }
        }
    }

    // Splits text into lines that fit the box width (XFA wraps draw text and captions).
    private List<string> Wrap(string font, float size, string text, float width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            if (width <= 0)
            {
                lines.Add(paragraph);
                continue;
            }
            string current = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && _pdf.GetTextWidth(font, size, candidate) > width)
                {
                    lines.Add(current);
                    current = word;
                }
                else current = candidate;
            }
            lines.Add(current);
        }
        return lines;
    }

    /// <summary>Draws a frame where a signature field goes: signature fields have no appearance until signed.</summary>
    public void DrawSignatureFrames(IEnumerable<XfaFieldInfo> fields)
    {
        _pdf.SetLineColor(120, 120, 120);
        _pdf.SetLineWidth(0.75f);
        foreach (var f in fields.Where(f => f.FieldType == XfaFieldType.Signature && f.PageNumber <= _pdf.GetPageCount()))
        {
            _pdf.SelectPage(f.PageNumber);
            _pdf.DrawRectangle(f.X, f.Y, f.Width, f.Height, false, true);
        }
    }

    /// <summary>Creates one AcroForm field per XFA field. Returns the number created.</summary>
    public int WriteFields(IReadOnlyList<XfaFieldInfo> fields)
    {
        _font ??= _pdf.AddStandardFont(PdfStandardFont.PdfStandardFontHelvetica);
        int created = 0;

        foreach (var field in fields)
        {
            if (field.PageNumber > _pdf.GetPageCount())
            {
                _warnings.Add($"Field '{field.FullName}': page {field.PageNumber} does not exist, skipped.");
                continue;
            }

            try
            {
                _pdf.SelectPage(field.PageNumber);
                int fieldId = field.FieldType switch
                {
                    XfaFieldType.CheckBox => WriteCheckBox(field),
                    XfaFieldType.RadioButton => WriteRadioButton(field),
                    XfaFieldType.ComboBox => WriteChoice(field, list: false),
                    XfaFieldType.ListBox => WriteChoice(field, list: true),
                    XfaFieldType.Signature => _pdf.AddSignatureFormField(field.X, field.Y, field.Width, field.Height, UniqueName(field.Name)),
                    XfaFieldType.PushButton => _pdf.AddPushButtonFormField(field.X, field.Y, field.Width, field.Height,
                        UniqueName(field.Name), field.Caption ?? field.Name, _font, field.FontSize, 0, 0, 0),
                    _ => WriteText(field)
                };

                if (fieldId == 0 || _pdf.GetStat() != GdPictureStatus.OK)
                {
                    _warnings.Add($"Field '{field.FullName}': GdPicture returned {_pdf.GetStat()}, skipped.");
                    continue;
                }

                Style(fieldId, field);
                created++;
            }
            catch (Exception ex)
            {
                _warnings.Add($"Field '{field.FullName}': {ex.Message}");
            }
        }

        // Let viewers regenerate the appearance of the fields when they open the file.
        _pdf.SetFormFieldsNeedAppearances(true);
        return created;
    }

    private int WriteText(XfaFieldInfo f)
    {
        string value = _options.PreserveValues ? f.Value ?? "" : "";
        int id = _pdf.AddTextFormField(f.X, f.Y, f.Width, f.Height, UniqueName(f.Name), value,
            f.MultiLine, _font, f.FontSize, 0, 0, 0);
        if (id != 0 && f.MaxChars > 0) _pdf.SetFormFieldMaxLen(id, f.MaxChars);
        return id;
    }

    private int WriteCheckBox(XfaFieldInfo f) =>
        _pdf.AddCheckBoxFormField(f.X, f.Y, f.Width, f.Height, UniqueName(f.Name),
            PdfCheckBoxStyle.PdfCheckBoxStyleCheck, _options.PreserveValues && f.IsChecked, 0, 0, 0);

    private int WriteRadioButton(XfaFieldInfo f)
    {
        string groupKey = f.FullName.Contains('.') ? f.FullName[..f.FullName.LastIndexOf('.')] : f.RadioGroupName!;
        if (!_radioGroups.TryGetValue(groupKey, out var group))
            group = (0, UniqueName(f.RadioGroupName ?? "RadioGroup"));

        // The radio button's export value is its XFA "on" value.
        int id = _pdf.AddRadioButtonFormField(f.X, f.Y, f.Width, f.Height, group.name, f.OnValue ?? f.Name,
            PdfCheckBoxStyle.PdfCheckBoxStyleCircle, 0, 0, 0);
        if (id == 0) return 0;

        _radioGroups[groupKey] = (id, group.name);
        // GdPicture turns the first button of a group on: set the state from the data instead.
        _pdf.SetFormFieldChecked(id, _options.PreserveValues && f.IsChecked);
        return id;
    }

    private int WriteChoice(XfaFieldInfo f, bool list)
    {
        string name = UniqueName(f.Name);
        int id = list
            ? _pdf.AddListFormField(f.X, f.Y, f.Width, f.Height, name, _font, f.FontSize, 0, 0, 0, false, false)
            : _pdf.AddComboFormField(f.X, f.Y, f.Width, f.Height, name, _font, f.FontSize, 0, 0, 0, false);
        if (id == 0) return 0;

        bool hasValue = _options.PreserveValues && !string.IsNullOrEmpty(f.Value);
        // GdPicture selects the last item added. A drop-down without a value gets a blank
        // first item, selected, so it starts empty like in the XFA form.
        if (!hasValue && !list) _pdf.AddFormFieldItem(id, " ");
        foreach (var item in f.Options) _pdf.AddFormFieldItem(id, item);
        string selected = hasValue ? f.Value! : " ";
        if (!list || hasValue)
        {
            if (_pdf.SetFormFieldValue(id, selected) != GdPictureStatus.OK)
                _warnings.Add($"Field '{f.FullName}': value '{f.Value}' is not one of its items.");
        }
        return id;
    }

    private void Style(int fieldId, XfaFieldInfo f)
    {
        if (f.FieldType != XfaFieldType.PushButton)
            _pdf.SetFormFieldBackgroundColor(fieldId, 255, 255, 255);
        _pdf.SetFormFieldBorderColor(fieldId, 120, 120, 120);
        _pdf.SetFormFieldBorderStyle(fieldId, 0.75f, PdfAnnotationBorderStyle.PdfAnnotationBorderStyleSolid, 0, 0);

        if (f.ReadOnly) _pdf.SetFormFieldReadOnly(fieldId, true);
        if (f.Required) _pdf.SetFormFieldRequired(fieldId, true);
        string? tip = f.ToolTip ?? f.Caption;
        if (!string.IsNullOrEmpty(tip) && f.FieldType != XfaFieldType.RadioButton)
            _pdf.SetFormFieldAlternateTitle(fieldId, tip);
    }

    // AcroForm names cannot contain dots (they separate the hierarchy): use the XFA field
    // name and add _2, _3... when the same name appears more than once.
    private string UniqueName(string baseName)
    {
        string name = string.IsNullOrWhiteSpace(baseName) ? "Field" : baseName.Replace('.', '_');
        if (!_nameCounts.TryGetValue(name, out int count))
        {
            _nameCounts[name] = 1;
            return name;
        }
        _nameCounts[name] = ++count;
        return $"{name}_{count}";
    }
}
