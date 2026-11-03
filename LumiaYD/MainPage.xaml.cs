using System;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Controls;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using System.IO.IsolatedStorage;
using System.Net;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

        private string _login;
        private string _password;
        private bool _initialized;

        private string currentPath = "/";
        private YandexFileItem selectedItem = null;
        private YandexFileItem _pendingDownloadFile;
        private readonly ObservableCollection<TransferItem> _transfers = new ObservableCollection<TransferItem>();

        private YandexWebDavClient _webDav;
        private int _folderRequestId;
        private CancellationTokenSource _folderCancellation;
        private CancellationTokenSource _quotaCancellation;

        public MainPage()
        {
            InitializeComponent();
            this.BackKeyPress += MainPage_BackKeyPress;

            TransferListBox.ItemsSource = _transfers;
            _transfers.CollectionChanged += (s, e) => UpdateTransfersEmptyState();
            UpdateTransfersEmptyState();
        }

        private void UpdateTransfersEmptyState()
        {
            SyncStatusText.Visibility = _transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MainPage_BackKeyPress(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (AuthArea.Visibility == Visibility.Visible)
            {
                return;
            }

            if (BottomActionPanel.Visibility == Visibility.Visible)
            {
                e.Cancel = true;
                BottomActionPanel.Visibility = Visibility.Collapsed;
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

            if (!_initialized)
            {
                _initialized = true;
                LoadCredentials();
            }

            var app = Application.Current as App;
            if (app != null && app.FileSaveContinuationArgs != null)
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

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(href))
            {
                _pendingDownloadFile = new YandexFileItem
                {
                    Name = name,
                    Href = href,
                    IsFolder = false,
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
            settings.Save();
        }

        private static void ClearPendingDownload()
        {
            var settings = IsolatedStorageSettings.ApplicationSettings;
            settings.Remove(PendingDownloadNameKey);
            settings.Remove(PendingDownloadHrefKey);
            settings.Save();
        }

        private void ShowAuth()
        {
            AuthArea.Visibility = Visibility.Visible;
            MainPivot.Visibility = Visibility.Collapsed;
            BottomActionPanel.Visibility = Visibility.Collapsed;
            LoginTextBox.Text = _login ?? "";
            PasswordBox.Password = "";
            StatusText.Text = "ВВЕДИТЕ ЛОГИН И ПАРОЛЬ ПРИЛОЖЕНИЯ";
            AuthProgress.Visibility = Visibility.Collapsed;
            LoginButton.IsEnabled = true;
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string login = LoginTextBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
            {
                StatusText.Text = "ЗАПОЛНИТЕ ЛОГИН И ПАРОЛЬ.";
                return;
            }

            LoginButton.IsEnabled = false;
            AuthProgress.Visibility = Visibility.Visible;
            StatusText.Text = "ПРОВЕРКА...";

            bool? ok = await TestCredentialsAsync(login, password);

            AuthProgress.Visibility = Visibility.Collapsed;
            LoginButton.IsEnabled = true;

            if (ok == true)
            {
                _login = login;
                _password = password;
                SaveCredentials();
                ShowMainInterface();
            }
            else if (ok == false)
            {
                StatusText.Text = "НЕВЕРНЫЙ ЛОГИН ИЛИ ПАРОЛЬ ПРИЛОЖЕНИЯ.";
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

                StatusText.Text = "ОШИБКА СЕТИ: " + ex.Message;
                return null;
            }
            catch (Exception ex)
            {
                StatusText.Text = "ОШИБКА: " + ex.Message;
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
            ClearCredentials();
            _pendingDownloadFile = null;
            ClearPendingDownload();
            _transfers.Clear();
            FilesListBox.ItemsSource = null;
            MainPivot.Visibility = Visibility.Collapsed;
            ShowAuth();
            StatusText.Text = "ПАРОЛЬ НЕ ПОДОШЁЛ ИЛИ СЕССИЯ ИСТЕКЛА. ВОЙДИТЕ СНОВА.";
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            ClearCredentials();
            _pendingDownloadFile = null;
            ClearPendingDownload();
            _transfers.Clear();
            FilesListBox.ItemsSource = null;
            currentPath = "/";
            SaveSetting(CurrentPathKey, "/");
            MainPivot.Visibility = Visibility.Collapsed;
            ShowAuth();
        }

        private void ShowMainInterface()
        {
            AuthArea.Visibility = Visibility.Collapsed;
            MainPivot.Visibility = Visibility.Visible;
            CurrentAccountText.Text = _login;
            _webDav = new YandexWebDavClient(_login, _password);
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
            string pretty = Uri.UnescapeDataString(path).Trim('/');
            CurrentPathText.Text = pretty == "" ? "диск / ЗАГРУЗКА..." : "диск / " + pretty + " ЗАГРУЗКА...";

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
                        IsFolder = true
                    });
                }

                FilesListBox.ItemsSource = files;
                SaveSetting(CurrentPathKey, path);

                int itemCount = files.Count - (path != "/" ? 1 : 0);
                CurrentPathText.Text = pretty == ""
                    ? "диск / (" + itemCount + " элементов)"
                    : "диск / " + pretty + " (" + itemCount + " элементов)";
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
                    MessageBox.Show("Папка не найдена. Открываю корень Диска.", "Инфо", MessageBoxButton.OK);
                    LoadFolder("/");
                    return;
                }

                CurrentPathText.Text = "диск / ОШИБКА";
                MessageBox.Show("Не удалось открыть папку:\n" + ex.Message, "Сбой", MessageBoxButton.OK);
            }
            catch (Exception ex)
            {
                if (requestId != _folderRequestId)
                {
                    return;
                }

                CurrentPathText.Text = "диск / ОШИБКА";
                MessageBox.Show("Ошибка при открытии папки:\n" + ex.Message, "Сбой", MessageBoxButton.OK);
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

            try
            {
                QuotaInfo quota = await client.GetQuotaAsync(token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

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
                    StorageText.Text = "Недоступно";
                }
            }
        }

        private void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            YandexFileItem item = FilesListBox.SelectedItem as YandexFileItem;
            FilesListBox.SelectedIndex = -1;
            if (item == null) return;

            if (item.Name == "..")
            {
                LoadFolder(item.Href);
                return;
            }

            if (item.IsFolder)
            {
                LoadFolder(item.Href);
            }
            else
            {
                selectedItem = item;
                ShowActionPanel(false);
            }
        }

        private void FilesListBox_Hold(object sender, System.Windows.Input.GestureEventArgs e)
        {
            try
            {
                var item = ((FrameworkElement)e.OriginalSource).DataContext as YandexFileItem;
                if (item == null || item.Name == "..") return;

                selectedItem = item;
                ShowActionPanel(item.IsFolder);
            }
            catch
            {
            }
        }

        private void ShowActionPanel(bool isFolder)
        {
            DownloadButton.Visibility = isFolder ? Visibility.Collapsed : Visibility.Visible;
            UploadButton.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
            BottomActionPanel.Visibility = Visibility.Visible;
        }

        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            BottomActionPanel.Visibility = Visibility.Collapsed;
            if (selectedItem != null && !selectedItem.IsFolder)
            {
                DownloadFile(selectedItem);
            }
        }

        private void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            BottomActionPanel.Visibility = Visibility.Collapsed;
            MessageBox.Show("Функция загрузки на диск будет добавлена следующим шагом!", "Инфо", MessageBoxButton.OK);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            BottomActionPanel.Visibility = Visibility.Collapsed;
            MessageBox.Show("Функция удаления будет добавлена следующим шагом!", "Инфо", MessageBoxButton.OK);
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
                MessageBox.Show("Не удалось открыть выбор файла:\n" + ex.Message, "Сбой", MessageBoxButton.OK);
            }
        }

        private static TransferItem CreateTransfer(string fileName, string status)
        {
            return new TransferItem
            {
                FileName = fileName,
                Progress = 0,
                StatusText = status,
                ProgressText = "0%",
                IconPath = "M12 4 L12 16 M12 16 L7 11 M12 16 L17 11",
                IconColor = "#0078D7"
            };
        }

        private async void DownloadImageToGallery(YandexFileItem file)
        {
            var transfer = CreateTransfer(file.Name, "Скачивание...");
            _transfers.Add(transfer);

            StorageFile tempFile = null;
            string safeName = SanitizeFileName(file.Name);
            var client = _webDav;

            if (client == null)
            {
                transfer.StatusText = "Ошибка авторизации";
                transfer.IconColor = "#E81123";
                return;
            }

            try
            {
                tempFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                    safeName, CreationCollisionOption.GenerateUniqueName);

                var request = client.CreateDownloadRequest(file.Href);

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
                        await fileStream.WriteAsync(buffer, 0, read);
                        bytesRead += read;

                        if (totalBytes > 0)
                        {
                            int progress = (int)((bytesRead * 100) / totalBytes);
                            if (progress != lastProgress)
                            {
                                lastProgress = progress;
                                int capturedProgress = progress;
                                Deployment.Current.Dispatcher.BeginInvoke(() => transfer.Progress = capturedProgress);
                            }
                        }
                    }

                    await fileStream.FlushAsync();
                }

                using (var readStream = await tempFile.OpenStreamForReadAsync())
                using (var mediaLibrary = new MediaLibrary())
                {
                    mediaLibrary.SavePicture(safeName, readStream);
                }

                transfer.StatusText = "В галерее";
                transfer.Progress = 100;
                MessageBox.Show("Картинка сохранена в галерею!\nЧерез USB: Phone\\Pictures", "Успех", MessageBoxButton.OK);
            }
            catch (Exception ex)
            {
                transfer.StatusText = "Ошибка";
                transfer.IconColor = "#E81123";
                MessageBox.Show("Ошибка при скачивании картинки:\n" + ex.Message, "Сбой", MessageBoxButton.OK);
            }
            finally
            {
                if (tempFile != null)
                {
                    // Удаление временного файла запускаем без await:
                    // в C# 5 (VS2013) нельзя использовать await в finally.
                    DeleteTempFile(tempFile);
                }
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

                var transfer = CreateTransfer(fileToDownload.Name, "Скачивание...");
                _transfers.Add(transfer);

                StartDownloadToStorageFile(fileToDownload, args.File, transfer);
            }
            else
            {
                _pendingDownloadFile = null;
                ClearPendingDownload();
                MessageBox.Show("Скачивание отменено", "Инфо", MessageBoxButton.OK);
            }
        }

        private async void StartDownloadToStorageFile(YandexFileItem file, StorageFile targetFile, TransferItem transfer)
        {
            const int maxRetries = 5;
            int attempt = 0;
            long totalBytesRead = 0;
            long totalBytes = 0;
            bool isCompleted = false;
            bool shouldRetry = true;
            Exception lastError = null;
            int lastProgress = -1;

            var client = _webDav;
            if (client == null)
            {
                transfer.StatusText = "Ошибка авторизации";
                transfer.IconColor = "#E81123";
                return;
            }

            while (attempt < maxRetries && !isCompleted)
            {
                attempt++;
                lastError = null;

                try
                {
                    var request = client.CreateDownloadRequest(file.Href);

                    if (totalBytesRead > 0)
                    {
                        if (TrySetRangeHeader(request, totalBytesRead))
                        {
                            transfer.StatusText = "Восстановление связи...";
                        }
                        else
                        {
                            // Range не поддерживается — скачиваем файл заново.
                            totalBytesRead = 0;
                            totalBytes = 0;
                            lastProgress = -1;
                        }
                    }

                    var response = await Task.Factory.FromAsync<WebResponse>(
                        request.BeginGetResponse, request.EndGetResponse, null);

                    using (var httpResponse = (HttpWebResponse)response)
                    {
                        int statusCode = (int)httpResponse.StatusCode;
                        long contentLength = httpResponse.ContentLength;

                        if (totalBytesRead > 0 && statusCode == 200)
                        {
                            // Сервер проигнорировал Range — начинаем файл заново.
                            totalBytesRead = 0;
                            totalBytes = contentLength;
                            lastProgress = -1;
                        }
                        else if (statusCode == 206)
                        {
                            // 206 — сервер вернул только остаток файла.
                            if (contentLength > 0)
                            {
                                totalBytes = totalBytesRead + contentLength;
                            }
                        }
                        else
                        {
                            totalBytes = contentLength;
                        }

                        using (Stream responseStream = httpResponse.GetResponseStream())
                        using (var fileStream = await targetFile.OpenStreamForWriteAsync())
                        {
                            if (totalBytesRead == 0)
                            {
                                fileStream.SetLength(0);
                            }
                            else
                            {
                                fileStream.Seek(totalBytesRead, SeekOrigin.Begin);
                            }

                            byte[] buffer = new byte[16384];
                            int read;

                            while ((read = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, read);
                                totalBytesRead += read;

                                if (totalBytes > 0)
                                {
                                    int progress = (int)((totalBytesRead * 100) / totalBytes);
                                    if (progress != lastProgress)
                                    {
                                        lastProgress = progress;
                                        int capturedProgress = progress;
                                        Deployment.Current.Dispatcher.BeginInvoke(() => transfer.Progress = capturedProgress);
                                    }
                                }
                            }

                            await fileStream.FlushAsync();
                        }
                    }

                    if (totalBytes > 0 && totalBytesRead < totalBytes)
                    {
                        throw new IOException("Соединение оборвалось. Скачано " + totalBytesRead + " из " + totalBytes + " байт.");
                    }

                    isCompleted = true;
                }
                catch (WebException ex)
                {
                    lastError = ex;

                    var errorResponse = ex.Response as HttpWebResponse;
                    if (errorResponse != null)
                    {
                        int errorStatus = (int)errorResponse.StatusCode;
                        if (errorStatus == 401 || errorStatus == 403)
                        {
                            // Нет смысла повторять: логин/пароль больше не подходят.
                            shouldRetry = false;
                        }
                        else if (errorStatus == 416)
                        {
                            // Range не принят. Если файл уже скачан целиком — это успех,
                            // иначе начинаем заново с нуля.
                            if (totalBytes > 0 && totalBytesRead >= totalBytes)
                            {
                                isCompleted = true;
                            }
                            else
                            {
                                totalBytesRead = 0;
                                totalBytes = 0;
                                lastProgress = -1;
                            }
                        }

                        errorResponse.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                if (!isCompleted && shouldRetry && attempt < maxRetries)
                {
                    transfer.StatusText = "Обрыв. Попытка " + (attempt + 1) + " из " + maxRetries + "...";
                    await Task.Delay(2000);
                }
            }

            if (isCompleted)
            {
                transfer.StatusText = "Готово";
                transfer.Progress = 100;
                MessageBox.Show("Файл сохранён в выбранную папку!\nТеперь он виден через USB.", "Успех", MessageBoxButton.OK);
            }
            else
            {
                transfer.StatusText = "Ошибка сети";
                transfer.IconColor = "#E81123";
                MessageBox.Show("Не удалось скачать файл полностью.\nПоследняя ошибка: " + (lastError != null ? lastError.Message : "Неизвестно"), "Сбой", MessageBoxButton.OK);
            }
        }

        private static bool TrySetRangeHeader(HttpWebRequest request, long from)
        {
            try
            {
                request.Headers["Range"] = "bytes=" + from + "-";
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

    public class TransferItem : INotifyPropertyChanged
    {
        private double _progress;
        private string _statusText;
        private string _progressText;

        public string FileName { get; set; }
        public string IconPath { get; set; }
        public string IconColor { get; set; }

        public double Progress
        {
            get { return _progress; }
            set
            {
                _progress = value;
                ProgressText = value >= 100 ? "100%" : value.ToString("0") + "%";
                if (PropertyChanged != null)
                    PropertyChanged(this, new PropertyChangedEventArgs("Progress"));
                if (PropertyChanged != null)
                    PropertyChanged(this, new PropertyChangedEventArgs("ProgressText"));
            }
        }

        public string StatusText
        {
            get { return _statusText; }
            set
            {
                _statusText = value;
                if (PropertyChanged != null)
                    PropertyChanged(this, new PropertyChangedEventArgs("StatusText"));
            }
        }

        public string ProgressText
        {
            get { return _progressText; }
            set
            {
                _progressText = value;
                if (PropertyChanged != null)
                    PropertyChanged(this, new PropertyChangedEventArgs("ProgressText"));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}