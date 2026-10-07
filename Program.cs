using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

const string Usage = """
    Put the links in a Netscape-format bookmarks file (as exported by Chrome, Edge
    or Firefox) into an SQLite database.

    Usage: File2Database bookmarksfile [database] [options]

    The database defaults to the bookmarks file with a .db extension
    (bookmarks_10_5_26.html -> bookmarks_10_5_26.db). It has one table,
    bookmarks, with a row per URL:

      id        row number, in the order links were first added
      title     the link's title
      url       the link's address
      created   when the bookmark was added (ADD_DATE), local time,
                as yyyy-MM-dd HH:mm:ss
      modified  LAST_MODIFIED, or created when there is none
      tags      the folders the link is in, outermost first, separated by ", "
                (Philosophy > Eastern -> "Philosophy, Eastern"). The browser's
                toolbar folder (Bookmarks Toolbar, Bookmarks bar) is left out.
                A link in more than one folder lists each, separated by " | "
                ("Philosophy, Eastern | Reading").

    A link that appears more than once keeps the created and modified dates of
    its earliest copy (the one with the earliest created date).

    If the database already exists, the bookmarks are merged into it: new links
    are added, and links already there get the title and tags from this file but
    keep whichever created and modified dates are earliest. Links that aren't in
    this file are left alone.

    Options:
      --overwrite   replace the database instead of merging into it
      -h, --help    show this help

    """;

var linkRegex = new Regex(@"<A\s(?<attrs>[^>]*)>(?<text>.*?)</A>", RegexOptions.IgnoreCase);
var folderRegex = new Regex(@"<H3(?<attrs>[^>]*)>(?<text>.*?)</H3>", RegexOptions.IgnoreCase);
var attrRegex = new Regex(@"(?<name>[A-Z_]+)\s*=\s*""(?<value>[^""]*)""", RegexOptions.IgnoreCase);

Console.OutputEncoding = Encoding.UTF8;
return Run(args);

int Run(string[] args)
{
    bool overwrite = false;
    var positionals = new List<string>();

    foreach (var arg in args)
    {
        switch (arg)
        {
            case "-h" or "--help":
                Console.Write(Usage);
                return 0;
            case "--overwrite": overwrite = true; break;
            default:
                if (arg.StartsWith('-') || positionals.Count == 2)
                {
                    Console.Error.WriteLine($"Unexpected argument: {arg}\n");
                    Console.Error.Write(Usage);
                    return 1;
                }
                positionals.Add(arg);
                break;
        }
    }

    if (positionals.Count == 0)
    {
        Console.Error.Write(Usage);
        return 1;
    }
    var file = Path.GetFullPath(positionals[0]);
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"Bookmarks file not found: {file}");
        return 1;
    }
    var database = Path.GetFullPath(positionals.Count > 1 ? positionals[1] : Path.ChangeExtension(file, ".db"));
    bool merge = File.Exists(database) && !overwrite;

    var (links, problems) = ReadLinks(file);

    // Work on a temporary copy and swap it in at the end, so a failure part way
    // through leaves any existing database alone.
    var temp = database + ".tmp";
    int added;
    try
    {
        if (merge)
            File.Copy(database, temp, overwrite: true);
        else
            File.Delete(temp);
        added = Save(links, temp);
        SqliteConnection.ClearAllPools();   // release the file so it can be moved
        File.Move(temp, database, overwrite: true);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(temp); } catch (IOException) { }
        Console.Error.WriteLine($"Can't write {database}: {e.Message}");
        if (e is SqliteException { SqliteErrorCode: 19 })    // constraint failed
            Console.Error.WriteLine("It has the same URL in more than one row (made by an older File2Database?). Use --overwrite to rebuild it.");
        else if (e is IOException)
            Console.Error.WriteLine("If it's open in DB Browser, close it and try again.");
        return 1;
    }

    Console.WriteLine(merge
        ? $"{links.Count} links merged into {database}: {added} new, {links.Count - added} already there"
        : $"{links.Count} links written to {database}");
    if (problems > 0)
        Console.WriteLine($"{problems} problems, see above");
    return problems > 0 ? 2 : 0;
}

// Read the links in the bookmarks file, one per URL, in the order they first appear.
(List<Link> Links, int Problems) ReadLinks(string file)
{
    var links = new List<Link>();
    var byUrl = new Dictionary<string, Link>(StringComparer.Ordinal);
    int problems = 0;

    // Names of the folders we're in, outermost first. The browser's toolbar folder
    // (Firefox's "Bookmarks Toolbar", Chrome's "Bookmarks bar", Edge's "Favorites
    // bar") is null, so it doesn't become a tag on everything in it.
    var folders = new List<string?>();

    foreach (var rawLine in File.ReadLines(file, Encoding.UTF8))
    {
        var line = rawLine.Trim();

        if (line.StartsWith("<DT><A", StringComparison.OrdinalIgnoreCase))
        {
            var m = linkRegex.Match(line);
            var attrs = m.Success ? ParseAttributes(m.Groups["attrs"].Value) : null;
            if (attrs == null || !attrs.TryGetValue("HREF", out var url) || url.Length == 0)
            {
                Console.WriteLine($"Link line not parsed correctly: {line}");
                problems++;
                continue;
            }

            var created = GetDate(attrs, "ADD_DATE");
            var modified = GetDate(attrs, "LAST_MODIFIED") ?? created;
            var path = string.Join(", ", folders.OfType<string>());

            if (!byUrl.TryGetValue(url, out var link))
            {
                link = new Link(CleanTitle(TagText(m.Groups["text"].Value)), url, created, modified);
                byUrl.Add(url, link);
                links.Add(link);
            }
            else if (IsEarlier(created, link.Created))
            {
                link.Created = created;
                link.Modified = modified;
            }
            if (path.Length > 0 && !link.Paths.Contains(path))
                link.Paths.Add(path);
        }
        else if (line.StartsWith("<DT><H3", StringComparison.OrdinalIgnoreCase))
        {
            var m = folderRegex.Match(line);
            var attrs = m.Success ? ParseAttributes(m.Groups["attrs"].Value) : [];
            var name = m.Success ? CleanTitle(TagText(m.Groups["text"].Value)) : "";
            if (attrs.TryGetValue("PERSONAL_TOOLBAR_FOLDER", out var toolbar) && toolbar.Equals("true", StringComparison.OrdinalIgnoreCase))
                folders.Add(null);
            else
                folders.Add(name.Length > 0 ? name : "untitled");
        }
        else if (line.StartsWith("</DL>", StringComparison.OrdinalIgnoreCase))
        {
            // The last </DL> closes the top level list, which isn't a folder.
            if (folders.Count > 0)
                folders.RemoveAt(folders.Count - 1);
        }
    }

    return (links, problems);
}

// A date beats no date; otherwise the earlier one wins.
bool IsEarlier(DateTime? a, DateTime? b) => a is { } x && (b is not { } y || x < y);

// Write the links into the database, creating the table if it isn't there yet.
// Returns how many were new.
int Save(List<Link> links, string database)
{
    using var connection = new SqliteConnection($"Data Source={database}");
    connection.Open();
    using var transaction = connection.BeginTransaction();

    var create = connection.CreateCommand();
    create.CommandText = """
        CREATE TABLE IF NOT EXISTS bookmarks (
            id       INTEGER PRIMARY KEY,
            title    TEXT NOT NULL,
            url      TEXT NOT NULL,
            created  TEXT,
            modified TEXT,
            tags     TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS bookmarks_url_unique ON bookmarks (url);
        """;
    create.ExecuteNonQuery();

    var count = connection.CreateCommand();
    count.CommandText = "SELECT count(*) FROM bookmarks";
    long before = (long)count.ExecuteScalar()!;

    // For a URL that's already there, take the new title and tags, and keep the
    // created/modified pair with the earliest created date. SET expressions all
    // see the row as it was, so both dates are judged on the old created value.
    // The dates are yyyy-MM-dd HH:mm:ss text, which compares in date order.
    var upsert = connection.CreateCommand();
    upsert.CommandText = """
        INSERT INTO bookmarks (title, url, created, modified, tags)
        VALUES ($title, $url, $created, $modified, $tags)
        ON CONFLICT (url) DO UPDATE SET
            title = excluded.title,
            tags = excluded.tags,
            created = CASE WHEN excluded.created < created OR (created IS NULL AND excluded.created IS NOT NULL)
                           THEN excluded.created ELSE created END,
            modified = CASE WHEN excluded.created < created OR (created IS NULL AND excluded.created IS NOT NULL)
                            THEN excluded.modified ELSE modified END
        """;
    var title = upsert.Parameters.Add("$title", SqliteType.Text);
    var url = upsert.Parameters.Add("$url", SqliteType.Text);
    var created = upsert.Parameters.Add("$created", SqliteType.Text);
    var modified = upsert.Parameters.Add("$modified", SqliteType.Text);
    var tags = upsert.Parameters.Add("$tags", SqliteType.Text);

    foreach (var link in links)
    {
        title.Value = link.Title;
        url.Value = link.Url;
        created.Value = FormatDate(link.Created);
        modified.Value = FormatDate(link.Modified);
        tags.Value = string.Join(" | ", link.Paths);
        upsert.ExecuteNonQuery();
    }

    long after = (long)count.ExecuteScalar()!;
    transaction.Commit();
    return (int)(after - before);
}


Dictionary<string, string> ParseAttributes(string attrs)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (Match m in attrRegex.Matches(attrs))
        result[m.Groups["name"].Value] = WebUtility.HtmlDecode(m.Groups["value"].Value);
    return result;
}

// The text between the tags, with any inner tags dropped and entities like &amp; decoded.
string TagText(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));

// Tidy whitespace, fix � and drop the bits YouTube puts in page titles. Nothing
// else is changed, since unlike a file name a title in the database can hold anything.
string CleanTitle(string title)
{
    var name = title.Replace('�', '-');           // � where a mis-decoded dash or bullet was
    name = name.Replace("▶ ", "");                     // YouTube "playing" marker
    name = Regex.Replace(name, @"\s+", " ").Trim();
    name = Regex.Replace(name, @"\A\(\d+\) ", "");     // leading (1), (2)... notification counts
    return name;
}

// ADD_DATE and LAST_MODIFIED are Unix times in seconds. A few exporters use
// milliseconds or microseconds, so scale anything too big to be seconds.
DateTime? GetDate(Dictionary<string, string> attrs, string key)
{
    if (!attrs.TryGetValue(key, out var text) || !long.TryParse(text, out var value) || value <= 0)
        return null;
    if (value > 100_000_000_000_000) value /= 1_000_000;
    else if (value > 100_000_000_000) value /= 1_000;
    try
    {
        return DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
    }
    catch (ArgumentOutOfRangeException)
    {
        return null;
    }
}

// Local time as text sorts correctly and reads well in DB Browser; SQLite's date
// functions take it as is.
object FormatDate(DateTime? utc) =>
    utc is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : DBNull.Value;

// One URL from the bookmarks file, with every folder path it was found in.
class Link(string title, string url, DateTime? created, DateTime? modified)
{
    public string Title { get; } = title;
    public string Url { get; } = url;
    public DateTime? Created { get; set; } = created;
    public DateTime? Modified { get; set; } = modified;
    public List<string> Paths { get; } = [];
}
