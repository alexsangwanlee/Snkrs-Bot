using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace OrdHelper.App;

public static class Theme
{
    /// Loads Theme.xaml (EmbeddedResource "OrdHelper.App.Theme.xaml") into the app's resources.
    public static void Apply(Application app)
    {
        using var stream = typeof(Theme).Assembly.GetManifestResourceStream("OrdHelper.App.Theme.xaml")
            ?? throw new InvalidOperationException("Embedded resource OrdHelper.App.Theme.xaml not found.");
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
    }

    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
}
