using System.Windows;

namespace MutagenMon.App;

/// <summary>
/// A small
/// undecorated, buttonless window shown for the duration of a remote
/// (SSH) operation and dismissed automatically once it completes
/// (FR-9.6), never by the user.
/// </summary>
public partial class ConnectingIndicatorWindow : Window
{
    public ConnectingIndicatorWindow()
    {
        InitializeComponent();
    }

    public static async Task<T> RunAsync<T>(Window? owner, Func<Task<T>> operation)
    {
        var indicator = new ConnectingIndicatorWindow { Owner = owner };
        indicator.Show();
        try
        {
            return await operation();
        }
        finally
        {
            indicator.Close();
        }
    }

    public static async Task RunAsync(Window? owner, Func<Task> operation)
    {
        var indicator = new ConnectingIndicatorWindow { Owner = owner };
        indicator.Show();
        try
        {
            await operation();
        }
        finally
        {
            indicator.Close();
        }
    }
}
