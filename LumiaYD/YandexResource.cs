using System.ComponentModel;

namespace LumiaYD
{
    public class YandexFileItem : INotifyPropertyChanged
    {
        private double _dimOpacity = 1.0;

        public string Name { get; set; }
        public string Info { get; set; }
        public string Href { get; set; }
        public bool IsFolder { get; set; }

        public double DimOpacity
        {
            get { return _dimOpacity; }
            set
            {
                if (_dimOpacity == value)
                {
                    return;
                }

                _dimOpacity = value;
                OnPropertyChanged("DimOpacity");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
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
