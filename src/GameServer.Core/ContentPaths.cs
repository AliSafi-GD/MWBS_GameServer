namespace GameServer.Core;

public static class ContentPaths
{
    /// <summary>
    /// Makes a configured folder absolute. A relative path is taken from the host's content root (the folder of the app,
    /// or the project folder under <c>dotnet run</c>), not from the process working directory, which is C:\Windows\System32
    /// for a Windows Service. An absolute path is returned as it is.
    /// </summary>
    public static string Resolve(string path, string contentRoot) => Path.GetFullPath(path, contentRoot);
}
