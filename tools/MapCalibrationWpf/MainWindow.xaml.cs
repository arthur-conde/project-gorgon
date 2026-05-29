namespace Mithril.Tools.MapCalibrationWpf;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ViewModels.MainViewModel(
            new Services.AreaCatalog(),
            new Services.PgInstallResolver());
    }
}
