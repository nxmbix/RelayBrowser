using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Relay.Core.Bookmarks;

/// <summary>A node in an imported/exported bookmark tree.</summary>
public sealed class BookmarkNode
{
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public bool IsFolder => Url is null;
    public List<BookmarkNode> Children { get; } = [];

    public int CountBookmarks() => (IsFolder ? 0 : 1) + Children.Sum(c => c.CountBookmarks());
}

/// <summary>
/// Reads and writes the "Netscape Bookmark File" HTML format that every major browser
/// (Chrome, Edge, Firefox, Brave, Opera, Safari) can import and export.
/// </summary>
public static partial class NetscapeBookmarks
{
    [GeneratedRegex(@"<(?<tag>/?(?:DL|DT|H3|A))\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"HREF\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    /// <summary>Parses bookmark HTML into a root folder. Tolerant of the format's many quirks.</summary>
    public static BookmarkNode Parse(string html)
    {
        var root = new BookmarkNode { Title = "Imported" };
        var stack = new Stack<BookmarkNode>();
        stack.Push(root);
        BookmarkNode? pendingFolder = null;

        var matches = TagRegex().Matches(html);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var tag = m.Groups["tag"].Value.ToUpperInvariant();
            switch (tag)
            {
                case "H3":
                {
                    var title = TextUntil(html, m.Index + m.Length, "</H3>");
                    pendingFolder = new BookmarkNode { Title = Clean(title, "Folder") };
                    stack.Peek().Children.Add(pendingFolder);
                    break;
                }
                case "DL":
                    if (pendingFolder is not null)
                    {
                        stack.Push(pendingFolder);
                        pendingFolder = null;
                    }
                    else if (i > 0) // stray nested list; treat as same level
                    {
                        stack.Push(stack.Peek());
                    }
                    break;
                case "/DL":
                    if (stack.Count > 1) stack.Pop();
                    break;
                case "A":
                {
                    pendingFolder = null; // an empty folder (H3 without its own DL) ends here
                    var href = HrefRegex().Match(m.Groups["attrs"].Value);
                    if (!href.Success) break;
                    var url = WebUtility.HtmlDecode(href.Groups["v"].Value).Trim();
                    if (!IsImportableUrl(url)) break;
                    var title = TextUntil(html, m.Index + m.Length, "</A>");
                    stack.Peek().Children.Add(new BookmarkNode { Title = Clean(title, url), Url = url });
                    break;
                }
            }
        }

        // Common export shape: a single top-level wrapper folder like "Bookmarks bar". Keep as-is.
        return root;
    }

    private static bool IsImportableUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeFile || u.Scheme == "ftp");

    private static string TextUntil(string html, int start, string endTag)
    {
        var end = html.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) end = Math.Min(html.Length, start + 300);
        var raw = html[start..end];
        // Remove any nested tags in the title.
        raw = Regex.Replace(raw, "<[^>]*>", string.Empty);
        return WebUtility.HtmlDecode(raw);
    }

    private static string Clean(string s, string fallback)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length > 300) s = s[..300];
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>Writes a bookmark tree in Netscape HTML format.</summary>
    public static string Export(BookmarkNode root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<!-- This is an automatically generated file. It will be read and overwritten. DO NOT EDIT! -->");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE>");
        sb.AppendLine("<H1>Bookmarks</H1>");
        WriteList(sb, root.Children, 0);
        return sb.ToString();
    }

    private static void WriteList(StringBuilder sb, List<BookmarkNode> items, int depth)
    {
        var pad = new string(' ', depth * 4);
        sb.Append(pad).AppendLine("<DL><p>");
        foreach (var item in items)
        {
            if (item.IsFolder)
            {
                sb.Append(pad).Append("    <DT><H3>").Append(WebUtility.HtmlEncode(item.Title)).AppendLine("</H3>");
                WriteList(sb, item.Children, depth + 1);
            }
            else
            {
                sb.Append(pad).Append("    <DT><A HREF=\"").Append(WebUtility.HtmlEncode(item.Url)).Append("\">")
                  .Append(WebUtility.HtmlEncode(item.Title)).AppendLine("</A>");
            }
        }
        sb.Append(pad).AppendLine("</DL><p>");
    }
}
