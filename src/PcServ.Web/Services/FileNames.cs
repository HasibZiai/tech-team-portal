namespace PcServ.Web.Services;

/// <summary>
/// File names and paths that arrive from browsers and from lists written on another computer. Path.GetFileName only
/// understands the current system's folder separator, so on Linux it would leave "C:\folder\photo.jpg" whole.
/// </summary>
public static class FileNames
{
    /// <summary>The name alone, with any Windows or Linux folders removed.</summary>
    public static string WithoutFolders(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>A relative path using forward slashes, which every system accepts.</summary>
    public static string WithForwardSlashes(string path) => path.Replace('\\', '/');
}
