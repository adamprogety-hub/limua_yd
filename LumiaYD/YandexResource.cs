namespace LumiaYD
{
    public class YandexFileItem
    {
        public string Name { get; set; }
        public string Info { get; set; }
        public string Href { get; set; }
        public bool IsFolder { get; set; }
    }

    public class QuotaInfo
    {
        public long UsedBytes { get; set; }
        public long AvailableBytes { get; set; }

        public bool HasAvailable
        {
            get { return AvailableBytes > 0; }
        }

        public long TotalBytes
        {
            get { return HasAvailable ? UsedBytes + AvailableBytes : UsedBytes; }
        }
    }
}
