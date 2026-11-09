namespace Spacewise.Desktop;

public static class AppPaths
{
    public static bool Portable { get; private set; }
    public static string Data { get; private set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spacewise");

    public static void Configure(string[] args)
    {
        Portable = args.Contains("--portable") || System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "portable.flag"));
        Data = Portable ? System.IO.Path.Combine(AppContext.BaseDirectory, "Data")
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spacewise");
    }
}
