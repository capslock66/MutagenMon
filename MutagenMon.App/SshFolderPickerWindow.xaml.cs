using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Ssh;

namespace MutagenMon.App;

/// <summary>
/// ENDPOINT_PATH_PICKER_PLAN.md's Phase 3 of the "Browse SSH server…" flow
/// (FR-18.5): after picking one of <see cref="MutagenMonOptions.SshServers"/>,
/// lets the user navigate that server's remote folders — displayed as a
/// lazy-loading <see cref="TreeView"/> so opening the picker never fetches
/// more than the one directory level actually expanded — and create new
/// ones via <see cref="FolderNameInputDialog"/>. OK returns
/// "&lt;host&gt;:&lt;relative path&gt;", matching the format Mutagen itself
/// expects for an SSH endpoint.
/// </summary>
public partial class SshFolderPickerWindow : Window
{
    private static readonly object LazyPlaceholder = new();

    private SftpDirectoryBrowser? _browser;

    /// <summary>The relative path to auto-navigate to right after the next
    /// successful connection — set from the Alpha/Beta box's current value
    /// in the constructor, consumed (and cleared) the first time a server
    /// connects, so re-selecting the same server later doesn't repeat the
    /// navigation.</summary>
    private string? _pendingPath;

    /// <summary>Set only when the window closes via OK.</summary>
    public string? Result { get; private set; }

    /// <param name="servers">The "Browse SSH server…" picklist.</param>
    /// <param name="currentValue">The Alpha/Beta box's current text (e.g.
    /// "robbie:sources/appman") — if it starts with "`<host>`:" for one of
    /// <paramref name="servers"/>, that host is preselected in the combo
    /// box on open (instead of leaving it blank) and, once connected, the
    /// tree auto-navigates to and selects the remainder of the path.</param>
    public SshFolderPickerWindow(IReadOnlyList<SshServerEntry> servers, string? currentValue)
    {
        InitializeComponent();

        var hosts = servers.Select(s => s.Host).ToList();
        ServerCombo.ItemsSource = hosts;
        NoServersText.Visibility = servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerCombo.IsEnabled = servers.Count > 0;

        var colonIndex = currentValue?.IndexOf(':') ?? -1;
        if (colonIndex > 0)
        {
            var currentHost = currentValue![..colonIndex];
            var matchedHost = hosts.FirstOrDefault(h => h.Equals(currentHost, StringComparison.OrdinalIgnoreCase));
            if (matchedHost is not null)
            {
                _pendingPath = currentValue[(colonIndex + 1)..];
                ServerCombo.SelectedItem = matchedHost;
            }
        }

        Closed += (_, _) => _browser?.Dispose();
    }

    private async void OnServerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _browser?.Dispose();
        _browser = null;
        FolderTree.Items.Clear();
        NewFolderButton.IsEnabled = false;
        OkButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        SelectedPathBox.Text = "";

        var pendingPath = _pendingPath;
        _pendingPath = null;

        if (ServerCombo.SelectedItem is not string host)
            return;

        Cursor = Cursors.Wait;
        try
        {
            _browser = await Task.Run(() => new SftpDirectoryBrowser(host));
            var root = CreateNode(_browser.HomeDirectory, _browser.HomeDirectory);
            FolderTree.Items.Add(root);
            root.IsSelected = true;

            if (!string.IsNullOrEmpty(pendingPath))
                await NavigateToPathAsync(root, pendingPath);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Could not connect to '{host}':\n\n{ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            Cursor = null;
        }
    }

    /// <summary>Walks <paramref name="relativePath"/> segment by segment
    /// from <paramref name="root"/>, expanding and (force-)loading each
    /// node along the way, so a session's existing Alpha/Beta value (e.g.
    /// "sources/mutagenMon") opens the picker already drilled down to it
    /// instead of just sitting on the home directory. Stops at the deepest
    /// segment that actually exists on the server if the path (or part of
    /// it) doesn't.</summary>
    private async Task NavigateToPathAsync(TreeViewItem root, string relativePath)
    {
        var current = root;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            await LoadChildrenAsync(current);
            var childPath = $"{((string)current.Tag).TrimEnd('/')}/{segment}";
            var next = current.Items.OfType<TreeViewItem>().FirstOrDefault(n => (string)n.Tag == childPath);
            if (next is null)
                break;
            current.IsExpanded = true;
            current = next;
        }

        current.IsSelected = true;
        current.BringIntoView();
    }

    private TreeViewItem CreateNode(string path, string header)
    {
        var node = new TreeViewItem { Header = header, Tag = path };
        node.Items.Add(new TreeViewItem { Tag = LazyPlaceholder });
        node.Expanded += OnNodeExpanded;
        return node;
    }

    private async void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        var node = (TreeViewItem)sender;
        if (node.Items.Count == 1 && ReferenceEquals(((TreeViewItem)node.Items[0]).Tag, LazyPlaceholder))
            await LoadChildrenAsync(node);
    }

    /// <summary>(Re)fetches <paramref name="node"/>'s sub-folders from the
    /// server and replaces its children — used both for the first-ever
    /// expansion (lazy loading) and to refresh a node right after "New
    /// folder" creates something inside it.</summary>
    private async Task LoadChildrenAsync(TreeViewItem node)
    {
        var path = (string)node.Tag;
        Cursor = Cursors.Wait;
        try
        {
            var subDirectories = await Task.Run(() => _browser!.ListSubDirectories(path));
            node.Items.Clear();
            foreach (var name in subDirectories)
                node.Items.Add(CreateNode($"{path.TrimEnd('/')}/{name}", name));
        }
        catch (Exception ex)
        {
            node.Items.Clear();
            MessageBox.Show(
                this, $"MutagenMon could not list '{path}':\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    private void OnFolderTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var selected = FolderTree.SelectedItem as TreeViewItem;
        OkButton.IsEnabled = selected is not null;
        NewFolderButton.IsEnabled = selected is not null;

        SelectedPathBox.Text = selected is not null && ServerCombo.SelectedItem is string host
            ? $"{host}:{GetRelativePath(selected)}"
            : "";
    }

    /// <summary>Path of <paramref name="node"/> relative to the connected
    /// server's home directory — the same value both the live "Selected"
    /// preview and the final OK result are built from.</summary>
    private string GetRelativePath(TreeViewItem node)
    {
        var fullPath = (string)node.Tag;
        return fullPath == _browser!.HomeDirectory
            ? ""
            : fullPath[(_browser.HomeDirectory.TrimEnd('/').Length + 1)..];
    }

    private async void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not TreeViewItem selected || _browser is null)
            return;

        var dialog = new FolderNameInputDialog { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var parentPath = (string)selected.Tag;
        var newPath = $"{parentPath.TrimEnd('/')}/{dialog.Result}";

        Cursor = Cursors.Wait;
        try
        {
            await Task.Run(() => _browser!.CreateDirectory(newPath));
        }
        catch (Exception ex)
        {
            Cursor = null;
            MessageBox.Show(
                this, $"MutagenMon could not create folder '{dialog.Result}':\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Cursor = null;

        selected.IsExpanded = true;
        await LoadChildrenAsync(selected);
        var newNode = selected.Items.OfType<TreeViewItem>().FirstOrDefault(n => (string)n.Tag == newPath);
        if (newNode is not null)
            newNode.IsSelected = true;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not TreeViewItem selected || _browser is null || ServerCombo.SelectedItem is not string host)
            return;

        Result = $"{host}:{GetRelativePath(selected)}";
        DialogResult = true;
    }
}
