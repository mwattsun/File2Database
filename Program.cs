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
    bookmarks, with a row per link:

      id        row number, in the order the links appear in the file
      title     the link's title
      url       the link's address
      created   when the bookmark was added (ADD_DATE), local time,
                as yyyy-MM-dd HH:mm:ss
      modified  LAST_MODIFIED, or created when there is none
      tags      the folders the link is in, outermost first, separated by ", "
                (Philosophy > Eastern -> "Philosophy, Eastern")

    A link that is in more than one folder gets a row for each.

    Options:
      --overwrite   replace the database if it already exists
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
    if (File.Exists(database) && !overwrite)
    {
        Console.Error.WriteLine($"{database} already exists. Use --overwrite to replace it.");
        return 1;
    }

    // Build the database under a temporary name and swap it in at the end, so a
    // failure part way through leaves any existing database alone.
    var temp = database + ".tmp";
    int linkCount, problems;
    try
    {
        File.Delete(temp);
        (linkCount, problems) = Load(file, temp);
        SqliteConnection.ClearAllPools();   // release the file so it can be moved
        File.Move(temp, database, overwrite: true);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(temp); } catch (IOException) { }
        Console.Error.WriteLine($"Can't write {database}: {e.Message}");
        if (e is IOException)
            Console.Error.WriteLine("If it's open in DB Browser, close it and try again.");
        return 1;
    }

    Console.WriteLine($"{linkCount} links written to {database}"
        + (problems > 0 ? $" ({problems} problems, see above)" : ""));
    return problems > 0 ? 2 : 0;
}

(int Links, int Problems) Load(string file, string database)
{
    using var connection = new SqliteConnection($"Data Source={database}");
    connection.Open();
    using var transaction = connection.BeginTransaction();

    var create = connection.CreateCommand();
    create.CommandText = """
        CREATE TABLE bookmarks (
            id       INTEGER PRIMARY KEY,
            title    TEXT NOT NULL,
            url      TEXT NOT NULL,
            created  TEXT,
            modified TEXT,
            tags     TEXT NOT NULL
        );
        CREATE INDEX bookmarks_url ON bookmarks (url);
        """;
    create.ExecuteNonQuery();

    var insert = connection.CreateCommand();
    insert.CommandText = """
        INSERT INTO bookmarks (title, url, created, modified, tags)
        VALUES ($title, $url, $created, $modified, $tags)
        """;
    var title = insert.Parameters.Add("$title", SqliteType.Text);
    var url = insert.Parameters.Add("$url", SqliteType.Text);
    var created = insert.Parameters.Add("$created", SqliteType.Text);
    var modified = insert.Parameters.Add("$modified", SqliteType.Text);
    var tags = insert.Parameters.Add("$tags", SqliteType.Text);

    // Names of the folders we're in, outermost first.
    var folders = new List<string>();
    int linkCount = 0, problems = 0;

    foreach (var rawLine in File.ReadLines(file, Encoding.UTF8))
    {
        var line = rawLine.Trim();

        if (line.StartsWith("<DT><A", StringComparison.OrdinalIgnoreCase))
        {
            var m = linkRegex.Match(line);
            var attrs = m.Success ? ParseAttributes(m.Groups["attrs"].Value) : null;
            if (attrs == null || !attrs.TryGetValue("HREF", out var href) || href.Length == 0)
            {
                Console.WriteLine($"Link line not parsed correctly: {line}");
                problems++;
                continue;
            }

            var added = GetDate(attrs, "ADD_DATE");
            title.Value = CleanTitle(TagText(m.Groups["text"].Value));
            url.Value = href;
            created.Value = FormatDate(added);
            modified.Value = FormatDate(GetDate(attrs, "LAST_MODIFIED") ?? added);
            tags.Value = string.Join(", ", folders);
            insert.ExecuteNonQuery();
            linkCount++;
        }
        else if (line.StartsWith("<DT><H3", StringComparison.OrdinalIgnoreCase))
        {
            var m = folderRegex.Match(line);
            var name = m.Success ? CleanTitle(TagText(m.Groups["text"].Value)) : "";
            folders.Add(name.Length > 0 ? name : "untitled");
        }
        else if (line.StartsWith("</DL>", StringComparison.OrdinalIgnoreCase))
        {
            // The last </DL> closes the top level list, which isn't a folder.
            if (folders.Count > 0)
                folders.RemoveAt(folders.Count - 1);
        }
    }

    transaction.Commit();
    return (linkCount, problems);
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
