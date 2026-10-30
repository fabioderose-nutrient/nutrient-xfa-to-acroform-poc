using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace XfaToAcroForm.Services;

/// <summary>The XML packets of an XFA form.</summary>
public sealed class XfaPackets
{
    public string? Template { get; set; }
    public string? Datasets { get; set; }
    public bool Found => Template != null;
}

/// <summary>
/// Reads the XFA packets (/AcroForm /XFA) of a PDF. GdPicture can detect and remove XFA,
/// but has no API to read it, so the packets are read with PdfPig, which also handles
/// encrypted files that open without a password. If that fails, the raw file is scanned
/// for compressed XML streams.
/// </summary>
public static class XfaPacketReader
{
    public static XfaPackets Read(string pdfPath, Action<string> log)
    {
        var xml = ReadWithPdfPig(pdfPath, log);
        if (xml.Count == 0)
        {
            log("PdfPig found no XFA packets, scanning the raw file.");
            xml = ReadRawStreams(pdfPath);
        }

        var packets = new XfaPackets();
        foreach (var x in xml)
        {
            if (packets.Template == null && x.Contains("xfa-template")) packets.Template = x;
            if (packets.Datasets == null && x.Contains("datasets")) packets.Datasets = x;
        }
        // A single packet can hold the whole XDP document.
        if (packets.Template == null)
            packets.Template = xml.Where(x => x.Contains("<subform")).OrderByDescending(x => x.Length).FirstOrDefault();

        log(packets.Found
            ? $"XFA packets read: template {packets.Template!.Length} chars, datasets {(packets.Datasets?.Length ?? 0)} chars."
            : "No XFA template packet found.");
        return packets;
    }

    private static List<string> ReadWithPdfPig(string pdfPath, Action<string> log)
    {
        var results = new List<string>();
        try
        {
            using var document = PdfDocument.Open(pdfPath);
            var catalog = document.Structure.Catalog.CatalogDictionary;
            if (!catalog.TryGet(NameToken.Create("AcroForm"), out IToken? acroFormToken))
                return results;

            var acroForm = Resolve(acroFormToken, document) as DictionaryToken;
            if (acroForm == null || !acroForm.TryGet(NameToken.Create("XFA"), out IToken? xfaToken))
                return results;

            var xfa = Resolve(xfaToken, document);
            if (xfa is StreamToken single)
            {
                results.Add(Decode(single));
            }
            else if (xfa is ArrayToken array)
            {
                // [(preamble) stream (config) stream (template) stream (datasets) stream ...]
                foreach (var item in array.Data)
                {
                    if (Resolve(item, document) is StreamToken stream)
                        results.Add(Decode(stream));
                }
            }
        }
        catch (Exception ex)
        {
            log($"PdfPig could not read the file: {ex.Message}");
        }
        return results.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
    }

    private static IToken Resolve(IToken token, PdfDocument document)
    {
        if (token is IndirectReferenceToken reference)
        {
            var obj = document.Structure.GetObject(reference.Data);
            if (obj != null) return obj.Data;
        }
        return token;
    }

    private static string Decode(StreamToken stream)
    {
        byte[] raw = stream.Data.ToArray();
        bool flate = false;
        if (stream.StreamDictionary.TryGet(NameToken.Create("Filter"), out IToken? filter))
        {
            flate = filter is NameToken n && n.Data == "FlateDecode"
                 || filter is ArrayToken a && a.Data.Any(f => f is NameToken fn && fn.Data == "FlateDecode");
        }
        return flate ? Inflate(raw) ?? "" : Encoding.UTF8.GetString(raw);
    }

    // Fallback for files PdfPig cannot parse: inflate every stream and keep the XFA XML ones.
    private static List<string> ReadRawStreams(string pdfPath)
    {
        var results = new List<string>();
        byte[] bytes = File.ReadAllBytes(pdfPath);
        string text = Encoding.Latin1.GetString(bytes);

        foreach (Match m in Regex.Matches(text, @"stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end <= start || end - start > 10_000_000) continue;

            var data = new byte[end - start];
            Array.Copy(bytes, start, data, 0, data.Length);
            string? content = Inflate(data);
            if (content == null)
            {
                string plain = Encoding.UTF8.GetString(data).TrimStart();
                if (plain.StartsWith("<")) content = plain;
            }
            if (content != null && (content.Contains("xfa-template") || content.Contains("xfa-data") || content.Contains("xdp:xdp")))
                results.Add(content);
        }
        return results;
    }

    private static string? Inflate(byte[] data)
    {
        foreach (int skip in new[] { 2, 0 }) // zlib header, then raw deflate
        {
            try
            {
                if (data.Length <= skip) continue;
                using var ms = new MemoryStream(data, skip, data.Length - skip);
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                using var reader = new StreamReader(deflate, Encoding.UTF8);
                string s = reader.ReadToEnd();
                if (s.Length > 0) return s;
            }
            catch { /* try the next variant */ }
        }
        return null;
    }
}
