using System;
using System.Collections.Generic;

namespace LumiaYD
{
    // Модель ответа от API Яндекс Диска
    public class YandexDiskResponse
    {
        public string name { get; set; }
        public long? size { get; set; }
        public string type { get; set; }
        public string path { get; set; }
        public YandexEmbedded _embedded { get; set; }
    }

    public class YandexEmbedded
    {
        public List<YandexDiskResponse> items { get; set; }
    }

    // Класс для красивого вывода в наш список ListBox
    public class FileItemViewModel
    {
        public string Name { get; set; }
        public string Info { get; set; }
        public string Path { get; set; }
        public bool IsFolder { get; set; }
    }
}
