using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LoungeTV.Controls;
using LoungeTV.Models;
using LoungeTV.Services;

namespace LoungeTV;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChannelCategory> _categories = new();
    private readonly ObservableCollection<LiveChannel> _visibleChannels = new();
    private readonly GamepadInput _gamepad = new();
    private readonly List<LiveChannel> _allChannels = new();
    private XtreamApiClient? _api;
    private IptvAccount? _account;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private LiveChannel? _playingChannel;
    private bool _connecting;
    private bool _fullScreen;
    private bool _focusChannels = true;
    private WindowState _previousWindowState = WindowState.Normal;
    private Thickness _normalBrowseMargin;

    public MainWindow()
    {
        InitializeComponent();
        CategoryList.ItemsSource = _categories;
        ChannelList.ItemsSource = _visibleChannels;
        _normalBrowseMargin = BrowsePanel.Margin;
        _gamepad.Command += HandleGamepad;
        _gamepad.Start();

        var (server, user, password) = SettingsStore.Load();
        ServerBox.Text = server;
        UsernameBox.Text = user;
        PasswordInput.Password = password;
        RememberCheck.IsChecked = !string.IsNullOrEmpty(password);
        if (!string.IsNullOrEmpty(server))
            LoginMessage.Text = "Saved settings loaded. Select Connect to continue.";
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) => await ConnectAsync();
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async Task ConnectAsync()
    {
        if (_connecting) return;
        var details = new IptvConnection(ServerBox.Text.Trim(), UsernameBox.Text.Trim(), PasswordInput.Password);
        if (string.IsNullOrWhiteSpace(details.ServerUrl) ||
            string.IsNullOrWhiteSpace(details.Username) || string.IsNullOrWhiteSpace(details.Password))
        {
            LoginMessage.Text = "Server URL, username and password are required.";
            return;
        }

        _connecting = true;
        ConnectButton.IsEnabled = RefreshButton.IsEnabled = false;
        LoginMessage.Text = "Connecting and loading live channels…";
        GeneralStatusText.Text = "Contacting IPTV service…";
        XtreamApiClient? newClient = null;
        try
        {
            newClient = new XtreamApiClient(details);
            var account = await newClient.AuthenticateAsync();
            var categories = await newClient.GetCategoriesAsync();
            var channels = await newClient.GetChannelsAsync();
            if (channels.Count == 0)
                throw new InvalidDataException("Login succeeded but this account has no accessible live channels.");

            // Only swap the active connection after the new one has loaded completely.
            var previousClient = _api;
            _api = newClient;
            newClient = null;
            _account = account;
            previousClient?.Dispose();

            _allChannels.Clear();
            _allChannels.AddRange(channels);
            _categories.Clear();
            _categories.Add(new ChannelCategory("*", "All channels"));
            foreach (var category in categories) _categories.Add(category);
            CategoryList.SelectedIndex = 0;
            UpdateVisibleChannels();

            var persisted = "";
            try
            {
                if (RememberCheck.IsChecked == true)
                    SettingsStore.Save(details.ServerUrl, details.Username, details.Password);
                else SettingsStore.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                persisted = " Settings couldn't be saved.";
            }

            LoginPanel.Visibility = Visibility.Collapsed;
            BrowsePanel.Visibility = Visibility.Visible;
            AccountButton.Visibility = RefreshButton.Visibility = FullScreenButton.Visibility = Visibility.Visible;
            GeneralStatusText.Text = $"Connected · {channels.Count:N0} channels · {categories.Count:N0} categories.{persisted}";
            PlayerStatusText.Text = "Select a channel and press A, Enter or Play selected.";
            ChannelList.Focus();
            _focusChannels = true;
        }
        catch (ArgumentException ex)
        {
            ReportConnectionError(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportConnectionError(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            ReportConnectionError($"Unable to contact IPTV provider (HTTP/network error{(ex.StatusCode is null ? "" : $" {(int)ex.StatusCode}")}). Check the server URL and internet connection.");
        }
        catch (TaskCanceledException)
        {
            ReportConnectionError("Connection timed out. Check the IPTV server and try again.");
        }
        catch (JsonException)
        {
            ReportConnectionError("The server response was not valid JSON. Check that this is an Xtream-compatible provider URL.");
        }
        catch (InvalidDataException ex)
        {
            ReportConnectionError(ex.Message);
        }
        catch (Exception)
        {
            ReportConnectionError("Unexpected connection error. Verify the provider address and credentials.");
        }
        finally
        {
            newClient?.Dispose();
            _connecting = false;
            ConnectButton.IsEnabled = RefreshButton.IsEnabled = true;
        }
    }

    private void ReportConnectionError(string message)
    {
        LoginMessage.Text = message;
        GeneralStatusText.Text = message;
        if (BrowsePanel.Visibility == Visibility.Visible)
            MessageBox.Show(this, message, "Connection failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void UpdateVisibleChannels()
    {
        var previousId = (ChannelList.SelectedItem as LiveChannel)?.Id;
        var categoryId = (CategoryList.SelectedItem as ChannelCategory)?.Id ?? "*";
        var search = SearchBox.Text.Trim();
        _visibleChannels.Clear();
        foreach (var channel in _allChannels)
        {
            if (categoryId != "*" && channel.CategoryId != categoryId) continue;
            if (search.Length > 0 && !channel.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            _visibleChannels.Add(channel);
        }
        ChannelList.SelectedItem = _visibleChannels.FirstOrDefault(c => c.Id == previousId) ?? _visibleChannels.FirstOrDefault();
        if (_allChannels.Count > 0)
            GeneralStatusText.Text = $"{_visibleChannels.Count:N0} channels shown / {_allChannels.Count:N0} total";
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_allChannels.Count > 0) UpdateVisibleChannels();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_allChannels.Count > 0) UpdateVisibleChannels();
    }

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelList.SelectedItem is LiveChannel channel && _playingChannel is null)
            NowPlayingText.Text = channel.Name;
    }

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelectedChannel();
    private void PlayButton_Click(object sender, RoutedEventArgs e) => PlaySelectedChannel();
    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void EnsurePlayer()
    {
        if (_mediaPlayer is not null) return;
        Core.Initialize();
        _libVlc = new LibVLC("--network-caching=900", "--live-caching=900", "--no-video-title-show");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Opening += (_, _) => DispatchPlaybackStatus("Opening stream…");
        _mediaPlayer.Buffering += (_, _) => DispatchPlaybackStatus("Buffering stream…");
        _mediaPlayer.Playing += (_, _) => DispatchPlaybackStatus("Playing live TV");
        _mediaPlayer.EncounteredError += (_, _) => DispatchPlaybackStatus("Playback error. Try another stream format or channel.");
        _mediaPlayer.EndReached += (_, _) => DispatchPlaybackStatus("Stream ended.");
        PlayerView.MediaPlayer = _mediaPlayer;
    }

    private void DispatchPlaybackStatus(string message)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => PlayerStatusText.Text = message));
    }

    private void PlaySelectedChannel()
    {
        if (_api is null || _account is null || ChannelList.SelectedItem is not LiveChannel channel)
            return;

        try
        {
            EnsurePlayer();
            var format = StreamFormatBox.SelectedIndex switch
            {
                1 => "ts",
                2 => "m3u8",
                _ => _account.AllowedFormats.Contains("ts") || _account.AllowedFormats.Count == 0 ? "ts" :
                    _account.AllowedFormats.Contains("m3u8") ? "m3u8" : "ts"
            };
            var streamUri = _api.GetStreamUri(channel.Id, format);
            _mediaPlayer!.Stop();
            _currentMedia?.Dispose();
            _currentMedia = new Media(_libVlc!, streamUri);
            _playingChannel = channel;
            NowPlayingText.Text = channel.Name;
            PlayerStatusText.Text = $"Opening {format.ToUpperInvariant()} stream…";
            if (!_mediaPlayer.Play(_currentMedia))
                PlayerStatusText.Text = "Could not start playback. Try another stream format.";
        }
        catch (Exception)
        {
            PlayerStatusText.Text = "Unable to start video. Check the native LibVLC libraries and stream format.";
        }
    }

    private void StopPlayback()
    {
        _mediaPlayer?.Stop();
        _currentMedia?.Dispose();
        _currentMedia = null;
        _playingChannel = null;
        PlayerStatusText.Text = "Playback stopped";
        NowPlayingText.Text = (ChannelList.SelectedItem as LiveChannel)?.Name ?? "Select a channel to begin";
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fullScreen) ToggleFullscreen();
        StopPlayback();
        BrowsePanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        AccountButton.Visibility = RefreshButton.Visibility = FullScreenButton.Visibility = Visibility.Collapsed;
        LoginMessage.Text = "Update your connection or reconnect.";
        ServerBox.Focus();
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (BrowsePanel.Visibility != Visibility.Visible) return;
        if (!_fullScreen)
        {
            _previousWindowState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            RootGrid.RowDefinitions[0].Height = new GridLength(0);
            RootGrid.RowDefinitions[2].Height = new GridLength(0);
            TopBar.Visibility = StatusBar.Visibility = Visibility.Collapsed;
            CategoriesColumn.Width = ChannelsColumn.Width = new GridLength(0);
            CategoryPanel.Visibility = ChannelPanel.Visibility = Visibility.Collapsed;
            BrowsePanel.Margin = new Thickness(0);
            _fullScreen = true;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _previousWindowState;
            RootGrid.RowDefinitions[0].Height = new GridLength(70);
            RootGrid.RowDefinitions[2].Height = new GridLength(35);
            TopBar.Visibility = StatusBar.Visibility = Visibility.Visible;
            CategoriesColumn.Width = new GridLength(215);
            ChannelsColumn.Width = new GridLength(325);
            CategoryPanel.Visibility = ChannelPanel.Visibility = Visibility.Visible;
            BrowsePanel.Margin = _normalBrowseMargin;
            _fullScreen = false;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullScreen) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Enter && BrowsePanel.Visibility == Visibility.Visible &&
                 (ChannelList.IsKeyboardFocusWithin || CategoryList.IsKeyboardFocusWithin))
        {
            PlaySelectedChannel(); e.Handled = true;
        }
    }

    private void HandleGamepad(GamepadCommand command)
    {
        if (!IsActive || BrowsePanel.Visibility != Visibility.Visible) return;
        if (command == GamepadCommand.Fullscreen) { ToggleFullscreen(); return; }
        if (command == GamepadCommand.Back)
        {
            if (_fullScreen) ToggleFullscreen();
            else StopPlayback();
            return;
        }
        if (_fullScreen)
        {
            if (command == GamepadCommand.Up) ChangeChannel(-1);
            if (command == GamepadCommand.Down) ChangeChannel(1);
            return;
        }
        switch (command)
        {
            case GamepadCommand.Left:
                _focusChannels = false;
                CategoryList.Focus();
                break;
            case GamepadCommand.Right:
                _focusChannels = true;
                ChannelList.Focus();
                break;
            case GamepadCommand.Up:
                MoveSelection(-1);
                break;
            case GamepadCommand.Down:
                MoveSelection(1);
                break;
            case GamepadCommand.Select:
                if (_focusChannels) PlaySelectedChannel();
                else { _focusChannels = true; ChannelList.Focus(); }
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var list = _focusChannels ? ChannelList : CategoryList;
        if (list.Items.Count == 0) return;
        var index = list.SelectedIndex < 0 ? 0 : list.SelectedIndex;
        list.SelectedIndex = Math.Clamp(index + delta, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.SelectedItem);
    }

    private void ChangeChannel(int delta)
    {
        if (_visibleChannels.Count == 0) return;
        var selected = _playingChannel is null ? ChannelList.SelectedIndex :
            _visibleChannels.ToList().FindIndex(c => c.Id == _playingChannel.Id);
        var next = (Math.Max(selected, 0) + delta + _visibleChannels.Count) % _visibleChannels.Count;
        ChannelList.SelectedIndex = next;
        PlaySelectedChannel();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _gamepad.Dispose();
        PlayerView.MediaPlayer = null;
        _mediaPlayer?.Stop();
        _currentMedia?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
        _api?.Dispose();
    }
}
