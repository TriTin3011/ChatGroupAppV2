using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChatGroupApp.Client;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChatMessage> _messages = [];
    private readonly Dictionary<string, string[]> _emojiGroups = new()
    {
        ["Smile"] =
        [
            "\U0001F600", "\U0001F601", "\U0001F602", "\U0001F923", "\U0001F605", "\U0001F60A",
            "\U0001F607", "\U0001F609", "\U0001F60D", "\U0001F618", "\U0001F61C", "\U0001F61D",
            "\U0001F92A", "\U0001F60E", "\U0001F914", "\U0001F62D", "\U0001F621", "\U0001F634"
        ],
        ["Love"] =
        [
            "\u2764\uFE0F", "\U0001F9E1", "\U0001F49B", "\U0001F49A", "\U0001F499", "\U0001F49C",
            "\U0001F90D", "\U0001F90E", "\U0001F5A4", "\U0001F496", "\U0001F497", "\U0001F498",
            "\U0001F49D", "\U0001F49E", "\U0001F495", "\U0001F48C", "\U0001F48B", "\U0001F970"
        ],
        ["Hand"] =
        [
            "\U0001F44D", "\U0001F44E", "\U0001F44F", "\U0001F64C", "\U0001F64F", "\U0001F91D",
            "\U0001F44A", "\U0001F91C", "\U0001F91B", "\u270C\uFE0F", "\U0001F91E", "\U0001FAF6",
            "\U0001F44C", "\U0001F918", "\U0001F449", "\U0001F448", "\U0001F446", "\U0001F447"
        ],
        ["Fun"] =
        [
            "\U0001F389", "\U0001F38A", "\u2728", "\U0001F525", "\U0001F4AF", "\u2B50",
            "\u2705", "\u274C", "\u26A1", "\U0001F4A5", "\U0001F4AB", "\U0001F31F",
            "\U0001F3C6", "\U0001F3AE", "\U0001F3B5", "\U0001F3A7", "\U0001F4F8", "\U0001F680"
        ],
        ["Food"] =
        [
            "\U0001F37F", "\U0001F355", "\U0001F354", "\U0001F35F", "\U0001F32D", "\U0001F363",
            "\U0001F35C", "\U0001F36D", "\U0001F369", "\U0001F370", "\U0001F9CB", "\u2615",
            "\U0001F34E", "\U0001F34C", "\U0001F347", "\U0001F349", "\U0001F352", "\U0001F36A"
        ]
    };

    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _receiveCancellation;

    private string? _previewFilePath;
    private bool _isPreviewImage;

    // Giới hạn max size 1GB
    private const long MaxFileSizeInBytes = 1024L * 1024L * 1024L;

    public MainWindow()
    {
        InitializeComponent();
        MessagesListBox.ItemsSource = _messages;
        BuildEmojiButtons("Smile");
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortTextBox.Text.Trim(), out var port) || port is <= 0 or > 65535)
        {
            MessageBox.Show("Port phải là số từ 1 đến 65535.", "Sai port", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var host = ServerIpTextBox.Text.Trim();
        var userName = UserNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(userName))
        {
            MessageBox.Show("Vui lòng nhập IP server và tên của bạn.", "Thiếu thông tin", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            SetConnectionUi(isConnected: false, isConnecting: true);
            StatusTextBlock.Text = "Đang kết nối server...";

            _client = new TcpClient();
            await _client.ConnectAsync(host, port);

            var stream = _client.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };

            await _writer.WriteLineAsync(userName);
            _receiveCancellation = new CancellationTokenSource();

            SetConnectionUi(isConnected: true);
            AddMessage("Hệ thống", $"Đã kết nối tới {host}:{port}", MessageKind.System);
            StatusTextBlock.Text = $"Đang chat tại {host}:{port}";

            _ = ReceiveLoopAsync(_receiveCancellation.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            AddMessage("Hệ thống", $"Không kết nối được server: {ex.Message}", MessageKind.System);
            Disconnect();
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        Disconnect();
        AddMessage("Hệ thống", "Đã ngắt kết nối.", MessageKind.System);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync();
    }

    private async void MessageTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

    private void EmojiButton_Click(object sender, RoutedEventArgs e)
    {
        EmojiPopup.IsOpen = !EmojiPopup.IsOpen;
    }

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        AttachPopup.IsOpen = !AttachPopup.IsOpen;
    }

    private void EmojiTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string groupName })
        {
            BuildEmojiButtons(groupName);
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        Disconnect();
    }

    private async Task SendMessageAsync()
    {
        if (_previewFilePath != null)
        {
            var filePath = _previewFilePath;
            var isImage = _isPreviewImage;
            CancelPreview_Click(null!, null!);
            _ = UploadFileAsync(filePath, isImage);
        }

        var text = MessageTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) || _writer is null)
        {
            return;
        }

        try
        {
            await _writer.WriteLineAsync(text);
            MessageTextBox.Clear();
            MessageTextBox.Focus();
        }
        catch (IOException ex)
        {
            AddMessage("Hệ thống", $"Gửi tin nhắn thất bại: {ex.Message}", MessageKind.System);
            Disconnect();
        }
    }

    private void SendImageOption_Click(object sender, RoutedEventArgs e)
    {
        AttachPopup.IsOpen = false;
        var openFileDialog = new OpenFileDialog
        {
            Filter = "Image files (*.png;*.jpeg;*.jpg)|*.png;*.jpeg;*.jpg|All files (*.*)|*.*",
            Title = "Chọn ảnh"
        };
        if (openFileDialog.ShowDialog() == true)
        {
            SetPreview(openFileDialog.FileName, true);
        }
    }

    private void SendFileOption_Click(object sender, RoutedEventArgs e)
    {
        AttachPopup.IsOpen = false;
        var openFileDialog = new OpenFileDialog
        {
            Filter = "All files (*.*)|*.*",
            Title = "Chọn file"
        };
        if (openFileDialog.ShowDialog() == true)
        {
            SetPreview(openFileDialog.FileName, false);
        }
    }

    private void CancelPreview_Click(object sender, RoutedEventArgs e)
    {
        _previewFilePath = null;
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewFileText.Text = "";
    }

    private void SetPreview(string filePath, bool isImage)
    {
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > MaxFileSizeInBytes)
        {
            MessageBox.Show("Dung lượng đính kèm vượt quá 1GB!\nVui lòng chọn file nhỏ hơn.", "Lỗi dung lượng", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _previewFilePath = filePath;
        _isPreviewImage = isImage;
        PreviewPanel.Visibility = Visibility.Visible;
        if (isImage)
        {
            try
            {
                PreviewImage.Source = ChatMessage.LoadImageEfficiently(filePath);
                PreviewImage.Visibility = Visibility.Visible;
                PreviewFileText.Text = Path.GetFileName(filePath);
            }
            catch
            {
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewFileText.Text = Path.GetFileName(filePath);
            }
        }
        else
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewFileText.Text = Path.GetFileName(filePath);
        }
    }

    // Logic click vào Image để mở to lên
    private void ViewImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ImageSource source)
        {
            FullSizeImage.Source = source;
            ImageViewerOverlay.Visibility = Visibility.Visible;
        }
    }

    // Đóng popup xem ảnh
    private void CloseViewer_Click(object sender, RoutedEventArgs e)
    {
        ImageViewerOverlay.Visibility = Visibility.Collapsed;
        FullSizeImage.Source = null;
    }

    private void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ChatMessage msg && msg.FileId != null)
        {
            _ = DownloadFileAsync(msg);
        }
    }

    private async Task UploadFileAsync(string filePath, bool isImage)
    {
        var fileName = Path.GetFileName(filePath);
        var fileId = Guid.NewGuid().ToString("N");
        var fileInfo = new FileInfo(filePath);
        var size = fileInfo.Length;

        var message = new ChatMessage("Bạn", fileName, MessageKind.Me)
        {
            IsFile = true,
            IsImage = isImage,
            FileId = fileId,
            FilePath = isImage ? filePath : null,
            FileSize = size,
            IsTransferring = true
        };

        _messages.Add(message);
        MessagesListBox.ScrollIntoView(message);

        try
        {
            if (!int.TryParse(PortTextBox.Text.Trim(), out var port)) return;
            var host = ServerIpTextBox.Text.Trim();
            var userName = UserNameTextBox.Text.Trim();

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port + 1);
            await using var stream = tcpClient.GetStream();

            var header = Encoding.UTF8.GetBytes($"UPLOAD|{userName}|{fileId}|{fileName}|{size}|{isImage}\n");
            await stream.WriteAsync(header);

            // Bọc logic loop truyền byte nặng vào Task.Run để không bị block UI Thread
            await Task.Run(async () =>
            {
                await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                var buffer = new byte[81920];
                long totalSent = 0;
                int read;
                while ((read = await fileStream.ReadAsync(buffer)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, read);
                    totalSent += read;
                    message.TransferProgress = (double)totalSent / size * 100;
                }
                var responseBytes = new byte[3];
                await stream.ReadAsync(responseBytes);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => AddMessage("Hệ thống", $"Lỗi gửi file: {ex.Message}", MessageKind.System));
        }
        finally
        {
            message.IsTransferring = false;
        }
    }

    private async Task DownloadFileAsync(ChatMessage msg)
    {
        if (msg.FileId == null) return;

        msg.IsTransferring = true;
        msg.TransferProgress = 0;

        try
        {
            if (!int.TryParse(PortTextBox.Text.Trim(), out var port)) return;
            var host = ServerIpTextBox.Text.Trim();

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port + 1);
            await using var stream = tcpClient.GetStream();

            var header = Encoding.UTF8.GetBytes($"DOWNLOAD|{msg.FileId}\n");
            await stream.WriteAsync(header);

            var responseBytes = new List<byte>();
            var buf = new byte[1];
            while (await stream.ReadAsync(buf) > 0)
            {
                if (buf[0] == '\n') break;
                if (buf[0] != '\r') responseBytes.Add(buf[0]);
            }
            var responseHeader = Encoding.UTF8.GetString(responseBytes.ToArray()).Split('|');
            if (responseHeader[0] == "OK" && responseHeader.Length > 1 && long.TryParse(responseHeader[1], out var size))
            {
                Directory.CreateDirectory("downloads");
                var ext = Path.GetExtension(msg.Text);
                if (string.IsNullOrEmpty(ext)) ext = ".dat";
                var localPath = Path.Combine("downloads", $"{msg.FileId}{ext}");
                localPath = Path.GetFullPath(localPath);

                // Task.Run để UI được scroll thoải mái
                await Task.Run(async () =>
                {
                    await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    while (totalRead < size && (read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, size - totalRead))) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;
                        msg.TransferProgress = (double)totalRead / size * 100;
                    }
                });

                msg.FilePath = localPath;

                if (!msg.IsImage)
                {
                    Dispatcher.Invoke(() => MessageBox.Show($"Đã tải xong: {localPath}", "Tải file", MessageBoxButton.OK, MessageBoxImage.Information));
                }
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => AddMessage("Hệ thống", $"Lỗi tải file: {ex.Message}", MessageKind.System));
        }
        finally
        {
            msg.IsTransferring = false;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader is not null)
            {
                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                Dispatcher.Invoke(() => HandleServerLine(line));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            Dispatcher.Invoke(() => AddMessage("Hệ thống", "Mất kết nối server.", MessageKind.System));
        }
        finally
        {
            Dispatcher.Invoke(Disconnect);
        }
    }

    private void HandleServerLine(string line)
    {
        var parts = line.Split('|', 6);
        switch (parts[0])
        {
            case "CHAT" when parts.Length >= 3:
                AddMessage(parts[1], parts[2], MessageKind.Other);
                break;
            case "FILE" when parts.Length >= 6:
                var sender = parts[1];
                var fileName = parts[2];
                var fileId = parts[3];

                if (_messages.Any(m => m.FileId == fileId))
                {
                    break;
                }

                _ = long.TryParse(parts[4], out var size);
                _ = bool.TryParse(parts[5], out var isImage);

                var msg = new ChatMessage(sender, fileName, MessageKind.Other)
                {
                    IsFile = true,
                    IsImage = isImage,
                    FileId = fileId,
                    FileSize = size
                };
                _messages.Add(msg);
                EmptyChatHint.Visibility = Visibility.Collapsed;
                MessagesListBox.ScrollIntoView(msg);

                if (isImage)
                {
                    _ = DownloadFileAsync(msg);
                }
                break;
            case "ME" when parts.Length >= 2:
                AddMessage("Bạn", parts[1], MessageKind.Me);
                break;
            case "SERVER" when parts.Length >= 2:
                AddMessage("Hệ thống", parts[1], MessageKind.System);
                break;
            default:
                AddMessage("Server", line, MessageKind.System);
                break;
        }
    }

    private void BuildEmojiButtons(string groupName)
    {
        EmojiWrapPanel.Children.Clear();

        if (!_emojiGroups.TryGetValue(groupName, out var emojiList))
        {
            return;
        }

        foreach (var emoji in emojiList)
        {
            var button = new Button
            {
                Style = (Style)FindResource("EmojiButtonStyle"),
                // Dùng Emoji.Wpf.TextBlock thay vì nhét text trực tiếp
                Content = new Emoji.Wpf.TextBlock { Text = emoji, FontSize = 23, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Margin = new Thickness(3),
                ToolTip = $"Chèn {emoji}"
            };

            button.Click += (_, _) =>
            {
                var selectionStart = MessageTextBox.SelectionStart;
                MessageTextBox.Text = MessageTextBox.Text.Insert(selectionStart, emoji);
                MessageTextBox.SelectionStart = selectionStart + emoji.Length;
                MessageTextBox.Focus();
            };

            EmojiWrapPanel.Children.Add(button);
        }
    }

    private void AddMessage(string sender, string text, MessageKind kind)
    {
        _messages.Add(new ChatMessage(sender, text, kind));
        EmptyChatHint.Visibility = Visibility.Collapsed;
        MessagesListBox.ScrollIntoView(_messages[^1]);
    }

    private void SetConnectionUi(bool isConnected, bool isConnecting = false)
    {
        ServerIpTextBox.IsEnabled = !isConnected && !isConnecting;
        PortTextBox.IsEnabled = !isConnected && !isConnecting;
        UserNameTextBox.IsEnabled = !isConnected && !isConnecting;
        ConnectButton.IsEnabled = !isConnected && !isConnecting;
        DisconnectButton.IsEnabled = isConnected;
        MessageTextBox.IsEnabled = isConnected;
        SendButton.IsEnabled = isConnected;
        AttachButton.IsEnabled = isConnected; // Disable / Enable nút đính kèm (+)
    }

    private void Disconnect()
    {
        _receiveCancellation?.Cancel();
        _receiveCancellation?.Dispose();
        _receiveCancellation = null;

        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Close();

        _reader = null;
        _writer = null;
        _client = null;

        SetConnectionUi(isConnected: false);
        StatusTextBlock.Text = "Sẵn sàng kết nối nè!";
    }
}

public sealed class ChatMessage : INotifyPropertyChanged
{
    public string Sender { get; }
    public string Text { get; }
    public MessageKind Kind { get; }

    public bool IsFile { get; init; }
    public bool IsImage { get; init; }
    public string? FileId { get; init; }
    public long FileSize { get; init; }

    private string? _filePath;
    public string? FilePath
    {
        get => _filePath;
        set
        {
            if (_filePath != value)
            {
                _filePath = value;
                OnPropertyChanged();
                if (IsImage && !string.IsNullOrEmpty(_filePath))
                {
                    ImageSource = LoadImageEfficiently(_filePath);
                }
            }
        }
    }

    private ImageSource? _imageSource;
    public ImageSource? ImageSource
    {
        get => _imageSource;
        set
        {
            if (_imageSource != value)
            {
                _imageSource = value;
                OnPropertyChanged();
            }
        }
    }

    public static BitmapImage? LoadImageEfficiently(string filePath)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath);
            bitmap.DecodePixelWidth = 400;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private double _transferProgress;
    public double TransferProgress
    {
        get => _transferProgress;
        set
        {
            if (_transferProgress != value)
            {
                _transferProgress = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isTransferring;
    public bool IsTransferring
    {
        get => _isTransferring;
        set
        {
            if (_isTransferring != value)
            {
                _isTransferring = value;
                OnPropertyChanged();
            }
        }
    }

    public Brush Background { get; }
    public Brush Foreground { get; }
    public Brush SenderForeground { get; }
    public HorizontalAlignment Alignment { get; }
    public CornerRadius CornerRadius { get; }

    public ChatMessage(string sender, string text, MessageKind kind)
    {
        Sender = sender;
        Text = text;
        Kind = kind;

        Background = kind switch
        {
            MessageKind.Me => new LinearGradientBrush(
                Color.FromRgb(255, 122, 182),
                Color.FromRgb(180, 140, 255),
                new Point(0, 0),
                new Point(1, 1)),
            MessageKind.Other => new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            _ => new SolidColorBrush(Color.FromRgb(255, 242, 220))
        };

        Foreground = kind == MessageKind.Me
            ? Brushes.White
            : new SolidColorBrush(Color.FromRgb(43, 36, 64));

        SenderForeground = kind == MessageKind.Me
            ? new SolidColorBrush(Color.FromRgb(255, 238, 248))
            : new SolidColorBrush(Color.FromRgb(127, 120, 149));

        Alignment = kind == MessageKind.Me
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;

        CornerRadius = kind == MessageKind.Me
            ? new CornerRadius(22, 22, 6, 22)
            : new CornerRadius(22, 22, 22, 6);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum MessageKind
{
    System,
    Other,
    Me
}