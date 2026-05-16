using System.Collections.ObjectModel;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
        var parts = line.Split('|', 3);
        switch (parts[0])
        {
            case "CHAT" when parts.Length == 3:
                AddMessage(parts[1], parts[2], MessageKind.Other);
                break;
            case "ME" when parts.Length == 2:
                AddMessage("Bạn", parts[1], MessageKind.Me);
                break;
            case "SERVER" when parts.Length == 2:
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
                Content = emoji,
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = 23,
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
        StatusTextBlock.Text = "Chưa kết nối server.";
    }
}

public sealed class ChatMessage(string sender, string text, MessageKind kind)
{
    public string Sender { get; } = sender;
    public string Text { get; } = text;

    public Brush Background { get; } = kind switch
    {
        MessageKind.Me => new LinearGradientBrush(
            Color.FromRgb(22, 119, 255),
            Color.FromRgb(124, 58, 237),
            new Point(0, 0),
            new Point(1, 1)),
        MessageKind.Other => new SolidColorBrush(Color.FromRgb(255, 255, 255)),
        _ => new SolidColorBrush(Color.FromRgb(255, 248, 220))
    };

    public Brush Foreground { get; } = kind == MessageKind.Me
        ? Brushes.White
        : new SolidColorBrush(Color.FromRgb(17, 24, 39));

    public Brush SenderForeground { get; } = kind == MessageKind.Me
        ? new SolidColorBrush(Color.FromRgb(235, 244, 255))
        : new SolidColorBrush(Color.FromRgb(107, 114, 128));

    public HorizontalAlignment Alignment { get; } = kind == MessageKind.Me
        ? HorizontalAlignment.Right
        : HorizontalAlignment.Left;

    public CornerRadius CornerRadius { get; } = kind == MessageKind.Me
        ? new CornerRadius(18, 18, 4, 18)
        : new CornerRadius(18, 18, 18, 4);
}

public enum MessageKind
{
    System,
    Other,
    Me
}
