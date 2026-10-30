using System.Collections.ObjectModel;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace RemoteManager.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<FATabViewItem, SessionView> sessions = [];
    // FATabView.TabItems is a read-only view in FluentAvalonia 3; tabs are owned through TabItemsSource.
    private readonly ObservableCollection<FATabViewItem> tabItems = [];
    private int sessionCounter;

    public MainWindow()
    {
        InitializeComponent();
        Tabs.TabItemsSource = tabItems;
        Tabs.AddTabButtonClick += (_, _) => AddSession();
        Tabs.TabCloseRequested += (_, e) => CloseSession((FATabViewItem)e.Tab);
        Tabs.SelectionChanged += (_, _) => ShowSelectedSession();
        AddSession();
    }

    public IReadOnlyList<SessionView> Sessions => [.. sessions.Values];

    public SessionView AddSession()
    {
        var view = new SessionView($"Session {++sessionCounter}");
        var tab = new FATabViewItem { Header = view.Title };
        view.TitleChanged += (_, _) => tab.Header = view.Title;

        sessions.Add(tab, view);
        view.IsVisible = false;
        SessionHost.Children.Add(view);

        tabItems.Add(tab);
        Tabs.SelectedItem = tab;
        ShowSelectedSession();
        return view;
    }

    public void SelectSession(SessionView view)
    {
        Tabs.SelectedItem = sessions.First(pair => pair.Value == view).Key;
        ShowSelectedSession();
    }

    public void CloseSession(SessionView view) => CloseSession(sessions.First(pair => pair.Value == view).Key);

    private void CloseSession(FATabViewItem tab)
    {
        if (!sessions.Remove(tab, out SessionView? view))
            return;

        tabItems.Remove(tab);
        SessionHost.Children.Remove(view);
        view.Dispose();

        if (sessions.Count == 0)
            AddSession();
        else
            ShowSelectedSession();
    }

    private void ShowSelectedSession()
    {
        foreach ((FATabViewItem tab, SessionView view) in sessions)
        {
            bool selected = ReferenceEquals(Tabs.SelectedItem, tab);
            view.IsVisible = selected;
            if (selected)
                view.OnActivated();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        foreach (SessionView view in sessions.Values)
            view.Dispose();
        sessions.Clear();
        base.OnClosing(e);
    }
}
