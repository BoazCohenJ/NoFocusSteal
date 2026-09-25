using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace NoFocusSteal;

/// <summary>Settings kept in %APPDATA%\NoFocusSteal\settings.ini, a plain key=value file you can edit by hand.</summary>
internal sealed class Settings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NoFocusSteal";

    public static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoFocusSteal");

    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoFocusSteal");

    private static string FilePath => Path.Combine(ConfigDir, "settings.ini");

    public PolicyOptions Policy { get; } = new PolicyOptions();
    public bool Notify = true;
    public bool TrustInjectedInput;
    public bool FirstRun = true;
    public Dictionary<string, AppRule> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AppRule RuleFor(string exeName) => Rules.TryGetValue(exeName, out var r) ? r : AppRule.Default;

    public void SetRule(string exeName, AppRule rule)
    {
        if (rule == AppRule.Default) Rules.Remove(exeName);
        else Rules[exeName] = rule;
    }

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            s.FirstRun = false;
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                if (key.StartsWith("allow:", StringComparison.OrdinalIgnoreCase))
                    s.Rules[key.Substring(6)] = AppRule.Allow;
                else if (key.StartsWith("block:", StringComparison.OrdinalIgnoreCase))
                    s.Rules[key.Substring(6)] = AppRule.Block;
                else
                    s.Apply(key, value);
            }
        }
        catch
        {
            // A broken settings file shouldn't stop protection; fall back to defaults.
        }
        return s;
    }

    private void Apply(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "mode":
                if (Enum.TryParse(value, true, out ProtectionMode mode)) Policy.Mode = mode;
                break;
            case "typingwindowms":
                Policy.TypingWindowMs = ParseMs(value, Policy.TypingWindowMs);
                break;
            case "intentwindowms":
                Policy.IntentWindowMs = ParseMs(value, Policy.IntentWindowMs);
                break;
            case "launchgracems":
                Policy.LaunchGraceMs = ParseMs(value, Policy.LaunchGraceMs);
                break;
            case "notify":
                Notify = ParseBool(value, Notify);
                break;
            case "trustinjectedinput":
                TrustInjectedInput = ParseBool(value, TrustInjectedInput);
                break;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var lines = new List<string>
            {
                "# NoFocusSteal settings. Edit while the app is closed, or use the tray menu.",
                "# mode: Off | LogOnly | Guard | Strict",
                "mode=" + Policy.Mode,
                "# How recent a keypress must be for you to count as typing.",
                "typingWindowMs=" + Policy.TypingWindowMs.ToString(CultureInfo.InvariantCulture),
                "# Strict mode: how long after a click or shortcut a focus change still counts as yours.",
                "intentWindowMs=" + Policy.IntentWindowMs.ToString(CultureInfo.InvariantCulture),
                "# Strict mode: how long an app you just launched may take to show its window.",
                "launchGraceMs=" + Policy.LaunchGraceMs.ToString(CultureInfo.InvariantCulture),
                "notify=" + (Notify ? "true" : "false"),
                "# Count software-generated input (remote desktop, KVM and on-screen keyboard tools) as yours.",
                "trustInjectedInput=" + (TrustInjectedInput ? "true" : "false"),
                "",
                "# Per-app rules: allow:<exe>=1 never blocks it, block:<exe>=1 blocks it even when you aren't typing.",
            };
            lines.AddRange(Rules.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .Select(r => (r.Value == AppRule.Allow ? "allow:" : "block:") + r.Key + "=1"));
            File.WriteAllLines(FilePath, lines);
            FirstRun = false;
        }
        catch
        {
            // Read-only profile or similar: keep running with in-memory settings.
        }
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --tray");
            else key.DeleteValue(RunValue, false);
        }
    }

    private static int ParseMs(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0 && v <= 600000 ? v : fallback;

    private static bool ParseBool(string value, bool fallback) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" ? true
        : value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" ? false
        : fallback;
}
