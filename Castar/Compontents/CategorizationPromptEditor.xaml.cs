using Castar.LocalAi;
using System;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public partial class CategorizationPromptEditor : UserControl
{
    internal CategorizationPromptEditor(string? prompt)
    {
        InitializeComponent();
        PromptInput.Text = string.IsNullOrWhiteSpace(prompt) ? LocalAiOptions.DefaultPrompt : prompt;
    }

    internal string? Prompt
    {
        get
        {
            var prompt = Normalize(PromptInput.Text);
            return prompt.Length == 0 || prompt == Normalize(LocalAiOptions.DefaultPrompt) ? null : prompt;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        PromptInput.Text = LocalAiOptions.DefaultPrompt;
        PromptInput.CaretIndex = 0;
        PromptInput.Focus();
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
