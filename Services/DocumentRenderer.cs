using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JobFinder.Models;
using static System.Net.WebUtility;

namespace JobFinder.Services;

/// <summary>
/// Renders CVs and cover letters as printable A4 HTML. The layout is deliberately ATS-safe:
/// one column, real text (no images/icons), standard section headings, no tables.
/// "Save as PDF" in the browser's print dialog produces the file to upload.
/// </summary>
public static class DocumentRenderer
{
    public static string RenderCv(TailoredCv cv, ApplicationJob? job)
    {
        var body = new StringBuilder();
        body.Append($"<h1>{HtmlEncode(cv.Contact.Name)}</h1>");
        body.Append($"<p class=\"headline\">{HtmlEncode(cv.Headline)}</p>");
        var contact = new[] { cv.Contact.Location, cv.Contact.Phone, cv.Contact.Email }
            .Concat(cv.Contact.Links)
            .Where(s => !string.IsNullOrWhiteSpace(s));
        body.Append($"<p class=\"contact\">{string.Join(" | ", contact.Select(HtmlEncode))}</p>");
        if (!string.IsNullOrWhiteSpace(cv.Contact.WorkAuthorization))
            body.Append($"<p class=\"contact\">{HtmlEncode(cv.Contact.WorkAuthorization)}</p>");

        body.Append("<h2>Summary</h2>");
        body.Append($"<p>{HtmlEncode(cv.Summary)}</p>");

        body.Append("<h2>Skills</h2>");
        foreach (var group in cv.Skills)
            body.Append($"<p class=\"skills\"><strong>{HtmlEncode(group.Category)}:</strong> {HtmlEncode(string.Join(", ", group.Items))}</p>");

        body.Append("<h2>Experience</h2>");
        AppendEntries(body, cv.Experience);

        body.Append("<h2>Education</h2>");
        AppendEntries(body, cv.Education);

        body.Append("<h2>Languages</h2>");
        body.Append($"<p>{HtmlEncode(cv.Languages)}</p>");

        var notes = cv.Changes.Count == 0 ? "" :
            "<details class=\"notes\" open><summary>What Claude changed (not printed)</summary><ul>" +
            string.Concat(cv.Changes.Select(c => $"<li>{HtmlEncode(c)}</li>")) + "</ul></details>";

        return Page(FileTitle(cv.Contact.Name, "CV", job), body.ToString(), notes);
    }

    public static string RenderCoverLetter(CoverLetter letter, ApplicationJob job, TailoredCv? cv)
    {
        var body = new StringBuilder();
        if (cv is not null)
        {
            body.Append($"<h1>{HtmlEncode(cv.Contact.Name)}</h1>");
            var contact = new[] { cv.Contact.Location, cv.Contact.Phone, cv.Contact.Email }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            body.Append($"<p class=\"contact\">{string.Join(" | ", contact.Select(HtmlEncode))}</p>");
        }
        body.Append($"<p class=\"date\">{DateTime.Now.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}</p>");
        if (job.Job is not null)
            body.Append($"<p class=\"re\"><strong>Application: {HtmlEncode(job.Job.Title)} — {HtmlEncode(job.Job.Company)}</strong></p>");
        body.Append($"<p>{HtmlEncode(letter.Greeting)}</p>");
        foreach (var paragraph in letter.Paragraphs)
            body.Append($"<p>{HtmlEncode(paragraph)}</p>");
        body.Append($"<p class=\"closing\">{HtmlEncode(letter.Closing)}<br>{HtmlEncode(letter.Signature)}</p>");

        return Page(FileTitle(letter.Signature, "Cover_Letter", job), body.ToString(), "", letter: true);
    }

    private static void AppendEntries(StringBuilder body, IEnumerable<CvEntry> entries)
    {
        foreach (var e in entries)
        {
            body.Append("<div class=\"entry\">");
            // plain inline text, no flex/float columns: ATS parsers read it in this exact order
            body.Append($"<p class=\"entry-head\"><strong>{HtmlEncode(e.Title)}</strong> — {HtmlEncode(e.Organization)}</p>");
            var meta = new[] { e.Dates, e.Location }.Where(s => !string.IsNullOrWhiteSpace(s));
            body.Append($"<p class=\"entry-meta\">{string.Join(" | ", meta.Select(HtmlEncode))}</p>");
            if (e.Bullets.Count > 0)
                body.Append("<ul>" + string.Concat(e.Bullets.Select(b => $"<li>{HtmlEncode(b)}</li>")) + "</ul>");
            body.Append("</div>");
        }
    }

    private static readonly Dictionary<char, string> Transliterations = new()
    {
        ['ö'] = "o", ['Ö'] = "O", ['ü'] = "u", ['Ü'] = "U", ['ä'] = "a", ['Ä'] = "A", ['ß'] = "ss",
        ['ş'] = "s", ['Ş'] = "S", ['ç'] = "c", ['Ç'] = "C", ['ğ'] = "g", ['Ğ'] = "G", ['ı'] = "i", ['İ'] = "I",
        ['é'] = "e", ['è'] = "e", ['á'] = "a", ['à'] = "a", ['ó'] = "o", ['ñ'] = "n",
    };

    // the <title> becomes the default PDF file name, e.g. Feyza_Ozkefe_CV_Acme.pdf
    private static string FileTitle(string name, string kind, ApplicationJob? job)
    {
        var parts = new List<string> { name, kind };
        if (job?.Job?.Company is { Length: > 0 } company)
            parts.Add(company);
        // InvariantGlobalization makes string.Normalize a no-op, so transliterate the usual suspects
        var ascii = new StringBuilder();
        foreach (var c in string.Join("_", parts))
            ascii.Append(Transliterations.GetValueOrDefault(c, c.ToString()));
        return Regex.Replace(ascii.ToString(), @"[^A-Za-z0-9_]+", "_").Trim('_');
    }

    private static string Page(string title, string body, string notes, bool letter = false) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8">
          <title>{{HtmlEncode(title)}}</title>
          <style>
            @page { size: A4; margin: 14mm 16mm; }
            * { box-sizing: border-box; }
            body { margin: 0; background: #e9ecf2; font-family: Calibri, Carlito, Arial, Helvetica, sans-serif;
                   font-size: 10.5pt; line-height: 1.35; color: #111; }
            .toolbar { position: sticky; top: 0; display: flex; gap: 10px; align-items: center; justify-content: center;
                       padding: 10px; background: #1c2333; color: #fff; font-size: 13px; }
            .toolbar button { font: inherit; font-weight: 600; color: #fff; background: #2f6fed; border: 0;
                              border-radius: 6px; padding: 7px 16px; cursor: pointer; }
            .sheet { width: 210mm; min-height: 297mm; margin: 16px auto; padding: 14mm 16mm; background: #fff;
                     box-shadow: 0 1px 6px rgba(0,0,0,.15); }
            .notes { width: 210mm; margin: 0 auto 16px; padding: 10px 16px; background: #fffbe6;
                     border: 1px solid #f0d78c; border-radius: 6px; font-size: 13px; }
            h1 { font-size: 20pt; margin: 0 0 2pt; }
            h2 { font-size: 11pt; text-transform: uppercase; letter-spacing: .04em; margin: 11pt 0 4pt;
                 padding-bottom: 2pt; border-bottom: 1px solid #333; }
            p { margin: 0 0 4pt; }
            ul { margin: 2pt 0 0; padding-left: 14pt; }
            li { margin-bottom: 2pt; }
            .headline { font-size: 11.5pt; font-weight: 600; }
            .contact { font-size: 9.5pt; }
            .entry { margin-bottom: 7pt; break-inside: avoid; }
            .entry-head { margin: 0; }
            .entry-meta { font-size: 9.5pt; color: #444; margin: 0; }
            .letter p { margin-bottom: 9pt; }
            .letter .date { margin-top: 14pt; }
            .letter .closing { margin-top: 14pt; }
            @media print {
              body { background: #fff; }
              .toolbar, .notes { display: none; }
              .sheet { width: auto; min-height: 0; margin: 0; padding: 0; box-shadow: none; }
            }
          </style>
        </head>
        <body>
          <div class="toolbar">
            <span>Print → "Save as PDF" (A4, margins: default, headers/footers off)</span>
            <button onclick="window.print()">Save as PDF</button>
          </div>
          {{notes}}
          <main class="sheet{{(letter ? " letter" : "")}}">{{body}}</main>
        </body>
        </html>
        """;
}
