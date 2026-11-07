using System.ComponentModel;
using System.Windows.Media;

namespace LumiaYD
{
    public class YandexFileItem : INotifyPropertyChanged
    {
        private static readonly Brush TransparentRowBrush = new SolidColorBrush(Colors.Transparent);
        private static readonly Brush DefaultRowNameBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF6, 0xF6, 0xF4));
        private static readonly Brush DefaultRowInfoBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E));
        private static readonly Brush DefaultRowBorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));

        private double _dimOpacity = 1.0;
        private Brush _rowBackground = TransparentRowBrush;
        private Brush _rowBorderBrush = DefaultRowBorderBrush;
        private Brush _rowNameBrush = DefaultRowNameBrush;
        private Brush _rowInfoBrush = DefaultRowInfoBrush;

        public string Name { get; set; }
        public string Info { get; set; }
        public string Href { get; set; }
        public string IconGlyph { get; set; }
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

        public Brush RowBackground
        {
            get { return _rowBackground; }
            set
            {
                if (_rowBackground == value)
                {
                    return;
                }

                _rowBackground = value;
                OnPropertyChanged("RowBackground");
            }
        }

        public Brush RowBorderBrush
        {
            get { return _rowBorderBrush; }
            set
            {
                if (_rowBorderBrush == value)
                {
                    return;
                }

                _rowBorderBrush = value;
                OnPropertyChanged("RowBorderBrush");
            }
        }

        public Brush RowNameBrush
        {
            get { return _rowNameBrush; }
            set
            {
                if (_rowNameBrush == value)
                {
                    return;
                }

                _rowNameBrush = value;
                OnPropertyChanged("RowNameBrush");
            }
        }

        public Brush RowInfoBrush
        {
            get { return _rowInfoBrush; }
            set
            {
                if (_rowInfoBrush == value)
                {
                    return;
                }

                _rowInfoBrush = value;
                OnPropertyChanged("RowInfoBrush");
            }
        }

        public void ResetRowStyle()
        {
            RowBackground = TransparentRowBrush;
            RowBorderBrush = DefaultRowBorderBrush;
            RowNameBrush = DefaultRowNameBrush;
            RowInfoBrush = DefaultRowInfoBrush;
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
