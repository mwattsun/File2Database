# File2Database

A Windows command-line tool that puts the links in a Netscape-format bookmarks file (as exported by Chrome, Edge or Firefox) into an SQLite database, for browsing and querying in a tool like [DB Browser for SQLite](https://sqlitebrowser.org/). Sister tool to [File2Folders](https://github.com/mwattsun/File2Folders).

## Usage

```
File2Database bookmarksfile [database] [options]
```

The database defaults to the bookmarks file with a `.db` extension (`bookmarks_10_5_26.html` → `bookmarks_10_5_26.db`). If it already exists, nothing is changed unless you add `--overwrite`.

| Option | Effect |
|---|---|
| `--overwrite` | Replace the database if it already exists |
| `-h`, `--help` | Show help |

Exit code is 0 on success, 1 if the bookmarks file can't be read or the database can't be written, and 2 if some links couldn't be read (listed in the output).

## The `bookmarks` table

| Column | Contents |
|---|---|
| `id` | Row number, in the order the links appear in the file |
| `title` | The link's title |
| `url` | The link's address |
| `created` | When the bookmark was added (`ADD_DATE`), local time, as `yyyy-MM-dd HH:mm:ss` |
| `modified` | `LAST_MODIFIED`, or `created` when there is none |
| `tags` | The folders the link is in, outermost first, separated by `, ` — a link in *Philosophy › Eastern* gets `Philosophy, Eastern` |

A link that is in more than one folder gets a row for each. Titles and folder names keep everything except extra whitespace (runs of spaces become one), `�` (which becomes `-`), YouTube's `▶ ` marker and leading `(3) ` notification counts.

The database is built under a temporary name and swapped in at the end, so if something goes wrong the existing database is left alone.

## Example queries

```sql
-- Everything under Philosophy, at any depth
SELECT title, url FROM bookmarks WHERE tags = 'Philosophy' OR tags LIKE 'Philosophy, %';

-- Links bookmarked more than once
SELECT url, count(*), group_concat(tags, ' | ') FROM bookmarks GROUP BY url HAVING count(*) > 1;

-- Added in 2020
SELECT * FROM bookmarks WHERE created LIKE '2020-%';
```

## Publish

Settings are in `Properties\PublishProfiles\FolderProfile.pubxml` (self-contained, single file, trimmed, to `D:\Tools\bin`; the native SQLite library is bundled into the exe):

```
dotnet publish File2Database.csproj -p:PublishProfile=FolderProfile
```
