using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TaskManager.Mcp;

[McpServerToolType]
public sealed class TaskTools(TaskService tasks)
{
    static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Cyrillic readable
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    static string J(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Validation errors are rethrown as McpException so the client sees the actual message.</summary>
    static T Run<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "list_projects", ReadOnly = true, Title = "List projects")]
    [Description("Lists projects with their keys (the prefix of issue IDs, e.g. TM in TM-12) and open issue counts.")]
    public string ListProjects() => Run(() => J(tasks.Projects()));

    [McpServerTool(Name = "list_labels", ReadOnly = true, Title = "List labels")]
    [Description("Lists all labels.")]
    public string ListLabels() => Run(() => J(tasks.Labels()));

    [McpServerTool(Name = "list_issues", ReadOnly = true, Title = "List or search issues")]
    [Description("Lists issues sorted by status then priority. By default only open issues (backlog, todo, in_progress) and only top-level ones. Use `query` for full-text search over titles and descriptions, or an exact key like TM-12.")]
    public string ListIssues(
        [Description("Project key or name")] string? project = null,
        [Description("Comma-separated statuses: backlog, todo, in_progress, done, canceled")] string? status = null,
        [Description("Label name")] string? label = null,
        [Description("urgent, high, medium, low or none")] string? priority = null,
        [Description("Full-text search (prefix match on words) or an issue key")] string? query = null,
        [Description("Include done and canceled issues")] bool include_closed = false,
        [Description("Include subtasks as separate rows")] bool include_subtasks = false,
        [Description("Max rows, 1-500")] int limit = 100) =>
        Run(() => J(tasks.List(project, status, label, priority, query, include_closed, include_subtasks, limit)));

    [McpServerTool(Name = "get_issue", ReadOnly = true, Title = "Get issue")]
    [Description("Returns an issue with its full Markdown description, labels, parent and subtasks. Images attached to the description are returned as image content.")]
    public List<ContentBlock> GetIssue([Description("Issue key, e.g. TM-12")] string key) => Run(() =>
    {
        var (issue, images) = tasks.Get(key);
        var result = new List<ContentBlock> { new TextContentBlock { Text = J(issue) } };
        foreach (var path in images.Take(5).Where(p => new FileInfo(p).Length <= 4 * 1024 * 1024))
            result.Add(ImageContentBlock.FromBytes(File.ReadAllBytes(path), MimeOf(path)));
        return result;
    });

    static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    [McpServerTool(Name = "create_issue", Title = "Create issue")]
    [Description("Creates an issue (or a subtask when `parent` is given). Description is Markdown; task lists (- [ ]) render as checkboxes. Unknown labels are created.")]
    public string CreateIssue(
        [Description("Issue title")] string title,
        [Description("Project key or name. May be omitted when `parent` is given.")] string? project = null,
        [Description("Markdown description")] string? description = null,
        [Description("backlog, todo (default), in_progress, done, canceled")] string? status = null,
        [Description("urgent, high, medium, low, none (default)")] string? priority = null,
        [Description("Label names")] string[]? labels = null,
        [Description("Parent issue key to create a subtask")] string? parent = null) =>
        Run(() => J(tasks.Create(project ?? "", title, description, status, priority, labels, parent)));

    [McpServerTool(Name = "update_issue", Idempotent = false, Title = "Update issue")]
    [Description("Updates fields of an issue; only the given fields change. Use `append_description` to add notes (e.g. what was done) without rewriting the description. Moving to another project changes the key.")]
    public string UpdateIssue(
        [Description("Issue key, e.g. TM-12")] string key,
        [Description("New title")] string? title = null,
        [Description("Replaces the whole Markdown description")] string? description = null,
        [Description("Markdown appended to the end of the description")] string? append_description = null,
        [Description("backlog, todo, in_progress, done, canceled")] string? status = null,
        [Description("urgent, high, medium, low, none")] string? priority = null,
        [Description("Label names to add (created if missing)")] string[]? add_labels = null,
        [Description("Label names to remove")] string[]? remove_labels = null,
        [Description("Move to this project (key or name)")] string? project = null,
        [Description("New parent key, or \"none\" to make it top-level")] string? parent = null) =>
        Run(() => J(tasks.Update(key, title, description, append_description, status, priority, add_labels, remove_labels, project, parent)));

    [McpServerTool(Name = "delete_issue", Destructive = true, Title = "Delete issue")]
    [Description("Deletes an issue together with its subtasks (soft delete: they disappear from the app).")]
    public string DeleteIssue([Description("Issue key, e.g. TM-12")] string key) =>
        Run(() => J(new { deleted = tasks.Archive(key) }));

    [McpServerTool(Name = "attach_image", Title = "Attach image")]
    [Description("Copies a local image file into the app and appends it to the issue description (shown inline in the app).")]
    public string AttachImage(
        [Description("Issue key, e.g. TM-12")] string key,
        [Description("Absolute path to a png/jpg/gif/bmp/webp file")] string path,
        [Description("Optional caption / alt text")] string? caption = null) =>
        Run(() => J(tasks.AttachImage(key, path, caption)));

    [McpServerTool(Name = "create_project", Title = "Create project")]
    [Description("Creates a project. The key (2-10 letters/digits) prefixes issue IDs; derived from the name when omitted.")]
    public string CreateProject([Description("Project name")] string name, [Description("Key, e.g. API")] string? key = null) =>
        Run(() => J(tasks.CreateProject(name, key)));

    [McpServerTool(Name = "create_label", Title = "Create label")]
    [Description("Creates a label.")]
    public string CreateLabel([Description("Label name")] string name, [Description("Hex color like #EB5757")] string? color = null) =>
        Run(() => J(tasks.CreateLabel(name, color)));
}
