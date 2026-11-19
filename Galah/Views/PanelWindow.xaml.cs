using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Galah.Services;
using Galah.ViewModels;

namespace Galah.Views;

/// <summary>
/// Frameless panel: custom chrome, tab strip, icon grid, drop target. It implements
/// <see cref="IPanelSurface"/> and never changes visibility by itself — the controller is the single
/// writer of window state (design §4).
/// </summary>
public sealed partial class PanelWindow : Window, IPanelSurface
{
    /// <summary>Data format used for dragging a tile inside the panel (never a file payload).</summary>
    private const string ItemDragFormat = "Galah.ItemId";

    private ItemTileViewModel? _dragCandidate;
    private Point _dragOrigin;
    private bool _dragInProgress;
    private int _dropIndex = -1;

    private readonly AppEnvironment _env;
    private readonly PanelViewModel _vm;
    private bool _allowClose;

    /// <summary>Create the panel.</summary>
    /// <param name="environment">Wired environment.</param>
    /// <param name="dialogHost">Dialog service used by the view model.</param>
    public PanelWindow(AppEnvironment environment, IDialogHost dialogHost)
    {
        InitializeComponent();
        _env = environment;
        _vm = new PanelViewModel(_env) { DialogHost = dialogHost };
        DataContext = _vm;

        // When a dialog closes the panel takes the foreground back; blur rules are guarded meanwhile.
        _env.Dialogs.AllClosed += () =>
        {
            if (_env.Panel.IsVisible) Activate();
        };

        LocationChanged += (_, _) => _env.Panel.NotifyGeometryChanged();
        SizeChanged += (_, _) => _env.Panel.NotifyGeometryChanged();
        StateChanged += (_, _) => _env.Panel.NotifyMaximizedChanged(WindowState == WindowState.Maximized);
    }

    /// <summary>Panel view model (used by the settings host to refresh layout after preference changes).</summary>
    public PanelViewModel ViewModel => _vm;

    /// <inheritdoc />
    public IntPtr Handle { get; private set; }

    /// <inheritdoc />
    public void ShowSurface()
    {
        if (!IsVisible) Show();
        Handle = new WindowInteropHelper(this).Handle;
        EnsureSizeMoveHook();
    }

    private bool _sizeMoveHooked;

    /// <summary>
    /// Commit geometry the moment a resize gesture ends (WM_EXITSIZEMOVE). The debounced writer
    /// stays as the fallback for programmatic changes, but "drag and immediately kill the process"
    /// must never lose the size the user just chose.
    /// </summary>
    private void EnsureSizeMoveHook()
    {
        if (_sizeMoveHooked || Handle == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(Handle);
        if (source is null) return;
        source.AddHook(SizeMoveHook);
        _sizeMoveHooked = true;
    }

    private IntPtr SizeMoveHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmExitSizeMove = 0x0114;
        if (msg == WmExitSizeMove)
        {
            _env.Panel.CommitGeometryNow();
        }
        handled = false;
        return IntPtr.Zero;
    }

    /// <inheritdoc />
    public void HideSurface()
    {
        if (IsVisible) Hide();
    }

    /// <inheritdoc />
    public void SetGeometry(double x, double y, double width, double height)
    {
        Left = x;
        Top = y;
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
    }

    /// <inheritdoc />
    public (double X, double Y, double Width, double Height) GetGeometry() => (Left, Top, ActualWidth, ActualHeight);

    /// <inheritdoc />
    public bool IsMaximized => WindowState == WindowState.Maximized;

    /// <inheritdoc />
    public void ActivateSurface()
    {
        if (!IsVisible) return;
        Activate();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            Galah.Services.Interop.Win32.SetForegroundWindow(handle);
            Galah.Services.Interop.Win32.ShowWindow(handle, 5 /*SW_SHOW*/);
        }
    }

    /// <inheritdoc />
    public void SetMaximized(bool maximized)
    {
        var target = maximized ? WindowState.Maximized : WindowState.Normal;
        if (WindowState != target) WindowState = target;
    }

    /// <inheritdoc />
    public void ReleaseForShutdown() => _allowClose = true;

    // ---- chrome -------------------------------------------------------------------

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2)
        {
            _env.Panel.Hide();
            return;
        }
        try { DragMove(); } catch (InvalidOperationException) { /* button already released */ }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => _env.Panel.Hide();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _env.Panel.Hide();
            e.Handled = true;
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (!ctrl) return;

        // Ctrl+Tab walks categories (Sprint 3 / E4)
        if (e.Key == Key.Tab)
        {
            _vm.SwitchTabRelative((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (!_env.Settings.Current.NumberKeysSwitchTab) return;

        // D0..D9 and NumPad0..9 both reach the same digit; the mapping rule lives in
        // TabNavigation so it is unit-tested (digit 0 addresses nothing on purpose).
        var digit = e.Key switch
        {
            >= Key.D0 and <= Key.D9 => (int)(e.Key - Key.D0),
            >= Key.NumPad0 and <= Key.NumPad9 => (int)(e.Key - Key.NumPad0),
            _ => -1,
        };
        if (digit < 0) return;
        var index = Galah.Services.TabNavigation.DigitToIndex(digit);
        if (index < 0) return;
        if (_vm.SelectTabByIndex(index)) e.Handled = true;
    }

    /// <summary>
    /// Wheel at the scroll edge switches category (Sprint 3 / E4). Scrolling keeps priority: the
    /// gesture only becomes a tab change when the grid has nowhere left to go, so long libraries
    /// still behave normally.
    /// </summary>
    private void TileScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_env.Settings.Current.WheelSwitchTab) return;
        var scroll = (System.Windows.Controls.ScrollViewer)sender;
        var direction = e.Delta < 0 ? 1 : -1;
        var atTop = scroll.VerticalOffset <= 0;
        var atBottom = scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 0.5;
        var edge = direction > 0 ? atBottom : atTop;
        if (!edge) return;
        _vm.SwitchTabRelative(direction);
        e.Handled = true;
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_env.Settings.Current.HideOnBlur) return;
        if (_env.Dialogs.IsBlocking) return;                       // spec §4.2: dialogs outrank blur
        if (HasOwnWindowActive()) return;
        _env.Panel.Hide();
    }

    /// <summary>True when focus moved to another Galah window rather than to a different process.</summary>
    private bool HasOwnWindowActive()
    {
        if (Application.Current is null) return false;
        foreach (var window in Application.Current.Windows.OfType<Window>())
        {
            if (ReferenceEquals(window, this)) continue;
            if (window.IsVisible && window.IsActive) return true;
        }
        return false;
    }

    // ---- tiles --------------------------------------------------------------------

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ItemTileViewModel tile }) return;
        _vm.Launch(tile, e.ClickCount);
        e.Handled = true;
    }

    private static ItemTileViewModel? FindTile(object sender) =>
        sender is MenuItem { DataContext: ItemTileViewModel tile } ? tile : null;

    private void TileOpen_Click(object sender, RoutedEventArgs e)
    {
        if (FindTile(sender) is { } tile) _vm.Launch(tile, 2);
    }

    private void TileEdit_Click(object sender, RoutedEventArgs e)
    {
        if (FindTile(sender) is { } tile) _vm.EditItemCommand.Execute(tile);
    }

    private void TileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (FindTile(sender) is { } tile) _vm.DeleteItemCommand.Execute(tile);
    }

    private void TileMoveLeft_Click(object sender, RoutedEventArgs e)
    {
        if (FindTile(sender) is { } tile) _vm.MoveItemLeftCommand.Execute(tile);
    }

    private void TileMoveRight_Click(object sender, RoutedEventArgs e)
    {
        if (FindTile(sender) is { } tile) _vm.MoveItemRightCommand.Execute(tile);
    }

    /// <summary>Fill the "移到其他分类" submenu on demand so the tab list is always current.</summary>
    private void MoveToTabMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu) return;
        menu.Items.Clear();
        if (menu.DataContext is not ItemTileViewModel tile) return;

        foreach (var tab in _vm.Tabs)
        {
            var item = new MenuItem
            {
                Header = tab.Name,
                Tag = new string[] { tile.Id, tab.Id },
                IsEnabled = tab.Id != _vm.SelectedTab?.Id,
            };
            item.Click += MoveToTabItem_Click;
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "（没有其他分类）", IsEnabled = false });
    }

    private void MoveToTabItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string[] pair } && pair.Length == 2)
            _vm.MoveItemToTab(pair[0], pair[1]);
    }

    // ---- tabs ---------------------------------------------------------------------

    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab }) _vm.SelectTabCommand.Execute(tab);
    }

    private static TabViewModel? FindTab(object sender) =>
        sender is MenuItem { DataContext: TabViewModel tab } ? tab : null;

    private void TabRename_Click(object sender, RoutedEventArgs e)
    {
        if (FindTab(sender) is { } tab) _vm.RenameTabCommand.Execute(tab);
    }

    private void TabDelete_Click(object sender, RoutedEventArgs e)
    {
        if (FindTab(sender) is { } tab) _vm.DeleteTabCommand.Execute(tab);
    }

    private void TabMoveLeft_Click(object sender, RoutedEventArgs e)
    {
        if (FindTab(sender) is { } tab) _vm.MoveTabLeftCommand.Execute(tab);
    }

    private void TabMoveRight_Click(object sender, RoutedEventArgs e)
    {
        if (FindTab(sender) is { } tab) _vm.MoveTabRightCommand.Execute(tab);
    }

    // ---- add menu -----------------------------------------------------------------

    private void AddApp_Click(object sender, RoutedEventArgs e) => _vm.AddAppCommand.Execute(null);

    private void Maintenance_Click(object sender, RoutedEventArgs e) =>
        _env.ShowMaintenance();

    private void AddPicker_Click(object sender, RoutedEventArgs e) =>
        _vm.AddPickerCommand.Execute(null);

    private void AddFileFolder_Click(object sender, RoutedEventArgs e) => _vm.AddFileCommand.Execute(null);

    private void AddUrl_Click(object sender, RoutedEventArgs e) => _vm.AddUrlCommand.Execute(null);

    private void NewTab_Click(object sender, RoutedEventArgs e) => _vm.NewTabCommand.Execute(null);

    // ---- drag & drop --------------------------------------------------------------

    private void Root_DragEnter(object sender, DragEventArgs e) => UpdateDropEffect(e);

    private void Root_DragOver(object sender, DragEventArgs e) => UpdateDropEffect(e);

    private static void UpdateDropEffect(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(ItemDragFormat))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        _vm.HandleDrop(e.Data);
        e.Handled = true;
    }

    // ---- tile drag & drop (reorder inside a category, move across categories) -----------

    private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ItemTileViewModel tile }) return;
        _dragCandidate = tile;
        _dragOrigin = e.GetPosition(this);
        _dragInProgress = false;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || _dragInProgress) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(this);
        var dx = current.X - _dragOrigin.X;
        var dy = current.Y - _dragOrigin.Y;
        if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance) return;

        var tile = _dragCandidate;
        _dragInProgress = true;
        _dragCandidate = null;

        var data = new DataObject();
        data.SetData(ItemDragFormat, tile.Id);
        DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
        HideDropIndicator();
        _dragInProgress = false;
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragInProgress) _dragCandidate = null;
    }

    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ItemDragFormat))
        {
            // A file coming from the explorer keeps the import behaviour of the window handler.
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ShowDropIndicator(e);
    }

    private void Grid_DragLeave(object sender, DragEventArgs e) => HideDropIndicator();

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        var id = e.Data.GetData(ItemDragFormat) as string;
        HideDropIndicator();
        if (id is null) return;
        e.Handled = true;

        var index = _dropIndex;
        _dropIndex = -1;
        if (index < 0) return;
        _env.Catalog.MoveItemToIndex(id, index);
    }

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ItemDragFormat)) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        var id = e.Data.GetData(ItemDragFormat) as string;
        if (id is null || sender is not FrameworkElement { DataContext: TabViewModel tab }) return;
        e.Handled = true;
        _env.Catalog.MoveItemToTab(id, tab.Id);
    }

    /// <summary>
    /// Work out where the dragged tile would land and show the insertion line. The grid is a
    /// wrapping panel, so the rows are derived from the realised containers.
    /// </summary>
    private void ShowDropIndicator(DragEventArgs e)
    {
        var containers = TileContainers();
        if (containers.Count == 0)
        {
            _dropIndex = 0;
            HideDropIndicator();
            return;
        }

        var point = e.GetPosition(GridRoot);
        var rects = new List<Rect>(containers.Count);
        foreach (var container in containers)
        {
            var origin = container.TranslatePoint(new Point(0, 0), GridRoot);
            rects.Add(new Rect(origin.X, origin.Y, container.ActualWidth, container.ActualHeight));
        }

        var index = InsertionIndex(point, rects);
        _dropIndex = index;

        Rect anchor;
        if (index < rects.Count) anchor = rects[index];
        else anchor = rects[rects.Count - 1];

        var x = index < rects.Count ? anchor.Left - 3 : anchor.Right + 1;
        DropIndicator.Visibility = Visibility.Visible;
        DropIndicator.Margin = new Thickness(x, anchor.Top, 0, 0);
        DropIndicator.Height = anchor.Height;
    }

    private void HideDropIndicator()
    {
        DropIndicator.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Insertion index for a pointer position inside a wrapping layout: inside a tile the pointer
    /// decides before/after its centre, outside every tile the nearest row decides.
    /// </summary>
    private static int InsertionIndex(Point point, IReadOnlyList<Rect> rects)
    {
        for (var i = 0; i < rects.Count; i++)
        {
            if (!rects[i].Contains(point)) continue;
            return point.X < rects[i].Left + rects[i].Width / 2 ? i : i + 1;
        }

        var bestRow = -1;
        var bestDistance = double.MaxValue;
        var rowTop = double.NaN;
        var rowBottom = double.NaN;
        var rowIndices = new List<int>();

        for (var i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];
            if (rowIndices.Count > 0 && Math.Abs(rect.Top - rowTop) > 1)
            {
                var distance = RowDistance(point.Y, rowTop, rowBottom);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestRow = rowIndices[0];
                }
                rowIndices.Clear();
                rowTop = rect.Top;
                rowBottom = rect.Bottom;
            }
            else if (rowIndices.Count == 0)
            {
                rowTop = rect.Top;
                rowBottom = rect.Bottom;
            }
            rowBottom = Math.Max(rowBottom, rect.Bottom);
            rowIndices.Add(i);
        }

        if (rowIndices.Count > 0)
        {
            var distance = RowDistance(point.Y, rowTop, rowBottom);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestRow = rowIndices[0];
            }
        }

        if (bestRow < 0) return rects.Count;

        // Walk the winning row and find the first tile that starts after the pointer.
        var row = new List<int>();
        var top = rects[bestRow].Top;
        for (var i = 0; i < rects.Count; i++)
            if (Math.Abs(rects[i].Top - top) <= 1) row.Add(i);

        foreach (var i in row)
        {
            if (rects[i].Left >= point.X) return i;
        }
        return row[0] + row.Count;
    }

    private static double RowDistance(double y, double top, double bottom)
    {
        if (y < top) return top - y;
        if (y > bottom) return y - bottom;
        return 0;
    }

    /// <summary>Realised tile containers in display order.</summary>
    private List<FrameworkElement> TileContainers()
    {
        var list = new List<FrameworkElement>();
        var generator = TileItems.ItemContainerGenerator;
        foreach (var tile in _vm.Items)
        {
            var container = generator.ContainerFromItem(tile) as FrameworkElement;
            if (container is not null) list.Add(container);
        }
        return list;
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        // ✕ and Alt+F4 both mean "hide"; quitting goes through the chrome menu (design §2.1 P-6).
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        _env.Panel.Hide();
        base.OnClosing(e);
    }
}
