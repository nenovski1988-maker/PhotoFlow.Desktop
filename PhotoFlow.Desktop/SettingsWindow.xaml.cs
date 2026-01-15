using System;
using System.IO;
using System.Windows;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace PhotoFlow.Desktop;

public partial class SettingsWindow : Window
{
    public string WorkspaceRoot { get; private set; }
    public string IncomingFolder { get; private set; }

    public SettingsWindow(string workspaceRoot, string incomingFolder)
    {
        InitializeComponent();

        WorkspaceRoot = workspaceRoot ?? "";
        IncomingFolder = incomingFolder ?? "";

        WorkspaceTextBox.Text = WorkspaceRoot;
        IncomingTextBox.Text = IncomingFolder;
    }

    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
            Title = "Select PhotoFlow workspace folder"
        };

        if (Directory.Exists(WorkspaceTextBox.Text))
            dlg.InitialDirectory = WorkspaceTextBox.Text;

        if (dlg.ShowDialog() == CommonFileDialogResult.Ok)
            WorkspaceTextBox.Text = dlg.FileName;
    }

    private void BrowseIncoming_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
            Title = "Select Incoming folder"
        };

        if (Directory.Exists(IncomingTextBox.Text))
            dlg.InitialDirectory = IncomingTextBox.Text;

        if (dlg.ShowDialog() == CommonFileDialogResult.Ok)
            IncomingTextBox.Text = dlg.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var ws = (WorkspaceTextBox.Text ?? "").Trim();
        var inc = (IncomingTextBox.Text ?? "").Trim();

        if (!EnsureFolderExistsOrOfferCreate(ref ws, "Workspace"))
            return;

        if (!EnsureFolderExistsOrOfferCreate(ref inc, "Incoming"))
            return;

        // Hard safety checks
        if (PathsEqual(ws, inc))
        {
            MessageBox.Show("Workspace and Incoming cannot be the same folder.", "Folders", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (IsSubPathOf(inc, ws) || IsSubPathOf(ws, inc))
        {
            MessageBox.Show(
                "Workspace and Incoming should be separate folders (not inside each other).",
                "Folders",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        WorkspaceRoot = ws;
        IncomingFolder = inc;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool EnsureFolderExistsOrOfferCreate(ref string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show($"{label} folder is empty.", "Folders", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (Directory.Exists(path))
            return true;

        var res = MessageBox.Show(
            $"{label} folder does not exist.\n\nCreate it?\n\n{path}",
            "Folders",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes)
            return false;

        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create folder:\n{ex.Message}", "Folders", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsSubPathOf(string maybeChild, string maybeParent)
    {
        var child = Norm(maybeChild);
        var parent = Norm(maybeParent);

        // parent must be prefix boundary-aware
        if (!child.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            return false;

        if (child.Length == parent.Length)
            return true;

        return child[parent.Length] == Path.DirectorySeparatorChar;
    }

    private static string Norm(string p)
    {
        var full = Path.GetFullPath(p);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
