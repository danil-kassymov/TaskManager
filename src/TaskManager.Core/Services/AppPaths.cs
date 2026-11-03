namespace TaskManager.Services;

public sealed class AppPaths
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "data.db");
    public string Attachments => Path.Combine(Root, "attachments");
    public string Settings => Path.Combine(Root, "settings.json");

    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Attachments);
    }

    /// <summary>
    /// %LOCALAPPDATA%\TaskManager by default; a "portable" file next to the exe keeps data
    /// in a "data" folder beside it; TASKMANAGER_DATA overrides both.
    /// </summary>
    public static AppPaths Default()
    {
        var env = Environment.GetEnvironmentVariable("TASKMANAGER_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return new AppPaths(env);
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable"))) return new AppPaths(Path.Combine(exeDir, "data"));
        return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskManager"));
    }
}
