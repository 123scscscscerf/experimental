using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace SmartLibOfflineFb2;

internal sealed class Fb2Book
{
    public string Title { get; set; } = "Untitled";
    public string Author { get; set; } = "Unknown";
    public string Annotation { get; set; } = string.Empty;
    public List<string> Pages { get; set; } = new();
}

internal static class Fb2Parser
{
    private static readonly XNamespace Ns = "http://www.gribuser.ru/xml/fictionbook/2.0";

    public static Fb2Book Parse(string filePath)
    {
        if (!File.Exists(filePath))
            throw new InvalidDataException("FB2 file not found.");

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length < 200)
            throw new InvalidDataException("FB2 file too small / not a real book");

        string head = ReadHeadText(filePath, 2048);
        if (head.IndexOf("<FictionBook", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidDataException("Not an FB2/XML file (missing FictionBook root).");

        try
        {
            var doc = LoadDocumentWithReader(filePath);
            return BuildBook(doc, filePath);
        }
        catch (Exception ex) when (NeedsEncodingFallback(ex))
        {
            try
            {
                string xmlText = ReadXmlTextWithDeclaredEncoding(filePath, head);
                xmlText = NormalizeXmlText(xmlText);
                var doc = XDocument.Parse(xmlText, LoadOptions.PreserveWhitespace);
                return BuildBook(doc, filePath);
            }
            catch (XmlException fallbackEx) when (LooksCorruptedByTailBytes(fallbackEx))
            {
                throw new InvalidDataException("File looks corrupted, downloaded size mismatch or extra bytes", fallbackEx);
            }
            catch (Exception fallbackEx)
            {
                throw new InvalidDataException("Invalid FB2 XML: " + fallbackEx.Message, fallbackEx);
            }
        }
        catch (XmlException ex) when (LooksCorruptedByTailBytes(ex))
        {
            throw new InvalidDataException("File looks corrupted, downloaded size mismatch or extra bytes", ex);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("Invalid FB2 XML: " + ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Invalid FB2 XML: " + ex.Message, ex);
        }
    }

    private static bool NeedsEncodingFallback(Exception ex)
    {
        if (ex is ArgumentException) return true;
        if (ex is XmlException xex)
        {
            var msg = xex.Message ?? string.Empty;
            return msg.Contains("encoding", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("windows-1251", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("koi8-r", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool LooksCorruptedByTailBytes(XmlException ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("root level is invalid", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("multiple root elements", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("end of file", StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument LoadDocumentWithReader(string filePath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = false,
            IgnoreWhitespace = false,
            CloseInput = true
        };

        using var fs = File.OpenRead(filePath);
        using var reader = XmlReader.Create(fs, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string ReadHeadText(string filePath, int bytes)
    {
        using var fs = File.OpenRead(filePath);
        int count = (int)Math.Min(bytes, fs.Length);
        var buffer = new byte[count];
        int read = fs.Read(buffer, 0, count);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private static string ReadXmlTextWithDeclaredEncoding(string filePath, string head)
    {
        Encoding enc = Encoding.UTF8;
        if (head.Contains("windows-1251", StringComparison.OrdinalIgnoreCase))
            enc = Encoding.GetEncoding(1251);
        else if (head.Contains("koi8-r", StringComparison.OrdinalIgnoreCase))
            enc = Encoding.GetEncoding("koi8-r");

        using var sr = new StreamReader(filePath, enc, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    private static string NormalizeXmlText(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return xml;
        xml = xml.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        int firstTag = xml.IndexOf('<');
        return firstTag > 0 ? xml[firstTag..] : xml;
    }

    private static Fb2Book BuildBook(XDocument doc, string filePath)
    {
        var root = doc.Root ?? throw new InvalidDataException("Invalid FB2 XML: missing root element.");
        if (!string.Equals(root.Name.LocalName, "FictionBook", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Not an FB2/XML file (missing FictionBook root).");

        var titleInfo = root.Element(Ns + "description")?.Element(Ns + "title-info");
        var annotation = titleInfo?.Element(Ns + "annotation");
        var allParagraphs = new List<string>();

        var mainBody = root.Elements(Ns + "body")
            .FirstOrDefault(b => string.IsNullOrWhiteSpace((string?)b.Attribute("name")));

        if (mainBody != null)
        {
            foreach (var section in mainBody.Elements(Ns + "section"))
            {
                string? chapterTitle = ExtractSectionTitle(section);
                if (!string.IsNullOrWhiteSpace(chapterTitle))
                    allParagraphs.Add(chapterTitle);

                foreach (var p in section.Descendants(Ns + "p"))
                {
                    var txt = Normalize(p.Value);
                    if (!string.IsNullOrWhiteSpace(txt))
                        allParagraphs.Add(txt);
                }
            }
        }

        var book = new Fb2Book
        {
            Title = titleInfo?.Element(Ns + "book-title")?.Value?.Trim() is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(filePath),
            Author = ExtractAuthors(titleInfo),
            Annotation = annotation == null ? string.Empty : string.Join(Environment.NewLine, annotation.Descendants(Ns + "p").Select(p => Normalize(p.Value)).Where(v => !string.IsNullOrWhiteSpace(v))),
            Pages = BuildPages(allParagraphs, 3000)
        };

        if (book.Pages.Count == 0)
            book.Pages.Add("No readable content found.");

        return book;
    }

    private static List<string> BuildPages(List<string> paragraphs, int targetChars)
    {
        var pages = new List<string>();
        var sb = new StringBuilder(targetChars + 512);

        foreach (var p in paragraphs)
        {
            if (sb.Length > 0 && sb.Length + p.Length > targetChars)
            {
                pages.Add(sb.ToString().Trim());
                sb.Clear();
            }

            if (sb.Length > 0)
                sb.AppendLine().AppendLine();
            sb.Append(p);
        }

        if (sb.Length > 0)
            pages.Add(sb.ToString().Trim());

        return pages;
    }

    private static string? ExtractSectionTitle(XElement section)
    {
        var titleNode = section.Element(Ns + "title");
        if (titleNode == null) return null;
        var txt = string.Join(" ", titleNode.Elements(Ns + "p").Select(p => Normalize(p.Value)).Where(v => !string.IsNullOrWhiteSpace(v)));
        return string.IsNullOrWhiteSpace(txt) ? null : txt;
    }

    private static string ExtractAuthors(XElement? titleInfo)
    {
        if (titleInfo == null) return "Unknown";
        var names = new List<string>();
        foreach (var a in titleInfo.Elements(Ns + "author"))
        {
            var parts = new[]
            {
                Normalize((string?)a.Element(Ns + "first-name") ?? string.Empty),
                Normalize((string?)a.Element(Ns + "middle-name") ?? string.Empty),
                Normalize((string?)a.Element(Ns + "last-name") ?? string.Empty),
                Normalize((string?)a.Element(Ns + "nickname") ?? string.Empty)
            }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

            if (parts.Length > 0)
                names.Add(string.Join(" ", parts));
        }

        return names.Count > 0 ? string.Join(", ", names) : "Unknown";
    }

    private static string Normalize(string? s) => (s ?? string.Empty).Replace("\r", "").Trim();
}

internal sealed class Fb2ReaderForm : Form
{
    private readonly string _filePath;
    private readonly Label _lblHeader = new() { AutoSize = false, Height = 46, Dock = DockStyle.Top };
    private readonly Label _lblPage = new() { AutoSize = false, Height = 28, Dock = DockStyle.Top };
    private readonly RichTextBox _reader = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ReadOnly = true, DetectUrls = false };
    private readonly ComboBox _cmbFont = new() { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _cmbFontSize = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _cmbTheme = new() { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _btnPrev = Theme.CreateButton("Prev");
    private readonly Button _btnNext = Theme.CreateButton("Next");

    private Fb2Book? _book;
    private int _pageIndex;
    private CancellationTokenSource? _loadCts;

    public Fb2ReaderForm(string filePath)
    {
        _filePath = filePath;
        Text = "FB2 Reader";
        Width = 920;
        Height = 740;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        BuildUi();
        Load += async (_, _) => await LoadBookAsync();
        FormClosed += (_, _) => _loadCts?.Cancel();
        KeyDown += Fb2ReaderForm_KeyDown;
    }

    private void BuildUi()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(10), BackColor = Color.White };
        _lblHeader.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _lblPage.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        top.Controls.Add(_lblPage);
        top.Controls.Add(_lblHeader);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 6, 10, 6), WrapContents = false };
        foreach (FontFamily fam in FontFamily.Families.OrderBy(f => f.Name))
            _cmbFont.Items.Add(fam.Name);
        _cmbFontSize.Items.AddRange(new object[] { "12", "14", "16", "18", "20", "22", "24", "26", "28" });
        _cmbTheme.Items.AddRange(new object[] { "Light", "Dark" });
        _cmbFont.SelectedItem = _cmbFont.Items.Cast<object>().FirstOrDefault(i => string.Equals(i.ToString(), "Segoe UI", StringComparison.OrdinalIgnoreCase)) ?? _cmbFont.Items[0];
        _cmbFontSize.SelectedItem = "16";
        _cmbTheme.SelectedItem = "Light";

        _btnPrev.Width = 72;
        _btnNext.Width = 72;

        toolbar.Controls.AddRange(new Control[]
        {
            new Label { Text = "Font", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _cmbFont,
            new Label { Text = "Size", AutoSize = true, Padding = new Padding(10, 8, 0, 0) }, _cmbFontSize,
            new Label { Text = "Theme", AutoSize = true, Padding = new Padding(10, 8, 0, 0) }, _cmbTheme,
            _btnPrev, _btnNext
        });

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14), BackColor = Color.White };
        host.Controls.Add(_reader);

        Controls.Add(host);
        Controls.Add(toolbar);
        Controls.Add(top);

        _btnPrev.Click += (_, _) => NavigatePage(-1);
        _btnNext.Click += (_, _) => NavigatePage(1);
        _cmbFont.SelectedIndexChanged += (_, _) => ApplyReaderStyle();
        _cmbFontSize.SelectedIndexChanged += (_, _) => ApplyReaderStyle();
        _cmbTheme.SelectedIndexChanged += (_, _) => ApplyReaderStyle();

        ApplyReaderStyle();
    }

    private async Task LoadBookAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        _lblHeader.Text = "Loading...";
        _lblPage.Text = string.Empty;
        _reader.Text = string.Empty;

        try
        {
            var ct = _loadCts.Token;
            _book = await Task.Run(() => Fb2Parser.Parse(_filePath), ct);
            _pageIndex = 0;
            RenderPage();
        }
        catch (OperationCanceledException)
        {
            _lblHeader.Text = "Load canceled.";
        }
        catch (InvalidDataException ex)
        {
            _lblHeader.Text = "Unable to open FB2 file.";
            _lblPage.Text = string.Empty;
            _reader.Text = ex.Message;
            MessageBox.Show(this, "Не удалось открыть FB2: " + ex.Message, "FB2 Reader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _lblHeader.Text = "Unable to open FB2 file.";
            _lblPage.Text = string.Empty;
            _reader.Text = ex.Message;
        }
    }

    private void Fb2ReaderForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Right or Keys.PageDown)
        {
            NavigatePage(1);
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Left or Keys.PageUp)
        {
            NavigatePage(-1);
            e.Handled = true;
        }
    }

    private void NavigatePage(int delta)
    {
        if (_book == null || _book.Pages.Count == 0) return;
        int target = Math.Clamp(_pageIndex + delta, 0, _book.Pages.Count - 1);
        if (target == _pageIndex) return;
        _pageIndex = target;
        RenderPage();
    }

    private void RenderPage()
    {
        if (_book == null || _book.Pages.Count == 0) return;

        _lblHeader.Text = $"{_book.Title} — {_book.Author}";
        _lblPage.Text = $"Page {_pageIndex + 1} / {_book.Pages.Count}";
        _reader.Text = _book.Pages[_pageIndex];
        _reader.SelectionStart = 0;
        _reader.ScrollToCaret();

        _btnPrev.Enabled = _pageIndex > 0;
        _btnNext.Enabled = _pageIndex < _book.Pages.Count - 1;
    }

    private void ApplyReaderStyle()
    {
        string fontName = _cmbFont.SelectedItem?.ToString() ?? "Segoe UI";
        float size = float.TryParse(_cmbFontSize.SelectedItem?.ToString(), out var f) ? f : 16f;
        _reader.Font = new Font(fontName, size, FontStyle.Regular);

        var theme = _cmbTheme.SelectedItem?.ToString() ?? "Light";
        if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            _reader.BackColor = Color.FromArgb(31, 31, 31);
            _reader.ForeColor = Color.Gainsboro;
        }
        else
        {
            _reader.BackColor = Color.White;
            _reader.ForeColor = Color.Black;
        }
    }
}
