using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;

namespace LumiaYD
{
    public sealed class YandexWebDavClient
    {
        private const string BaseUrl = "https://webdav.yandex.ru";
        private const string UserAgent = "Mozilla/5.0 (Windows Phone 8.1; ARM; Trident/7.0; IEMobile/11.0; NOKIA; Lumia 925)";

        private const string TestPayload =
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?><propfind xmlns=\"DAV:\"><prop><displayname/></prop></propfind>";

        private const string FolderPayload =
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?><propfind xmlns=\"DAV:\"><prop><displayname/><resourcetype/><getcontentlength/></prop></propfind>";

        private const string QuotaPayload =
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?><propfind xmlns=\"DAV:\"><prop><quota-used-bytes/><quota-available-bytes/></prop></propfind>";

        private readonly string _login;
        private readonly string _password;

        public YandexWebDavClient(string login, string password)
        {
            if (string.IsNullOrEmpty(login))
            {
                throw new ArgumentException("login");
            }
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("password");
            }

            _login = login;
            _password = password.Replace(" ", "");
        }

        public async Task TestConnectionAsync(CancellationToken cancellationToken)
        {
            await SendPropFindAsync("/", "0", TestPayload, cancellationToken);
        }

        public async Task<List<YandexFileItem>> GetFolderAsync(string path, CancellationToken cancellationToken)
        {
            string xml = await SendPropFindAsync(path, "1", FolderPayload, cancellationToken);
            return ParseFolder(xml, path);
        }

        public async Task<QuotaInfo> GetQuotaAsync(CancellationToken cancellationToken)
        {
            string xml = await SendPropFindAsync("/", "0", QuotaPayload, cancellationToken);
            return ParseQuota(xml);
        }

        public HttpWebRequest CreateDownloadRequest(string href)
        {
            var request = (HttpWebRequest)WebRequest.Create(BuildUrl(href));
            request.Method = "GET";
            request.AllowReadStreamBuffering = false;
            request.UserAgent = UserAgent;
            request.Headers["Authorization"] = MakeAuthHeader(_login, _password);
            return request;
        }

        public async Task DeleteAsync(string href, CancellationToken cancellationToken)
        {
            await SendSimpleRequestAsync(href, "DELETE", null, cancellationToken);
        }

        public async Task CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
        {
            string target = CombinePath(parentPath, name) + "/";
            await SendSimpleRequestAsync(target, "MKCOL", null, cancellationToken);
        }

        public async Task RenameAsync(string href, string newName, bool isFolder, CancellationToken cancellationToken)
        {
            string parentPath = GetParentPath(href);
            string target = CombinePath(parentPath, newName);
            if (isFolder)
            {
                target += "/";
            }

            await SendSimpleRequestAsync(href, "MOVE", request =>
            {
                request.Headers["Destination"] = BuildUrl(target);
                request.Headers["Overwrite"] = "F";
            }, cancellationToken);
        }

        public async Task UploadFileAsync(string folderPath, StorageFile file, Action<int> progress,
                                          CancellationToken cancellationToken)
        {
            if (file == null)
            {
                throw new ArgumentNullException("file");
            }

            cancellationToken.ThrowIfCancellationRequested();

            string target = CombinePath(folderPath, file.Name);

            using (Stream fileStream = await file.OpenStreamForReadAsync())
            {
                long totalBytes;
                if (fileStream.CanSeek)
                {
                    totalBytes = fileStream.Length;
                }
                else
                {
                    var properties = await file.GetBasicPropertiesAsync();
                    totalBytes = (long)properties.Size;
                }

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(BuildUrl(target));
                request.Method = "PUT";
                TryDisableWriteBuffering(request);
                request.ContentLength = totalBytes;
                request.UserAgent = UserAgent;
                request.Headers["Authorization"] = MakeAuthHeader(_login, _password);

                using (cancellationToken.Register(() =>
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
                        using (Stream requestStream = await Task.Factory.FromAsync<Stream>(
                            request.BeginGetRequestStream, request.EndGetRequestStream, null))
                        {
                            byte[] buffer = new byte[64 * 1024];
                            long sentBytes = 0;
                            int lastProgress = -1;
                            int read;

                            while ((read = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await requestStream.WriteAsync(buffer, 0, read);
                                sentBytes += read;

                                if (progress != null && totalBytes > 0)
                                {
                                    int percent = (int)Math.Min(100, sentBytes * 100 / totalBytes);
                                    if (percent != lastProgress)
                                    {
                                        lastProgress = percent;
                                        progress(percent);
                                    }
                                }
                            }

                            requestStream.Flush();
                        }

                        using (WebResponse response = await Task.Factory.FromAsync<WebResponse>(
                            request.BeginGetResponse, request.EndGetResponse, null))
                        {
                        }

                        if (progress != null)
                        {
                            progress(100);
                        }
                    }
                    catch (WebException ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        throw CreateWebDavException(ex);
                    }
                }
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "—";
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " КБ";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " МБ";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.0") + " ГБ";
        }

        private async Task<string> SendPropFindAsync(string path, string depth, string payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(BuildUrl(path));
            request.Method = "PROPFIND";
            request.AllowReadStreamBuffering = true;
            request.ContentType = "application/xml; charset=utf-8";
            request.UserAgent = UserAgent;
            request.Headers["Authorization"] = MakeAuthHeader(_login, _password);
            request.Headers["Depth"] = depth;

            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            request.ContentLength = bytes.Length;

            using (cancellationToken.Register(() =>
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
                    using (Stream requestStream = await Task.Factory.FromAsync<Stream>(
                        request.BeginGetRequestStream, request.EndGetRequestStream, null))
                    {
                        await requestStream.WriteAsync(bytes, 0, bytes.Length);
                    }

                    using (WebResponse response = await Task.Factory.FromAsync<WebResponse>(
                        request.BeginGetResponse, request.EndGetResponse, null))
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (WebException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw CreateWebDavException(ex);
                }
            }
        }

        private async Task SendSimpleRequestAsync(string path, string method, Action<HttpWebRequest> configure,
                                                  CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(BuildUrl(path));
            request.Method = method;
            request.AllowReadStreamBuffering = true;
            request.UserAgent = UserAgent;
            request.Headers["Authorization"] = MakeAuthHeader(_login, _password);

            if (configure != null)
            {
                configure(request);
            }

            using (cancellationToken.Register(() =>
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
                    }
                }
                catch (WebException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw CreateWebDavException(ex);
                }
            }
        }

        private static void TryDisableWriteBuffering(HttpWebRequest request)
        {
            try
            {
                var property = typeof(HttpWebRequest).GetProperty("AllowWriteStreamBuffering");
                if (property != null && property.CanWrite)
                {
                    property.SetValue(request, false, null);
                }
            }
            catch
            {
                // На некоторых сборках Silverlight свойство недоступно.
            }
        }

        private static string CombinePath(string parentPath, string name)
        {
            if (string.IsNullOrEmpty(parentPath))
            {
                parentPath = "/";
            }
            else if (parentPath[0] != '/')
            {
                parentPath = "/" + parentPath;
            }

            return parentPath.TrimEnd('/') + "/" + Uri.EscapeDataString(name);
        }

        private static string GetParentPath(string href)
        {
            string trimmed = href.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            if (slash <= 0)
            {
                return "/";
            }

            return trimmed.Substring(0, slash + 1);
        }

        private static List<YandexFileItem> ParseFolder(string xml, string path)
        {
            XDocument document = XDocument.Parse(xml);
            XNamespace dav = "DAV:";
            var result = new List<YandexFileItem>();

            foreach (XElement response in document.Descendants(dav + "response"))
            {
                string href = (string)response.Element(dav + "href");
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                string normalizedHref = NormalizeHref(href);
                if (normalizedHref.TrimEnd('/') == path.TrimEnd('/'))
                {
                    continue;
                }

                bool isFolder = response.Descendants(dav + "collection").Any();
                string displayName = (string)response.Element(dav + "displayname");
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = GetNameFromHref(normalizedHref);
                }
                if (string.IsNullOrEmpty(displayName))
                {
                    continue;
                }

                XElement lengthElement = response.Descendants(dav + "getcontentlength").FirstOrDefault();
                string info;
                if (isFolder)
                {
                    info = "Папка";
                }
                else if (lengthElement != null)
                {
                    long length = 0;
                    long.TryParse(lengthElement.Value, out length);
                    info = FormatSize(length);
                }
                else
                {
                    info = "Файл";
                }

                result.Add(new YandexFileItem
                {
                    Name = displayName,
                    Info = info,
                    Href = normalizedHref,
                    IsFolder = isFolder
                });
            }

            return result
                .OrderByDescending(item => item.IsFolder)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static QuotaInfo ParseQuota(string xml)
        {
            XDocument document = XDocument.Parse(xml);
            XNamespace dav = "DAV:";

            XElement usedElement = document.Descendants(dav + "quota-used-bytes").FirstOrDefault();
            XElement availableElement = document.Descendants(dav + "quota-available-bytes").FirstOrDefault();

            if (usedElement == null && availableElement == null)
            {
                return null;
            }

            long used = 0;
            long available = 0;

            if (usedElement != null)
            {
                long.TryParse(usedElement.Value, out used);
            }
            if (availableElement != null)
            {
                long.TryParse(availableElement.Value, out available);
            }

            return new QuotaInfo
            {
                UsedBytes = used,
                AvailableBytes = available
            };
        }

        private static string NormalizeHref(string href)
        {
            Uri absolute;
            if (Uri.TryCreate(href, UriKind.Absolute, out absolute))
            {
                return absolute.PathAndQuery;
            }

            return href;
        }

        private static string GetNameFromHref(string href)
        {
            string trimmed = href.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            string name = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
            return Uri.UnescapeDataString(name);
        }

        private static string BuildUrl(string path)
        {
            Uri absolute;
            if (Uri.TryCreate(path, UriKind.Absolute, out absolute))
            {
                return absolute.AbsoluteUri;
            }

            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }
            else if (path[0] != '/')
            {
                path = "/" + path;
            }

            return BaseUrl + path;
        }

        private static string MakeAuthHeader(string login, string password)
        {
            string pair = login + ":" + password.Replace(" ", "");
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair));
        }

        private static WebDavException CreateWebDavException(WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response == null)
            {
                return new WebDavException("Сетевая ошибка: " + ex.Message, null, null, ex);
            }

            using (response)
            {
                HttpStatusCode statusCode = response.StatusCode;
                string body = null;

                try
                {
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        body = reader.ReadToEnd();
                    }
                }
                catch
                {
                }

                string message = "Сервер вернул " + (int)statusCode + " (" + statusCode + ").";
                return new WebDavException(message, statusCode, body, ex);
            }
        }
    }

    public sealed class WebDavException : Exception
    {
        public HttpStatusCode? StatusCode { get; private set; }
        public string ResponseBody { get; private set; }

        public WebDavException(string message, HttpStatusCode? statusCode, string responseBody, Exception innerException)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
