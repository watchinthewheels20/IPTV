using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LoungeTV.Models;
using LoungeTV.Services;

namespace LoungeTV.Controls;

/// <summary>
/// A bounded, controller-first EPG grid. Only the visible channel rows are
/// constructed. The user offset shifts programme positions, never the clock.
/// </summary>
public partial class EpgGuideControl : UserControl
{
    private static readonly TimeZoneInfo UkZone = GetUkZone();
    private static readonly Brush Normal = Brush("#23518B");
    private static readonly Brush Selected = Brush("#835A15");
    private static readonly Brush Empty = Brush("#193B69");
    private static readonly Brush Gold = Brush("#FFCC3D");

    private List<LiveChannel> _channels = new();
    private EpgSnapshot _epg = EpgSnapshot.Empty;
    private int _selectedIndex;
    private DateTimeOffset _windowStart = RoundDown(DateTimeOffset.UtcNow);
    private DateTimeOffset _cursor = DateTimeOffset.UtcNow;
    private int _offsetMinutes;
    private double _previousWidth;

    public event Action<LiveChannel>? WatchRequested;
    public event Action? RefreshRequested;

    public EpgGuideControl()
    {
        InitializeComponent();
        _offsetMinutes = EpgOffsetStore.LoadMinutes();
        for (var i = 0; i < EpgOffsetBox.Items.Count; i++)
        {
            if (EpgOffsetBox.Items[i] is ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out var minutes) &&
                minutes == _offsetMinutes)
            {
                EpgOffsetBox.SelectedIndex = i;
                break;
            }
        }
    }

    public void SetChannels(IEnumerable<LiveChannel> channels)
    {
        var previous = SelectedChannel?.Id;
        _channels = channels.ToList();
        _selectedIndex = Math.Max(0, _channels.FindIndex(c => c.Id == previous));
        Render();
    }

    public void SetEpg(EpgSnapshot data)
    {
        _epg = data;
        EpgStatusText.Text = data.ProgrammeCount == 0 ? "Provider returned no listings" :
            $"{data.ProgrammeCount:N0} programmes loaded";
        Render();
    }

    public void SetStatus(string message) => EpgStatusText.Text = message;

    public void GoNow()
    {
        _windowStart = RoundDown(DateTimeOffset.UtcNow);
        _cursor = DateTimeOffset.UtcNow;
        Render();
    }

    public LiveChannel? SelectedChannel =>
        _selectedIndex >= 0 && _selectedIndex < _channels.Count ? _channels[_selectedIndex] : null;

    public void Navigate(GamepadCommand command)
    {
        if (_channels.Count == 0) return;
        switch (command)
        {
            case GamepadCommand.Up:
                _selectedIndex = Math.Max(0, _selectedIndex - 1);
                break;
            case GamepadCommand.Down:
                _selectedIndex = Math.Min(_channels.Count - 1, _selectedIndex + 1);
                break;
            case GamepadCommand.Left:
                _cursor = _cursor.AddMinutes(-30);
                if (_cursor < _windowStart) _windowStart = _windowStart.AddMinutes(-30);
                break;
            case GamepadCommand.Right:
                _cursor = _cursor.AddMinutes(30);
                if (_cursor >= _windowStart.AddHours(2))
                    _windowStart = _windowStart.AddMinutes(30);
                break;
            case GamepadCommand.Select:
                Watch();
                return;
            default:
                return;
        }
        Render();
    }

    private void Watch()
    {
        if (SelectedChannel is { } channel) WatchRequested?.Invoke(channel);
    }

    private void GuideLoaded(object sender, RoutedEventArgs e) => Render();
    private void GuideResized(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(TimeHeader.ActualWidth - _previousWidth) > 15)
            Render();
    }

    private void Earlier_Click(object sender, RoutedEventArgs e)
    {
        _windowStart = _windowStart.AddMinutes(-30);
        _cursor = _windowStart.AddMinutes(15);
        Render();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        _windowStart = _windowStart.AddMinutes(30);
        _cursor = _windowStart.AddMinutes(15);
        Render();
    }

    private void Now_Click(object sender, RoutedEventArgs e) => GoNow();

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void Watch_Click(object sender, RoutedEventArgs e) => Watch();

    private void EpgOffset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EpgOffsetBox.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out var selected)) return;
        _offsetMinutes = selected;
        EpgOffsetStore.SaveMinutes(selected);
        Render();
    }

    private void Programme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LiveChannel channel })
        {
            var index = _channels.FindIndex(c => c.Id == channel.Id);
            if (index >= 0) _selectedIndex = index;
            Render();
        }
    }

    private void Render()
    {
        if (!IsLoaded) return;
        var width = Math.Max(300, TimeHeader.ActualWidth);
        _previousWidth = TimeHeader.ActualWidth;
        TimeHeader.Children.Clear();
        RowsHost.Children.Clear();

        for (var i = 0; i < 4; i++)
        {
            var time = _windowStart.AddMinutes(i * 30);
            var label = new TextBlock
            {
                Text = ToUk(time).ToString("HH:mm", CultureInfo.GetCultureInfo("en-GB")),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Gold,
                Margin = new Thickness(9, 11, 0, 0)
            };
            Canvas.SetLeft(label, i * width / 4);
            TimeHeader.Children.Add(label);
        }

        if (_channels.Count == 0)
        {
            SelectedProgrammeText.Text = "No live channels loaded";
            SelectedProgrammeDescription.Text = "";
            return;
        }

        var count = Math.Clamp((int)((ActualHeight - 225) / 54), 4, 8);
        var first = Math.Clamp(_selectedIndex - count / 2, 0, Math.Max(0, _channels.Count - count));
        var last = Math.Min(_channels.Count, first + count);
        for (var index = first; index < last; index++)
            RowsHost.Children.Add(BuildRow(_channels[index], index == _selectedIndex, width));

        var programme = FindProgramme(SelectedChannel, _cursor);
        SelectedProgrammeText.Text = programme is null ? "No programme information" :
            $"{programme.Title}    {ToUk(programme.StartUtc.AddMinutes(_offsetMinutes)):HH:mm}–{ToUk(programme.EndUtc.AddMinutes(_offsetMinutes)):HH:mm}";
        SelectedProgrammeDescription.Text = programme?.Description ?? 
            "This channel has no matching EPG data at the selected time. Check provider data or try an EPG shift.";
    }

    private FrameworkElement BuildRow(LiveChannel channel, bool selected, double width)
    {
        var grid = new Grid { Height = 54, Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(234) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var channelButton = new Button
        {
            Tag = channel, Content = channel.DisplayName,
            Background = selected ? Selected : Normal,
            Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 5, 5, 5),
            Margin = new Thickness(0, 0, 3, 0),
            FontSize = 13,
            FontWeight = selected ? FontWeights.Bold : FontWeights.Normal
        };
        channelButton.Click += Programme_Click;
        grid.Children.Add(channelButton);

        var canvas = new Canvas
        {
            Width = width,
            Height = 54,
            Background = selected ? Brush("#173969") : Brush("#102E57"),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(canvas, 1);
        grid.Children.Add(canvas);

        var windowEnd = _windowStart.AddHours(2);
        var matches = _epg.ForChannel(channel);
        var shown = 0;
        foreach (var programme in matches)
        {
            var start = programme.StartUtc.AddMinutes(_offsetMinutes);
            var end = programme.EndUtc.AddMinutes(_offsetMinutes);
            if (end <= _windowStart || start >= windowEnd) continue;

            var left = Math.Max(0, (start - _windowStart).TotalMinutes / 120 * width);
            var right = Math.Min(width, (end - _windowStart).TotalMinutes / 120 * width);
            if (right - left < 5) continue;

            var cell = new Button
            {
                Tag = channel,
                Content = new TextBlock
                {
                    Text = programme.Title,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    Foreground = Brushes.White
                },
                Background = selected && start <= _cursor && _cursor < end ? Selected : Normal,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(7, 3, 2, 3),
                Height = 49,
                Width = right - left - 3
            };
            cell.Click += Programme_Click;
            Canvas.SetLeft(cell, left + 1);
            Canvas.SetTop(cell, 2);
            canvas.Children.Add(cell);
            shown++;
        }

        if (shown == 0)
        {
            var empty = new TextBlock
            {
                Text = "No programme information",
                Foreground = Brush("#A9BCD4"), FontSize = 13,
                Margin = new Thickness(12, 17, 0, 0)
            };
            canvas.Children.Add(empty);
        }

        var nowX = (DateTimeOffset.UtcNow - _windowStart).TotalMinutes / 120 * width;
        if (nowX >= 0 && nowX < width)
        {
            var line = new Rectangle { Width = 2, Height = 54, Fill = Gold, IsHitTestVisible = false };
            Canvas.SetLeft(line, nowX);
            canvas.Children.Add(line);
        }
        return grid;
    }

    private EpgProgramme? FindProgramme(LiveChannel? channel, DateTimeOffset time)
    {
        if (channel is null) return null;
        var sourceTime = time.AddMinutes(-_offsetMinutes);
        return _epg.ForChannel(channel).FirstOrDefault(p =>
            p.StartUtc <= sourceTime && sourceTime < p.EndUtc);
    }

    private static DateTimeOffset RoundDown(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour,
            utc.Minute / 30 * 30, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset ToUk(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, UkZone);

    private static TimeZoneInfo GetUkZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        }
    }

    private static Brush Brush(string hex) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
