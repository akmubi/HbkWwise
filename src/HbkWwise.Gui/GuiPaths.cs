namespace HbkWwise.Gui;

public static class GuiPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HbkWwise");

    public static string IndexPath => Path.Combine(Root, "index.json");

    public static string IndexCacheDirectory => Path.Combine(Root, "index-cache");
}
