using System.Drawing.Drawing2D;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();
        Theme.Init();
        Content.InitDb();
        Application.Run(new MainMenuForm());
    }
}

public sealed class MainMenuForm : Form
{
    public MainMenuForm()
    {
        Text = "Кто хочет стать миллионером?";
        Width = 980;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        Theme.EnableDoubleBuffer(this);

        var bg = new BackgroundGradientPanel();
        var titleWrap = new TransparentPanel { Dock = DockStyle.Top, Height = 220, Padding = new Padding(70, 45, 70, 20), BackColor = Color.Transparent };
        var title = new Label
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.FontTitle,
            BackColor = Theme.CardColor,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Кто хочет стать миллионером?"
        };
        titleWrap.Controls.Add(title);

        var content = new TransparentTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(180, 10, 180, 70) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));

        var menu = new TransparentTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 10, 20, 10), Margin = new Padding(0, 0, 15, 0) };
        menu.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        menu.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        menu.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        menu.RowStyles.Add(new RowStyle(SizeType.Percent, 25));

        menu.Controls.Add(BuildMenuButton("Играть", (_, _) => OpenChooser()), 0, 0);
        menu.Controls.Add(BuildMenuButton("Настройки", (_, _) => new SettingsForm().ShowDialog(this)), 0, 1);
        menu.Controls.Add(BuildMenuButton("DLC", (_, _) => new DlcForm().ShowDialog(this)), 0, 2);
        menu.Controls.Add(BuildMenuButton("История", (_, _) => new HistoryForm().ShowDialog(this)), 0, 3);

        content.Controls.Add(menu, 0, 0);
        content.Controls.Add(BuildRichTexturePanel(), 1, 0);

        bg.Controls.Add(content);
        bg.Controls.Add(titleWrap);
        Controls.Add(bg);
        Theme.Apply(this);
    }

    private static Control BuildRichTexturePanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0), BackColor = Theme.CardColor };
        var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
        var imagePath = Path.Combine(AppContext.BaseDirectory, "richman.png");
        if (File.Exists(imagePath))
        {
            picture.Image = LoadImageUnlocked(imagePath);
            panel.Controls.Add(picture);
        }
        else
        {
            panel.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Положите PNG 1024x1536\nрядом с приложением:\n./richman.png",
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 10, FontStyle.Regular)
            });
        }

        return panel;
    }


    private static Image LoadImageUnlocked(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var img = Image.FromStream(fs);
        return new Bitmap(img);
    }

    private static Button BuildMenuButton(string text, EventHandler click)
    {
        var b = new Button { Dock = DockStyle.Fill, Text = text, Font = Theme.FontBold, FlatStyle = FlatStyle.Flat };
        b.FlatAppearance.BorderSize = 3;
        b.Click += click;
        return b;
    }

    private void OpenChooser()
    {
        var sets = Content.GetEnabledGameSets();
        if (sets.Count == 0)
        {
            MessageBox.Show("Нет установленных игровых наборов.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dlg = new Form
        {
            Text = "Выбор набора",
            Width = 520,
            Height = 210,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var box = new ComboBox { Dock = DockStyle.Top, Height = 35, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(GameSet.Title) };
        box.DataSource = sets;
        var defaultSlug = Content.GetSetting("default_game_set_slug", sets[0].Slug);
        var defaultSet = sets.FirstOrDefault(s => s.Slug == defaultSlug);
        if (defaultSet is not null)
        {
            box.SelectedItem = defaultSet;
        }
        else if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }

        var info = new Label { Dock = DockStyle.Top, Height = 44, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4), Text = "Выберите набор вопросов и нажмите Старт." };
        var start = new Button { Text = "Старт", Dock = DockStyle.Bottom, Height = 45, Font = new Font("Segoe UI", 12, FontStyle.Bold) };
        start.Click += (_, _) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
        dlg.AcceptButton = start;

        dlg.Controls.Add(box);
        dlg.Controls.Add(info);
        dlg.Controls.Add(start);

        if (dlg.ShowDialog(this) == DialogResult.OK && box.SelectedItem is GameSet set)
        {
            Content.SetSetting("default_game_set_slug", set.Slug);
            using var game = new GameForm(set);
            game.ShowDialog(this);
        }
    }
}

public sealed class GradientPanel : Panel
{
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var br = new LinearGradientBrush(ClientRectangle, Color.FromArgb(24, 66, 140), Color.FromArgb(120, 62, 175), 35f);
        e.Graphics.FillRectangle(br, ClientRectangle);
    }
}

public sealed class GameForm : Form
{
    private readonly GameSet _set;
    private readonly List<LadderItem> _ladder;
    private readonly HashSet<string> _usedQuestionIds = [];
    private readonly Button[] _answerButtons = new Button[4];
    private readonly ListBox _ladderList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _questionLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _hintBtn = new() { Text = "Подсказка ⭐", Width = 272, Height = 56 };
    private readonly Button _fiftyBtn = new() { Text = "50/50", Width = 272, Height = 56 };
    private readonly Button _audienceBtn = new() { Text = "Помощь зала", Width = 272, Height = 56 };
    private readonly Button _cashoutBtn = new() { Text = "Забрать деньги", Width = 272, Height = 56, Enabled = false };
    private readonly Panel _hintPanel = new() { Width = 272, Height = 70, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(8), BackColor = Theme.CardColor };
    private readonly Label _hintBody = new() { Dock = DockStyle.Fill, Text = "Подсказка будет здесь", ForeColor = Color.Gray };
    private readonly Label _audienceLabel = new() { Width = 272, Height = 120, BorderStyle = BorderStyle.FixedSingle, Font = Theme.FontRegular, BackColor = Theme.CardColor, Padding = new Padding(6), Text = "Мнение зала появится здесь" };

    private readonly string _sessionId = string.Empty;
    private QuestionItem? _question;
    private int _currentStep = 1;
    private int _lastCompletedStep;
    private int _currentPrize;
    private int _lastSafePrize;
    private bool _sessionEnded;

    private bool _hintUsedGlobal;
    private bool _fiftyUsedGlobal;
    private int _fiftyLastUsedStep;
    private bool _audienceUsedGlobal;

    private bool _stepHintUsed;
    private bool _step5050Used;
    private bool _stepAudienceUsed;
    private AudienceResult? _stepAudience;

    public GameForm(GameSet set)
    {
        _set = set;
        _ladder = Content.GetLadder(_set.Id);
        if (_ladder.Count == 0)
        {
            MessageBox.Show("Лестница призов не найдена.");
            Close();
            return;
        }

        foreach (var s in _ladder)
        {
            if (Content.CountQuestions(_set.Id, s.StepIndex) < 1)
            {
                MessageBox.Show($"Недостаточно вопросов для шага {s.StepIndex}.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
                return;
            }
        }

        _sessionId = Content.CreateSession(_set.Id);

        Text = $"Игра — {_set.Title}";
        Width = 1280;
        Height = 820;
        StartPosition = FormStartPosition.CenterParent;
        Theme.EnableDoubleBuffer(this);

        BuildUi();
        LoadQuestion();

        FormClosing += (_, _) =>
        {
            if (!_sessionEnded)
            {
                Content.EndSession(_sessionId, _lastCompletedStep, _lastSafePrize, false, _hintUsedGlobal, _fiftyUsedGlobal, _audienceUsedGlobal);
                _sessionEnded = true;
            }
        };
    }

    private void BuildUi()
    {
        var root = new TransparentTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0), Padding = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));

        _ladderList.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        foreach (var item in _ladder.OrderByDescending(x => x.StepIndex))
        {
            _ladderList.Items.Add($"{item.StepIndex}. {item.PrizeAmount:N0} ₽{(item.IsSafe ? " ★" : "")}");
        }

        var center = new TransparentTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0) };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 58));

        var questionHost = new GradientPanel { Dock = DockStyle.Fill, Margin = new Padding(8), Padding = new Padding(14) };
        var qBorder = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardColor, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(12) };
        _questionLabel.Font = new Font("Segoe UI", 19, FontStyle.Bold);
        qBorder.Controls.Add(_questionLabel);
        questionHost.Controls.Add(qBorder);

        var answers = new TransparentTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12), Margin = new Padding(0) };
        answers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        answers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        answers.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        answers.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        for (var i = 0; i < 4; i++)
        {
            var letter = (char)('A' + i);
            var btn = new Button
            {
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                Tag = letter
            };
            btn.FlatAppearance.BorderSize = 3;
            btn.Margin = new Padding(10);
            btn.Click += OnAnswer;
            _answerButtons[i] = btn;
            answers.Controls.Add(btn, i % 2, i / 2);
        }

        center.Controls.Add(questionHost, 0, 0);
        center.Controls.Add(answers, 0, 1);

        var right = new TransparentFlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10), AutoScroll = true };
        foreach (var b in new[] { _hintBtn, _fiftyBtn, _audienceBtn, _cashoutBtn })
        {
            b.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 2;
            right.Controls.Add(b);
        }

        _hintPanel.Controls.Add(new Label { Dock = DockStyle.Top, Height = 24, Text = "Подсказка:", Font = new Font("Segoe UI", 10, FontStyle.Bold) });
        _hintPanel.Controls.Add(_hintBody);
        right.Controls.Add(_hintPanel);
        right.Controls.Add(_audienceLabel);

        _hintBtn.Click += (_, _) => UseHint();
        _fiftyBtn.Click += (_, _) => UseFifty();
        _audienceBtn.Click += (_, _) => UseAudience();
        _cashoutBtn.Click += (_, _) => Cashout();

        root.Controls.Add(_ladderList, 0, 0);
        root.Controls.Add(center, 1, 0);
        root.Controls.Add(right, 2, 0);
        var bg = new BackgroundGradientPanel();
        bg.Controls.Add(root);
        Controls.Add(bg);
        Theme.Apply(this);
    }

    private void LoadQuestion()
    {
        HighlightStep();
        ResetStepFlags();
        ResetStepUi();
        _question = Content.GetRandomQuestion(_set.Id, _currentStep, _usedQuestionIds);
        if (_question is null)
        {
            MessageBox.Show("Для текущего шага закончились вопросы.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            EndGame(false, "Нет вопроса", _lastSafePrize);
            return;
        }

        _usedQuestionIds.Add(_question.Id);
        _questionLabel.Text = _question.QuestionText;

        SetButton('A', _question.AnswerA);
        SetButton('B', _question.AnswerB);
        SetButton('C', _question.AnswerC);
        SetButton('D', _question.AnswerD);
    }

    private void HighlightStep()
    {
        var idx = _ladder.Count - _currentStep;
        _ladderList.SelectedIndex = Math.Max(0, idx);
    }

    private void SetButton(char letter, string text)
    {
        var b = _answerButtons[letter - 'A'];
        b.Enabled = true;
        b.BackColor = SystemColors.Control;
        b.FlatAppearance.BorderColor = Color.Black;
        b.Text = $"{letter}: {text}";
    }

    private bool CanUseFifty() => _fiftyLastUsedStep == 0 || (_currentStep - _fiftyLastUsedStep) >= 5;

    private void ResetStepUi()
    {
        _hintPanel.Height = 70;
        _hintBody.Text = "Подсказка будет здесь";
        _hintBody.ForeColor = Color.Gray;
        _hintBody.AutoSize = false;
        _hintBody.TextAlign = ContentAlignment.TopLeft;
        _audienceLabel.Text = "Мнение зала появится здесь";

        _hintBtn.Visible = true;
        _hintBtn.Enabled = !_hintUsedGlobal;
        _hintBtn.Text = _hintUsedGlobal ? "Подсказка ⭐ (исп.)" : "Подсказка ⭐";
        _hintBtn.BackColor = _hintUsedGlobal ? Color.LightGray : SystemColors.Control;

        _audienceBtn.Visible = !_audienceUsedGlobal;
        _audienceBtn.Enabled = !_audienceUsedGlobal;

        var fiftyAllowed = CanUseFifty();
        var stepsLeft = _fiftyLastUsedStep == 0 ? 0 : Math.Max(0, 5 - (_currentStep - _fiftyLastUsedStep));
        _fiftyBtn.Visible = true;
        _fiftyBtn.Enabled = fiftyAllowed;
        _fiftyBtn.Text = fiftyAllowed ? "50/50" : $"50/50 (кд: {stepsLeft})";
        _fiftyBtn.BackColor = fiftyAllowed ? SystemColors.Control : Color.LightGray;
    }

    private void ResetStepFlags()
    {
        _stepHintUsed = false;
        _step5050Used = false;
        _stepAudienceUsed = false;
        _stepAudience = null;
    }

    private async void OnAnswer(object? sender, EventArgs e)
    {
        if (_question is null || sender is not Button btn || btn.Tag is not char chosen) return;

        foreach (var b in _answerButtons) b.Enabled = false;
        var isCorrect = chosen == _question.CorrectAnswer;

        btn.FlatAppearance.BorderColor = isCorrect ? Color.Green : Color.Red;
        btn.BackColor = isCorrect ? Color.Honeydew : Color.MistyRose;

        var right = _answerButtons[_question.CorrectAnswer - 'A'];
        right.FlatAppearance.BorderColor = Color.Green;
        right.BackColor = Color.Honeydew;

        Content.AddSessionAnswer(
            _sessionId,
            _currentStep,
            _question.Id,
            chosen,
            isCorrect,
            _stepHintUsed,
            _step5050Used,
            _stepAudienceUsed,
            _stepAudience);

        await Task.Delay(700);

        if (!isCorrect)
        {
            EndGame(false, "Неверный ответ", _lastSafePrize);
            return;
        }

        var ladderItem = _ladder.First(l => l.StepIndex == _currentStep);
        _lastCompletedStep = _currentStep;
        _currentPrize = ladderItem.PrizeAmount;
        _cashoutBtn.Enabled = _lastCompletedStep > 0;

        if (ladderItem.IsSafe) _lastSafePrize = ladderItem.PrizeAmount;

        if (_currentStep == _ladder.Max(l => l.StepIndex))
        {
            EndGame(true, "Победа", _currentPrize);
            return;
        }

        _currentStep++;
        LoadQuestion();
    }

    private void UseHint()
    {
        if (_question is null || _hintUsedGlobal) return;
        _hintUsedGlobal = true;
        _stepHintUsed = true;
        _hintBtn.Enabled = false;
        _hintBtn.Visible = true;
        _hintBtn.BackColor = Color.LightGray;
        _hintBtn.Text = "Подсказка ⭐ (исп.)";

        var text = string.IsNullOrWhiteSpace(_question.HintText) ? "Подсказки нет." : _question.HintText;
        _hintBody.ForeColor = Color.Black;
        _hintBody.Text = text;
        _hintBody.BringToFront();
        _hintPanel.Refresh();
        _hintPanel.Height = 70;

        if (Content.GetSetting("animations_enabled", "1") == "1")
        {
            var t = new System.Windows.Forms.Timer { Interval = 20 };
            t.Tick += (_, _) =>
            {
                _hintPanel.Height += 12;
                if (_hintPanel.Height >= 145)
                {
                    _hintPanel.Height = 145;
                    t.Stop();
                    t.Dispose();
                }
            };
            t.Start();
        }
        else
        {
            _hintPanel.Height = 145;
        }
    }

    private void UseFifty()
    {
        if (_question is null) return;
        if (!CanUseFifty()) return;

        _fiftyUsedGlobal = true;
        _step5050Used = true;
        _fiftyLastUsedStep = _currentStep;
        _fiftyBtn.Enabled = false;
        _fiftyBtn.Visible = true;
        _fiftyBtn.BackColor = Color.LightGray;
        _fiftyBtn.Text = "50/50 (кд: 5)";

        var wrong = new List<char> { 'A', 'B', 'C', 'D' };
        wrong.Remove(_question.CorrectAnswer);
        var keepOne = wrong[Random.Shared.Next(wrong.Count)];
        foreach (var c in wrong)
        {
            if (c == keepOne) continue;
            var b = _answerButtons[c - 'A'];
            b.Enabled = false;
            b.BackColor = Color.Gainsboro;
            b.FlatAppearance.BorderColor = Color.DarkGray;
        }
    }

    private void UseAudience()
    {
        if (_question is null || _audienceUsedGlobal) return;
        _audienceUsedGlobal = true;
        _stepAudienceUsed = true;
        _audienceBtn.Enabled = false;
        _audienceBtn.Visible = false;
        _audienceBtn.BackColor = Color.LightGray;

        var enabled = new List<char>();
        for (var i = 0; i < 4; i++)
        {
            if (_answerButtons[i].Enabled) enabled.Add((char)('A' + i));
        }
        if (!enabled.Contains(_question.CorrectAnswer)) enabled.Add(_question.CorrectAnswer);

        _stepAudience = AudienceHelper.Generate(_currentStep, _question.CorrectAnswer, enabled);
        var a = _stepAudience;
        _audienceLabel.Text = $"A: {Bar(a.A)} {a.A}%\nB: {Bar(a.B)} {a.B}%\nC: {Bar(a.C)} {a.C}%\nD: {Bar(a.D)} {a.D}%";
    }

    private static string Bar(int p) => new string('█', Math.Max(1, p / 6));

    private void Cashout()
    {
        if (_lastCompletedStep <= 0) return;
        if (MessageBox.Show($"Забрать {_currentPrize:N0} ₽ и завершить игру?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        EndGame(false, "Игрок забрал деньги", _currentPrize);
    }

    private void EndGame(bool finishedAll, string reason, int finalPrize)
    {
        if (_sessionEnded) return;
        _sessionEnded = true;
        Content.EndSession(_sessionId, _lastCompletedStep, finalPrize, finishedAll, _hintUsedGlobal, _fiftyUsedGlobal, _audienceUsedGlobal);
        using var summary = new SummaryForm(reason, finishedAll, _lastCompletedStep, _lastSafePrize, finalPrize);
        summary.ShowDialog(this);
        Close();
    }
}

public static class AudienceHelper
{
    public static AudienceResult Generate(int step, char correct, List<char> enabled)
    {
        var minMax = step switch
        {
            <= 3 => (65, 85),
            <= 6 => (45, 70),
            <= 8 => (35, 55),
            _ => (25, 45)
        };
        var correctPct = Random.Shared.Next(minMax.Item1, minMax.Item2 + 1);

        var letters = new[] { 'A', 'B', 'C', 'D' }.Where(enabled.Contains).ToList();
        if (!letters.Contains(correct)) letters.Add(correct);

        var wrong = letters.Where(c => c != correct).ToList();
        var remain = 100 - correctPct;
        var dist = new Dictionary<char, int> { [correct] = correctPct };

        if (wrong.Count == 0)
        {
            dist[correct] = 100;
        }
        else if (wrong.Count == 1)
        {
            dist[wrong[0]] = remain;
        }
        else
        {
            var mainWrong = wrong[Random.Shared.Next(wrong.Count)];
            var mainWrongPct = Math.Min(remain - (wrong.Count - 1), Math.Max(8, remain / 2 + Random.Shared.Next(-8, 9)));
            remain -= mainWrongPct;
            dist[mainWrong] = mainWrongPct;
            var rest = wrong.Where(w => w != mainWrong).ToList();
            for (var i = 0; i < rest.Count; i++)
            {
                var chunk = i == rest.Count - 1 ? remain : Random.Shared.Next(1, remain - (rest.Count - i - 1));
                dist[rest[i]] = chunk;
                remain -= chunk;
            }
        }

        var all = new Dictionary<char, int> { ['A'] = 0, ['B'] = 0, ['C'] = 0, ['D'] = 0 };
        foreach (var kv in dist) all[kv.Key] = kv.Value;

        var total = all.Values.Sum();
        if (total != 100) all[correct] += 100 - total;

        return new AudienceResult(all['A'], all['B'], all['C'], all['D']);
    }
}

public sealed class SettingsForm : Form
{
    public SettingsForm()
    {
        Text = "Настройки";
        Width = 520;
        Height = 280;
        StartPosition = FormStartPosition.CenterParent;
        Theme.EnableDoubleBuffer(this);

        var bg = new BackgroundGradientPanel();
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18), BackColor = Color.Transparent };
        var card = new TransparentTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Padding = new Padding(20), BackColor = Theme.CardColor };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

        var anim = new CheckBox { Text = "animations_enabled", Checked = Content.GetSetting("animations_enabled", "1") == "1", Dock = DockStyle.Fill };
        var snd = new CheckBox { Text = "sounds_enabled (stub)", Checked = Content.GetSetting("sounds_enabled", "0") == "1", Dock = DockStyle.Fill };

        var sets = Content.GetEnabledGameSets();
        var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(GameSet.Title) };
        combo.DataSource = sets;
        var slug = Content.GetSetting("default_game_set_slug", sets.Count > 0 ? sets[0].Slug : "");
        var defaultSet = sets.FirstOrDefault(s => s.Slug == slug);
        if (defaultSet is not null)
        {
            combo.SelectedItem = defaultSet;
        }
        else if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }

        var save = new Button { Text = "Сохранить", Dock = DockStyle.Right, Width = 140 };
        save.Click += (_, _) =>
        {
            Content.SetSetting("animations_enabled", anim.Checked ? "1" : "0");
            Content.SetSetting("sounds_enabled", snd.Checked ? "1" : "0");
            if (combo.SelectedItem is GameSet gs) Content.SetSetting("default_game_set_slug", gs.Slug);
            DialogResult = DialogResult.OK;
        };

        card.Controls.Add(anim, 0, 0);
        card.Controls.Add(snd, 0, 1);
        card.Controls.Add(combo, 0, 2);
        card.Controls.Add(save, 0, 3);
        panel.Controls.Add(card);
        bg.Controls.Add(panel);
        Controls.Add(bg);
        Theme.Apply(this);
    }
}

public sealed class DlcForm : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill };

    public DlcForm()
    {
        Text = "DLC";
        Width = 800;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        Theme.EnableDoubleBuffer(this);

        var bg = new BackgroundGradientPanel();
        var root = new TransparentTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, BackColor = Color.Transparent, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        var url = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "https://site/pack.zip" };
        var installUrl = new Button { Text = "Install from URL", Dock = DockStyle.Right, Width = 220 };
        installUrl.Click += (_, _) =>
        {
            try
            {
                var msg = Content.InstallFromUrl(url.Text.Trim());
                MessageBox.Show(msg, "DLC");
                ReloadSets();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка");
            }
        };

        var installFile = new Button { Text = "Install from file", Dock = DockStyle.Left, Width = 220 };
        installFile.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog { Filter = "ZIP files|*.zip" };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var msg = Content.InstallDlcFromZip(ofd.FileName, null);
                MessageBox.Show(msg, "DLC");
                ReloadSets();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка");
            }
        };

        var row = new Panel { Dock = DockStyle.Fill };
        row.Controls.Add(url);
        row.Controls.Add(installUrl);

        _list.BackColor = Color.White;
        root.Controls.Add(_list, 0, 0);
        root.Controls.Add(row, 0, 1);
        root.Controls.Add(installFile, 0, 2);
        bg.Controls.Add(root);
        Controls.Add(bg);
        Theme.Apply(this);

        ReloadSets();
    }

    private void ReloadSets()
    {
        _list.Items.Clear();
        foreach (var g in Content.GetGameSets())
        {
            _list.Items.Add($"{g.Title} | ver {g.Version} | slug {g.Slug}");
        }
    }
}

public sealed class HistoryForm : Form
{
    public HistoryForm()
    {
        Text = "История";
        Width = 860;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        Theme.EnableDoubleBuffer(this);

        var bg = new BackgroundGradientPanel();
        var card = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14), BackColor = Color.Transparent };
        var list = new ListBox { Dock = DockStyle.Fill, Font = Theme.FontRegular, BackColor = Color.White };
        var rows = Content.GetRecentSessions(20);
        foreach (var r in rows)
        {
            list.Items.Add($"{r.StartedAt:dd.MM.yyyy HH:mm} | {r.GameSetTitle} | шаг {r.FinalStep} | {r.FinalPrize:N0} ₽ | {r.ResultText}");
        }

        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            MessageBox.Show(Content.GetSessionDetails(rows[list.SelectedIndex].Id), "Детали");
        };

        card.Controls.Add(list);
        bg.Controls.Add(card);
        Controls.Add(bg);
        Theme.Apply(this);
    }
}

public sealed class SummaryForm : Form
{
    public SummaryForm(string reason, bool finishedAll, int finalStep, int safePrize, int finalPrize)
    {
        Text = "Итог";
        Width = 560;
        Height = 320;
        StartPosition = FormStartPosition.CenterParent;
        Theme.EnableDoubleBuffer(this);

        var bg = new BackgroundGradientPanel();
        var card = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18), BackColor = Color.Transparent };
        var label = new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.FontBold,
            BackColor = Theme.CardColor,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = $"{reason}\nШаг: {finalStep}\nНесгораемая сумма: {safePrize:N0} ₽\nИтог: {finalPrize:N0} ₽\n{(finishedAll ? "Поздравляем с победой!" : "Спасибо за игру!")}" 
        };

        card.Controls.Add(label);
        bg.Controls.Add(card);
        Controls.Add(bg);
        Theme.Apply(this);
    }
}
