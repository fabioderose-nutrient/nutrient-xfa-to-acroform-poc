using System.Diagnostics;
using GdPicture14;
using XfaToAcroForm.Models;
using XfaToAcroForm.Services;

namespace XfaToAcroForm;

/// <summary>
/// Converts an XFA PDF form to a standard AcroForm PDF:
///
///   1. Read the XFA template and data packets (PdfPig).
///   2. Open the PDF with GdPicture and check what kind of XFA form it is.
///      - Static XFA: the PDF already has AcroForm fields. Remove the XFA packets and keep them.
///      - Dynamic XFA: the page only shows a placeholder. Lay out the template, clear the
///        placeholder, draw the static text and create the AcroForm fields.
///   3. Save the result.
/// </summary>
public static class XfaConverter
{
    public static ConversionResult Convert(string inputPath, string outputPath, ConversionOptions options)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        void Log(string message)
        {
            if (options.Verbose) Console.WriteLine($"  {message}");
        }

        if (!File.Exists(inputPath))
            return Fail(inputPath, $"File not found: {inputPath}", sw);

        var packets = XfaPacketReader.Read(inputPath, Log);

        using var pdf = new GdPicturePDF();
        var status = pdf.LoadFromFile(inputPath, true);
        if (status != GdPictureStatus.OK)
            return Fail(inputPath, $"GdPicture could not open the file: {status}", sw);

        bool hasXfa = pdf.HasXFAFormFields() || packets.Found;
        if (!hasXfa)
            return Fail(inputPath, "The file has no XFA form.", sw, "none");

        int existingFields = pdf.GetFormFieldsCount();
        bool isStatic = existingFields > 0;
        Log($"{(isStatic ? "Static" : "Dynamic")} XFA form, {existingFields} existing AcroForm field(s), {pdf.GetPageCount()} page(s).");

        // Static form: the AcroForm fields are already there. Dropping the XFA packets is enough.
        if (isStatic && !options.Rebuild && options.BackgroundPath == null)
        {
            status = pdf.RemoveXFAFormFieldsData();
            if (status != GdPictureStatus.OK)
                return Fail(inputPath, $"RemoveXFAFormFieldsData returned {status}.", sw, "static");
            return Save(pdf, inputPath, outputPath, "static", existingFields, existingFields, warnings, Array.Empty<XfaFieldInfo>(), sw);
        }

        if (!packets.Found)
            return Fail(inputPath, "The XFA template could not be read.", sw, isStatic ? "static" : "dynamic");

        XfaForm form;
        try
        {
            form = new XfaTemplateParser().Parse(packets.Template!, packets.Datasets);
        }
        catch (Exception ex)
        {
            return Fail(inputPath, $"The XFA template could not be parsed: {ex.Message}", sw, isStatic ? "static" : "dynamic");
        }
        warnings.AddRange(form.Warnings);
        Log($"Template: {form.Fields.Count} field(s), {form.Texts.Count} text(s), {form.PageCount} page(s) of {form.PageWidth:F0}x{form.PageHeight:F0} pt.");
        if (options.Verbose)
            foreach (var f in form.Fields) Console.WriteLine($"    {f}");

        GdPicturePDF target = pdf;
        GdPicturePDF? background = null;
        try
        {
            if (options.BackgroundPath != null)
            {
                // Overlay mode: the fields go on top of a flattened rendering of the form.
                background = new GdPicturePDF();
                status = background.LoadFromFile(options.BackgroundPath, true);
                if (status != GdPictureStatus.OK)
                    return Fail(inputPath, $"GdPicture could not open the background PDF: {status}", sw);
                target = background;
            }
            else
            {
                status = pdf.RemoveXFAFormFieldsData();
                if (status != GdPictureStatus.OK)
                    warnings.Add($"RemoveXFAFormFieldsData returned {status}.");
                if (isStatic) pdf.RemoveFormFields();
            }

            var writer = new AcroFormWriter(target, options);
            if (options.BackgroundPath == null && !isStatic && !options.KeepPageContent)
            {
                writer.ClearPages();
                writer.EnsurePageCount(form.PageCount, form.PageWidth, form.PageHeight);
                writer.DrawTexts(form.Texts);
                writer.DrawSignatureFrames(form.Fields);
            }
            else
            {
                writer.EnsurePageCount(form.PageCount, form.PageWidth, form.PageHeight);
            }

            int created = writer.WriteFields(form.Fields);
            warnings.AddRange(writer.Warnings);
            return Save(target, inputPath, outputPath, isStatic ? "static" : "dynamic", form.Fields.Count, created, warnings, form.Fields, sw);
        }
        finally
        {
            background?.Dispose();
        }
    }

    /// <summary>Converts every PDF of a folder. Output files keep their name.</summary>
    public static IReadOnlyList<ConversionResult> ConvertDirectory(string inputDir, string outputDir, ConversionOptions options)
    {
        Directory.CreateDirectory(outputDir);
        var results = new List<ConversionResult>();
        foreach (string file in Directory.GetFiles(inputDir, "*.pdf").OrderBy(f => f))
        {
            var result = Convert(file, Path.Combine(outputDir, Path.GetFileName(file)), options);
            Console.WriteLine($"{Path.GetFileName(file)}: {result}");
            results.Add(result);
        }
        return results;
    }

    private static ConversionResult Save(GdPicturePDF pdf, string input, string output, string kind, int found, int created,
        List<string> warnings, IReadOnlyList<XfaFieldInfo> fields, Stopwatch sw)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var status = pdf.SaveToFile(output);
        if (status != GdPictureStatus.OK)
            return Fail(input, $"SaveToFile returned {status}.", sw, kind);

        return new ConversionResult
        {
            Success = true,
            InputPath = input,
            OutputPath = output,
            FormKind = kind,
            FieldsFound = found,
            FieldsCreated = created,
            Elapsed = sw.Elapsed,
            Warnings = warnings,
            Fields = fields
        };
    }

    private static ConversionResult Fail(string input, string message, Stopwatch sw, string kind = "") =>
        new() { Success = false, InputPath = input, ErrorMessage = message, Elapsed = sw.Elapsed, FormKind = kind };
}
