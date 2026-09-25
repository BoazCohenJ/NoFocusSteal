using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NoFocusSteal;

internal sealed class RulesForm : Form
{
    private readonly Settings _settings;
    private readonly ListView _list;
    private readonly TextBox _exe;

    public RulesForm(Settings settings)
    {
        _settings = settings;
        Text = "NoFocusSteal - app rules";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(520, 360);
        MinimumSize = new Size(420, 260);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;

        var help = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(8, 8, 8, 0),
            Text = "Allow: the app may always take focus.  Block: the app is stopped even when you're not typing.\r\n" +
                   "Tip: right-click a row in the focus log to add a rule for that app.",
        };

        _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        _list.Columns.Add("App", 300);
        _list.Columns.Add("Rule", 120);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        _exe = new TextBox { Width = 180 };
        bottom.Controls.Add(new Label { Text = "App (.exe):", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        bottom.Controls.Add(_exe);
        bottom.Controls.Add(Button("Allow", (_, _) => Add(AppRule.Allow)));
        bottom.Controls.Add(Button("Block", (_, _) => Add(AppRule.Block)));
        bottom.Controls.Add(Button("Remove selected", (_, _) => RemoveSelected()));
        bottom.Controls.Add(Button("Close", (_, _) => Close()));

        Controls.Add(_list);
        Controls.Add(help);
        Controls.Add(bottom);
        Reload();
    }

    private static Button Button(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += onClick;
        return b;
    }

    private void Reload()
    {
        _list.Items.Clear();
        foreach (var rule in _settings.Rules.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
            _list.Items.Add(new ListViewItem(new[] { rule.Key, rule.Value.ToString() }) { Tag = rule.Key });
    }

    private void Add(AppRule rule)
    {
        string exe = _exe.Text.Trim();
        if (exe.Length == 0) return;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        _settings.SetRule(exe, rule);
        _settings.Save();
        _exe.Clear();
        Reload();
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem item in _list.SelectedItems) _settings.SetRule((string)item.Tag, AppRule.Default);
        _settings.Save();
        Reload();
    }
}
