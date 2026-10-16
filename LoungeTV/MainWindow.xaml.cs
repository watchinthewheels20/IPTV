using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private List<LiveChannel> _visibleChannels = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly GamepadInput _gamepad = new();
    private readonly List<LiveChannel> _allChannels = new();
    private CancellationTokenSource? _epgDownload;
    private EpgSnapshot _epg = EpgSnapshot.Empty;
    private XtreamApiClient? _api;
    private IptvAccount? _account;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private LiveChannel? _playingChannel;
    private readonly SemaphoreSlim _mediaGate = new(1, 1);
    private int _playbackRequest;
    private bool _receivedPlaying;
    private bool _autoFullscreenPending;
    private bool _closingInProgress;
    private bool _closePermitted;
    private bool _connecting;
    private bool _fullScreen;
    private readonly DispatcherTimer _overlayRefreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private Window? _epgOverlay;
    private TextBlock? _bannerChannel;
    private TextBlock? _bannerClock;
    private TextBlock? _bannerNow;
    private TextBlock? _bannerNext;
    private TextBlock? _bannerHint;
    private System.Windows.Controls.ProgressBar? _bannerProgress;
    private int _bannerSelectedIndex;
    private int _bannerProgrammeIndex;
    private bool _focusChannels = true;
    private DispatcherOperation? _scrollOperation;
    private WindowState _previousWindowState = WindowState.Normal;
    private Thickness _normalBrowseMargin;

    public MainWindow()
    {
        InitializeComponent();
        CategoryList.ItemsSource = _categories;
        ChannelList.ItemsSource = _visibleChannels;
        _normalBrowseMargin = BrowsePanel.Margin;
        GuideView.WatchRequested += WatchGuideChannel;
        GuideView.RefreshRequested += () => { if (_api is not null) _ = LoadEpgAsync(_api); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); if (_allChannels.Count > 0) UpdateVisibleChannels(); };
        _gamepad.Command += HandleGamepad;
        _gamepad.Start();
        _overlayRefreshTimer.Tick += (_, _) => RefreshOverlayListings();

        var (server, user, password) = SettingsStore.Load();
        ServerBox.Text = server;
        UsernameBox.Text = user;
        PasswordInput.Password = password;
        RememberCheck.IsChecked = !string.IsNullOrEmpty(password);
        if (!string.IsNullOrEmpty(server))
            LoginMessage.Text = "Saved settings loaded. Select Connect to continue.";
        if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(user) &&
            !string.IsNullOrWhiteSpace(password))
            Loaded += (_, _) => _ = ConnectAsync();
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
            // Some Xtream implementations perform expensive JSON parsing synchronously
            // after network responses. Keep authentication/catalogue work off the UI thread.
            var catalogueClient = newClient;
            var (account, categories, channels) = await Task.Run(async () =>
            {
                var authenticated = await catalogueClient.AuthenticateAsync();
                var loadedCategories = await catalogueClient.GetCategoriesAsync();
                var loadedChannels = await catalogueClient.GetChannelsAsync();
                return (authenticated, loadedCategories, loadedChannels);
            });
            if (channels.Count == 0)
                throw new InvalidDataException("Login succeeded but this account has no accessible live channels.");

            // Only swap the active connection after the new one has loaded completely.
            var previousClient = _api;
            _api = newClient;
            newClient = null;
            _account = account;
            _epgDownload?.Cancel();
            _epg = EpgSnapshot.Empty;
            GuideView.SetEpg(_epg);
            previousClient?.Dispose();

            _allChannels.Clear();
            _allChannels.AddRange(channels);
            _categories.Clear();
            _categories.Add(new ChannelCategory("*", "All channels"));
            foreach (var category in categories) _categories.Add(category);
            CategoryList.SelectedIndex = 0;
            UpdateVisibleChannels();
            GuideView.SetChannels(_allChannels);
            GuideView.SetCategories(_categories);
            HomeCategoryItems.ItemsSource = categories.Take(9).ToArray();
            UpdateResumeTile();

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
            HomePanel.Visibility = Visibility.Collapsed;
            BrowsePanel.Visibility = Visibility.Collapsed;
            GuideView.Visibility = Visibility.Visible;
            GuideButton.Visibility = ChannelsButton.Visibility = HomeButton.Visibility =
                AccountButton.Visibility = RefreshButton.Visibility = Visibility.Visible;
            FullScreenButton.Visibility = Visibility.Collapsed;
            GeneralStatusText.Text = $"Connected · {channels.Count:N0} channels · {categories.Count:N0} categories.{persisted}";
            PlayerStatusText.Text = "Select a channel and press A, Enter or Watch channel.";
            GuideView.GoNow();
            GuideView.Focus();
            _focusChannels = true;
            _ = LoadEpgAsync(_api!);

            // Resume live TV automatically only when login is securely remembered
            // and the prior channel still exists in this provider's catalogue.
            if (RememberCheck.IsChecked == true)
            {
                var recent = _allChannels.FirstOrDefault(c => c.Id == RecentChannelStore.Load());
                if (recent is not null)
                {
                    ChannelList.SelectedItem = _visibleChannels.FirstOrDefault(c => c.Id == recent.Id);
                    ShowLiveTv();
                    _ = ResumeLastChannelAsync();
                }
            }
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

    private async Task ResumeLastChannelAsync()
    {
        // First make the browser and video surface visible and let WPF lay them out.
        // Calling native playback while the VideoView is still being revealed is
        // unreliable on some PCs, particularly during fullscreen transitions.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (_closingInProgress || BrowsePanel.Visibility != Visibility.Visible) return;

        _autoFullscreenPending = true;
        await PlaySelectedChannelAsync();

        // Startup-specific recovery: when a first attempt never reaches Playing,
        // retry once after a clean stop (equivalent to the successful manual fix).
        // Never loop indefinitely or retry if the viewer has changed channels.
        var initialRequest = Volatile.Read(ref _playbackRequest);
        var initialId = _playingChannel?.Id;
        if (initialId is null) return;
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (_closingInProgress || _receivedPlaying || !_autoFullscreenPending ||
            Volatile.Read(ref _playbackRequest) != initialRequest ||
            _playingChannel?.Id != initialId) return;

        PlaybackDiagnostics.Note("Autoplay: first attempt did not reach Playing; retrying once");
        await StopPlaybackAsync();
        if (_closingInProgress || BrowsePanel.Visibility != Visibility.Visible) return;
        ChannelList.SelectedItem = _visibleChannels.FirstOrDefault(c => c.Id == initialId);
        _autoFullscreenPending = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await PlaySelectedChannelAsync();
    }

    private void ReportConnectionError(string message)
    {
        LoginMessage.Text = message;
        GeneralStatusText.Text = message;
        if (BrowsePanel.Visibility == Visibility.Visible)
            MessageBox.Show(this, message, "Connection failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task LoadEpgAsync(XtreamApiClient client)
    {
        _epgDownload?.Cancel();
        var cts = new CancellationTokenSource();
        _epgDownload = cts;
        GuideView.SetStatus("Downloading programme listings…");
        try
        {
            // XMLTV feeds can be large. Parse on a worker, never on the WPF UI thread.
            var subscribedChannels = _allChannels.ToArray();
            var data = await Task.Run(() => client.GetEpgAsync(subscribedChannels, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || _api != client || _closingInProgress) return;
            _epg = data;
            GuideView.SetEpg(data);
            if (_epgOverlay is not null) RefreshOverlayListings();
            PlaybackDiagnostics.Note($"EPG parsed: {data.ProgrammeCount} programmes");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && _api == client && !_closingInProgress)
            {
                GuideView.SetStatus($"EPG unavailable ({ex.GetType().Name}). Press Refresh EPG to retry.");
                PlaybackDiagnostics.Failure("Loading EPG", ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_epgDownload, cts)) _epgDownload = null;
            cts.Dispose();
        }
    }

    private void WatchGuideChannel(LiveChannel channel)
    {
        _autoFullscreenPending = false;
        CategoryList.SelectedIndex = 0;
        SearchBox.Text = "";
        UpdateVisibleChannels();
        ChannelList.SelectedItem = _visibleChannels.FirstOrDefault(c => c.Id == channel.Id);
        ShowLiveTv();
        PlaySelectedChannel();
    }

    private void GuideButton_Click(object sender, RoutedEventArgs e) => ShowGuide();
    private void ChannelsButton_Click(object sender, RoutedEventArgs e) => ShowLiveTv();

    private void ShowGuide()
    {
        if (_api is null) return;
        if (_fullScreen) ToggleFullscreen();
        HomePanel.Visibility = Visibility.Collapsed;
        BrowsePanel.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Visible;
        FullScreenButton.Visibility = Visibility.Collapsed;
        GuideView.Focus();
    }

    private void UpdateVisibleChannels()
    {
        // Replace one ItemsSource instead of triggering thousands of
        // ObservableCollection change notifications for large provider lineups.
        var previousId = (ChannelList.SelectedItem as LiveChannel)?.Id;
        var categoryId = (CategoryList.SelectedItem as ChannelCategory)?.Id ?? "*";
        var search = SearchBox.Text.Trim();
        _visibleChannels = _allChannels.Where(channel =>
            (categoryId == "*" || channel.CategoryId == categoryId) &&
            (search.Length == 0 || channel.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        ChannelList.ItemsSource = _visibleChannels;
        ChannelList.SelectedItem = _visibleChannels.FirstOrDefault(c => c.Id == previousId) ??
                                   _visibleChannels.FirstOrDefault();
        if (_allChannels.Count > 0)
            GeneralStatusText.Text = $"{_visibleChannels.Count:N0} channels shown / {_allChannels.Count:N0} total";
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _searchTimer.Stop();
        if (_allChannels.Count > 0) UpdateVisibleChannels();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_allChannels.Count == 0) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void UpdateResumeTile()
    {
        var id = RecentChannelStore.Load();
        var recent = _allChannels.FirstOrDefault(c => c.Id == id);
        HomeResumeButton.IsEnabled = recent is not null;
        HomeResumeName.Text = recent?.Name ?? "No recently watched channel";
    }

    private void ShowLiveTv(bool focusSearch = false)
    {
        if (_api is null) return;
        HomePanel.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Collapsed;
        BrowsePanel.Visibility = Visibility.Visible;
        FullScreenButton.Visibility = Visibility.Visible;
        if (focusSearch) SearchBox.Focus();
        else ChannelList.Focus();
        _focusChannels = true;
    }

    private void ShowHome()
    {
        if (_api is null) return;
        _autoFullscreenPending = false;
        if (_fullScreen) ToggleFullscreen();
        BrowsePanel.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
        FullScreenButton.Visibility = Visibility.Collapsed;
        UpdateResumeTile();
        HomeLiveButton.Focus();
        _ = StopPlaybackAsync(); // Do not leave live TV running invisibly.
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void HomeLiveButton_Click(object sender, RoutedEventArgs e) => ShowGuide();
    private void HomeBrowseButton_Click(object sender, RoutedEventArgs e) => ShowLiveTv();
    private void HomeSearchButton_Click(object sender, RoutedEventArgs e) => ShowLiveTv(focusSearch: true);

    private void HomeResumeButton_Click(object sender, RoutedEventArgs e)
    {
        var recentId = RecentChannelStore.Load();
        var recent = _allChannels.FirstOrDefault(c => c.Id == recentId);
        if (recent is null) { UpdateResumeTile(); return; }
        CategoryList.SelectedIndex = 0;
        SearchBox.Text = "";
        UpdateVisibleChannels();
        ChannelList.SelectedItem = _visibleChannels.FirstOrDefault(c => c.Id == recentId);
        ShowLiveTv();
        PlaySelectedChannel();
    }

    private void HomeCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string categoryId }) return;
        var category = _categories.FirstOrDefault(c => c.Id == categoryId);
        if (category is null) return;
        SearchBox.Text = "";
        CategoryList.SelectedItem = category;
        UpdateVisibleChannels();
        ShowLiveTv();
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

        PlaybackDiagnostics.Note("Initialising LibVLC");
        Core.Initialize();
        _libVlc = new LibVLC("--network-caching=900", "--live-caching=900", "--no-video-title-show");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Opening += (_, _) => DispatchPlaybackStatus("Opening stream…");
        _mediaPlayer.Buffering += (_, _) => DispatchPlaybackStatus("Buffering stream…");
        _mediaPlayer.Playing += (_, _) => DispatchPlaybackStatus("Playing live TV", confirmed: true);
        _mediaPlayer.EncounteredError += (_, _) =>
            DispatchPlaybackStatus("Playback error. Try another stream format or channel.");
        _mediaPlayer.EndReached += (_, _) => DispatchPlaybackStatus("Stream ended.");
        PlayerView.MediaPlayer = _mediaPlayer;
        PlaybackDiagnostics.Note("LibVLC player created and attached to VideoView");
    }

    private void DispatchPlaybackStatus(string message, bool confirmed = false)
    {
        // LibVLC callbacks run on native worker threads. Never call Stop or Play in a callback.
        PlaybackDiagnostics.Note("LibVLC event: " + message);
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_closingInProgress || _playingChannel is null) return;
            if (confirmed)
            {
                _receivedPlaying = true;
                if (_autoFullscreenPending)
                {
                    _autoFullscreenPending = false;
                    if (!_fullScreen && BrowsePanel.Visibility == Visibility.Visible)
                        ToggleFullscreen();
                }
            }
            PlayerStatusText.Text = message;
        }));
    }

    private void PlaySelectedChannel() => _ = PlaySelectedChannelAsync();

    private async Task PlaySelectedChannelAsync()
    {
        if (_closingInProgress || _api is null || _account is null ||
            ChannelList.SelectedItem is not LiveChannel channel)
            return;

        // Invalidate any older pending playback or timeout request.
        var request = Interlocked.Increment(ref _playbackRequest);
        _receivedPlaying = false;
        _playingChannel = channel;
        NowPlayingText.Text = channel.Name;
        var format = StreamFormatBox.SelectedIndex switch
        {
            1 => "ts",
            2 => "m3u8",
            _ => _account.AllowedFormats.Contains("ts") || _account.AllowedFormats.Count == 0 ? "ts" :
                _account.AllowedFormats.Contains("m3u8") ? "m3u8" : "ts"
        };
        PlayerStatusText.Text = $"Preparing {format.ToUpperInvariant()} stream…";
        PlaybackDiagnostics.Note($"Requested playback (format {format}); no URL or credentials recorded");
        _ = PlaybackWatchdogAsync(request);
        bool needsStop = false;

        try
        {
            EnsurePlayer(); // VideoView.MediaPlayer must be set on the WPF UI thread.
            var streamUri = _api.GetStreamUri(channel.Id, format);
            await _mediaGate.WaitAsync();
            try
            {
                if (request != Volatile.Read(ref _playbackRequest)) return;
                PlayerStatusText.Text = $"Opening {format.ToUpperInvariant()} stream…";
                PlaybackDiagnostics.Note("Calling LibVLC MediaPlayer.Play");

                // LibVLC Stop is synchronous and may wait for native threads: never run
                // it on the UI thread. Serialize all stop/play operations to avoid races.
                var started = await Task.Run(() =>
                {
                    if (_currentMedia is not null)
                    {
                        var previous = _currentMedia;
                        try { _mediaPlayer!.Stop(); }
                        finally
                        {
                            previous.Dispose();
                            _currentMedia = null;
                        }
                    }

                    var media = new Media(_libVlc!, streamUri);
                    _currentMedia = media;
                    return _mediaPlayer!.Play(media);
                });
                PlaybackDiagnostics.Note(started ? "LibVLC Play returned true" : "LibVLC Play returned false");
                if (started && request == Volatile.Read(ref _playbackRequest))
                    RecentChannelStore.Save(channel.Id);
                if (request == Volatile.Read(ref _playbackRequest) && !started)
                {
                    PlayerStatusText.Text = "VLC could not start this stream. Try a different format.";
                    needsStop = true;
                }
            }
            finally { _mediaGate.Release(); }
        }
        catch (Exception ex)
        {
            PlaybackDiagnostics.Failure("Starting playback", ex);
            if (request == Volatile.Read(ref _playbackRequest))
            {
                PlayerStatusText.Text = $"Playback failed ({ex.GetType().Name}). Try HLS or MPEG-TS.";
                needsStop = true;
            }
        }

        if (needsStop && request == Volatile.Read(ref _playbackRequest))
            await StopPlaybackAsync();
    }

    private async Task PlaybackWatchdogAsync(int request)
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        if (_closingInProgress || request != Volatile.Read(ref _playbackRequest) || _receivedPlaying)
            return;

        PlaybackDiagnostics.Note("Playback timed out: no Playing event after 20 seconds");
        PlayerStatusText.Text = "Stream timed out after 20 seconds. Try HLS/MPEG-TS or another channel.";
        await StopPlaybackAsync();
        // Preserve the useful timeout message after stopping.
        if (!_closingInProgress && request + 1 == Volatile.Read(ref _playbackRequest))
            PlayerStatusText.Text = "Stream timed out. Try HLS/MPEG-TS or another channel.";
    }

    private void StopPlayback() { _autoFullscreenPending = false; _ = StopPlaybackAsync(); }

    private async Task StopPlaybackAsync()
    {
        var request = Interlocked.Increment(ref _playbackRequest);
        _receivedPlaying = false;
        _playingChannel = null;
        PlayerStatusText.Text = "Stopping playback…";
        PlaybackDiagnostics.Note("Stop requested");

        await _mediaGate.WaitAsync();
        try
        {
            if (_currentMedia is not null)
            {
                PlaybackDiagnostics.Note("Calling native LibVLC Stop in background");
                await Task.Run(() =>
                {
                    var current = _currentMedia;
                    try { _mediaPlayer?.Stop(); }
                    finally
                    {
                        current?.Dispose();
                        _currentMedia = null;
                    }
                });
                PlaybackDiagnostics.Note("Native LibVLC Stop completed");
            }
            if (request == Volatile.Read(ref _playbackRequest))
            {
                PlayerStatusText.Text = "Playback stopped";
                NowPlayingText.Text = (ChannelList.SelectedItem as LiveChannel)?.Name ??
                                      "Select a channel to begin";
            }
        }
        catch (Exception ex)
        {
            PlaybackDiagnostics.Failure("Stopping playback", ex);
            if (request == Volatile.Read(ref _playbackRequest))
                PlayerStatusText.Text = $"Playback stop failed ({ex.GetType().Name}).";
        }
        finally { _mediaGate.Release(); }
    }

    private async void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fullScreen) ToggleFullscreen();
        await StopPlaybackAsync();
        BrowsePanel.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        GuideButton.Visibility = ChannelsButton.Visibility = HomeButton.Visibility =
            AccountButton.Visibility = RefreshButton.Visibility = FullScreenButton.Visibility = Visibility.Collapsed;
        LoginMessage.Text = "Update your connection or reconnect.";
        ServerBox.Focus();
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (BrowsePanel.Visibility != Visibility.Visible) return;
        if (_fullScreen) CloseEpgOverlay();
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
            PlayerContainerGrid.RowDefinitions[0].Height = new GridLength(0);
            PlayerContainerGrid.RowDefinitions[2].Height = new GridLength(0);
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
            PlayerContainerGrid.RowDefinitions[0].Height = GridLength.Auto;
            PlayerContainerGrid.RowDefinitions[2].Height = GridLength.Auto;
            _fullScreen = false;
            ChannelList.Focus();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // This handler is registered as PreviewKeyDown in XAML so the remote's
        // arrows / OK are not swallowed by the focused WPF ListBox.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (LoginPanel.Visibility == Visibility.Visible) return;
        if (key is Key.BrowserBack or Key.Escape or Key.Back)
        {
            if (_epgOverlay is not null) CloseEpgOverlay();
            else if (_fullScreen) ToggleFullscreen();
            else if (GuideView.Visibility == Visibility.Visible) ShowLiveTv();
            else if (BrowsePanel.Visibility == Visibility.Visible) ShowHome();
            e.Handled = true;
            return;
        }

        // The Rii i25 usually presents as a keyboard, not an XInput controller.
        // Apps/Menu is the player/fullscreen toggle; OK opens the compact banner.
        if (key is Key.Apps or Key.F11)
        {
            if (BrowsePanel.Visibility == Visibility.Visible) ToggleFullscreen();
            else if (GuideView.Visibility == Visibility.Visible) ShowLiveTv();
            e.Handled = true;
            return;
        }

        if (_fullScreen)
        {
            if (key is Key.PageUp or Key.PageDown or Key.G or Key.Tab)
            {
                ToggleEpgOverlay(); e.Handled = true; return;
            }
            if (_epgOverlay is not null)
            {
                if (key is Key.Up or Key.Down) MoveOverlaySelection(key == Key.Up ? -1 : 1);
                else if (key is Key.Left or Key.Right)
                {
                    _bannerProgrammeIndex = key == Key.Left ? 0 : 1;
                    RefreshOverlayListings();
                }
                else if (key is Key.Enter or Key.Return) TuneOverlaySelection();
                else return;
                e.Handled = true;
                return;
            }
            if (key is Key.Enter or Key.Return or Key.Left or Key.Right)
            {
                ToggleEpgOverlay(); e.Handled = true; return;
            }
            if (key is Key.Up or Key.Down)
            {
                ChangeChannel(key == Key.Up ? -1 : 1);
                e.Handled = true; return;
            }
        }

        if (GuideView.Visibility == Visibility.Visible)
        {
            if (GuideView.IsEditingPicker) return;
            if (key is Key.OemOpenBrackets or Key.PageUp)
            {
                GuideView.ChangeOffset(-30); e.Handled = true; return;
            }
            if (key is Key.OemCloseBrackets or Key.PageDown)
            {
                GuideView.ChangeOffset(30); e.Handled = true; return;
            }
            if (key == Key.C)
            {
                GuideView.Navigate(GamepadCommand.NextCategory); e.Handled = true; return;
            }
            var navigation = key switch
            {
                Key.Up => GamepadCommand.Up,
                Key.Down => GamepadCommand.Down,
                Key.Left => GamepadCommand.Left,
                Key.Right => GamepadCommand.Right,
                Key.Enter => GamepadCommand.Select,
                _ => (GamepadCommand?)null
            };
            if (navigation is { } command)
            {
                GuideView.Navigate(command);
                e.Handled = true;
                return;
            }
        }

        if (HomePanel.Visibility == Visibility.Visible &&
            key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            var direction = key is Key.Up or Key.Left ?
                FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
            if (Keyboard.FocusedElement is UIElement current)
                current.MoveFocus(new TraversalRequest(direction));
            else HomeLiveButton.Focus();
            e.Handled = true;
            return;
        }

        if (key == Key.Enter && BrowsePanel.Visibility == Visibility.Visible &&
            (ChannelList.IsKeyboardFocusWithin || CategoryList.IsKeyboardFocusWithin))
        {
            PlaySelectedChannel(); e.Handled = true;
        }
    }

    private void HandleGamepad(GamepadCommand command)
    {
        if (!IsActive) return;

        if (GuideView.Visibility == Visibility.Visible)
        {
            if (command == GamepadCommand.Back) ShowLiveTv();
            else GuideView.Navigate(command);
            return;
        }

        if (HomePanel.Visibility == Visibility.Visible)
        {
            if (command is GamepadCommand.Up or GamepadCommand.Left or GamepadCommand.Down or GamepadCommand.Right)
            {
                var direction = command is GamepadCommand.Up or GamepadCommand.Left ?
                    FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
                if (Keyboard.FocusedElement is UIElement current)
                    current.MoveFocus(new TraversalRequest(direction));
                else HomeLiveButton.Focus();
            }
            else if (command == GamepadCommand.Select &&
                     Keyboard.FocusedElement is Button button && button.IsEnabled)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            return;
        }

        if (BrowsePanel.Visibility != Visibility.Visible) return;
        if (command == GamepadCommand.Fullscreen) { ToggleFullscreen(); return; }
        if (command == GamepadCommand.Back)
        {
            if (_epgOverlay is not null) { CloseEpgOverlay(); return; }
            if (_fullScreen) ToggleFullscreen();
            else ShowHome();
            return;
        }
        if (_fullScreen)
        {
            if (command == GamepadCommand.NextCategory) { ToggleEpgOverlay(); return; }
            if (_epgOverlay is not null)
            {
                if (command == GamepadCommand.Back) CloseEpgOverlay();
                else if (command == GamepadCommand.Up) MoveOverlaySelection(-1);
                else if (command == GamepadCommand.Down) MoveOverlaySelection(1);
                else if (command == GamepadCommand.Select) TuneOverlaySelection();
                return;
            }
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


    // A separate transparent owned WPF window avoids the HWND airspace issue
    // introduced by LibVLC's native video surface. No second stream is created.
    private void ToggleEpgOverlay()
    {
        if (!_fullScreen) return;
        if (_epgOverlay is not null) { CloseEpgOverlay(); return; }

        _bannerSelectedIndex = Math.Max(0, _visibleChannels.FindIndex(
            c => c.Id == _playingChannel?.Id));
        _bannerProgrammeIndex = 0;
        var gold = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(255, 206, 61));
        var white = System.Windows.Media.Brushes.White;
        _bannerChannel = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = white };
        _bannerClock = new TextBlock { FontSize = 15, Foreground = white, HorizontalAlignment = HorizontalAlignment.Right };
        _bannerNow = new TextBlock { FontSize = 20, Foreground = gold, TextTrimming = TextTrimming.CharacterEllipsis };
        _bannerNext = new TextBlock { FontSize = 18, Foreground = white, TextTrimming = TextTrimming.CharacterEllipsis };
        _bannerHint = new TextBlock { FontSize = 13, Foreground = white, Margin = new Thickness(0, 10, 0, 0) };
        _bannerProgress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Foreground = gold,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(25, 60, 98)), Margin = new Thickness(0, 8, 0, 0) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_bannerChannel);
        Grid.SetColumn(_bannerClock, 1);
        header.Children.Add(_bannerClock);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(new Separator { Margin = new Thickness(0, 9, 0, 10),
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(65, 105, 151)) });
        stack.Children.Add(_bannerNow);
        stack.Children.Add(_bannerNext);
        stack.Children.Add(_bannerProgress);
        stack.Children.Add(_bannerHint);

        var surface = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(218, 9, 37, 79)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(71, 116, 166)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(19, 14, 19, 11),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Width = Math.Min(960, Math.Max(470, ActualWidth * .82)),
            Margin = new Thickness(0, 0, 0, Math.Max(28, ActualHeight * .075)),
            Child = stack
        };
        var layout = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        layout.Children.Add(surface);
        _epgOverlay = new Window
        {
            Owner = this, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, ShowInTaskbar = false,
            ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.Transparent,
            Content = layout, Left = Left, Top = Top,
            Width = ActualWidth, Height = ActualHeight,
            IsHitTestVisible = false
        };
        _epgOverlay.Show();
        _overlayRefreshTimer.Start();
        RefreshOverlayListings();
    }

    private void CloseEpgOverlay()
    {
        _overlayRefreshTimer.Stop();
        var overlay = _epgOverlay;
        _epgOverlay = null;
        _bannerChannel = _bannerClock = _bannerNow = _bannerNext = _bannerHint = null;
        _bannerProgress = null;
        overlay?.Close();
    }

    private void RefreshOverlayListings()
    {
        if (_epgOverlay is null || _bannerChannel is null || _bannerClock is null ||
            _bannerNow is null || _bannerNext is null || _bannerHint is null || _bannerProgress is null)
            return;
        if (_visibleChannels.Count == 0) return;
        _bannerSelectedIndex = Math.Clamp(_bannerSelectedIndex, 0, _visibleChannels.Count - 1);
        var channel = _visibleChannels[_bannerSelectedIndex];
        var now = DateTimeOffset.UtcNow;
        var shift = TimeSpan.FromMinutes(EpgOffsetStore.LoadMinutes());
        var london = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        var programmes = _epg.ForChannel(channel);
        var playing = programmes.FirstOrDefault(p => p.StartUtc + shift <= now && p.EndUtc + shift > now);
        var next = programmes.FirstOrDefault(p => p.StartUtc + shift > now);
        static string TimeLabel(DateTimeOffset time, TimeZoneInfo zone) =>
            TimeZoneInfo.ConvertTime(time, zone).ToString("HH:mm");
        _bannerChannel.Text = channel.DisplayName + (_playingChannel?.Id == channel.Id ? "   • LIVE" : "   • PREVIEW");
        _bannerClock.Text = TimeZoneInfo.ConvertTime(now, london).ToString("ddd dd MMM  HH:mm");
        _bannerNow.Text = playing is null ? "NOW     No programme information" :
            $"NOW     {TimeLabel(playing.StartUtc + shift, london)}–{TimeLabel(playing.EndUtc + shift, london)}   {playing.Title}";
        _bannerNext.Text = next is null ? "NEXT    No programme information" :
            $"NEXT    {TimeLabel(next.StartUtc + shift, london)}   {next.Title}";
        _bannerNow.FontWeight = _bannerProgrammeIndex == 0 ? FontWeights.Bold : FontWeights.Normal;
        _bannerNext.FontWeight = _bannerProgrammeIndex == 1 ? FontWeights.Bold : FontWeights.Normal;
        _bannerHint.Text = "▲▼ Browse channels    ◄► Now / Next    OK Tune    Back Close    Menu Minimise";
        _bannerProgress.Value = playing is null ? 0 : Math.Clamp(
            (now - (playing.StartUtc + shift)).TotalSeconds /
            Math.Max(1, (playing.EndUtc - playing.StartUtc).TotalSeconds) * 100, 0, 100);
    }

    private void MoveOverlaySelection(int delta)
    {
        if (_epgOverlay is null || _visibleChannels.Count == 0) return;
        _bannerSelectedIndex = Math.Clamp(_bannerSelectedIndex + delta, 0, _visibleChannels.Count - 1);
        _bannerProgrammeIndex = 0;
        RefreshOverlayListings();
    }

    private void TuneOverlaySelection()
    {
        if (_epgOverlay is null || _visibleChannels.Count == 0) return;
        var channel = _visibleChannels[_bannerSelectedIndex];
        var changed = _playingChannel?.Id != channel.Id;
        ChannelList.SelectedItem = channel;
        CloseEpgOverlay();
        if (changed) PlaySelectedChannel();
    }

    private void MoveSelection(int delta)
    {
        var list = _focusChannels ? ChannelList : CategoryList;
        if (list.Items.Count == 0) return;
        var index = list.SelectedIndex < 0 ? 0 : list.SelectedIndex;
        var next = Math.Clamp(index + delta, 0, list.Items.Count - 1);
        if (next == list.SelectedIndex) return;
        list.SelectedIndex = next;

        // Scroll after the selection can render. Cancel an old request during rapid
        // D-pad repeats, so many synchronous ScrollIntoView calls cannot queue up.
        if (_scrollOperation?.Status == DispatcherOperationStatus.Pending)
            _scrollOperation.Abort();
        _scrollOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
        }));
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

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closePermitted) return;
        e.Cancel = true;
        if (_closingInProgress) return;

        _closingInProgress = true;
        CloseEpgOverlay();
        _gamepad.Dispose();
        _epgDownload?.Cancel();
        await StopPlaybackAsync();
        try
        {
            PlayerView.MediaPlayer = null;
            _mediaPlayer?.Dispose();
            _libVlc?.Dispose();
            _api?.Dispose();
            PlaybackDiagnostics.Note("Player closed normally");
        }
        catch (Exception ex)
        {
            PlaybackDiagnostics.Failure("Closing player", ex);
        }
        _closePermitted = true;
        Close();
    }
}
