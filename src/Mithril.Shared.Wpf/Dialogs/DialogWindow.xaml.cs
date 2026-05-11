using System.Windows;
using System.Windows.Input;

namespace Mithril.Shared.Wpf.Dialogs;

public partial class DialogWindow : Window
{
    private readonly DialogViewModelBase _viewModel;

    public DialogWindow(DialogViewModelBase viewModel, FrameworkElement content)
    {
        InitializeComponent();

        _viewModel = viewModel;

        TitleText.Text = viewModel.Title;
        PrimaryButton.Content = viewModel.PrimaryButtonText;
        // The XAML default (MaxWidth=560) suits prose-shaped dialogs; let each VM
        // override for image-preview / wide-content cases without bumping the
        // global cap and accidentally letting other dialogs grow.
        DialogContainer.MaxWidth = viewModel.MaxContentWidth;

        if (viewModel.SecondaryButtonText is { } secondaryText)
        {
            SecondaryButton.Content = secondaryText;
        }
        else
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }

        content.DataContext = viewModel;
        DialogContent.Content = content;

        viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(bool? result)
    {
        DialogResult = result;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.OnPrimaryAction())
        {
            DialogResult = true;
            Close();
        }
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = null;
        Close();
    }
}
