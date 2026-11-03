using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using TaskManager.Data;

namespace TaskManager.Kb;

public sealed class KbSource
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Sort { get; set; }
    /// <summary>Open the Obsidian vault after changes so Obsidian Sync uploads them.</summary>
    public bool ObsidianSync { get; set; }
}

/// <summary>A markdown file inside a source. <see cref="Path"/> is relative to the source root, with '/' separators.</summary>
public sealed record KbEntry(long SourceId, string Path, string Title, string Folder, DateTime Modified, IReadOnlyList<string> Tags);

public sealed record KbLink(long SourceId, string Path, string SourceName, bool Exists)
{
    public string Title => KnowledgeBase.TitleOf(Path);
    public string Folder => KnowledgeBase.FolderOf(Path);
}

/// <summary>
/// Knowledge base: user-chosen directories of .md files. Files and folders live on disk as-is;
/// the database only keeps the list of sources, tag colors and issue ↔ entry links.
/// </summary>
public sealed partial class KnowledgeBase(Db db)
{
    public static readonly string[] TagPalette =
        ["#EB5757", "#F2994A", "#F2C94C", "#4CB782", "#26B5CE", "#5E6AD2", "#BB87FC", "#E879B9", "#9A9CA5", "#7FB069"];

    // Parsed tags and lower-cased text per file, invalidated by modification time.
    readonly Dictionary<string, (DateTime Modified, string[] Tags, string Text)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after TaskManager itself changed files of a source (used to trigger Obsidian Sync).</summary>
    public Action<KbSource>? Changed { get; set; }

    void Touched(long sourceId)
    {
        if (Changed is { } handler) handler(Source(sourceId));
    }

    // ---------------- Sources ----------------

    public List<KbSource> Sources() =>
        db.Conn.Query<KbSource>("SELECT * FROM kb_sources ORDER BY sort, id").ToList();

    public KbSource Source(long id) =>
        db.Conn.QuerySingleOrDefault<KbSource>("SELECT * FROM kb_sources WHERE id=@id", new { id })
        ?? throw new ArgumentException($"Источник базы {id} не найден");

    public KbSource AddSource(string path, string? name = null)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new ArgumentException($"Папка не найдена: {full}");
        if (Sources().Any(s => s.Path.Equals(full, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Папка уже добавлена: {full}");
        name = string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(full).Name : name.Trim();
        var sort = db.Conn.ExecuteScalar<long>("SELECT COALESCE(MAX(sort), 0) + 1 FROM kb_sources");
        var sync = TaskManager.Kb.ObsidianSync.IsInVault(full); // folders of an Obsidian vault sync by default
        var id = db.Conn.ExecuteScalar<long>(
            "INSERT INTO kb_sources(name, path, sort, obsidian_sync) VALUES (@name, @full, @sort, @sync) RETURNING id",
            new { name, full, sort, sync });
        return Source(id);
    }

    public void SetObsidianSync(long id, bool enabled) =>
        db.Conn.Execute("UPDATE kb_sources SET obsidian_sync=@enabled WHERE id=@id", new { id, enabled });

    public void RenameSource(long id, string name) =>
        db.Conn.Execute("UPDATE kb_sources SET name=@name WHERE id=@id", new { id, name = name.Trim() });

    /// <summary>Removes the source from the app only; files on disk are untouched.</summary>
    public void RemoveSource(long id) => db.Conn.Execute("DELETE FROM kb_sources WHERE id=@id", new { id });

    /// <summary>Source by id, name or folder path (for MCP).</summary>
    public KbSource FindSource(string idNameOrPath)
    {
        var s = idNameOrPath.Trim();
        var all = Sources();
        return (long.TryParse(s, out var id) ? all.FirstOrDefault(x => x.Id == id) : null)
               ?? all.FirstOrDefault(x => x.Name.Equals(s, StringComparison.CurrentCultureIgnoreCase))
               ?? all.FirstOrDefault(x => x.Path.Equals(System.IO.Path.TrimEndingDirectorySeparator(s), StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"Источник «{s}» не найден. Есть: {string.Join(", ", all.Select(x => $"{x.Name} ({x.Id})"))}");
    }

    // ---------------- Paths ----------------

    public static string Normalize(string rel) => rel.Replace('\\', '/').Trim('/').Trim();
    public static string TitleOf(string rel) => System.IO.Path.GetFileNameWithoutExtension(rel.Split('/')[^1]);
    public static string FolderOf(string rel) => rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";

    /// <summary>Absolute path of <paramref name="rel"/>; refuses anything outside the source root.</summary>
    public string Resolve(long sourceId, string rel)
    {
        var root = Source(sourceId).Path;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, Normalize(rel)));
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Путь выходит за пределы источника");
        return full;
    }

    static string RelOf(string root, string full) => System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');

    static bool IsHidden(string name) => name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> Walk(string dir, bool files)
    {
        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir).Where(d => !IsHidden(System.IO.Path.GetFileName(d))).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        if (files)
        {
            IEnumerable<string> mds;
            try { mds = Directory.EnumerateFiles(dir, "*.md").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { mds = []; }
            foreach (var f in mds) yield return f;
        }
        foreach (var d in subdirs)
        {
            if (!files) yield return d;
            foreach (var x in Walk(d, files)) yield return x;
        }
    }

    // ---------------- Entries ----------------

    public List<string> Folders(long sourceId)
    {
        var root = Source(sourceId).Path;
        return Walk(root, files: false).Select(d => RelOf(root, d)).OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public List<KbEntry> Entries(long? sourceId = null)
    {
        var result = new List<KbEntry>();
        foreach (var s in Sources().Where(s => sourceId is null || s.Id == sourceId))
        {
            if (!Directory.Exists(s.Path)) continue;
            foreach (var file in Walk(s.Path, files: true))
                if (Load(s, file) is { } e) result.Add(e);
        }
        return result;
    }

    public KbEntry? Entry(long sourceId, string rel)
    {
        var full = Resolve(sourceId, rel);
        return File.Exists(full) ? Load(Source(sourceId), full) : null;
    }

    KbEntry? Load(KbSource s, string full)
    {
        var info = new FileInfo(full);
        if (!info.Exists) return null;
        var (tags, _) = Analyze(full, info.LastWriteTimeUtc);
        var rel = RelOf(s.Path, full);
        return new KbEntry(s.Id, rel, TitleOf(rel), FolderOf(rel), info.LastWriteTime, tags);
    }

    (string[] Tags, string Text) Analyze(string full, DateTime modified)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(full, out var c) && c.Modified == modified) return (c.Tags, c.Text);
        }
        string text;
        try { text = File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = ""; }
        var tags = ParseTags(text).ToArray();
        lock (_cache) _cache[full] = (modified, tags, text.ToLowerInvariant());
        return (tags, text);
    }

    public string Read(long sourceId, string rel)
    {
        var full = Resolve(sourceId, rel);
        if (!File.Exists(full)) throw new ArgumentException($"Запись не найдена: {rel}");
        return File.ReadAllText(full);
    }

    public void Write(long sourceId, string rel, string content)
    {
        var full = Resolve(sourceId, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        Touched(sourceId);
    }

    static readonly char[] Invalid = System.IO.Path.GetInvalidFileNameChars();

    public static string SafeName(string name)
    {
        var clean = new string(name.Trim().Select(c => Invalid.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Без названия" : clean;
    }

    /// <summary>Creates "<paramref name="title"/>.md" in the folder; adds " 2", " 3"... if the name is taken.</summary>
    public KbEntry Create(long sourceId, string folder, string title, string content = "")
    {
        var dir = Resolve(sourceId, folder);
        Directory.CreateDirectory(dir);
        var name = SafeName(title);
        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        var full = System.IO.Path.Combine(dir, name + ".md");
        for (var i = 2; File.Exists(full); i++) full = System.IO.Path.Combine(dir, $"{name} {i}.md");
        File.WriteAllText(full, content, new UTF8Encoding(false));
        Touched(sourceId);
        return Load(Source(sourceId), full)!;
    }

    public string CreateFolder(long sourceId, string parent, string name)
    {
        var full = System.IO.Path.Combine(Resolve(sourceId, parent), SafeName(name));
        Resolve(sourceId, RelOf(Source(sourceId).Path, full)); // validates
        Directory.CreateDirectory(full);
        Touched(sourceId);
        return RelOf(Source(sourceId).Path, full);
    }

    /// <summary>Renames a file or folder in place and rewrites issue links pointing at it. Returns the new relative path.</summary>
    public string Rename(long sourceId, string rel, string newName)
    {
        rel = Normalize(rel);
        var full = Resolve(sourceId, rel);
        var isDir = Directory.Exists(full);
        if (!isDir && !File.Exists(full)) throw new ArgumentException($"Не найдено: {rel}");
        var name = SafeName(newName);
        if (!isDir && !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name += ".md";
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full)!, name);
        if (target.Equals(full, StringComparison.Ordinal)) return rel;
        if (!target.Equals(full, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target)))
            throw new InvalidOperationException($"«{name}» уже существует");
        if (isDir) Directory.Move(full, target);
        else File.Move(full, target);
        var newRel = RelOf(Source(sourceId).Path, target);
        RewriteLinks(sourceId, rel, newRel, isDir);
        Touched(sourceId);
        return newRel;
    }

    /// <summary>Moves an entry or folder into another folder of the same source.</summary>
    public string Move(long sourceId, string rel, string targetFolder)
    {
        rel = Normalize(rel);
        var full = Resolve(sourceId, rel);
        var isDir = Directory.Exists(full);
        var dest = System.IO.Path.Combine(Resolve(sourceId, targetFolder), System.IO.Path.GetFileName(full));
        if (isDir && (dest + System.IO.Path.DirectorySeparatorChar).StartsWith(full + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нельзя переместить папку внутрь самой себя");
        if (dest.Equals(full, StringComparison.OrdinalIgnoreCase)) return rel;
        if (File.Exists(dest) || Directory.Exists(dest)) throw new InvalidOperationException("Там уже есть элемент с таким именем");
        if (isDir) Directory.Move(full, dest);
        else File.Move(full, dest);
        var newRel = RelOf(Source(sourceId).Path, dest);
        RewriteLinks(sourceId, rel, newRel, isDir);
        Touched(sourceId);
        return newRel;
    }

    void RewriteLinks(long sourceId, string oldRel, string newRel, bool isDir)
    {
        using var tx = db.Conn.BeginTransaction();
        if (isDir)
            db.Conn.Execute("""
                UPDATE issue_links SET path = @newRel || substr(path, length(@oldRel) + 1)
                WHERE source_id = @sourceId AND (path LIKE @prefix ESCAPE '\')
                """, new { sourceId, oldRel, newRel, prefix = LikeEscape(oldRel) + "/%" }, tx);
        else
            db.Conn.Execute("UPDATE issue_links SET path = @newRel WHERE source_id = @sourceId AND path = @oldRel",
                new { sourceId, oldRel, newRel }, tx);

        // Markdown links in issue descriptions: kb:<id>/<escaped path>
        var oldUrl = LinkUrl(sourceId, oldRel);
        var newUrl = LinkUrl(sourceId, newRel);
        db.Conn.Execute("""
            UPDATE issues SET description = replace(description, @oldUrl, @newUrl)
            WHERE instr(description, @oldUrl) > 0
            """, new { oldUrl = isDir ? oldUrl + "/" : oldUrl + ")", newUrl = isDir ? newUrl + "/" : newUrl + ")" }, tx);
        tx.Commit();
    }

    static string LikeEscape(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>Call after a file/folder was deleted: drops issue links to it.</summary>
    public void ForgetDeleted(long sourceId, string rel)
    {
        rel = Normalize(rel);
        db.Conn.Execute("DELETE FROM issue_links WHERE source_id = @sourceId AND (path = @rel OR path LIKE @prefix ESCAPE '\\')",
            new { sourceId, rel, prefix = LikeEscape(rel) + "/%" });
        Touched(sourceId);
    }

    /// <summary>Words must all be present in the title, path or text; tag filters by exact tag.</summary>
    public List<KbEntry> Search(long? sourceId, string? query, string? tag = null, int limit = 200)
    {
        var words = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var t = tag?.TrimStart('#');
        var result = new List<KbEntry>();
        foreach (var s in Sources().Where(s => sourceId is null || s.Id == sourceId))
        {
            if (!Directory.Exists(s.Path)) continue;
            foreach (var file in Walk(s.Path, files: true))
            {
                var info = new FileInfo(file);
                var (tags, _) = Analyze(file, info.LastWriteTimeUtc);
                if (t is { Length: > 0 } && !tags.Contains(t, StringComparer.CurrentCultureIgnoreCase)) continue;
                string text;
                lock (_cache) text = _cache[file].Text;
                var rel = RelOf(s.Path, file);
                var hay = rel.ToLowerInvariant();
                if (!words.All(w => hay.Contains(w) || text.Contains(w))) continue;
                result.Add(new KbEntry(s.Id, rel, TitleOf(rel), FolderOf(rel), info.LastWriteTime, tags));
                if (result.Count >= limit) return result;
            }
        }
        return result;
    }

    // ---------------- Tags ----------------

    // Code and link targets never contain tags: "`#x`", "[a](#anchor)".
    [GeneratedRegex(@"```.*?```|~~~.*?~~~|`[^`\n]*`|\]\([^)\s]*\)", RegexOptions.Singleline)]
    private static partial Regex IgnoredRegex();

    // A tag starts after whitespace/punctuation (not inside words, urls or links) and may contain letters, digits, _ - /
    [GeneratedRegex(@"(?<![\p{L}\p{N}_&/#\]\)\\:])#([\p{L}\p{N}_][\p{L}\p{N}_\-/]*)")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^(\d+|[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex NotATag();

    public static IEnumerable<string> ParseTags(string markdown)
    {
        var text = IgnoredRegex().Replace(markdown ?? "", " ");
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (Match m in TagRegex().Matches(text))
        {
            var tag = m.Groups[1].Value.TrimEnd('-', '/', '_');
            if (tag.Length == 0) continue;
            // "#123" is an issue number, "#EB5757" is a color — but "#todo" or "#a1" are tags.
            if (NotATag().IsMatch(tag) && tag.Any(char.IsDigit)) continue;
            if (seen.Add(tag)) yield return tag;
        }
    }

    /// <summary>Colors for the given tags; tags seen for the first time get a random palette color, persisted.</summary>
    public Dictionary<string, string> TagColors(IEnumerable<string> tags)
    {
        var known = db.Conn.Query<(string Name, string Color)>("SELECT name, color FROM tags")
            .ToDictionary(t => t.Name, t => t.Color, StringComparer.CurrentCultureIgnoreCase);
        var missing = tags.Where(t => !known.ContainsKey(t)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            using var tx = db.Conn.BeginTransaction();
            foreach (var t in missing)
            {
                var color = TagPalette[Random.Shared.Next(TagPalette.Length)];
                db.Conn.Execute("INSERT OR IGNORE INTO tags(name, color) VALUES (@t, @color)", new { t, color }, tx);
                known[t] = color;
            }
            tx.Commit();
        }
        return known;
    }

    public void SetTagColor(string tag, string color) =>
        db.Conn.Execute("INSERT INTO tags(name, color) VALUES (@tag, @color) ON CONFLICT(name) DO UPDATE SET color = excluded.color",
            new { tag = tag.TrimStart('#'), color });

    public List<(string Tag, int Count)> TagCounts(long? sourceId = null) =>
        Entries(sourceId).SelectMany(e => e.Tags)
            .GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (g.First(), g.Count()))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Item1, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // ---------------- Issue links ----------------

    public void Link(long issueId, long sourceId, string rel) =>
        db.Conn.Execute("INSERT OR IGNORE INTO issue_links(issue_id, source_id, path) VALUES (@issueId, @sourceId, @rel)",
            new { issueId, sourceId, rel = Normalize(rel) });

    public void Unlink(long issueId, long sourceId, string rel) =>
        db.Conn.Execute("DELETE FROM issue_links WHERE issue_id = @issueId AND source_id = @sourceId AND path = @rel",
            new { issueId, sourceId, rel = Normalize(rel) });

    public List<KbLink> LinksOf(long issueId)
    {
        var rows = db.Conn.Query<(long SourceId, string Path, string Name, string Root)>("""
            SELECT l.source_id, l.path, s.name, s.path FROM issue_links l JOIN kb_sources s ON s.id = l.source_id
            WHERE l.issue_id = @issueId ORDER BY l.path
            """, new { issueId });
        return rows.Select(r => new KbLink(r.SourceId, r.Path, r.Name,
            File.Exists(System.IO.Path.Combine(r.Root, r.Path.Replace('/', System.IO.Path.DirectorySeparatorChar))))).ToList();
    }

    public List<long> IssuesLinking(long sourceId, string rel) =>
        db.Conn.Query<long>("""
            SELECT l.issue_id FROM issue_links l JOIN issues i ON i.id = l.issue_id
            WHERE l.source_id = @sourceId AND l.path = @rel AND i.archived = 0
            """, new { sourceId, rel = Normalize(rel) }).ToList();

    // ---------------- Markdown links ----------------

    public const string LinkScheme = "kb:";

    public static string LinkUrl(long sourceId, string rel) =>
        $"{LinkScheme}{sourceId}/{string.Join('/', Normalize(rel).Split('/').Select(Uri.EscapeDataString))}";

    public static string LinkMarkdown(long sourceId, string rel) => $"[{TitleOf(rel)}]({LinkUrl(sourceId, rel)})";

    public static bool TryParseLink(string url, out long sourceId, out string rel)
    {
        sourceId = 0;
        rel = "";
        if (!url.StartsWith(LinkScheme, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = url[LinkScheme.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || !long.TryParse(rest[..slash], out sourceId)) return false;
        rel = Uri.UnescapeDataString(rest[(slash + 1)..]);
        return rel.Length > 0;
    }
}
