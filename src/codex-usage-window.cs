using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
        bool first;
        using (var instance = new Mutex(true, @"Local\CodexUsageBarDesktop", out first))
        {
            if (!first) return;
            new Application().Run(new UsageWindow());
            instance.ReleaseMutex();
        }
    }

}

internal sealed class UsageWindow : Window
{
    private readonly UsageLogs logs = new UsageLogs();
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly Grid surface;
    private readonly Border fill;
    private readonly TextBlock total, hit, context, model, windowSize, sessionName;
    private readonly TextBlock[] counts;
    private readonly FrameworkElement details, picker;
    private readonly Button toggle, selectSession, useSession;
    private readonly TextBox sessionQuery;
    private readonly TextBlock pickerHint;
    private readonly ListBox sessionResults;
    private readonly ShapePath chevron;
    private List<Session> recent = new List<Session>();
    private List<Session> searchable = new List<Session>();
    private DateTime nextDiscovery;
    private Session pinned;
    private string snapshotKey;

    public UsageWindow()
    {
        Title = "Codex 用量";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 574;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        FontSize = 13;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        using (Stream layout = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsageBar.xaml"))
            surface = (Grid)XamlReader.Load(layout);
        Content = surface;
        total = Element<TextBlock>("Total");
        hit = Element<TextBlock>("Hit");
        context = Element<TextBlock>("Context");
        fill = Element<Border>("Fill");
        model = Element<TextBlock>("Model");
        windowSize = Element<TextBlock>("WindowSize");
        sessionName = Element<TextBlock>("SessionName");
        counts = new[] { Element<TextBlock>("Input"), Element<TextBlock>("Uncached"), Element<TextBlock>("Read"),
                         Element<TextBlock>("Write"), Element<TextBlock>("Output") };
        details = Element<FrameworkElement>("Details");
        picker = Element<FrameworkElement>("Picker");
        sessionQuery = Element<TextBox>("SessionQuery");
        pickerHint = Element<TextBlock>("PickerHint");
        sessionResults = Element<ListBox>("SessionResults");
        useSession = Element<Button>("UseSession");
        chevron = Element<ShapePath>("Chevron");
        toggle = Element<Button>("Toggle");
        selectSession = Element<Button>("SelectSession");

        hit.ToolTip = "会话累计缓存读取 ÷ 累计输入";
        context.ToolTip = "最近一次记录的上下文用量 ÷ 会话有效窗口";
        total.ToolTip = "会话累计输入和输出 token，含缓存读取";
        Element<Grid>("StatusBar").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        };
        toggle.Click += delegate { ToggleDetails(); };
        selectSession.Click += delegate { TogglePicker(); };
        Element<Button>("PasteLink").Click += delegate { PasteLink(); };
        Element<Button>("FollowRecent").Click += delegate { SetSession(null); };
        useSession.Click += delegate { UseSelectedSession(); };
        sessionResults.MouseDoubleClick += delegate { UseSelectedSession(); };
        sessionResults.SelectionChanged += delegate { useSession.IsEnabled = sessionResults.SelectedItem != null; };
        sessionQuery.TextChanged += delegate { FilterSessions(); };
        sessionQuery.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            UseSelectedSession();
        };
        Element<Button>("Dismiss").Click += delegate { Close(); };
        timer.Tick += delegate { RefreshUsage(); };
        IsVisibleChanged += delegate
        {
            if (IsVisible)
            {
                RefreshUsage();
                timer.Start();
            }
            else timer.Stop();
        };
        Loaded += delegate
        {
            Rect work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - ActualWidth) / 2;
            Top = work.Bottom - ActualHeight - 15;
        };
    }

    private T Element<T>(string name) where T : class
    {
        return (T)surface.FindName(name);
    }

    private void ToggleDetails()
    {
        double bottom = Top + ActualHeight;
        bool show = details.Visibility != Visibility.Visible;
        details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) picker.Visibility = Visibility.Collapsed;
        chevron.LayoutTransform = new RotateTransform(show ? 180 : 0);
        AutomationProperties.SetName(toggle, show ? "收起明细" : "展开明细");
        toggle.ToolTip = show ? "收起" : "明细";
        selectSession.IsEnabled = recent.Count > 0;
        PreserveBottom(bottom);
    }

    private void PreserveBottom(double bottom)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate { Top = bottom - ActualHeight; }));
    }

    private void TogglePicker()
    {
        double bottom = Top + ActualHeight;
        if (picker.Visibility == Visibility.Visible) picker.Visibility = Visibility.Collapsed;
        else
        {
            searchable = logs.Sessions(true);
            sessionQuery.Text = "";
            picker.Visibility = Visibility.Visible;
            FilterSessions();
            sessionQuery.Focus();
        }
        PreserveBottom(bottom);
    }

    private Session MatchThread(string id)
    {
        return searchable.FirstOrDefault(item => String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private void FilterSessions()
    {
        string query = sessionQuery.Text.Trim();
        string id = Session.ParseId(query);
        List<Session> matches;
        if (id != null)
        {
            var exact = MatchThread(id);
            matches = exact == null ? new List<Session>() : new List<Session> { exact };
        }
        else
        {
            matches = searchable.Where(item => Contains(item.Name, query) || Contains(item.Details, query)
                || Contains(item.Id, query)).Take(100).ToList();
        }
        sessionResults.ItemsSource = matches;
        sessionResults.SelectedIndex = matches.Count == 1 ? 0 : -1;
        useSession.IsEnabled = sessionResults.SelectedItem != null;
        pickerHint.Text = matches.Count == 0 ? "没有匹配的本地会话"
            : query.Length == 0 ? "搜索标题、项目，或粘贴 Codex 会话链接"
            : id != null ? "已匹配这条会话，按 Enter 使用" : "选择一条会话";
    }

    private static bool Contains(string value, string query)
    {
        return value != null && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
    }

    private void PasteLink()
    {
        string copied;
        try { copied = Clipboard.GetText().Trim(); }
        catch (ExternalException) { pickerHint.Text = "剪贴板暂时不可读，请稍后再试"; return; }
        string id = Session.ParseId(copied);
        if (id == null)
        {
            pickerHint.Text = "先在 Codex 对话里按 Ctrl + Alt + L 复制会话链接";
            return;
        }
        searchable = logs.Sessions(true);
        sessionQuery.Text = copied;
        Session match = MatchThread(id);
        if (match != null) SetSession(match);
        else
        {
            FilterSessions();
            pickerHint.Text = "找不到这条会话的本机日志";
        }
    }

    private void UseSelectedSession()
    {
        var selected = sessionResults.SelectedItem as Session;
        if (selected != null) SetSession(selected);
    }

    private void SetSession(Session session)
    {
        pinned = session;
        snapshotKey = null;
        double bottom = Top + ActualHeight;
        picker.Visibility = Visibility.Collapsed;
        RefreshUsage();
        PreserveBottom(bottom);
    }

    private void RefreshUsage()
    {
        if (DateTime.UtcNow >= nextDiscovery)
        {
            recent = logs.Sessions(false);
            if (pinned != null && !File.Exists(pinned.Path)) pinned = null;
            nextDiscovery = DateTime.UtcNow.AddSeconds(6);
        }
        var active = pinned ?? (recent.Count > 0 ? recent[0] : null);
        sessionName.Text = active == null ? "等待本地会话日志" : (pinned == null ? "最近活动 · " : "已锁定 · ") + active.Name;
        if (active == null)
        {
            snapshotKey = null;
            Render(null);
            return;
        }

        try
        {
            var file = new FileInfo(active.Path);
            string key = active.Path + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            if (key == snapshotKey) return;
            Render(logs.ReadLatest(active.Path));
            snapshotKey = key;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Render(Snapshot usage)
    {
        var data = usage ?? new Snapshot();
        double? used = data.Window > 0 && data.Context.HasValue ? (double?)data.Context.Value / data.Window.Value : null;
        double? cache = data.Input > 0 && data.Read.HasValue ? (double?)data.Read.Value / data.Input.Value : null;
        total.Text = data.Total.HasValue ? Compact(data.Total) + " tok" : "—";
        hit.Text = Percent(cache);
        context.Text = Percent(used);
        fill.Width = used.HasValue ? 70 * Math.Max(0, Math.Min(1, used.Value)) : 0;
        model.Text = data.Model ?? "—";
        windowSize.Text = Compact(data.Context) + " / " + Compact(data.Window);
        windowSize.ToolTip = Exact(data.Context) + " / " + Exact(data.Window) + " token";
        var values = new[] { data.Input, data.Uncached, data.Read, data.Write, data.Output };
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i].Text = Compact(values[i]);
            counts[i].ToolTip = values[i].HasValue ? Exact(values[i]) + " token" : "日志未提供";
        }
        if (data.Write == 0)
            counts[3].ToolTip = "Codex 日志报告 0 个写入 token，服务端实际写入量无法从这项统计判断。";
    }

    private static string Percent(double? fraction)
    {
        return fraction.HasValue ? (fraction.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
    }

    private static string Exact(long? amount)
    {
        return amount.HasValue ? amount.Value.ToString("N0") : "—";
    }

    private static string Compact(long? amount)
    {
        if (!amount.HasValue) return "—";
        var n = amount.Value;
        double scale = n >= 1000000000 ? 1e9 : n >= 1000000 ? 1e6 : n >= 1000 ? 1e3 : 1;
        string suffix = scale == 1e9 ? "B" : scale == 1e6 ? "M" : scale == 1e3 ? "k" : "";
        return (n / scale).ToString("0.##", CultureInfo.InvariantCulture) + suffix;
    }
}
