using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartLibOfflineFb2;

internal static class Program
{
    [STAThread]
    static async Task Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (args.Length > 0)
        {
            int code = await CliRunner.RunAsync(args);
            Environment.Exit(code);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

internal static class AppConstants
{
    public const string BaseUrl = "https://smartlib.sytes.net";
    public const string ApiKeyHeaderName = "X-SmartLib-Key";
    public const string ApiKeyValue = "skibidi";
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}

#region Models
internal sealed class BookDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "Untitled";
    public string Author { get; set; } = "Unknown";
    public string Genre { get; set; } = "Unknown";
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("cover_url")] public string? CoverUrl { get; set; }
    public bool Available { get; set; } = true;
}

internal sealed class CatalogResponse
{
    public bool Ok { get; set; }
    public List<BookDto> Books { get; set; } = new();
    public List<string> Genres { get; set; } = new();
}

internal sealed class LibraryBook
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "Untitled";
    public string Author { get; set; } = "Unknown";
    public string Genre { get; set; } = "Unknown";
    public string FilePath { get; set; } = "";
}

internal sealed class AppSettings
{
    public string BooksDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartLib", "Books");
    public bool CheckUpdatesOnStart { get; set; } = true;
}

internal sealed class UpdateInfo
{
    public string Latest { get; set; } = "";
    public string Url { get; set; } = "";
    public string SilentArgs { get; set; } = "/S";
    public List<string> Changelog { get; set; } = new();
}

internal sealed class BookInfo
{
    public bool Ok { get; set; }
    public int Id { get; set; }
    public bool Available { get; set; }
    [JsonPropertyName("size_bytes")] public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? Download { get; set; }
}
#endregion

#region Core Services
internal interface ISmartLibHttp
{
    HttpClient Client { get; }
    HttpRequestMessage CreateRequest(HttpMethod method, string relativeOrAbsoluteUrl);
}

internal sealed class SmartLibHttp : ISmartLibHttp
{
    private readonly Uri _baseUri;
    public HttpClient Client { get; }

    public SmartLibHttp(string baseUrl)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        Client = SharedHttpClient.Instance;
    }

    public HttpRequestMessage CreateRequest(HttpMethod method, string relativeOrAbsoluteUrl)
    {
        Uri uri = Uri.TryCreate(relativeOrAbsoluteUrl, UriKind.Absolute, out var abs)
            ? abs!
            : new Uri(_baseUri, relativeOrAbsoluteUrl.TrimStart('/'));

        var req = new HttpRequestMessage(method, uri);
        req.Headers.TryAddWithoutValidation(AppConstants.ApiKeyHeaderName, AppConstants.ApiKeyValue);
        return req;
    }
}

internal static class SharedHttpClient
{
    public static readonly HttpClient Instance = CreateJsonClient();
    public static readonly HttpClient DownloadInstance = CreateDownloadClient();

    private static HttpClient CreateJsonClient()
    {
        var h = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("SmartLibOfflineFB2/1.0");
        return h;
    }

    private static HttpClient CreateDownloadClient()
    {
        var h = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("SmartLibOfflineFB2/1.0");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
        return h;
    }
}

internal interface ICatalogService
{
    Task<(List<BookDto> books, List<string> genres)> GetCatalogAsync(string search, string genre, CancellationToken ct);
}

internal sealed class CatalogService : ICatalogService
{
    private readonly ISmartLibHttp _http;
    public CatalogService(ISmartLibHttp http) => _http = http;

    public async Task<(List<BookDto> books, List<string> genres)> GetCatalogAsync(string search, string genre, CancellationToken ct)
    {
        try
        {
            return await FetchApiAsync(search, genre, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return (new List<BookDto>(), new List<string>());
        }
        catch (TaskCanceledException)
        {
            return (new List<BookDto>(), new List<string>());
        }
    }

    private async Task<(List<BookDto>, List<string>)> FetchApiAsync(string search, string genre, CancellationToken ct)
    {
        string path = $"api/books.php?search={Uri.EscapeDataString(search ?? "")}&genre={Uri.EscapeDataString(genre ?? "")}";
        using var req = _http.CreateRequest(HttpMethod.Get, path);
        using var res = await _http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("Catalog API unavailable.", null, res.StatusCode);

        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        CatalogResponse? response;
        try
        {
            response = await JsonSerializer.DeserializeAsync<CatalogResponse>(stream, AppConstants.Json, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("API returned unexpected JSON", ex);
        }

        if (response == null)
            throw new InvalidDataException("API returned unexpected JSON");

        if (!response.Ok)
            throw new InvalidDataException("Catalog API returned ok=false");

        var books = response.Books ?? new List<BookDto>();
        foreach (var b in books)
        {
            if (string.IsNullOrWhiteSpace(b.Title)) b.Title = "Untitled";
            if (string.IsNullOrWhiteSpace(b.Author)) b.Author = "Unknown";
            if (string.IsNullOrWhiteSpace(b.Genre)) b.Genre = "Unknown";
        }

        var genres = (response.Genres ?? new List<string>())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g)
            .ToList();
        return (books, genres);
    }
}


internal interface IDownloadService
{
    Task<BookInfo> GetBookInfoAsync(string baseUrl, int id, CancellationToken ct);
    Task DownloadBookFb2Async(string baseUrl, int id, string outputPath, IProgress<double>? progress, CancellationToken ct);
}

internal sealed class DownloadService : IDownloadService
{
    private readonly ISmartLibHttp _http;
    private readonly HttpClient _downloadClient = SharedHttpClient.DownloadInstance;
    public DownloadService(ISmartLibHttp http) => _http = http;

    public async Task<BookInfo> GetBookInfoAsync(string baseUrl, int id, CancellationToken ct)
    {
        using var req = _http.CreateRequest(HttpMethod.Get, $"api/book.php?id={id}");
        using var res = await _http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("Book info service unavailable or unauthorized.", null, res.StatusCode);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Book info HTTP {(int)res.StatusCode}", null, res.StatusCode);

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        BookInfo? info;
        try
        {
            info = await JsonSerializer.DeserializeAsync<BookInfo>(stream, AppConstants.Json, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Book info returned invalid JSON.", ex);
        }

        if (info == null)
            throw new InvalidDataException("Book info is empty.");

        return info;
    }

    public async Task DownloadBookFb2Async(string baseUrl, int id, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await GetBookInfoAsync(baseUrl, id, ct).ConfigureAwait(false);
        if (!info.Ok || !info.Available)
            throw new InvalidOperationException("Book is not available for download.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        long existingSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
        if (info.SizeBytes.HasValue && existingSize == info.SizeBytes.Value)
        {
            try
            {
                await ValidateFileAsync(outputPath, info, ct).ConfigureAwait(false);
                progress?.Report(1.0);
                return;
            }
            catch (InvalidDataException)
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                existingSize = 0;
            }
        }

        bool append = existingSize > 0 && (!info.SizeBytes.HasValue || existingSize < info.SizeBytes.Value);
        using var req = _http.CreateRequest(HttpMethod.Get, $"api/download.php?id={id}");
        req.Headers.AcceptEncoding.Clear();
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (append)
            req.Headers.Range = new RangeHeaderValue(existingSize, null);

        using var res = await _downloadClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("Download service unavailable or unauthorized.", null, res.StatusCode);

        if (res.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (info.SizeBytes.HasValue && existingSize >= info.SizeBytes.Value)
            {
                await ValidateFileAsync(outputPath, info, ct).ConfigureAwait(false);
                progress?.Report(1.0);
                return;
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);
            existingSize = 0;
            append = false;
        }

        if (res.StatusCode == HttpStatusCode.PartialContent)
        {
            append = append;
        }
        else if (res.IsSuccessStatusCode)
        {
            if (append && res.StatusCode == HttpStatusCode.OK)
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
            append = false;
            existingSize = 0;
        }
        else
        {
            throw new HttpRequestException($"Download HTTP {(int)res.StatusCode}", null, res.StatusCode);
        }

        long totalExpected = info.SizeBytes ?? ((append ? existingSize : 0) + (res.Content.Headers.ContentLength ?? 0));
        long written = append ? existingSize : 0;

        await using (var input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(outputPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
        {
            var buffer = new byte[81920];
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (totalExpected > 0)
                    progress?.Report(Math.Min(1.0, written / (double)totalExpected));
            }
        }

        await ValidateFileAsync(outputPath, info, ct).ConfigureAwait(false);
        progress?.Report(1.0);
    }

    private static async Task ValidateFileAsync(string path, BookInfo info, CancellationToken ct)
    {
        var fileInfo = new FileInfo(path);
        if (info.SizeBytes.HasValue && fileInfo.Length != info.SizeBytes.Value)
            throw new InvalidDataException($"Size mismatch after download. Expected {info.SizeBytes.Value}, got {fileInfo.Length}.");

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            string expected = info.Sha256.Trim().ToLowerInvariant();
            await using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            string actual = Convert.ToHexString(hash).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 mismatch after download.");
        }
    }
}

internal interface ILocalLibraryService
{
    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);
    List<LibraryBook> LoadLibrary();
    void SaveLibrary(List<LibraryBook> books);
    void UpsertBook(LibraryBook book);
    void RemoveBook(string filePath);
    string SettingsPath { get; }
    string LibraryPath { get; }
}

internal sealed class LocalLibraryService : ILocalLibraryService
{
    public string RootDir { get; }
    public string SettingsPath => Path.Combine(RootDir, "settings.json");
    public string LibraryPath => Path.Combine(RootDir, "library.json");

    public LocalLibraryService()
    {
        RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartLib");
        Directory.CreateDirectory(RootDir);
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var s = new AppSettings();
                SaveSettings(s);
                return s;
            }
            var txt = File.ReadAllText(SettingsPath);
            var result = JsonSerializer.Deserialize<AppSettings>(txt, AppConstants.Json) ?? new AppSettings();
            if (string.IsNullOrWhiteSpace(result.BooksDir))
                result.BooksDir = new AppSettings().BooksDir;
            Directory.CreateDirectory(result.BooksDir);
            return result;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        Directory.CreateDirectory(settings.BooksDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, AppConstants.Json));
    }

    public List<LibraryBook> LoadLibrary()
    {
        try
        {
            if (!File.Exists(LibraryPath)) return new List<LibraryBook>();
            var txt = File.ReadAllText(LibraryPath);
            var list = JsonSerializer.Deserialize<List<LibraryBook>>(txt, AppConstants.Json) ?? new List<LibraryBook>();
            foreach (var b in list)
            {
                if (string.IsNullOrWhiteSpace(b.Title))
                    b.Title = Path.GetFileNameWithoutExtension(b.FilePath);
            }
            return list;
        }
        catch
        {
            return new List<LibraryBook>();
        }
    }

    public void SaveLibrary(List<LibraryBook> books)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath)!);
        File.WriteAllText(LibraryPath, JsonSerializer.Serialize(books, AppConstants.Json));
    }

    public void UpsertBook(LibraryBook book)
    {
        var list = LoadLibrary();
        int idx = list.FindIndex(x => string.Equals(x.FilePath, book.FilePath, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) list[idx] = book; else list.Add(book);
        SaveLibrary(list);
    }

    public void RemoveBook(string filePath)
    {
        var list = LoadLibrary();
        list = list.Where(x => !string.Equals(x.FilePath, filePath, StringComparison.OrdinalIgnoreCase)).ToList();
        SaveLibrary(list);
    }
}

internal interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct);
    Task<string> DownloadInstallerAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct);
}

internal sealed class UpdateService : IUpdateService
{
    private readonly ISmartLibHttp _http;
    public UpdateService(ISmartLibHttp http) => _http = http;

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct)
    {
        using var req = _http.CreateRequest(HttpMethod.Get, "app/version.json");
        using var res = await _http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound)
            return null;

        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        UpdateInfo? info;
        try
        {
            info = await JsonSerializer.DeserializeAsync<UpdateInfo>(stream, AppConstants.Json, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (info == null || string.IsNullOrWhiteSpace(info.Latest) || string.IsNullOrWhiteSpace(info.Url))
            return null;

        if (string.IsNullOrWhiteSpace(info.SilentArgs))
            info.SilentArgs = "/S";

        var current = GetCurrentVersion();
        return IsNewer(info.Latest, current) ? info : null;
    }

    public async Task<string> DownloadInstallerAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"SmartLibSetup_{Guid.NewGuid():N}.exe");
        using var req = _http.CreateRequest(HttpMethod.Get, info.Url);
        using var res = await _http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        long total = res.Content.Headers.ContentLength ?? 0;
        long written = 0;

        await using var input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true);
        var buffer = new byte[81920];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            if (total > 0) progress?.Report(Math.Min(1.0, written / (double)total));
        }

        progress?.Report(1.0);
        return tempPath;
    }

    private static string GetCurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
            return info.Split('+')[0];
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static bool IsNewer(string latest, string current)
    {
        Version lv = ParseVersion(latest);
        Version cv = ParseVersion(current);
        return lv > cv;
    }

    private static Version ParseVersion(string v)
    {
        var normalized = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(normalized, out var parsed) ? parsed : new Version(0, 0, 0, 0);
    }
}
#endregion

#region CLI
internal static class CliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var http = new SmartLibHttp(AppConstants.BaseUrl);
        var download = new DownloadService(http);

        if (args.Length >= 2 && string.Equals(args[0], "info", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int infoId))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                var info = await download.GetBookInfoAsync(AppConstants.BaseUrl, infoId, cts.Token);
                Console.WriteLine(JsonSerializer.Serialize(info, AppConstants.Json));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        if (args.Length >= 3 && string.Equals(args[0], "download", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dlId))
        {
            string output = args[2];
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var progress = new Progress<double>(p => Console.WriteLine($"Progress: {p:P0}"));
            try
            {
                await download.DownloadBookFb2Async(AppConstants.BaseUrl, dlId, output, progress, cts.Token);
                Console.WriteLine("Done");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  smartlib.exe info <id>");
        Console.WriteLine("  smartlib.exe download <id> <outputPath>");
        return 2;
    }
}
#endregion

#region UI
internal sealed class MainForm : Form
{
    private readonly ISmartLibHttp _http = new SmartLibHttp(AppConstants.BaseUrl);
    private readonly ICatalogService _catalog;
    private readonly IDownloadService _download;
    private readonly ILocalLibraryService _library;
    private readonly IUpdateService _updates;

    private AppSettings _settings;

    private readonly Panel _sidebar = new();
    private readonly Panel _topBar = new();
    private readonly Panel _content = new();
    private readonly Panel _onlinePage = new();
    private readonly Panel _libraryPage = new();
    private readonly Panel _settingsPage = new();
    private readonly FlowLayoutPanel _onlineFlow = new();
    private readonly FlowLayoutPanel _libraryFlow = new();

    private readonly NavButton _btnOnline = new() { Text = "Online" };
    private readonly NavButton _btnLibrary = new() { Text = "Library" };
    private readonly NavButton _btnSettings = new() { Text = "Settings" };

    private readonly TextBox _txtSearch = new();
    private readonly ComboBox _cmbGenre = new();
    private readonly Button _btnRefresh = Theme.CreateButton("Refresh");
    private readonly Button _btnCheckUpdatesTop = Theme.CreateButton("Check Updates");

    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _globalProgress = new() { Minimum = 0, Maximum = 100, Width = 200, Visible = false };

    private readonly TextBox _txtBooksDir = new();
    private readonly CheckBox _chkUpdates = new() { Text = "Check updates on start", AutoSize = true };
    private readonly Button _btnBrowseBooksDir = Theme.CreateButton("Browse");
    private readonly Button _btnSaveSettings = Theme.CreateButton("Save Settings");
    private readonly Button _btnCheckUpdatesSettings = Theme.CreateButton("Check Updates Now");
    private readonly System.Windows.Forms.Timer _searchDebounceTimer = new() { Interval = 500 };

    private CancellationTokenSource? _catalogCts;
    private int _refreshInProgress;
    private bool _suppressGenreEvents;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private DateTime _lastUpdateAttemptUtc = DateTime.MinValue;

    public MainForm()
    {
        _catalog = new CatalogService(_http);
        _download = new DownloadService(_http);
        _library = new LocalLibraryService();
        _updates = new UpdateService(_http);
        _settings = _library.LoadSettings();

        Text = "SmartLib Offline FB2";
        MinimumSize = new Size(1000, 700);
        BackColor = Theme.BackColor;
        Font = Theme.Font;

        BuildLayout();
        WireEvents();
        ShowPage(_onlinePage, _btnOnline);

        Load += async (_, _) =>
        {
            DebugLog("Refresh triggered by: Form Load");
            await RefreshCatalogAsync();
            RefreshLibraryUI();
        };
    }

    private void BuildLayout()
    {
        SuspendLayout();

        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = 210;
        _sidebar.BackColor = Color.White;
        _sidebar.Padding = new Padding(16);

        var logo = new PictureBox { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        string logoPath = Path.Combine(AppContext.BaseDirectory, "logo.png");
        if (File.Exists(logoPath))
        {
            try { logo.Image = Image.FromFile(logoPath); } catch { }
        }

        var title = new Label { Text = "SmartLib", Font = new Font("Segoe UI", 16, FontStyle.Bold), AutoSize = true, ForeColor = Theme.TextPrimary };
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        head.Controls.Add(logo);
        head.Controls.Add(title);

        var navPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 220, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 20, 0, 0) };
        foreach (var b in new[] { _btnOnline, _btnLibrary, _btnSettings })
        {
            b.Width = 170;
            b.Margin = new Padding(0, 0, 0, 10);
            navPanel.Controls.Add(b);
        }

        _sidebar.Controls.Add(navPanel);
        _sidebar.Controls.Add(head);

        _topBar.Dock = DockStyle.Top;
        _topBar.Height = 54;
        _topBar.BackColor = Theme.BackColor;
        _topBar.Padding = new Padding(12, 10, 12, 10);

        _txtSearch.PlaceholderText = "Search books...";
        _txtSearch.Width = 280;
        _txtSearch.BorderStyle = BorderStyle.FixedSingle;

        _cmbGenre.Width = 170;
        _cmbGenre.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbGenre.Items.Add("All genres");
        _cmbGenre.SelectedIndex = 0;

        var topLeft = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        topLeft.Controls.AddRange(new Control[] { _txtSearch, _cmbGenre, _btnRefresh });

        var topRight = new Panel { Dock = DockStyle.Right, Width = 170 };
        _btnCheckUpdatesTop.Dock = DockStyle.Fill;
        topRight.Controls.Add(_btnCheckUpdatesTop);

        _topBar.Controls.Add(topRight);
        _topBar.Controls.Add(topLeft);

        _content.Dock = DockStyle.Fill;
        _content.Padding = new Padding(14, 6, 14, 8);
        _content.BackColor = Theme.BackColor;

        _onlinePage.Dock = DockStyle.Fill;
        _libraryPage.Dock = DockStyle.Fill;
        _settingsPage.Dock = DockStyle.Fill;

        _onlineFlow.Dock = DockStyle.Fill;
        _onlineFlow.AutoScroll = true;
        _onlineFlow.WrapContents = true;
        _onlineFlow.Padding = new Padding(4);
        _onlineFlow.BackColor = Theme.BackColor;
        _onlinePage.Controls.Add(_onlineFlow);

        _libraryFlow.Dock = DockStyle.Fill;
        _libraryFlow.AutoScroll = true;
        _libraryFlow.WrapContents = true;
        _libraryFlow.Padding = new Padding(4);
        _libraryFlow.BackColor = Theme.BackColor;
        _libraryPage.Controls.Add(_libraryFlow);

        BuildSettingsPage();
        _content.Controls.AddRange(new Control[] { _onlinePage, _libraryPage, _settingsPage });

        _statusStrip.SizingGrip = false;
        _statusStrip.Items.Add(_status);
        _statusStrip.Items.Add(_globalProgress);

        Controls.Add(_content);
        Controls.Add(_topBar);
        Controls.Add(_sidebar);
        Controls.Add(_statusStrip);

        ResumeLayout();
    }

    private void BuildSettingsPage()
    {
        var card = new Panel
        {
            Dock = DockStyle.Top,
            Height = 210,
            BackColor = Color.White,
            Padding = new Padding(16),
            Margin = new Padding(0),
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblDir = new Label { Text = "Books folder", AutoSize = true };
        _txtBooksDir.Width = 460;
        _txtBooksDir.Text = _settings.BooksDir;
        _chkUpdates.Checked = _settings.CheckUpdatesOnStart;

        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false };
        row1.Controls.AddRange(new Control[] { _txtBooksDir, _btnBrowseBooksDir });

        var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        row2.Controls.Add(_chkUpdates);

        var row3 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        row3.Controls.AddRange(new Control[] { _btnSaveSettings, _btnCheckUpdatesSettings });

        card.Controls.Add(row3);
        card.Controls.Add(row2);
        card.Controls.Add(row1);
        card.Controls.Add(lblDir);

        _settingsPage.Padding = new Padding(8);
        _settingsPage.Controls.Add(card);
    }

    private void WireEvents()
    {
        _btnOnline.Click += (_, _) => ShowPage(_onlinePage, _btnOnline);
        _btnLibrary.Click += (_, _) => { ShowPage(_libraryPage, _btnLibrary); RefreshLibraryUI(); };
        _btnSettings.Click += (_, _) => ShowPage(_settingsPage, _btnSettings);

        _btnRefresh.Click += async (_, _) => { _searchDebounceTimer.Stop(); DebugLog("Refresh triggered by: Refresh button"); await RefreshCatalogAsync(); };
        _txtSearch.TextChanged += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        };
        _searchDebounceTimer.Tick += async (_, _) =>
        {
            _searchDebounceTimer.Stop();
            DebugLog("Refresh triggered by: Search debounce");
            await RefreshCatalogAsync();
        };
        _txtSearch.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _searchDebounceTimer.Stop(); DebugLog("Refresh triggered by: Search Enter"); await RefreshCatalogAsync(); } };
        _cmbGenre.SelectionChangeCommitted += async (_, _) => { if (_suppressGenreEvents) return; _searchDebounceTimer.Stop(); DebugLog("Refresh triggered by: Genre SelectionChangeCommitted"); await RefreshCatalogAsync(); };

        _btnCheckUpdatesTop.Click += async (_, _) => await CheckUpdatesAsync(true);
        _btnCheckUpdatesSettings.Click += async (_, _) => await CheckUpdatesAsync(true);

        _btnBrowseBooksDir.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { SelectedPath = _txtBooksDir.Text };
            if (fbd.ShowDialog(this) == DialogResult.OK)
                _txtBooksDir.Text = fbd.SelectedPath;
        };

        _btnSaveSettings.Click += (_, _) =>
        {
            _settings.BooksDir = _txtBooksDir.Text.Trim();
            _settings.CheckUpdatesOnStart = _chkUpdates.Checked;
            _library.SaveSettings(_settings);
            SetStatus("Settings saved.");
        };
    }

    private void DebugLog(string msg)
    {
        Debug.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    private void ShowPage(Panel page, NavButton nav)
    {
        _onlinePage.Visible = _libraryPage.Visible = _settingsPage.Visible = false;
        page.Visible = true;
        _btnOnline.IsActive = nav == _btnOnline;
        _btnLibrary.IsActive = nav == _btnLibrary;
        _btnSettings.IsActive = nav == _btnSettings;
    }

    private async Task RefreshCatalogAsync()
    {
        if (Interlocked.Exchange(ref _refreshInProgress, 1) == 1)
            return;

        var previousCts = _catalogCts;
        previousCts?.Cancel();
        previousCts?.Dispose();
        _catalogCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = _catalogCts.Token;

        string search = _txtSearch.Text.Trim();
        string genre = _cmbGenre.SelectedIndex <= 0 ? "" : _cmbGenre.SelectedItem?.ToString() ?? "";
        DebugLog($"RefreshCatalogAsync START. search='{search}' genre='{genre}'");

        try
        {
            SetStatus("Loading catalog...");
            SetProgressVisible(true);
            SetProgress(5);

            var (books, genres) = await _catalog.GetCatalogAsync(search, genre, ct);
            SetProgress(60);
            RefreshGenreList(genres, genre);
            BuildOnlineCards(books);
            SetProgress(100);
            SetStatus(books.Count == 0 ? "Catalog API returned no books." : $"Loaded {books.Count} books.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Catalog request canceled.");
        }
        catch (Exception ex)
        {
            SetStatus(string.Equals(ex.Message, "API returned unexpected JSON", StringComparison.Ordinal) ? "Catalog API error: unexpected JSON" : (string.Equals(ex.Message, "Catalog API returned ok=false", StringComparison.Ordinal) ? "Catalog API returned ok=false" : "Catalog unavailable: " + ex.Message));
            BuildOnlineCards(new List<BookDto>());
        }
        finally
        {
            await Task.Delay(150);
            SetProgressVisible(false);
            Interlocked.Exchange(ref _refreshInProgress, 0);
        }
    }

    private void RefreshGenreList(List<string> genres, string selected)
    {
        var prev = selected;
        _suppressGenreEvents = true;
        _cmbGenre.BeginUpdate();
        try
        {
            _cmbGenre.Items.Clear();
            _cmbGenre.Items.Add("All genres");
            foreach (var g in genres)
                _cmbGenre.Items.Add(g);

            int idx = _cmbGenre.Items.IndexOf(prev);
            _cmbGenre.SelectedIndex = idx >= 0 ? idx : 0;
        }
        finally
        {
            _cmbGenre.EndUpdate();
            _suppressGenreEvents = false;
        }
    }

    private void BuildOnlineCards(List<BookDto> books)
    {
        _onlineFlow.SuspendLayout();
        _onlineFlow.Controls.Clear();

        foreach (var book in books)
        {
            var card = new BookCardControl();
            Debug.WriteLine($"CARD title={book.Title} id={book.Id} dl={book.DownloadUrl}");
            bool canDownload = book.Id > 0 && book.Available;
            card.SetData(book.Title, book.Author, book.Genre, canDownload ? "Download" : "No ID");
            card.SetActionEnabled(canDownload);
            card.ActionClicked += async (_, _) => await DownloadBookFromCardAsync(book, card);
            _onlineFlow.Controls.Add(card);
        }

        _onlineFlow.ResumeLayout();
    }

    private async Task DownloadBookFromCardAsync(BookDto book, BookCardControl card)
    {
        Debug.WriteLine($"CLICK id={book.Id} title={book.Title}");
        if (book.Id <= 0 || !book.Available)
        {
            SetStatus("Download unavailable: book id is missing or unavailable.");
            return;
        }

        string fileName = SanitizeFileName($"{book.Id} - {book.Title} - {book.Author}.fb2");
        string path = Path.Combine(_settings.BooksDir, fileName);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var progress = new Progress<double>(p =>
        {
            int v = (int)Math.Round(p * 100);
            card.SetProgress(v);
            SetProgress(v);
        });

        try
        {
            SetProgressVisible(true);
            SetStatus($"Downloading '{book.Title}'...");
            card.SetBusy(true);
            await _download.DownloadBookFb2Async(AppConstants.BaseUrl, book.Id, path, progress, cts.Token);

            _library.UpsertBook(new LibraryBook
            {
                Id = book.Id.ToString(CultureInfo.InvariantCulture),
                Title = book.Title,
                Author = book.Author,
                Genre = book.Genre,
                FilePath = path
            });

            card.SetProgress(100);
            SetStatus($"Downloaded: {book.Title}");
        }
        catch (Exception ex)
        {
            SetStatus("Download failed: " + ex.Message);
        }
        finally
        {
            card.SetBusy(false);
            await Task.Delay(150);
            SetProgressVisible(false);
            RefreshLibraryUI();
        }
    }

    private void RefreshLibraryUI()
    {
        var books = _library.LoadLibrary().Where(b => File.Exists(b.FilePath)).ToList();
        if (books.Count == 0)
        {
            var all = _library.LoadLibrary();
            if (all.Count != 0)
                _library.SaveLibrary(books);
        }

        _libraryFlow.SuspendLayout();
        _libraryFlow.Controls.Clear();

        foreach (var b in books)
        {
            var card = new BookCardControl();
            card.SetData(string.IsNullOrWhiteSpace(b.Title) ? Path.GetFileNameWithoutExtension(b.FilePath) : b.Title, b.Author, b.Genre, "Open");

            var btnDelete = Theme.CreateButton("Delete");
            var btnFolder = Theme.CreateButton("Folder");
            btnDelete.Width = 72;
            btnFolder.Width = 72;
            card.AddExtraButtons(btnFolder, btnDelete);

            card.ActionClicked += (_, _) => OpenReaderSafe(b.FilePath);
            btnFolder.Click += (_, _) => OpenFolderSafe(Path.GetDirectoryName(b.FilePath) ?? _settings.BooksDir);
            btnDelete.Click += (_, _) =>
            {
                try
                {
                    if (File.Exists(b.FilePath)) File.Delete(b.FilePath);
                    _library.RemoveBook(b.FilePath);
                    RefreshLibraryUI();
                    SetStatus("Deleted file.");
                }
                catch (Exception ex)
                {
                    SetStatus("Delete failed: " + ex.Message);
                }
            };

            _libraryFlow.Controls.Add(card);
        }

        _libraryFlow.ResumeLayout();
    }

    private async Task CheckUpdatesAsync(bool userInitiated)
    {
        if (DateTime.UtcNow - _lastUpdateAttemptUtc < TimeSpan.FromSeconds(2))
            return;

        if (!await _updateLock.WaitAsync(0))
            return;

        _lastUpdateAttemptUtc = DateTime.UtcNow;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            SetStatus("Checking for updates...");
            SetProgressVisible(true);
            SetProgress(10);

            var info = await _updates.CheckForUpdateAsync(cts.Token);
            SetProgress(70);

            if (info == null)
            {
                SetStatus("Update service unavailable");
                return;
            }

            var changelog = info.Changelog?.Count > 0 ? string.Join(Environment.NewLine + "• ", info.Changelog) : "No changelog";
            var text = $"New version {info.Latest} is available.{Environment.NewLine}{Environment.NewLine}• {changelog}{Environment.NewLine}{Environment.NewLine}Install now?";
            if (MessageBox.Show(this, text, "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            {
                SetStatus("Update postponed.");
                return;
            }

            SetStatus("Downloading update installer...");
            var progress = new Progress<double>(p => SetProgress((int)Math.Round(p * 100)));
            string installer = await _updates.DownloadInstallerAsync(info, progress, cts.Token);

            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = string.IsNullOrWhiteSpace(info.SilentArgs) ? "/S" : info.SilentArgs,
                UseShellExecute = true
            });

            Close();
        }
        catch (HttpRequestException)
        {
            SetStatus("Update service unavailable");
        }
        catch (TaskCanceledException)
        {
            SetStatus("Update service unavailable");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Update service unavailable");
        }
        catch (Exception)
        {
            SetStatus("Update service unavailable");
        }
        finally
        {
            await Task.Delay(150);
            SetProgressVisible(false);
            _updateLock.Release();
        }
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) BeginInvoke(() => _status.Text = text);
        else _status.Text = text;
    }

    private void SetProgress(int percent)
    {
        if (IsDisposed || Disposing) return;
        percent = Math.Clamp(percent, 0, 100);

        void Apply()
        {
            if (IsDisposed || Disposing) return;
            if (!_globalProgress.Visible)
            {
                if (_globalProgress.Value != 0) _globalProgress.Value = 0;
                return;
            }

            if (_globalProgress.Value != percent)
                _globalProgress.Value = percent;
        }

        if (InvokeRequired) BeginInvoke((Action)Apply);
        else Apply();
    }

    private void SetProgressVisible(bool visible)
    {
        if (IsDisposed || Disposing) return;

        void Apply()
        {
            if (IsDisposed || Disposing) return;
            if (_globalProgress.Visible != visible)
                _globalProgress.Visible = visible;

            if (!visible && _globalProgress.Value != 0)
                _globalProgress.Value = 0;
        }

        if (InvokeRequired) BeginInvoke((Action)Apply);
        else Apply();
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private void OpenReaderSafe(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                SetStatus("Unable to open file: file not found.");
                return;
            }

            var reader = new Fb2ReaderForm(path);
            reader.Show(this);
        }
        catch (Exception ex)
        {
            SetStatus("Unable to open file: " + ex.Message);
        }
    }

    private void OpenFolderSafe(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Unable to open folder: " + ex.Message);
        }
    }
}

internal sealed class NavButton : Button
{
    private bool _hover;
    public bool IsActive { get; set; }

    public NavButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Height = 40;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(14, 0, 0, 0);
        Font = new Font("Segoe UI", 10f, FontStyle.Regular);
        BackColor = Color.Transparent;
        ForeColor = Theme.TextPrimary;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.Clear(IsActive ? Color.FromArgb(227, 236, 250) : _hover ? Color.FromArgb(241, 244, 249) : Color.Transparent);
        if (IsActive)
        {
            using var b = new SolidBrush(Theme.Accent);
            g.FillRectangle(b, 0, 0, 4, Height);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class BookCardControl : Panel
{
    private readonly Label _lblTitle = new();
    private readonly Label _lblAuthor = new();
    private readonly Label _lblGenre = new();
    private readonly Button _btnAction = Theme.CreateButton("Action");
    private readonly ProgressBar _progress = new() { Width = 180, Height = 8, Style = ProgressBarStyle.Continuous, Visible = false };
    private readonly FlowLayoutPanel _buttonRow = new() { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };

    public event EventHandler? ActionClicked;

    public BookCardControl()
    {
        Width = 290;
        Height = 160;
        Margin = new Padding(8);
        Padding = new Padding(12);
        BackColor = Color.White;
        BorderStyle = BorderStyle.FixedSingle;

        _lblTitle.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _lblTitle.AutoSize = false;
        _lblTitle.Width = 250;
        _lblTitle.Height = 40;

        _lblAuthor.AutoSize = true;
        _lblGenre.AutoSize = true;

        _btnAction.Width = 88;
        _btnAction.Click += (_, _) => ActionClicked?.Invoke(this, EventArgs.Empty);

        _buttonRow.Controls.Add(_btnAction);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));

        layout.Controls.Add(_lblTitle, 0, 0);
        layout.Controls.Add(_lblAuthor, 0, 1);
        layout.Controls.Add(_lblGenre, 0, 2);
        layout.Controls.Add(_buttonRow, 0, 3);
        layout.Controls.Add(_progress, 0, 4);

        Controls.Add(layout);
    }

    public void SetData(string title, string author, string genre, string actionText)
    {
        _lblTitle.Text = title;
        _lblAuthor.Text = "Author: " + author;
        _lblGenre.Text = "Genre: " + genre;
        _btnAction.Text = actionText;
    }

    public void AddExtraButtons(params Control[] controls)
    {
        foreach (var c in controls)
            _buttonRow.Controls.Add(c);
    }

    public void SetBusy(bool busy)
    {
        _btnAction.Enabled = !busy;
        _progress.Visible = busy;
    }

    public void SetActionEnabled(bool enabled)
    {
        _btnAction.Enabled = enabled;
    }

    public void SetProgress(int percent)
    {
        _progress.Visible = true;
        _progress.Value = Math.Clamp(percent, 0, 100);
    }
}

internal static class Theme
{
    public static readonly Color BackColor = ColorTranslator.FromHtml("#F7F8FA");
    public static readonly Color Accent = ColorTranslator.FromHtml("#2B5AA6");
    public static readonly Color TextPrimary = ColorTranslator.FromHtml("#1E2430");
    public static readonly Font Font = new("Segoe UI", 9f, FontStyle.Regular);

    public static Button CreateButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            Height = 32,
            Width = 110,
            BackColor = Accent,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };
        b.FlatAppearance.BorderSize = 0;
        b.MouseEnter += (_, _) => b.BackColor = ControlPaint.Dark(Accent, 0.08f);
        b.MouseLeave += (_, _) => b.BackColor = Accent;
        b.MouseDown += (_, _) => b.BackColor = ControlPaint.Dark(Accent, 0.16f);
        b.MouseUp += (_, _) => b.BackColor = ControlPaint.Dark(Accent, 0.08f);
        return b;
    }
}
#endregion
