﻿using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.CommandWpf;
using GalaSoft.MvvmLight.Threading;
using LightBulb.Models;
using LightBulb.Services.Helpers;
using LightBulb.Services.Interfaces;
using LightBulb.ViewModels.Interfaces;
using Tyrrrz.Extensions;
using Tyrrrz.WpfExtensions;

namespace LightBulb.ViewModels
{
    public class MainViewModel : ViewModelBase, IMainViewModel, IDisposable
    {
        private readonly ITemperatureService _temperatureService;
        private readonly IWindowService _windowService;
        private readonly IHotkeyService _hotkeyService;
        private readonly IGeoService _geoService;
        private readonly IVersionCheckService _versionCheckService;

        private readonly SyncedTimer _statusUpdateTimer;
        private readonly SyncedTimer _geoSyncTimer;
        private readonly Timer _disableTemporarilyTimer;

        private bool _isUpdateAvailable;
        private bool _isEnabled;
        private bool _isBlocked;
        private string _statusText;
        private CycleState _cycleState;
        private double _cyclePosition;

        /// <inheritdoc />
        public ISettingsService SettingsService { get; }

        /// <inheritdoc />
        public Version Version => Assembly.GetExecutingAssembly().GetName().Version;

        /// <inheritdoc />
        public bool IsUpdateAvailable
        {
            get { return _isUpdateAvailable; }
            private set { Set(ref _isUpdateAvailable, value); }
        }

        /// <inheritdoc />
        public bool IsEnabled
        {
            get { return _isEnabled; }
            set
            {
                if (!Set(ref _isEnabled, value)) return;
                if (value) _disableTemporarilyTimer.IsEnabled = false;

                _temperatureService.IsRealtimeModeEnabled = value && !IsBlocked;
            }
        }

        /// <inheritdoc />
        public bool IsBlocked
        {
            get { return _isBlocked; }
            private set
            {
                if (!Set(ref _isBlocked, value)) return;

                _temperatureService.IsRealtimeModeEnabled = !value && IsEnabled;
            }
        }

        /// <inheritdoc />
        public string StatusText
        {
            get { return _statusText; }
            private set { Set(ref _statusText, value); }
        }

        /// <inheritdoc />
        public CycleState CycleState
        {
            get { return _cycleState; }
            private set { Set(ref _cycleState, value); }
        }

        /// <inheritdoc />
        public double CyclePosition
        {
            get { return _cyclePosition; }
            private set { Set(ref _cyclePosition, value); }
        }

        // Commands
        public RelayCommand ShowMainWindowCommand { get; }
        public RelayCommand ExitApplicationCommand { get; }
        public RelayCommand AboutCommand { get; }
        public RelayCommand ToggleEnabledCommand { get; }
        public RelayCommand<double> DisableTemporarilyCommand { get; }
        public RelayCommand DownloadNewVersionCommand { get; }

        public MainViewModel(
            ISettingsService settingsService,
            ITemperatureService temperatureService,
            IWindowService windowService,
            IHotkeyService hotkeyService,
            IGeoService geoService,
            IVersionCheckService versionCheckService)
        {
            // Services
            SettingsService = settingsService;
            _temperatureService = temperatureService;
            _windowService = windowService;
            _hotkeyService = hotkeyService;
            _geoService = geoService;
            _versionCheckService = versionCheckService;

            _temperatureService.Updated += (sender, args) =>
            {
                UpdateStatusText();
                UpdateCycleState();
                UpdateCyclePosition();
            };
            _windowService.FullScreenStateChanged += (sender, args) =>
            {
                UpdateBlock();
            };

            // Timers
            _statusUpdateTimer = new SyncedTimer(TimeSpan.FromMinutes(1));
            _statusUpdateTimer.Tick += (sender, args) =>
            {
                UpdateStatusText();
                UpdateCycleState();
                UpdateCyclePosition();
            };

            _geoSyncTimer = new SyncedTimer();
            _geoSyncTimer.Tick += async (sender, args) => await SynchronizeSolarSettingsAsync();

            _disableTemporarilyTimer = new Timer();
            _disableTemporarilyTimer.Tick += (sender, args) =>
            {
                IsEnabled = true;
                _disableTemporarilyTimer.IsEnabled = false;
            };

            // Commands
            ShowMainWindowCommand = new RelayCommand(() =>
            {
                DispatcherHelper.CheckBeginInvokeOnUI(() =>
                {
                    Application.Current.MainWindow.Show();
                    Application.Current.MainWindow.Activate();
                    Application.Current.MainWindow.Focus();
                });
            });
            ExitApplicationCommand = new RelayCommand(() =>
            {
                Application.Current.ShutdownSafe();
            });
            AboutCommand = new RelayCommand(() =>
            {
                Process.Start("https://github.com/Tyrrrz/LightBulb");
            });
            ToggleEnabledCommand = new RelayCommand(() =>
            {
                IsEnabled = !IsEnabled;
            });
            DisableTemporarilyCommand = new RelayCommand<double>(ms =>
            {
                _disableTemporarilyTimer.IsEnabled = false;
                _disableTemporarilyTimer.Interval = TimeSpan.FromMilliseconds(ms);
                IsEnabled = false;
                _disableTemporarilyTimer.IsEnabled = true;
            });
            DownloadNewVersionCommand = new RelayCommand(() =>
            {
                Process.Start("https://github.com/Tyrrrz/LightBulb/releases");
            });

            // Settings
            SettingsService.PropertyChanged += (sender, args) =>
            {
                UpdateConfiguration();

                if (args.PropertyName.IsEither(
                    nameof(SettingsService.ToggleHotkey),
                    nameof(SettingsService.TogglePollingHotkey),
                    nameof(SettingsService.RefreshGammaHotkey)))
                    UpdateHotkeys();

                if (args.PropertyName == nameof(SettingsService.IsInternetTimeSyncEnabled))
                    SynchronizeSolarSettingsAsync().Forget();
            };
            UpdateConfiguration();
            UpdateHotkeys();

            // Init
            CheckForUpdates().Forget();
            SynchronizeSolarSettingsAsync().Forget();
            _statusUpdateTimer.IsEnabled = true;
            IsEnabled = true;
        }

        private void UpdateConfiguration()
        {
            _geoSyncTimer.Interval = SettingsService.InternetSyncInterval;
            _geoSyncTimer.IsEnabled = SettingsService.IsInternetTimeSyncEnabled;
        }

        private void UpdateHotkeys()
        {
            _hotkeyService.UnregisterAllHotkeys();

            if (SettingsService.ToggleHotkey != null)
                _hotkeyService.RegisterHotkey(SettingsService.ToggleHotkey,
                    () => IsEnabled = !IsEnabled);

            if (SettingsService.TogglePollingHotkey != null)
                _hotkeyService.RegisterHotkey(SettingsService.TogglePollingHotkey,
                    () => SettingsService.IsGammaPollingEnabled = !SettingsService.IsGammaPollingEnabled);

            if (SettingsService.RefreshGammaHotkey != null)
                _hotkeyService.RegisterHotkey(SettingsService.RefreshGammaHotkey,
                    () => _temperatureService.RefreshGamma());
        }

        private void UpdateBlock()
        {
            IsBlocked = SettingsService.IsFullscreenBlocking && _windowService.IsForegroundFullScreen;

            Debug.WriteLine($"Updated block status (to {IsBlocked})", GetType().Name);
        }

        private void UpdateStatusText()
        {
            // Preview mode (24 hr cycle preview)
            if (_temperatureService.IsPreviewModeEnabled && _temperatureService.IsCyclePreviewRunning)
            {
                StatusText =
                    $"Temp: {_temperatureService.Temperature}K   Time: {_temperatureService.CyclePreviewTime:t}   (preview)";
            }
            // Preview mode
            else if (_temperatureService.IsPreviewModeEnabled)
            {
                StatusText = $"Temp: {_temperatureService.Temperature}K   (preview)";
            }
            // Not enabled
            else if (!IsEnabled)
            {
                StatusText = "Disabled";
            }
            // Blocked
            else if (IsBlocked)
            {
                StatusText = "Blocked";
            }
            // Realtime mode
            else
            {
                StatusText = $"Temp: {_temperatureService.Temperature}K";
            }
        }

        private void UpdateCycleState()
        {
            // Not enabled or blocked
            if (!IsEnabled || IsBlocked)
            {
                CycleState = CycleState.Disabled;
            }
            else
            {
                if (_temperatureService.Temperature >= SettingsService.MaxTemperature)
                {
                    CycleState = CycleState.Day;
                }
                else if (_temperatureService.Temperature <= SettingsService.MinTemperature)
                {
                    CycleState = CycleState.Night;
                }
                else
                {
                    CycleState = CycleState.Transition;
                }
            }
        }

        private void UpdateCyclePosition()
        {
            // Preview mode (24 hr cycle preview)
            if (_temperatureService.IsPreviewModeEnabled && _temperatureService.IsCyclePreviewRunning)
            {
                CyclePosition = _temperatureService.CyclePreviewTime.TimeOfDay.TotalHours/24;
            }
            // Preview mode
            else if (_temperatureService.IsPreviewModeEnabled)
            {
                CyclePosition = 0;
            }
            // Not enabled or blocked
            else if (!IsEnabled || IsBlocked)
            {
                CyclePosition = 0;
            }
            // Realtime mode
            else
            {
                CyclePosition = DateTime.Now.TimeOfDay.TotalHours/24;
            }
        }

        /// <summary>
        /// Check for program updates
        /// </summary>
        private async Task CheckForUpdates()
        {
            IsUpdateAvailable = await _versionCheckService.GetUpdateStatusAsync();

            Debug.WriteLine($"Checked for updates ({(IsUpdateAvailable ? "update found" : "update not found")})",
                GetType().Name);
        }

        /// <summary>
        /// Get updated solar info and overwrite respective settings with new data
        /// </summary>
        private async Task SynchronizeSolarSettingsAsync()
        {
            if (!SettingsService.IsInternetTimeSyncEnabled) return;

            // Geo info
            if (SettingsService.GeoInfo == null || SettingsService.ShouldUpdateGeoInfo)
            {
                var geoInfo = await _geoService.GetGeoInfoAsync();
                if (geoInfo == null) return;
                SettingsService.GeoInfo = geoInfo;
            }

            // Solar info
            var solarInfo = await _geoService.GetSolarInfoAsync(SettingsService.GeoInfo);
            if (solarInfo == null) return;

            if (SettingsService.IsInternetTimeSyncEnabled)
            {
                SettingsService.SunriseTime = solarInfo.SunriseTime;
                SettingsService.SunsetTime = solarInfo.SunsetTime;
            }

            Debug.WriteLine("Geosync done", GetType().Name);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _statusUpdateTimer.Dispose();
                _geoSyncTimer.Dispose();
                _disableTemporarilyTimer.Dispose();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}