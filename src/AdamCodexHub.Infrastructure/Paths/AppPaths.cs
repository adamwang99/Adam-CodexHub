namespace AdamCodexHub.Infrastructure.Paths;

public sealed class AppPaths
{
    public AppPaths()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdamCodexHub"))
    {
    }

    private AppPaths(string root)
    {
        Root = Path.GetFullPath(root);

        Data = Path.Combine(Root, "data");
        Logs = Path.Combine(Root, "logs");
        Backups = Path.Combine(Root, "backups");
        Secrets = Path.Combine(Root, "secrets");

        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(Secrets);
    }

    public static AppPaths ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return new AppPaths(root);
    }

    public string Root { get; }
    public string Data { get; }
    public string Logs { get; }
    public string Backups { get; }
    public string Secrets { get; }

    public string SettingsFile => Path.Combine(Data, "settings.json");
    public string DatabaseFile => Path.Combine(Data, "adam-codexhub.db");
}
