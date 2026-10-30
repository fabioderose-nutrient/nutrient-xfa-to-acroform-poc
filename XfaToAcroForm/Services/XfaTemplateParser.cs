using System.Globalization;
using System.Xml.Linq;
using XfaToAcroForm.Models;

namespace XfaToAcroForm.Services;

/// <summary>
/// Reads the XFA template and data packets and lays the form out on pages.
///
/// The layout is a simplified version of the XFA layout rules:
///   - positioned containers place children at their x/y;
///   - "tb" containers stack children top to bottom, "lr-tb" and "row" containers place
///     them left to right in rows;
///   - while the flow continues from the root subform, a child that does not fit in the
///     content area, or that has a break before it, starts a new page;
///   - subform margins are applied; captions take their "reserve" from the field box,
///     the rest is the widget.
/// Repeating subforms, tables that grow with data, master page fields and scripts are not
/// evaluated.
/// </summary>
public sealed class XfaTemplateParser
{
    private static readonly HashSet<string> FlowLayouts = new() { "tb", "lr-tb", "rl-tb", "table", "row" };
    private static readonly HashSet<string> HiddenPresence = new() { "hidden", "invisible", "inactive" };

    private readonly XfaForm _form = new();
    private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _dataByLeaf = new(StringComparer.Ordinal);
    private float _contentX, _contentY, _contentW, _contentH;
    private int _page = 1;
    private bool _pageHasContent;

    public XfaForm Parse(string templateXml, string? datasetsXml)
    {
        var template = XDocument.Parse(templateXml);
        var templateRoot = template.Descendants().FirstOrDefault(e => e.Name.LocalName == "template")
                           ?? template.Root!;
        var rootSubform = templateRoot.Elements().FirstOrDefault(e => e.Name.LocalName == "subform")
                          ?? throw new InvalidDataException("The XFA template has no root subform.");

        ReadPageSetup(rootSubform);
        if (datasetsXml != null) ReadData(datasetsXml);

        string rootName = rootSubform.Attribute("name")?.Value ?? "";
        LayoutContainer(rootSubform, _contentX, _contentY, rootName, paginate: true);

        _form.PageCount = _page;
        return _form;
    }

    // ----------------------------------------------------------------------------
    // Page setup: first pageArea of the pageSet.
    // ----------------------------------------------------------------------------
    private void ReadPageSetup(XElement rootSubform)
    {
        var pageArea = rootSubform.Descendants().FirstOrDefault(e => e.Name.LocalName == "pageArea");
        var medium = pageArea?.Elements().FirstOrDefault(e => e.Name.LocalName == "medium");
        if (medium != null)
        {
            string stock = medium.Attribute("stock")?.Value ?? "";
            float shortSide = Measure(medium.Attribute("short")?.Value, stock.StartsWith("a4") ? 595.3f : 612f);
            float longSide = Measure(medium.Attribute("long")?.Value, stock.StartsWith("a4") ? 841.9f : 792f);
            bool landscape = medium.Attribute("orientation")?.Value == "landscape";
            _form.PageWidth = landscape ? longSide : shortSide;
            _form.PageHeight = landscape ? shortSide : longSide;
        }

        var contentArea = pageArea?.Elements().FirstOrDefault(e => e.Name.LocalName == "contentArea");
        _contentX = Measure(contentArea?.Attribute("x")?.Value, 18f);
        _contentY = Measure(contentArea?.Attribute("y")?.Value, 18f);
        _contentW = Measure(contentArea?.Attribute("w")?.Value, _form.PageWidth - 2 * _contentX);
        _contentH = Measure(contentArea?.Attribute("h")?.Value, _form.PageHeight - 2 * _contentY);

        if (pageArea != null && pageArea.Descendants().Any(e => e.Name.LocalName == "field"))
            _form.Warnings.Add("Fields on the master page (pageArea) are not converted.");
    }

    // ----------------------------------------------------------------------------
    // Data: every leaf of xfa:data, by dotted path and by name.
    // ----------------------------------------------------------------------------
    private void ReadData(string datasetsXml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(datasetsXml); }
        catch (Exception ex)
        {
            _form.Warnings.Add($"The XFA data packet could not be parsed: {ex.Message}");
            return;
        }

        var data = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "data");
        if (data == null) return;

        void Walk(XElement element, string path)
        {
            string p = path.Length == 0 ? element.Name.LocalName : $"{path}.{element.Name.LocalName}";
            var children = element.Elements().ToList();
            if (children.Count == 0)
            {
                _data.TryAdd(p, element.Value);
                if (!_dataByLeaf.TryGetValue(element.Name.LocalName, out var list))
                    _dataByLeaf[element.Name.LocalName] = list = new List<string>();
                list.Add(element.Value);
                return;
            }
            foreach (var child in children) Walk(child, p);
        }

        foreach (var root in data.Elements()) Walk(root, "");
    }

    private string? LookupData(XElement node, string dataPath)
    {
        var bind = Child(node, "bind");
        string match = bind?.Attribute("match")?.Value ?? "once";
        if (match == "none") return null;

        if (match == "dataRef" && bind?.Attribute("ref")?.Value is { Length: > 0 } reference)
        {
            // "$.a.b" is relative to the parent data node, "$record.a.b" to the data root.
            string parent = dataPath.Contains('.') ? dataPath[..dataPath.LastIndexOf('.')] : dataPath;
            string root = dataPath.Split('.')[0];
            string resolved = reference.StartsWith("$record.") ? $"{root}.{reference[8..]}"
                            : reference.StartsWith("$.") ? $"{parent}.{reference[2..]}"
                            : reference.TrimStart('$', '.');
            resolved = System.Text.RegularExpressions.Regex.Replace(resolved, @"\[\d+\]", "");
            if (_data.TryGetValue(resolved, out var byRef)) return byRef;
        }

        if (_data.TryGetValue(dataPath, out var value)) return value;

        // Fall back on the leaf name when it is unique in the data.
        string leaf = dataPath[(dataPath.LastIndexOf('.') + 1)..];
        return _dataByLeaf.TryGetValue(leaf, out var values) && values.Count == 1 ? values[0] : null;
    }

    // ----------------------------------------------------------------------------
    // Layout
    // ----------------------------------------------------------------------------
    private float ContentBottom => _contentY + _contentH;

    /// <summary>
    /// Lays out the children of a container whose top-left corner is at (x, y) on the current
    /// page. <paramref name="paginate"/> is true while every ancestor flows top to bottom
    /// from the root: only then can content move to the next page. Returns the bottom of
    /// the laid-out content (on the current page).
    /// </summary>
    private float LayoutContainer(XElement container, float x, float y, string dataPath, bool paginate)
    {
        string layout = container.Attribute("layout")?.Value ?? "position";
        var margin = Child(container, "margin");
        float left = Measure(margin?.Attribute("leftInset")?.Value);
        float top = Measure(margin?.Attribute("topInset")?.Value);
        float right = Measure(margin?.Attribute("rightInset")?.Value);
        float bottom = Measure(margin?.Attribute("bottomInset")?.Value);
        float width = Measure(container.Attribute("w")?.Value, _contentW) - left - right;
        x += left;
        y += top;

        if (!FlowLayouts.Contains(layout))
        {
            float extent = y;
            foreach (var child in LayoutChildren(container))
            {
                float cx = x + Measure(child.Attribute("x")?.Value);
                float cy = y + Measure(child.Attribute("y")?.Value);
                float end = Place(child, cx, cy, dataPath, paginate: false);
                extent = Math.Max(extent, end);
            }
            float h = Measure(container.Attribute("h")?.Value, -1f);
            return h >= 0 ? y - top + h : extent + bottom;
        }

        bool rows = layout is "lr-tb" or "rl-tb" or "row";
        float cursorY = y, cursorX = x, rowHeight = 0f;
        bool breakPending = false;

        foreach (var child in LayoutChildren(container))
        {
            if (paginate && (breakPending || HasBreak(child, before: true)) && _pageHasContent)
            {
                NewPage();
                cursorY = _contentY;
                cursorX = x;
                rowHeight = 0f;
            }
            breakPending = false;

            float h = Measure(child.Attribute("h")?.Value, Measure(child.Attribute("minH")?.Value, -1f));
            float w = Measure(child.Attribute("w")?.Value, Measure(child.Attribute("minW")?.Value, width));

            if (rows)
            {
                // Left to right, wrapping to a new row when the container is full.
                if (cursorX > x && cursorX + w > x + width + 0.5f && layout != "row")
                {
                    cursorY += rowHeight;
                    cursorX = x;
                    rowHeight = 0f;
                }
                float rowH = Math.Max(h, 0f);
                if (paginate && cursorY > _contentY && cursorY + rowH > ContentBottom + 0.5f)
                {
                    NewPage();
                    cursorY = _contentY;
                }
                float end = Place(child, cursorX, cursorY, dataPath, paginate: false);
                rowHeight = Math.Max(rowHeight, Math.Max(rowH, end - cursorY));
                cursorX += w;
            }
            else
            {
                bool nestedFlow = IsContainer(child) && FlowLayouts.Contains(child.Attribute("layout")?.Value ?? "position");
                if (paginate && h >= 0 && cursorY > _contentY && cursorY + h > ContentBottom + 0.5f)
                {
                    NewPage();
                    cursorY = _contentY;
                }
                // A flowing child without a fixed height continues the page flow itself.
                float end = Place(child, x, cursorY, dataPath, paginate && nestedFlow && h < 0);
                cursorY = h >= 0 ? cursorY + h : end;
            }

            if (paginate && HasBreak(child, before: false))
                breakPending = true;
        }

        return (rows ? cursorY + rowHeight : cursorY) + bottom;
    }

    /// <summary>Places one child at (x, y). Returns the bottom of what was placed.</summary>
    private float Place(XElement child, float x, float y, string dataPath, bool paginate)
    {
        string name = child.Attribute("name")?.Value ?? "";
        string childPath = name.Length > 0 ? $"{dataPath}.{name}" : dataPath;
        float h = Measure(child.Attribute("h")?.Value, Measure(child.Attribute("minH")?.Value, 0f));
        _pageHasContent = true;

        switch (child.Name.LocalName)
        {
            case "subform":
            case "subformSet":
            case "area":
                return LayoutContainer(child, x, y, childPath, paginate);
            case "draw":
                AddDraw(child, x, y);
                return y + h;
            case "exclGroup":
                AddExclusionGroup(child, x, y, childPath);
                return y + h;
            default:
                AddField(child, x, y, childPath, group: null, groupValue: null);
                return y + h;
        }
    }

    private static IEnumerable<XElement> LayoutChildren(XElement container) =>
        container.Elements().Where(e =>
            e.Name.LocalName is "subform" or "subformSet" or "area" or "field" or "draw" or "exclGroup"
            && !HiddenPresence.Contains(e.Attribute("presence")?.Value ?? ""));

    private static bool IsContainer(XElement e) => e.Name.LocalName is "subform" or "subformSet" or "area";

    private void NewPage()
    {
        _page++;
        _pageHasContent = false;
    }

    private static bool HasBreak(XElement node, bool before)
    {
        var legacy = Child(node, "break");
        string? attr = legacy?.Attribute(before ? "before" : "after")?.Value;
        if (attr is "pageArea" or "contentArea" or "pageEven" or "pageOdd") return true;

        var element = Child(node, before ? "breakBefore" : "breakAfter");
        string? target = element?.Attribute("targetType")?.Value;
        return target is "pageArea" or "contentArea";
    }

    private void AddDraw(XElement draw, float x, float y)
    {
        string text = TextValue(Child(draw, "value"));
        if (string.IsNullOrWhiteSpace(text)) return;

        var (size, bold) = Font(draw, 10f);
        _form.Texts.Add(new XfaText
        {
            PageNumber = _page,
            X = x, Y = y,
            Width = Measure(draw.Attribute("w")?.Value),
            Height = Measure(draw.Attribute("h")?.Value, size * 1.4f),
            Text = text.Trim(),
            FontSize = size,
            Bold = bold
        });
    }

    private void AddExclusionGroup(XElement group, float x, float y, string dataPath)
    {
        string groupName = group.Attribute("name")?.Value ?? "RadioGroup";
        string? groupValue = LookupData(group, dataPath);
        foreach (var field in group.Elements().Where(e => e.Name.LocalName == "field"))
        {
            if (HiddenPresence.Contains(field.Attribute("presence")?.Value ?? "")) continue;
            float fx = x + Measure(field.Attribute("x")?.Value);
            float fy = y + Measure(field.Attribute("y")?.Value);
            AddField(field, fx, fy, $"{dataPath}.{field.Attribute("name")?.Value}", groupName, groupValue);
        }
    }

    private void AddField(XElement field, float x, float y, string dataPath, string? group, string? groupValue)
    {
        string name = field.Attribute("name")?.Value ?? $"Field{_form.Fields.Count + 1}";
        float w = Measure(field.Attribute("w")?.Value, Measure(field.Attribute("minW")?.Value, 144f));
        float h = Measure(field.Attribute("h")?.Value, Measure(field.Attribute("minH")?.Value, 20f));

        var ui = Child(field, "ui")?.Elements()
            .FirstOrDefault(e => e.Name.LocalName is not ("border" or "margin" or "extras" or "picture"));
        string uiName = ui?.Name.LocalName ?? "textEdit";

        XfaFieldType? type = uiName switch
        {
            "textEdit" or "numericEdit" or "dateTimeEdit" or "passwordEdit" => XfaFieldType.Text,
            "checkButton" => group != null ? XfaFieldType.RadioButton : XfaFieldType.CheckBox,
            "choiceList" => ui!.Attribute("open")?.Value is "always" or "multiSelect" ? XfaFieldType.ListBox : XfaFieldType.ComboBox,
            "signature" => XfaFieldType.Signature,
            "button" => XfaFieldType.PushButton,
            _ => null
        };
        if (type == null)
        {
            _form.Warnings.Add($"Field '{dataPath}': ui '{uiName}' is not supported, skipped.");
            return;
        }

        var (fontSize, _) = Font(field, 10f);
        var info = new XfaFieldInfo
        {
            Name = name,
            FullName = dataPath,
            XfaUi = uiName,
            FieldType = type.Value,
            PageNumber = _page,
            FontSize = fontSize,
            MultiLine = ui?.Attribute("multiLine")?.Value is "1" or "true",
            ReadOnly = field.Attribute("access")?.Value is "readOnly" or "protected" or "nonInteractive",
            Required = Child(field, "validate")?.Attribute("nullTest")?.Value == "error",
            ToolTip = TextOf(Child(Child(field, "assist"), "toolTip")),
            RadioGroupName = group
        };

        var defaultValue = Child(field, "value");
        if (int.TryParse(Child(defaultValue, "text")?.Attribute("maxChars")?.Value, out int maxChars))
            info.MaxChars = maxChars;

        // Items: displayed texts, and saved values when a second <items save="1"> exists.
        var itemLists = field.Elements().Where(e => e.Name.LocalName == "items").ToList();
        var display = itemLists.FirstOrDefault(i => i.Attribute("save")?.Value != "1") ?? itemLists.FirstOrDefault();
        var saved = itemLists.FirstOrDefault(i => i.Attribute("save")?.Value == "1");
        var displayItems = display?.Elements().Select(e => e.Value).ToList() ?? new List<string>();
        var savedItems = saved?.Elements().Select(e => e.Value).ToList();

        // Value: data packet first, then the template default.
        string? value = group != null ? groupValue : LookupData(field, dataPath);
        if (string.IsNullOrEmpty(value)) value = NullIfEmpty(TextValue(defaultValue));

        switch (info.FieldType)
        {
            case XfaFieldType.CheckBox:
            case XfaFieldType.RadioButton:
                info.OnValue = displayItems.FirstOrDefault() ?? "1";
                info.IsChecked = value != null && value == info.OnValue;
                info.Value = value;
                break;
            case XfaFieldType.ComboBox:
            case XfaFieldType.ListBox:
                info.Options = displayItems;
                int savedIndex = savedItems?.IndexOf(value ?? "") ?? -1;
                info.Value = savedIndex >= 0 && savedIndex < displayItems.Count ? displayItems[savedIndex] : value;
                break;
            default:
                info.Value = value;
                break;
        }

        // Caption: its reserve is taken from the field box; the rest is the widget.
        float bx = x, by = y, bw = w, bh = h;
        var caption = Child(field, "caption");
        string captionText = TextValue(Child(caption, "value")).Trim();
        if (info.FieldType == XfaFieldType.PushButton)
        {
            // The caption of a button is its face, not a label next to it.
            info.Caption = captionText.Length > 0 ? captionText : null;
        }
        else if (caption != null && captionText.Length > 0 && caption.Attribute("presence")?.Value is not ("hidden" or "invisible"))
        {
            info.Caption = captionText;
            var (capSize, capBold) = Font(caption, fontSize);
            string placement = caption.Attribute("placement")?.Value ?? "left";
            float reserve = Measure(caption.Attribute("reserve")?.Value, -1f);
            if (reserve < 0)
                reserve = placement is "top" or "bottom" ? capSize * 1.5f : Math.Min(w * 0.5f, captionText.Length * capSize * 0.5f + 6f);

            var text = new XfaText { PageNumber = _page, Text = captionText, FontSize = capSize, Bold = capBold, CenterVertically = true };
            switch (placement)
            {
                case "top":
                    (text.X, text.Y, text.Width, text.Height) = (x, y, w, reserve);
                    (by, bh) = (y + reserve, h - reserve);
                    break;
                case "bottom":
                    (text.X, text.Y, text.Width, text.Height) = (x, y + h - reserve, w, reserve);
                    bh = h - reserve;
                    break;
                case "right":
                    (text.X, text.Y, text.Width, text.Height) = (x + w - reserve + 4f, y, reserve, h);
                    bw = w - reserve;
                    break;
                case "inline":
                    text.Text = "";
                    break;
                default: // left
                    (text.X, text.Y, text.Width, text.Height) = (x, y, reserve, h);
                    (bx, bw) = (x + reserve, w - reserve);
                    break;
            }
            if (text.Text.Length > 0) _form.Texts.Add(text);
        }

        // Check boxes and radio buttons: a square of the checkButton size, centred vertically.
        if (info.FieldType is XfaFieldType.CheckBox or XfaFieldType.RadioButton)
        {
            float size = Measure(ui?.Attribute("size")?.Value, 10f);
            by = by + (bh - size) / 2f;
            bw = bh = size;
        }

        (info.X, info.Y, info.Width, info.Height) = (bx, by, Math.Max(bw, 4f), Math.Max(bh, 4f));
        _form.Fields.Add(info);
    }

    // ----------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------
    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? TextOf(XElement? element) => NullIfEmpty(element?.Value.Trim());

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Text of a value element: text, integer, decimal, float, date... or rich text.
    /// Images and other binary values are ignored.</summary>
    private static string TextValue(XElement? value)
    {
        var content = value?.Elements().FirstOrDefault();
        if (content == null) return "";
        return content.Name.LocalName switch
        {
            "text" or "integer" or "decimal" or "float" or "date" or "time" or "dateTime" or "boolean" => content.Value,
            // Rich text (XHTML): keep the text of each paragraph on its own line.
            "exData" => string.Join("\n", content.Descendants()
                .Where(e => e.Name.LocalName == "p" || (!e.HasElements && e.Parent?.Name.LocalName == "body"))
                .Select(e => e.Value.Trim()).Where(t => t.Length > 0)
                .DefaultIfEmpty(content.Value.Trim())),
            _ => ""
        };
    }

    private static (float size, bool bold) Font(XElement node, float defaultSize)
    {
        var font = Child(node, "font");
        float size = Measure(font?.Attribute("size")?.Value, defaultSize, bareUnitIsPoint: true);
        bool bold = font?.Attribute("weight")?.Value == "bold";
        return (size, bold);
    }

    /// <summary>
    /// Converts an XFA measurement ("25.4mm", "1in", "72pt", "2.54cm", "96px") to points.
    /// A number without unit is in inches, except font sizes.
    /// </summary>
    public static float Measure(string? raw, float defaultPt = 0f, bool bareUnitIsPoint = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return defaultPt;
        raw = raw.Trim();

        int unitStart = raw.Length;
        while (unitStart > 0 && char.IsLetter(raw[unitStart - 1])) unitStart--;
        if (!float.TryParse(raw[..unitStart], NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
            return defaultPt;

        return raw[unitStart..].ToLowerInvariant() switch
        {
            "in" => number * 72f,
            "mm" => number * 72f / 25.4f,
            "cm" => number * 72f / 2.54f,
            "pt" => number,
            "mp" => number / 1000f,
            "px" => number * 72f / 96f,
            "" => bareUnitIsPoint ? number : number * 72f,
            _ => defaultPt
        };
    }
}
