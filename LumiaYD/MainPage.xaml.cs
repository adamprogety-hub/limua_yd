using System;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Microsoft.Phone.Tasks;
using System.IO.IsolatedStorage;
using System.Net;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Xna.Framework.Media;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.Activation;

namespace LumiaYD
{
    public partial class MainPage : PhoneApplicationPage
    {
        private const string LoginKey = "YandexLogin";
        private const string PasswordKey = "YandexPassword";
        private const string CurrentPathKey = "CurrentPath";
        private const string PendingDownloadNameKey = "PendingDownloadName";
        private const string PendingDownloadHrefKey = "PendingDownloadHref";
        private const string PendingDownloadIsFolderKey = "PendingDownloadIsFolder";
        private const string TransferHistoryKey = "TransferHistory";
        private const int TransferHistoryLimit = 40;
        private const long DownloadChunkSize = 8L * 1024 * 1024;
        private const int DownloadMaxAttempts = 5;

        private string _login;
        private string _password;
        private bool _initialized;

        private string currentPath = "/";
        private YandexFileItem selectedItem = null;
        private YandexFileItem _pendingDownloadFile;
        private readonly ObservableCollection<TransferItem> _activeTransfers = new ObservableCollection<TransferItem>();
        private readonly ObservableCollection<TransferItem> _completedTransfers = new ObservableCollection<TransferItem>();

        private YandexWebDavClient _webDav;
        private QuotaInfo _quota;
        private bool _quotaFailed;
        private double _storagePercent;
        private int _activeDownloads;
        private int _folderRequestId;
        private CancellationTokenSource _folderCancellation;
        private CancellationTokenSource _quotaCancellation;
        private bool _accountMenuOpen;
        private bool _dialogOpen;
        private bool _dialogInsideAccountMenu;
        private bool _suppressBackdropTap;
        private Action _dialogPrimaryAction;
        private Action _dialogSecondaryAction;
        private bool _dialogInputOpen;
        private Action<string> _dialogInputAction;

        private readonly DispatcherTimer _directoryCollapseTimer = new DispatcherTimer();
        private bool _directoryExpanded = false;
        private bool _selectedIslandOpen;
        private int _selectedIslandVersion;
        private DateTime _suppressTapUntil;
        private bool _transferIslandOpen;
        private int _transferIslandVersion;
        private TransferItem _transferIslandItem;
        private bool _passwordVisible;
        private bool _syncingPassword;

        public MainPage()
        {
            InitializeComponent();
            this.BackKeyPress += MainPage_BackKeyPress;

            ActiveTransfersList.ItemsSource = _activeTransfers;
            CompletedTransfersList.ItemsSource = _completedTransfers;
            _activeTransfers.CollectionChanged += (s, e) => UpdateTransfersSections();
            _completedTransfers.CollectionChanged += (s, e) => UpdateTransfersSections();
            LoadTransferHistory();
            UpdateTransfersSections();

            _directoryCollapseTimer.Interval = TimeSpan.FromSeconds(6);
            _directoryCollapseTimer.Tick += DirectoryCollapseTimer_Tick;
        }

        private void OpenCloudFromTransfers_Click(object sender, RoutedEventArgs e)
        {
            MainPivot.SelectedIndex = 0;
        }

        private void UpdateTransfersSections()
        {
            if (ActiveTransfersSection == null)
            {
                return;
            }

            bool hasActive = _activeTransfers.Count > 0;
            bool hasCompleted = _completedTransfers.Count > 0;

            ActiveTransfersSection.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
            CompletedTransfersSection.Visibility = hasCompleted ? Visibility.Visible : Visibility.Collapsed;
            TransfersEmptyState.Visibility = (!hasActive && !hasCompleted)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ClearCompletedButton.Visibility = hasCompleted ? Visibility.Visible : Visibility.Collapsed;

            ActiveTransfersTitle.Text = "АКТИВНЫЕ · " + _activeTransfers.Count;
            CompletedTransfersTitle.Text = "ЗАВЕРШЁННЫЕ · " + _completedTransfers.Count;
        }

        private void MainPage_BackKeyPress(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_dialogOpen)
            {
                e.Cancel = true;
                HideNothingDialog(false);
                return;
            }

            if (_accountMenuOpen)
            {
                e.Cancel = true;
                HideAccountMenu();
                return;
            }

            if (_transferIslandOpen)
            {
                e.Cancel = true;
                HideTransferIsland();
                return;
            }

            if (_selectedIslandOpen)
            {
                e.Cancel = true;
                HideSelectedIsland();
                return;
            }

            if (AuthArea.Visibility == Visibility.Visible)
            {
                return;
            }

            if (currentPath != "/")
            {
                e.Cancel = true;
                LoadFolder(GetParentPath(currentPath));
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            SystemTray.IsVisible = false;
            LoadDiagnostics();

            if (!_initialized)
            {
                _initialized = true;
                LoadCredentials();
            }

            HandlePickerContinuations();
        }

        public void HandlePickerContinuations()
        {
            if (!_initialized)
            {
                return;
            }

            var app = Application.Current as App;
            if (app == null)
            {
                return;
            }

            if (app.FileSaveContinuationArgs != null)
            {
                var args = app.FileSaveContinuationArgs;
                app.FileSaveContinuationArgs = null;

                if (!string.IsNullOrEmpty(_login) && !string.IsNullOrEmpty(_password))
                {
                    ContinueFileSavePicker(args);
                }
                else
                {
                    _pendingDownloadFile = null;
                    ClearPendingDownload();
                }
            }

            if (app.FileOpenContinuationArgs != null)
            {
                var openArgs = app.FileOpenContinuationArgs;
                app.FileOpenContinuationArgs = null;

                if (!string.IsNullOrEmpty(_login) && !string.IsNullOrEmpty(_password))
                {
                    ContinueFileOpenPicker(openArgs);
                }
            }
        }

        private void LoadDiagnostics()
        {
            string text = App.ReadDiagnosticsTail(1200);
            DiagnosticsText.Text = string.IsNullOrEmpty(text.Trim())
                ? "Записей нет."
                : text;
        }

        private void MainPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MainPivot.SelectedIndex == 2)
            {
                LoadDiagnostics();
            }

            if (MainPivot.SelectedIndex != 1 && _transferIslandOpen)
            {
                HideTransferIslandImmediate();
            }

            UpdateBottomIslandsVisibility();
        }

        private void LoadCredentials()
        {
            var settings = IsolatedStorageSettings.ApplicationSettings;

            string savedPath = settings.Contains(CurrentPathKey) ? settings[CurrentPathKey] as string : null;
            currentPath = (!string.IsNullOrEmpty(savedPath) && savedPath.StartsWith("/")) ? savedPath : "/";

            RestorePendingDownload(settings);

            if (settings.Contains(LoginKey) && settings.Contains(PasswordKey))
            {
                _login = settings[LoginKey] as string;
                _password = settings[PasswordKey] as string;
                if (!string.IsNullOrEmpty(_login) && !string.IsNullOrEmpty(_password))
                {
                    ShowMainInterface();
                    return;
                }
            }

            ShowAuth();
        }

        private void RestorePendingDownload(IsolatedStorageSettings settings)
        {
            string name = settings.Contains(PendingDownloadNameKey) ? settings[PendingDownloadNameKey] as string : null;
            string href = settings.Contains(PendingDownloadHrefKey) ? settings[PendingDownloadHrefKey] as string : null;
            bool isFolder = settings.Contains(PendingDownloadIsFolderKey) &&
                            settings[PendingDownloadIsFolderKey] is bool &&
                            (bool)settings[PendingDownloadIsFolderKey];

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(href))
            {
                _pendingDownloadFile = new YandexFileItem
                {
                    Name = name,
                    Href = href,
                    IsFolder = isFolder,
                    Info = ""
                };
            }
        }

        private static void SaveSetting(string key, object value)
        {
            var settings = IsolatedStorageSettings.ApplicationSettings;
            settings[key] = value;
            settings.Save();
        }

        private static void SavePendingDownload(YandexFileItem file)
        {
            var settings = IsolatedStorageSettings.ApplicationSettings;
            settings[PendingDownloadNameKey] = file.Name;
            settings[PendingDownloadHrefKey] = file.Href;
            settings[PendingDownloadIsFolderKey] = file.IsFolder;
            settings.Save();
        }

        private static void ClearPendingDownload()
        {
            var settings = IsolatedStorageSettings.ApplicationSettings;
            settings.Remove(PendingDownloadNameKey);
            settings.Remove(PendingDownloadHrefKey);
            settings.Remove(PendingDownloadIsFolderKey);
            settings.Save();
        }

        private void ShowAuth()
        {
            AuthArea.Visibility = Visibility.Visible;
            MainPivot.Visibility = Visibility.Collapsed;
            ResetBottomIslands();
            HideTransferIslandImmediate();

            _syncingPassword = true;
            LoginTextBox.Text = _login ?? "";
            PasswordBox.Password = _password ?? "";
            PasswordTextBox.Text = _password ?? "";
            _syncingPassword = false;
            ApplyPasswordVisibility(false);

            SetAuthStatus("ВВЕДИТЕ ЛОГИН И ПАРОЛЬ ПРИЛОЖЕНИЯ", false);
            AuthProgress.Visibility = Visibility.Collapsed;
            LoginButton.IsEnabled = true;
            LoginButtonLabel.Text = "ВОЙТИ";
        }

        private void ApplyPasswordVisibility(bool visible)
        {
            _syncingPassword = true;
            if (visible)
            {
                PasswordTextBox.Text = PasswordBox.Password;
            }
            else
            {
                PasswordBox.Password = PasswordTextBox.Text;
            }
            _syncingPassword = false;

            _passwordVisible = visible;
            PasswordBox.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            PasswordTextBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            PasswordRevealLabel.Text = visible ? "СКРЫТЬ" : "ПОКАЗАТЬ";
        }

        private string GetEnteredPassword()
        {
            return _passwordVisible ? PasswordTextBox.Text : PasswordBox.Password;
        }

        private void SetAuthStatus(string text, bool isError)
        {
            StatusText.Text = text;
            StatusText.Foreground = isError
                ? (Brush)Application.Current.Resources["NothingRedBrush"]
                : (Brush)Application.Current.Resources["NothingMutedDarkBrush"];
        }

        private void PasswordRevealButton_Click(object sender, RoutedEventArgs e)
        {
            bool show = !_passwordVisible;
            ApplyPasswordVisibility(show);

            if (show)
            {
                PasswordTextBox.Focus();
                PasswordTextBox.SelectionStart = PasswordTextBox.Text.Length;
            }
            else
            {
                PasswordBox.Focus();
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPassword || !_passwordVisible)
            {
                return;
            }

            _syncingPassword = true;
            PasswordTextBox.Text = PasswordBox.Password;
            _syncingPassword = false;
        }

        private void PasswordTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingPassword || !_passwordVisible)
            {
                return;
            }

            _syncingPassword = true;
            PasswordBox.Password = PasswordTextBox.Text;
            _syncingPassword = false;
        }

        private void LoginTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                TriggerLogin();
            }
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                TriggerLogin();
            }
        }

        private void PasswordTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                TriggerLogin();
            }
        }

        private async void TriggerLogin()
        {
            if (!LoginButton.IsEnabled)
            {
                return;
            }

            await SubmitLoginAsync();
        }

        private void OpenAppPasswordHelp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var task = new WebBrowserTask();
                task.Uri = new Uri("https://id.yandex.ru/security/app-passwords");
                task.Show();
            }
            catch (Exception ex)
            {
                App.Log("auth help failed: " + ex.Message);
                SetAuthStatus("НЕ УДАЛОСЬ ОТКРЫТЬ БРАУЗЕР.", true);
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            await SubmitLoginAsync();
        }

        private async Task SubmitLoginAsync()
        {
            if (!LoginButton.IsEnabled)
            {
                return;
            }

            string login = LoginTextBox.Text.Trim();
            string password = GetEnteredPassword();

            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
            {
                SetAuthStatus("ЗАПОЛНИТЕ ЛОГИН И ПАРОЛЬ.", true);
                return;
            }

            LoginButton.IsEnabled = false;
            LoginButtonLabel.Text = "ПРОВЕРКА...";
            AuthProgress.Visibility = Visibility.Visible;
            SetAuthStatus("ПРОВЕРКА ПОДКЛЮЧЕНИЯ...", false);

            bool? ok = await TestCredentialsAsync(login, password);

            AuthProgress.Visibility = Visibility.Collapsed;
            LoginButton.IsEnabled = true;
            LoginButtonLabel.Text = "ВОЙТИ";

            if (ok == true)
            {
                _login = login;
                _password = password;
                SaveCredentials();
                ApplyPasswordVisibility(false);
                ShowMainInterface();
            }
            else if (ok == false)
            {
                SetAuthStatus("НЕВЕРНЫЙ ЛОГИН ИЛИ ПАРОЛЬ ПРИЛОЖЕНИЯ.", true);
                if (_passwordVisible)
                {
                    PasswordTextBox.Focus();
                }
                else
                {
                    PasswordBox.Focus();
                }
            }
        }

        private async Task<bool?> TestCredentialsAsync(string login, string password)
        {
            try
            {
                var client = new YandexWebDavClient(login, password);
                await client.TestConnectionAsync(CancellationToken.None);
                return true;
            }
            catch (WebDavException ex)
            {
                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return false;
                }

                SetAuthStatus("ОШИБКА СЕТИ: " + ex.Message, true);
                return null;
            }
            catch (Exception ex)
            {
                SetAuthStatus("ОШИБКА: " + ex.Message, true);
                return null;
            }
        }

        private void SaveCredentials()
        {
            var settings = IsolatedStorageSettings.ApplicationSettings;
            settings[LoginKey] = _login;
            settings[PasswordKey] = _password;
            settings.Save();
        }

        private void ClearCredentials()
        {
            CancelFolderRequest();
            CancelQuotaRequest();
            _webDav = null;

            var settings = IsolatedStorageSettings.ApplicationSettings;
            settings.Remove(LoginKey);
            settings.Remove(PasswordKey);
            settings.Save();
            _login = null;
            _password = null;
        }

        private void HandleUnauthorized()
        {
            if (AuthArea.Visibility == Visibility.Visible)
            {
                return;
            }

            string lastLogin = _login;
            CloseOverlaysImmediate();
            ClearCredentials();
            _quota = null;
            _quotaFailed = false;
            UpdateAccountMenu();
            _pendingDownloadFile = null;
            ClearPendingDownload();
            CancelAndClearActiveTransfers();
            HideTransferIslandImmediate();
            FilesListBox.ItemsSource = null;
            MainPivot.Visibility = Visibility.Collapsed;
            ShowAuth();
            LoginTextBox.Text = lastLogin ?? "";
            SetAuthStatus("ПАРОЛЬ НЕ ПОДОШЁЛ ИЛИ СЕССИЯ ИСТЕКЛА. ВОЙДИТЕ СНОВА.", true);
        }

        private void AccountBlock_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            ShowAccountMenu();
        }

        private void ShowAccountMenu()
        {
            if (_accountMenuOpen || _dialogOpen)
            {
                return;
            }

            _accountMenuOpen = true;
            SetModalBackgroundLocked(true);
            UpdateAccountMenu();

            if (_quota == null && _webDav != null)
            {
                LoadQuotaAsync();
            }

            NothingDialogCard.Visibility = Visibility.Collapsed;
            AccountMenuCard.Visibility = Visibility.Visible;
            AccountMenuCard.IsHitTestVisible = true;
            SetTranslate(AccountMenuCard, 0, 24);

            ModalOverlay.Opacity = 0;
            ModalOverlay.Visibility = Visibility.Visible;

            Storyboard storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, ModalOverlay, 0, 1, 160);
            AddTranslateYAnimation(storyboard, AccountMenuCard, 0, 200);
            storyboard.Begin();
        }

        private void HideAccountMenu()
        {
            if (!_accountMenuOpen || _dialogOpen)
            {
                return;
            }

            _accountMenuOpen = false;
            AccountMenuCard.IsHitTestVisible = false;
            SetTranslate(AccountMenuCard, 0, 0);

            double opacity = ModalOverlay.Opacity;
            Storyboard storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, ModalOverlay, opacity, 0, 140);
            AddTranslateYAnimation(storyboard, AccountMenuCard, 20, 160);
            storyboard.Completed += AccountMenuClose_Completed;
            storyboard.Begin();
        }

        private void AccountMenuClose_Completed(object sender, EventArgs e)
        {
            if (_accountMenuOpen || _dialogOpen)
            {
                return;
            }

            ModalOverlay.Visibility = Visibility.Collapsed;
            SetModalBackgroundLocked(false);
            ResetModalPositions();
        }

        private void ModalOverlay_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            if (_suppressBackdropTap)
            {
                return;
            }

            if (_dialogOpen)
            {
                HideNothingDialog(false);
                return;
            }

            HideAccountMenu();
        }

        private void AccountMenuCard_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            e.Handled = true;
        }

        private void AccountMenuClose_Click(object sender, RoutedEventArgs e)
        {
            HideAccountMenu();
        }

        private void AccountMenuRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadQuotaAsync();
            LoadFolder(currentPath);
        }

        private void AccountMenuLogout_Click(object sender, RoutedEventArgs e)
        {
            App.Log("modal: logout click, accountOpen=" + _accountMenuOpen + ", dialogOpen=" + _dialogOpen);
            ShowNothingDialog(
                "ВЫЙТИ ИЗ АККАУНТА?",
                "Текущий аккаунт будет отключён на этом устройстве. Файлы на Яндекс.Диске останутся.",
                "ВЫЙТИ",
                PerformLogout,
                "ОТМЕНА",
                null,
                true);
        }

        private void UpdateAccountMenu()
        {
            AccountMenuLogin.Text = string.IsNullOrEmpty(_login) ? "Не указан" : _login;

            if (_quota == null)
            {
                _storagePercent = 0;
                AccountStorageTrack.Visibility = Visibility.Collapsed;
                AccountStorageText.Text = _quotaFailed ? "Недоступно" : "Загрузка...";
                UpdateStorageBarWidth();
                return;
            }

            if (_quota.HasAvailable && _quota.TotalBytes > 0)
            {
                double percent = _quota.UsedBytes * 100.0 / _quota.TotalBytes;
                _storagePercent = Math.Min(100.0, Math.Max(0.0, percent));
                AccountStorageTrack.Visibility = Visibility.Visible;
                AccountStorageText.Text = "Использовано " + YandexWebDavClient.FormatSize(_quota.UsedBytes) +
                                          " из " + YandexWebDavClient.FormatSize(_quota.TotalBytes);
            }
            else
            {
                _storagePercent = 0;
                AccountStorageTrack.Visibility = Visibility.Collapsed;
                AccountStorageText.Text = "Использовано " + YandexWebDavClient.FormatSize(_quota.UsedBytes);
            }

            UpdateStorageBarWidth();
        }

        private void AccountStorageTrack_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateStorageBarWidth();
        }

        private void UpdateStorageBarWidth()
        {
            if (AccountStorageTrack == null || AccountStorageFill == null || AccountStorageTrack.ActualWidth <= 0)
            {
                return;
            }

            AccountStorageFill.Width = AccountStorageTrack.ActualWidth * _storagePercent / 100.0;
        }

        private bool ShowNothingDialog(string title, string message, string primaryText, Action primaryAction,
                                       string secondaryText, Action secondaryAction, bool isDanger)
        {
            if (_dialogOpen)
            {
                return false;
            }

            _dialogOpen = true;
            _dialogInsideAccountMenu = _accountMenuOpen;
            _suppressBackdropTap = _dialogInsideAccountMenu;
            _dialogPrimaryAction = primaryAction;
            _dialogSecondaryAction = secondaryAction;
            _dialogInputOpen = false;
            _dialogInputAction = null;
            _directoryCollapseTimer.Stop();
            App.Log("modal: show dialog, fromAccount=" + _dialogInsideAccountMenu);

            NothingDialogTitle.Text = title;
            NothingDialogMessage.Text = message;
            NothingDialogPrimaryButton.Content = primaryText;
            NothingDialogPrimaryButton.Background = (SolidColorBrush)Application.Current.Resources[
                isDanger ? "NothingRedBrush" : "NothingInkBrush"];

            NothingDialogInputBorder.Visibility = Visibility.Collapsed;
            NothingDialogError.Visibility = Visibility.Collapsed;
            NothingDialogInput.Text = "";

            if (string.IsNullOrEmpty(secondaryText))
            {
                NothingDialogSecondaryButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                NothingDialogSecondaryButton.Visibility = Visibility.Visible;
                NothingDialogSecondaryButton.Content = secondaryText;
            }

            NothingDialogCard.Visibility = Visibility.Visible;
            NothingDialogCard.IsHitTestVisible = false;

            if (_dialogInsideAccountMenu)
            {
                double width = GetModalWidth();

                AccountMenuCard.Visibility = Visibility.Visible;
                AccountMenuCard.IsHitTestVisible = false;
                SetTranslate(AccountMenuCard, 0, 0);
                SetTranslate(NothingDialogCard, width, 0);

                App.Log("modal: swipe to dialog, width=" + width);

                Storyboard swap = new Storyboard();
                AddTranslateXAnimation(swap, AccountMenuCard, -width, 280);
                AddTranslateXAnimation(swap, NothingDialogCard, 0, 280);
                swap.Completed += (sender, e) =>
                {
                    App.Log("modal: swipe to dialog done, dialogOpen=" + _dialogOpen +
                            ", fromAccount=" + _dialogInsideAccountMenu);

                    _suppressBackdropTap = false;

                    if (_dialogOpen && _dialogInsideAccountMenu)
                    {
                        SetTranslate(AccountMenuCard, -width, 0);
                        SetTranslate(NothingDialogCard, 0, 0);
                        AccountMenuCard.Visibility = Visibility.Collapsed;
                        NothingDialogCard.Visibility = Visibility.Visible;
                        NothingDialogCard.IsHitTestVisible = true;
                    }
                };
                swap.Begin();
                return true;
            }

            AccountMenuCard.Visibility = Visibility.Collapsed;
            SetTranslate(NothingDialogCard, 0, 20);
            NothingDialogCard.IsHitTestVisible = true;

            if (ModalOverlay.Visibility != Visibility.Visible)
            {
                ModalOverlay.Opacity = 0;
                ModalOverlay.Visibility = Visibility.Visible;
                SetModalBackgroundLocked(true);
            }

            Storyboard storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, ModalOverlay, 0, 1, 140);
            AddTranslateYAnimation(storyboard, NothingDialogCard, 0, 180);
            storyboard.Begin();
            return true;
        }

        private void ShowInputDialog(string title, string message, string initialValue, string primaryText,
                                     Action<string> action, bool isDanger)
        {
            if (!ShowNothingDialog(title, message, primaryText, null, "ОТМЕНА", null, isDanger))
            {
                return;
            }

            _dialogInputOpen = true;
            _dialogInputAction = action;
            NothingDialogInput.Text = initialValue ?? "";
            NothingDialogInputBorder.Visibility = Visibility.Visible;
            NothingDialogError.Visibility = Visibility.Collapsed;

            Dispatcher.BeginInvoke(() =>
            {
                if (_dialogOpen && _dialogInputOpen && NothingDialogInput != null)
                {
                    NothingDialogInput.Focus();
                    NothingDialogInput.SelectAll();
                }
            });
        }

        private void HideNothingDialog(bool primary)
        {
            if (!_dialogOpen)
            {
                return;
            }

            _dialogOpen = false;
            _suppressBackdropTap = false;
            Action action = primary ? _dialogPrimaryAction : _dialogSecondaryAction;
            bool fromAccountMenu = _dialogInsideAccountMenu;
            _dialogInsideAccountMenu = false;
            _dialogPrimaryAction = null;
            _dialogSecondaryAction = null;
            _dialogInputOpen = false;
            _dialogInputAction = null;
            NothingDialogInput.Text = "";
            NothingDialogError.Visibility = Visibility.Collapsed;

            if (!primary && fromAccountMenu)
            {
                double width = GetModalWidth();

                AccountMenuCard.Visibility = Visibility.Visible;
                AccountMenuCard.IsHitTestVisible = false;
                SetTranslate(AccountMenuCard, -width, 0);
                NothingDialogCard.IsHitTestVisible = false;

                App.Log("modal: swipe back to account, width=" + width);

                Storyboard swap = new Storyboard();
                AddTranslateXAnimation(swap, NothingDialogCard, width, 280);
                AddTranslateXAnimation(swap, AccountMenuCard, 0, 280);
                swap.Completed += (sender, e) =>
                {
                    App.Log("modal: swipe back done, dialogOpen=" + _dialogOpen +
                            ", accountOpen=" + _accountMenuOpen);

                    if (_dialogOpen || !_accountMenuOpen)
                    {
                        return;
                    }

                    NothingDialogCard.Visibility = Visibility.Collapsed;
                    NothingDialogCard.IsHitTestVisible = true;
                    SetTranslate(NothingDialogCard, width, 0);
                    SetTranslate(AccountMenuCard, 0, 0);
                    AccountMenuCard.IsHitTestVisible = true;

                    if (action != null)
                    {
                        action();
                    }
                };
                swap.Begin();
                return;
            }

            if (fromAccountMenu)
            {
                _accountMenuOpen = false;
            }

            AccountMenuCard.IsHitTestVisible = false;
            NothingDialogCard.IsHitTestVisible = false;

            double opacity = ModalOverlay.Opacity;
            Storyboard storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, ModalOverlay, opacity, 0, 140);
            storyboard.Completed += (sender, e) =>
            {
                if (_dialogOpen || _accountMenuOpen)
                {
                    return;
                }

                ModalOverlay.Visibility = Visibility.Collapsed;
                SetModalBackgroundLocked(false);
                ResetModalPositions();
                RestartDirectoryCollapseTimer();

                if (action != null)
                {
                    action();
                }
            };
            storyboard.Begin();
        }

        private void NothingDialogCard_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            e.Handled = true;
        }

        private void NothingDialogPrimary_Click(object sender, RoutedEventArgs e)
        {
            if (_dialogInputOpen)
            {
                string value = (NothingDialogInput.Text ?? "").Trim();
                string error = ValidateName(value);
                if (error != null)
                {
                    NothingDialogError.Text = error;
                    NothingDialogError.Visibility = Visibility.Visible;
                    NothingDialogInput.Focus();
                    return;
                }

                Action<string> inputAction = _dialogInputAction;
                _dialogInputAction = null;
                if (inputAction != null)
                {
                    _dialogPrimaryAction = delegate { inputAction(value); };
                }
            }

            HideNothingDialog(true);
        }

        private void NothingDialogInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                NothingDialogPrimary_Click(sender, new RoutedEventArgs());
            }
        }

        private static string ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "Имя не может быть пустым.";
            }

            if (name == "." || name == "..")
            {
                return "Такое имя недопустимо.";
            }

            if (name.Length > 255)
            {
                return "Имя слишком длинное.";
            }

            if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                return "Имя содержит недопустимые символы: / \\ : * ? \" < > |";
            }

            if (name.EndsWith(".") || name.EndsWith(" "))
            {
                return "Имя не может заканчиваться точкой или пробелом.";
            }

            return null;
        }

        private void NothingDialogSecondary_Click(object sender, RoutedEventArgs e)
        {
            HideNothingDialog(false);
        }

        private static void SetTranslate(UIElement element, double x, double y)
        {
            element.RenderTransform = new TranslateTransform { X = x, Y = y };
        }

        private static void AddOpacityAnimation(Storyboard storyboard, DependencyObject target,
                                                double from, double to, int milliseconds)
        {
            DoubleAnimation animation = new DoubleAnimation();
            animation.From = from;
            animation.To = to;
            animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
            animation.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath("Opacity"));
            storyboard.Children.Add(animation);
        }

        private static void AddTranslateXAnimation(Storyboard storyboard, UIElement target, double to, int milliseconds)
        {
            DoubleAnimation animation = new DoubleAnimation();
            animation.To = to;
            animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
            animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
            storyboard.Children.Add(animation);
        }

        private static void AddTranslateYAnimation(Storyboard storyboard, UIElement target, double to, int milliseconds)
        {
            DoubleAnimation animation = new DoubleAnimation();
            animation.To = to;
            animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
            animation.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            storyboard.Children.Add(animation);
        }

        private double GetModalWidth()
        {
            return ModalOverlay.ActualWidth > 0 ? ModalOverlay.ActualWidth : 480.0;
        }

        private void SetModalBackgroundLocked(bool locked)
        {
            MainPivot.IsEnabled = !locked;
            MainPivot.IsHitTestVisible = !locked;
            BottomIslands.IsHitTestVisible = !locked;
            AuthArea.IsHitTestVisible = !locked;
        }

        private void ResetModalPositions()
        {
            double width = GetModalWidth();

            AccountMenuCard.Visibility = Visibility.Visible;
            AccountMenuCard.IsHitTestVisible = true;
            SetTranslate(AccountMenuCard, 0, 24);

            NothingDialogCard.Visibility = Visibility.Collapsed;
            NothingDialogCard.IsHitTestVisible = true;
            SetTranslate(NothingDialogCard, width, 0);
        }

        private void CloseOverlaysImmediate()
        {
            _accountMenuOpen = false;
            _dialogOpen = false;
            _dialogInsideAccountMenu = false;
            _suppressBackdropTap = false;
            _dialogPrimaryAction = null;
            _dialogSecondaryAction = null;
            _dialogInputOpen = false;
            _dialogInputAction = null;

            ModalOverlay.Visibility = Visibility.Collapsed;
            SetModalBackgroundLocked(false);
            ResetModalPositions();
        }

        private void PerformLogout()
        {
            CloseOverlaysImmediate();
            ClearCredentials();
            _pendingDownloadFile = null;
            ClearPendingDownload();
            ClearAllTransfers(true);
            HideTransferIslandImmediate();
            FilesListBox.ItemsSource = null;
            currentPath = "/";
            SaveSetting(CurrentPathKey, "/");
            _quota = null;
            _quotaFailed = false;
            UpdateAccountMenu();
            MainPivot.Visibility = Visibility.Collapsed;
            ShowAuth();
        }

        private void ShowMainInterface()
        {
            this.Focus();
            AuthArea.Visibility = Visibility.Collapsed;
            MainPivot.Visibility = Visibility.Visible;
            CurrentAccountText.Text = _login;
            UpdateAccountMenu();
            _webDav = new YandexWebDavClient(_login, _password);

            selectedItem = null;
            _selectedIslandOpen = false;
            _directoryExpanded = false;
            UpdateBottomIslandsVisibility();

            LoadFolder(currentPath);
            LoadQuotaAsync();
        }

        private string GetParentPath(string path)
        {
            string p = path.TrimEnd('/');
            int idx = p.LastIndexOf('/');
            return idx <= 0 ? "/" : p.Substring(0, idx + 1);
        }

        private void CancelFolderRequest()
        {
            _folderRequestId++;

            if (_folderCancellation != null)
            {
                _folderCancellation.Cancel();
                _folderCancellation = null;
            }
        }

        private void CancelQuotaRequest()
        {
            if (_quotaCancellation != null)
            {
                _quotaCancellation.Cancel();
                _quotaCancellation = null;
            }
        }

        private async void LoadFolder(string path)
        {
            if (_webDav == null || string.IsNullOrEmpty(_login) || string.IsNullOrEmpty(_password))
            {
                ShowAuth();
                return;
            }

            CancelFolderRequest();
            _folderCancellation = new CancellationTokenSource();
            CancellationToken token = _folderCancellation.Token;
            int requestId = _folderRequestId;

            currentPath = path;
            DirectoryRenameButton.Visibility = path == "/" ? Visibility.Collapsed : Visibility.Visible;
            string pretty = Uri.UnescapeDataString(path).Trim('/');
            string prettyPath = pretty == "" ? "диск /" : "диск / " + pretty.Replace("/", " / ");
            CurrentPathText.Text = pretty == "" ? "диск / ЗАГРУЗКА..." : "диск / " + pretty + " ЗАГРУЗКА...";
            DirectoryPathText.Text = prettyPath;

            if (_selectedIslandOpen)
            {
                HideSelectedIslandImmediate();
            }
            ApplyBottomIslandState();
            UpdateBottomIslandsPadding();
            RestartDirectoryCollapseTimer();

            try
            {
                List<YandexFileItem> files = await _webDav.GetFolderAsync(path, token);
                if (requestId != _folderRequestId)
                {
                    return;
                }

                if (path != "/")
                {
                    files.Insert(0, new YandexFileItem
                    {
                        Name = "..",
                        Info = "подняться на уровень вверх",
                        Href = GetParentPath(path),
                        IconGlyph = "\uE24A",
                        IsFolder = true
                    });
                }

                FilesListBox.ItemsSource = files;
                SaveSetting(CurrentPathKey, path);

                int itemCount = files.Count - (path != "/" ? 1 : 0);
                CurrentPathText.Text = pretty == ""
                    ? "диск / (" + itemCount + " элементов)"
                    : "диск / " + pretty + " (" + itemCount + " элементов)";
                DirectoryPathText.Text = prettyPath;
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebDavException ex)
            {
                if (requestId != _folderRequestId)
                {
                    return;
                }

                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    HandleUnauthorized();
                    return;
                }

                if (ex.StatusCode == HttpStatusCode.NotFound && path != "/")
                {
                    ShowInfoDialog("ПАПКА НЕ НАЙДЕНА", "Открываю корень Диска.");
                    LoadFolder("/");
                    return;
                }

                CurrentPathText.Text = "диск / ОШИБКА";
                ShowInfoDialog("СБОЙ", "Не удалось открыть папку:\n" + ex.Message);
            }
            catch (Exception ex)
            {
                if (requestId != _folderRequestId)
                {
                    return;
                }

                CurrentPathText.Text = "диск / ОШИБКА";
                ShowInfoDialog("СБОЙ", "Ошибка при открытии папки:\n" + ex.Message);
            }
        }

        private async void LoadQuotaAsync()
        {
            var client = _webDav;
            if (client == null)
            {
                return;
            }

            CancelQuotaRequest();
            _quotaCancellation = new CancellationTokenSource();
            CancellationToken token = _quotaCancellation.Token;
            _quotaFailed = false;
            UpdateAccountMenu();

            try
            {
                QuotaInfo quota = await client.GetQuotaAsync(token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _quota = quota;
                _quotaFailed = quota == null;
                UpdateAccountMenu();

                if (quota == null)
                {
                    StorageText.Text = "Недоступно";
                }
                else if (quota.HasAvailable)
                {
                    StorageText.Text = YandexWebDavClient.FormatSize(quota.UsedBytes) + " из " +
                                       YandexWebDavClient.FormatSize(quota.TotalBytes);
                }
                else
                {
                    StorageText.Text = "Использовано " + YandexWebDavClient.FormatSize(quota.UsedBytes);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                if (!token.IsCancellationRequested)
                {
                    _quota = null;
                    _quotaFailed = true;
                    UpdateAccountMenu();
                    StorageText.Text = "Недоступно";
                }
            }
        }

        private void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FilesListBox.SelectedIndex != -1)
            {
                FilesListBox.SelectedIndex = -1;
            }
        }

        private void FileRow_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            if (DateTime.UtcNow < _suppressTapUntil)
            {
                e.Handled = true;
                return;
            }

            var element = sender as FrameworkElement;
            YandexFileItem item = element != null ? element.DataContext as YandexFileItem : null;
            if (item == null)
            {
                return;
            }

            e.Handled = true;

            if (item.Name == ".." || item.IsFolder)
            {
                LoadFolder(item.Href);
                return;
            }

            if (_selectedIslandOpen && item == selectedItem)
            {
                HideSelectedIsland();
                return;
            }

            ShowSelectedIsland(item);
        }

        private void FilesListBox_Hold(object sender, System.Windows.Input.GestureEventArgs e)
        {
            try
            {
                YandexFileItem item = GetGestureItem(e);
                if (item == null || item.Name == "..")
                {
                    return;
                }

                _suppressTapUntil = DateTime.UtcNow.AddMilliseconds(800);
                e.Handled = true;
                ShowSelectedIsland(item);
            }
            catch
            {
            }
        }

        private static YandexFileItem GetGestureItem(System.Windows.Input.GestureEventArgs e)
        {
            var element = e.OriginalSource as FrameworkElement;
            return element != null ? element.DataContext as YandexFileItem : null;
        }

        private void ShowSelectedIsland(YandexFileItem item)
        {
            selectedItem = item;
            _selectedIslandOpen = true;
            _selectedIslandVersion++;
            int version = _selectedIslandVersion;

            SelectedIslandTitle.Text = item.Name;
            SelectedIslandSubtitle.Text = item.IsFolder
                ? "ПАПКА"
                : ("ФАЙЛ" + (string.IsNullOrEmpty(item.Info) ? "" : " · " + item.Info.ToUpperInvariant()));
            SelectedIslandTypeIcon.Text = string.IsNullOrEmpty(item.IconGlyph)
                ? (item.IsFolder ? "\uE24A" : "\uE230")
                : item.IconGlyph;

            FileActionsGrid.Visibility = item.IsFolder ? Visibility.Collapsed : Visibility.Visible;
            FolderActionsGrid.Visibility = item.IsFolder ? Visibility.Visible : Visibility.Collapsed;

            HideDirectoryIslandsForSelection();
            ApplyDimming(item);

            SelectedIsland.Visibility = Visibility.Visible;
            SelectedIsland.IsHitTestVisible = true;
            SelectedIsland.Opacity = 0;
            SetTranslate(SelectedIsland, 0, 14);

            Storyboard storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, SelectedIsland, 0, 1, 160);
            AddTranslateYAnimation(storyboard, SelectedIsland, 0, 180);
            storyboard.Completed += (s, e) =>
            {
                if (_selectedIslandOpen && version == _selectedIslandVersion)
                {
                    SetTranslate(SelectedIsland, 0, 0);
                }
            };
            storyboard.Begin();

            _directoryCollapseTimer.Stop();
            UpdateBottomIslandsPadding();
            ScrollSelectedItemIntoView(item);
        }

        private void ScrollSelectedItemIntoView(YandexFileItem item)
        {
            if (item == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (!_selectedIslandOpen || selectedItem != item)
                {
                    return;
                }

                FilesListBox.UpdateLayout();
                FilesListBox.ScrollIntoView(item);
            });
        }

        private void HideSelectedIsland()
        {
            if (!_selectedIslandOpen)
            {
                return;
            }

            _selectedIslandOpen = false;
            _selectedIslandVersion++;
            int version = _selectedIslandVersion;
            SelectedIsland.IsHitTestVisible = false;

            Storyboard storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, SelectedIsland, SelectedIsland.Opacity, 0, 130);
            AddTranslateYAnimation(storyboard, SelectedIsland, 14, 130);
            storyboard.Completed += (s, e) =>
            {
                if (_selectedIslandOpen || version != _selectedIslandVersion)
                {
                    return;
                }

                SelectedIsland.Visibility = Visibility.Collapsed;
                SelectedIsland.IsHitTestVisible = true;
                selectedItem = null;
                ClearDimming();
                ApplyBottomIslandState();
                UpdateBottomIslandsPadding();
            };
            storyboard.Begin();
        }

        private void HideSelectedIslandImmediate()
        {
            _selectedIslandOpen = false;
            _selectedIslandVersion++;
            SelectedIsland.Visibility = Visibility.Collapsed;
            SelectedIsland.IsHitTestVisible = true;
            selectedItem = null;
            ClearDimming();
            ApplyBottomIslandState();
            UpdateBottomIslandsPadding();
        }

        private void ApplyDimming(YandexFileItem selected)
        {
            var items = FilesListBox.ItemsSource as IEnumerable<YandexFileItem>;
            if (items == null)
            {
                return;
            }

            Brush selectedBackground = (Brush)Application.Current.Resources["NothingWhiteBrush"];
            Brush selectedName = (Brush)Application.Current.Resources["NothingInkBrush"];
            Brush selectedInfo = (Brush)Application.Current.Resources["NothingMutedLightBrush"];
            Brush selectedBorder = new SolidColorBrush(Colors.Transparent);

            foreach (YandexFileItem item in items)
            {
                if (item == selected)
                {
                    item.DimOpacity = 1.0;
                    item.RowBackground = selectedBackground;
                    item.RowBorderBrush = selectedBorder;
                    item.RowNameBrush = selectedName;
                    item.RowInfoBrush = selectedInfo;
                }
                else
                {
                    item.DimOpacity = 0.32;
                    item.ResetRowStyle();
                }
            }
        }

        private void ClearDimming()
        {
            var items = FilesListBox.ItemsSource as IEnumerable<YandexFileItem>;
            if (items == null)
            {
                return;
            }

            foreach (YandexFileItem item in items)
            {
                item.DimOpacity = 1.0;
                item.ResetRowStyle();
            }
        }

        private void UpdateBottomIslandsVisibility()
        {
            if (BottomIslands == null || FilesListBox == null)
            {
                return;
            }

            bool cloudTab = MainPivot.Visibility == Visibility.Visible && MainPivot.SelectedIndex == 0;
            bool transfersTab = MainPivot.Visibility == Visibility.Visible && MainPivot.SelectedIndex == 1;
            bool visible = cloudTab || (transfersTab && _transferIslandOpen);
            if (!visible)
            {
                _directoryCollapseTimer.Stop();
                BottomIslands.Visibility = Visibility.Collapsed;
                UpdateBottomIslandsPadding();
                UpdateTransfersBottomPadding();
                return;
            }

            if (!cloudTab)
            {
                _directoryCollapseTimer.Stop();
            }

            BottomIslands.Visibility = Visibility.Visible;
            ApplyBottomIslandState();
            UpdateBottomIslandsPadding();
            UpdateTransfersBottomPadding();
            if (cloudTab)
            {
                RestartDirectoryCollapseTimer();
            }
        }

        private void ApplyBottomIslandState()
        {
            bool cloudTab = MainPivot.SelectedIndex == 0;
            bool transfersTab = MainPivot.SelectedIndex == 1;

            SelectedIsland.Visibility = (cloudTab && _selectedIslandOpen)
                ? Visibility.Visible
                : Visibility.Collapsed;

            bool showDirectory = cloudTab && !_selectedIslandOpen;
            DirectoryIsland.Visibility = (showDirectory && _directoryExpanded) ? Visibility.Visible : Visibility.Collapsed;
            DirectoryMicroIsland.Visibility = (showDirectory && !_directoryExpanded) ? Visibility.Visible : Visibility.Collapsed;

            TransferIsland.Visibility = (transfersTab && _transferIslandOpen)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ResetBottomIslands()
        {
            _directoryCollapseTimer.Stop();
            _selectedIslandOpen = false;
            _selectedIslandVersion++;
            _directoryExpanded = false;
            selectedItem = null;

            BottomIslands.Visibility = Visibility.Collapsed;
            SelectedIsland.Visibility = Visibility.Collapsed;
            SelectedIsland.IsHitTestVisible = true;
            DirectoryIsland.Visibility = Visibility.Collapsed;
            DirectoryMicroIsland.Visibility = Visibility.Collapsed;
            TransferIsland.Visibility = Visibility.Collapsed;
            ClearDimming();
            UpdateBottomIslandsPadding();
            UpdateTransfersBottomPadding();
        }

        private void UpdateBottomIslandsPadding()
        {
            if (FilesListBox == null || BottomIslands == null)
            {
                return;
            }

            double bottom = 0;

            if (BottomIslands.Visibility == Visibility.Visible)
            {
                if (_selectedIslandOpen)
                {
                    bottom = 180;
                }
                else
                {
                    bottom = _directoryExpanded ? (currentPath == "/" ? 152 : 208) : 68;
                }
            }

            FilesListBox.Margin = new Thickness(0, 0, 0, bottom);
        }

        private void UpdateTransfersBottomPadding()
        {
            if (TransfersScrollViewer == null)
            {
                return;
            }

            double bottom = (_transferIslandOpen && MainPivot.SelectedIndex == 1) ? 180 : 0;
            TransfersScrollViewer.Margin = new Thickness(0, 0, 0, bottom);
        }

        private void ShowDirectoryIsland()
        {
            if (_selectedIslandOpen)
            {
                return;
            }

            bool wasVisible = DirectoryIsland.Visibility == Visibility.Visible;
            _directoryExpanded = true;

            DirectoryMicroIsland.Visibility = Visibility.Collapsed;
            DirectoryIsland.Visibility = Visibility.Visible;

            if (!wasVisible && BottomIslands.Visibility == Visibility.Visible)
            {
                DirectoryIsland.Opacity = 0;
                SetTranslate(DirectoryIsland, 0, 12);

                Storyboard storyboard = new Storyboard();
                AddOpacityAnimation(storyboard, DirectoryIsland, 0, 1, 150);
                AddTranslateYAnimation(storyboard, DirectoryIsland, 0, 170);
                storyboard.Begin();
            }
            else
            {
                DirectoryIsland.Opacity = 1;
                SetTranslate(DirectoryIsland, 0, 0);
            }

            UpdateBottomIslandsPadding();
            RestartDirectoryCollapseTimer();
        }

        private void CollapseDirectoryIsland()
        {
            if (!_directoryExpanded)
            {
                return;
            }

            _directoryExpanded = false;
            _directoryCollapseTimer.Stop();

            DirectoryIsland.Visibility = Visibility.Collapsed;
            DirectoryMicroIsland.Visibility = Visibility.Visible;
            DirectoryMicroIsland.Opacity = 0;

            Storyboard storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, DirectoryMicroIsland, 0, 1, 150);
            storyboard.Begin();

            UpdateBottomIslandsPadding();
        }

        private void HideDirectoryIslandsForSelection()
        {
            _directoryExpanded = false;
            _directoryCollapseTimer.Stop();
            DirectoryIsland.Visibility = Visibility.Collapsed;
            DirectoryMicroIsland.Visibility = Visibility.Collapsed;
        }

        private void TransferRow_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            e.Handled = true;

            var element = sender as FrameworkElement;
            TransferItem item = element != null ? element.DataContext as TransferItem : null;
            if (item == null)
            {
                return;
            }

            ShowTransferIsland(item);
        }

        private void ShowTransferIsland(TransferItem transfer)
        {
            if (transfer == null)
            {
                return;
            }

            bool wasOpen = _transferIslandOpen;

            if (_transferIslandItem != null)
            {
                _transferIslandItem.PropertyChanged -= TransferIslandItem_PropertyChanged;
            }

            _transferIslandItem = transfer;
            _transferIslandItem.PropertyChanged += TransferIslandItem_PropertyChanged;
            _transferIslandOpen = true;

            UpdateTransferIslandContent();
            UpdateBottomIslandsVisibility();

            TransferIsland.IsHitTestVisible = true;

            if (!wasOpen)
            {
                TransferIsland.Opacity = 0;
                SetTranslate(TransferIsland, 0, 14);

                Storyboard storyboard = new Storyboard();
                AddOpacityAnimation(storyboard, TransferIsland, 0, 1, 150);
                AddTranslateYAnimation(storyboard, TransferIsland, 0, 170);
                storyboard.Begin();
            }
            else
            {
                TransferIsland.Opacity = 1;
                SetTranslate(TransferIsland, 0, 0);
            }
        }

        private void TransferIslandItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "State" || e.PropertyName == "StatusText" ||
                e.PropertyName == "ProgressText" || e.PropertyName == "IconColor")
            {
                UpdateTransferIslandContent();
            }
        }

        private void UpdateTransferIslandContent()
        {
            TransferItem item = _transferIslandItem;
            if (item == null)
            {
                return;
            }

            TransferIslandTitle.Text = item.FileName;
            TransferIslandSubtitle.Text = string.IsNullOrEmpty(item.TargetText)
                ? item.StatusText
                : item.TargetText + " · " + item.StatusText;
            TransferIslandIcon.Text = item.IconGlyph;

            bool running = item.IsRunning;
            bool canRetry = !running && item.CanRetry;

            TransferRunningActions.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            TransferRetryRemoveActions.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
            TransferRemoveActions.Visibility = (!running && !canRetry)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void TransferIslandClose_Click(object sender, RoutedEventArgs e)
        {
            HideTransferIsland();
        }

        private void HideTransferIsland()
        {
            if (!_transferIslandOpen)
            {
                return;
            }

            _transferIslandOpen = false;
            int version = ++_transferIslandVersion;

            if (_transferIslandItem != null)
            {
                _transferIslandItem.PropertyChanged -= TransferIslandItem_PropertyChanged;
                _transferIslandItem = null;
            }

            TransferIsland.IsHitTestVisible = false;

            Storyboard storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, TransferIsland, TransferIsland.Opacity, 0, 130);
            AddTranslateYAnimation(storyboard, TransferIsland, 14, 130);
            storyboard.Completed += (s, e) =>
            {
                if (_transferIslandOpen || version != _transferIslandVersion)
                {
                    return;
                }

                TransferIsland.Visibility = Visibility.Collapsed;
                TransferIsland.IsHitTestVisible = true;
                UpdateBottomIslandsVisibility();
            };
            storyboard.Begin();
        }

        private void HideTransferIslandImmediate()
        {
            _transferIslandOpen = false;
            _transferIslandVersion++;

            if (_transferIslandItem != null)
            {
                _transferIslandItem.PropertyChanged -= TransferIslandItem_PropertyChanged;
                _transferIslandItem = null;
            }

            TransferIsland.Visibility = Visibility.Collapsed;
            TransferIsland.IsHitTestVisible = true;
            UpdateBottomIslandsVisibility();
        }

        private void TransferCancelAction_Click(object sender, RoutedEventArgs e)
        {
            TransferItem item = _transferIslandItem;
            HideTransferIsland();

            if (item != null)
            {
                CancelTransfer(item);
            }
        }

        private void TransferRetryAction_Click(object sender, RoutedEventArgs e)
        {
            TransferItem item = _transferIslandItem;
            HideTransferIslandImmediate();

            if (item != null)
            {
                RetryTransfer(item);
            }
        }

        private void TransferRemoveAction_Click(object sender, RoutedEventArgs e)
        {
            TransferItem item = _transferIslandItem;
            HideTransferIslandImmediate();

            if (item != null)
            {
                RemoveTransfer(item);
            }
        }

        private void RestartDirectoryCollapseTimer()
        {
            _directoryCollapseTimer.Stop();

            if (BottomIslands.Visibility == Visibility.Visible && _directoryExpanded && !_selectedIslandOpen)
            {
                _directoryCollapseTimer.Start();
            }
        }

        private void DirectoryCollapseTimer_Tick(object sender, EventArgs e)
        {
            _directoryCollapseTimer.Stop();

            if (BottomIslands.Visibility == Visibility.Visible && _directoryExpanded && !_selectedIslandOpen)
            {
                CollapseDirectoryIsland();
            }
        }

        private void DirectoryCollapse_Click(object sender, RoutedEventArgs e)
        {
            CollapseDirectoryIsland();
        }

        private void DirectoryMicroIsland_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            e.Handled = true;
            ShowDirectoryIsland();
        }

        private void DirectoryRename_Click(object sender, RoutedEventArgs e)
        {
            RestartDirectoryCollapseTimer();

            if (_webDav == null || currentPath == "/")
            {
                return;
            }

            string trimmed = currentPath.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            string currentName = slash >= 0 ? Uri.UnescapeDataString(trimmed.Substring(slash + 1)) : "";

            ShowInputDialog(
                "ПЕРЕИМЕНОВАТЬ ПАПКУ",
                "Введите новое имя текущей папки.",
                currentName,
                "СОХРАНИТЬ",
                delegate(string name) { RenameCurrentFolderAsync(name); },
                false);
        }

        private async void RenameCurrentFolderAsync(string newName)
        {
            var client = _webDav;
            string oldPath = currentPath;
            if (client == null || string.IsNullOrEmpty(oldPath) || oldPath == "/")
            {
                return;
            }

            string trimmed = oldPath.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            string oldName = slash >= 0 ? Uri.UnescapeDataString(trimmed.Substring(slash + 1)) : "";
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return;
            }

            string parent = GetParentPath(oldPath);
            string newPath = parent.TrimEnd('/') + "/" + Uri.EscapeDataString(newName) + "/";

            try
            {
                App.Log("rename folder start: " + oldPath + " -> " + newPath);
                await client.RenameAsync(oldPath, newName, true, CancellationToken.None);
                LoadFolder(newPath);
                ShowInfoDialog("ГОТОВО", "Папка переименована.");
            }
            catch (WebDavException ex)
            {
                App.Log("rename folder failed: " + ex.Message);
                ShowInfoDialog("СБОЙ", "Не удалось переименовать папку:\n" + ex.Message);
            }
            catch (Exception ex)
            {
                App.Log("rename folder error: " + ex.Message);
                ShowInfoDialog("СБОЙ", "Ошибка при переименовании папки:\n" + ex.Message);
            }
        }

        private void SelectedIslandClose_Click(object sender, RoutedEventArgs e)
        {
            HideSelectedIsland();
        }

        private void SelectedOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            YandexFileItem item = selectedItem;
            if (item == null || !item.IsFolder)
            {
                return;
            }

            HideSelectedIslandImmediate();
            LoadFolder(item.Href);
        }

        private void SelectedDownload_Click(object sender, RoutedEventArgs e)
        {
            YandexFileItem item = selectedItem;
            if (item == null || item.IsFolder)
            {
                return;
            }

            HideSelectedIslandImmediate();
            DownloadFile(item);
        }

        private void SelectedDownloadAll_Click(object sender, RoutedEventArgs e)
        {
            YandexFileItem item = selectedItem;
            if (item == null || !item.IsFolder)
            {
                return;
            }

            HideSelectedIslandImmediate();

            string suggested = SanitizeFileName(item.Name);
            if (string.IsNullOrEmpty(suggested))
            {
                suggested = "folder";
            }

            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("ZIP-архив", new List<string>() { ".zip" });
            picker.SuggestedFileName = suggested;

            _pendingDownloadFile = item;
            SavePendingDownload(item);

            try
            {
                picker.PickSaveFileAndContinue();
            }
            catch (Exception ex)
            {
                _pendingDownloadFile = null;
                ClearPendingDownload();
                ShowInfoDialog("СБОЙ", "Не удалось открыть выбор файла:\n" + ex.Message);
            }
        }

        private void SelectedRename_Click(object sender, RoutedEventArgs e)
        {
            YandexFileItem item = selectedItem;
            if (item == null)
            {
                return;
            }

            ShowInputDialog(
                "ПЕРЕИМЕНОВАТЬ",
                item.IsFolder ? "Введите новое имя папки." : "Введите новое имя файла.",
                item.Name,
                "СОХРАНИТЬ",
                delegate(string name) { RenameSelectedAsync(item, name); },
                false);
        }

        private async void RenameSelectedAsync(YandexFileItem item, string newName)
        {
            var client = _webDav;
            if (client == null || item == null)
            {
                return;
            }

            if (string.Equals(item.Name, newName, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                App.Log("rename start: " + item.Name + " -> " + newName);
                await client.RenameAsync(item.Href, newName, item.IsFolder, CancellationToken.None);
                HideSelectedIslandImmediate();
                LoadFolder(currentPath);
                ShowInfoDialog("ГОТОВО", "Объект переименован.");
            }
            catch (WebDavException ex)
            {
                App.Log("rename failed: " + ex.Message);
                ShowInfoDialog("СБОЙ", "Не удалось переименовать объект:\n" + ex.Message);
            }
            catch (Exception ex)
            {
                App.Log("rename error: " + ex.Message);
                ShowInfoDialog("СБОЙ", "Ошибка при переименовании:\n" + ex.Message);
            }
        }

        private void SelectedDelete_Click(object sender, RoutedEventArgs e)
        {
            YandexFileItem item = selectedItem;
            if (item == null)
            {
                return;
            }

            ShowNothingDialog(
                item.IsFolder ? "УДАЛИТЬ ПАПКУ?" : "УДАЛИТЬ ФАЙЛ?",
                item.IsFolder
                    ? "Папка «" + item.Name + "» будет удалена с Диска."
                    : "Файл «" + item.Name + "» будет удалён с Диска.",
                "УДАЛИТЬ",
                delegate { DeleteSelectedAsync(item); },
                "ОТМЕНА",
                null,
                true);
        }

        private async void DeleteSelectedAsync(YandexFileItem item)
        {
            var client = _webDav;
            if (client == null || item == null)
            {
                return;
            }

            try
            {
                App.Log("delete start: " + item.Name);
                await client.DeleteAsync(item.Href, CancellationToken.None);
                HideSelectedIslandImmediate();
                LoadFolder(currentPath);
                LoadQuotaAsync();
                ShowInfoDialog("ГОТОВО", "Объект удалён с Диска.");
            }
            catch (WebDavException ex)
            {
                App.Log("delete failed: " + ex.Message);
                ShowInfoDialog("СБОЙ", "Не удалось удалить объект:\n" + ex.Message);
            }
            catch (Exception ex)
            {
                App.Log("delete error: " + ex.Message);
                ShowInfoDialog("СБОЙ", "Ошибка при удалении:\n" + ex.Message);
            }
        }

        private void SelectedProperties_Click(object sender, RoutedEventArgs e)
        {
            YandexFileItem item = selectedItem;
            if (item == null)
            {
                return;
            }

            string message = (item.IsFolder ? "Папка" : "Файл") + "\n" +
                             (string.IsNullOrEmpty(item.Info) ? "" : item.Info + "\n") +
                             "\nПуть:\n" + Uri.UnescapeDataString(item.Href);
            ShowNothingDialog(item.Name, message, "ПОНЯТНО", null, null, null, false);
        }

        private void UploadFile_Click(object sender, RoutedEventArgs e)
        {
            RestartDirectoryCollapseTimer();

            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");

            try
            {
                picker.PickSingleFileAndContinue();
            }
            catch (Exception ex)
            {
                ShowInfoDialog("СБОЙ", "Не удалось открыть выбор файла:\n" + ex.Message);
            }
        }

        public void ContinueFileOpenPicker(FileOpenPickerContinuationEventArgs args)
        {
            if (args.Files == null || args.Files.Count == 0)
            {
                return;
            }

            StartUpload(args.Files[0]);
        }

        private void StartUpload(StorageFile file)
        {
            var client = _webDav;
            if (client == null || file == null)
            {
                return;
            }

            string folderPath = currentPath;
            TransferItem transfer = CreateTransfer(
                file.Name,
                "Загрузка...",
                TransferKind.Upload,
                "НА ДИСК · " + (folderPath == "/" ? "корень" : folderPath));
            transfer.SourceStorageFile = file;
            transfer.UploadFolderPath = folderPath;

            AddActiveTransfer(transfer);
            RunUploadAsync(transfer, file);
        }

        private async void RunUploadAsync(TransferItem transfer, StorageFile file)
        {
            var client = _webDav;
            if (transfer == null || client == null || file == null)
            {
                FinishTransfer(transfer, TransferState.Failed, "Нет подключения");
                return;
            }

            CancellationToken token = transfer.Cancellation != null
                ? transfer.Cancellation.Token
                : CancellationToken.None;
            string folderPath = string.IsNullOrEmpty(transfer.UploadFolderPath)
                ? currentPath
                : transfer.UploadFolderPath;

            BeginDownloadActivity();
            App.Log("upload start: " + file.Name);

            try
            {
                await client.UploadFileAsync(folderPath, file, delegate(int percent)
                {
                    Deployment.Current.Dispatcher.BeginInvoke(() =>
                    {
                        if (transfer.IsRunning)
                        {
                            transfer.Progress = percent;
                        }
                    });
                }, token);

                token.ThrowIfCancellationRequested();

                transfer.Progress = 100;
                App.Log("upload done: " + file.Name);
                FinishTransfer(transfer, TransferState.Completed, "На Диске");

                if (MainPivot.Visibility == Visibility.Visible && currentPath == folderPath)
                {
                    LoadFolder(currentPath);
                    LoadQuotaAsync();
                }
            }
            catch (OperationCanceledException)
            {
                FinishTransfer(transfer, TransferState.Canceled, "Отменено");
            }
            catch (WebDavException ex)
            {
                App.Log("upload failed: " + ex.Message);
                FinishTransfer(transfer, TransferState.Failed, "Ошибка: " + ex.Message);

                if (IsUnauthorizedError(ex))
                {
                    HandleUnauthorized();
                }
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    FinishTransfer(transfer, TransferState.Canceled, "Отменено");
                }
                else
                {
                    App.Log("upload error: " + ex);
                    FinishTransfer(transfer, TransferState.Failed, "Ошибка загрузки");

                    if (IsUnauthorizedError(ex))
                    {
                        HandleUnauthorized();
                    }
                }
            }
            finally
            {
                EndDownloadActivity();
            }
        }

        private void CreateFolder_Click(object sender, RoutedEventArgs e)
        {
            RestartDirectoryCollapseTimer();

            ShowInputDialog(
                "НОВАЯ ПАПКА",
                "Введите имя новой папки на Диске.",
                "",
                "СОЗДАТЬ",
                delegate(string name) { CreateFolderAsync(name); },
                false);
        }

        private async void CreateFolderAsync(string name)
        {
            var client = _webDav;
            if (client == null)
            {
                return;
            }

            try
            {
                App.Log("mkdir start: " + name);
                await client.CreateFolderAsync(currentPath, name, CancellationToken.None);
                LoadFolder(currentPath);
                ShowInfoDialog("ГОТОВО", "Папка «" + name + "» создана.");
            }
            catch (WebDavException ex)
            {
                App.Log("mkdir failed: " + ex.Message);
                ShowInfoDialog("СБОЙ", "Не удалось создать папку:\n" + ex.Message);
            }
            catch (Exception ex)
            {
                App.Log("mkdir error: " + ex.Message);
                ShowInfoDialog("СБОЙ", "Ошибка при создании папки:\n" + ex.Message);
            }
        }

        private void RefreshFolder_Click(object sender, RoutedEventArgs e)
        {
            RestartDirectoryCollapseTimer();
            LoadFolder(currentPath);
            LoadQuotaAsync();
        }

        private void ShowInfoDialog(string title, string message)
        {
            ShowNothingDialog(title, message, "ПОНЯТНО", null, null, null, false);
        }

        private static bool IsUnauthorizedError(Exception ex)
        {
            var webDavException = ex as WebDavException;
            if (webDavException != null)
            {
                return webDavException.StatusCode == HttpStatusCode.Unauthorized;
            }

            var webException = ex as WebException;
            if (webException != null)
            {
                var response = webException.Response as HttpWebResponse;
                if (response != null)
                {
                    try
                    {
                        return response.StatusCode == HttpStatusCode.Unauthorized;
                    }
                    finally
                    {
                        response.Dispose();
                    }
                }
            }

            return false;
        }

        private static string GetTransferGlyph(TransferKind kind)
        {
            switch (kind)
            {
                case TransferKind.Upload:
                    return "\uE4C0";
                case TransferKind.FolderArchive:
                    return "\uEB2A";
                case TransferKind.Image:
                    return "\uEA24";
                default:
                    return "\uE20C";
            }
        }

        private static TransferItem CreateTransfer(string fileName, string status, TransferKind kind,
                                                   string targetText)
        {
            return new TransferItem
            {
                Id = Guid.NewGuid().ToString("N"),
                FileName = string.IsNullOrEmpty(fileName) ? "файл" : fileName,
                Progress = 0,
                StatusText = status,
                TargetText = targetText ?? "",
                Kind = kind,
                CreatedAt = DateTime.Now,
                IconGlyph = GetTransferGlyph(kind),
                IconColor = "#F6F6F4",
                State = TransferState.Running,
                Cancellation = new CancellationTokenSource()
            };
        }

        private void AddActiveTransfer(TransferItem transfer)
        {
            if (transfer == null)
            {
                return;
            }

            _activeTransfers.Insert(0, transfer);
            SaveTransferHistory();
            UpdateTransfersSections();
        }

        private void FinishTransfer(TransferItem transfer, TransferState state, string status)
        {
            if (transfer == null)
            {
                return;
            }

            transfer.State = state;
            transfer.StatusText = status;
            transfer.IconColor = state == TransferState.Failed
                ? "#D71920"
                : (state == TransferState.Canceled || state == TransferState.Interrupted
                    ? "#8A8A8A"
                    : "#F6F6F4");

            if (state == TransferState.Completed)
            {
                transfer.Progress = 100;
            }

            bool wasActive = _activeTransfers.Remove(transfer);
            if (wasActive)
            {
                _completedTransfers.Insert(0, transfer);
                while (_completedTransfers.Count > TransferHistoryLimit)
                {
                    _completedTransfers.RemoveAt(_completedTransfers.Count - 1);
                }
            }

            if (transfer.Cancellation != null)
            {
                try
                {
                    transfer.Cancellation.Dispose();
                }
                catch
                {
                }

                transfer.Cancellation = null;
            }

            SaveTransferHistory();
            UpdateTransfersSections();
        }

        private void CancelTransfer(TransferItem transfer)
        {
            if (transfer == null || !transfer.IsRunning)
            {
                return;
            }

            transfer.StatusText = "Остановка...";

            if (transfer.Cancellation == null)
            {
                FinishTransfer(transfer, TransferState.Canceled, "Отменено");
                return;
            }

            try
            {
                transfer.Cancellation.Cancel();
            }
            catch
            {
                FinishTransfer(transfer, TransferState.Canceled, "Отменено");
            }
        }

        private void RetryTransfer(TransferItem transfer)
        {
            if (transfer == null || !transfer.CanRetry)
            {
                return;
            }

            _completedTransfers.Remove(transfer);

            if (transfer.Cancellation != null)
            {
                try
                {
                    transfer.Cancellation.Dispose();
                }
                catch
                {
                }
            }

            transfer.Cancellation = new CancellationTokenSource();
            transfer.State = TransferState.Running;
            transfer.Progress = 0;
            transfer.IconColor = "#F6F6F4";

            switch (transfer.Kind)
            {
                case TransferKind.Upload:
                    transfer.StatusText = "Загрузка...";
                    break;
                case TransferKind.FolderArchive:
                    transfer.StatusText = "Подготовка архива...";
                    break;
                default:
                    transfer.StatusText = "Скачивание...";
                    break;
            }

            _activeTransfers.Insert(0, transfer);
            SaveTransferHistory();
            UpdateTransfersSections();

            switch (transfer.Kind)
            {
                case TransferKind.Upload:
                    RunUploadAsync(transfer, transfer.SourceStorageFile);
                    break;
                case TransferKind.FolderArchive:
                    StartFolderDownloadToStorageFile(transfer.SourceFile, transfer.TargetFile, transfer);
                    break;
                case TransferKind.Image:
                    RunImageDownloadAsync(transfer, transfer.SourceFile);
                    break;
                default:
                    StartDownloadToStorageFile(transfer.SourceFile, transfer.TargetFile, transfer);
                    break;
            }
        }

        private void RemoveTransfer(TransferItem transfer)
        {
            if (transfer == null || transfer.IsRunning)
            {
                return;
            }

            _completedTransfers.Remove(transfer);
            SaveTransferHistory();
            UpdateTransfersSections();
        }

        private void ClearCompletedTransfers_Click(object sender, RoutedEventArgs e)
        {
            if (_completedTransfers.Count == 0)
            {
                return;
            }

            ShowNothingDialog(
                "ОЧИСТИТЬ ИСТОРИЮ?",
                "Записи о завершённых передачах будут удалены. Сами файлы останутся на месте.",
                "ОЧИСТИТЬ",
                delegate { ClearCompletedTransfers(); },
                "ОТМЕНА",
                null,
                true);
        }

        private void ClearCompletedTransfers()
        {
            if (_transferIslandItem != null && _completedTransfers.Contains(_transferIslandItem))
            {
                HideTransferIslandImmediate();
            }

            _completedTransfers.Clear();
            SaveTransferHistory();
            UpdateTransfersSections();
        }

        private void CancelAndClearActiveTransfers()
        {
            for (int i = 0; i < _activeTransfers.Count; i++)
            {
                TransferItem transfer = _activeTransfers[i];
                transfer.State = TransferState.Canceled;
                transfer.StatusText = "Отменено";
                transfer.IconColor = "#8A8A8A";

                if (transfer.Cancellation != null)
                {
                    try
                    {
                        transfer.Cancellation.Cancel();
                    }
                    catch
                    {
                    }
                }
            }

            _activeTransfers.Clear();
            SaveTransferHistory();
            UpdateTransfersSections();
        }

        private void ClearAllTransfers(bool clearHistory)
        {
            CancelAndClearActiveTransfers();
            _completedTransfers.Clear();

            if (clearHistory)
            {
                var settings = IsolatedStorageSettings.ApplicationSettings;
                settings.Remove(TransferHistoryKey);
                settings.Save();
            }
            else
            {
                SaveTransferHistory();
            }

            UpdateTransfersSections();
        }

        private void SaveTransferHistory()
        {
            var settings = IsolatedStorageSettings.ApplicationSettings;
            StringBuilder builder = new StringBuilder();
            int count = 0;

            AppendTransferHistory(builder, _activeTransfers, ref count);
            AppendTransferHistory(builder, _completedTransfers, ref count);

            if (builder.Length == 0)
            {
                settings.Remove(TransferHistoryKey);
            }
            else
            {
                settings[TransferHistoryKey] = builder.ToString();
            }

            settings.Save();
        }

        private static void AppendTransferHistory(StringBuilder builder, IEnumerable<TransferItem> transfers,
                                                   ref int count)
        {
            foreach (TransferItem transfer in transfers)
            {
                if (count >= TransferHistoryLimit)
                {
                    return;
                }

                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(Uri.EscapeDataString(transfer.Id ?? ""));
                builder.Append('\t').Append((int)transfer.Kind);
                builder.Append('\t').Append((int)transfer.State);
                builder.Append('\t').Append(Uri.EscapeDataString(transfer.FileName ?? ""));
                builder.Append('\t').Append(Uri.EscapeDataString(transfer.StatusText ?? ""));
                builder.Append('\t').Append(Uri.EscapeDataString(transfer.TargetText ?? ""));
                builder.Append('\t').Append(transfer.Progress.ToString(CultureInfo.InvariantCulture));
                builder.Append('\t').Append(transfer.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture));
                builder.Append('\t').Append(Uri.EscapeDataString(transfer.IconGlyph ?? ""));
                builder.Append('\t').Append(Uri.EscapeDataString(transfer.IconColor ?? ""));
                count++;
            }
        }

        private void LoadTransferHistory()
        {
            var settings = IsolatedStorageSettings.ApplicationSettings;
            string raw = settings.Contains(TransferHistoryKey) ? settings[TransferHistoryKey] as string : null;
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }

            bool changed = false;
            string[] lines = raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 9)
                {
                    continue;
                }

                int kindValue;
                int stateValue;
                if (!int.TryParse(parts[1], out kindValue) || !int.TryParse(parts[2], out stateValue))
                {
                    continue;
                }

                TransferItem transfer = new TransferItem();
                transfer.Id = SafeUnescape(parts[0]);
                transfer.Kind = (TransferKind)kindValue;
                transfer.FileName = SafeUnescape(parts[3]);
                transfer.StatusText = SafeUnescape(parts[4]);
                transfer.TargetText = SafeUnescape(parts[5]);

                double progress;
                if (double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out progress))
                {
                    transfer.Progress = progress;
                }

                long ticks;
                if (long.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) &&
                    ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
                {
                    transfer.CreatedAt = new DateTime(ticks);
                }
                else
                {
                    transfer.CreatedAt = DateTime.Now;
                }

                transfer.IconGlyph = parts.Length > 8
                    ? SafeUnescape(parts[8])
                    : GetTransferGlyph(transfer.Kind);
                transfer.IconColor = parts.Length > 9
                    ? SafeUnescape(parts[9])
                    : "#F6F6F4";

                TransferState state = (TransferState)stateValue;
                if (state == TransferState.Running)
                {
                    state = TransferState.Interrupted;
                    transfer.StatusText = "Прервано при закрытии";
                    transfer.IconColor = "#8A8A8A";
                    changed = true;
                }

                transfer.State = state;
                transfer.Cancellation = null;
                _completedTransfers.Add(transfer);
            }

            while (_completedTransfers.Count > TransferHistoryLimit)
            {
                _completedTransfers.RemoveAt(_completedTransfers.Count - 1);
            }

            if (changed)
            {
                SaveTransferHistory();
            }
        }

        private static string SafeUnescape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch
            {
                return value;
            }
        }

        private void DownloadFile(YandexFileItem file)
        {
            string safeName = SanitizeFileName(file.Name);
            string extension = Path.GetExtension(safeName);

            if (string.IsNullOrEmpty(extension) || extension == ".")
            {
                extension = ".bin";
            }

            extension = extension.ToLowerInvariant();

            // КАРТИНКИ — сразу в галерею
            if (extension == ".jpg" || extension == ".jpeg" || extension == ".png")
            {
                DownloadImageToGallery(file);
                return;
            }

            // ОСТАЛЬНЫЕ ФАЙЛЫ — через FileSavePicker
            var savePicker = new FileSavePicker();
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("Файлы", new List<string>() { extension });
            savePicker.SuggestedFileName = safeName;

            _pendingDownloadFile = file;
            SavePendingDownload(file);

            try
            {
                savePicker.PickSaveFileAndContinue();
            }
            catch (Exception ex)
            {
                _pendingDownloadFile = null;
                ClearPendingDownload();
                ShowInfoDialog("СБОЙ", "Не удалось открыть выбор файла:\n" + ex.Message);
            }
        }

        private void DownloadImageToGallery(YandexFileItem file)
        {
            TransferItem transfer = CreateTransfer(file.Name, "Скачивание...", TransferKind.Image, "В ГАЛЕРЕЮ");
            transfer.SourceFile = file;

            AddActiveTransfer(transfer);
            RunImageDownloadAsync(transfer, file);
        }

        private async void RunImageDownloadAsync(TransferItem transfer, YandexFileItem file)
        {
            var client = _webDav;
            if (transfer == null || client == null || file == null)
            {
                FinishTransfer(transfer, TransferState.Failed, "Нет подключения");
                return;
            }

            CancellationToken token = transfer.Cancellation != null
                ? transfer.Cancellation.Token
                : CancellationToken.None;
            string safeName = SanitizeFileName(file.Name);
            StorageFile tempFile = null;

            BeginDownloadActivity();
            App.Log("image download start: " + file.Name);

            try
            {
                tempFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                    safeName, CreationCollisionOption.GenerateUniqueName);
                token.ThrowIfCancellationRequested();

                var request = client.CreateDownloadRequest(file.Href);

                using (token.Register(delegate
                {
                    try
                    {
                        request.Abort();
                    }
                    catch
                    {
                    }
                }))
                {
                    try
                    {
                        var response = await Task.Factory.FromAsync<WebResponse>(
                            request.BeginGetResponse, request.EndGetResponse, null);

                        using (var httpResponse = (HttpWebResponse)response)
                        using (Stream responseStream = httpResponse.GetResponseStream())
                        using (var fileStream = await tempFile.OpenStreamForWriteAsync())
                        {
                            long totalBytes = httpResponse.ContentLength;
                            long bytesRead = 0;
                            int lastProgress = -1;
                            byte[] buffer = new byte[16384];
                            int read;

                            while ((read = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                token.ThrowIfCancellationRequested();
                                await fileStream.WriteAsync(buffer, 0, read);
                                bytesRead += read;

                                if (totalBytes > 0)
                                {
                                    int progress = (int)((bytesRead * 100) / totalBytes);
                                    if (progress != lastProgress)
                                    {
                                        lastProgress = progress;
                                        int capturedProgress = progress;
                                        Deployment.Current.Dispatcher.BeginInvoke(() =>
                                        {
                                            if (transfer.IsRunning)
                                            {
                                                transfer.Progress = capturedProgress;
                                            }
                                        });
                                    }
                                }
                            }

                            await fileStream.FlushAsync();
                        }
                    }
                    catch (WebException)
                    {
                        if (token.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(token);
                        }

                        throw;
                    }
                }

                token.ThrowIfCancellationRequested();

                using (var readStream = await tempFile.OpenStreamForReadAsync())
                using (var mediaLibrary = new MediaLibrary())
                {
                    mediaLibrary.SavePicture(safeName, readStream);
                }

                App.Log("image download done: " + file.Name);
                FinishTransfer(transfer, TransferState.Completed, "В Галерее");
            }
            catch (OperationCanceledException)
            {
                FinishTransfer(transfer, TransferState.Canceled, "Отменено");
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    FinishTransfer(transfer, TransferState.Canceled, "Отменено");
                }
                else
                {
                    App.Log("image download failed: " + file.Name + ": " + ex);
                    FinishTransfer(transfer, TransferState.Failed, "Ошибка скачивания");

                    if (IsUnauthorizedError(ex))
                    {
                        HandleUnauthorized();
                    }
                }
            }
            finally
            {
                if (tempFile != null)
                {
                    // Удаление временного файла запускаем без await:
                    // в C# 5 (VS2013) нельзя использовать await в finally.
                    DeleteTempFile(tempFile);
                }

                EndDownloadActivity();
            }
        }

        private static async void DeleteTempFile(StorageFile file)
        {
            try
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        public void ContinueFileSavePicker(FileSavePickerContinuationEventArgs args)
        {
            if (_pendingDownloadFile == null)
            {
                RestorePendingDownload(IsolatedStorageSettings.ApplicationSettings);
            }

            if (args.File != null && _pendingDownloadFile != null)
            {
                var fileToDownload = _pendingDownloadFile;
                _pendingDownloadFile = null;
                ClearPendingDownload();

                string transferName = fileToDownload.IsFolder
                    ? fileToDownload.Name + ".zip"
                    : fileToDownload.Name;
                TransferKind kind = fileToDownload.IsFolder
                    ? TransferKind.FolderArchive
                    : TransferKind.Download;
                TransferItem transfer = CreateTransfer(
                    transferName,
                    fileToDownload.IsFolder ? "Подготовка архива..." : "Скачивание...",
                    kind,
                    fileToDownload.IsFolder ? "ZIP · НА ТЕЛЕФОН" : "НА ТЕЛЕФОН");
                transfer.SourceFile = fileToDownload;
                transfer.TargetFile = args.File;

                AddActiveTransfer(transfer);

                if (fileToDownload.IsFolder)
                {
                    StartFolderDownloadToStorageFile(fileToDownload, args.File, transfer);
                }
                else
                {
                    StartDownloadToStorageFile(fileToDownload, args.File, transfer);
                }
            }
            else
            {
                _pendingDownloadFile = null;
                ClearPendingDownload();
            }
        }

        private async void StartFolderDownloadToStorageFile(YandexFileItem folder, StorageFile targetFile,
                                                          TransferItem transfer)
        {
            var client = _webDav;
            if (transfer == null || client == null || folder == null || targetFile == null)
            {
                FinishTransfer(transfer, TransferState.Failed, "Нет подключения");
                return;
            }

            CancellationToken token = transfer.Cancellation != null
                ? transfer.Cancellation.Token
                : CancellationToken.None;

            BeginDownloadActivity();
            App.Log("folder download start: " + folder.Name);

            try
            {
                transfer.StatusText = "Подготовка архива...";
                List<FolderZipEntry> entries = await CollectFolderZipEntriesAsync(client, folder, transfer, token);
                token.ThrowIfCancellationRequested();

                int totalFiles = 0;
                foreach (FolderZipEntry entry in entries)
                {
                    if (!entry.IsDirectory)
                    {
                        totalFiles++;
                    }
                }

                App.Log("folder download prepared: " + folder.Name + ", files=" + totalFiles);

                string rootName = SanitizeZipSegment(folder.Name);
                if (string.IsNullOrEmpty(rootName))
                {
                    rootName = "folder";
                }

                using (Stream output = await targetFile.OpenStreamForWriteAsync())
                {
                    output.SetLength(0);
                    output.Seek(0, SeekOrigin.Begin);

                    using (var zip = new ZipArchiveWriter(output))
                    {
                        zip.AddDirectory(rootName + "/");

                        int completedFiles = 0;

                        foreach (FolderZipEntry entry in entries)
                        {
                            token.ThrowIfCancellationRequested();

                            if (entry.IsDirectory)
                            {
                                zip.AddDirectory(entry.EntryPath);
                                continue;
                            }

                            transfer.StatusText = "Скачивание: " + entry.Item.Name;

                            HttpWebRequest request = client.CreateDownloadRequest(entry.Item.Href);

                            using (token.Register(delegate
                            {
                                try
                                {
                                    request.Abort();
                                }
                                catch
                                {
                                }
                            }))
                            {
                                try
                                {
                                    using (WebResponse response = await Task.Factory.FromAsync<WebResponse>(
                                        request.BeginGetResponse, request.EndGetResponse, null))
                                    using (Stream responseStream = response.GetResponseStream())
                                    using (Stream entryStream = zip.BeginEntry(entry.EntryPath))
                                    {
                                        byte[] buffer = new byte[65536];
                                        int read;

                                        while ((read = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                        {
                                            token.ThrowIfCancellationRequested();
                                            entryStream.Write(buffer, 0, read);
                                        }
                                    }
                                }
                                catch (WebException)
                                {
                                    if (token.IsCancellationRequested)
                                    {
                                        throw new OperationCanceledException(token);
                                    }

                                    throw;
                                }
                            }

                            completedFiles++;

                            if (totalFiles > 0)
                            {
                                transfer.Progress = completedFiles * 100.0 / totalFiles;
                            }
                        }

                        zip.Finish();
                    }

                    await output.FlushAsync();
                }

                token.ThrowIfCancellationRequested();

                App.Log("folder download done: " + folder.Name + ", files=" + totalFiles);
                FinishTransfer(transfer, TransferState.Completed, "Архив готов");
            }
            catch (OperationCanceledException)
            {
                FinishTransfer(transfer, TransferState.Canceled, "Отменено");
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    FinishTransfer(transfer, TransferState.Canceled, "Отменено");
                }
                else
                {
                    App.Log("folder download failed: " + folder.Name + ": " + ex);
                    FinishTransfer(transfer, TransferState.Failed, "Ошибка архива");

                    if (IsUnauthorizedError(ex))
                    {
                        HandleUnauthorized();
                    }
                }
            }
            finally
            {
                EndDownloadActivity();
            }
        }

        private async Task<List<FolderZipEntry>> CollectFolderZipEntriesAsync(YandexWebDavClient client,
                                                                              YandexFileItem folder,
                                                                              TransferItem transfer,
                                                                              CancellationToken token)
        {
            var result = new List<FolderZipEntry>();
            var queue = new Queue<KeyValuePair<string, string>>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string rootName = SanitizeZipSegment(folder.Name);
            if (string.IsNullOrEmpty(rootName))
            {
                rootName = "folder";
            }

            visited.Add(folder.Href.TrimEnd('/'));
            queue.Enqueue(new KeyValuePair<string, string>(folder.Href, rootName));

            int foldersScanned = 0;

            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested();

                KeyValuePair<string, string> current = queue.Dequeue();
                List<YandexFileItem> items = await client.GetFolderAsync(current.Key, token);

                foldersScanned++;
                transfer.StatusText = "Подготовка: папок " + foldersScanned;

                foreach (YandexFileItem child in items)
                {
                    string childName = SanitizeZipSegment(child.Name);
                    if (string.IsNullOrEmpty(childName))
                    {
                        continue;
                    }

                    string entryPath = current.Value + "/" + childName;

                    if (child.IsFolder)
                    {
                        string childHref = child.Href.TrimEnd('/');
                        if (!visited.Add(childHref))
                        {
                            continue;
                        }

                        result.Add(new FolderZipEntry
                        {
                            Item = child,
                            EntryPath = entryPath + "/",
                            IsDirectory = true
                        });

                        queue.Enqueue(new KeyValuePair<string, string>(child.Href, entryPath));
                    }
                    else
                    {
                        result.Add(new FolderZipEntry
                        {
                            Item = child,
                            EntryPath = entryPath,
                            IsDirectory = false
                        });
                    }
                }
            }

            return result;
        }

        private static string SanitizeZipSegment(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "";
            }

            var builder = new StringBuilder(name.Length);

            foreach (char c in name.Trim())
            {
                char value = c;

                if (value == '/' || value == '\\' || value == ':' || value == '*' ||
                    value == '?' || value == '"' || value == '<' || value == '>' || value == '|' ||
                    char.IsControl(value))
                {
                    value = '_';
                }

                builder.Append(value);
            }

            return builder.ToString().TrimEnd('.', ' ');
        }

        private sealed class FolderZipEntry
        {
            public YandexFileItem Item;
            public string EntryPath;
            public bool IsDirectory;
        }

        private async void StartDownloadToStorageFile(YandexFileItem file, StorageFile targetFile, TransferItem transfer)
        {
            var client = _webDav;
            if (transfer == null || client == null || file == null || targetFile == null)
            {
                FinishTransfer(transfer, TransferState.Failed, "Нет подключения");
                return;
            }

            CancellationToken token = transfer.Cancellation != null
                ? transfer.Cancellation.Token
                : CancellationToken.None;

            long totalBytes = -1;
            long downloadedBytes = 0;
            int lastProgress = -1;
            bool isCompleted = false;
            bool authExpired = false;
            Exception lastError = null;

            BeginDownloadActivity();
            App.Log("download start: " + file.Name);

            try
            {
                try
                {
                    totalBytes = await GetRemoteFileSizeAsync(client, file.Href, token);
                    token.ThrowIfCancellationRequested();

                    if (totalBytes > 0)
                    {
                        App.Log("download size: " + totalBytes);
                    }
                    else if (totalBytes == 0)
                    {
                        isCompleted = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // HEAD может быть запрещён — размер возьмём из первого 206-ответа.
                    App.Log("download HEAD failed: " + ex.Message);
                }

                token.ThrowIfCancellationRequested();

                using (var fileStream = await targetFile.OpenStreamForWriteAsync())
                {
                    fileStream.SetLength(0);
                    await fileStream.FlushAsync();
                }

                while (!isCompleted)
                {
                    token.ThrowIfCancellationRequested();

                    bool chunkDone = false;
                    Exception chunkError = null;
                    bool stopRetry = false;

                    for (int attempt = 1; attempt <= DownloadMaxAttempts && !chunkDone && !stopRetry; attempt++)
                    {
                        token.ThrowIfCancellationRequested();

                        HttpWebResponse response = null;

                        try
                        {
                            // Начинаем каждый раз с текущей позиции: если предыдущая
                            // попытка успела записать часть данных, она не потеряется.
                            long chunkStart = downloadedBytes;
                            long chunkEnd = chunkStart + DownloadChunkSize - 1;

                            if (totalBytes > 0 && chunkEnd >= totalBytes)
                            {
                                chunkEnd = totalBytes - 1;
                            }

                            HttpWebRequest request = client.CreateDownloadRequest(file.Href);

                            using (token.Register(delegate
                            {
                                try
                                {
                                    request.Abort();
                                }
                                catch
                                {
                                }
                            }))
                            {
                                if (!TrySetRangeHeader(request, chunkStart, chunkEnd))
                                {
                                    stopRetry = true;
                                    throw new NotSupportedException(
                                        "Этот телефон не позволяет скачивать файл по частям.");
                                }

                                response = (HttpWebResponse)await Task.Factory.FromAsync<WebResponse>(
                                    request.BeginGetResponse, request.EndGetResponse, null);

                                token.ThrowIfCancellationRequested();

                                int status = (int)response.StatusCode;

                                if (status == 401 || status == 403)
                                {
                                    stopRetry = true;

                                    if (status == 401)
                                    {
                                        authExpired = true;
                                    }

                                    throw new IOException(status == 401
                                        ? "Пароль не подошёл или сессия истекла."
                                        : "Нет прав на скачивание файла.");
                                }

                                if (status == 206)
                                {
                                    if (totalBytes <= 0)
                                    {
                                        totalBytes = ParseTotalBytes(response.Headers["Content-Range"]);
                                    }
                                }
                                else if (status == 200)
                                {
                                    // Сервер проигнорировал Range. Для маленького файла
                                    // это допустимо, для большого — нет.
                                    if (chunkStart == 0 && response.ContentLength > 0 &&
                                        response.ContentLength <= DownloadChunkSize * 2)
                                    {
                                        totalBytes = response.ContentLength;
                                    }
                                    else
                                    {
                                        stopRetry = true;
                                        throw new NotSupportedException(
                                            "Сервер не поддерживает докачку, а файл слишком большой для одного запроса.");
                                    }
                                }
                                else if (status == 416)
                                {
                                    // 416 после уже скачанных байт означает, что файл кончился.
                                    if (downloadedBytes > 0 && (totalBytes <= 0 || downloadedBytes >= totalBytes))
                                    {
                                        isCompleted = true;
                                        chunkDone = true;
                                        break;
                                    }

                                    stopRetry = true;
                                    throw new IOException("Сервер отклонил диапазон докачки.");
                                }
                                else
                                {
                                    throw new IOException("HTTP " + status);
                                }

                                long expectedBytes = response.ContentLength;
                                long chunkRead = 0;

                                using (Stream responseStream = response.GetResponseStream())
                                using (var fileStream = await targetFile.OpenStreamForWriteAsync())
                                {
                                    fileStream.Seek(downloadedBytes, SeekOrigin.Begin);

                                    byte[] buffer = new byte[65536];
                                    int read;

                                    while ((read = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                    {
                                        token.ThrowIfCancellationRequested();

                                        await fileStream.WriteAsync(buffer, 0, read);
                                        downloadedBytes += read;
                                        chunkRead += read;

                                        ReportDownloadProgress(transfer, downloadedBytes, totalBytes, ref lastProgress);
                                    }

                                    await fileStream.FlushAsync();
                                }

                                if (expectedBytes > 0 && chunkRead < expectedBytes)
                                {
                                    throw new IOException("Пришла только часть данных.");
                                }

                                if (chunkRead == 0)
                                {
                                    throw new IOException("Сервер вернул пустой ответ.");
                                }

                                chunkDone = true;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (WebException ex)
                        {
                            if (token.IsCancellationRequested)
                            {
                                throw new OperationCanceledException(token);
                            }

                            chunkError = ex;

                            var errorResponse = ex.Response as HttpWebResponse;
                            if (errorResponse != null)
                            {
                                int errorStatus = (int)errorResponse.StatusCode;
                                errorResponse.Dispose();

                                if (errorStatus == 401 || errorStatus == 403)
                                {
                                    stopRetry = true;

                                    if (errorStatus == 401)
                                    {
                                        authExpired = true;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (token.IsCancellationRequested)
                            {
                                throw new OperationCanceledException(token);
                            }

                            chunkError = ex;
                        }
                        finally
                        {
                            if (response != null)
                            {
                                response.Dispose();
                            }
                        }

                        if (!chunkDone && !stopRetry && attempt < DownloadMaxAttempts)
                        {
                            transfer.StatusText = "Обрыв. Попытка " + (attempt + 1) + " из " + DownloadMaxAttempts;
                            App.Log("download retry " + file.Name + " at " + downloadedBytes +
                                ": " + (chunkError != null ? chunkError.Message : "?"));
                            await Task.Delay(2000);
                            token.ThrowIfCancellationRequested();
                        }
                    }

                    if (!chunkDone)
                    {
                        lastError = chunkError;
                        break;
                    }

                    if (totalBytes > 0)
                    {
                        if (downloadedBytes >= totalBytes)
                        {
                            isCompleted = true;
                        }
                    }
                    else if (downloadedBytes <= 0)
                    {
                        lastError = new IOException("Сервер не сообщил размер файла.");
                        break;
                    }
                }

                token.ThrowIfCancellationRequested();

                if (isCompleted)
                {
                    App.Log("download done: " + file.Name + " (" + downloadedBytes + " bytes)");
                    FinishTransfer(transfer, TransferState.Completed, "На телефоне");
                }
                else
                {
                    App.Log("download failed: " + file.Name + " at " + downloadedBytes + " / " + totalBytes +
                        ": " + (lastError != null ? lastError.Message : "?"));
                    FinishTransfer(transfer, TransferState.Failed, "Ошибка сети");

                    if (authExpired)
                    {
                        HandleUnauthorized();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                FinishTransfer(transfer, TransferState.Canceled, "Отменено");
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    FinishTransfer(transfer, TransferState.Canceled, "Отменено");
                }
                else
                {
                    App.Log("download exception: " + ex);
                    FinishTransfer(transfer, TransferState.Failed, "Ошибка скачивания");

                    if (authExpired || IsUnauthorizedError(ex))
                    {
                        HandleUnauthorized();
                    }
                }
            }
            finally
            {
                EndDownloadActivity();
            }
        }

        private static async Task<long> GetRemoteFileSizeAsync(YandexWebDavClient client, string href,
                                                               CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            HttpWebRequest request = client.CreateDownloadRequest(href);
            request.Method = "HEAD";

            using (token.Register(delegate
            {
                try
                {
                    request.Abort();
                }
                catch
                {
                }
            }))
            {
                try
                {
                    using (WebResponse response = await Task.Factory.FromAsync<WebResponse>(
                        request.BeginGetResponse, request.EndGetResponse, null))
                    {
                        return response.ContentLength;
                    }
                }
                catch (WebException)
                {
                    if (token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(token);
                    }

                    throw;
                }
            }
        }

        private static long ParseTotalBytes(string contentRange)
        {
            if (string.IsNullOrEmpty(contentRange))
            {
                return -1;
            }

            int slash = contentRange.LastIndexOf('/');
            if (slash < 0)
            {
                return -1;
            }

            long total;
            if (long.TryParse(contentRange.Substring(slash + 1).Trim(), out total))
            {
                return total;
            }

            return -1;
        }

        private void ReportDownloadProgress(TransferItem transfer, long downloaded, long total, ref int lastProgress)
        {
            if (total <= 0)
            {
                return;
            }

            int progress = (int)((downloaded * 100) / total);
            if (progress == lastProgress)
            {
                return;
            }

            lastProgress = progress;
            int capturedProgress = progress;
            Deployment.Current.Dispatcher.BeginInvoke(() =>
            {
                if (transfer.IsRunning)
                {
                    transfer.Progress = capturedProgress;
                }
            });

            if (progress % 10 == 0)
            {
                App.Log("download " + transfer.FileName + ": " + progress + "% (" + downloaded + " bytes)");
            }
        }

        private void BeginDownloadActivity()
        {
            if (_activeDownloads == 0)
            {
                try
                {
                    PhoneApplicationService.Current.UserIdleDetectionMode = IdleDetectionMode.Disabled;
                }
                catch
                {
                }

                try
                {
                    PhoneApplicationService.Current.ApplicationIdleDetectionMode = IdleDetectionMode.Disabled;
                }
                catch
                {
                }
            }

            _activeDownloads++;
        }

        private void EndDownloadActivity()
        {
            if (_activeDownloads > 0)
            {
                _activeDownloads--;
            }

            if (_activeDownloads == 0)
            {
                try
                {
                    PhoneApplicationService.Current.UserIdleDetectionMode = IdleDetectionMode.Enabled;
                }
                catch
                {
                }

                try
                {
                    PhoneApplicationService.Current.ApplicationIdleDetectionMode = IdleDetectionMode.Enabled;
                }
                catch
                {
                }
            }
        }

        private static bool TrySetRangeHeader(HttpWebRequest request, long from, long to)
        {
            try
            {
                request.Headers["Range"] = "bytes=" + from + "-" + to;
                return true;
            }
            catch (Exception)
            {
                // На некоторых сборках Silverlight заголовок Range запрещён.
                return false;
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "file";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            string result = builder.ToString().Trim().TrimEnd('.');
            if (result.Length == 0)
            {
                return "file";
            }

            if (result.Length > 100)
            {
                string extension = Path.GetExtension(result);
                if (extension.Length > 10)
                {
                    extension = "";
                }

                result = result.Substring(0, 100 - extension.Length) + extension;
            }

            return result;
        }
    }

    public enum TransferKind
    {
        Download = 0,
        Upload = 1,
        FolderArchive = 2,
        Image = 3
    }

    public enum TransferState
    {
        Running = 0,
        Completed = 1,
        Failed = 2,
        Canceled = 3,
        Interrupted = 4
    }

    public class TransferItem : INotifyPropertyChanged
    {
        private double _progress;
        private string _statusText;
        private string _progressText = "0%";
        private string _iconGlyph;
        private string _iconColor = "#F6F6F4";
        private Brush _iconBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF6, 0xF6, 0xF4));
        private string _targetText = "";
        private TransferState _state = TransferState.Running;

        public string Id { get; set; }
        public string FileName { get; set; }
        public TransferKind Kind { get; set; }
        public DateTime CreatedAt { get; set; }

        public YandexFileItem SourceFile { get; set; }
        public StorageFile SourceStorageFile { get; set; }
        public StorageFile TargetFile { get; set; }
        public string UploadFolderPath { get; set; }

        public CancellationTokenSource Cancellation { get; set; }

        public string TargetText
        {
            get { return _targetText; }
            set
            {
                if (_targetText == value)
                {
                    return;
                }

                _targetText = value;
                Notify("TargetText");
            }
        }

        public string IconGlyph
        {
            get { return _iconGlyph; }
            set
            {
                if (_iconGlyph == value)
                {
                    return;
                }

                _iconGlyph = value;
                Notify("IconGlyph");
            }
        }

        public string IconColor
        {
            get { return _iconColor; }
            set
            {
                if (_iconColor == value)
                {
                    return;
                }

                _iconColor = value;
                _iconBrush = CreateIconBrush(value);
                Notify("IconColor");
                Notify("IconBrush");
            }
        }

        public Brush IconBrush
        {
            get { return _iconBrush; }
        }

        private static Brush CreateIconBrush(string color)
        {
            try
            {
                if (!string.IsNullOrEmpty(color) && color.Length == 7 && color[0] == '#')
                {
                    int rgb = Convert.ToInt32(color.Substring(1), 16);
                    return new SolidColorBrush(Color.FromArgb(
                        0xFF,
                        (byte)((rgb >> 16) & 0xFF),
                        (byte)((rgb >> 8) & 0xFF),
                        (byte)(rgb & 0xFF)));
                }
            }
            catch
            {
            }

            return new SolidColorBrush(Color.FromArgb(0xFF, 0xF6, 0xF6, 0xF4));
        }

        public TransferState State
        {
            get { return _state; }
            set
            {
                if (_state == value)
                {
                    return;
                }

                _state = value;
                Notify("State");
                Notify("IsRunning");
                Notify("CanRetry");
            }
        }

        public bool IsRunning
        {
            get { return _state == TransferState.Running; }
        }

        public bool CanRetry
        {
            get
            {
                if (_state == TransferState.Running || _state == TransferState.Completed)
                {
                    return false;
                }

                switch (Kind)
                {
                    case TransferKind.Upload:
                        return SourceStorageFile != null;
                    case TransferKind.Image:
                        return SourceFile != null;
                    default:
                        return SourceFile != null && TargetFile != null;
                }
            }
        }

        public double Progress
        {
            get { return _progress; }
            set
            {
                if (value < 0)
                {
                    value = 0;
                }
                else if (value > 100)
                {
                    value = 100;
                }

                if (_progress == value)
                {
                    return;
                }

                _progress = value;
                ProgressText = value >= 100 ? "100%" : value.ToString("0") + "%";
                Notify("Progress");
            }
        }

        public string StatusText
        {
            get { return _statusText; }
            set
            {
                if (_statusText == value)
                {
                    return;
                }

                _statusText = value;
                Notify("StatusText");
            }
        }

        public string ProgressText
        {
            get { return _progressText; }
            private set
            {
                if (_progressText == value)
                {
                    return;
                }

                _progressText = value;
                Notify("ProgressText");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Notify(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}