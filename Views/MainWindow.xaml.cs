using System.Windows;
using GsmAgent.ViewModels;

namespace GsmAgent.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Closing += (s, e) =>
        {
            var result = MessageBox.Show(
                "Bạn có chắc muốn thoát GSM Agent?",
                "Xác nhận thoát",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }

            _viewModel.Cleanup();
        };
    }
}
