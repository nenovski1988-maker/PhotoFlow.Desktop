using System;
using System.Windows;

namespace PhotoFlow.Desktop;

public partial class LicenseBlockedWindow : Window
{
    public LicenseBlockedWindow(string reason, string licensePath)
    {
        InitializeComponent();
        ReasonText.Text = string.IsNullOrWhiteSpace(reason) ? "-" : reason;
        PathText.Text = licensePath;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
