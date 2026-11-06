using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace PcServ.Web.Services;

public sealed record WikiHeading(string Id, string Text, int Level);

/// <summary>
/// Cleans page content on the server, however it arrived, because the browser can't be trusted to have done it.
/// Anything not on these lists is removed, so scripts and pasted styling can't reach a reader.
/// </summary>
public static partial class WikiHtml
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "br", "hr", "h2", "h3", "strong", "b", "em", "i", "u", "s", "code", "pre", "blockquote", "ul", "ol", "li", "a", "img", "table", "colgroup", "col", "thead", "tbody", "tr", "th", "td", "div", "span" })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "href", "src", "alt", "title", "colspan", "rowspan", "data-callout", "start" }) sanitizer.AllowedAttributes.Add(attribute);

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedClasses.Clear();
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto" }) sanitizer.AllowedSchemes.Add(scheme);
        sanitizer.KeepChildNodes = true;

        // Keeping child nodes is for formatting that isn't allowed. Anything inside these isn't meant to be read at all.
        sanitizer.RemovingTag += (_, e) =>
        {
            if (e.Tag.TagName.ToUpperInvariant() is "SCRIPT" or "STYLE" or "NOSCRIPT" or "TEMPLATE" or "IFRAME" or "OBJECT" or "EMBED" or "SVG" or "MATH") e.Tag.InnerHtml = "";
        };

        // A header cell with nothing in it gives screen readers nothing to announce, so it becomes an ordinary cell.
        sanitizer.PostProcessDom += (_, e) =>
        {
            foreach (var header in e.Document.QuerySelectorAll("th").Where(th => string.IsNullOrWhiteSpace(th.TextContent) && th.QuerySelector("img") is null).ToList())
            {
                var cell = e.Document.CreateElement("td");
                foreach (var attribute in header.Attributes) cell.SetAttribute(attribute.Name, attribute.Value);
                cell.InnerHtml = header.InnerHtml;
                header.Replace(cell);
            }
        };

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement element) return;
            switch (element.TagName)
            {
                case "A":
                    var href = element.GetAttribute("href") ?? "";
                    if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        element.SetAttribute("target", "_blank");
                        element.SetAttribute("rel", "noopener noreferrer");
                    }
                    break;
                case "IMG":
                    // Only the wiki's own images, so a page can't quietly load something from elsewhere.
                    if (!LocalImage().IsMatch(element.GetAttribute("src") ?? "")) element.Remove();
                    break;
                case "DIV":
                    if (element.GetAttribute("data-callout") is not ("note" or "warning")) element.RemoveAttribute("data-callout");
                    break;
            }
        };
        return sanitizer;
    }

    public static string Clean(string? html) => Sanitizer.Sanitize(html ?? "").Trim();

    /// <summary>The words on a page, for searching.</summary>
    public static string TextOf(string html)
    {
        var text = WebUtility.HtmlDecode(Tag().Replace(html, " "));
        return Space().Replace(text, " ").Trim();
    }

    /// <summary>Whether there's anything on the page once the markup is ignored.</summary>
    public static bool HasContent(string html) => TextOf(html).Length > 0 || html.Contains("<img", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gives headings addresses for the "On this page" list, and returns that list.</summary>
    public static (string Html, IReadOnlyList<WikiHeading> Headings) WithHeadings(string html)
    {
        var document = new HtmlParser().ParseDocument($"<!doctype html><body>{html}</body>");
        var headings = new List<WikiHeading>();
        var used = new HashSet<string>();
        foreach (var element in document.Body!.QuerySelectorAll("h2, h3"))
        {
            var text = Space().Replace(element.TextContent, " ").Trim();
            if (text.Length == 0) continue;
            var id = UniqueId(text, used);
            element.SetAttribute("id", id);
            headings.Add(new WikiHeading(id, text, element.TagName == "H2" ? 2 : 3));
        }
        return (document.Body.InnerHtml, headings);
    }

    private static string UniqueId(string text, HashSet<string> used)
    {
        var slug = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        var start = "h-" + slug.ToString().Trim('-');
        var id = start;
        for (var n = 2; !used.Add(id); n++) id = $"{start}-{n}";
        return id;
    }

    [GeneratedRegex(@"^wiki-images/[0-9a-f]{32}$")]
    private static partial Regex LocalImage();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
