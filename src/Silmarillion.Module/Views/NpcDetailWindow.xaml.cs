using System.Windows;
using System.Windows.Input;
using Mithril.Shared.Wpf;

namespace Silmarillion.Views;

public partial class NpcDetailWindow : Window
{
    public NpcDetailWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ExportImageButton_Click(object sender, RoutedEventArgs e)
        => DetailExportFeedback.Run(VisualImageExporter.CopyToClipboard(DetailBody), ExportImageButton);
}
