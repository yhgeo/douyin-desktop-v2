using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DouyinDesktop.Core;

namespace DouyinDesktop.App;

/// <summary>
/// 快捷键设置。点"修改"后进入捕获状态，按任意键绑定；
/// 被别的动作占用的键会自动从那边解除。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly KeyMap _map;
    private readonly ObservableCollection<KeyRow> _rows = new();
    private readonly DouyinDesktop.Core.Api.DouyinClient _client;
    private readonly Func<Task<bool>> _loginAction;
    private readonly Action _logoutAction;
    private readonly AppSettings _settings;
    private KeyRow? _capturing;

    public SettingsWindow(KeyMap map,
                          AppSettings settings,
                          DouyinDesktop.Core.Api.DouyinClient client,
                          Func<Task<bool>> loginAction,
                          Action logoutAction)
    {
        InitializeComponent();

        _map = map;
        _settings = settings;
        _client = client;
        _loginAction = loginAction;
        _logoutAction = logoutAction;

        foreach (var def in KeyMap.Actions) _rows.Add(new KeyRow(_map, def));
        KeyList.ItemsSource = _rows;

        DataPathText.Text = DouyinDesktop.Core.DouyinPaths.DataDirectory;
        // 图集开关已经从界面上去掉了（推荐流实测 0 图集，留着只会误导），
        // 但配置项保留——万一以后接口恢复，改一行就能放回来。
        // ImageOnlyCheck 不再存在，所以这里只读不写。

        InitTagGroups(settings);

        RefreshTasteCountButtons();

        ShowBackupState();

        LoadDanmakuUi();
        RefreshAccount();

        // 后台检查完登录状态后，设置页开着也能实时更新
        LoginHealth.Changed += OnHealthChanged;
        Closed += (_, _) => LoginHealth.Changed -= OnHealthChanged;
    }

    private void OnHealthChanged() => Dispatcher.BeginInvoke(UpdateHealthText);

    // ================= 左侧导航 =================

    private void OnNavAccount(object sender, RoutedEventArgs e) => SwitchPage("account");
    private void OnNavContent(object sender, RoutedEventArgs e) => SwitchPage("content");
    private void OnNavTaste(object sender, RoutedEventArgs e) => SwitchPage("taste");
    private void OnNavDanmaku(object sender, RoutedEventArgs e) => SwitchPage("danmaku");
    private void OnNavKeys(object sender, RoutedEventArgs e) => SwitchPage("keys");
    private void OnNavBackup(object sender, RoutedEventArgs e) => SwitchPage("backup");
    private void OnNavAbout(object sender, RoutedEventArgs e) => SwitchPage("about");

    // ================= 备份 =================

    private DateTime _backupStartedAt = DateTime.MinValue;
    private string _lastBackupDir = "";

    /// <summary>把"上次备份"的情况显示出来，并让两个打开按钮指向它。</summary>
    private void ShowBackupState()
    {
        var st = DouyinDesktop.Core.BackupState.Load(DouyinDesktop.Core.DouyinPaths.DataDirectory);
        if (st is null || st.Count == 0)
        {
            BackupLast.Text = "还没有备份过";
            return;
        }

        bool dirExists = System.IO.Directory.Exists(st.Dir);

        BackupLast.Text = dirExists
            ? $"上次备份：{st.At} · {st.Count} 条 · {st.Bytes / 1024.0:0} KB"
            : $"上次备份：{st.At} · {st.Count} 条（目录已被删除）";

        if (!dirExists) return;

        // 重启后也能直接打开上次那份，不用重新备份一遍
        if (_lastBackupDir.Length == 0) _lastBackupDir = st.Dir;

        BackupResult.Visibility = Visibility.Visible;
        BackupSummary.Text = $"上次备份 {st.Count} 条 · {st.Bytes / 1024.0:0} KB";
        BackupPath.Text = st.Dir;
    }

    private async void OnRunBackup(object sender, RoutedEventArgs e)
    {
        if ((DateTime.Now - _backupStartedAt).TotalSeconds < 3) return;
        _backupStartedAt = DateTime.Now;

        BackupRunBtn.IsEnabled = false;
        BackupStatus.Text = "准备中…";

        try
        {
            if (_client.Session?.IsLoggedIn != true)
            {
                BackupStatus.Text = "需要先登录（设置 → 账号）";
                return;
            }

            string collectsId = await _client.GetDefaultCollectsIdAsync();
            if (collectsId.Length == 0)
            {
                BackupStatus.Text = "没有找到收藏夹";
                return;
            }

            // 翻到没有下一页为止。上限 60 页（1200 条）纯粹是防死循环，
            // 正常收藏夹远到不了。
            var all = new List<DouyinDesktop.Core.Models.Aweme>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string cursor = "0";

            for (int page = 0; page < 60; page++)
            {
                BackupStatus.Text = $"正在读取收藏… 已收 {all.Count} 条";

                var r = await _client.GetCollectedAsync(collectsId, cursor, 20);
                if (!r.Success)
                {
                    if (all.Count == 0)
                    {
                        BackupStatus.Text = "读取失败：" + r.Message;
                        return;
                    }
                    break;      // 已经拿到一部分，就别把整次备份废掉
                }

                foreach (var a in r.Items)
                    if (seen.Add(a.Id)) all.Add(a);

                if (!r.HasMore || r.NextCursor.Length == 0 || r.NextCursor == cursor) break;
                cursor = r.NextCursor;
                await Task.Delay(700);      // 限速，别把接口打成风控
            }

            if (all.Count == 0)
            {
                BackupStatus.Text = "一条都没读到";
                return;
            }

            BackupStatus.Text = $"正在生成文件（{all.Count} 条）…";

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir = System.IO.Path.Combine(
                DouyinDesktop.Core.DouyinPaths.DataDirectory, "backup", $"collect-{stamp}");
            System.IO.Directory.CreateDirectory(dir);

            var file = DouyinDesktop.Core.BackupExporter.Build(all, "collect");

            // 不带 BOM：CSV 里已经手动加了（Excel 需要），JSON/HTML 加了反而有杂字符
            var utf8 = new System.Text.UTF8Encoding(false);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "collect.json"),
                DouyinDesktop.Core.BackupExporter.ToJson(file), utf8);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "collect.csv"),
                DouyinDesktop.Core.BackupExporter.ToCsv(file), utf8);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "index.html"),
                DouyinDesktop.Core.BackupExporter.ToHtml(file), utf8);

            _lastBackupDir = dir;

            long bytes = new System.IO.DirectoryInfo(dir).GetFiles().Sum(f => f.Length);
            int imageCount = all.Count(x => x.IsImagePost);

            // 记一笔，下次打开设置能直接看到"上次备份过没有"
            new DouyinDesktop.Core.BackupState
            {
                At = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Count = all.Count,
                Dir = dir,
                Bytes = bytes,
            }.Save(DouyinDesktop.Core.DouyinPaths.DataDirectory);

            ShowBackupState();

            BackupResult.Visibility = Visibility.Visible;
            BackupSummary.Text = $"已备份 {all.Count} 条" +
                                 (imageCount > 0 ? $"（其中图集 {imageCount} 条）" : "") +
                                 $" · 共 {bytes / 1024.0:0} KB";
            BackupPath.Text = dir;

            BackupStatus.Text = "完成";
            AppLog.Write($"[备份] {all.Count} 条 → {dir}");
        }
        catch (Exception ex)
        {
            AppLog.Write("[备份] 失败：" + ex);
            BackupStatus.Text = "备份失败：" + ex.Message;
        }
        finally
        {
            BackupRunBtn.IsEnabled = true;
        }
    }

    private void OnOpenBackupDir(object sender, RoutedEventArgs e)
    {
        if (_lastBackupDir.Length == 0) return;
        try
        {
            // 路径带空格时必须自己加引号：explorer.exe 的参数是原始命令行，
            // 不加引号会把 "D:\My Data\backup" 截成 "D:\My"。
            System.Diagnostics.Process.Start("explorer.exe", $"\"{_lastBackupDir}\"");
        }
        catch (Exception ex) { BackupStatus.Text = "打不开：" + ex.Message; }
    }

    private void OnOpenBackupHtml(object sender, RoutedEventArgs e)
    {
        if (_lastBackupDir.Length == 0) return;
        string html = System.IO.Path.Combine(_lastBackupDir, "index.html");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(html)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { BackupStatus.Text = "打不开：" + ex.Message; }
    }

    // ================= 推荐倾向 =================

    /// <summary>
    /// 每批拉多少。平台标签只有详情接口给（推荐流里 100% 是 null），
    /// 所以每条都要额外调一次详情，实测 **602ms/条**。
    /// 具体采样总量由用户选（20/40/60/100），见 <see cref="_tasteCount"/>。
    /// </summary>
    private const int TastePerBatch = 20;

    /// <summary>用户选的采样条数。</summary>
    private int _tasteCount = 40;

    private void OnTasteCountClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        string raw = b.CommandParameter?.ToString() ?? "";
        if (!int.TryParse(raw, out int n) || n <= 0) return;

        _tasteCount = n;
        RefreshTasteCountButtons();
    }

    private void RefreshTasteCountButtons()
    {
        foreach (var (btn, val) in new[]
                 {
                     (TasteCount20, 20), (TasteCount40, 40),
                     (TasteCount60, 60), (TasteCount100, 100),
                 })
        {
            if (btn is null) continue;
            btn.Tag = _tasteCount == val ? "active" : null;
        }

        // 估时：拉列表约 2 秒/批 + 详情 0.62 秒/条
        double sec = _tasteCount / (double)TastePerBatch * 2 + _tasteCount * 0.62;
        if (TasteCountHint is not null) TasteCountHint.Text = $"约 {sec:0} 秒";
    }

    /// <summary>重入保护用时间戳而不是 bool——异常时 bool 不释放，按钮就永远点不动了。</summary>
    private DateTime _tasteStartedAt = DateTime.MinValue;

    private async void OnRunTaste(object sender, RoutedEventArgs e)
    {
        // 分析期间按钮是禁用的，这里只是防"双击的第二次已经在队列里"。
        // 别按总时长算——用户可能选 100 条要跑一分钟。
        if ((DateTime.Now - _tasteStartedAt).TotalSeconds < 3) return;
        _tasteStartedAt = DateTime.Now;

        TasteRunBtn.IsEnabled = false;
        TasteStatus.Text = "准备中…";

        try
        {
            var collected = new List<DouyinDesktop.Core.Models.Aweme>();
            var seenIds = new List<string>();
            int failed = 0;

            int batches = Math.Max(1, (int)Math.Ceiling(_tasteCount / (double)TastePerBatch));

            for (int i = 0; i < batches; i++)
            {
                int want = Math.Min(TastePerBatch, _tasteCount - collected.Count);
                if (want <= 0) break;

                TasteStatus.Text = $"正在拉取推荐… 第 {i + 1}/{batches} 批，已收 {collected.Count} 条";

                var result = await _client.GetFeedAsync(want, seenIds);
                if (result.Success)
                {
                    collected.AddRange(result.Items);
                    seenIds.AddRange(result.Items.Select(x => x.Id));
                }
                else
                {
                    failed++;
                    AppLog.Write($"[倾向] 第 {i + 1} 批失败：{result.Message}");
                }

                if (i < batches - 1) await Task.Delay(1200);
            }

            // presented_ids 只能减少重复，不能保证没有——再按 id 去一次
            var distinct = collected
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            if (distinct.Count == 0)
            {
                TasteStatus.Text = failed > 0
                    ? "一条都没拉到，接口可能被风控挡了（稍后再试）"
                    : "一条都没拉到";
                return;
            }

            // ---- 逐条取详情，把平台标签（video_tag）补齐 ----
            //
            // 推荐流里 video_tag 一条都没有（实测 29 条全 null），
            // 只能靠详情接口。平台标签是抖音自己的三级分类，比作者标签准得多，
            // 所以这一步值得多花二十几秒。
            var enriched = new List<DouyinDesktop.Core.Models.Aweme>(distinct.Count);
            int detailOk = 0, officialOk = 0;

            for (int i = 0; i < distinct.Count; i++)
            {
                TasteStatus.Text = $"读取平台标签 {i + 1}/{distinct.Count}…（每条约 0.6 秒）";

                var detail = await _client.GetAwemeDetailAsync(distinct[i].Id);
                if (detail is not null)
                {
                    detailOk++;
                    if (detail.OfficialTags.Count > 0) officialOk++;
                    enriched.Add(detail);
                }
                else
                {
                    enriched.Add(distinct[i]);      // 详情失败就退回作者标签
                }

                await Task.Delay(300);              // 限速，别把详情接口打成风控
            }

            AppLog.Write($"[倾向] 详情 {detailOk}/{distinct.Count} 成功，平台标签 {officialOk} 条");

            var report = DouyinDesktop.Core.TasteAnalyzer.Analyze(enriched);
            RenderTaste(report);

            string tail = officialOk > 0 ? $"，平台标签覆盖 {officialOk}/{distinct.Count}" : "";
            TasteStatus.Text = failed == 0
                ? $"完成，采样 {distinct.Count} 条{tail}"
                : $"完成，采样 {distinct.Count} 条{tail}（{failed} 批失败）";
        }
        catch (Exception ex)
        {
            TasteStatus.Text = "分析失败：" + ex.Message;
            AppLog.Write("[倾向] 异常：" + ex);
        }
        finally
        {
            TasteRunBtn.IsEnabled = true;
        }
    }

    private void RenderTaste(DouyinDesktop.Core.TasteReport r)
    {
        TasteResult.Visibility = Visibility.Visible;
        TasteSummary.Text = r.Summary;

        var session = _client.Session ?? DouyinDesktop.Core.Api.DouyinSession.Load();
        bool logged = session?.IsLoggedIn == true;

        double avg = r.Sampled == 0 ? 0 : (double)r.TagTotal / r.Sampled;
        string meta = $"采样 {r.Sampled} 条 · 平台标签 {r.OfficialCovered} 条 · 已归类 {r.Classified} 条 · 平均每条 {avg:0.0} 个标签";
        if (r.ImagePosts > 0) meta += $" · 图集 {r.ImagePosts} 条";
        meta += logged ? " · 已登录" : " · 游客态（登录后结果更贴近你的兴趣）";
        TasteMeta.Text = meta;

        // ---- 主题分布 ----
        // 代表标签直接排在行下面，**不用 ToolTip**：
        // 悬浮气泡在深色界面上容易出白底（继承前景色的问题），
        // 而且"要点一下才能看到"本身就不如直接显示。
        TasteThemes.Children.Clear();
        for (int i = 0; i < r.Themes.Count; i++)
        {
            var t = r.Themes[i];
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

            row.Children.Add(new TextBlock
            {
                Text = t.Theme,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD2)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // 用两列 Star 权重画占比，不用固定像素——窗口缩放也不会错位
            var barGrid = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            double fillWeight = Math.Max(0.01, t.Percent);
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fillWeight, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 1 - fillWeight), GridUnitType.Star) });

            var fill = new Border
            {
                CornerRadius = new CornerRadius(4, 0, 0, 4),
                Background = new SolidColorBrush(ThemeBarColor(i)),
            };
            var rest = new Border
            {
                CornerRadius = new CornerRadius(0, 4, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2C)),
            };
            Grid.SetColumn(rest, 1);
            barGrid.Children.Add(fill);
            barGrid.Children.Add(rest);
            Grid.SetColumn(barGrid, 1);
            row.Children.Add(barGrid);

            var pct = new TextBlock
            {
                Text = t.Percent.ToString("P0"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x99)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pct, 2);
            row.Children.Add(pct);

            block.Children.Add(row);

            // 代表标签：跟在主题名下方，最多 3 个，超了截断
            if (t.Samples.Count > 0)
            {
                block.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", t.Samples.Take(3)),
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0x5E, 0x6C)),
                    Margin = new Thickness(92, 3, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            TasteThemes.Children.Add(block);
        }

        // ---- 高频标签 ----
        TasteTags.Children.Clear();
        foreach (var tag in r.TopTags)
        {
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2C)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 6, 6),
            };
            chip.Child = new TextBlock
            {
                Text = $"#{tag.Tag}  {tag.Count}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB8)),
            };
            TasteTags.Children.Add(chip);
        }

        // ---- 作者集中度 ----
        TasteAuthors.Children.Clear();
        foreach (var a in r.TopAuthors)
        {
            double share = r.Sampled == 0 ? 0 : (double)a.Count / r.Sampled;
            TasteAuthors.Children.Add(new TextBlock
            {
                Text = $"{a.Name}　{a.Count} 条（{share:P0}）",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x99)),
                Margin = new Thickness(0, 0, 0, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
    }

    /// <summary>主题条的颜色：前几名用品牌红，往后逐渐冷下去，一眼能分出主次。</summary>
    private static Color ThemeBarColor(int index) => index switch
    {
        0 => Color.FromRgb(0xFE, 0x2C, 0x55),
        1 => Color.FromRgb(0xFF, 0x6B, 0x81),
        2 => Color.FromRgb(0x7A, 0x5C, 0xFF),
        3 => Color.FromRgb(0x4A, 0x90, 0xD9),
        4 => Color.FromRgb(0x3D, 0xB0, 0x8A),
        _ => Color.FromRgb(0x4A, 0x4A, 0x58),
    };


    // ================= 标签分组 =================

    private List<(string Name, string Rules)> _groups = new();
    private int _groupIndex;

    /// <summary>
    /// 载入分组时会给 TextBox 赋文本，那会触发 TextChanged。
    /// 用这个标志挡住，免得"载入"被当成"用户编辑"。
    /// </summary>
    private bool _loadingGroup;

    private void InitTagGroups(AppSettings settings)
    {
        var parsed = TagGroups.Parse(settings.TagGroupText);
        _groups = parsed.Items.Select(g => (g.Name, g.Rules)).ToList();
        if (_groups.Count == 0) _groups.Add(("默认", ""));

        _groupIndex = _groups.FindIndex(g => g.Name == settings.ActiveTagGroup);
        if (_groupIndex < 0) _groupIndex = 0;

        RefreshGroupTabs();
        LoadGroup(_groupIndex);

        // 留个痕迹：分组"消失"这类问题，看一眼日志就知道存了几个、读到几个
        AppLog.Write($"[分组] 载入 {_groups.Count} 个：" +
                     string.Join(" / ", _groups.Select(g => $"{g.Name}({g.Rules.Length}字)")));
    }

    /// <summary>重画顶部的分组按钮，当前组高亮。</summary>
    private void RefreshGroupTabs()
    {
        if (GroupTabs is null) return;
        GroupTabs.Children.Clear();

        for (int i = 0; i < _groups.Count; i++)
        {
            int idx = i;
            bool active = idx == _groupIndex;

            var btn = new Button
            {
                Content = _groups[i].Name,
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(13, 5, 13, 5),
                FontSize = 12,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(active ? Color.FromRgb(0x2C, 0x2C, 0x38) : Color.FromRgb(0x17, 0x17, 0x1E)),
                Foreground = new SolidColorBrush(active ? Color.FromRgb(0xF2, 0xF2, 0xF6) : Color.FromRgb(0x82, 0x82, 0x90)),
            };
            btn.Click += (_, _) => SelectGroup(idx);
            GroupTabs.Children.Add(btn);
        }
    }

    private void SelectGroup(int index)
    {
        if (_loadingGroup) return;
        if (index < 0 || index >= _groups.Count || index == _groupIndex) return;

        SaveCurrentGroup();
        _groupIndex = index;

        RefreshGroupTabs();
        LoadGroup(index);
        PersistGroups();
    }

    private void LoadGroup(int index)
    {
        if (GroupNameBox is null || GroupRulesBox is null) return;

        _loadingGroup = true;
        try
        {
            GroupNameBox.Text = _groups[index].Name;
            GroupRulesBox.Text = _groups[index].Rules;
        }
        finally
        {
            _loadingGroup = false;
        }

        UpdateGroupHint();
    }

    /// <summary>把界面上的内容写回内存里的当前组（不落盘）。</summary>
    private void SaveCurrentGroup()
    {
        if (_groups.Count == 0 || GroupNameBox is null || GroupRulesBox is null) return;

        string name = GroupNameBox.Text.Trim();
        if (name.Length == 0) name = _groups[_groupIndex].Name;   // 空名字不接受，保留原来的

        _groups[_groupIndex] = (name, GroupRulesBox.Text);
    }

    /// <summary>序列化回 app-settings.json，并记住当前生效的组。</summary>
    private void PersistGroups()
    {
        if (_settings is null) return;

        _settings.TagGroupText = string.Join("\n\n",
            _groups.Select(g => $"[{g.Name}]\n{g.Rules}"));

        _settings.ActiveTagGroup = _groups.Count > _groupIndex ? _groups[_groupIndex].Name : "";
        _settings.Save();
    }

    private void OnGroupRulesChanged(object sender, TextChangedEventArgs e)
    {
        // ⚠️ XAML 解析阶段就会触发一次，那时 _settings 还是 null
        if (_settings is null || _loadingGroup) return;

        SaveCurrentGroup();
        PersistGroups();
        UpdateGroupHint();
    }

    private void OnGroupNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_settings is null || _loadingGroup) return;

        SaveCurrentGroup();
        RefreshGroupTabs();      // Tab 上的名字要跟着变
        PersistGroups();
    }

    private void OnAddGroup(object sender, RoutedEventArgs e)
    {
        SaveCurrentGroup();

        // 自动起个不重名的名字，用户想改直接改上面那个框
        int n = _groups.Count + 1;
        string name = $"分组{n}";
        while (_groups.Any(g => g.Name == name)) name = $"分组{++n}";

        _groups.Add((name, ""));
        _groupIndex = _groups.Count - 1;

        RefreshGroupTabs();
        LoadGroup(_groupIndex);
        PersistGroups();

        GroupRulesBox?.Focus();
    }

    private void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        if (_groups.Count <= 1)
        {
            UpdateGroupHint("至少要留一个分组");
            return;
        }

        string removed = _groups[_groupIndex].Name;
        _groups.RemoveAt(_groupIndex);

        if (_groupIndex >= _groups.Count) _groupIndex = _groups.Count - 1;

        RefreshGroupTabs();
        LoadGroup(_groupIndex);
        PersistGroups();

        UpdateGroupHint($"已删除「{removed}」");
    }

    private void UpdateGroupHint(string? overrideText = null)
    {
        if (GroupHint is null) return;

        if (overrideText is not null)
        {
            GroupHint.Text = overrideText;
            return;
        }

        var rule = TagRule.Parse(_groups.Count > _groupIndex ? _groups[_groupIndex].Rules : "");
        string summary = rule.IsEmpty ? "不过滤" : rule.Describe();
        GroupHint.Text = $"当前组：{_groups[_groupIndex].Name}　·　{summary}　·　切组键在「快捷键」里绑";
    }

    private void OnDanmakuChanged(object sender, RoutedEventArgs e)
    {
        _settings.Danmaku = DanmakuCheck.IsChecked == true;
        _settings.Save();
        if (!_danmakuLoading) DanmakuOptions.IsEnabled = _settings.Danmaku;
    }

    private void OnOverlayRendererChanged(object sender, RoutedEventArgs e)
    {
        if (_danmakuLoading) return;

        _settings.DanmakuRenderer = OverlayCheck.IsChecked == true ? "overlay" : "ass";
        _settings.Save();
    }

    // ================= 弹幕外观 =================

    private bool _danmakuLoading;

    /// <summary>把配置里的值灌进控件。灌的过程中别回写，否则初始化就会触发一堆保存。</summary>
    private void LoadDanmakuUi()
    {
        _danmakuLoading = true;
        try
        {
            DanmakuCheck.IsChecked = _settings.Danmaku;
            OverlayCheck.IsChecked = _settings.DanmakuRenderer == "overlay";
            FontSlider.Value = Math.Clamp(_settings.DanmakuFontPercent, FontSlider.Minimum, FontSlider.Maximum);
            OpacitySlider.Value = Math.Clamp(_settings.DanmakuOpacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
            OutlineSlider.Value = Math.Clamp(_settings.DanmakuOutlinePercent, OutlineSlider.Minimum, OutlineSlider.Maximum);
            SpeedSlider.Value = Math.Clamp(_settings.DanmakuSpeed, SpeedSlider.Minimum, SpeedSlider.Maximum);

            UpdateDanmakuLabels();
            UpdateAreaButtons();
            DanmakuOptions.IsEnabled = _settings.Danmaku;
        }
        finally
        {
            _danmakuLoading = false;
        }
    }

    private void UpdateDanmakuLabels()
    {
        FontValue.Text = $"{FontSlider.Value:0.0}%";
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        OutlineValue.Text = $"{OutlineSlider.Value:0}%";
        SpeedValue.Text = $"{SpeedSlider.Value:0.#} 秒";
    }

    private void OnDanmakuStyleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 解析阶段滑杆的 Value 也会变一次，那时 _settings 和别的控件都还没建好
        if (_danmakuLoading || _settings is null || FontValue is null) return;

        _settings.DanmakuFontPercent = Math.Round(FontSlider.Value, 1);
        _settings.DanmakuOpacity = (int)Math.Round(OpacitySlider.Value);
        _settings.DanmakuOutlinePercent = Math.Round(OutlineSlider.Value, 1);
        _settings.DanmakuSpeed = Math.Round(SpeedSlider.Value, 1);

        UpdateDanmakuLabels();
        _settings.Save();
    }

    private void OnDanmakuAreaClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not string area) return;

        _settings.DanmakuArea = area;
        _settings.Save();
        UpdateAreaButtons();
    }

    private void UpdateAreaButtons()
    {
        foreach (var (btn, id) in new (Button, string)[]
                 {
                     (AreaTop, "top"), (AreaMiddle, "middle"),
                     (AreaBottom, "bottom"), (AreaFull, "full"),
                 })
        {
            btn.Tag = _settings.DanmakuArea == id ? "active" : null;
        }
    }

    private void SwitchPage(string page)
    {
        NavAccount.Tag = page == "account" ? "active" : null;
        NavContent.Tag = page == "content" ? "active" : null;
        NavTaste.Tag = page == "taste" ? "active" : null;
        NavDanmaku.Tag = page == "danmaku" ? "active" : null;
        NavKeys.Tag = page == "keys" ? "active" : null;
        NavBackup.Tag = page == "backup" ? "active" : null;
        NavAbout.Tag = page == "about" ? "active" : null;

        PageAccount.Visibility = page == "account" ? Visibility.Visible : Visibility.Collapsed;
        PageContent.Visibility = page == "content" ? Visibility.Visible : Visibility.Collapsed;
        PageTaste.Visibility = page == "taste" ? Visibility.Visible : Visibility.Collapsed;
        PageDanmaku.Visibility = page == "danmaku" ? Visibility.Visible : Visibility.Collapsed;
        PageKeys.Visibility = page == "keys" ? Visibility.Visible : Visibility.Collapsed;
        PageBackup.Visibility = page == "backup" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            // 同样要自己加引号，否则路径带空格（"D:\My Data\..."）会打不开
            System.Diagnostics.Process.Start("explorer.exe",
                $"\"{DouyinDesktop.Core.DouyinPaths.DataDirectory}\"");
        }
        catch (Exception ex)
        {
            HintText.Text = "打不开：" + ex.Message;
        }
    }

    /// <summary>把 LoginHealth 的状态画到账号区块（过期用红色，正常用灰）。</summary>
    private void UpdateHealthText()
    {
        if (HealthText is null) return;

        // 内存里的会话可能还没加载（启动后直接"接着上次看"不会拉数据），
        // 拿磁盘上的那份兜底，避免把"还没加载"显示成"未登录"。
        var session = _client.Session ?? DouyinDesktop.Core.Api.DouyinSession.Load();
        bool logged = session?.IsLoggedIn == true;

        // 有 Cookie 却显示"未登录"：健康检查在会话就绪前误判过。
        // 以事实为准，别让同一屏同时出现"已登录"和"未登录"。
        string health = logged && LoginHealth.Current == LoginHealth.State.Guest
            ? "待检查"
            : LoginHealth.Describe();

        string expiry = SessionExpiry.Describe(session?.LoginCookies);
        HealthText.Text = $"登录状态：{health}　·　{expiry}";
        HealthText.Foreground = LoginHealth.Current == LoginHealth.State.Expired
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFE, 0x2C, 0x55))
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x5A, 0x5A, 0x66));
    }

    private void RefreshAccount()
    {
        var session = _client.Session ?? DouyinDesktop.Core.Api.DouyinSession.Load();
        bool logged = session?.IsLoggedIn == true;

        AccountStatus.Text = logged ? "已登录" : "未登录（游客态）";
        AccountStatus.Foreground = logged
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC8, 0xC8, 0xD2))
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x8C, 0x8C, 0x99));

        LoginBtn.Content = logged ? "重新登录" : "扫码登录";
        LogoutBtn.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;

        // 登录健康度：靠"能不能读到评论"判断，主窗口每次加载视频后自动更新
        UpdateHealthText();

        if (logged) _ = VerifyLoginAsync();
    }

    /// <summary>
    /// 本地有 Cookie 不等于服务端认这个登录态（过期了照样是"已登录"）。
    /// 这里实际问一次服务端，失效就把状态标出来，别让用户以为还能点赞/关注。
    /// </summary>
    private async Task VerifyLoginAsync()
    {
        try
        {
            bool? ok = await _client.VerifyLoginAsync();
            if (ok == true) return;

            if (ok == false)
            {
                AccountStatus.Text = "登录已失效，请重新扫码";
                AccountStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFE, 0x2C, 0x55));
                HintText.Text = "点赞、关注、评论都需要有效登录，点「重新登录」扫码即可。";
                LoginBtn.Content = "重新登录";
                LogoutBtn.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            // 网络问题判断不了就保持原样
        }
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        bool ok = await _loginAction();
        if (!ok) return;

        RefreshAccount();
        HintText.Text = "登录成功";
        Close();
    }

    private void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        _logoutAction();
        RefreshAccount();
        HintText.Text = "已退出登录";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 和主窗口一样断开输入法，否则按字母键会被 IME 吃掉
        ImmAssociateContext(new System.Windows.Interop.WindowInteropHelper(this).Handle, IntPtr.Zero);
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hImc);

    private void OnRebindClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: KeyRow row }) return;

        if (_capturing is not null) _capturing.IsCapturing = false;

        _capturing = row;
        row.IsCapturing = true;
        HintText.Text = $"按下要绑定到「{row.Label}」的键…";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is null) return;

        e.Handled = true;

        // Esc 取消绑定，不当作按键
        if (e.Key == Key.Escape)
        {
            _capturing.IsCapturing = false;
            _capturing = null;
            HintText.Text = "";
            return;
        }

        // 单独的修饰键不算
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        var row = _capturing;
        _capturing = null;
        row.IsCapturing = false;

        _map.Set(row.ActionId, e.Key);

        foreach (var r in _rows) r.Refresh();
        HintText.Text = $"「{row.Label}」已改为 {KeyMap.KeyText(e.Key)}";
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: KeyRow row }) return;

        // Key.None 表示不绑定：ActionFor 永远匹配不到，等于这个功能没有快捷键
        _map.Set(row.ActionId, Key.None);
        foreach (var r in _rows) r.Refresh();
        HintText.Text = $"「{row.Label}」已取消绑定（方向键 / WASD 别名仍可用）";
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _map.ResetAll();
        foreach (var r in _rows) r.Refresh();
        HintText.Text = "已恢复默认";
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>设置窗口里的一行。</summary>
public sealed class KeyRow : INotifyPropertyChanged
{
    private readonly KeyMap _map;
    private readonly KeyMap.ActionDef _def;
    private bool _capturing;

    public KeyRow(KeyMap map, KeyMap.ActionDef def)
    {
        _map = map;
        _def = def;
    }

    public string ActionId => _def.Id;
    public string Label => _def.Label;

    public string AliasText => _def.Aliases.Length > 0
        ? "另可用 " + string.Join(" / ", _def.Aliases.Select(KeyMap.KeyText))
        : "";

    public string KeyText => _capturing
        ? "请按键…"
        : KeyMap.KeyText(_map.Get(_def.Id));

    public bool IsCapturing
    {
        get => _capturing;
        set
        {
            _capturing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyText)));
        }
    }

    public void Refresh()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyText)));

    public event PropertyChangedEventHandler? PropertyChanged;
}
