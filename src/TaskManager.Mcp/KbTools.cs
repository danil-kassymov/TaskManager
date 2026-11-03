using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace TaskManager.Mcp;

/// <summary>Knowledge base ("База данных"): folders of .md files the user registered as sources.</summary>
[McpServerToolType]
public sealed class KbTools(TaskService tasks)
{
    static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    static string Run(Func<object> f)
    {
        try { return JsonSerializer.Serialize(f(), Json); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "kb_list_sources", ReadOnly = true, Title = "List knowledge base sources")]
    [Description("Lists knowledge base sources: directories on disk whose .md files are the entries. Use the source name or id in other kb_* tools.")]
    public string ListSources() => Run(tasks.KbSources);

    [McpServerTool(Name = "kb_add_source", Title = "Add knowledge base source")]
    [Description("Registers an existing directory as a knowledge base source.")]
    public string AddSource([Description("Absolute directory path")] string path, [Description("Display name (defaults to folder name)")] string? name = null) =>
        Run(() => tasks.KbAddSource(path, name));

    [McpServerTool(Name = "kb_list", ReadOnly = true, Title = "List or search knowledge base entries")]
    [Description("Without query/tag: lists folders and entries (optionally inside `folder`). With `query` (all words must match title, path or text) or `tag` (without #): searches entries. Entry paths are relative to the source, with '/'.")]
    public string List(
        [Description("Source name or id; omit to search all sources")] string? source = null,
        [Description("Folder inside the source, e.g. \"notes/api\"")] string? folder = null,
        [Description("Tag to filter by, e.g. backend")] string? tag = null,
        [Description("Words to search for")] string? query = null,
        [Description("Max entries")] int limit = 200) =>
        Run(() => tasks.KbList(source, folder, tag, query, limit));

    [McpServerTool(Name = "kb_read", ReadOnly = true, Title = "Read knowledge base entry")]
    [Description("Returns an entry's markdown content, its #tags, linked issues and `link_markdown` to reference it from an issue description.")]
    public string Read([Description("Source name or id")] string source, [Description("Entry path, e.g. notes/api.md")] string path) =>
        Run(() => tasks.KbRead(source, path));

    [McpServerTool(Name = "kb_write", Title = "Create or edit knowledge base entry")]
    [Description("Creates or overwrites an entry with `content`, or appends `append` to its end. Missing folders are created; .md is added to the path if missing. Tags are written in text as #tag.")]
    public string Write(
        [Description("Source name or id")] string source,
        [Description("Entry path, e.g. notes/Architecture.md")] string path,
        [Description("Full new content (replaces the file)")] string? content = null,
        [Description("Markdown appended to the end of the entry")] string? append = null) =>
        Run(() => tasks.KbWrite(source, path, content, append));

    [McpServerTool(Name = "kb_create_folder", Title = "Create knowledge base folder")]
    [Description("Creates a folder (a real directory) inside a source.")]
    public string CreateFolder([Description("Source name or id")] string source, [Description("Folder path, e.g. notes/design")] string path) =>
        Run(() => tasks.KbCreateFolder(source, path));

    [McpServerTool(Name = "kb_rename", Title = "Rename knowledge base entry or folder")]
    [Description("Renames an entry or folder in place. Issue links and kb: links in issue descriptions are updated.")]
    public string Rename([Description("Source name or id")] string source, [Description("Current path")] string path,
        [Description("New name (without folder)")] string new_name) =>
        Run(() => tasks.KbRename(source, path, new_name));

    [McpServerTool(Name = "kb_move", Title = "Move knowledge base entry or folder")]
    [Description("Moves an entry or folder into another folder of the same source (\"\" = root). Links are updated.")]
    public string Move([Description("Source name or id")] string source, [Description("Current path")] string path,
        [Description("Target folder")] string folder) =>
        Run(() => tasks.KbMove(source, path, folder));

    [McpServerTool(Name = "kb_list_tags", ReadOnly = true, Title = "List tags")]
    [Description("Lists #tags used in entries with their colors and entry counts.")]
    public string ListTags([Description("Source name or id; omit for all")] string? source = null) => Run(() => tasks.KbTags(source));

    [McpServerTool(Name = "kb_set_tag_color", Idempotent = true, Title = "Set tag color")]
    [Description("Sets the display color of a tag.")]
    public string SetTagColor([Description("Tag, with or without #")] string tag, [Description("Hex color like #4CB782")] string color) =>
        Run(() => tasks.KbSetTagColor(tag, color));

    [McpServerTool(Name = "link_entry", Title = "Link entry to issue")]
    [Description("Attaches a knowledge base entry to an issue (shown in the issue's \"Записи из базы\"). Set unlink=true to detach.")]
    public string LinkEntry(
        [Description("Issue key, e.g. TM-12")] string key,
        [Description("Source name or id")] string source,
        [Description("Entry path")] string path,
        [Description("Detach instead of attach")] bool unlink = false) =>
        Run(() => tasks.LinkEntry(key, source, path, unlink));
}
