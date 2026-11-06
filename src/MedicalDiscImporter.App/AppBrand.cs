namespace MedicalDiscImporter.App;

internal static class AppBrand
{
    public static Icon Icon { get; } = LoadIcon();
    private static Icon LoadIcon()
    {
        using var stream = typeof(AppBrand).Assembly.GetManifestResourceStream("MedicalDiscImporter.App.Application.ico")
            ?? throw new InvalidOperationException("アプリアイコンがありません。");
        using var source = new Icon(stream, 32, 32);
        return (Icon)source.Clone();
    }
}
