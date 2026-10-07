# File2Database

A Windows command-line tool that puts the links in a Netscape-format bookmarks file (as exported by Chrome, Edge or Firefox) into an SQLite database, for browsing and querying in a tool like [DB Browser for SQLite](https://sqlitebrowser.org/). Sister tool to [File2Folders](https://github.com/mwattsun/File2Folders).

## Usage

```
File2Database bookmarksfile [database] [options]
```

The database defaults to the bookmarks file with a `.db` extension (`bookmarks_10_5_26.html` → `bookmarks_10_5_26.db`). If it already exists, the bookmarks are merged into it (see below) unless you add `--overwrite`.

| Option | Effect |
|---|---|
| `--overwrite` | Replace the database instead of merging into it |
| `-h`, `--help` | Show help |

Exit code is 0 on success, 1 if the bookmarks file can't be read or the database can't be written, and 2 if some links couldn't be read (listed in the output).

## The `bookmarks` table

| Column | Contents |
|---|---|
| `id` | Row number, in the order links were first added |
| `title` | The link's title |
| `url` | The link's address |
| `created` | When the bookmark was added (`ADD_DATE`), local time, as `yyyy-MM-dd HH:mm:ss` |
| `modified` | `LAST_MODIFIED`, or `created` when there is none |
| `tags` | The folders the link is in, outermost first, separated by `, ` — a link in *Philosophy › Eastern* gets `Philosophy, Eastern`. The browser's toolbar folder (Firefox's *Bookmarks Toolbar*, Chrome's *Bookmarks bar*, Edge's *Favorites bar*) is left out. A link in more than one folder lists each, separated by ` \| ` — `Philosophy, Eastern \| Reading` |
| `notes` | Empty, for your own notes. Merging never changes it |

There is one row per URL. When a URL appears more than once, it keeps the created and modified dates of its earliest copy (the one with the earliest created date), and the title of the first one in the file.

Titles and folder names keep everything except extra whitespace (runs of spaces become one), `�` (which becomes `-`), YouTube's `▶ ` marker and leading `(3) ` notification counts.

## Merging

Running File2Database on a newer export with the same database merges it in, so the database can keep growing as you export over time:

- Links not in the database yet are added.
- Links already there get the title and tags from the new file, but keep whichever created and modified dates are earliest. Their `notes` are never touched, so hand edits there are safe; edits to `title` and `tags` are replaced by the new file's.
- Links that aren't in the new file are left alone, so bookmarks you've deleted in the browser stay in the database.

The work is done on a temporary copy that is swapped in at the end, so if something goes wrong the existing database is left alone. A database made by the first version of File2Database (with a row per copy of a link) can't be merged into; rebuild it with `--overwrite`.

## Example queries

```sql
-- Everything under a top-level Philosophy folder, at any depth
SELECT title, url, tags FROM bookmarks WHERE ' | ' || tags LIKE '% | Philosophy%';

-- Links that are in more than one folder
SELECT title, url, tags FROM bookmarks WHERE tags LIKE '% | %';

-- Added in 2020
SELECT * FROM bookmarks WHERE created LIKE '2020-%';
```

## Publish

Settings are in `Properties\PublishProfiles\FolderProfile.pubxml` (self-contained, single file, trimmed, to `D:\Tools\bin`; the native SQLite library is bundled into the exe):

```
dotnet publish File2Database.csproj -p:PublishProfile=FolderProfile
```
