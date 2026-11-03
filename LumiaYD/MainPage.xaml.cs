using System;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Controls;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using System.IO.IsolatedStorage;
using System.Net;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.Xna.Framework.Media;
using System.IO;
using System.Text;
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

        private string _login;
        private string _password;
        private bool _initialized;

        private string currentPath = "/";
        private YandexFileItem selectedItem = null;
        private YandexFileItem _pendingDownloadFile;

        public MainPage()
        {
            InitializeComponent();
            this.BackKeyPress += MainPage_BackKeyPress;
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
            }
        }

        private void LoadCredentials()
        {
            var settings = IsolatedStorageSettings.ApplicationSettings;
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
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://webdav.yandex.ru/");
                request.Method = "PROPFIND";
                request.ContentType = "application/xml; charset=utf-8";
                request.UserAgent = "LumiaYD/1.0 (Windows Phone 8.1)";
                request.Headers["Authorization"] = MakeAuthHeader(login, password);
                request.Headers["Depth"] = "0";

                string xmlPayload = "<?xml version=\"1.0\" encoding=\"utf-8\" ?><propfind xmlns=\"DAV:\"><prop><displayname/></prop></propfind>";
                byte[] bytes = Encoding.UTF8.GetBytes(xmlPayload);
                request.ContentLength = bytes.Length;

                using (Stream requestStream = await Task.Factory.FromAsync<Stream>(
                    request.BeginGetRequestStream, request.EndGetRequestStream, null))
                {
                    await requestStream.WriteAsync(bytes, 0, bytes.Length);
                }

                using (WebResponse response = await Task.Factory.FromAsync<WebResponse>(
                    request.BeginGetResponse, request.EndGetResponse, null))
                {
                    return (int)((HttpWebResponse)response).StatusCode == 207;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.Unauthorized)
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

        private static string MakeAuthHeader(string login, string password)
        {
            string pair = login + ":" + password.Replace(" ", "");
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair));
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
            FilesListBox.ItemsSource = null;
            MainPivot.Visibility = Visibility.Collapsed;
            ShowAuth();
            StatusText.Text = "ПАРОЛЬ НЕ ПОДОШЁЛ ИЛИ СЕССИЯ ИСТЕКЛА. ВОЙДИТЕ СНОВА.";
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            ClearCredentials();
            FilesListBox.ItemsSource = null;
            TransferListBox.ItemsSource = null;
            MainPivot.Visibility = Visibility.Collapsed;
            ShowAuth();
        }

        private void ShowMainInterface()
        {
            AuthArea.Visibility = Visibility.Collapsed;
            MainPivot.Visibility = Visibility.Visible;
            CurrentAccountText.Text = _login;
            LoadFolder(currentPath);
        }

        private string GetParentPath(string path)
        {
            string p = path.TrimEnd('/');
            int idx = p.LastIndexOf('/');
            return idx <= 0 ? "/" : p.Substring(0, idx + 1);
        }

        private void LoadFolder(string path)
        {
            if (string.IsNullOrEmpty(_login) || string.IsNullOrEmpty(_password))
            {
                ShowAuth();
                return;
            }

            currentPath = path;
            string pretty = Uri.UnescapeDataString(path).Trim('/');
            CurrentPathText.Text = pretty == "" ? "диск / ЗАГРУЗКА..." : "диск / " + pretty + " ЗАГРУЗКА...";
            try
            {
                string url = "https://webdav.yandex.ru" + path;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "PROPFIND";
                request.AllowReadStreamBuffering = true;
                request.ContentType = "application/xml; charset=utf-8";
                request.UserAgent = "Mozilla/5.0 (Windows Phone 8.1; ARM; Trident/7.0; IEMobile/11.0; NOKIA; Lumia 925)";

                request.Headers["Authorization"] = MakeAuthHeader(_login, _password);
                request.Headers["Depth"] = "1";

                string xmlPayload = "<?xml version=\"1.0\" encoding=\"utf-8\" ?><propfind xmlns=\"DAV:\"><prop><displayname/><resourcetype/><getcontentlength/></prop></propfind>";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(xmlPayload);
                request.ContentLength = bytes.Length;

                request.BeginGetRequestStream(new AsyncCallback(asynchronousResult =>
                {
                    try
                    {
                        HttpWebRequest req = (HttpWebRequest)asynchronousResult.AsyncState;
                        using (System.IO.Stream postStream = req.EndGetRequestStream(asynchronousResult))
                        {
                            postStream.Write(bytes, 0, bytes.Length);
                            postStream.Flush();
                        }
                        req.BeginGetResponse(new AsyncCallback(PropFindCompleted), req);
                    }
                    catch (Exception ex)
                    {
                        Deployment.Current.Dispatcher.BeginInvoke(() =>
                        {
                            MessageBox.Show("Ошибка потока WP:\n" + ex.Message, "Сбой", MessageBoxButton.OK);
                        });
                    }
                }), request);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка создания WebRequest:\n" + ex.Message, "Сбой", MessageBoxButton.OK);
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
            DownloadButton.Visibility = Visibility.Visible;
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
            string extension = Path.GetExtension(file.Name).ToLower();

            // КАРТИНКИ — сразу в галерею
            if (extension == ".jpg" || extension == ".jpeg" || extension == ".png")
            {
                DownloadImageToGallery(file);
                return;
            }

            // ОСТАЛЬНЫЕ ФАЙЛЫ — через FileSavePicker
            var savePicker = new FileSavePicker();
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            if (!string.IsNullOrEmpty(extension))
            {
                savePicker.FileTypeChoices.Add("Файлы", new List<string>() { extension });
            }
            else
            {
                savePicker.FileTypeChoices.Add("Все файлы", new List<string>() { ".*" });
            }
            savePicker.SuggestedFileName = file.Name;

            _pendingDownloadFile = file;
            savePicker.PickSaveFileAndContinue();
        }

        private async void DownloadImageToGallery(YandexFileItem file)
        {
            var transfer = new TransferItem
            {
                FileName = file.Name,
                Progress = 0,
                StatusText = "Скачивание...",
                ProgressText = "0%",
                IconPath = "M12 4 L12 16 M12 16 L7 11 M12 16 L17 11",
                IconColor = "#0078D7"
            };

            var transfers = TransferListBox.ItemsSource as List<TransferItem>;
            if (transfers == null)
            {
                transfers = new List<TransferItem>();
                TransferListBox.ItemsSource = transfers;
                SyncStatusText.Text = "";
            }
            transfers.Add(transfer);

            try
            {
                var request = (HttpWebRequest)WebRequest.Create("https://webdav.yandex.ru" + file.Href);
                request.Method = "GET";
                request.AllowReadStreamBuffering = false;
                request.Headers["Authorization"] = MakeAuthHeader(_login, _password);

                var response = await Task.Factory.FromAsync<WebResponse>(request.BeginGetResponse, request.EndGetResponse, null);

                using (var httpResponse = (HttpWebResponse)response)
                using (Stream responseStream = httpResponse.GetResponseStream())
                using (var memoryStream = new MemoryStream())
                {
                    long totalBytes = httpResponse.ContentLength;
                    byte[] buffer = new byte[16384];
                    long bytesRead = 0;
                    int read;

                    while ((read = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await memoryStream.WriteAsync(buffer, 0, read);
                        bytesRead += read;

                        if (totalBytes > 0 && (bytesRead % (16384 * 10) == 0 || bytesRead == totalBytes))
                        {
                            int progress = (int)((bytesRead * 100) / totalBytes);
                            Deployment.Current.Dispatcher.BeginInvoke(() => transfer.Progress = progress);
                        }
                    }

                    memoryStream.Position = 0;
                    using (var mediaLibrary = new MediaLibrary())
                    {
                        mediaLibrary.SavePicture(file.Name, memoryStream);
                    }

                    Deployment.Current.Dispatcher.BeginInvoke(() =>
                    {
                        transfer.StatusText = "В галерее";
                        transfer.Progress = 100;
                        MessageBox.Show("Картинка сохранена в галерею!\nЧерез USB: Phone\\Pictures", "Успех", MessageBoxButton.OK);
                    });
                }
            }
            catch (Exception ex)
            {
                Deployment.Current.Dispatcher.BeginInvoke(() =>
                {
                    transfer.StatusText = "Ошибка";
                    transfer.IconColor = "#E81123";
                    MessageBox.Show("Ошибка при скачивании картинки:\n" + ex.Message, "Сбой", MessageBoxButton.OK);
                });
            }
        }
        public void ContinueFileSavePicker(FileSavePickerContinuationEventArgs args)
        {
            if (args.File != null && _pendingDownloadFile != null)
            {
                var fileToDownload = _pendingDownloadFile;
                _pendingDownloadFile = null;

                var transfer = new TransferItem
                {
                    FileName = fileToDownload.Name,
                    Progress = 0,
                    StatusText = "Скачивание...",
                    ProgressText = "0%",
                    IconPath = "M12 4 L12 16 M12 16 L7 11 M12 16 L17 11",
                    IconColor = "#0078D7"
                };

                var transfers = TransferListBox.ItemsSource as List<TransferItem>;
                if (transfers == null)
                {
                    transfers = new List<TransferItem>();
                    TransferListBox.ItemsSource = transfers;
                    SyncStatusText.Text = "";
                }
                transfers.Add(transfer);

                StartDownloadToStorageFile(fileToDownload, args.File, transfer);
            }
            else
            {
                MessageBox.Show("Скачивание отменено", "Инфо", MessageBoxButton.OK);
            }
        }

        private async void StartDownloadToStorageFile(YandexFileItem file, StorageFile targetFile, TransferItem transfer)
        {
            int maxRetries = 5; // Увеличим количество попыток
            int currentRetry = 0;
            long totalBytesRead = 0;
            long totalBytes = 0;
            bool isCompleted = false;
            Exception lastError = null;

            while (currentRetry < maxRetries && !isCompleted)
            {
                lastError = null;

                try
                {
                    var request = (HttpWebRequest)WebRequest.Create("https://webdav.yandex.ru" + file.Href);
                    request.Method = "GET";
                    request.AllowReadStreamBuffering = false;

                    request.Headers["Authorization"] = MakeAuthHeader(_login, _password);

                    if (totalBytesRead > 0)
                    {
                        request.Headers["Range"] = "bytes=" + totalBytesRead + "-";
                        Deployment.Current.Dispatcher.BeginInvoke(() =>
                        {
                            transfer.StatusText = "Восстановление связи...";
                        });
                    }

                    var response = await Task.Factory.FromAsync<WebResponse>(request.BeginGetResponse, request.EndGetResponse, null);

                    using (var httpResponse = (HttpWebResponse)response)
                    {
                        if (totalBytes == 0)
                            totalBytes = httpResponse.ContentLength + totalBytesRead;

                        using (Stream responseStream = httpResponse.GetResponseStream())
                        using (var fileStream = await targetFile.OpenStreamForWriteAsync())
                        {
                            if (totalBytesRead > 0)
                            {
                                fileStream.Seek(totalBytesRead, System.IO.SeekOrigin.Begin);
                            }

                            byte[] buffer = new byte[16384];
                            int read;

                            while ((read = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, read);
                                totalBytesRead += read;

                                if (totalBytes > 0 && (totalBytesRead % (16384 * 10) == 0 || totalBytesRead == totalBytes))
                                {
                                    int progress = (int)((totalBytesRead * 100) / totalBytes);
                                    Deployment.Current.Dispatcher.BeginInvoke(() => transfer.Progress = progress);
                                }
                            }

                            await fileStream.FlushAsync();
                        }
                    }

                    // --- ГЛАВНАЯ ПРОВЕРКА ---
                    // Если мы скачали меньше, чем должны были, выбрасываем ошибку, чтобы сработал retry
                    if (totalBytes > 0 && totalBytesRead < totalBytes)
                    {
                        throw new Exception("Соединение оборвалось. Скачано " + totalBytesRead + " из " + totalBytes + " байт.");
                    }

                    isCompleted = true;
                }
                catch (Exception ex)
                {
                    currentRetry++;
                    lastError = ex;

                    if (currentRetry < maxRetries)
                    {
                        Deployment.Current.Dispatcher.BeginInvoke(() =>
                        {
                            transfer.StatusText = "Обрыв! Попытка " + (currentRetry + 1) + "...";
                        });
                    }
                }

                if (lastError != null && currentRetry < maxRetries)
                {
                    await Task.Delay(3000);
                }
            }

            if (isCompleted)
            {
                Deployment.Current.Dispatcher.BeginInvoke(() =>
                {
                    transfer.StatusText = "Готово";
                    transfer.Progress = 100;
                    MessageBox.Show("Файл сохранён в выбранную папку!\nТеперь он виден через USB.", "Успех", MessageBoxButton.OK);
                });
            }
            else
            {
                Deployment.Current.Dispatcher.BeginInvoke(() =>
                {
                    transfer.StatusText = "Ошибка сети";
                    transfer.IconColor = "#E81123";
                    MessageBox.Show("Не удалось скачать файл полностью после " + maxRetries + " попыток.\nПоследняя ошибка: " + (lastError != null ? lastError.Message : "Неизвестно"), "Сбой", MessageBoxButton.OK);
                });
            }
        }
        private void PropFindCompleted(IAsyncResult asynchronousResult)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)asynchronousResult.AsyncState;
                using (HttpWebResponse response = (HttpWebResponse)request.EndGetResponse(asynchronousResult))
                {
                    using (System.IO.StreamReader reader = new System.IO.StreamReader(response.GetResponseStream()))
                    {
                        string resultString = reader.ReadToEnd();
                        Deployment.Current.Dispatcher.BeginInvoke(() =>
                        {
                            ParseWebDavResponse(resultString);
                        });
                    }
                }
            }
            catch (WebException webEx)
            {
                if (webEx.Response != null)
                {
                    try
                    {
                        HttpWebResponse httpResponse = (HttpWebResponse)webEx.Response;
                        int statusCode = (int)httpResponse.StatusCode;

                        if (statusCode == 401)
                        {
                            Deployment.Current.Dispatcher.BeginInvoke(() => HandleUnauthorized());
                            return;
                        }

                        using (var errorStream = httpResponse.GetResponseStream())
                        using (var reader = new System.IO.StreamReader(errorStream))
                        {
                            string errorText = reader.ReadToEnd();
                            Deployment.Current.Dispatcher.BeginInvoke(() =>
                            {
                                MessageBox.Show("Код ошибки сервера: " + statusCode + "\nТекст ответа: '" + errorText + "'",
                                    "Детали от Яндекса", MessageBoxButton.OK);
                            });
                            return;
                        }
                    }
                    catch { }
                }
                Deployment.Current.Dispatcher.BeginInvoke(() =>
                {
                    MessageBox.Show("Сетевой сбой:\n" + webEx.Message, "Упс!", MessageBoxButton.OK);
                });
            }
            catch (Exception ex)
            {
                Deployment.Current.Dispatcher.BeginInvoke(() =>
                {
                    MessageBox.Show("Общая ошибка:\n" + ex.Message, "Упс!", MessageBoxButton.OK);
                });
            }
        }

        private void ParseWebDavResponse(string xmlContent)
        {
            try
            {
                List<YandexFileItem> files = new List<YandexFileItem>();
                string[] responses = xmlContent.Split(new string[] { "<d:response>" }, StringSplitOptions.RemoveEmptyEntries);

                for (int i = 1; i < responses.Length; i++)
                {
                    string block = responses[i];
                    string href = ExtractValue(block, "<d:href>", "</d:href>");
                    string displayName = ExtractValue(block, "<d:displayname>", "</d:displayname>");

                    if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(displayName)) continue;
                    if (href.TrimEnd('/') == currentPath.TrimEnd('/')) continue;

                    bool isFolder = block.Contains("<d:collection/>");
                    string info;
                    if (isFolder)
                    {
                        info = "Папка";
                    }
                    else
                    {
                        string lenStr = ExtractValue(block, "<d:getcontentlength>", "</d:getcontentlength>");
                        long len = 0;
                        if (!string.IsNullOrEmpty(lenStr)) long.TryParse(lenStr, out len);
                        info = FormatSize(len);
                    }

                    files.Add(new YandexFileItem
                    {
                        Name = displayName,
                        Info = info,
                        Href = href,
                        IsFolder = isFolder
                    });
                }

                if (currentPath != "/")
                {
                    files.Insert(0, new YandexFileItem
                    {
                        Name = "..",
                        Info = "подняться на уровень вверх",
                        Href = GetParentPath(currentPath),
                        IsFolder = true
                    });
                }

                FilesListBox.ItemsSource = files;

                string pretty = Uri.UnescapeDataString(currentPath).Trim('/');
                CurrentPathText.Text = pretty == ""
                    ? "диск / (" + files.Count + " элементов)"
                    : "диск / " + pretty + " (" + files.Count + " элементов)";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка парсинга XML:\n" + ex.Message, "Сбой", MessageBoxButton.OK);
            }
        }

        private string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " КБ";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " МБ";
        }

        private string ExtractValue(string source, string startTag, string endTag)
        {
            int startIndex = source.IndexOf(startTag);
            if (startIndex == -1) return null;
            startIndex += startTag.Length;
            int endIndex = source.IndexOf(endTag, startIndex);
            if (endIndex == -1) return null;
            return source.Substring(startIndex, endIndex - startIndex);
        }
    }

    public class YandexFileItem
    {
        public string Name { get; set; }
        public string Info { get; set; }
        public string Href { get; set; }
        public bool IsFolder { get; set; }
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