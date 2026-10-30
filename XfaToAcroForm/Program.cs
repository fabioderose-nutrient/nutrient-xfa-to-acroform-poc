using GdPicture14;
using XfaToAcroForm;
using XfaToAcroForm.Models;

// An empty key starts GdPicture in evaluation mode.
new LicenseManager().RegisterKEY(Environment.GetEnvironmentVariable("GDPICTURE_LICENSE_KEY") ?? "");

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return 0;
}

var options = new ConversionOptions();
var positional = new List<string>();
bool batch = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--verbose" or "-v": options.Verbose = true; break;
        case "--no-values": options.PreserveValues = false; break;
        case "--rebuild": options.Rebuild = true; break;
        case "--keep-content": options.KeepPageContent = true; break;
        case "--background" or "-b":
            if (i + 1 >= args.Length) { Console.Error.WriteLine("--background needs a PDF path."); return 2; }
            options.BackgroundPath = args[++i];
            break;
        case "--dir" or "-d": batch = true; break;
        default:
            if (args[i].StartsWith('-')) { Console.Error.WriteLine($"Unknown option {args[i]}"); return 2; }
            positional.Add(args[i]);
            break;
    }
}

if (batch)
{
    if (positional.Count < 2) { Console.Error.WriteLine("--dir needs an input folder and an output folder."); return 2; }
    var results = XfaConverter.ConvertDirectory(positional[0], positional[1], options);
    int skipped = results.Count(r => !r.Success && r.FormKind == "none");
    int failed = results.Count(r => !r.Success) - skipped;
    Console.WriteLine($"\n{results.Count - failed - skipped} converted, {skipped} skipped (no XFA), {failed} failed.");
    return failed > 0 ? 1 : 0;
}

if (positional.Count == 0) { PrintHelp(); return 2; }

string input = positional[0];
string output = positional.Count > 1
    ? positional[1]
    : Path.Combine("output", Path.GetFileNameWithoutExtension(input) + "_acroform.pdf");

Console.WriteLine($"Converting {Path.GetFileName(input)}");
var result = XfaConverter.Convert(input, output, options);

foreach (var w in result.Warnings) Console.WriteLine($"  warning: {w}");
if (!result.Success)
{
    Console.Error.WriteLine($"Conversion failed: {result.ErrorMessage}");
    return 1;
}

Console.WriteLine($"  Form       : {result.FormKind} XFA");
Console.WriteLine(result.FormKind == "static" && result.Fields.Count == 0
    ? $"  Fields     : XFA packets removed, {result.FieldsFound} existing AcroForm field(s) kept"
    : $"  Fields     : {result.FieldsCreated} created of {result.FieldsFound} found");
foreach (var group in result.Fields.GroupBy(f => f.FieldType).OrderByDescending(g => g.Count()))
    Console.WriteLine($"               {group.Count(),3} {group.Key}");
Console.WriteLine($"  Output     : {result.OutputPath}");
Console.WriteLine($"  Elapsed    : {result.Elapsed.TotalSeconds:F2}s");
return 0;

static void PrintHelp()
{
    Console.WriteLine("""
    XFA to AcroForm converter (GdPicture.NET)

    Usage:
      XfaToAcroForm <input.pdf> [output.pdf] [options]
      XfaToAcroForm --dir <input-folder> <output-folder> [options]

    Options:
      --background, -b <pdf>  Add the fields on top of this flattened rendering of the form
      --no-values             Do not copy the XFA data into the fields
      --rebuild               Static XFA forms: recreate the fields from the template
      --keep-content          Dynamic XFA forms: keep the placeholder page content
      --verbose, -v           Print every field found
      --help, -h              Show this help

    Default output: output/<input name>_acroform.pdf
    """);
}
