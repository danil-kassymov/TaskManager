using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TaskManager.GDrive;

namespace TaskManager.Views;

/// <summary>Google account: setup instructions, Client ID/Secret, sign in through the browser, sign out.</summary>
public sealed class GoogleDriveDialog : Window
{
    readonly GoogleAuth _auth;
    readonly StackPanel _body = new() { Margin = new Thickness(22, 18, 22, 8) };
    CancellationTokenSource? _signIn;

    static ResourceDictionary Res => Application.Current.Resources;

    public GoogleDriveDialog(Window owner, GoogleAuth auth)
    {
        _auth = auth;
        Owner = owner;
        Title = "Google Диск";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Res["Panel"];
        Foreground = (Brush)Res["Text"];
        FontFamily = (FontFamily)Res["UiFont"];
        FontSize = 13.5;
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };
        Closed += (_, _) => _signIn?.Cancel();
        Build();
    }

    TextBlock Text(string s, double size = 13.5, string brush = "Text", double top = 0) => new()
    {
        Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
        Foreground = (Brush)Res[brush], LineHeight = size * 1.5,
    };

    Button Btn(string text, bool primary = false) => new()
    {
        Content = text, Style = (Style)Res[primary ? "PrimaryButton" : "ChipButton"], Margin = new Thickness(0, 0, 8, 0),
        Padding = new Thickness(12, 5, 12, 5),
    };

    void Build()
    {
        _body.Children.Clear();
        var account = _auth.Load();
        if (account?.RefreshToken is { Length: > 0 })
        {
            _body.Children.Add(Text("Вход выполнен", 16, top: 0));
            _body.Children.Add(Text(account.Email ?? "(адрес не получен)", top: 6));
            _body.Children.Add(Text(account.FullAccess
                ? "Доступ: весь Google Диск — видны и файлы, загруженные не через TaskManager."
                : "Доступ: только файлы и папки, созданные TaskManager (drive.file).", 12.5, "Text2", 6));
            _body.Children.Add(Text("Подключить папку базы: правый клик по источнику в разделе «База данных» → «Google Диск: подключить…».", 12.5, "Text2", 12));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 14) };
            var signOut = Btn("Выйти");
            signOut.Click += (_, _) => { _auth.SignOut(); Build(); };
            var close = Btn("Готово", primary: true);
            close.Click += (_, _) => DialogResult = true;
            row.Children.Add(signOut);
            row.Children.Add(close);
            _body.Children.Add(row);
            return;
        }

        _body.Children.Add(Text("Подключение Google Диска", 16));
        _body.Children.Add(Text("Google требует, чтобы у приложения был свой «клиент» в Google Cloud. Это бесплатно и делается один раз:", 12.5, "Text2", 6));
        var steps = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = (Brush)Res["Text2"], Margin = new Thickness(0, 8, 0, 0), LineHeight = 20 };
        void Step(string s) => steps.Inlines.Add(new Run(s + "\n"));
        void Link(string text, string url)
        {
            var h = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), Foreground = (Brush)Res["Accent"] };
            h.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
            steps.Inlines.Add(h);
        }
        steps.Inlines.Add(new Run("1. Откройте "));
        Link("console.cloud.google.com", "https://console.cloud.google.com/projectcreate");
        Step(" и создайте проект (любое имя).");
        steps.Inlines.Add(new Run("2. Включите "));
        Link("Google Drive API", "https://console.cloud.google.com/apis/library/drive.googleapis.com");
        Step(" для проекта.");
        steps.Inlines.Add(new Run("3. "));
        Link("Google Auth Platform", "https://console.cloud.google.com/auth/overview");
        Step(" → «Get started»: имя приложения, ваш email, Audience: External.");
        Step("4. Audience → Test users → добавьте свой Gmail. Чтобы не входить заново каждые 7 дней — нажмите «Publish app» (для доступа «только файлы TaskManager» проверка Google не нужна).");
        steps.Inlines.Add(new Run("5. "));
        Link("Clients", "https://console.cloud.google.com/auth/clients");
        Step(" → Create client → Application type: Desktop app → скопируйте Client ID и Client secret сюда.");
        _body.Children.Add(steps);

        var id = new TextBox { Style = (Style)Res["InputTextBox"], Text = account?.ClientId ?? "", Tag = "xxxxxxxx.apps.googleusercontent.com" };
        var secret = new TextBox { Style = (Style)Res["InputTextBox"], Text = account?.ClientSecret ?? "", Tag = "GOCSPX-…" };
        var full = new CheckBox
        {
            Content = "Полный доступ к Диску (видеть файлы, добавленные не через TaskManager)", FontSize = 12.5,
            IsChecked = account?.FullAccess ?? false, Margin = new Thickness(0, 12, 0, 0), Foreground = (Brush)Res["Text2"],
        };
        _body.Children.Add(Text("Client ID", 12.5, "Text2", 12));
        _body.Children.Add(id);
        _body.Children.Add(Text("Client secret", 12.5, "Text2", 10));
        _body.Children.Add(secret);
        _body.Children.Add(full);
        _body.Children.Add(Text("Полный доступ — «чувствительная» область: опубликовать такое приложение без проверки Google нельзя, вход придётся повторять раз в 7 дней.",
            11.5, "Text3", 4));

        var status = Text("", 12.5, "Text2", 10);
        _body.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 14) };
        var signIn = Btn("Войти через браузер", primary: true);
        var cancel = Btn("Закрыть");
        cancel.Click += (_, _) => Close();
        signIn.Click += async (_, _) =>
        {
            signIn.IsEnabled = false;
            status.Text = "Откройте браузер и подтвердите доступ… (окно можно не закрывать)";
            _signIn = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await _auth.SignIn(id.Text, secret.Text, full.IsChecked == true,
                    url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }), _signIn.Token);
                Build();
            }
            catch (OperationCanceledException) { status.Text = "Время ожидания истекло"; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException or System.Net.Sockets.SocketException)
            {
                status.Text = ex.Message;
                status.Foreground = Brushes.IndianRed;
            }
            finally
            {
                signIn.IsEnabled = true;
            }
        };
        buttons.Children.Add(signIn);
        buttons.Children.Add(cancel);
        _body.Children.Add(buttons);
    }
}
